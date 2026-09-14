using System.Security.Cryptography;
using System.Text;
using Vaguul.CodexAccountSwitcher.Models;
using Vaguul.CodexAccountSwitcher.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("auth fingerprint survives token refresh", TestFingerprintAsync),
    ("invalid auth is rejected", TestInvalidAuthAsync),
    ("DPAPI round trip", TestDpapiAsync),
    ("vault encrypts credentials and metadata has no token", TestVaultAsync),
    ("duplicate account is rejected", TestDuplicateAsync),
    ("switch changes only auth.json", TestSwitchAsync),
    ("failed launch rolls back auth.json", TestRollbackAsync),
    ("invalid target does not close Codex", TestInvalidTargetAsync),
    ("interrupted transaction recovers", TestRecoveryAsync),
    ("committed transaction reconciles without rollback", TestCommittedRecoveryAsync),
    ("trusted binary path is constrained", TestBinaryPathAsync),
    ("path containment rejects siblings", TestPathContainmentAsync),
    ("rate windows use actual durations", TestRateLimitsAsync),
    ("usage metadata persists without credentials", TestUsageMetadataAsync)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static byte[] Auth(string account, string token = "token") =>
    Encoding.UTF8.GetBytes($"{{\"auth_mode\":\"chatgpt\",\"tokens\":{{\"account_id\":\"{account}\",\"access_token\":\"{token}\"}}}}");

static Task TestFingerprintAsync()
{
    Equal(AuthDocument.Validate(Auth("alpha", "old")).Fingerprint, AuthDocument.Validate(Auth("alpha", "new")).Fingerprint);
    NotEqual(AuthDocument.Validate(Auth("alpha")).Fingerprint, AuthDocument.Validate(Auth("beta")).Fingerprint);
    return Task.CompletedTask;
}

static Task TestInvalidAuthAsync()
{
    Throws<InvalidDataException>(() => AuthDocument.Validate("{}"u8));
    Throws<System.Text.Json.JsonException>(() => AuthDocument.Validate("not-json"u8));
    return Task.CompletedTask;
}

static Task TestDpapiAsync()
{
    var protector = new DpapiProtector();
    var secret = Auth("dpapi");
    var encrypted = protector.Protect(secret);
    var decrypted = protector.Unprotect(encrypted);
    try
    {
        True(!secret.SequenceEqual(encrypted), "DPAPI output matched plaintext.");
        True(secret.SequenceEqual(decrypted), "DPAPI round trip changed the bytes.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(encrypted);
        CryptographicOperations.ZeroMemory(decrypted);
    }

    return Task.CompletedTask;
}

static async Task TestVaultAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        var secret = Auth("vault-account", "TOP_SECRET_VALUE");
        try
        {
            var profile = await fixture.Vault.AddAsync("Primary", "#4F8EF7", secret);
            var metadata = await File.ReadAllTextAsync(fixture.Paths.ProfilesPath);
            var encrypted = await File.ReadAllBytesAsync(Path.Combine(fixture.Paths.VaultDirectory, profile.Id + ".auth.dpapi"));
            True(!metadata.Contains("TOP_SECRET_VALUE", StringComparison.Ordinal), "A token leaked into metadata.");
            True(!Encoding.UTF8.GetString(encrypted).Contains("TOP_SECRET_VALUE", StringComparison.Ordinal), "A token remained readable in the vault.");
            var restored = await fixture.Vault.ReadAuthAsync(profile.Id);
            try { True(secret.SequenceEqual(restored), "Vault round trip changed auth.json."); }
            finally { CryptographicOperations.ZeroMemory(restored); }
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    });
}

static async Task TestDuplicateAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        await fixture.Vault.AddAsync("One", "#4F8EF7", Auth("same", "a"));
        await ThrowsAsync<InvalidOperationException>(() => fixture.Vault.AddAsync("Two", "#4F8EF7", Auth("same", "b")));
    });
}

