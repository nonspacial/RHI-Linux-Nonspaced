using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Unit 1 of the view-model split (epic #89): <see cref="SelectedGameSession"/> analyses the selected game on a working
/// copy (#82), getters read its snapshot instead of the disk (#71), bulk operations always re-analyse the selected game
/// (#72), file-changing operations rebuild its file index (#83) and the cache is bounded (#74, #60).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SelectedGameSessionTests
{
    // UI-15: the probe writes a copy off the UI thread; the shared instance changes only when the result is applied.
    [Fact]
    public async Task Probe_WritesAWorkingCopy_AndTheSharedGameChangesOnlyWhenTheResultIsApplied()
    {
        var probe = new GatedProbe();
        using var fx = await ViewModelFixture.StartAsync([], probe: probe);
        var add = AddAsync(fx, "Tunic");
        var probed = await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var shared = fx.Vm.SelectedGame!;
        Assert.NotSame(shared, probed);
        Assert.Equal(GraphicsApi.DirectX11, probed.GraphicsApi);   // the probe has written its copy…
        Assert.Equal(GraphicsApi.Unknown, shared.GraphicsApi);     // …and the UI's instance is untouched
        Assert.True(fx.Vm.IsSelectionProbeBusy);

        probe.Release.SetResult();
        await add;
        await WaitForAnalysisAsync(fx);
        Assert.Equal(GraphicsApi.DirectX11, shared.GraphicsApi);
        Assert.Equal("probe evidence", shared.GraphicsApiEvidence);
        Assert.StartsWith("DirectX 11", fx.Vm.Runtime.SelectedApi);
    }

    // UI-03: after the analysis, the panels read its snapshot; a file changing on disk shows only after a re-analysis.
    [Fact]
    public async Task Getters_ReadTheAnalysisSnapshot_NotTheDisk()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Dlss Game");
        var dll = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        File.WriteAllBytes(dll, [0x4D, 0x5A]);
        File.WriteAllBytes(dll + TransactionalFileDeploymentService.BackupExtension, [0x4D, 0x5A]);
        await AddAsync(fx, game);
        await WaitForAnalysisAsync(fx);
        Assert.True(fx.Vm.Dlss.DlssSrRestoreAvailable);
        var state = fx.Vm.Dlss.DlssSrDeploymentState;

        File.Delete(dll + TransactionalFileDeploymentService.BackupExtension);
        Assert.True(fx.Vm.Dlss.DlssSrRestoreAvailable);          // no disk read in the getter
        Assert.Equal(state, fx.Vm.Dlss.DlssSrDeploymentState);

        await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!);
        Assert.False(fx.Vm.Dlss.DlssSrRestoreAvailable);
    }

    // UI-04: a bulk operation that drops the cache re-analyses the selected game even when nothing changed.
    [Theory]
    [InlineData("apply")]
    [InlineData("restore")]
    public async Task DlssBatch_WithNothingChanged_LeavesTheSelectedGameAnalysed(string mode)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        await AddAsync(fx, game);
        await WaitForAnalysisAsync(fx);
        var selected = fx.Vm.SelectedGame!;

        var result = mode == "apply"
            ? await fx.Vm.Dlss.ApplyDlssBatchAsync([selected], new LinuxDlssVersionDefaults("310.1.0", "", "", "", ""))
            : await fx.Vm.Dlss.RestoreDlssBatchAsync([selected]);
        Assert.Equal(0, result.ChangedCount);
        await WaitForAnalysisAsync(fx);

        // Analysed again, not left on the analysis from before the batch with an empty cache behind it.
        Assert.True(fx.Vm.Session.TryGetCached(selected, out var again));
        Assert.Same(again, fx.Vm.Session.Analysis);
        Assert.NotEmpty(fx.Vm.Components);
        await fx.Vm.ManageComponentCommand.ExecuteAsync(fx.Vm.Components[0].Kind);
        Assert.DoesNotContain("Wait for game analysis", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    // UI-16: a DLSS restore rebuilds the selected game's file index, and its own result line stays on screen.
    [Fact]
    public async Task DlssRestore_RebuildsTheFileIndex_AndKeepsItsResultLine()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Dlss Game");
        var dll = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        var backup = dll + TransactionalFileDeploymentService.BackupExtension;
        File.WriteAllBytes(dll, [0x4D, 0x5A, 0x01]);
        File.WriteAllBytes(backup, [0x4D, 0x5A, 0x02]);
        await AddAsync(fx, game);
        await WaitForAnalysisAsync(fx);
        var selected = fx.Vm.SelectedGame!;
        Assert.Contains(backup, fx.Vm.Session.CachedFiles(selected)!);

        await fx.Vm.Dlss.RestoreDlssComponentCommand.ExecuteAsync("SR");

        Assert.False(File.Exists(backup));
        Assert.DoesNotContain(backup, fx.Vm.Session.CachedFiles(selected)!);
        Assert.False(fx.Vm.Dlss.DlssSrRestoreAvailable);
        Assert.StartsWith("Super resolution: original DLL restored.", fx.Vm.StatusText);
    }

    // UI-06 / DET-21: browsing more games than the cache holds keeps only the most recent analyses.
    [Fact]
    public async Task BrowsingManyGames_KeepsAtMostTheCacheCapacity()
    {
        var games = new List<DetectedGame>();
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rhi-lru-{Guid.NewGuid():N}")).FullName;
        try
        {
            for (var i = 0; i < SelectedGameSession.DefaultCapacity + 4; i++)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, $"Game {i:00}")).FullName;
                File.WriteAllBytes(Path.Combine(dir, "game.exe"), [0x4D, 0x5A]);
                games.Add(new DetectedGame { Name = $"Game {i:00}", Source = "Heroic", InstallPath = dir, Runner = "Proton" });
            }
            using var fx = await ViewModelFixture.StartAsync(games);
            foreach (var row in fx.Vm.Games.ToArray())
            {
                fx.Vm.SelectedGame = row.Game;
                await WaitForAnalysisAsync(fx);
            }

            Assert.Equal(SelectedGameSession.DefaultCapacity, fx.Vm.Session.CachedCount);
            Assert.True(fx.Vm.Session.TryGetCached(fx.Vm.SelectedGame!, out _)); // the selected game is the newest
            Assert.False(fx.Vm.Session.TryGetCached(fx.Vm.Games[0].Game, out _));    // the first visited was dropped
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // UI-06 verification: 60 games of 3,000 paths each; only the cache's capacity worth of file indexes stays reachable.
    [Fact]
    public void Cache_RetainsOnlyItsCapacityOfFileIndexes()
    {
        var cache = new LruCache<string, IReadOnlyList<string>>(SelectedGameSession.DefaultCapacity);
        var weak = new List<WeakReference>();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        Fill(cache, weak);
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;

        Assert.Equal(SelectedGameSession.DefaultCapacity, weak.Count(reference => reference.IsAlive));
        Assert.Equal(SelectedGameSession.DefaultCapacity, cache.Count);
        // Measured 6.2 MiB for the 8 kept (≈270 bytes per path); the unbounded cache kept all 60, ~48 MiB.
        Assert.True(retained < 16L * 1024 * 1024, $"retained {retained / 1024} KiB");
        GC.KeepAlive(cache);
    }

    [Fact]
    public void LruCache_ReadingAnEntryMakesItTheNewest()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet("a", out _));
        cache.Set("c", 3);
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.Remove("a"));
        Assert.Equal(1, cache.Count);
    }

    // The probe-owned snapshot must cover every field the probe pipeline writes; a new DetectedGame property has to be
    // classified here, or a probed value would silently stay on the working copy.
    [Fact]
    public void RuntimeFacts_CoverEveryProbeWrittenField_AndRoundTrip()
    {
        string[] notProbeOwned =
        [
            nameof(DetectedGame.Name), nameof(DetectedGame.InstallPath), nameof(DetectedGame.Source), nameof(DetectedGame.IsManuallyAdded),
            nameof(DetectedGame.IsFavourite), nameof(DetectedGame.IsHidden), nameof(DetectedGame.GameRootNotAllowed), nameof(DetectedGame.FolderMissing),
            nameof(DetectedGame.PatchTargetFolderMissing), // set by identity resolution from the saved edit (w2/stores #147), not the probe
            nameof(DetectedGame.SteamAppId), nameof(DetectedGame.ManifestSteamAppIdOverride), nameof(DetectedGame.EpicCatalogNamespace),
            nameof(DetectedGame.EpicAppName), nameof(DetectedGame.HeroicAppName), nameof(DetectedGame.XboxAumid), nameof(DetectedGame.PrefixPath),
            nameof(DetectedGame.LibraryIdentityPath), nameof(DetectedGame.MergedAliases), nameof(DetectedGame.LibraryProvider),
            nameof(DetectedGame.LaunchExecutablePath), nameof(DetectedGame.LaunchProvider), nameof(DetectedGame.RequiresExplicitPatchTarget),
            nameof(DetectedGame.PatchTargetAuthority), nameof(DetectedGame.Runner), nameof(DetectedGame.LaunchCommand),
            nameof(DetectedGame.UserLaunchCommand), nameof(DetectedGame.DetectedLaunch), // identity resolution from the saved edit (Edit game command)
            nameof(DetectedGame.ParentLauncherName), nameof(DetectedGame.CompatibilityTool), nameof(DetectedGame.CompatibilityLayerType),
            nameof(DetectedGame.WineBinaryPath), nameof(DetectedGame.WineServerPath), nameof(DetectedGame.UserGraphicsApiOverride),
            nameof(DetectedGame.WikiNameOverride), nameof(DetectedGame.ManifestInstallRoot), nameof(DetectedGame.IsManifestSplitChild),
            nameof(DetectedGame.WikiUnlinkedByManifest), nameof(DetectedGame.DlssScanSkippedByManifest), nameof(DetectedGame.UeExtendedByManifest),
            nameof(DetectedGame.NativeHdrByManifest), nameof(DetectedGame.UeExtendedBlockedByManifest), nameof(DetectedGame.UeExtendedHdrByManifest),
            nameof(DetectedGame.UeExtendedLutByManifest), nameof(DetectedGame.ReShadeDllNameOverride), nameof(DetectedGame.DisplayCommanderDllNameOverride),
            nameof(DetectedGame.OptiScalerDllNameOverride), nameof(DetectedGame.PdUpscalerArtifact), nameof(DetectedGame.ManifestLaunchExecutable),
            nameof(DetectedGame.ExternalModUrl), nameof(DetectedGame.ExternalModLabel), nameof(DetectedGame.NexusUrlOverride),
            nameof(DetectedGame.PcgwUrlOverride), nameof(DetectedGame.UwFixUrlOverride), nameof(DetectedGame.UltraPlusUrlOverride),
            nameof(DetectedGame.WikiStatusOverride), nameof(DetectedGame.AuthorOverride), nameof(DetectedGame.ManifestGameNote),
            nameof(DetectedGame.ManifestGameNoteUrl), nameof(DetectedGame.ManifestGameNoteUrlLabel), nameof(DetectedGame.ManifestInstallWarnings),
            nameof(DetectedGame.EngineIniFileOverride),
            // w3/sync rows
            nameof(DetectedGame.DlssPathOverride), nameof(DetectedGame.StreamlinePathOverride),
            // w3/journal rows
            // w3/scan rows
            nameof(DetectedGame.XboxPackage),
            // w3/parity rows
            // w3/vmsplit rows
            // w3/dllnames rows
            // w5/update-policy rows
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            nameof(DetectedGame.UserDisplayName), nameof(DetectedGame.UserWikiName), nameof(DetectedGame.DetectedWikiName), // Edit game (#190)
            nameof(DetectedGame.UserArchitectureOverride),
            // w5/reshade-core rows
            nameof(DetectedGame.ManifestLegacyReShadeVersion), nameof(DetectedGame.ManifestReShadeNote),
            nameof(DetectedGame.ManifestReShadeNoteUrl), nameof(DetectedGame.ManifestReShadeNoteUrlLabel),
            // w7/smalls rows
            // w7/reshade rows
            nameof(DetectedGame.ManifestXnaGacFolder),
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
        ];
        var writable = typeof(DetectedGame).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).ToHashSet();
        Assert.Empty(writable.Except(GameRuntimeFacts.OwnedProperties).Except(notProbeOwned));
        Assert.Empty(GameRuntimeFacts.OwnedProperties.Intersect(notProbeOwned));

        var probed = new DetectedGame
        {
            ExecutablePath = "/g/x.exe", BinaryDirectory = "/g", EngineHint = "Unreal Engine 4", GraphicsApi = GraphicsApi.DirectX12,
            GraphicsApis = [GraphicsApi.DirectX12, GraphicsApi.Vulkan], GraphicsApiConfidence = GraphicsApiConfidence.High,
            GraphicsApiEvidence = "e", CanonicalManifestName = "X", ForcedArchitecture = PeArchitecture.X64, EngineIniProjectOverride = "P",
            ManifestGraphicsApiOverride = GraphicsApi.DirectX11, ManifestGraphicsApis = [GraphicsApi.DirectX11],
            DofFixForcedByManifest = true, DofFixSkippedByManifest = true
        };
        var shared = new DetectedGame { Name = "X", UserGraphicsApiOverride = GraphicsApi.Vulkan };
        GameRuntimeFacts.Capture(probed).ApplyTo(shared);
        foreach (var name in GameRuntimeFacts.OwnedProperties)
        {
            var property = typeof(DetectedGame).GetProperty(name)!;
            object? Value(DetectedGame g) => property.GetValue(g) is IEnumerable<GraphicsApi> list ? string.Join(",", list) : property.GetValue(g);
            Assert.Equal(Value(probed), Value(shared));
        }
        Assert.Equal(GraphicsApi.Vulkan, shared.UserGraphicsApiOverride); // the user's choice is not the probe's to change
    }

    // w2/vmsplit review nit 4: the game count alone does not bound memory (one index may hold 200,000 paths), so the
    // cache is bounded by its total path count too; the newest analysis is always kept.
    [Fact]
    public void LruCache_IsBoundedByTotalWeight_AndKeepsTheNewestEntry()
    {
        var cache = new LruCache<string, int>(8, weigh: size => size, maxWeight: 100);
        cache.Set("a", 40);
        cache.Set("b", 40);
        Assert.Equal(80, cache.Weight);
        cache.Set("c", 40);                 // 120 > 100: the oldest goes
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(80, cache.Weight);
        cache.Set("huge", 500);             // heavier than the bound on its own: kept, everything older goes
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("huge", out _));
        cache.Set("huge", 10);              // replacing an entry re-weighs it
        Assert.Equal(10, cache.Weight);
        Assert.True(cache.Remove("huge"));
        Assert.Equal(0, cache.Weight);
    }

    [Fact]
    public async Task Session_DropsOlderAnalysesBeyondItsPathBudget()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rhi-lru-paths-{Guid.NewGuid():N}")).FullName;
        try
        {
            var games = new List<DetectedGame>();
            for (var i = 0; i < 3; i++)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, $"Paths {i}")).FullName;
                for (var f = 0; f < 50; f++) File.WriteAllBytes(Path.Combine(dir, $"f{f}.pak"), [1]);
                var exe = Path.Combine(dir, "game.exe");
                File.WriteAllBytes(exe, [0x4D, 0x5A]);
                games.Add(new DetectedGame { Name = $"Paths {i}", Source = "Heroic", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Runner = "Proton" });
            }
            using var fx = await ViewModelFixture.StartAsync(games);
            using var http = new HttpClient();
            var dlss = new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(fx.Root, "session-dlss"));
            var session = new SelectedGameSession(new NoOpProbe(), fx.Managed, new LinuxDlssDefaultsService(dlss, fx.Records),
                new RHI.Platform.Linux.Hdr.LinuxHdrLaunchProfileService(), maxCachedPaths: 120);
            foreach (var game in games)
            {
                session.Select(game);
                for (var i = 0; i < 500 && (session.IsProbeBusy || session.Analysis is null); i++) await Task.Delay(10);
            }

            Assert.Equal(2, session.CachedCount);        // 3 × 51 paths > 120: the oldest went
            Assert.True(session.CachedPaths <= 120);
            Assert.False(session.TryGetCached(games[0], out _));
            Assert.True(session.TryGetCached(games[2], out _));
            Assert.Equal(100_000L, SelectedGameSession.DefaultMaxCachedPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // w2/vmsplit review nit 5: a cached analysis published for another instance of the same game (a refresh hands
    // back fresh detector instances) writes its probed facts onto that instance; without it the fresh instance kept
    // GraphicsApi Unknown and no binary directory, and every install was refused.
    [Fact]
    public async Task CachedAnalysis_SelectedThroughAFreshInstance_AppliesItsProbedFacts()
    {
        var probe = new GatedProbe();
        probe.Release.SetResult();
        using var fx = await ViewModelFixture.StartAsync([], probe: probe);
        await AddAsync(fx, "Tunic");
        await WaitForAnalysisAsync(fx);
        var shared = fx.Vm.SelectedGame!;
        Assert.Equal(GraphicsApi.DirectX11, shared.GraphicsApi);

        var fresh = shared.ShallowClone();
        fresh.GraphicsApi = GraphicsApi.Unknown;
        fresh.GraphicsApiEvidence = null;
        fx.Vm.Session.Select(fresh);

        Assert.Same(fresh, fx.Vm.Session.Game);
        Assert.False(fx.Vm.Session.IsProbeBusy);       // a cache hit, not a new analysis
        Assert.Equal(GraphicsApi.DirectX11, fresh.GraphicsApi);
        Assert.Equal("probe evidence", fresh.GraphicsApiEvidence);
    }

    // w2/vmsplit review nit 6: a probe that ignores cancellation and finishes after the selection moved on publishes
    // nothing: the new game's analysis stays, the old game's shared instance is untouched and nothing is cached for it.
    [Fact]
    public async Task StaleProbe_FinishingAfterTheSelectionMoved_IsDiscarded()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rhi-stale-{Guid.NewGuid():N}")).FullName;
        try
        {
            DetectedGame Make(string name)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                var exe = Path.Combine(dir, "game.exe");
                File.WriteAllBytes(exe, [0x4D, 0x5A]);
                return new DetectedGame { Name = name, Source = "Heroic", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Runner = "Proton" };
            }
            var probe = new PerGameProbe();
            using var fx = await ViewModelFixture.StartAsync([Make("Alpha"), Make("Bravo")], probe: probe);
            var alpha = fx.Vm.Games.Single(row => row.Game.Name == "Alpha").Game;
            var bravo = fx.Vm.Games.Single(row => row.Game.Name == "Bravo").Game;

            fx.Vm.SelectedGame = alpha;
            await probe.Started("Alpha").WaitAsync(TimeSpan.FromSeconds(30));
            fx.Vm.SelectedGame = bravo;
            await probe.Started("Bravo").WaitAsync(TimeSpan.FromSeconds(30));
            probe.Release("Bravo");
            await WaitForAnalysisAsync(fx);
            var bravoAnalysis = fx.Vm.Session.Analysis;

            probe.Release("Alpha");
            await probe.Finished("Alpha").WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(200); // let the stale continuation run

            Assert.Same(bravo, fx.Vm.Session.Game);
            Assert.Same(bravoAnalysis, fx.Vm.Session.Analysis);
            Assert.Equal(GraphicsApi.Unknown, alpha.GraphicsApi);
            Assert.False(fx.Vm.Session.TryGetCached(alpha, out _));
            Assert.Equal(GraphicsApi.DirectX11, bravo.GraphicsApi);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // w2/vmsplit review nit 3: the HDR host is never probed from a getter; before the start-up probe lands the plan
    // is built for a "not probed yet" host and rebuilt when it lands.
    [Fact]
    public void HdrHost_BeforeAnyProbe_IsTheNotProbedStandIn_NeverAProbe()
    {
        Assert.Same(LinuxHdrHostSnapshotNotProbed, RuntimePanelViewModel.HdrHostFor(null, null));
        Assert.False(RuntimePanelViewModel.HdrHostFor(null, null).Probed);
        var startup = new RHI.Platform.Linux.Hdr.LinuxHdrHostSnapshot(true, "KDE", true, "595.1", false);
        Assert.Same(startup, RuntimePanelViewModel.HdrHostFor(null, startup));
        Assert.True(startup.Probed);
    }

    private static RHI.Platform.Linux.Hdr.LinuxHdrHostSnapshot LinuxHdrHostSnapshotNotProbed => RHI.Platform.Linux.Hdr.LinuxHdrHostSnapshot.NotProbed;

    private static void Fill(LruCache<string, IReadOnlyList<string>> cache, List<WeakReference> weak)
    {
        for (var game = 0; game < 60; game++)
        {
            var files = new string[3000];
            for (var i = 0; i < files.Length; i++)
                files[i] = $"/home/user/.local/share/Steam/steamapps/common/Some Fairly Long Game Name {game:00}/Engine/Binaries/ThirdParty/file-{i:00000}.pak";
            weak.Add(new WeakReference(files));
            cache.Set($"game-{game}", files);
        }
    }

    private static Task<(bool Added, string Message)> AddAsync(ViewModelFixture fx, string name) => AddAsync(fx, fx.Game(name));

    private static async Task<(bool Added, string Message)> AddAsync(ViewModelFixture fx, DetectedGame game)
    {
        var result = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(result.Added, result.Message);
        return result;
    }

    private static async Task WaitForAnalysisAsync(ViewModelFixture fx)
    {
        for (var i = 0; i < 500 && (fx.Vm.IsBusy || fx.Vm.IsSelectionProbeBusy || fx.Vm.Session.Analysis is null); i++)
            await Task.Delay(10);
        Assert.False(fx.Vm.IsSelectionProbeBusy);
        Assert.NotNull(fx.Vm.Session.Analysis);
    }

    private sealed class NoOpProbe : IGameRuntimeProbe
    {
        public Task ProbeAsync(DetectedGame game, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A probe per game name that writes its copy, then waits for the test, ignoring cancellation.</summary>
    private sealed class PerGameProbe : IGameRuntimeProbe
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TaskCompletionSource Started, TaskCompletionSource Release, TaskCompletionSource Finished)> _gates = new();

        private (TaskCompletionSource Started, TaskCompletionSource Release, TaskCompletionSource Finished) Gate(string name)
            => _gates.GetOrAdd(name, _ => (new(TaskCreationOptions.RunContinuationsAsynchronously),
                new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)));

        public Task Started(string name) => Gate(name).Started.Task;
        public Task Finished(string name) => Gate(name).Finished.Task;
        public void Release(string name) => Gate(name).Release.TrySetResult();

        public async Task ProbeAsync(DetectedGame game, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            var gate = Gate(game.Name);
            game.GraphicsApi = GraphicsApi.DirectX11;
            gate.Started.TrySetResult();
            await gate.Release.Task; // ignores cancellation on purpose
            gate.Finished.TrySetResult();
        }
    }

    /// <summary>A probe that writes the game it is given, then waits until the test lets it finish.</summary>
    private sealed class GatedProbe : IGameRuntimeProbe
    {
        public TaskCompletionSource<DetectedGame> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProbeAsync(DetectedGame game, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            game.GraphicsApi = GraphicsApi.DirectX11;
            game.GraphicsApiConfidence = GraphicsApiConfidence.High;
            game.GraphicsApiEvidence = "probe evidence";
            Started.TrySetResult(game);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
