using System.Text.Json.Serialization;

namespace Vaguul.CodexAccountSwitcher.Models;

public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Account";
    public string ColorHex { get; set; } = "#5B8DEF";
    public string Fingerprint { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public UsageSnapshot? Usage { get; set; }
    [JsonIgnore]
    public bool IsActive { get; set; }
}

public sealed class UsageSnapshot
{
    public string Status { get; set; } = "unavailable";
    public string? PlanType { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<UsageWindow> Windows { get; set; } = [];
}

public sealed class UsageWindow
{
    public string BucketId { get; set; } = "codex";
    public string Position { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double UsedPercent { get; set; }
    public double RemainingPercent { get; set; }
    public int WindowDurationMinutes { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
}

public sealed record AuthIdentity(string Fingerprint);
public sealed record AppServerProfileSnapshot(UsageSnapshot Usage, string? Email);