static async Task TestSwitchAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        var source = Auth("source", "fresh-source");
        var target = Auth("target", "target-token");
        await File.WriteAllBytesAsync(fixture.Paths.ActiveAuthPath, source);
        var sourceProfile = await fixture.Vault.AddAsync("Source", "#4F8EF7", Auth("source", "stale-source"));
        var targetProfile = await fixture.Vault.AddAsync("Target", "#33B679", target);
        var sentinel = Path.Combine(fixture.Paths.CodexHome, "sessions", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        await File.WriteAllTextAsync(sentinel, "untouched");
        await File.WriteAllTextAsync(Path.Combine(fixture.Paths.CodexHome, "config.toml"), "model='keep'");

        var result = await fixture.Coordinator(new FakeDesktop(true)).SwitchAsync(targetProfile.Id);

        True(result.Succeeded && !result.RolledBack, result.Message);
        True((await File.ReadAllBytesAsync(fixture.Paths.ActiveAuthPath)).SequenceEqual(target), "Target auth was not installed.");
        Equal("untouched", await File.ReadAllTextAsync(sentinel));
        Equal("model='keep'", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.CodexHome, "config.toml")));
        var savedSource = await fixture.Vault.ReadAuthAsync(sourceProfile.Id);
        try { True(savedSource.SequenceEqual(source), "The departing account refresh was not preserved."); }
        finally { CryptographicOperations.ZeroMemory(savedSource); }
    });
}

static async Task TestRollbackAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        var source = Auth("source", "source-token");
        await File.WriteAllBytesAsync(fixture.Paths.ActiveAuthPath, source);
        await fixture.Vault.AddAsync("Source", "#4F8EF7", source);
        var target = await fixture.Vault.AddAsync("Target", "#33B679", Auth("target", "target-token"));

        var result = await fixture.Coordinator(new FakeDesktop(false, true)).SwitchAsync(target.Id);

        True(!result.Succeeded && result.RolledBack, result.Message);
        True((await File.ReadAllBytesAsync(fixture.Paths.ActiveAuthPath)).SequenceEqual(source), "Rollback did not restore auth.json.");
        True(!File.Exists(fixture.Paths.TransactionPath), "Completed rollback left a pending journal.");
    });
}

static async Task TestInvalidTargetAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        await File.WriteAllBytesAsync(fixture.Paths.ActiveAuthPath, Auth("source"));
        var desktop = new FakeDesktop(true);
        var result = await fixture.Coordinator(desktop).SwitchAsync(Guid.NewGuid().ToString("N"));
        True(!result.Succeeded, "An unknown profile unexpectedly switched.");
        Equal(0, desktop.CloseCount);
    });
}

static async Task TestRecoveryAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        var source = Auth("source", "source-token");
        await File.WriteAllBytesAsync(fixture.Paths.ActiveAuthPath, Auth("target"));
        var sourceProfile = await fixture.Vault.AddAsync("Source", "#4F8EF7", source);
        var targetProfile = await fixture.Vault.AddAsync("Target", "#33B679", Auth("target"));
        _ = await fixture.Recovery.BeginAsync(sourceProfile.Id, targetProfile.Id, source);

        var result = await fixture.Coordinator(new FakeDesktop(true)).RecoverPendingAsync();

        True(result.Succeeded && result.RolledBack, result.Message);
        True((await File.ReadAllBytesAsync(fixture.Paths.ActiveAuthPath)).SequenceEqual(source), "Recovery did not restore the prior account.");
        True(!File.Exists(fixture.Paths.TransactionPath), "Recovery journal was not completed.");
    });
}

static async Task TestCommittedRecoveryAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        var source = Auth("source");
        var target = Auth("target");
        await File.WriteAllBytesAsync(fixture.Paths.ActiveAuthPath, target);
        var sourceProfile = await fixture.Vault.AddAsync("Source", "#4F8EF7", source);
        var targetProfile = await fixture.Vault.AddAsync("Target", "#33B679", target);
        var transaction = await fixture.Recovery.BeginAsync(sourceProfile.Id, targetProfile.Id, source);
        await fixture.Recovery.MarkCommittedAsync(transaction);
        var desktop = new FakeDesktop(true);

        var result = await fixture.Coordinator(desktop).RecoverPendingAsync();

        True(result.Succeeded && !result.RolledBack, result.Message);
        True((await File.ReadAllBytesAsync(fixture.Paths.ActiveAuthPath)).SequenceEqual(target), "Committed target was rolled back.");
        Equal(0, desktop.CloseCount);
        True(!File.Exists(fixture.Paths.TransactionPath), "Committed journal was not reconciled.");
    });
}

