using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #23 (DEP-03) fault injection: the process dies after ReShade's proxy is deployed and its Wine
// override written, before the record is saved. The next start must roll both back instead of
// leaving an "external" ReShade that RHI then refuses to install over, update or remove.
public sealed partial class ReShadeChannelTests
{
    [Fact]
    public async Task CrashAfterProxyAndOverride_BeforeRecord_IsRolledBackOnNextStart()
    {
        var (game, store) = CreateGame("crash-recovery");
        var compiler = Path.Combine(_root, "crash-native", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(compiler, 0x8664);
        var userReg = Path.Combine(game.PrefixPath!, "user.reg");
        var journalDir = Path.Combine(_root, "crash-journal");
        var overrides = new WineDllOverrideService();
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir);
        DeploymentOperation? started = null;
        journal.OperationStartedForTesting = operation => started = operation;
        // The second file operation (native D3DCompiler, after the proxy and its override) never returns.
        var files = new HangOnSecondReplace(new TransactionalFileDeploymentService());
        var cache = Path.Combine(_root, "crash-cache");
        var http = new HttpClient(new MutableHandler());
        var service = new ReShadeDeploymentService(http, files, store, overrides, new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);

        var install = service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false, D3DCompiler47Path = compiler });
        await files.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var proxy = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        Assert.True(File.Exists(proxy));
        Assert.True(File.Exists(files.SecondTarget));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
        started!.AbandonAsIfCrashed(); // the process dies here

        var report = await LinuxDeploymentRecovery.RecoverAsync(
            LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(), journalDir), store);

        Assert.Equal(1, report!.RolledBack);
        Assert.False(File.Exists(proxy));
        Assert.False(File.Exists(files.SecondTarget));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.Empty(Directory.GetFiles(journalDir));
        Assert.DoesNotContain(Directory.GetFiles(game.BinaryDirectory!), path => path.Contains(".rhi-", StringComparison.Ordinal));
        Assert.False(install.IsCompleted);

        // RHI no longer sees an external ReShade: a normal install goes through.
        var retry = await NewService(store, new MutableHandler(), cache).InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(retry.Success, retry.Message);
    }

    [Fact]
    public async Task JournaledInstall_RecordCarriesOperationId_SoAnInterruptedCommitIsNotRolledBack()
    {
        var (game, store) = CreateGame("commit-probe");
        var journalDir = Path.Combine(_root, "commit-journal");
        var overrides = new WineDllOverrideService();
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir);
        var cache = Path.Combine(_root, "commit-cache");
        var http = new HttpClient(new MutableHandler());
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), store, overrides, new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(installed.Success, installed.Message);
        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.True(record.Metadata.ContainsKey(LinuxDeploymentRecovery.OperationIdMetadataKey));
        Assert.Empty(Directory.GetFiles(journalDir));

        // A crash after the record was saved but before the journal entry was removed: the record is
        // the commit point, so recovery only cleans up.
        var operation = await journal.BeginAsync("ReShade install: Game", LinuxDeploymentRecovery.RecordCommitProbe(game, ManagedComponentKind.ReShade));
        await operation.TrackFileAsync(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        record.Metadata[LinuxDeploymentRecovery.OperationIdMetadataKey] = operation.Id;
        store.Upsert(record);
        operation.AbandonAsIfCrashed();
        var proxyHash = FileHash.Sha256Hex(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));

        var report = await LinuxDeploymentRecovery.RecoverAsync(LinuxDeploymentRecovery.CreateJournal(overrides, journalDir), store);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(proxyHash, FileHash.Sha256Hex(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.DoesNotContain(Directory.GetFiles(game.BinaryDirectory!), path => path.Contains(".rhi-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CrashMidInstall_ThenUserCreatesReShadeIni_RecoveryUndoesRhiFilesAndKeepsTheIni()
    {
        var (game, store) = CreateGame("crash-user-ini");
        var compiler = Path.Combine(_root, "crash-user-ini-native", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(compiler, 0x8664);
        var userReg = Path.Combine(game.PrefixPath!, "user.reg");
        var journalDir = Path.Combine(_root, "crash-user-ini-journal");
        var overrides = new WineDllOverrideService();
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        DeploymentOperation? started = null;
        journal.OperationStartedForTesting = operation => started = operation;
        var files = new HangOnSecondReplace(new TransactionalFileDeploymentService());
        var cache = Path.Combine(_root, "crash-user-ini-cache");
        var http = new HttpClient(new MutableHandler());
        var service = new ReShadeDeploymentService(http, files, store, overrides, new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);

        _ = service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false, D3DCompiler47Path = compiler });
        await files.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        started!.AbandonAsIfCrashed(); // the process dies here
        // Before RHI starts again the user tunes ReShade in its overlay, which creates ReShade.ini.
        var ini = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nPreprocessorDefinitions=USER_TUNED\n");

        var report = await LinuxDeploymentRecovery.RecoverAsync(
            LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(), journalDir, isPrefixInUse: _ => false), store);

        var entry = Assert.Single(report!.Entries);
        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, entry.Outcome);
        Assert.Contains(entry.Kept, kept => kept.StartsWith(ini, StringComparison.Ordinal));
        Assert.Equal("[GENERAL]\nPreprocessorDefinitions=USER_TUNED\n", File.ReadAllText(ini));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.False(File.Exists(files.SecondTarget));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.Empty(Directory.GetFiles(journalDir)); // final: not retried at every start
    }

    [Fact]
    public async Task CrashAfterAnUpdateEditedReShadeIni_RollsTheIniBackToItsPreUpdateBytes()
    {
        // The update's in-place INI edit is declared (RecordWrittenContentAsync), so recovery knows the half-applied
        // INI is RHI's and restores the pre-update bytes instead of keeping it as "changed by someone else"
        // (integration w1 deploy review 2).
        var (game, store) = CreateGame("crash-ini-edit");
        var cache = Path.Combine(_root, "crash-ini-edit-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var compiler = Path.Combine(_root, "crash-ini-edit-native", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(compiler, 0x8664);
        var options = new ReShadeInstallOptions { ManageIni = true, D3DCompiler47Path = compiler, ShaderPackIds = ["CrosireMaster"] };
        // The user's own ReShade.ini, which RHI then extends with its shader paths: the update's cleanup edits it in place.
        File.WriteAllText(ReShadeIniManager.ResolveIniPath(game.BinaryDirectory!), "[GENERAL]\nEffectSearchPaths=.\\user-shaders\nPreprocessorDefinitions=USER\n");
        var first = await NewService(store, new MutableHandler(), cache).InstallAsync(game, options);
        Assert.True(first.Success, first.Message);
        var ini = ReShadeIniManager.ResolveIniPath(game.BinaryDirectory!);
        var before = File.ReadAllBytes(ini);

        var journalDir = Path.Combine(_root, "crash-ini-edit-journal");
        var overrides = new WineDllOverrideService();
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        DeploymentOperation? started = null;
        journal.OperationStartedForTesting = operation => started = operation;
        // The update edits ReShade.ini first, then replaces the proxy: the process dies right after that write.
        var files = new HangOnSecondReplace(new TransactionalFileDeploymentService(), hangAt: 1);
        var http = new HttpClient(new MutableHandler());
        var service = new ReShadeDeploymentService(http, files, store, overrides, new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);

        _ = service.InstallAsync(game, options);
        await files.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(ini));
        Assert.NotEqual(before, File.ReadAllBytes(ini)); // the update removed RHI's previous INI entries
        started!.AbandonAsIfCrashed(); // the process dies here

        var report = await LinuxDeploymentRecovery.RecoverAsync(
            LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(), journalDir, isPrefixInUse: _ => false), store);

        var entry = Assert.Single(report!.Entries);
        Assert.Equal(JournalRecoveryOutcome.RolledBack, entry.Outcome);
        Assert.Equal(before, File.ReadAllBytes(ini));
        Assert.False(Directory.Exists(Path.Combine(game.BinaryDirectory!, ".rhi-recovered")));
    }

    /// <summary>Lets the first <paramref name="hangAt"/> - 1 replaces through; the next one writes and then never returns.</summary>
    private sealed class HangOnSecondReplace(IFileDeploymentService inner, int hangAt = 2) : IFileDeploymentService
    {
        private int _calls;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SecondTarget { get; private set; } = "";

        public async Task<FileDeploymentResult> ReplaceAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReplaceAsync(sourcePath, targetPath, cancellationToken);
            if (Interlocked.Increment(ref _calls) < hangAt) return result;
            SecondTarget = targetPath;
            Reached.TrySetResult();
            return await new TaskCompletionSource<FileDeploymentResult>().Task; // abandoned, as in a crash
        }

        public Task<FileDeploymentResult> RestoreAsync(string targetPath, CancellationToken cancellationToken = default)
            => inner.RestoreAsync(targetPath, cancellationToken);

        public Task<FileDeploymentBatchResult> ReplaceBatchAsync(IEnumerable<(string Source, string Target)> files, CancellationToken cancellationToken = default)
            => inner.ReplaceBatchAsync(files, cancellationToken);

        public Task<FileDeploymentBatchResult> RestoreBatchAsync(IEnumerable<string> targetPaths, CancellationToken cancellationToken = default)
            => inner.RestoreBatchAsync(targetPaths, cancellationToken);
    }
}
