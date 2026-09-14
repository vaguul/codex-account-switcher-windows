using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vaguul.CodexAccountSwitcher.Models;

namespace Vaguul.CodexAccountSwitcher.Services;

public static class AuthDocument
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly string[] IdentityClaims =
    [
        "account_id",
        "accountId",
        "chatgpt_account_id",
        "chatgptAccountId",
        "sub"
    ];

    public static AuthIdentity Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("The Codex authentication file has an invalid size.");
        }

        var jsonBytes = bytes.ToArray();
        try
        {
            using var document = JsonDocument.Parse(jsonBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The Codex authentication file is not a JSON object.");
            }

            var stableIdentity = FindIdentity(document.RootElement) ?? FindIdentityInTokens(document.RootElement);
            if (string.IsNullOrWhiteSpace(stableIdentity))
            {
                throw new InvalidDataException("The Codex authentication file does not contain a stable account identity.");
            }

            var identityBytes = Encoding.UTF8.GetBytes("vaguul-codex-account:" + stableIdentity.Trim());
            try
            {
                return new AuthIdentity(Convert.ToHexString(SHA256.HashData(identityBytes)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(identityBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    private static string? FindIdentity(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IdentityClaims.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString();
                }

                var nested = FindIdentity(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindIdentity(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? FindIdentityInTokens(JsonElement root)
    {
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "id_token", "access_token" })
        {
            if (!tokens.TryGetProperty(name, out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            try
            {
                var segments = token.Split('.');
                if (segments.Length < 2)
                {
                    continue;
                }

                var payload = segments[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
                var decoded = Convert.FromBase64String(payload);
                try
                {
                    using var jwt = JsonDocument.Parse(decoded);
                    var identity = FindIdentity(jwt.RootElement);
                    if (identity is not null)
                    {
                        return identity;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(decoded);
                }
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                // Opaque tokens are allowed when another supported identity field exists.
            }
        }

        return null;
    }
}
