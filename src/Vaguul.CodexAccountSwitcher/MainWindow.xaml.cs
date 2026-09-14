using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vaguul.CodexAccountSwitcher.Models;
using Vaguul.CodexAccountSwitcher.Services;

namespace Vaguul.CodexAccountSwitcher;

public partial class MainWindow : Window
{
    private static readonly string[] Colors = ["#4F8EF7", "#33B679", "#E9A23B", "#D66BA0", "#8B7CF6"];
    private readonly AppPaths _paths = AppPaths.FromEnvironment();
    private readonly ProfileVault _vault;
    private readonly SwitchRecoveryStore _recovery;
    private readonly AccountSwitchCoordinator _coordinator;
    private string? _activeFingerprint;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        var protector = new DpapiProtector();
        _vault = new ProfileVault(_paths, protector);
        _recovery = new SwitchRecoveryStore(_paths, protector);
        _coordinator = new AccountSwitchCoordinator(_paths, _vault, _recovery, new CodexDesktopController());
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _vault.Initialize();
            if (_recovery.HasPendingTransaction)
            {
                var answer = MessageBox.Show(
                    "An interrupted account switch was found. Restore the previous account now? Codex will close and reopen.",
                    "Recovery required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    SetBusy(true, "Recovering previous account...");
                    var recovery = await _coordinator.RecoverPendingAsync();
                    MessageBox.Show(recovery.Message, "Recovery", MessageBoxButton.OK,
                        recovery.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
            }

            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false, "Ready"); }
    }

    private async void SaveActiveButton_Click(object sender, RoutedEventArgs e)
    {
        byte[]? auth = null;
        try
        {
            SetBusy(true, "Reading active account...");
            auth = await SecureFileSystem.ReadBoundedAsync(_paths.ActiveAuthPath, AuthDocument.MaximumBytes);
            _ = AuthDocument.Validate(auth);
            var dialog = new AccountNameDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var count = (await _vault.GetProfilesAsync()).Count;
            await _vault.AddAsync(dialog.AccountName, Colors[count % Colors.Length], auth);
            await ReloadAsync();
            StatusText.Text = "Active account saved securely.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            if (auth is not null) CryptographicOperations.ZeroMemory(auth);
            SetBusy(false, StatusText.Text);
        }
    }

    private async void SwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not AccountProfile profile)
        {
            ShowError("Select an account first.");
            return;
        }

        if (MessageBox.Show(
                $"Switch to {profile.DisplayName}? Codex Desktop will close and reopen. Running CLI sessions must be closed first.",
                "Confirm account switch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, $"Switching to {profile.DisplayName}...");
            var result = await _coordinator.SwitchAsync(profile.Id);
            StatusText.Text = result.Message;
            if (!result.Succeeded)
                MessageBox.Show(result.Message, "Account switch", MessageBoxButton.OK, MessageBoxImage.Warning);
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not AccountProfile profile)
        {
            ShowError("Select an account first.");
            return;
        }

        try
        {
            if (await GetActiveFingerprintAsync() == profile.Fingerprint)
            {
                ShowError("The active account cannot be deleted. Switch to another saved account first.");
                return;
            }

            if (MessageBox.Show($"Delete the encrypted profile for {profile.DisplayName}?", "Delete account",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            await _vault.DeleteAsync(profile.Id);
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Refreshing...");
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private async Task ReloadAsync()
    {
        var profiles = await _vault.GetProfilesAsync();
        ProfileList.ItemsSource = profiles;
        var fingerprint = await GetActiveFingerprintAsync();
        _activeFingerprint = fingerprint;
        var active = profiles.FirstOrDefault(profile => profile.Fingerprint == fingerprint);
        ActiveAccountText.Text = active?.DisplayName ?? (fingerprint is null ? "No valid login detected" : "Active account is not saved");
        ActiveDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active?.ColorHex ?? "#6B737B"));
        UpdateSelectionState();
    }

    private async Task<string?> GetActiveFingerprintAsync()
    {
        if (!File.Exists(_paths.ActiveAuthPath)) return null;
        byte[]? auth = null;
        try
        {
            auth = await SecureFileSystem.ReadBoundedAsync(_paths.ActiveAuthPath, AuthDocument.MaximumBytes);
            return AuthDocument.Validate(auth).Fingerprint;
        }
        catch { return null; }
        finally { if (auth is not null) CryptographicOperations.ZeroMemory(auth); }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        SaveActiveButton.IsEnabled = !busy;
        ProfileList.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
        SwitchButton.IsEnabled = !busy;
        if (!busy) UpdateSelectionState();
        StatusText.Text = status;
    }

    private void UpdateSelectionState()
    {
        if (_busy || ProfileList.SelectedItem is not AccountProfile profile)
        {
            if (!_busy)
            {
                DeleteButton.IsEnabled = ProfileList.SelectedItem is AccountProfile;
                SwitchButton.Content = "Switch to selected";
            }
            return;
        }

        var isActive = string.Equals(profile.Fingerprint, _activeFingerprint, StringComparison.Ordinal);
        DeleteButton.IsEnabled = !isActive;
        SwitchButton.IsEnabled = !isActive;
        SwitchButton.Content = isActive ? "Already active" : "Switch to selected";
        StatusText.Text = isActive
            ? "This account is already active. Save another account to switch."
            : $"Ready to switch to {profile.DisplayName}.";
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        MessageBox.Show(message, "Vaguul Codex Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
