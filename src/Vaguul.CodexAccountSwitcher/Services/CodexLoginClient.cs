using System.Diagnostics;
using System.Text;

namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class CodexLoginClient
{
    private readonly AppPaths _paths;
    private readonly CodexBinaryLocator _locator;

    public CodexLoginClient(AppPaths paths, CodexBinaryLocator locator)
    {
        _paths = paths;
        _locator = locator;
    }

    public Task<byte[]> LoginAsync(CancellationToken cancellationToken = default) =>
        LoginAsync(CodexLoginMode.Browser, cancellationToken);

    public async Task<byte[]> LoginAsync(CodexLoginMode mode, CancellationToken cancellationToken = default)
    {
        var executable = _locator.Find()
            ?? throw new InvalidOperationException("Codex CLI was not found in the local installation.");
        var temporaryHome = Path.Combine(_paths.TemporaryDirectory, Guid.NewGuid().ToString("N"));
        SecureFileSystem.CreatePrivateDirectory(temporaryHome);
        Process? process = null;
        byte[]? auth = null;
        try
        {
            var config = Encoding.UTF8.GetBytes("cli_auth_credentials_store = \"file\"\r\n");
            await SecureFileSystem.AtomicWriteAsync(Path.Combine(temporaryHome, "config.toml"), config, cancellationToken: cancellationToken);

            process = Start(executable, temporaryHome, mode);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var method = mode == CodexLoginMode.Browser ? "browser" : "device-code";
                throw new InvalidOperationException($"Codex {method} sign-in was canceled or did not complete successfully.");
            }

            var authPath = Path.Combine(temporaryHome, "auth.json");
            auth = await SecureFileSystem.ReadBoundedAsync(authPath, AuthDocument.MaximumBytes, cancellationToken);
            AuthDocument.Validate(auth);
            var result = auth;
            auth = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
        finally
        {
            if (auth is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth);
            }

            if (process is not null)
            {
                process.Dispose();
            }

            SecureFileSystem.DeleteTreeInside(temporaryHome, _paths.TemporaryDirectory);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string temporaryHome, CodexLoginMode mode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = temporaryHome
        };
        startInfo.ArgumentList.Add("login");
        if (mode == CodexLoginMode.DeviceCode)
        {
            startInfo.ArgumentList.Add("--device-auth");
        }

        startInfo.Environment["CODEX_HOME"] = temporaryHome;
        return startInfo;
    }

    private static Process Start(string executable, string temporaryHome, CodexLoginMode mode)
    {
        var process = new Process
        {
            StartInfo = BuildStartInfo(executable, temporaryHome, mode)
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex sign-in could not be started.");
        }

        return process;
    }
}
