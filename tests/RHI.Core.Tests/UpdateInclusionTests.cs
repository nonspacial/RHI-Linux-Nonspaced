using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #178 (upstream ExcludeFromUpdateAll*, GlobalSkip*Updates): a component left out by a game's update inclusion, or by
/// Settings for every game, is neither checked nor updated by Update All or the background pass and counts as "left
/// out"; the per-game DLSS / Streamline choice stops Follow newest; the store keeps names it does not know, fails closed
/// when unreadable and follows a moved game.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UpdateInclusionTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-inclusion-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---- store ----

    [Fact]
    public void Store_SavesPerGameChoices_KeepsUnknownAndUnshownNames()
    {
        var path = Path.Combine(_root, "update-inclusion.json");
        var store = new UpdateInclusionStore(path);
        var game = Game("Kept");
        File.WriteAllText(path, $$"""{ "{{GameRecordIdentity.Key(game).Replace("\\", "\\\\")}}": ["SomethingNewer", "OptiScaler"] }""");

        store.SetExcluded(game, shown: [ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX], excluded: [ManagedComponentKind.ReShade]);

        Assert.Equal(UpdateExclusion.ThisGame, store.ExclusionFor(game, ManagedComponentKind.ReShade));
        Assert.Equal(UpdateExclusion.None, store.ExclusionFor(game, ManagedComponentKind.RenoDX));
        Assert.Equal(UpdateExclusion.ThisGame, store.ExclusionFor(game, ManagedComponentKind.OptiScaler)); // not shown: kept
        Assert.Contains("SomethingNewer", File.ReadAllText(path));
        Assert.Equal([ManagedComponentKind.ReShade, ManagedComponentKind.OptiScaler], store.ExcludedFor(game).Order());

        store.SetExcluded(game, [ManagedComponentKind.ReShade, ManagedComponentKind.OptiScaler], []);
        Assert.Empty(store.ExcludedFor(game));
        Assert.Contains("SomethingNewer", File.ReadAllText(path));
    }

    [Fact]
    public void Store_GlobalSkips_ApplyToUpstreamsSixComponents_ForEveryGame()
    {
        var settings = new LinuxAppSettings { GlobalSkipReShadeUpdates = true, GlobalSkipReFrameworkUpdates = true };
        var store = new UpdateInclusionStore(Path.Combine(_root, "g.json"), () => UpdateInclusionStore.GlobalSkipsFrom(settings));

        Assert.Equal(UpdateExclusion.AllGames, store.ExclusionFor(Game("Any"), ManagedComponentKind.ReShade));
        Assert.Equal(UpdateExclusion.AllGames, store.ExclusionFor(Game("Any"), ManagedComponentKind.REFramework));
        Assert.Equal(UpdateExclusion.None, store.ExclusionFor(Game("Any"), ManagedComponentKind.RenoDX));
        Assert.Equal(
            [ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX, ManagedComponentKind.ReLimiter, ManagedComponentKind.DisplayCommander, ManagedComponentKind.OptiScaler, ManagedComponentKind.REFramework],
            UpdateInclusionStore.GlobalSkipsFrom(new LinuxAppSettings
            {
                GlobalSkipReShadeUpdates = true, GlobalSkipRenoDxUpdates = true, GlobalSkipReLimiterUpdates = true,
                GlobalSkipDisplayCommanderUpdates = true, GlobalSkipOptiScalerUpdates = true, GlobalSkipReFrameworkUpdates = true
            }).Order());
    }

    [Fact]
    public void Store_Unreadable_LeavesEverythingOut_UntilItReadsAgain()
    {
        var path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ not json");
        var store = new UpdateInclusionStore(path);

        Assert.Equal(UpdateExclusion.ThisGame, store.ExclusionFor(Game("Any"), ManagedComponentKind.ReShade));
        Assert.Throws<RHI.Platform.Linux.Persistence.JsonStoreUnavailableException>(() =>
            store.SetExcluded(Game("Any"), [ManagedComponentKind.ReShade], []));
    }

    [Fact]
    public void Store_FollowsAMovedFolderKeyedGame()
    {
        var store = new UpdateInclusionStore(Path.Combine(_root, "move.json"));
        var game = Game("Mover");
        store.SetExcluded(game, [ManagedComponentKind.RenoDX], [ManagedComponentKind.RenoDX]);
        var moved = game.ShallowClone();
        moved.InstallPath = Directory.CreateDirectory(Path.Combine(_root, "new", "Mover")).FullName;

        var write = store.StageMove(new GameStateMove(moved, [(game.InstallPath, moved.InstallPath)], [GameRecordIdentity.Key(game)]));

        Assert.NotNull(write);
        RHI.Platform.Linux.Persistence.StagedFileCommit.Commit([write!]);
        Assert.Equal(UpdateExclusion.ThisGame, store.ExclusionFor(moved, ManagedComponentKind.RenoDX));
    }

    // ---- Update All ----

    [Theory]
    [InlineData(LinuxUpdateRunMode.Manual)]
    [InlineData(LinuxUpdateRunMode.Background)]
    public async Task UpdateAll_LeavesExcludedComponentsAlone_AndCountsThemLeftOut(LinuxUpdateRunMode mode)
    {
        var game = Game("Excluded");
        var records = Records(game, ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX);
        var backend = new CountingBackend(component => component == ManagedComponentKind.RenoDX ? UpdateExclusion.ThisGame : UpdateExclusion.None);

        var result = await new LinuxUpdateAllService(records, backend, new Running(false)).RunAsync([game], _ => [], mode);

        Assert.DoesNotContain(ManagedComponentKind.RenoDX, backend.Touched);
        Assert.Contains(ManagedComponentKind.ReShade, backend.Touched);
        Assert.Equal((1, 0, 0), (result.ExcludedCount, result.UncheckedCount, result.FailedCount));
        var item = Assert.Single(result.Items, i => i.Component == ManagedComponentKind.RenoDX);
        Assert.True(item.Excluded);
        Assert.Equal(LinuxUpdateAllService.LeftOutMessage, item.Message);
        Assert.Equal(1, result.CurrentCount); // ReShade: checked/refreshed, nothing changed
    }

    [Fact]
    public async Task UpdateAll_ARunningGameWhoseComponentsAreAllLeftOut_IsNotDeferred()
    {
        var game = Game("Running");
        var records = Records(game, ManagedComponentKind.OptiScaler);
        var backend = new CountingBackend(_ => UpdateExclusion.AllGames);

        var result = await new LinuxUpdateAllService(records, backend, new Running(true)).RunAsync([game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Equal((1, 0), (result.ExcludedCount, result.DeferredCount));
    }

    [Theory]
    [InlineData("en-US", "Update All: 0 updated · 1 current · 0 deferred · 0 failed · 1 left out (update inclusion).")]
    [InlineData("pt-BR", "Atualizar tudo: 0 atualizado(s) · 1 em dia · 0 adiado(s) · 0 com falha · 1 deixado(s) de fora (inclusão em atualizações).")]
    public void UpdateAllSummary_NamesWhatWasLeftOut(string language, string expected)
    {
        var result = new LinuxUpdateAllResult([
            new LinuxUpdateItemResult("G", ManagedComponentKind.ReShade, true, false, false, "current"),
            new LinuxUpdateItemResult("G", ManagedComponentKind.RenoDX, true, false, false, LinuxUpdateAllService.LeftOutMessage, Excluded: true)
        ]);
        Assert.Equal(expected, UiLocalizationService.UpdateAllSummaryFor(language, result));
    }

    // ---- component rows ----

    [Theory]
    [InlineData(ManagedComponentKind.ReShade, UpdateExclusion.ThisGame, "Left out of Update All in this game.")]
    [InlineData(ManagedComponentKind.ReShade, UpdateExclusion.AllGames, "Left out of Update All in every game (Settings).")]
    [InlineData(ManagedComponentKind.Dlss, UpdateExclusion.ThisGame, "Not moved to the newest version automatically in this game.")]
    public void ComponentRow_SaysWhyUpdateAllLeavesItAlone(ManagedComponentKind kind, UpdateExclusion exclusion, string note)
    {
        var row = new ComponentRowViewModel(new ManagedComponent
        {
            Kind = kind, DisplayName = kind.ToString(), Availability = ComponentAvailability.Installed, Note = "Installed by RHI.",
            UpdateExclusion = exclusion
        }, new NoOpCommand());

        Assert.Equal(note, row.UpdateExclusionNote);
        Assert.EndsWith(note, row.Note);
        Assert.Null(new ComponentRowViewModel(new ManagedComponent { Kind = kind, DisplayName = "x" }, new NoOpCommand()).UpdateExclusionNote);
    }

    [Fact]
    public async Task Decoration_MarksInstalledRhiRowsTheInclusionLeavesOut()
    {
        var game = Game("Decorated");
        using var fx = await ViewModelFixture.StartAsync([game]);
        fx.Records.Upsert(Record(game, ManagedComponentKind.RenoDX));
        ManagedComponentKind[] leftOut = [ManagedComponentKind.RenoDX, ManagedComponentKind.ReShade, ManagedComponentKind.Dlss];
        fx.Vm.UpdatePolicy!.Inclusion!.SetExcluded(game, leftOut, leftOut);
        var components = new List<ManagedComponent>
        {
            new() { Kind = ManagedComponentKind.RenoDX, DisplayName = "RenoDX", Availability = ComponentAvailability.Installed },
            new() { Kind = ManagedComponentKind.ReShade, DisplayName = "ReShade", Availability = ComponentAvailability.Installed },
            new() { Kind = ManagedComponentKind.Dlss, DisplayName = "DLSS", Availability = ComponentAvailability.Installed }
        };

        fx.Managed.Decorate(game, components);

        Assert.Equal(UpdateExclusion.ThisGame, components[0].UpdateExclusion);
        Assert.Equal(UpdateExclusion.None, components[1].UpdateExclusion); // left out, but not RHI's: Update All never touches it anyway
        Assert.Equal(UpdateExclusion.ThisGame, components[2].UpdateExclusion);
    }

    // ---- card menu and dialog ----

    [Fact]
    public async Task CardMenu_OffersUpdateInclusion_OnlyWhereItHasABox_AndSavesTheDialogsChoice()
    {
        var managed = Game("Managed Quest");
        var plain = Game("Plain Quest");
        using var fx = await ViewModelFixture.StartAsync([managed, plain]);
        fx.Records.Upsert(Record(managed, ManagedComponentKind.ReShade));
        fx.Records.Upsert(Record(managed, ManagedComponentKind.OptiScaler));
        UpdateInclusionDialogModel? shown = null;
        fx.Vm.UpdatePolicy!.ShowInclusionAsync = model =>
        {
            shown = model;
            return Task.FromResult<IReadOnlySet<ManagedComponentKind>?>(new HashSet<ManagedComponentKind> { ManagedComponentKind.OptiScaler });
        };
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.GlobalSkipReShadeUpdates = true;
        fx.Vm.Settings.SaveLinuxSettings(settings);

        Assert.DoesNotContain(fx.Vm.CardMenuFor(Row(fx, "Plain Quest")), item => item.Id == "update-inclusion");
        var entry = Assert.Single(fx.Vm.CardMenuFor(Row(fx, "Managed Quest")), item => item.Id == "update-inclusion");
        // The line the save wrote (the selection analysis' "Ready" line may replace it a moment later).
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };
        Assert.Equal("Update inclusion…", entry.Label);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)entry.Command).ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal([ManagedComponentKind.OptiScaler], shown.Choices.Select(choice => choice.Kind)); // ReShade: left out for every game
        Assert.Equal("Left out of Update All in every game (Settings → Maintenance): ReShade", shown.AllGamesNote);
        Assert.Equal(UpdateExclusion.ThisGame, fx.Managed.UpdateExclusionFor(Row(fx, "Managed Quest").Game, ManagedComponentKind.OptiScaler));
        Assert.Equal(UpdateExclusion.AllGames, fx.Managed.UpdateExclusionFor(Row(fx, "Managed Quest").Game, ManagedComponentKind.ReShade));
        Assert.Contains(lines, line => line.StartsWith("Update inclusion saved for Managed Quest.", StringComparison.Ordinal)); // a pending notice may ride along
    }

    [Fact]
    public async Task CardMenu_OffersTheDlssBox_OnlyWhileFollowNewestIsOn()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Dlss Quest")]);
        fx.Vm.UpdatePolicy!.ShowInclusionAsync = _ => Task.FromResult<IReadOnlySet<ManagedComponentKind>?>(null);
        Assert.DoesNotContain(fx.Vm.CardMenuFor(Row(fx, "Dlss Quest")), item => item.Id == "update-inclusion");

        var model = fx.Vm.UpdatePolicy.InclusionFor(Row(fx, "Dlss Quest").Game, [], new LinuxAppSettings { FollowNewestDlss = true });

        Assert.Equal([ManagedComponentKind.Dlss], model!.Choices.Select(choice => choice.Kind));
        Assert.Equal("DLSS (follow newest)", model.Choices[0].Label);
    }

    [Fact]
    public async Task CancelledDialog_SavesNothing()
    {
        var game = Game("Cancel Quest");
        using var fx = await ViewModelFixture.StartAsync([game]);
        fx.Records.Upsert(Record(game, ManagedComponentKind.ReShade));
        fx.Vm.UpdatePolicy!.ShowInclusionAsync = _ => Task.FromResult<IReadOnlySet<ManagedComponentKind>?>(null);
        var entry = fx.Vm.CardMenuFor(Row(fx, "Cancel Quest")).Single(item => item.Id == "update-inclusion");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)entry.Command).ExecuteAsync(null);

        Assert.Empty(fx.Vm.UpdatePolicy.Inclusion!.ExcludedFor(game));
        Assert.DoesNotContain(lines, line => line.Contains("Update inclusion saved", StringComparison.Ordinal));
    }

    // ---- settings ----

    [Fact]
    public async Task SettingsSnapshot_KeepsTheGlobalChoices()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.GlobalSkipRenoDxUpdates = true;
        settings.GlobalSkipOptiScalerUpdates = true;
        fx.Vm.Settings.SaveLinuxSettings(settings);

        var again = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        fx.Vm.Settings.SaveLinuxSettings(again); // Save rebuilds from the snapshot: nothing may be reset

        var saved = fx.Settings.Load();
        Assert.Equal([ManagedComponentKind.RenoDX, ManagedComponentKind.OptiScaler], UpdateInclusionStore.GlobalSkipsFrom(saved).Order());
    }

    // ---- Follow newest per game ----

    [Theory]
    [InlineData(ManagedComponentKind.Dlss)]
    [InlineData(ManagedComponentKind.Streamline)]
    public async Task FollowNewest_SkipsAGameWhoseInclusionLeavesThatFamilyOut(ManagedComponentKind family)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "follow", family.ToString())).FullName;
        var exe = Path.Combine(dir, "g.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.4.0"));
        File.WriteAllBytes(Path.Combine(dir, "sl.interposer.dll"), DlssAutoFollowServiceTests.Pe("2.14.1.0"));
        var game = new DetectedGame { Name = "follow-" + family, InstallPath = dir, BinaryDirectory = dir, ExecutablePath = exe, Source = "Test", Runner = "Wine" };
        var inclusion = new UpdateInclusionStore(Path.Combine(_root, "follow-inclusion.json"));
        inclusion.SetExcluded(game, [family], [family]);
        var http = new HttpClient(new DlssAutoFollowServiceTests.ManifestHandler());
        var dlss = new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-" + family));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records-" + family + ".json"));
        var store = new DlssAutoFollowStore(Path.Combine(_root, "follow-" + family + ".json"));
        store.Save(new(StringComparer.OrdinalIgnoreCase) { ["dlss"] = new() { Newest = "310.4.0" }, ["streamline"] = new() { Newest = "2.14.1.0" } });
        var service = new DlssAutoFollowService(dlss, new LinuxDlssDefaultsService(dlss, records), records, store,
            runningProbe: new Running(false), updateInclusion: inclusion);

        var result = await service.RunAsync([game], new DlssFollowChoice(true, true));

        var dlssMoved = DlssAutoFollowService.PeFileVersion(Path.Combine(dir, "nvngx_dlss.dll")) == "310.5.2.0";
        var slMoved = DlssAutoFollowService.PeFileVersion(Path.Combine(dir, "sl.interposer.dll")) == "2.14.16.0";
        Assert.Equal((family != ManagedComponentKind.Dlss, family != ManagedComponentKind.Streamline), (dlssMoved, slMoved));
        Assert.Equal(1, result.UpdatedCount);
    }

    // ---- helpers ----

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "games", name)).FullName;
        var exe = Path.Combine(dir, "game.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Source = "Manual", Runner = "Manual Windows" };
    }

    private static LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!, Version = "1.0"
    };

    private LinuxDeploymentRecordStore Records(DetectedGame game, params ManagedComponentKind[] kinds)
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records-" + Guid.NewGuid().ToString("N") + ".json"));
        foreach (var kind in kinds) records.Upsert(Record(game, kind));
        return records;
    }

    private static GameRowViewModel Row(ViewModelFixture fx, string name) => fx.Vm.Games.Single(row => row.Name == name);

    private sealed class NoOpCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    private sealed class Running(bool running) : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => running;
    }

    private sealed class CountingBackend(Func<ManagedComponentKind, UpdateExclusion> exclusion) : ILinuxManagedUpdateBackend
    {
        public List<ManagedComponentKind> Touched { get; } = [];

        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            Touched.Add(component);
            return Task.FromResult(new ManagedUpdateCheck(true, false, "current"));
        }

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            Touched.Add(component);
            return Task.FromResult(new ComponentOperationResult(true, "refreshed"));
        }

        public UpdateExclusion UpdateExclusionFor(DetectedGame game, ManagedComponentKind component) => exclusion(component);
    }
}
