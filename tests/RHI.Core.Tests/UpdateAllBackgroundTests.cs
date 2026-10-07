using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

public sealed class UpdateAllBackgroundTests
{
    [Fact]
    public void SupportedComponents_MirrorsLinuxNativeFamilies()
    {
        Assert.Equal(13, LinuxUpdateAllService.SupportedComponents.Count);
        Assert.Contains(ManagedComponentKind.ReShade, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.RenoDX, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.ReLimiter, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.DisplayCommander, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.OptiScaler, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.REFramework, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.LumaFramework, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.DofFix, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.DlssEnablerStandalone, LinuxUpdateAllService.SupportedComponents);
        Assert.Contains(ManagedComponentKind.Rtx40MfgStandalone, LinuxUpdateAllService.SupportedComponents);
    }

    [Fact]
    public void SupportedComponents_ExcludesAuxiliaryFamilies()
    {
        Assert.False(LinuxUpdateAllService.IsSupported(ManagedComponentKind.Dlss));
        Assert.False(LinuxUpdateAllService.IsSupported(ManagedComponentKind.Streamline));
        Assert.False(LinuxUpdateAllService.IsSupported(ManagedComponentKind.UltimateAsiLoader));
        Assert.False(LinuxUpdateAllService.IsSupported(ManagedComponentKind.RenoDXUpgrade));
        Assert.False(LinuxUpdateAllService.IsSupported(ManagedComponentKind.Unknown));
    }

