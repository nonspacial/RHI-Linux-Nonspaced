using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#23 (DEP-03): start-up recovery of interrupted deployments and the leftover sweep.</summary>
public sealed class DeploymentRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-recovery-tests-" + Guid.NewGuid().ToString("N"));

    public DeploymentRecoveryTests() => Directory.CreateDirectory(_root);

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public async Task FileServiceCrashMidBatch_IsRolledBackByTheNextStart()
    {
        Directory.CreateDirectory(P("game"));
        File.WriteAllText(P("game", "nvngx_dlss.dll"), "game dlss");
        File.WriteAllText(P("game", "sl.common.dll"), "managed sl");
        File.WriteAllText(P("game", "sl.common.dll.original"), "game sl");
        File.WriteAllText(P("new.dll"), "new build");
        var journal = new DeploymentJournal(P("journal")) { CrashAtCommitForTesting = true };

        await Assert.ThrowsAsync<SimulatedCrashException>(() => new TransactionalFileDeploymentService(journal).ReplaceBatchAsync(new[]
        {
            (P("new.dll"), P("game", "nvngx_dlss.dll")),
            (P("new.dll"), P("game", "sl.common.dll"))
        }));
        Assert.Equal("new build", File.ReadAllText(P("game", "nvngx_dlss.dll")));
        Assert.Equal("new build", File.ReadAllText(P("game", "sl.common.dll")));

        var report = await new DeploymentJournal(P("journal")).RecoverAsync();

        Assert.Equal(1, report.RolledBack);
        Assert.Equal("game dlss", File.ReadAllText(P("game", "nvngx_dlss.dll")));
        Assert.False(File.Exists(P("game", "nvngx_dlss.dll.original")));
        Assert.Equal("managed sl", File.ReadAllText(P("game", "sl.common.dll")));
        Assert.Equal("game sl", File.ReadAllText(P("game", "sl.common.dll.original")));
        Assert.DoesNotContain(Directory.GetFiles(P("game")), path => path.Contains(".rhi-", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(P("journal")));
    }

    [Fact]
    public async Task FileChangedByAGameUpdateAfterTheCrash_IsLeftAlone_AndRhisCopySetAside()
    {
        Directory.CreateDirectory(P("game"));
        File.WriteAllText(P("game", "nvngx_dlss.dll"), "game dlss");
        File.WriteAllText(P("new.dll"), "new build");
        var journal = new DeploymentJournal(P("journal")) { CrashAtCommitForTesting = true };
        await Assert.ThrowsAsync<SimulatedCrashException>(() => new TransactionalFileDeploymentService(journal)
            .ReplaceAsync(P("new.dll"), P("game", "nvngx_dlss.dll")));
        File.WriteAllText(P("game", "nvngx_dlss.dll"), "patched by the store"); // e.g. Steam update before RHI restarts

        var report = await new DeploymentJournal(P("journal")).RecoverAsync();

        // Final, not a failure retried at every start: the store's file stays, and the .original this
        // operation created (the only copy of the old game file) is moved aside, not deleted.
        var entry = Assert.Single(report.Entries);
        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, entry.Outcome);
        Assert.Equal(0, report.Failed);
        Assert.Equal("patched by the store", File.ReadAllText(P("game", "nvngx_dlss.dll")));
        Assert.False(File.Exists(P("game", "nvngx_dlss.dll.original")));
        var setAside = Assert.Single(Directory.GetFiles(P("game", RecoveredCopies.FolderName), "*", SearchOption.AllDirectories));
        Assert.Equal("nvngx_dlss.dll" + RecoveredCopies.PreOperationSuffix, Path.GetFileName(setAside));
        Assert.Equal("game dlss", File.ReadAllText(setAside));
        Assert.Empty(Directory.GetFiles(P("journal")));
    }

    [Fact]
    public async Task UnreadableLedger_DefersRecovery_AndTouchesNothing()
    {
        Directory.CreateDirectory(P("game"));
        File.WriteAllText(P("game", "dxgi.dll"), "payload");
        var journal = new DeploymentJournal(P("journal"));
        var operation = await journal.BeginAsync("x", "record|ReShade|steam:1");
        await operation.TrackFileAsync(P("game", "dxgi.dll"));
        File.WriteAllText(P("game", "dxgi.dll"), "changed");
        operation.AbandonAsIfCrashed();
        File.WriteAllText(P("records.json"), "{ not json");
        var records = new LinuxDeploymentRecordStore(P("records.json"));

        Assert.Null(await LinuxDeploymentRecovery.RecoverAsync(journal, records));
        Assert.Equal("changed", File.ReadAllText(P("game", "dxgi.dll")));
        Assert.Contains(operation.Id, journal.ReferencedOperationIds());
        Assert.NotEmpty(LinuxDeploymentRecovery.Sweep(journal, records).Problems);
    }

    [Fact]
    public void Sweep_CoversRecordedDirectoriesOnly()
    {
        Directory.CreateDirectory(P("game", "bin"));
        Directory.CreateDirectory(P("elsewhere"));
        File.WriteAllText(P("game", "bin", "dxgi.dll"), "managed");
        var records = new LinuxDeploymentRecordStore(P("records.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:1", Component = ManagedComponentKind.ReShade, BinaryDirectory = P("game", "bin"),
            Files = [new LinuxDeployedFile { Path = P("game", "bin", "dxgi.dll") }]
        });
        var leftoverName = "dxgi.dll.rhi-tmp-" + Guid.NewGuid().ToString("N");
        foreach (var dir in new[] { P("game", "bin"), P("elsewhere") })
            File.WriteAllText(Path.Combine(dir, leftoverName), "partial");

        var report = LinuxDeploymentRecovery.Sweep(new DeploymentJournal(P("journal")), records, nowUtc: DateTime.UtcNow.AddDays(1));

        Assert.Equal([P("game", "bin", leftoverName)], report.Deleted);
        Assert.True(File.Exists(P("elsewhere", leftoverName)));
    }

    [Fact]
    public void WineOverrideStep_IsSafeWhenTheOverrideWasNeverWritten()
    {
        Directory.CreateDirectory(P("pfx"));
        var userReg = P("pfx", "user.reg");
        const string registry = "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n";
        File.WriteAllText(userReg, registry);
        var overrides = new WineDllOverrideService();
        var step = WineOverrideCompensator.Describe(P("pfx"), overrides.PlanNativeBuiltin(P("pfx"), "dxgi", "Game.exe"));
        var compensator = new WineOverrideCompensator(overrides, isPrefixInUse: _ => false);

        Assert.Equal(CompensationStatus.Done, compensator.Compensate(step).Status); // crash came before the override was written
        Assert.Equal(registry, File.ReadAllText(userReg));

        overrides.EnsureNativeBuiltin(P("pfx"), "dxgi", "Game.exe");
        Assert.Equal(CompensationStatus.Done, compensator.Compensate(step).Status);
        Assert.Equal(CompensationStatus.Done, compensator.Compensate(step).Status); // a retried recovery runs it again
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.Contains("\"x\"=\"y\"", File.ReadAllText(userReg));
    }

    [Fact]
    public async Task CommittedHeroicInstall_ReKeyedAfterTheCrash_IsNotRolledBack()
    {
        // Integration w1 (core x stores): the commit probe embeds the key the game had when the install began
        // (an older build's install-path key for Heroic); the scan-time MigrateGameKeys then re-keys the record to
        // heroic:<app>. Recovery matches the operation id alone, so the committed install stays.
        Directory.CreateDirectory(P("game"));
        File.WriteAllText(P("game", "dxgi.dll"), "game file");
        var game = new DetectedGame { Name = "Hades", Source = "Heroic", HeroicAppName = "Min", InstallPath = P("game"), BinaryDirectory = P("game") };
        var journal = new DeploymentJournal(P("journal"));
        var operation = await journal.BeginAsync("ReShade install: Hades", $"record|ReShade|{P("game")}");
        await operation.TrackFileAsync(P("game", "dxgi.dll"));
        File.WriteAllText(P("game", "dxgi.dll"), "reshade");
        await operation.RecordWrittenContentAsync(P("game", "dxgi.dll"));
        var records = new LinuxDeploymentRecordStore(P("records.json"));
        var record = new LinuxDeploymentRecord
        {
            GameKey = P("game"), Component = ManagedComponentKind.ReShade, BinaryDirectory = P("game"),
            Files = [new LinuxDeployedFile { Path = P("game", "dxgi.dll") }]
        };
        record.Metadata[LinuxDeploymentRecovery.OperationIdMetadataKey] = operation.Id;
        records.Upsert(record);
        operation.AbandonAsIfCrashed();

        Assert.Equal(1, records.MigrateGameKeys([game]));
        Assert.Equal("heroic:Min", Assert.Single(records.All()).GameKey);
        var report = await LinuxDeploymentRecovery.RecoverAsync(new DeploymentJournal(P("journal")), records);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.Equal("reshade", File.ReadAllText(P("game", "dxgi.dll")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
