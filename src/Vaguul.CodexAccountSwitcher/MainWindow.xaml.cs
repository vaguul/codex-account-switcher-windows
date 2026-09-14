using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinFormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsToolStripSeparator = System.Windows.Forms.ToolStripSeparator;
using WinFormsSystemIcons = System.Drawing.SystemIcons;
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
    private readonly CodexAppServerClient _usageClient;
    private readonly CodexLoginClient _loginClient;
    private readonly ProfileTransferService _transfer;
    private readonly WinFormsNotifyIcon _trayIcon;
    private string? _activeFingerprint;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        var protector = new DpapiProtector();
        _vault = new ProfileVault(_paths, protector);
        _recovery = new SwitchRecoveryStore(_paths, protector);
        _coordinator = new AccountSwitchCoordinator(_paths, _vault, _recovery, new CodexDesktopController());
        _usageClient = new CodexAppServerClient(_paths, new CodexBinaryLocator());
        _loginClient = new CodexLoginClient(_paths, new CodexBinaryLocator());
        _transfer = new ProfileTransferService(_vault);
        _trayIcon = CreateTrayIcon();
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _vault.Initialize();
            if (_recovery.HasPendingTransaction)
            {
                var answer = System.Windows.MessageBox.Show(
                    "An interrupted account switch was found. Restore the previous account now? Codex will close and reopen.",
                    "Recovery required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    SetBusy(true, "Recovering previous account...");
                    var recovery = await _coordinator.RecoverPendingAsync();
                    System.Windows.MessageBox.Show(recovery.Message, "Recovery", MessageBoxButton.OK,
                        recovery.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
            }

            await RecoverOrphanedActiveAsync();
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false, StatusText.Text); }
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
            if (dialog.ShowDialog() != true)
            {
                StatusText.Text = "Ready.";
                return;
            }

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

    private async Task RecoverOrphanedActiveAsync()
    {
        var orphanedId = await _vault.FindOrphanedActiveAsync();
        if (orphanedId is null)
        {
            return;
        }

        var dialog = new AccountNameDialog("Recover active account") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            StatusText.Text = "An encrypted active account snapshot is waiting to be recovered.";
            return;
        }

        var count = (await _vault.GetProfilesAsync()).Count;
        await _vault.AdoptOrphanAsync(orphanedId, dialog.AccountName, Colors[count % Colors.Length]);
        StatusText.Text = "Recovered the active encrypted account snapshot.";
    }

    private async void SwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not AccountProfile profile)
        {
            ShowError("Select an account first.");
            return;
        }

        if (System.Windows.MessageBox.Show(
            $"Switch to {profile.DisplayName}? Codex Desktop, CLI sessions, and app-server processes will close and Codex will reopen. Unsaved CLI work may be lost.",
            "Confirm account switch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            StatusText.Text = "Account switch canceled.";
            return;
        }

        try
        {
            SetBusy(true, $"Switching to {profile.DisplayName}...");
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)
                || !Path.IsPathFullyQualified(executablePath)
                || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Detached switching is available from the published Windows executable, not from a dotnet development host.");
            }

            _ = await SwitchTaskLauncher.ScheduleAsync(profile.Id, executablePath);
            System.Windows.MessageBox.Show(
                "The switch was queued safely. This window will close, Codex will restart, and the detached worker will finish the account change.",
                "Account switch",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async void BrowserLoginButton_Click(object sender, RoutedEventArgs e)
    {
        await LoginButton_ClickAsync(CodexLoginMode.Browser);
    }

    private async void DeviceLoginButton_Click(object sender, RoutedEventArgs e)
    {
        await LoginButton_ClickAsync(CodexLoginMode.DeviceCode);
    }

    private async Task LoginButton_ClickAsync(CodexLoginMode mode)
    {
        byte[]? auth = null;
        try
        {
            var method = mode == CodexLoginMode.Browser ? "browser" : "device-code";
            SetBusy(true, $"Waiting for Codex {method} sign-in...");
            auth = await _loginClient.LoginAsync(mode);
            var dialog = new AccountNameDialog { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                StatusText.Text = "Sign-in canceled after authentication.";
                return;
            }

            var count = (await _vault.GetProfilesAsync()).Count;
            await _vault.AddAsync(dialog.AccountName, Colors[count % Colors.Length], auth);
            await ReloadAsync();
            StatusText.Text = "Account saved securely.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            if (auth is not null) CryptographicOperations.ZeroMemory(auth);
            SetBusy(false, StatusText.Text);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Vaguul profile package (*.vaguul-profiles)|*.vaguul-profiles|All files (*.*)|*.*",
            DefaultExt = ".vaguul-profiles",
            AddExtension = true,
            FileName = "vaguul-profiles.vaguul-profiles",
            OverwritePrompt = true
        };
        if (fileDialog.ShowDialog() != true)
        {
            StatusText.Text = "Export canceled.";
            return;
        }

        var passwordDialog = new PasswordDialog("Export profiles", "Create a password for this portable package.", confirmPassword: true)
        {
            Owner = this
        };
        if (passwordDialog.ShowDialog() != true)
        {
            StatusText.Text = "Export canceled.";
            return;
        }

        var password = passwordDialog.CopyPassword();
        try
        {
            SetBusy(true, "Encrypting profiles...");
            await _transfer.ExportAsync(fileDialog.FileName, password);
            StatusText.Text = "Encrypted profiles exported.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            Array.Clear(password, 0, password.Length);
            SetBusy(false, StatusText.Text);
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Vaguul profile package (*.vaguul-profiles)|*.vaguul-profiles|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog() != true)
        {
            StatusText.Text = "Import canceled.";
            return;
        }

        var passwordDialog = new PasswordDialog("Import profiles", "Enter the password for this portable package.", confirmPassword: false)
        {
            Owner = this
        };
        if (passwordDialog.ShowDialog() != true)
        {
            StatusText.Text = "Import canceled.";
            return;
        }

        var password = passwordDialog.CopyPassword();
        try
        {
            SetBusy(true, "Decrypting profiles...");
            var result = await _transfer.ImportAsync(fileDialog.FileName, password);
            await ReloadAsync();
            StatusText.Text = $"Imported {result.Imported} profile{(result.Imported == 1 ? "" : "s")}; skipped {result.SkippedDuplicates} duplicate{(result.SkippedDuplicates == 1 ? "" : "s")}.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            Array.Clear(password, 0, password.Length);
            SetBusy(false, StatusText.Text);
        }
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

            if (System.Windows.MessageBox.Show($"Delete the encrypted profile for {profile.DisplayName}?", "Delete account",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                StatusText.Text = "Deletion canceled.";
                return;
            }

            SetBusy(true, $"Deleting {profile.DisplayName}...");
            await _vault.DeleteAsync(profile.Id);
            await ReloadAsync();
            StatusText.Text = $"Deleted {profile.DisplayName}.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Refreshing usage from Codex...");
            var profiles = await _vault.GetProfilesAsync();
            if (profiles.Count == 0)
            {
                StatusText.Text = "Save an account before refreshing usage.";
                return;
            }

            var failures = 0;
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                StatusText.Text = $"Refreshing usage {index + 1}/{profiles.Count}: {profile.DisplayName}...";
                byte[]? auth = null;
                try
                {
                    auth = await _vault.ReadAuthAsync(profile.Id);
                    var snapshot = await _usageClient.ReadProfileAsync(auth);
                    await _vault.UpdateUsageAsync(profile.Id, snapshot.Usage, snapshot.Email);
                }
                catch
                {
                    failures++;
                }
                finally
                {
                    if (auth is not null) CryptographicOperations.ZeroMemory(auth);
                }
            }

            await ReloadAsync();
            StatusText.Text = failures == 0
                ? "Usage refreshed."
                : $"Usage refreshed with {failures} unavailable profile{(failures == 1 ? "" : "s")}; previous data was kept.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private async Task ReloadAsync()
    {
        var selectedId = (ProfileList.SelectedItem as AccountProfile)?.Id;
        var profiles = await _vault.GetProfilesAsync();
        var duplicateNames = profiles
            .GroupBy(profile => profile.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            profile.DisplayLabel = duplicateNames.Contains(profile.Id)
                ? $"{profile.DisplayName} · {profile.Email ?? $"profile {profile.Id[..6]}"}"
                : profile.DisplayName;
        }

        var fingerprint = await GetActiveFingerprintAsync();
        _activeFingerprint = fingerprint;
        var active = profiles.FirstOrDefault(profile => profile.Fingerprint == fingerprint);
        foreach (var profile in profiles) profile.IsActive = profile.Fingerprint == fingerprint;
        ProfileList.ItemsSource = profiles;
        ProfileList.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedId);
        ActiveAccountText.Text = active?.DisplayName ?? (fingerprint is null ? "No valid login detected" : "Active account is not saved");
        ActiveDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(active?.ColorHex ?? "#6B737B"));
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
        BrowserLoginButton.IsEnabled = !busy;
        DeviceLoginButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        ProfileList.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
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
        System.Windows.MessageBox.Show(message, "Vaguul Codex Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private WinFormsNotifyIcon CreateTrayIcon()
    {
        var menu = new WinFormsContextMenuStrip();
        menu.Items.Add(new WinFormsToolStripMenuItem("Open", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray)));
        menu.Items.Add(new WinFormsToolStripMenuItem("Refresh usage", null, (_, _) => Dispatcher.BeginInvoke(RefreshFromTray)));
        menu.Items.Add(new WinFormsToolStripSeparator());
        menu.Items.Add(new WinFormsToolStripMenuItem("Exit", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray)));

        var icon = new WinFormsNotifyIcon
        {
            Icon = WinFormsSystemIcons.Application,
            Text = "Vaguul Codex Account Switcher",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        return icon;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        StatusText.Text = "Running in the system tray.";
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void RefreshFromTray()
    {
        ShowFromTray();
        RefreshButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }

    private void ExitFromTray()
    {
        Close();
    }
}
