using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vaguul.CodexAccountSwitcher.Models;

namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class ProfileTransferService
{
    private static readonly byte[] Magic = "VAGUUL-PROFILES-1"u8.ToArray();
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 600_000;
    private const int MaximumProfiles = 100;
    private const int MaximumPackageBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly ProfileVault _vault;

    public ProfileTransferService(ProfileVault vault)
    {
        _vault = vault;
    }

    public async Task ExportAsync(string destination, ReadOnlyMemory<char> password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password.Span);
        var profiles = await _vault.GetProfilesAsync(cancellationToken);
        if (profiles.Count > MaximumProfiles)
        {
            throw new InvalidOperationException("Too many profiles to export.");
        }

        var records = new List<TransferProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            byte[]? auth = null;
            try
            {
                auth = await _vault.ReadAuthAsync(profile.Id, cancellationToken);
                records.Add(new TransferProfile
                {
                    DisplayName = profile.DisplayName,
                    ColorHex = profile.ColorHex,
                    Email = profile.Email,
                    Usage = profile.Usage,
                    AuthBase64 = Convert.ToBase64String(auth)
                });
            }
            finally
            {
                if (auth is not null) CryptographicOperations.ZeroMemory(auth);
            }
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions);
        byte[]? package = null;
        try
        {
            package = Encrypt(payload, password.Span);
            await SecureFileSystem.AtomicWriteAsync(destination, package, cancellationToken: cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (package is not null) CryptographicOperations.ZeroMemory(package);
        }
    }

    public async Task<ProfileImportResult> ImportAsync(string source, ReadOnlyMemory<char> password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password.Span);
        var package = await SecureFileSystem.ReadBoundedAsync(source, MaximumPackageBytes, cancellationToken);
        byte[]? payload = null;
        try
        {
            payload = Decrypt(package, password.Span);
            var records = JsonSerializer.Deserialize<List<TransferProfile>>(payload, JsonOptions)
                ?? throw new InvalidDataException("The profile package is empty or invalid.");
            if (records.Count > MaximumProfiles)
            {
                throw new InvalidDataException("The profile package contains too many profiles.");
            }

            return await ImportRecordsAsync(records, cancellationToken);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("The package password is incorrect or the package was modified.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The profile package is invalid.");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The profile package is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(package);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async Task<ProfileImportResult> ImportRecordsAsync(List<TransferProfile> records, CancellationToken cancellationToken)
    {
        var existing = await _vault.GetProfilesAsync(cancellationToken);
        var knownFingerprints = existing.Select(profile => profile.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var prepared = new List<PreparedProfile>(records.Count);
        var skipped = 0;
        try
        {
            foreach (var record in records)
            {
                ValidateRecord(record);
                var auth = Convert.FromBase64String(record.AuthBase64);
                try
                {
                    var identity = AuthDocument.Validate(auth);
                    if (!knownFingerprints.Add(identity.Fingerprint))
                    {
                        skipped++;
                        CryptographicOperations.ZeroMemory(auth);
                        continue;
                    }

                    prepared.Add(new PreparedProfile(record, auth));
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(auth);
                    throw;
                }
            }

            var imported = 0;
            foreach (var item in prepared)
            {
                var profile = await _vault.AddAsync(item.Record.DisplayName, item.Record.ColorHex, item.Auth, cancellationToken);
                if (item.Record.Usage is not null)
                {
                    await _vault.UpdateUsageAsync(profile.Id, item.Record.Usage, item.Record.Email, cancellationToken);
                }

                imported++;
            }

            return new ProfileImportResult(imported, skipped);
        }
        finally
        {
            foreach (var item in prepared)
            {
                CryptographicOperations.ZeroMemory(item.Auth);
            }
        }
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> payload, ReadOnlySpan<char> password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, payload, ciphertext, tag, Magic);
            var package = new byte[Magic.Length + SaltSize + NonceSize + TagSize + ciphertext.Length];
            var offset = 0;
            Magic.CopyTo(package, offset); offset += Magic.Length;
            salt.CopyTo(package, offset); offset += SaltSize;
            nonce.CopyTo(package, offset); offset += NonceSize;
            tag.CopyTo(package, offset); offset += TagSize;
            ciphertext.CopyTo(package, offset);
            return package;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> package, ReadOnlySpan<char> password)
    {
        var minimum = Magic.Length + SaltSize + NonceSize + TagSize + 1;
        if (package.Length < minimum || !package[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The profile package format is not supported.");
        }

        var offset = Magic.Length;
        var salt = package.Slice(offset, SaltSize).ToArray(); offset += SaltSize;
        var nonce = package.Slice(offset, NonceSize).ToArray(); offset += NonceSize;
        var tag = package.Slice(offset, TagSize).ToArray(); offset += TagSize;
        var ciphertext = package[offset..].ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
        var payload = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, payload, Magic);
            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidatePassword(ReadOnlySpan<char> password)
    {
        if (password.Length < 8 || password.Length > 256)
        {
            throw new ArgumentException("The transfer password must contain 8 to 256 characters.", nameof(password));
        }
    }

    private static void ValidateRecord(TransferProfile? record)
    {
        if (record is null
            || string.IsNullOrWhiteSpace(record.DisplayName) || record.DisplayName.Trim().Length > 60
            || string.IsNullOrWhiteSpace(record.ColorHex) || record.ColorHex.Length != 7 || record.ColorHex[0] != '#'
            || !int.TryParse(record.ColorHex[1..], System.Globalization.NumberStyles.HexNumber, null, out _)
            || record.Email?.Length > 320
            || record.Usage?.PlanType?.Length > 80
            || record.Usage is { Windows: null }
            || record.Usage?.Windows.Count > 32)
        {
            throw new InvalidDataException("The profile package contains invalid profile metadata.");
        }

        if (string.IsNullOrWhiteSpace(record.AuthBase64) || record.AuthBase64.Length > AuthDocument.MaximumBytes * 2)
        {
            throw new InvalidDataException("The profile package contains an invalid account snapshot.");
        }
    }

    private sealed class TransferProfile
    {
        public string DisplayName { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#5B8DEF";
        public string? Email { get; set; }
        public UsageSnapshot? Usage { get; set; }
        public string AuthBase64 { get; set; } = string.Empty;
    }

    private sealed record PreparedProfile(TransferProfile Record, byte[] Auth);
}

public sealed record ProfileImportResult(int Imported, int SkippedDuplicates);
