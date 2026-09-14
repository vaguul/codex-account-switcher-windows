using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vaguul.CodexAccountSwitcher.Models;

namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class CodexAppServerClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly AppPaths _paths;
    private readonly CodexBinaryLocator _locator;

    public CodexAppServerClient(AppPaths paths, CodexBinaryLocator locator)
    {
        _paths = paths;
        _locator = locator;
    }

    public async Task<UsageSnapshot> ReadUsageAsync(ReadOnlyMemory<byte> authJson, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadProfileAsync(authJson, cancellationToken);
        try
        {
            return snapshot.Usage;
        }
        finally
        {
            if (snapshot.RefreshedAuthJson is not null)
            {
                CryptographicOperations.ZeroMemory(snapshot.RefreshedAuthJson);
            }
        }
    }

    public async Task<AppServerProfileSnapshot> ReadProfileAsync(ReadOnlyMemory<byte> authJson, CancellationToken cancellationToken = default)
    {
        var inputIdentity = AuthDocument.Validate(authJson.Span);
        var executable = _locator.Find()
            ?? throw new InvalidOperationException("Codex CLI was not found in the local installation.");
        var temporaryHome = Path.Combine(_paths.TemporaryDirectory, Guid.NewGuid().ToString("N"));
        SecureFileSystem.CreatePrivateDirectory(temporaryHome);
        Process? process = null;
        byte[]? refreshedAuth = null;
        var temporaryAuthPath = Path.Combine(temporaryHome, "auth.json");
        try
        {
            var config = Encoding.UTF8.GetBytes("cli_auth_credentials_store = \"file\"\r\n");
            await SecureFileSystem.AtomicWriteAsync(Path.Combine(temporaryHome, "config.toml"), config, cancellationToken: cancellationToken);
            await SecureFileSystem.AtomicWriteAsync(temporaryAuthPath, authJson, cancellationToken: cancellationToken);

            process = Start(executable, temporaryHome);
            var stderrDrain = DrainAsync(process.StandardError);
            await SendAsync(process.StandardInput, new
            {
                method = "initialize",
                id = 1,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "vaguul_account_switcher",
                        title = "Vaguul Codex Account Switcher",
                        version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString(3) ?? "unknown"
                    }
                }
            }, cancellationToken);
            _ = await ReadResponseAsync(process.StandardOutput, 1, cancellationToken);
            await SendAsync(process.StandardInput, new { method = "initialized", @params = new { } }, cancellationToken);
            await SendAsync(process.StandardInput, new { method = "account/rateLimits/read", id = 2, @params = new { } }, cancellationToken);
            var usageResponse = await ReadResponseAsync(process.StandardOutput, 2, cancellationToken);
            string? accountResponse = null;
            try
            {
                await SendAsync(process.StandardInput, new { method = "account/read", id = 3, @params = new { refreshToken = false } }, cancellationToken);
                accountResponse = await ReadResponseAsync(process.StandardOutput, 3, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or TimeoutException
                                       || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Usage remains useful when an older Codex build does not expose account metadata.
            }

            // Codex may remove the temporary auth file during graceful shutdown. Capture any
            // refresh-token rotation while app-server is still running, then try once more after it exits.
            refreshedAuth = await TryReadAuthSnapshotAsync(temporaryAuthPath, cancellationToken);
            await StopAsync(process, cancellationToken);
            await stderrDrain;
            process.Dispose();
            process = null;

            refreshedAuth ??= await TryReadAuthSnapshotAsync(temporaryAuthPath, cancellationToken);
            if (refreshedAuth is not null
                && AuthDocument.Validate(refreshedAuth).Fingerprint != inputIdentity.Fingerprint)
            {
                throw new InvalidDataException("Codex changed the account identity during the usage check.");
            }

            var usage = RateLimitParser.Parse(Encoding.UTF8.GetBytes(usageResponse));
            var account = ParseAccount(accountResponse);
            usage.PlanType = account.PlanType;
            var resultAuth = refreshedAuth;
            refreshedAuth = null;
            return new AppServerProfileSnapshot(usage, account.Email, resultAuth);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Codex returned an invalid usage response.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Codex did not return usage before the timeout.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Codex could not provide usage for this account.");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    await StopAsync(process, CancellationToken.None);
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (refreshedAuth is not null)
            {
                CryptographicOperations.ZeroMemory(refreshedAuth);
            }

            SecureFileSystem.DeleteTreeInside(temporaryHome, _paths.TemporaryDirectory);
        }
    }

    private static Process Start(string executable, string temporaryHome)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--stdio");
        process.StartInfo.Environment["CODEX_HOME"] = temporaryHome;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex app-server could not be started.");
        }

        return process;
    }

    private static async Task SendAsync(StreamWriter writer, object request, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(request);
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new InvalidOperationException("Codex app-server closed before returning usage.");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != expectedId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out _))
            {
                throw new InvalidOperationException("Codex rejected the request.");
            }

            return root.GetRawText();
        }
    }

    private static async Task StopAsync(Process process, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is not null) { }
    }

    internal static async Task<byte[]?> TryReadAuthSnapshotAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SecureFileSystem.ReadBoundedAsync(path, AuthDocument.MaximumBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static (string? Email, string? PlanType) ParseAccount(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return (null, null);
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            root = result;
        }

        if (!root.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var email = ReadBoundedString(account, "email", 320);
        var planType = ReadBoundedString(account, "planType", 80);
        return (email, planType);
    }

    private static string? ReadBoundedString(JsonElement element, string propertyName, int maximumLength)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maximumLength ? null : text;
    }
}
