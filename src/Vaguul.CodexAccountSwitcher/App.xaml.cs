using System.Windows;
using Vaguul.CodexAccountSwitcher.Services;

namespace Vaguul.CodexAccountSwitcher;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\Vaguul.CodexAccountSwitcher";
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (SwitchTaskLauncher.TryParse(e.Args, out var switchTask))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            _ = RunSwitchWorkerAsync(switchTask);
            return;
        }

        _singleInstance = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Codex Account Switcher is already open.", "Vaguul", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    private async Task RunSwitchWorkerAsync(SwitchTaskRequest request)
    {
        try
        {
            // Give the original UI time to exit and release the single-instance mutex.
            await Task.Delay(TimeSpan.FromSeconds(3));
            await AcquireSingleInstanceAsync();

            var paths = AppPaths.FromEnvironment();
            var protector = new DpapiProtector();
            var vault = new ProfileVault(paths, protector);
            vault.Initialize();
            var coordinator = new AccountSwitchCoordinator(
                paths,
                vault,
                new SwitchRecoveryStore(paths, protector),
                new CodexDesktopController());
            var result = await coordinator.SwitchAsync(request.ProfileId);
            System.Windows.MessageBox.Show(
                result.Message,
                "Vaguul Codex Account Switcher",
                MessageBoxButton.OK,
                result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Vaguul Codex Account Switcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            try { await SwitchTaskLauncher.DeleteAsync(request.TaskName); }
            catch { }
            Shutdown();
        }
    }

    private async Task AcquireSingleInstanceAsync()
    {
        _singleInstance = new Mutex(false, MutexName);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                if (_singleInstance.WaitOne(0))
                {
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException("The previous switcher instance did not close in time.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
