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
        Assert.Equal(16, LinuxUpdateAllService.SupportedComponents.Count);
        Assert.Contains(ManagedComponentKind.NrCostScaler, LinuxUpdateAllService.SupportedComponents); // #199
        Assert.Contains(ManagedComponentKind.RtxEncore, LinuxUpdateAllService.SupportedComponents); // w4/encore (#158)
        Assert.Contains(ManagedComponentKind.ReShadeAddonManager, LinuxUpdateAllService.SupportedComponents); // #194
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
    public async Task ManualRun_RefreshesReShade_AndChecksEveryOtherFamilyFirst()
    {
        // #113: manual Update All used to reinstall every family but RenoDX and call each one "updated". Upstream
        // re-deploys only ReShade unconditionally; every other family updates only when its check finds one.
        using var fixture = new Fixture();
        var game = fixture.Game("manual");
        foreach (var component in LinuxUpdateAllService.SupportedComponents) fixture.Record(game, component);
        var backend = new FakeBackend();
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => ["file"], LinuxUpdateRunMode.Manual);

        Assert.Equal([ManagedComponentKind.ReShade], backend.UpdateCalls);
        Assert.Equal(LinuxUpdateAllService.SupportedComponents.Where(c => c != ManagedComponentKind.ReShade).Order(), backend.CheckCalls.Order());
        Assert.Equal((1, LinuxUpdateAllService.SupportedComponents.Count - 1, 0), (result.ChangedCount, result.CurrentCount, result.FailedCount));
    }

    [Theory]
    [InlineData(LinuxUpdateRunMode.Manual, ManagedComponentKind.ReShade)]
    [InlineData(LinuxUpdateRunMode.Manual, ManagedComponentKind.DofFix)]
    [InlineData(LinuxUpdateRunMode.Background, ManagedComponentKind.OptiScaler)]
    public async Task UpdateThatLeavesThePayloadAsItWas_IsCurrent_NotUpdated(LinuxUpdateRunMode mode, ManagedComponentKind component)
    {
        // A refresh (or an update a check asked for) that re-deploys the same files changes nothing for the user.
        using var fixture = new Fixture();
        var game = fixture.Game("same-payload");
        fixture.Record(game, component);
        var backend = new FakeBackend();
        backend.Checks[component] = new(true, true, "update");
        backend.Unchanged.Add(component);
        var service = fixture.Service(backend);

        var result = await service.RunAsync([game], _ => [], mode);

        Assert.Equal([component], backend.UpdateCalls);
        Assert.Equal((0, 1, 0), (result.ChangedCount, result.CurrentCount, result.FailedCount));
    }

    [Fact]
    public async Task ManualRun_CheckThatCouldNotRun_IsCouldNotBeChecked_NotCurrent()
    {
        // With every family checked first (#113), a failed check (offline, exe gone) must not read as "current".
        using var fixture = new Fixture();
        var game = fixture.Game("offline");
        fixture.Record(game, ManagedComponentKind.DofFix);
        fixture.Record(game, ManagedComponentKind.ReLimiter);
        using var http = new HttpClient(new OfflineHandler());
        var dofFix = new DofFixDeploymentService(http, fixture.Records, new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json"),
            Path.Combine(fixture.Root, "cache-dof"));
        var relimiter = new GitHubReShadeAddonDeploymentService(http, fixture.Records, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.ReLimiter, "ReLimiter", "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest",
            "relimiter.addon64", "relimiter.addon32", 100 * 1024, ManagedComponentKind.DisplayCommander), Path.Combine(fixture.Root, "cache-rl"));
        var backend = new ChecksOnly(new Dictionary<ManagedComponentKind, Func<Task<ManagedUpdateCheck>>>
        {
            [ManagedComponentKind.DofFix] = () => dofFix.CheckUpdateAsync(game),
            [ManagedComponentKind.ReLimiter] = () => relimiter.CheckUpdateAsync(game) // no game executable
        });

        var result = await fixture.Service(backend).RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal((0, 0, 2), (result.ChangedCount, result.CurrentCount, result.UncheckedCount));
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }

    private sealed class ChecksOnly(Dictionary<ManagedComponentKind, Func<Task<ManagedUpdateCheck>>> checks) : ILinuxManagedUpdateBackend
    {
        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => checks[component]();

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no update without an available update");
    }

    [Fact]
    public void PayloadFingerprint_IgnoresInstallTimeMetadataAndUserEditableFiles_ButNotPayloadHashes()
    {
        LinuxDeploymentRecord Record(string sha, string ini, DateTime at, string op) => new()
        {
            Version = "6.4.0", InstalledAtUtc = at, Metadata = { ["Deployment.OperationId"] = op },
            Files =
            [
                new LinuxDeployedFile { Path = "/g/dxgi.dll", Sha256 = sha },
                new LinuxDeployedFile { Path = "/g/ReShade.ini", Sha256 = ini, AllowUserChanges = true }
            ]
        };
        var first = LinuxDeploymentRecord.PayloadFingerprint(Record("aa", "i1", DateTime.UnixEpoch, "op1"));

        Assert.Equal(first, LinuxDeploymentRecord.PayloadFingerprint(Record("aa", "i2", DateTime.UtcNow, "op2")));
        Assert.NotEqual(first, LinuxDeploymentRecord.PayloadFingerprint(Record("bb", "i1", DateTime.UnixEpoch, "op1")));
        // The w2/trust toolkit stores upper-case hex; a record written before it holds lower case for the same bytes.
        Assert.Equal(first, LinuxDeploymentRecord.PayloadFingerprint(Record("AA", "i1", DateTime.UnixEpoch, "op1")));
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
        // Re-review nit: the summary says why it failed.
        Assert.True(Assert.Single(result.Items).Blocked);
        Assert.EndsWith(" Failed: blocked (RenoDX HDR) — updates blocked until Edit game confirms the game executable.",
            UiLocalizationService.UpdateAllSummaryFor("en-US", result), StringComparison.Ordinal);
        Assert.EndsWith(" Falha: blocked (RenoDX HDR) — atualizações bloqueadas até Editar jogo confirmar o executável do jogo.",
            UiLocalizationService.UpdateAllSummaryFor("pt-BR", result), StringComparison.Ordinal);
    }

    // Re-review nit (up-129): a failed item's own reason reaches the status line, with the count of further failures.
    [Fact]
    public void Summary_NamesTheFirstFailureAndItsReason()
    {
        var result = new LinuxUpdateAllResult([
            new("A", ManagedComponentKind.RenoDX, true, true, false, "updated"),
            new("Hades II", ManagedComponentKind.ReShade, false, false, false, "RenoDX update check failed: boom."),
            new("C", ManagedComponentKind.LumaFramework, false, false, false, "other"),
            new("D", ManagedComponentKind.RenoDX, true, false, true, "deferred")
        ]);

        Assert.Equal("Update All: 1 updated · 0 current · 1 deferred · 2 failed. Failed: Hades II (ReShade) — RenoDX update check failed: boom (+1 more).",
            UiLocalizationService.UpdateAllSummaryFor("en-US", result));
        Assert.Equal("Atualizar tudo: 1 atualizado(s) · 0 em dia · 1 adiado(s) · 2 com falha. Falha: Hades II (ReShade) — A verificação de atualização do RenoDX falhou: boom (+1 outra(s)).",
            UiLocalizationService.UpdateAllSummaryFor("pt-BR", result));
        var noFailures = new LinuxUpdateAllResult(result.Items.Where(i => i.Success).ToArray());
        Assert.Equal(noFailures.Summary, UiLocalizationService.UpdateAllSummaryFor("en-US", noFailures));
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

        Assert.StartsWith("Atualizar tudo: 1 atualizado(s) · 1 em dia · 1 adiado(s) · 1 com falha · 1 não verificado(s). Falha: D (RenoDX HDR) — ",
            UiLocalizationService.UpdateAllSummaryFor("pt-BR", result));
        Assert.StartsWith("Atualizar tudo: 1 atualizado(s) · 1 em dia · 1 adiado(s) · 1 com falha. Falha: D ",
            UiLocalizationService.UpdateAllSummaryFor("pt-BR", noUnchecked));
        foreach (var r in new[] { result, noUnchecked, empty })
            Assert.StartsWith(r.Summary, UiLocalizationService.UpdateAllSummaryFor("en-US", r));
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
        backend.Checks[ManagedComponentKind.DofFix] = new(true, true, "update");
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
        /// <summary>Set by the fixture: a successful update bumps the record's version, as a real payload change does.</summary>
        public LinuxDeploymentRecordStore? Records { get; set; }
        /// <summary>Families whose successful update re-deploys the same payload.</summary>
        public HashSet<ManagedComponentKind> Unchanged { get; } = [];
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
            var result = UpdateResults.TryGetValue(component, out var value) ? value : ComponentOperationResult.Ok("updated");
            if (result.Success && Records?.Find(game, component) is { } record && !Unchanged.Contains(component))
            {
                record.Version += "-new";
                Records.Upsert(record);
            }
            return result;
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
        {
            if (backend is FakeBackend fake) fake.Records = Records;
            return new(Records, backend, running ?? new FixedRunningProbe(false));
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
