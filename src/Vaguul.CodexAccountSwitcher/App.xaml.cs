using System.Windows;

namespace Vaguul.CodexAccountSwitcher;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\Vaguul.CodexAccountSwitcher", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Codex Account Switcher is already open.", "Vaguul", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
