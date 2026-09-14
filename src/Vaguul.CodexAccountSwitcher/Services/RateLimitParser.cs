using System.Text.Json;
using Vaguul.CodexAccountSwitcher.Models;

namespace Vaguul.CodexAccountSwitcher.Services;

public static class RateLimitParser
{
    public static UsageSnapshot Parse(ReadOnlySpan<byte> responseJson)
    {
        using var document = JsonDocument.Parse(responseJson.ToArray());
        var root = document.RootElement;
        if (root.TryGetProperty("result", out var result)) root = result;

        var windows = new List<UsageWindow>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in buckets.EnumerateObject())
            {
                ParseBucket(bucket.Name, bucket.Value, windows);
            }
        }
        else if (root.TryGetProperty("rateLimits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
        {
            ParseBucket("codex", rateLimits, windows);
        }

        return new UsageSnapshot
        {
            Status = windows.Count == 0 ? "unavailable" : "available",
            CheckedAt = DateTimeOffset.UtcNow,
            Windows = windows
        };
    }

    private static void ParseBucket(string bucketId, JsonElement bucket, List<UsageWindow> output)
    {
        foreach (var position in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(position, out var window) || window.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var duration = ReadInt(window, "windowDurationMins");
            var used = Math.Clamp(ReadDouble(window, "usedPercent"), 0, 100);
            var resetSeconds = ReadLong(window, "resetsAt");
            output.Add(new UsageWindow
            {
                BucketId = bucketId,
                Position = position,
                Label = FormatDuration(duration),
                UsedPercent = used,
                RemainingPercent = 100 - used,
                WindowDurationMinutes = duration,
                ResetsAt = resetSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetSeconds) : null
            });
        }
    }

    internal static string FormatDuration(int minutes) => minutes switch
    {
        300 => "5 hours",
        10_080 => "7 days",
        > 0 when minutes % 1_440 == 0 => $"{minutes / 1_440} days",
        > 0 when minutes % 60 == 0 => $"{minutes / 60} hours",
        > 0 => $"{minutes} minutes",
        _ => "Usage window"
    };

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static double ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : 0;
}
