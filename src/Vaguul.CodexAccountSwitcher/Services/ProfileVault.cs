using System.Security.Cryptography;
using System.Text.Json;
using Vaguul.CodexAccountSwitcher.Models;

namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class ProfileVault
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileVault(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    public void Initialize()
    {
        SecureFileSystem.CreatePrivateDirectory(_paths.DataRoot);
        SecureFileSystem.CreatePrivateDirectory(_paths.VaultDirectory);
        SecureFileSystem.CreatePrivateDirectory(_paths.BackupDirectory);
        SecureFileSystem.CreatePrivateDirectory(_paths.TemporaryDirectory);
    }

    public async Task<IReadOnlyList<AccountProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadMetadataCoreAsync(cancellationToken)).Select(Clone).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountProfile> AddAsync(
        string displayName,
        string colorHex,
        ReadOnlyMemory<byte> authJson,
        CancellationToken cancellationToken = default)
    {
        var identity = AuthDocument.Validate(authJson.Span);
        ValidateDisplayName(displayName);
        ValidateColor(colorHex);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadMetadataCoreAsync(cancellationToken);
            if (profiles.Any(profile => profile.Fingerprint == identity.Fingerprint))
            {
                throw new InvalidOperationException("This Codex account is already saved.");
            }

            var profile = new AccountProfile
            {
                DisplayName = displayName.Trim(),
                ColorHex = colorHex,
                Fingerprint = identity.Fingerprint
            };
            var vaultPath = GetVaultPath(profile.Id);
            var encrypted = _protector.Protect(authJson.Span);
            try
            {
                await SecureFileSystem.AtomicWriteAsync(vaultPath, encrypted, cancellationToken: cancellationToken);
                profiles.Add(profile);
                try
                {
                    await SaveMetadataCoreAsync(profiles, cancellationToken);
                }
                catch
                {
                    File.Delete(vaultPath);
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            return Clone(profile);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadAuthAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ValidateProfileId(profileId);
        var encrypted = await SecureFileSystem.ReadBoundedAsync(GetVaultPath(profileId), AuthDocument.MaximumBytes * 2, cancellationToken);
        try
        {
            var plaintext = _protector.Unprotect(encrypted);
            AuthDocument.Validate(plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task UpdateAuthAsync(string profileId, ReadOnlyMemory<byte> authJson, CancellationToken cancellationToken = default)
    {
        ValidateProfileId(profileId);
        var identity = AuthDocument.Validate(authJson.Span);
        var profiles = await GetProfilesAsync(cancellationToken);
        var profile = profiles.SingleOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("The account profile no longer exists.");
        if (profile.Fingerprint != identity.Fingerprint)
        {
            throw new InvalidDataException("Refusing to overwrite a profile with a different account.");
        }

        var encrypted = _protector.Protect(authJson.Span);
        try
        {
            await SecureFileSystem.AtomicWriteAsync(GetVaultPath(profileId), encrypted, cancellationToken: cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task TouchAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadMetadataCoreAsync(cancellationToken);
            var profile = profiles.Single(item => item.Id == profileId);
            profile.LastUsedAt = DateTimeOffset.UtcNow;
            await SaveMetadataCoreAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateUsageAsync(string profileId, UsageSnapshot usage, CancellationToken cancellationToken = default)
    {
        ValidateProfileId(profileId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadMetadataCoreAsync(cancellationToken);
            var profile = profiles.SingleOrDefault(item => item.Id == profileId)
                ?? throw new InvalidOperationException("The account profile no longer exists.");
            profile.Usage = usage;
            await SaveMetadataCoreAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ValidateProfileId(profileId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadMetadataCoreAsync(cancellationToken);
            if (profiles.RemoveAll(item => item.Id == profileId) == 0)
            {
                return;
            }

            await SaveMetadataCoreAsync(profiles, cancellationToken);
            File.Delete(GetVaultPath(profileId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<AccountProfile>> LoadMetadataCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ProfilesPath))
        {
            return [];
        }

        try
        {
            var bytes = await SecureFileSystem.ReadBoundedAsync(_paths.ProfilesPath, 1024 * 1024, cancellationToken);
            return JsonSerializer.Deserialize<List<AccountProfile>>(bytes) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException && File.Exists(_paths.ProfilesBackupPath))
        {
            var backup = await SecureFileSystem.ReadBoundedAsync(_paths.ProfilesBackupPath, 1024 * 1024, cancellationToken);
            var recovered = JsonSerializer.Deserialize<List<AccountProfile>>(backup) ?? [];
            await SaveMetadataCoreAsync(recovered, cancellationToken);
            return recovered;
        }
    }

    private Task SaveMetadataCoreAsync(List<AccountProfile> profiles, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profiles, JsonOptions);
        return SecureFileSystem.AtomicWriteAsync(_paths.ProfilesPath, bytes, _paths.ProfilesBackupPath, cancellationToken);
    }

    private string GetVaultPath(string profileId)
    {
        ValidateProfileId(profileId);
        var path = Path.Combine(_paths.VaultDirectory, profileId + ".auth.dpapi");
        if (!SecureFileSystem.IsInside(path, _paths.VaultDirectory))
        {
            throw new IOException("The profile path escaped the secure vault.");
        }

        return path;
    }

    private static void ValidateProfileId(string profileId)
    {
        if (profileId.Length != 32 || !Guid.TryParseExact(profileId, "N", out _))
        {
            throw new ArgumentException("Invalid profile identifier.", nameof(profileId));
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 60)
        {
            throw new ArgumentException("Account names must contain 1 to 60 characters.", nameof(displayName));
        }
    }

    private static void ValidateColor(string colorHex)
    {
        if (colorHex.Length != 7 || colorHex[0] != '#' || !int.TryParse(colorHex[1..], System.Globalization.NumberStyles.HexNumber, null, out _))
        {
            throw new ArgumentException("Invalid account color.", nameof(colorHex));
        }
    }

    private static AccountProfile Clone(AccountProfile value) => new()
    {
        Id = value.Id,
        DisplayName = value.DisplayName,
        ColorHex = value.ColorHex,
        Fingerprint = value.Fingerprint,
        CreatedAt = value.CreatedAt,
        LastUsedAt = value.LastUsedAt,
        Usage = value.Usage
    };
}
