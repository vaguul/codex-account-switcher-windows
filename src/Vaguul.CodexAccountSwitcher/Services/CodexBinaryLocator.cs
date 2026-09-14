using System.Diagnostics;

namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class CodexBinaryLocator
{
    public string? Find()
    {
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");

        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && IsTrustedCandidate(path, localRoot))
                    {
                        return Path.GetFullPath(path);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Continue with deterministic installation paths.
                }
            }
        }

        if (Directory.Exists(localRoot))
        {
            var installed = Directory.EnumerateFiles(localRoot, "codex.exe", SearchOption.AllDirectories)
                .Where(path => IsTrustedCandidate(path, localRoot))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (installed is not null)
            {
                return Path.GetFullPath(installed);
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                if (File.Exists(candidate) && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    internal static bool IsTrustedCandidate(string candidate, string localRoot)
    {
        var fullPath = Path.GetFullPath(candidate);
        return File.Exists(fullPath)
            && Path.GetFileName(fullPath).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
            && SecureFileSystem.IsInside(fullPath, localRoot)
            && !File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint);
    }
}
