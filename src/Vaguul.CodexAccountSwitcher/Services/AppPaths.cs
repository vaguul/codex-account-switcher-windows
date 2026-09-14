namespace Vaguul.CodexAccountSwitcher.Services;

public sealed class AppPaths
{
    public AppPaths(string codexHome, string dataRoot)
    {
        CodexHome = Path.GetFullPath(codexHome);
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string CodexHome { get; }
    public string DataRoot { get; }
    public string ActiveAuthPath => Path.Combine(CodexHome, "auth.json");
    public string ProfilesPath => Path.Combine(DataRoot, "profiles.json");
    public string ProfilesBackupPath => Path.Combine(DataRoot, "profiles.json.bak");
    public string VaultDirectory => Path.Combine(DataRoot, "vault");
    public string BackupDirectory => Path.Combine(DataRoot, "backups");
    public string TemporaryDirectory => Path.Combine(DataRoot, "temporary");
    public string TransactionPath => Path.Combine(DataRoot, "switch-transaction.json");

    public static AppPaths FromEnvironment()
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Environment.ExpandEnvironmentVariables(configuredHome);
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vaguul",
            "CodexAccountSwitcher");
        return new AppPaths(codexHome, dataRoot);
    }
}
