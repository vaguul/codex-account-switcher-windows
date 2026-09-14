using System.Security.Cryptography;
using Vaguul.CodexAccountSwitcher.Models;

namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class AccountSwitchCoordinator
{
    private readonly AppPaths _paths;
    private readonly ProfileVault _vault;
    private readonly SwitchRecoveryStore _recovery;
    private readonly ICodexDesktopController _desktop;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountSwitchCoordinator(
        AppPaths paths,
        ProfileVault vault,
        SwitchRecoveryStore recovery,
        ICodexDesktopController desktop)
    {
        _paths = paths;
        _vault = vault;
        _recovery = recovery;
        _desktop = desktop;
    }

    public async Task<SwitchResult> SwitchAsync(string targetProfileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        byte[]? targetAuth = null;
        byte[]? currentAuth = null;
        SwitchTransaction? transaction = null;
        var desktopClosed = false;
        try
        {
            targetAuth = await _vault.ReadAuthAsync(targetProfileId, cancellationToken);
            var targetIdentity = AuthDocument.Validate(targetAuth);
            var profiles = await _vault.GetProfilesAsync(cancellationToken);
            var target = profiles.SingleOrDefault(profile => profile.Id == targetProfileId)
                ?? throw new InvalidOperationException("The target profile no longer exists.");
            if (target.Fingerprint != targetIdentity.Fingerprint)
            {
                throw new InvalidDataException("The target profile failed its identity check.");
            }

            if (File.Exists(_paths.ActiveAuthPath))
            {
                var preflight = await SecureFileSystem.ReadBoundedAsync(_paths.ActiveAuthPath, AuthDocument.MaximumBytes, cancellationToken);
                try
                {
                    if (AuthDocument.Validate(preflight).Fingerprint == target.Fingerprint)
                    {
                        return new SwitchResult(true, false, "This account is already active.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(preflight);
                }
            }

            await _desktop.CloseAsync(cancellationToken);
            desktopClosed = true;
            if (_desktop.HasBlockingCodexProcesses())
            {
                throw new InvalidOperationException("A Codex CLI or app-server process is still running. Close it before switching accounts.");
            }

            currentAuth = await SecureFileSystem.ReadBoundedAsync(_paths.ActiveAuthPath, AuthDocument.MaximumBytes, cancellationToken);
            var currentIdentity = AuthDocument.Validate(currentAuth);
            var source = profiles.SingleOrDefault(profile => profile.Fingerprint == currentIdentity.Fingerprint)
                ?? throw new InvalidOperationException("Save the currently active account before switching away from it.");

            await _vault.UpdateAuthAsync(source.Id, currentAuth, cancellationToken);
            transaction = await _recovery.BeginAsync(source.Id, target.Id, currentAuth, cancellationToken);
            await SecureFileSystem.AtomicWriteAsync(_paths.ActiveAuthPath, targetAuth, cancellationToken: cancellationToken);

            var installed = await SecureFileSystem.ReadBoundedAsync(_paths.ActiveAuthPath, AuthDocument.MaximumBytes, cancellationToken);
            try
            {
                if (AuthDocument.Validate(installed).Fingerprint != target.Fingerprint)
                {
                    throw new InvalidDataException("The active account did not match the selected profile after replacement.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(installed);
            }

            if (!await _desktop.LaunchAndVerifyAsync(cancellationToken))
            {
                throw new InvalidOperationException("Codex Desktop did not reopen with the selected account.");
            }
            desktopClosed = false;

            await _recovery.MarkCommittedAsync(transaction, cancellationToken);
            var completedTransaction = transaction;
            transaction = null;
            try
            {
                _recovery.Complete(completedTransaction);
            }
            catch
            {
                return new SwitchResult(true, false, $"Switched to {target.DisplayName}. Recovery-journal cleanup is pending and will be reconciled at next startup.");
            }

            try
            {
                await _vault.TouchAsync(target.Id, cancellationToken);
            }
            catch
            {
                return new SwitchResult(true, false, $"Switched to {target.DisplayName}. The last-used timestamp could not be updated.");
            }

            return new SwitchResult(true, false, $"Switched to {target.DisplayName}.");
        }
        catch (Exception switchError) when (transaction is not null && currentAuth is not null)
        {
            try
            {
                if (!desktopClosed)
                {
                    await _desktop.CloseAsync(CancellationToken.None);
                    desktopClosed = true;
                }

                if (_desktop.HasBlockingCodexProcesses())
                {
                    return new SwitchResult(false, false, "The switch failed and rollback is waiting for Codex processes to close. Recovery data was preserved.");
                }

                await SecureFileSystem.AtomicWriteAsync(_paths.ActiveAuthPath, currentAuth, cancellationToken: cancellationToken);
                var reopened = await _desktop.LaunchAndVerifyAsync(cancellationToken);
                desktopClosed = !reopened;
                if (!reopened)
                {
                    return new SwitchResult(false, true, "The switch failed and the previous account was restored, but Codex must be opened manually.");
                }

                _recovery.Complete(transaction);
                transaction = null;
                return new SwitchResult(false, true, $"The switch failed and was rolled back: {switchError.Message}");
            }
            catch (Exception rollbackError)
            {
                return new SwitchResult(false, false, $"Switch and automatic rollback failed. Recovery data was preserved. {rollbackError.Message}");
            }
        }
        catch (Exception error)
        {
            if (desktopClosed)
            {
                try
                {
                    _ = await _desktop.LaunchAndVerifyAsync(CancellationToken.None);
                }
                catch
                {
                    return new SwitchResult(false, false, $"The switch was stopped before changing the account, and Codex must be opened manually. {error.Message}");
                }
            }

            return new SwitchResult(false, false, error.Message);
        }
        finally
        {
            if (targetAuth is not null)
            {
                CryptographicOperations.ZeroMemory(targetAuth);
            }

            if (currentAuth is not null)
            {
                CryptographicOperations.ZeroMemory(currentAuth);
            }

            _gate.Release();
        }
    }

    public async Task<SwitchResult> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        byte[]? backup = null;
        try
        {
            if (!_recovery.HasPendingTransaction)
            {
                return new SwitchResult(true, false, "No interrupted switch was found.");
            }

            var transaction = await _recovery.ReadPendingAsync(cancellationToken);
            if (transaction.State == "committed" && File.Exists(_paths.ActiveAuthPath))
            {
                var active = await SecureFileSystem.ReadBoundedAsync(_paths.ActiveAuthPath, AuthDocument.MaximumBytes, cancellationToken);
                try
                {
                    var activeFingerprint = AuthDocument.Validate(active).Fingerprint;
                    var profiles = await _vault.GetProfilesAsync(cancellationToken);
                    var target = profiles.SingleOrDefault(profile => profile.Id == transaction.TargetProfileId);
                    if (target?.Fingerprint == activeFingerprint)
                    {
                        _recovery.Complete(transaction);
                        return new SwitchResult(true, false, "The interrupted switch had already installed the target account; its journal was reconciled.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(active);
                }
            }

            backup = await _recovery.ReadBackupAsync(transaction, cancellationToken);
            await _desktop.CloseAsync(cancellationToken);
            if (_desktop.HasBlockingCodexProcesses())
            {
                return new SwitchResult(false, false, "Recovery is waiting for other Codex processes to close.");
            }

            await SecureFileSystem.AtomicWriteAsync(_paths.ActiveAuthPath, backup, cancellationToken: cancellationToken);
            var reopened = await _desktop.LaunchAndVerifyAsync(cancellationToken);
            if (reopened)
            {
                _recovery.Complete(transaction);
            }

            return new SwitchResult(reopened, true, reopened
                ? "The account from before the interrupted switch was restored."
                : "The previous account was restored, but Codex must be opened manually.");
        }
        finally
        {
            if (backup is not null)
            {
                CryptographicOperations.ZeroMemory(backup);
            }

            _gate.Release();
        }
    }
}
