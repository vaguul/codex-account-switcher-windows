using System.Diagnostics;

namespace Vaguul.CodexAccountSwitcher.Services;

public interface ICodexDesktopController
{
    Task CloseAsync(CancellationToken cancellationToken = default);
    bool HasBlockingCodexProcesses();
    Task<bool> LaunchAndVerifyAsync(CancellationToken cancellationToken = default);
}

public sealed class CodexDesktopController : ICodexDesktopController
{
    private const string AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var processes = GetVerifiedDesktopProcesses();
        foreach (var process in processes)
        {
            using (process)
            {
                if (process.HasExited)
                {
                    continue;
                }

                _ = process.CloseMainWindow();
                try
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
        }

        await CloseCodexProcessesAsync(cancellationToken);

        if (GetVerifiedDesktopProcesses().Count != 0)
        {
            throw new InvalidOperationException("Codex Desktop did not close completely.");
        }

    }

    public bool HasBlockingCodexProcesses()
    {
        var desktopProcesses = GetVerifiedDesktopProcesses();
        var desktopRunning = desktopProcesses.Any(process => !process.HasExited);
        foreach (var process in desktopProcesses)
        {
            process.Dispose();
        }

        if (desktopRunning)
        {
            return true;
        }

        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task CloseCodexProcessesAsync(CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    _ = process.CloseMainWindow();
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch (TimeoutException)
                {
                    try
                    {
                        // Do not kill the process tree: the switcher may have been launched from Codex's terminal.
                        process.Kill(entireProcessTree: false);
                        await process.WaitForExitAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                    {
                        // The coordinator performs the final process check and reports any process that remains.
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // The process may have exited or become inaccessible between enumeration and close.
                    // The coordinator performs the final process check and reports any process that remains.
                }
            }
        }
    }

    public async Task<bool> LaunchAndVerifyAsync(CancellationToken cancellationToken = default)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{AppUserModelId}")
        {
            UseShellExecute = true
        });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = GetVerifiedDesktopProcesses();
            var running = processes.Any(process => !process.HasExited);
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (running)
            {
                return true;
            }

            await Task.Delay(300, cancellationToken);
        }

        return false;
    }

    private static List<Process> GetVerifiedDesktopProcesses()
    {
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")
            + Path.DirectorySeparatorChar;
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                var path = process.MainModule?.FileName ?? string.Empty;
                var fullPath = Path.GetFullPath(path);
                var relative = Path.GetRelativePath(windowsApps, fullPath);
                if (!relative.StartsWith("..", StringComparison.Ordinal)
                    && relative.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(fullPath).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                    && process.MainWindowHandle != IntPtr.Zero)
                {
                    result.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
                throw new InvalidOperationException("A ChatGPT process could not be verified safely. Close it manually and retry.", ex);
            }

            process.Dispose();
        }

        return result;
    }
}
