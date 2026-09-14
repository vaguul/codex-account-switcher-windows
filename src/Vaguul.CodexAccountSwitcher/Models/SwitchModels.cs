namespace Vaguul.CodexAccountSwitcher.Models;

public sealed class SwitchTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceProfileId { get; set; } = string.Empty;
    public string TargetProfileId { get; set; } = string.Empty;
    public string BackupFileName { get; set; } = string.Empty;
    public string State { get; set; } = "prepared";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record SwitchResult(bool Succeeded, bool RolledBack, string Message);
