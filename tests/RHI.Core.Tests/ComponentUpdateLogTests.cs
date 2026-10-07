using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ComponentUpdateLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-update-log-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DetectedGame _game;

    public ComponentUpdateLogTests()
    {
        Directory.CreateDirectory(_root);
        _game = new DetectedGame { Name = "Game", InstallPath = _root, BinaryDirectory = _root, SteamAppId = 42 };
    }

    private string LogPath => Path.Combine(_root, "update-log.json");

    private static ComponentUpdateLogEntry Entry(int minute, bool success = true, string? message = null) => new()
    {
        TimestampUtc = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc).AddMinutes(minute),
        GameName = "Game",
        GameKey = "steam:42",
        Component = ManagedComponentKind.RenoDX,
        ComponentName = "RenoDX HDR",
        Action = ComponentUpdateAction.Update,
        FromVersion = $"v{minute}",
        ToVersion = $"v{minute + 1}",
        Success = success,
        Message = message
    };

    [Fact]
    public void Store_PersistsNewestFirstAcrossInstances_AndIsBounded()
    {
        var store = new ComponentUpdateLogStore(LogPath);
        for (var i = 0; i < ComponentUpdateLogStore.MaxEntries + 5; i++)
            Assert.True(store.Record(Entry(i)));

        var reloaded = new ComponentUpdateLogStore(LogPath).GetAll();
        Assert.Equal(ComponentUpdateLogStore.MaxEntries, reloaded.Count);
        Assert.Equal($"v{ComponentUpdateLogStore.MaxEntries + 4}", reloaded[0].FromVersion);
        Assert.Equal(DateTimeKind.Utc, reloaded[0].TimestampUtc.Kind);
        Assert.False(File.Exists(LogPath + ".tmp"));
    }

    [Fact]
    public void CorruptLog_IsQuarantinedAndNotOverwritten_UntilCleared()
    {
        File.WriteAllText(LogPath, "[ not json");
        var store = new ComponentUpdateLogStore(LogPath);

        Assert.Empty(store.GetAll());
        var quarantine = store.CorruptLogPath;
        Assert.NotNull(quarantine);
        Assert.Equal("[ not json", File.ReadAllText(quarantine!));
        Assert.False(store.Record(Entry(1)));
        Assert.Equal("[ not json", File.ReadAllText(LogPath));

        store.Clear();
        Assert.Null(store.CorruptLogPath);
        Assert.True(store.Record(Entry(2)));
        Assert.Single(store.GetAll());
        Assert.True(File.Exists(quarantine));
    }

    [Fact]
    public void RepeatedIdenticalFailure_IsRecordedOnce()
    {
        var store = new ComponentUpdateLogStore(LogPath);
        Assert.True(store.Record(Entry(1, success: false, message: "offline")));
        Assert.False(store.Record(Entry(2, success: false, message: "offline")));
        Assert.True(store.Record(Entry(3, success: false, message: "hash mismatch")));
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void Grouping_SplitsByLocalDay_NewestFirst()
    {
        var today = new DateTime(2026, 10, 6);
        DateTime At(DateTime localDay, int hour) => DateTime.SpecifyKind(localDay.AddHours(hour), DateTimeKind.Local).ToUniversalTime();
        var entries = new[]
        {
            Entry(0) with { TimestampUtc = At(today.AddDays(-5), 9) },
            Entry(0) with { TimestampUtc = At(today, 8), ComponentName = "early" },
            Entry(0) with { TimestampUtc = At(today.AddDays(-1), 23) },
            Entry(0) with { TimestampUtc = At(today, 20), ComponentName = "late" }
        };

        var groups = ComponentUpdateLogStore.GroupByLocalDate(entries);

        Assert.Equal([today, today.AddDays(-1), today.AddDays(-5)], groups.Select(g => g.LocalDate));
        Assert.Equal(["late", "early"], groups[0].Entries.Select(e => e.ComponentName));
        Assert.Equal(
            [UpdateHistoryDay.Today, UpdateHistoryDay.Yesterday, UpdateHistoryDay.Earlier],
            groups.Select(g => ComponentUpdateHistoryPresenter.Classify(g.LocalDate, today)));
    }

    [Fact]
    public async Task UpdateAll_WithTwoUpdatedComponents_LogsTwoVersionChanges()
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        Upsert(records, ManagedComponentKind.ReShade, "6.3.0");
        Upsert(records, ManagedComponentKind.RenoDX, "0.40");
        var log = new ComponentUpdateLogStore(LogPath);
        var backend = new RecordingBackend(new ComponentUpdateRecorder(records, log), records);
        var updateAll = new LinuxUpdateAllService(records, backend, new NotRunning());

        var result = await updateAll.RunAsync([_game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal(2, result.ChangedCount);
        var entries = log.GetAll();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(ComponentUpdateAction.Update, e.Action));
        var renodx = Assert.Single(entries, e => e.Component == ManagedComponentKind.RenoDX);
        Assert.Equal(("0.40", "0.40-new", "Game"), (renodx.FromVersion, renodx.ToVersion, renodx.GameName));
        Assert.Equal("0.40  →  0.40-new", ComponentUpdateHistoryPresenter.VersionText(renodx));
    }

    [Fact]
    public async Task Recorder_LogsDependencyInstalls_Removals_AndFailures_ButNotSameVersionRefresh()
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        var log = new ComponentUpdateLogStore(LogPath);
        var recorder = new ComponentUpdateRecorder(records, log);

        await recorder.TrackAsync(_game, ManagedComponentKind.RenoDX, ComponentUpdateAction.Install, () =>
        {
            Upsert(records, ManagedComponentKind.ReShade, "6.3.0"); // auto-installed dependency
            Upsert(records, ManagedComponentKind.RenoDX, "0.40");
            return Task.FromResult(ComponentOperationResult.Ok("ok"));
        });
        Assert.Equal([ManagedComponentKind.RenoDX, ManagedComponentKind.ReShade], log.GetAll().Select(e => e.Component));
        Assert.All(log.GetAll(), e => Assert.Equal(ComponentUpdateAction.Install, e.Action));

        await recorder.TrackAsync(_game, ManagedComponentKind.ReShade, ComponentUpdateAction.Update, () =>
        {
            Upsert(records, ManagedComponentKind.ReShade, "6.3.0");
            return Task.FromResult(ComponentOperationResult.Ok("already current"));
        });
        Assert.Equal(2, log.GetAll().Count);

        await recorder.TrackAsync(_game, ManagedComponentKind.RenoDX, ComponentUpdateAction.Remove, () =>
        {
            records.Remove(_game, ManagedComponentKind.RenoDX);
            return Task.FromResult(ComponentOperationResult.Ok("removed"));
        });
        var removed = log.GetAll()[0];
        Assert.Equal((ComponentUpdateAction.Remove, "0.40", (string?)null), (removed.Action, removed.FromVersion, removed.ToVersion));

        await Assert.ThrowsAsync<IOException>(() => recorder.TrackAsync(_game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Install,
            () => Task.FromException<ComponentOperationResult>(new IOException("disk full"))));
        var failed = log.GetAll()[0];
        Assert.Equal((false, ManagedComponentKind.OptiScaler, "disk full"), (failed.Success, failed.Component, failed.Message));
    }

    // Review up-129 #9: a RenoDX version read that throws must not empty the snapshot, which would
    // drop the real change and log every other component as installed or removed.
    [Fact]
    public async Task Recorder_RenoDxVersionReadFailure_FallsBackPerRecord()
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        Upsert(records, ManagedComponentKind.ReShade, "6.3.0");
        Upsert(records, ManagedComponentKind.RenoDX, "Generic Unity");
        var log = new ComponentUpdateLogStore(LogPath);
        var recorder = new ComponentUpdateRecorder(records, log, null, _ => throw new IOException("unreadable add-on"));

        var result = await recorder.TrackAsync(_game, ManagedComponentKind.ReShade, ComponentUpdateAction.Update, () =>
        {
            Upsert(records, ManagedComponentKind.ReShade, "6.4.0");
            return Task.FromResult(ComponentOperationResult.Ok("updated"));
        });

        Assert.True(result.Success);
        var entry = Assert.Single(log.GetAll());
        Assert.Equal((ManagedComponentKind.ReShade, ComponentUpdateAction.Update, "6.3.0", "6.4.0"),
            (entry.Component, entry.Action, entry.FromVersion, entry.ToVersion));
    }

    private void Upsert(LinuxDeploymentRecordStore records, ManagedComponentKind kind, string version)
        => records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(_game),
            GameName = _game.Name,
            Component = kind,
            BinaryDirectory = _root,
            Version = version,
            InstalledAtUtc = DateTime.UtcNow
        });

    // Mirrors how LinuxManagedComponentService wraps its update dispatch.
    private sealed class RecordingBackend(ComponentUpdateRecorder recorder, LinuxDeploymentRecordStore records) : ILinuxManagedUpdateBackend
    {
        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagedUpdateCheck(true, true, "update"));

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => recorder.TrackAsync(game, component, ComponentUpdateAction.Update, () =>
            {
                var record = records.Find(game, component)!;
                record.Version += "-new"; // services mutate the cached record in place before upserting
                records.Upsert(record);
                return Task.FromResult(ComponentOperationResult.Ok("updated"));
            });
    }

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
