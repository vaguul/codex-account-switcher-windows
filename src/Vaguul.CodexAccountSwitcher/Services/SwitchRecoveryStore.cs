using System.Security.Cryptography;
using System.Text.Json;
using Vaguul.CodexAccountSwitcher.Models;

namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class SwitchRecoveryStore
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;

    public SwitchRecoveryStore(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    public bool HasPendingTransaction => File.Exists(_paths.TransactionPath);

    public async Task<SwitchTransaction> BeginAsync(
        string? sourceProfileId,
        string targetProfileId,
        ReadOnlyMemory<byte> currentAuth,
        CancellationToken cancellationToken = default)
    {
        var transaction = new SwitchTransaction
        {
            SourceProfileId = sourceProfileId,
            TargetProfileId = targetProfileId,
            BackupFileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.auth.dpapi"
        };
        var backupPath = GetBackupPath(transaction.BackupFileName);
        var encrypted = _protector.Protect(currentAuth.Span);
        try
        {
            await SecureFileSystem.AtomicWriteAsync(backupPath, encrypted, cancellationToken: cancellationToken);
            var journal = Serialize(transaction);
            try
            {
                await SecureFileSystem.AtomicWriteAsync(_paths.TransactionPath, journal, cancellationToken: cancellationToken);
            }
            catch
            {
                File.Delete(backupPath);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }

        return transaction;
    }

    public async Task<SwitchTransaction> ReadPendingAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await SecureFileSystem.ReadBoundedAsync(_paths.TransactionPath, 64 * 1024, cancellationToken);
        var transaction = JsonSerializer.Deserialize<SwitchTransaction>(bytes)
            ?? throw new InvalidDataException("The switch recovery journal is invalid.");
        _ = GetBackupPath(transaction.BackupFileName);
        return transaction;
    }

    public async Task<byte[]> ReadBackupAsync(SwitchTransaction transaction, CancellationToken cancellationToken = default)
    {
        var encrypted = await SecureFileSystem.ReadBoundedAsync(GetBackupPath(transaction.BackupFileName), AuthDocument.MaximumBytes * 2, cancellationToken);
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

    public async Task MarkCommittedAsync(SwitchTransaction transaction, CancellationToken cancellationToken = default)
    {
        transaction.State = "committed";
        await SecureFileSystem.AtomicWriteAsync(_paths.TransactionPath, Serialize(transaction), cancellationToken: cancellationToken);
    }

    public void Complete(SwitchTransaction transaction)
    {
        File.Delete(_paths.TransactionPath);
        PruneBackups(transaction.BackupFileName);
    }

    private string GetBackupPath(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".auth.dpapi", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The recovery backup name is invalid.");
        }

        var path = Path.Combine(_paths.BackupDirectory, fileName);
        if (!SecureFileSystem.IsInside(path, _paths.BackupDirectory))
        {
            throw new IOException("The recovery path escaped the backup directory.");
        }

        return path;
    }

    private static byte[] Serialize(SwitchTransaction transaction) =>
        JsonSerializer.SerializeToUtf8Bytes(transaction, new JsonSerializerOptions { WriteIndented = true });

    private void PruneBackups(string currentFileName)
    {
        var files = new DirectoryInfo(_paths.BackupDirectory)
            .EnumerateFiles("*.auth.dpapi")
            .OrderByDescending(file => file.CreationTimeUtc)
            .ToList();
        foreach (var file in files.Skip(5))
        {
            if (!string.Equals(file.Name, currentFileName, StringComparison.OrdinalIgnoreCase))
            {
                file.Delete();
            }
        }
    }
}
