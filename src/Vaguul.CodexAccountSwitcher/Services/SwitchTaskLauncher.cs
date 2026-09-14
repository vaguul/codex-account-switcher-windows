using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Vaguul.CodexAccountSwitcher.Services;

internal sealed record SwitchTaskRequest(string ProfileId, string TaskName);

internal static class SwitchTaskLauncher
{
    private const string TaskPrefix = "Vaguul-Codex-Switch-";
    private const string WorkerFlag = "--complete-switch";
    private const string TaskFlag = "--task-name";
    private static readonly Regex TaskNamePattern = new(
        $"^{Regex.Escape(TaskPrefix)}[a-f0-9]{{12}}-[a-f0-9]{{32}}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string CreateTaskName(string profileId)
    {
        ValidateProfileId(profileId);
        return $"{TaskPrefix}{profileId[..12]}-{Guid.NewGuid():N}";
    }

    public static bool TryParse(string[] args, out SwitchTaskRequest request)
    {
        request = null!;
        if (args.Length != 4
            || !string.Equals(args[0], WorkerFlag, StringComparison.Ordinal)
            || !string.Equals(args[2], TaskFlag, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParseExact(args[1], "N", out _)
            || !TaskNamePattern.IsMatch(args[3]))
        {
            return false;
        }

        request = new SwitchTaskRequest(args[1], args[3]);
        return true;
    }

    public static string BuildTaskCommand(string executablePath, string profileId, string taskName)
    {
        ValidateProfileId(profileId);
        ValidateTaskName(taskName);
        if (!Path.IsPathFullyQualified(executablePath)
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains('"'))
        {
            throw new ArgumentException("The worker executable path is invalid.", nameof(executablePath));
        }

        return $"\"{executablePath}\" {WorkerFlag} \"{profileId}\" {TaskFlag} \"{taskName}\"";
    }

    public static async Task<string> ScheduleAsync(
        string profileId,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var taskName = CreateTaskName(profileId);
        // schtasks accepts minute precision; always choose the next minute to avoid a past start time.
        var scheduledFor = DateTime.Now.AddMinutes(1);
        var taskCommand = BuildTaskCommand(executablePath, profileId, taskName);
        var create = await RunSchtasksAsync(
            [
                "/Create",
                "/SC", "ONCE",
                "/TN", taskName,
                "/TR", taskCommand,
                "/ST", scheduledFor.ToString("HH:mm", CultureInfo.InvariantCulture),
                // schtasks parses dates using the Windows locale, not invariant culture.
                "/SD", scheduledFor.ToString("d", CultureInfo.CurrentCulture),
                "/RL", "LIMITED",
                "/IT",
                "/F"
            ],
            cancellationToken);
        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows could not schedule the detached switch worker. {create.Error}");
        }

        var run = await RunSchtasksAsync(["/Run", "/TN", taskName], cancellationToken);
        if (run.ExitCode != 0)
        {
            await DeleteAsync(taskName, CancellationToken.None);
            throw new InvalidOperationException($"Windows could not start the detached switch worker. {run.Error}");
        }

        return taskName;
    }

    public static async Task DeleteAsync(string taskName, CancellationToken cancellationToken = default)
    {
        ValidateTaskName(taskName);
        _ = await RunSchtasksAsync(["/Delete", "/TN", taskName, "/F"], cancellationToken);
    }

    private static void ValidateProfileId(string profileId)
    {
        if (!Guid.TryParseExact(profileId, "N", out _))
        {
            throw new ArgumentException("The profile identifier is invalid.", nameof(profileId));
        }
    }

    private static void ValidateTaskName(string taskName)
    {
        if (!TaskNamePattern.IsMatch(taskName))
        {
            throw new ArgumentException("The switch task name is invalid.", nameof(taskName));
        }
    }

    private static async Task<SchtasksResult> RunSchtasksAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        return new SchtasksResult(process.ExitCode, Compact(output, error));
    }

    private static string Compact(string output, string error)
    {
        var combined = string.Join(" ", new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return combined.Length <= 400 ? combined : combined[..400];
    }

    private sealed record SchtasksResult(int ExitCode, string Error);
}
