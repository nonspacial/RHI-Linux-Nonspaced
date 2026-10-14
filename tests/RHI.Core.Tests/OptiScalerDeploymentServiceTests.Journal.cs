using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/dllnames phase B (#23, #31): OptiScaler's install, update and removal and ReShade's removal are journaled
// (DeploymentScope). A crash before the commit write is rolled back at the next start — files, RHI ReShade's name in the
// OptiScaler chain and the Wine override — and the ledger still describes what is on disk. In process, a failure
// part-way rolls everything back (a removal never stops half-way).
public sealed partial class OptiScalerDeploymentServiceTests
{
    private sealed record Journaled(string Dir, string Ledger, DeploymentJournal Journal, WineDllOverrideService Overrides);

    private Journaled NewJournaled(string name, LinuxDeploymentRecordStore store)
    {
        var dir = Path.Combine(_root, name, "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(dir, store);
        return new Journaled(dir, Path.Combine(_root, name, "records.json"), journal, overrides);
    }

    private static OptiScalerDeploymentService NewJournaledService(LinuxDeploymentRecordStore store, IOptiScalerPackageProvider provider, Journaled j)
        => new(new TransactionalFileDeploymentService(), store, j.Overrides, provider, journal: j.Journal);

    private ReShadeDeploymentService NewReShadeService(LinuxDeploymentRecordStore store, Journaled? j = null)
    {
        var http = new HttpClient(new NeverUsedHttpHandler());
        return new ReShadeDeploymentService(
            http,
            new TransactionalFileDeploymentService(),
            store,
            j?.Overrides ?? new WineDllOverrideService(),
            new NeverUsedReShadeExtractor(),
            Path.Combine(_root, "unused-reshade-cache"),
            new ReShadeShaderPackService(http, Path.Combine(_root, "unused-reshade-cache", "shader-packs"),
                customRoot: Path.Combine(_root, "custom-shaders")),
            journal: j?.Journal);
    }

    private static string? DxgiOverride(DetectedGame game)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")), "dxgi", "Game.exe");

    [Fact]
    public async Task Install_OverManagedReShade_CrashAtTheCommitWrite_NextStartPutsReShadeBackAtItsNameWithItsRecord()
    {
        var (game, store) = CreateGame("journal-install", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var reShadeBytes = File.ReadAllBytes(dxgi);
        var j = NewJournaled("journal-install", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("journal-install-package")), j);
        using var death = new SimulatedDeath(j.Journal);
        store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(game));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));

        var report = await SimulatedDeath.RecoverAsync(j.Dir, j.Ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(dxgi));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler", "libxess.dll")));
        var ledger = new LinuxDeploymentRecordStore(j.Ledger);
        Assert.Null(ledger.Find(game, ManagedComponentKind.OptiScaler));
        var reShade = ledger.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(Path.GetFullPath(dxgi), Path.GetFullPath(Assert.Single(reShade.Files).Path));
        Assert.NotNull(reShade.WineOverride);
        Assert.Equal("native,builtin", DxgiOverride(game));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.rhi-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Install_CrashAtTheCommitWrite_NextStartRemovesEveryFileAndTheOverride()
    {
        var (game, store) = CreateGame("journal-install-plain", GraphicsApi.DirectX12);
        var j = NewJournaled("journal-install-plain", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("journal-install-plain-package")), j);
        using var death = new SimulatedDeath(j.Journal);
        store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(game));
        Assert.True(died);
        Assert.Equal("native,builtin", DxgiOverride(game));

        await SimulatedDeath.RecoverAsync(j.Dir, j.Ledger);

        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler", "libxess.dll")));
        Assert.Null(DxgiOverride(game));
        Assert.Null(new LinuxDeploymentRecordStore(j.Ledger).Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Update_CrashAtTheCommitWrite_NextStartPutsThePreviousFilesBack()
    {
        var (game, store) = CreateGame("journal-update", GraphicsApi.DirectX12);
        var first = CreatePackage("journal-update-1");
        var second = CreatePackage("journal-update-2") with { Version = "9.9.10" };
        File.AppendAllText(Path.Combine(second.RootDirectory, "OptiScaler", "libxess.dll"), " v2");
        File.WriteAllText(Path.Combine(second.RootDirectory, "OptiScaler", "new-runtime.dll"), "MZ new in v2");
        var provider = new SwitchingPackageProvider(first);
        var j = NewJournaled("journal-update", store);
        var service = NewJournaledService(store, provider, j);
        Assert.True((await service.InstallAsync(game)).Success);
        var xess = Path.Combine(game.BinaryDirectory!, "OptiScaler", "libxess.dll");
        var before = File.ReadAllBytes(xess);
        var ledgerBefore = File.ReadAllText(j.Ledger);
        provider.Package = second;
        using var death = new SimulatedDeath(j.Journal);
        store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.UpdateAsync(game));
        Assert.True(died);
        Assert.NotEqual(before, File.ReadAllBytes(xess));

        var report = await SimulatedDeath.RecoverAsync(j.Dir, j.Ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(before, File.ReadAllBytes(xess));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler", "new-runtime.dll")));
        Assert.Equal(ledgerBefore, File.ReadAllText(j.Ledger));
    }

    [Fact]
    public async Task Remove_WithReShadeInTheChain_CrashAtTheCommitWrite_NextStartPutsOptiScalerAndReShade64Back()
    {
        var (game, store) = CreateGame("journal-remove", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var j = NewJournaled("journal-remove", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("journal-remove-package")), j);
        Assert.True((await service.InstallAsync(game)).Success);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var reshade64 = Path.Combine(game.BinaryDirectory!, "ReShade64.dll");
        var optiBytes = File.ReadAllBytes(dxgi);
        var reShadeBytes = File.ReadAllBytes(reshade64);
        using var death = new SimulatedDeath(j.Journal);
        // The removal's stamp is the first save; the commit write (OptiScaler out, ReShade moved back) dies.
        var saves = 0;
        store.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null;

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(game));
        Assert.True(died);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(dxgi));

        var report = await SimulatedDeath.RecoverAsync(j.Dir, j.Ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(optiBytes, File.ReadAllBytes(dxgi));
        Assert.Equal(reShadeBytes, File.ReadAllBytes(reshade64));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
        var ledger = new LinuxDeploymentRecordStore(j.Ledger);
        Assert.NotNull(ledger.Find(game, ManagedComponentKind.OptiScaler));
        Assert.Equal(Path.GetFullPath(reshade64), Path.GetFullPath(Assert.Single(ledger.Find(game, ManagedComponentKind.ReShade)!.Files).Path));
        Assert.Equal("native,builtin", DxgiOverride(game));
    }

    [Fact]
    public async Task Remove_FailurePartWay_RollsBackEverythingKeepsTheRecordAndARetrySucceeds()
    {
        var (game, store) = CreateGame("journal-remove-fail", GraphicsApi.DirectX12);
        var j = NewJournaled("journal-remove-fail", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("journal-remove-fail-package")), j);
        Assert.True((await service.InstallAsync(game)).Success);
        var nested = Path.Combine(game.BinaryDirectory!, "OptiScaler");
        var xess = Path.Combine(nested, "libxess.dll");
        // The commit write fails after every file was handled: the removal must not stop half-way.
        var saves = 0;
        store.SaveFaultForTesting = () => ++saves == 2 ? new IOException("disk full (test)") : null;

        var failed = await service.RemoveAsync(game);

        Assert.False(failed.Success);
        Assert.StartsWith(OptiScalerDeploymentService.RemovalFailedPrefix, failed.Message);
        Assert.True(File.Exists(xess));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("native,builtin", DxgiOverride(game));
        Assert.NotNull(store.Find(game, ManagedComponentKind.OptiScaler));
        Assert.Empty(Directory.GetFiles(j.Dir));

        store.SaveFaultForTesting = null;
        var retry = await service.RemoveAsync(game);
        Assert.True(retry.Success, retry.Message);
        Assert.False(File.Exists(xess));
        Assert.Null(DxgiOverride(game));
    }

    [Fact]
    public async Task ReShadeRemove_InTheOptiScalerChain_CrashAtTheCommitWrite_NextStartPutsReShade64AndLoadReshadeBack()
    {
        var (game, store) = CreateGame("journal-reshade-remove", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var j = NewJournaled("journal-reshade-remove", store);
        var opti = NewJournaledService(store, new FakePackageProvider(CreatePackage("journal-reshade-remove-package")), j);
        Assert.True((await opti.InstallAsync(game)).Success);
        var reshade64 = Path.Combine(game.BinaryDirectory!, "ReShade64.dll");
        var optiIni = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        var reShadeBytes = File.ReadAllBytes(reshade64);
        var iniBefore = File.ReadAllText(optiIni);
        Assert.Contains("LoadReshade=true", iniBefore, StringComparison.OrdinalIgnoreCase);
        var reshade = NewReShadeService(store, j);
        using var death = new SimulatedDeath(j.Journal);
        var saves = 0;
        store.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null;

        var (died, _) = await death.RunUntilDeathAsync(() => reshade.RemoveAsync(game));
        Assert.True(died);
        Assert.False(File.Exists(reshade64));

        var report = await SimulatedDeath.RecoverAsync(j.Dir, j.Ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(reshade64));
        Assert.Equal(iniBefore, File.ReadAllText(optiIni));
        var ledger = new LinuxDeploymentRecordStore(j.Ledger);
        Assert.NotNull(ledger.Find(game, ManagedComponentKind.ReShade));
        Assert.NotNull(ledger.Find(game, ManagedComponentKind.OptiScaler));
    }

    // Review nit 4: the chain's commit write saves OptiScaler's record (LoadReshade=false INI hash) with ReShade's
    // removal. When that write lands and then reports failure, the rollback puts OptiScaler's original record back
    // beside ReShade's, so the ledger matches the INI the rollback restores (LoadReshade=true).
    [Fact]
    public async Task ReShadeRemove_InTheOptiScalerChain_CommitWriteLandsThenFails_OptiScalersRecordGoesBackWithTheIni()
    {
        var (game, store) = CreateGame("journal-reshade-written-fault", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var j = NewJournaled("journal-reshade-written-fault", store);
        var opti = NewJournaledService(store, new FakePackageProvider(CreatePackage("journal-reshade-written-fault-package")), j);
        Assert.True((await opti.InstallAsync(game)).Success);
        var optiIni = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        var iniBefore = File.ReadAllBytes(optiIni);
        var optiRecordBefore = store.Find(game, ManagedComponentKind.OptiScaler)!;
        var iniEntry = optiRecordBefore.Files.Single(file => Path.GetFileName(file.Path) == "OptiScaler.ini");
        Assert.Equal(FileHash.Sha256HexOf(iniBefore), iniEntry.Sha256, ignoreCase: true);
        var reshade = NewReShadeService(store, j);
        // Save 1 stamps ReShade's record; save 2 is the commit write, which replaces the ledger and then fails.
        var saves = 0;
        store.SaveWrittenFaultForTesting = () => ++saves == 2 ? new IOException("directory fsync failed (test)") : null;

        var failed = await reshade.RemoveAsync(game);

        store.SaveWrittenFaultForTesting = null;
        Assert.False(failed.Success);
        Assert.StartsWith(ReShadeDeploymentService.RemovalFailedPrefix, failed.Message);
        Assert.Equal(iniBefore, File.ReadAllBytes(optiIni));
        var ledger = new LinuxDeploymentRecordStore(j.Ledger);
        Assert.NotNull(ledger.Find(game, ManagedComponentKind.ReShade));
        var optiRecord = ledger.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(FileHash.Sha256HexOf(iniBefore),
            optiRecord.Files.Single(file => Path.GetFileName(file.Path) == "OptiScaler.ini").Sha256, ignoreCase: true);

        var retry = await reshade.RemoveAsync(game);
        Assert.True(retry.Success, retry.Message);
        Assert.DoesNotContain("LoadReshade=true", File.ReadAllText(optiIni), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReShadeRemove_CrashAtTheCommitWrite_NextStartPutsTheProxyAndItsOverrideBack()
    {
        var (game, store) = CreateGame("journal-reshade-plain", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var reShadeBytes = File.ReadAllBytes(dxgi);
        var j = NewJournaled("journal-reshade-plain", store);
        var reshade = NewReShadeService(store, j);
        using var death = new SimulatedDeath(j.Journal);
        // The override put-back is journaled and done; the commit write that takes the record away dies.
        j.Overrides.Registry.InsideLockForTesting = _ => store.SaveFaultForTesting ??= death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => reshade.RemoveAsync(game));
        Assert.True(died);
        Assert.False(File.Exists(dxgi));
        Assert.Null(DxgiOverride(game));

        var report = await SimulatedDeath.RecoverAsync(j.Dir, j.Ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(dxgi));
        Assert.Equal("native,builtin", DxgiOverride(game));
        Assert.NotNull(new LinuxDeploymentRecordStore(j.Ledger).Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task ReShadeRemove_FailurePartWay_RollsBackKeepsTheRecordAndARetrySucceeds()
    {
        var (game, store) = CreateGame("journal-reshade-fail", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var reShadeBytes = File.ReadAllBytes(dxgi);
        var j = NewJournaled("journal-reshade-fail", store);
        var reshade = NewReShadeService(store, j);
        var saves = 0;
        store.SaveFaultForTesting = () => ++saves == 2 ? new IOException("disk full (test)") : null;

        var failed = await reshade.RemoveAsync(game);

        Assert.False(failed.Success);
        Assert.StartsWith(ReShadeDeploymentService.RemovalFailedPrefix, failed.Message);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(dxgi));
        Assert.Equal("native,builtin", DxgiOverride(game));
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));

        store.SaveFaultForTesting = null;
        var retry = await reshade.RemoveAsync(game);
        Assert.True(retry.Success, retry.Message);
        Assert.False(File.Exists(dxgi));
        Assert.Null(DxgiOverride(game));
    }
}