    [Fact]
    public async Task ManualRun_UpdatesEverySupportedManagedRecordWithoutPrecheck()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("manual");
        fixture.Record(game, ManagedComponentKind.ReShade);
        fixture.Record(game, ManagedComponentKind.DofFix);
        var backend = new FakeBackend();
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => ["file"], LinuxUpdateRunMode.Manual);

        Assert.Equal(2, backend.UpdateCalls.Count);
        Assert.Empty(backend.CheckCalls);
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task ManualRun_ChecksRenoDxFirst_UpdatesOnlyWhenFlagged()
    {
        using var fixture = new Fixture();
        var current = fixture.Game("renodx-current");
        var rebuilt = fixture.Game("renodx-rebuilt");
        var offline = fixture.Game("renodx-offline");
        foreach (var game in new[] { current, rebuilt, offline }) fixture.Record(game, ManagedComponentKind.RenoDX);
        var backend = new FakeBackend();
        backend.GameChecks[rebuilt.Name] = new(true, true, "rebuilt");
        backend.GameChecks[offline.Name] = new(true, false, "offline", CouldNotCheck: true);
        var service = fixture.Service(backend);

        var result = await service.RunAsync([current, rebuilt, offline], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal(3, backend.CheckCalls.Count);
        Assert.Equal([ManagedComponentKind.RenoDX], backend.UpdateCalls);
        Assert.Equal((1, 1, 1), (result.ChangedCount, result.CurrentCount, result.UncheckedCount));
        Assert.EndsWith("· 1 could not be checked.", result.Summary, StringComparison.Ordinal);
    }

    // Review up-129 #2: a blocked check (no confirmed patch target) is a failure in both modes.
    [Theory]
    [InlineData(LinuxUpdateRunMode.Manual)]
    [InlineData(LinuxUpdateRunMode.Background)]
    public async Task BlockedCheck_IsCountedFailedAndNeverUpdated(LinuxUpdateRunMode mode)
    {
        using var fixture = new Fixture();
        var game = fixture.Game("blocked");
        fixture.Record(game, ManagedComponentKind.RenoDX);
        var backend = new FakeBackend();
        backend.GameChecks[game.Name] = new(true, false, "blocked", Blocked: true);
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => [], mode);

        Assert.Empty(backend.UpdateCalls);
        Assert.Equal((1, 0, 0), (result.FailedCount, result.CurrentCount, result.UncheckedCount));
    }

    // Review up-129 #4: the Update All status line is built from counts in the UI language.
    [Fact]
    public void Summary_IsLocalisedFromCounts_EnglishMatchesPlatformSummary()
    {
        var result = new LinuxUpdateAllResult([
            new("A", ManagedComponentKind.RenoDX, true, true, false, "updated"),
            new("B", ManagedComponentKind.RenoDX, true, false, false, "current"),
            new("C", ManagedComponentKind.RenoDX, true, false, true, "deferred"),
            new("D", ManagedComponentKind.RenoDX, false, false, false, "failed"),
            new("E", ManagedComponentKind.RenoDX, true, false, false, "offline", Unchecked: true)
        ]);
        var noUnchecked = new LinuxUpdateAllResult(result.Items.Take(4).ToArray());
        var empty = new LinuxUpdateAllResult([]);

        Assert.Equal("Atualizar tudo: 1 atualizado(s) · 1 em dia · 1 adiado(s) · 1 com falha · 1 não verificado(s).",
            UiLocalizationService.UpdateAllSummaryFor("pt-BR", result));
        Assert.Equal("Atualizar tudo: 1 atualizado(s) · 1 em dia · 1 adiado(s) · 1 com falha.",
            UiLocalizationService.UpdateAllSummaryFor("pt-BR", noUnchecked));
        foreach (var r in new[] { result, noUnchecked, empty })
            Assert.Equal(r.Summary, UiLocalizationService.UpdateAllSummaryFor("en-US", r));
        Assert.NotEqual(empty.Summary, UiLocalizationService.UpdateAllSummaryFor("pt-BR", empty));
        Assert.True(UiLocalizationService.HasKeyParity);
    }

    [Fact]
    public async Task BackgroundRun_ChecksAndSkipsCurrentComponent()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("current");
        fixture.Record(game, ManagedComponentKind.ReShade);
        var backend = new FakeBackend();
        backend.Checks[ManagedComponentKind.ReShade] = new(true, false, "current", "1", "1");
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Single(backend.CheckCalls);
        Assert.Empty(backend.UpdateCalls);
        Assert.Equal(1, result.CurrentCount);
        Assert.Equal(0, result.ChangedCount);
    }

    [Fact]
    public async Task BackgroundRun_UpdatesOnlyWhenCheckReportsAvailable()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("available");
        fixture.Record(game, ManagedComponentKind.DofFix);
        var backend = new FakeBackend();
        backend.Checks[ManagedComponentKind.DofFix] = new(true, true, "update", "1", "2");
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Single(backend.CheckCalls);
        Assert.Single(backend.UpdateCalls);
        Assert.Equal(1, result.ChangedCount);
    }

    [Fact]
    public async Task RunningGame_DefersEveryManagedTargetWithoutNetworkBackendCalls()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("running");
        fixture.Record(game, ManagedComponentKind.RenoDX);
        fixture.Record(game, ManagedComponentKind.ReLimiter);
        var backend = new FakeBackend();
        var service = fixture.Service(backend, new FixedRunningProbe(true));

        var result = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Equal(2, result.DeferredCount);
        Assert.Empty(backend.CheckCalls);
        Assert.Empty(backend.UpdateCalls);
    }

    [Fact]
    public async Task ComponentFailure_IsolatedAndLaterComponentsStillRun()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("isolation");
        fixture.Record(game, ManagedComponentKind.ReShade);
        fixture.Record(game, ManagedComponentKind.DofFix);
        var backend = new FakeBackend();
        backend.UpdateResults[ManagedComponentKind.ReShade] = ComponentOperationResult.Fail("boom");
        backend.UpdateResults[ManagedComponentKind.DofFix] = ComponentOperationResult.Ok("ok");
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal(2, backend.UpdateCalls.Count);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.ChangedCount);
    }

    [Fact]
    public async Task UnsupportedManagedRecords_AreIgnoredByUpdateAll()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("unsupported");
        fixture.Record(game, ManagedComponentKind.Dlss);
        fixture.Record(game, ManagedComponentKind.UltimateAsiLoader);
        var backend = new FakeBackend();
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Empty(result.Items);
        Assert.Empty(backend.UpdateCalls);
        Assert.False(service.HasManagedTargets([game]));
    }

    [Fact]
    public async Task IndexedFiles_ArePassedToRenoDxInsteadOfEmptySharedProbeState()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("renodx-index");
        fixture.Record(game, ManagedComponentKind.ReShade);
        fixture.Record(game, ManagedComponentKind.RenoDX);
        var backend = new FakeBackend();
        var service = fixture.Service(backend);
        var expected = new[] { "/game/a.exe", "/game/special.pak" };

        await service.RunAsync([game], _ => expected, LinuxUpdateRunMode.Manual);

        Assert.Equal(expected, backend.LastIndexedFiles[ManagedComponentKind.RenoDX]);
    }

    [Fact]
    public async Task ConcurrentPass_IsDeferredByCoordinatorGuard()
    {
        using var fixture = new Fixture();
        var game = fixture.Game("concurrent");
        fixture.Record(game, ManagedComponentKind.ReShade);
        var backend = new FakeBackend { BlockUpdates = true };
        var service = fixture.Service(backend);

        var first = service.RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);
        await backend.UpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);
        backend.ReleaseUpdates.SetResult(true);
        await first;

        Assert.Equal(1, second.DeferredCount);
        Assert.Contains("already running", second.Items[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_DefaultBackgroundUpdateIsOffWithFourHourCadence()
    {
        var settings = new LinuxAppSettings();
        Assert.False(settings.AutoUpdateComponents);
        Assert.Equal(4, settings.UpdateCheckIntervalHours);
    }

    [Fact]
    public void Settings_NormalizeClampsInvalidBackgroundCadenceToFourHours()
    {
        Assert.Equal(4, new LinuxAppSettings { UpdateCheckIntervalHours = 0 }.Normalize().UpdateCheckIntervalHours);
        Assert.Equal(4, new LinuxAppSettings { UpdateCheckIntervalHours = 25 }.Normalize().UpdateCheckIntervalHours);
        Assert.Equal(8, new LinuxAppSettings { UpdateCheckIntervalHours = 8 }.Normalize().UpdateCheckIntervalHours);
    }

    [Fact]
    public void SettingsStore_RoundTripsBackgroundUpdateOptIn()
    {
        using var fixture = new Fixture();
        var settingsPath = Path.Combine(fixture.Root, "settings", "settings.json");
        var store = new LinuxSettingsStore(settingsPath, Path.Combine(fixture.Root, "cache"), Path.Combine(fixture.Root, "data"));
        store.Save(new LinuxAppSettings { AutoUpdateComponents = true, UpdateCheckIntervalHours = 4 });
        var loaded = store.Load();
        Assert.True(loaded.AutoUpdateComponents);
        Assert.Equal(4, loaded.UpdateCheckIntervalHours);
    }

    private sealed class FakeBackend : ILinuxManagedUpdateBackend
    {
        public Dictionary<ManagedComponentKind, ManagedUpdateCheck> Checks { get; } = new();
        public Dictionary<string, ManagedUpdateCheck> GameChecks { get; } = new();
        public Dictionary<ManagedComponentKind, ComponentOperationResult> UpdateResults { get; } = new();
        public List<ManagedComponentKind> CheckCalls { get; } = [];
        public List<ManagedComponentKind> UpdateCalls { get; } = [];
        public Dictionary<ManagedComponentKind, IReadOnlyList<string>> LastIndexedFiles { get; } = new();
        public bool BlockUpdates { get; set; }
        public TaskCompletionSource<bool> UpdateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseUpdates { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            CheckCalls.Add(component);
            LastIndexedFiles[component] = indexedFiles;
            return Task.FromResult(GameChecks.TryGetValue(game.Name, out var byGame) ? byGame
                : Checks.TryGetValue(component, out var value) ? value : new ManagedUpdateCheck(true, false, "current"));
        }

        public async Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            UpdateCalls.Add(component);
            LastIndexedFiles[component] = indexedFiles;
            if (BlockUpdates)
            {
                UpdateEntered.TrySetResult(true);
                await ReleaseUpdates.Task.WaitAsync(cancellationToken);
            }
            return UpdateResults.TryGetValue(component, out var value) ? value : ComponentOperationResult.Ok("updated");
        }
    }

    private sealed class FixedRunningProbe(bool value) : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => value;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rhi-update-all-" + Guid.NewGuid().ToString("N"));
        public LinuxDeploymentRecordStore Records { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Records = new LinuxDeploymentRecordStore(Path.Combine(Root, "deployments.json"));
        }

        public DetectedGame Game(string name)
        {
            var install = Path.Combine(Root, name);
            Directory.CreateDirectory(install);
            return new DetectedGame { Name = name, InstallPath = install, BinaryDirectory = install, Source = "Test", Runner = "Wine" };
        }

        public void Record(DetectedGame game, ManagedComponentKind component)
            => Records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = component,
                BinaryDirectory = game.BinaryDirectory!,
                Version = "1"
            });

        public LinuxUpdateAllService Service(ILinuxManagedUpdateBackend backend, IGameRunningProbe? running = null)
            => new(Records, backend, running ?? new FixedRunningProbe(false));

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
