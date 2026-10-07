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

    private sealed class HangOnSecondReplace(IFileDeploymentService inner) : IFileDeploymentService
    {
        private int _calls;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SecondTarget { get; private set; } = "";

        public async Task<FileDeploymentResult> ReplaceAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReplaceAsync(sourcePath, targetPath, cancellationToken);
            if (Interlocked.Increment(ref _calls) < 2) return result;
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
