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

    public async Task<byte[]> LoginAsync(CancellationToken cancellationToken = default)
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

            process = Start(executable, temporaryHome);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Browser sign-in did not complete successfully.");
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

    private static Process Start(string executable, string temporaryHome)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = temporaryHome
            }
        };
        process.StartInfo.ArgumentList.Add("login");
        process.StartInfo.ArgumentList.Add("--device-auth");
        process.StartInfo.Environment["CODEX_HOME"] = temporaryHome;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex browser sign-in could not be started.");
        }

        return process;
    }
}