static async Task TestBinaryPathAsync()
{
    var root = NewTemporaryRoot();
    try
    {
        var trustedRoot = Path.Combine(root, "OpenAI", "Codex", "bin");
        var trusted = Path.Combine(trustedRoot, "version", "codex.exe");
        var untrusted = Path.Combine(root, "elsewhere", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(trusted)!);
        Directory.CreateDirectory(Path.GetDirectoryName(untrusted)!);
        await File.WriteAllBytesAsync(trusted, [1]);
        await File.WriteAllBytesAsync(untrusted, [1]);
        True(CodexBinaryLocator.IsTrustedCandidate(trusted, trustedRoot), "Installed binary was rejected.");
        True(!CodexBinaryLocator.IsTrustedCandidate(untrusted, trustedRoot), "Untrusted binary was accepted.");
    }
    finally { Directory.Delete(root, true); }
}

static Task TestPathContainmentAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "vault");
    True(SecureFileSystem.IsInside(Path.Combine(root, "profile"), root), "Child path was rejected.");
    True(!SecureFileSystem.IsInside(root + "-evil", root), "Sibling path was accepted.");
    return Task.CompletedTask;
}

static Task TestRateLimitsAsync()
{
    var json = """
        {"result":{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":7,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":51,"windowDurationMins":10080,"resetsAt":1800100000}},"review":{"primary":{"usedPercent":20,"windowDurationMins":15,"resetsAt":1800000000}}}}}
        """u8;
    var usage = RateLimitParser.Parse(json);
    Equal(3, usage.Windows.Count);
    Equal("5 hours", usage.Windows[0].Label);
    Equal("7 days", usage.Windows[1].Label);
    Equal("15 minutes", usage.Windows[2].Label);
    Equal(49d, usage.Windows[1].RemainingPercent);
    return Task.CompletedTask;
}

static async Task TestUsageMetadataAsync()
{
    await WithFixtureAsync(async fixture =>
    {
        var secret = Auth("usage-account", "usage-secret");
        try
        {
            var profile = await fixture.Vault.AddAsync("Usage", "#4F8EF7", secret);
            await fixture.Vault.UpdateUsageAsync(profile.Id, new UsageSnapshot
            {
                Status = "available",
                PlanType = "pro",
                Windows =
                [
                    new UsageWindow
                    {
                        BucketId = "codex",
                        Position = "primary",
                        Label = "5 hours",
                        UsedPercent = 25,
                        RemainingPercent = 75,
                        WindowDurationMinutes = 300,
                        ResetsAt = DateTimeOffset.UtcNow.AddHours(2)
                    }
                ]
            });

            var loaded = (await fixture.Vault.GetProfilesAsync()).Single();
            Equal("available", loaded.Usage?.Status);
            Equal(75d, loaded.Usage?.Windows.Single().RemainingPercent);
            var metadata = await File.ReadAllTextAsync(fixture.Paths.ProfilesPath);
            True(!metadata.Contains("usage-secret", StringComparison.Ordinal), "A credential leaked into usage metadata.");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    });
}

static async Task WithFixtureAsync(Func<Fixture, Task> test)
{
    var root = NewTemporaryRoot();
    try
    {
        var paths = new AppPaths(Path.Combine(root, "codex"), Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.CodexHome);
        var protector = new DpapiProtector();
        var vault = new ProfileVault(paths, protector);
        vault.Initialize();
        var recovery = new SwitchRecoveryStore(paths, protector);
        await test(new Fixture(paths, vault, recovery));
    }
    finally { Directory.Delete(root, true); }
}

static string NewTemporaryRoot() => Path.Combine(Path.GetTempPath(), "vaguul-switcher-tests", Guid.NewGuid().ToString("N"));
static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
static void NotEqual<T>(T left, T right) { if (EqualityComparer<T>.Default.Equals(left, right)) throw new InvalidOperationException("Values unexpectedly matched."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

sealed record Fixture(AppPaths Paths, ProfileVault Vault, SwitchRecoveryStore Recovery)
{
    public AccountSwitchCoordinator Coordinator(ICodexDesktopController desktop) => new(Paths, Vault, Recovery, desktop);
}

sealed class FakeDesktop(params bool[] launchResults) : ICodexDesktopController
{
    private readonly Queue<bool> _launchResults = new(launchResults);
    public int CloseCount { get; private set; }
    public bool Blocking { get; set; }
    public Task CloseAsync(CancellationToken cancellationToken = default) { CloseCount++; return Task.CompletedTask; }
    public bool HasBlockingCodexProcesses() => Blocking;
    public Task<bool> LaunchAndVerifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_launchResults.Count == 0 || _launchResults.Dequeue());
}
