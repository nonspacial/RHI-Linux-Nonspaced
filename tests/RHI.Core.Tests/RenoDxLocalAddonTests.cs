using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;

namespace RHI.Core.Tests;

/// <summary>
/// #195: a dropped or watched RenoDX add-on installs into the matched game as a local package (recorded like a RenoDX
/// install, removal works, Update All cannot check it), with RenoDX's refusals plus the add-on's own bitness; other
/// ReShade add-ons keep the Custom Add-ons import.
/// </summary>
public sealed class RenoDxLocalAddonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-local-" + Guid.NewGuid().ToString("N"));
    private readonly LinuxDeploymentRecordStore _store;
    private readonly RenoDxDeploymentService _service;
    private readonly DetectedGame _game;
    private readonly GameLinkCatalogueTests.Fake _http = new(_ => throw new HttpRequestException("offline test"));

    private string Bin => _game.BinaryDirectory!;
    private string Downloads => Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;

    public RenoDxLocalAddonTests()
    {
        _game = Game(_root, "Foo Game", 0x8664);
        _store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var http = new HttpClient(_http);
        _service = new RenoDxDeploymentService(http, _store, new RenoDxCatalogService(http), Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal static DetectedGame Game(string root, string name, ushort machine, bool reshade = true)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "games", name, "bin")).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, machine);
        if (reshade) File.WriteAllText(Path.Combine(bin, machine == 0x8664 ? "ReShade64.dll" : "ReShade32.dll"), "MZ ReShade crosire");
        return new DetectedGame { Name = name, Source = "Manual", Runner = "Proton", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe };
    }

    internal static string Addon(string folder, string name, ushort machine = 0x8664, int size = 2048)
    {
        var path = Path.Combine(folder, name);
        PeArchitectureDetectorTests.WritePe(path, machine, size);
        return path;
    }

    [Fact]
    public async Task ALocalAddon_IsInstalledAndRecordedAsRenoDx_UpdateAllCannotCheckIt_RemoveTakesItOut_TheSourceStays()
    {
        var source = Addon(Downloads, "renodx-foogame.addon64");
        var sourceBytes = File.ReadAllBytes(source);

        var result = await _service.InstallLocalAddonAsync(_game, source);

        Assert.True(result.Success, result.Message);
        var target = Path.Combine(Bin, "renodx-foogame.addon64");
        Assert.Equal(RenoDxDeploymentService.LocalInstalledPrefix + target, result.Message);
        Assert.Equal(sourceBytes, File.ReadAllBytes(target));
        var record = _store.Find(_game, ManagedComponentKind.RenoDX)!;
        Assert.True(RenoDxDeploymentService.IsLocalPackage(record));
        Assert.Equal("renodx-foogame.addon64", record.Version);
        Assert.Null(record.SourceUrl);
        Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(target), Assert.Single(record.Files).Sha256!));
        Assert.False(string.IsNullOrWhiteSpace(record.Metadata[RenoDxDeploymentService.AddonVersionMetadataKey]));

        var check = await _service.CheckUpdateAsync(_game, []);
        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck)); // never "current"
        Assert.Equal(RenoDxDeploymentService.LocalPackageCheckMessage, check.Message);
        Assert.Empty(_http.Requests); // nothing was asked of the network

        Assert.True((await _service.RemoveAsync(_game)).Success);
        Assert.False(File.Exists(target));
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task TheAddonsOwnBitnessMustMatchTheGame_WithAPlainReason()
    {
        var x86Addon = Addon(Downloads, "renodx-foogame.addon32", machine: 0x14c);
        var refused = await _service.InstallLocalAddonAsync(_game, x86Addon);
        Assert.Equal(RenoDxDeploymentService.Local32BitOn64BitMessage, refused.Message);

        var oldGame = Game(_root, "Old Game", 0x14c);
        var x64Addon = Addon(Downloads, "renodx-oldgame.addon64");
        Assert.Equal(RenoDxDeploymentService.Local64BitOn32BitMessage, (await _service.InstallLocalAddonAsync(oldGame, x64Addon)).Message);
        // The header decides, not the name: a 64-bit file named .addon32 is refused for the 32-bit game too.
        var misnamed = Addon(Downloads, "renodx-oldgame.addon32", machine: 0x8664);
        Assert.Equal(RenoDxDeploymentService.Local64BitOn32BitMessage, (await _service.InstallLocalAddonAsync(oldGame, misnamed)).Message);

        Assert.Equal(["Game.exe", "ReShade64.dll"], Directory.EnumerateFiles(Bin).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Null(_store.Find(_game, ManagedComponentKind.RenoDX));
        Assert.Equal("Este add-on do RenoDX é de 32 bits e o jogo é de 64 bits; ele precisa do arquivo .addon64 do jogo.",
            UiLocalizationService.PresentFor("pt-BR", refused.Message));
    }

    [Fact]
    public async Task AFileRhiDoesNotOwn_IsNeverOverwritten()
    {
        var foreign = Path.Combine(Bin, "renodx-other.addon64");
        File.WriteAllText(foreign, "MZ the user's own RenoDX");
        var refused = await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64"));
        Assert.StartsWith("Foreign RenoDX protection:", refused.Message, StringComparison.Ordinal);
        File.Delete(foreign);

        var sameName = Path.Combine(Bin, "RENODX-FOOGAME.addon64");
        File.WriteAllText(sameName, "MZ same name, not RHI's");
        var refusedSame = await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64"));
        Assert.False(refusedSame.Success);
        Assert.Equal("MZ same name, not RHI's", File.ReadAllText(sameName));
        Assert.Null(_store.Find(_game, ManagedComponentKind.RenoDX));
    }

    [Fact]
    public async Task ReplacingRhisOwnRenoDx_RemovesTheOldFileOnlyAfterTheSave_AndKeepsItsIniOwnership()
    {
        var old = Addon(Bin, "renodx-foogame-catalogue.addon64");
        _store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(_game), GameName = _game.Name, Component = ManagedComponentKind.RenoDX, BinaryDirectory = Bin,
            Version = "Foo Game", SourceUrl = "https://github.com/rhi-tests/foo/releases/download/snapshot/renodx-foogame-catalogue.addon64",
            Files = [new LinuxDeployedFile { Path = old, Sha256 = FileHash.Sha256Hex(old) }],
            Metadata = { [RenoDxIniOverrideManager.MetadataKey] = "{\"kept\":true}", [RenoDxDeploymentService.RemoteStampMetadataKey] = "{}" }
        });
        var source = Addon(Downloads, "renodx-foogame.addon64", size: 4096);

        _store.SaveFaultForTesting = () => new IOException("Simulated write failure: records.json");
        var failed = await _service.InstallLocalAddonAsync(_game, source);
        _store.SaveFaultForTesting = null;
        Assert.StartsWith("RenoDX install failed: ", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Bin, "renodx-foogame.addon64"))); // put back as it was
        Assert.True(File.Exists(old));
        Assert.Equal("Foo Game", _store.Find(_game, ManagedComponentKind.RenoDX)!.Version);

        var result = await _service.InstallLocalAddonAsync(_game, source);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(old));
        var record = _store.Find(_game, ManagedComponentKind.RenoDX)!;
        Assert.Equal("{\"kept\":true}", record.Metadata[RenoDxIniOverrideManager.MetadataKey]); // removal still puts those values back
        Assert.False(record.Metadata.ContainsKey(RenoDxDeploymentService.RemoteStampMetadataKey));
    }

    [Fact]
    public async Task ASaveTheLedgerRefuses_IsAFailedResult_AndPutsTheTargetBack()
    {
        // The ledger's own refusal (an unreadable ledger) is an InvalidOperationException, not an IO error.
        _store.SaveFaultForTesting = () => new InvalidOperationException("The RHI deployment ledger is unreadable");
        var result = await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64"));
        _store.SaveFaultForTesting = null;

        Assert.StartsWith("RenoDX install failed: ", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Bin, "renodx-foogame.addon64"))); // no add-on left without a record
    }

    [Fact]
    public async Task AnUnreadableLedger_RefusesBeforeAnythingIsWritten()
    {
        File.WriteAllText(Path.Combine(_root, "records.json"), "{ not json");
        var result = await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64"));

        Assert.StartsWith(RenoDxDeploymentService.LocalLedgerUnreadablePrefix, result.Message, StringComparison.Ordinal);
        Assert.Equal(["Game.exe", "ReShade64.dll"], Directory.EnumerateFiles(Bin).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnUnreadableLedger_WithRhisOwnAddonInTheFolder_SaysTheRecordsAreUnreadable_NotForeign()
    {
        // w7 follow-up B11 (smalls re-review 7): RHI's own add-on is not taken for someone else's when its record cannot be read.
        Assert.True((await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64"))).Success);
        File.WriteAllText(Path.Combine(_root, "records.json"), "{ not json");
        var fresh = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var http = new HttpClient();
        var service = new RenoDxDeploymentService(http, fresh, new RenoDxCatalogService(http), Path.Combine(_root, "cache-b11"));
        var before = Directory.EnumerateFiles(Bin).Select(f => (f, File.ReadAllBytes(f).Length)).ToArray();

        var result = await service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64", size: 4096));

        Assert.StartsWith(RenoDxDeploymentService.LocalLedgerUnreadablePrefix, result.Message, StringComparison.Ordinal);
        Assert.Equal(before, Directory.EnumerateFiles(Bin).Select(f => (f, File.ReadAllBytes(f).Length)).ToArray());
    }

    [Fact]
    public async Task RhisOwnAddon_TheUserChanged_IsNotReplaced()
    {
        Assert.True((await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64"))).Success);
        var target = Path.Combine(Bin, "renodx-foogame.addon64");
        File.WriteAllText(target, "MZ the user's edit");

        var refused = await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-foogame.addon64", size: 4096));

        Assert.StartsWith("Managed RenoDX file was modified outside RHI", refused.Message, StringComparison.Ordinal);
        Assert.Equal("MZ the user's edit", File.ReadAllText(target));
    }

    [Fact]
    public async Task AFileWithoutAnAddonHeader_IsNotInstalled_AnAddonWithoutBitnessInItsNameIs()
    {
        var page = Path.Combine(Downloads, "renodx-foogame.addon64");
        File.WriteAllText(page, "<html>a web page saved under the add-on's name</html>");
        Assert.Equal(RenoDxDeploymentService.LocalNotAnAddonPrefix + "renodx-foogame.addon64", (await _service.InstallLocalAddonAsync(_game, page)).Message);
        Assert.Null(_store.Find(_game, ManagedComponentKind.RenoDX));

        // Upstream's drop takes a plain .addon too; its header says which game it fits.
        var plain = Addon(Downloads, "renodx-foogame.addon");
        var result = await _service.InstallLocalAddonAsync(_game, plain);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(Bin, "renodx-foogame.addon")));
    }

    [Fact]
    public async Task OtherRefusals_ChangeNothing()
    {
        var noReShade = Game(_root, "Bare Game", 0x8664, reshade: false);
        Assert.StartsWith("Install ReShade", (await _service.InstallLocalAddonAsync(noReShade, Addon(Downloads, "renodx-baregame.addon64"))).Message, StringComparison.Ordinal);
        var native = Game(_root, "Native Game", 0x8664);
        native.Runner = "Native Linux";
        Assert.False((await _service.InstallLocalAddonAsync(native, Addon(Downloads, "renodx-nativegame.addon64"))).Success);
        var notAddon = Path.Combine(Downloads, "renodx-foogame.txt");
        File.WriteAllText(notAddon, "x");
        Assert.StartsWith(RenoDxDeploymentService.LocalNotAnAddonPrefix, (await _service.InstallLocalAddonAsync(_game, notAddon)).Message, StringComparison.Ordinal);
        Assert.StartsWith(RenoDxDeploymentService.LocalNotAnAddonPrefix, (await _service.InstallLocalAddonAsync(_game, Addon(Downloads, "renodx-dlss5-feed.addon64"))).Message, StringComparison.Ordinal);
        var control = Game(_root, ControlUePostInstallService.GameName, 0x8664);
        Assert.Equal(RenoDxDeploymentService.LocalControlUeMessage,
            (await _service.InstallLocalAddonAsync(control, Addon(Downloads, ControlUePostInstallService.TriggerAddonFileName))).Message);
        Assert.Empty(_store.FindAll(_game));
    }

    [Theory]
    [InlineData("renodx-hogwartslegacy.addon64", true)]
    [InlineData("renodx-hogwartslegacy.addon", true)]     // upstream's drop: no bitness in the name
    [InlineData("renodxhogwarts.addon64", false)]         // upstream: the name starts "renodx-"
    [InlineData("renodx-universal_ue-dof-fix.addon64", false)] // DoF Fix's
    [InlineData("renodx-dlssfix.addon64", false)]
    [InlineData("RenoDX-Cyberpunk2077.addon32", true)]
    [InlineData("renodx-dlss5-feed.addon64", false)]     // DLSS5 Feeder's
    [InlineData("renodx-upgrade.addon64", false)]        // RenoDX Upgrade's
    [InlineData("renodx-mfgunlock.addon64", false)]
    [InlineData("renodx-devkit.addon64", false)]
    [InlineData("renodx-dlss.addon64", false)]           // ShortFuse's
    [InlineData("relimiter.addon64", false)]             // another ReShade add-on: Custom Add-ons import
    [InlineData("renodx-foo.addon.txt", false)]
    public void OnlyRenoDxGameAddons_TakeTheInstallPath(string name, bool expected)
        => Assert.Equal(expected, RenoDxDeploymentService.IsDroppedRenoDxAddon(Path.Combine("/downloads", name)));

    [Fact]
    public void TheGameIsMatched_ByTheCatalogueFile_ThenItsName_ThenUpstreamsWords_ThenTheSelection()
    {
        DetectedGame G(string name) => new() { Name = name, InstallPath = "/g/" + name };
        var games = new[] { G("Alan Wake 2"), G("Cyberpunk 2077"), G("Hogwarts Legacy"), G("Lies of P"), G("Tunic") };
        string? None(DetectedGame _) => null;

        // The catalogue's add-on file for a game wins, even when the file name says nothing of the game's name.
        Assert.Equal(3, DroppedRenoDxPresenter.MatchIndex(games, "renodx-lop.addon64", games[4], game => game.Name == "Lies of P" ? "RENODX-LOP.addon64" : null));
        Assert.Equal(1, DroppedRenoDxPresenter.MatchIndex(games, "renodx-cyberpunk2077.addon64", null, None));
        Assert.Equal(2, DroppedRenoDxPresenter.MatchIndex(games, "renodx-hogwarts.addon64", null, None)); // upstream: "Hogwarts" (> 3 letters) in the name
        Assert.Equal(3, DroppedRenoDxPresenter.MatchIndex(games, "renodx-unknown.addon64", games[3], None)); // the selected game
        Assert.Equal(-1, DroppedRenoDxPresenter.MatchIndex(games, "renodx-unknown.addon64", null, None));    // the user picks
        Assert.Equal(0, DroppedRenoDxPresenter.MatchIndex(games, "renodx-alanwake2.addon64", games[4], None)); // a match beats the selection
        // A one-word name: upstream's word match needs two words, the whole name in the file name finds it.
        Assert.Equal(4, DroppedRenoDxPresenter.MatchIndex(games, "renodx-tunic.addon64", null, None));
        // The longest whole name wins over a shorter one it contains.
        var dishonored = new[] { G("Dishonored"), G("Dishonored 2") };
        Assert.Equal(1, DroppedRenoDxPresenter.MatchIndex(dishonored, "renodx-dishonored2.addon64", null, None));
    }

    [Theory]
    // Dropped (upstream DragDropHandler): every add-on but renodx-dlss5* / renodx-dlss.* is offered for a game.
    [InlineData("renodx-hogwartslegacy.addon64", false, DroppedAddonTarget.RenoDxInstall)]
    [InlineData("renodx-hogwartslegacy.addon", false, DroppedAddonTarget.RenoDxInstall)]
    [InlineData("ShaderToggler.addon64", false, DroppedAddonTarget.GameAddonInstall)]
    [InlineData("tw3-darkernights-remastered.addon32", false, DroppedAddonTarget.GameAddonInstall)]
    [InlineData("renodxhogwarts.addon64", false, DroppedAddonTarget.GameAddonInstall)] // not "renodx-": not RenoDX's
    [InlineData("renodx-dlss5-feed.addon64", false, DroppedAddonTarget.CustomAddonsImport)] // upstream ignores it
    [InlineData("renodx-dlss.addon64", false, DroppedAddonTarget.CustomAddonsImport)]
    [InlineData("renodx-upgrade.addon64", false, DroppedAddonTarget.CustomAddonsImport)] // RHI's rows install these
    [InlineData("renodx-mfgunlock.addon64", false, DroppedAddonTarget.CustomAddonsImport)]
    [InlineData("renodx-universal_ue-dof-fix.addon64", false, DroppedAddonTarget.CustomAddonsImport)]
    [InlineData("relimiter.addon64", false, DroppedAddonTarget.CustomAddonsImport)]
    [InlineData("tool.addon", false, DroppedAddonTarget.CustomAddonsImport)] // Custom Add-ons needs the bitness in the name
    // Watched (upstream AddonFileWatcher): only renodx-*.addon64|32 game add-ons.
    [InlineData("renodx-hogwartslegacy.addon64", true, DroppedAddonTarget.RenoDxInstall)]
    [InlineData("renodx-hogwartslegacy.addon32", true, DroppedAddonTarget.RenoDxInstall)]
    [InlineData("renodx-hogwartslegacy.addon", true, DroppedAddonTarget.CustomAddonsImport)]
    [InlineData("ShaderToggler.addon64", true, DroppedAddonTarget.CustomAddonsImport)]
    [InlineData("renodxhogwarts.addon64", true, DroppedAddonTarget.CustomAddonsImport)]
    [InlineData("renodx-devkit.addon64", true, DroppedAddonTarget.CustomAddonsImport)]
    public void TheGameInstallIsOffered_ForUpstreamsDroppedAndWatchedSets(string name, bool watched, DroppedAddonTarget expected)
    {
        Assert.Equal(expected, LinuxDroppedFileClassifier.AddonTarget(Path.Combine("/downloads", name), watched));
        Assert.Equal(expected != DroppedAddonTarget.CustomAddonsImport, DroppedRenoDxPresenter.Handles(Path.Combine("/downloads", name), watched));
    }

    [Fact]
    public async Task AWatchedFile_StaysWhenTheInstallFails_OrWhenItChangedMeanwhile()
    {
        var game = Game(Path.Combine(_root, "shell"), "Foo Game", 0x8664);
        using var fx = await ViewModelFixture.StartAsync([game]);
        game = fx.Vm.AllLibraryGames.Single(g => g.Name == "Foo Game");
        var watch = Directory.CreateDirectory(Path.Combine(fx.Root, "watch")).FullName;

        var wrongBitness = Addon(watch, "renodx-foogame.addon32", machine: 0x14c);
        var refusedOffer = (await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, wrongBitness, watch))!;
        Assert.True(refusedOffer.DeleteSourceAfterInstall);
        Assert.False(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, refusedOffer, refusedOffer.Games[0]));
        Assert.True(File.Exists(wrongBitness));

        // The browser saves a new download under the same name while RHI installs the first one.
        var watched = Addon(watch, "renodx-foogame.addon64");
        var offer = (await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, watched, watch))!;
        var rewritten = false;
        fx.Records.SaveFaultForTesting = () =>
        {
            if (!rewritten) { rewritten = true; PeArchitectureDetectorTests.WritePe(watched, 0x8664, 4096); }
            return null;
        };
        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, offer.Games[0]));
        fx.Records.SaveFaultForTesting = null;
        Assert.True(rewritten);
        Assert.Equal(4096, new FileInfo(watched).Length); // the newer download is kept
    }

    [Fact]
    public async Task AnotherDroppedAddon_GoesIntoTheGameThroughShadersAddons_BesideRhisRenoDx()
    {
        var game = Game(Path.Combine(_root, "shell"), "Foo Game", 0x8664);
        using var fx = await ViewModelFixture.StartAsync([game]);
        game = fx.Vm.AllLibraryGames.Single(g => g.Name == "Foo Game");
        Assert.True((await fx.Managed.InstallLocalRenoDxAddonAsync(game, Addon(fx.Root, "renodx-foogame.addon64"))).Success);
        var dropped = Addon(Directory.CreateDirectory(Path.Combine(fx.Root, "drop")).FullName, "ShaderToggler.addon64");
        var bytes = File.ReadAllBytes(dropped);

        var offer = (await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, dropped))!;
        Assert.False(offer.IsRenoDx);
        Assert.Equal(UiLocalizationService.Text("Sm.DropAddonTitle"), offer.Title);
        Assert.Null(fx.Vm.DroppedRenoDx.ReplacesFor(offer, offer.Games[0])); // it replaces nothing
        Assert.False(offer.DeleteSourceAfterInstall);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };

        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, offer.Games[0]), string.Join(" | ", lines));

        var placed = Path.Combine(game.BinaryDirectory!, "ShaderToggler.addon64");
        Assert.Equal(bytes, File.ReadAllBytes(placed));
        Assert.Contains(lines, line => line.StartsWith(LinuxManagedComponentService.DroppedAddonInstalledPrefix + placed, StringComparison.Ordinal));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fx.Managed.CustomAddonRoot, "ShaderToggler.addon64")));
        Assert.Contains("custom-ShaderToggler", (await fx.Managed.GetReShadeAddonManagerSnapshotAsync(game)).SelectedIds);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-foogame.addon64"))); // RenoDX stays
        Assert.Equal("renodx-foogame.addon64", fx.Records.Find(game, ManagedComponentKind.RenoDX)!.Version);
        Assert.Equal(bytes, File.ReadAllBytes(dropped)); // a dropped file is never touched

        // Unticked in Shaders/Add-ons, it leaves the game.
        Assert.True((await fx.Managed.ApplyReShadeAddonSelectionAsync(game, [])).Success);
        Assert.False(File.Exists(placed));

        // Its own bitness must fit the game, with a plain reason, before anything is copied.
        var x86 = Addon(Path.Combine(fx.Root, "drop"), "Other.addon32", machine: 0x14c);
        Assert.Equal(LinuxManagedComponentService.DroppedAddon32BitOn64BitMessage, (await fx.Managed.InstallDroppedAddonForGameAsync(game, x86)).Message);
        Assert.False(File.Exists(Path.Combine(fx.Managed.CustomAddonRoot, "Other.addon32")));
    }

    // w7 follow-up B10 (smalls re-review 6): "Install add-on" lists only games with ReShade with add-on support (hide what
    // cannot apply); with none, the status line says why, and the install refuses before anything is copied.
    [Fact]
    public async Task AnotherDroppedAddon_IsOfferedOnlyForGamesWithReShade_AndRefusedBeforeAnyCopyOnOthers()
    {
        var with = Game(Path.Combine(_root, "offer"), "With ReShade", 0x8664);
        var without = Game(Path.Combine(_root, "offer"), "Without ReShade", 0x8664, reshade: false);
        var noAddons = Game(Path.Combine(_root, "offer"), "No Addons ReShade", 0x8664);
        using var fx = await ViewModelFixture.StartAsync([with, without, noAddons]);
        without = fx.Vm.AllLibraryGames.Single(g => g.Name == "Without ReShade");
        noAddons = fx.Vm.AllLibraryGames.Single(g => g.Name == "No Addons ReShade");
        // W7 final review 4: RHI's ReShade there is the No Addons build (ReShade is in the folder, add-ons are not loaded).
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(noAddons), GameName = noAddons.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = noAddons.BinaryDirectory!, Version = "6.8.0",
            Files = [new LinuxDeployedFile { Path = Path.Combine(noAddons.BinaryDirectory!, "ReShade64.dll") }],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["ReShade.Channel"] = nameof(ReShadeChannel.NoAddons) }
        });
        var dropped = Addon(Directory.CreateDirectory(Path.Combine(fx.Root, "drop")).FullName, "ShaderToggler.addon64");

        var offer = (await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, dropped))!;

        Assert.Equal(["With ReShade"], offer.GameNames);
        foreach (var refusedGame in new[] { without, noAddons })
        {
            var refused = await fx.Managed.InstallDroppedAddonForGameAsync(refusedGame, dropped);
            Assert.False(refused.Success);
            Assert.Equal(LinuxManagedComponentService.DroppedAddonNeedsReShadeMessage, refused.Message);
            Assert.False(File.Exists(Path.Combine(fx.Managed.CustomAddonRoot, "ShaderToggler.addon64"))); // nothing copied
            Assert.False(File.Exists(Path.Combine(refusedGame.BinaryDirectory!, "ShaderToggler.addon64")));
        }

        // A RenoDX add-on still lists every Windows game (RenoDX installs ReShade itself where it is missing).
        Assert.Equal(3, (await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, Addon(Path.Combine(fx.Root, "drop"), "renodx-foo.addon64")))!.Games.Count);
    }

    [Fact]
    public async Task AnotherDroppedAddon_NoGameWithReShade_NoDialog_AndTheLineSaysWhy()
    {
        var without = Game(Path.Combine(_root, "none"), "Without ReShade", 0x8664, reshade: false);
        using var fx = await ViewModelFixture.StartAsync([without]);
        var dropped = Addon(Directory.CreateDirectory(Path.Combine(fx.Root, "drop")).FullName, "ShaderToggler.addon64");

        Assert.Null((await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, dropped)));
        Assert.Equal(UiLocalizationService.Format("Sm.DropAddonNoGame", "ShaderToggler.addon64"), DroppedRenoDxPresenter.NoGameLine(dropped, watched: false));
        Assert.Equal(UiLocalizationService.Format("Sm.DropNoGame", "renodx-foo.addon64"), DroppedRenoDxPresenter.NoGameLine("/x/renodx-foo.addon64", watched: false));
    }

    // W7 final review 7: the ReShade check of every library game runs off the UI thread (it reads folders and DLL
    // signatures); the offer itself is built back on the caller's (UI) thread, with the same games as before.
    [Fact]
    public async Task AnotherDroppedAddon_TheReShadeCheckRunsOffTheUiThread_AndTheOfferIsBuiltBackOnIt()
    {
        var with = Game(Path.Combine(_root, "thread"), "With ReShade", 0x8664);
        var without = Game(Path.Combine(_root, "thread"), "Without ReShade", 0x8664, reshade: false);
        using var fx = await ViewModelFixture.StartAsync([with, without]);
        var dropped = Addon(Directory.CreateDirectory(Path.Combine(fx.Root, "drop")).FullName, "ShaderToggler.addon64");
        var presenter = fx.Vm.DroppedRenoDx;
        var checkedOn = new System.Collections.Concurrent.ConcurrentBag<int>();
        presenter.Refusal = game => { checkedOn.Add(Environment.CurrentManagedThreadId); return fx.Managed.DroppedAddonRefusal(game); };
        using var ui = new UiThreadStandIn();

        var (offer, builtOnUi) = await ui.RunAsync(async () =>
        {
            var made = await presenter.OfferForAsync(fx.Vm, dropped);
            return (made, ui.IsCurrent);
        }).WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(["With ReShade"], offer!.GameNames);
        Assert.Equal(2, checkedOn.Count);
        Assert.DoesNotContain(ui.ThreadId, checkedOn);
        Assert.True(builtOnUi);
    }

    // w7 integration (#195 × #179): a dropped add-on "ticked in the game's Shaders/Add-ons list" switches a game that used
    // the global set to "Choose for this game" with the add-ons it already got plus the new one; the global set itself is
    // never edited, and a later global edit no longer takes the dropped add-on out of that game.
    [Fact]
    public async Task ADroppedAddon_OnAGameUsingTheGlobalSet_BecomesItsOwnListWithTheGlobalAddons_AndTheGlobalSetIsUntouched()
    {
        var game = Game(Path.Combine(_root, "sets"), "Set Game", 0x8664);
        using var fx = await ViewModelFixture.StartAsync([game]);
        game = fx.Vm.AllLibraryGames.Single(g => g.Name == "Set Game");
        var sets = fx.Managed.ShaderSets!;
        Assert.True(fx.Managed.ImportCustomAddonFile(Addon(Directory.CreateDirectory(Path.Combine(fx.Root, "own")).FullName, "Other.addon64")).Success);
        sets.SetGlobalAddonIds(["custom-Other"]);
        Assert.True(sets.Pin(game)); // a saved choice, as start-up's adoption leaves every game (an unsaved one re-adopts from the ledger)
        Assert.Equal(RHI.Platform.Linux.Settings.ShaderAddonMode.Global, sets.ChoiceFor(game)!.AddonMode);
        var dropped = Addon(Directory.CreateDirectory(Path.Combine(fx.Root, "drop")).FullName, "ShaderToggler.addon64");

        var result = await fx.Managed.InstallDroppedAddonForGameAsync(game, dropped);

        Assert.True(result.Success, result.Message);
        var choice = sets.ChoiceFor(game)!;
        Assert.Equal(RHI.Platform.Linux.Settings.ShaderAddonMode.Select, choice.AddonMode);
        Assert.Equal(["custom-Other", "custom-ShaderToggler"], choice.AddonIds.Order(StringComparer.Ordinal));
        Assert.Equal(["custom-Other"], sets.GlobalAddonIds);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "Other.addon64")));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "ShaderToggler.addon64")));

        sets.SetGlobalAddonIds([]);
        Assert.Equal(["custom-Other", "custom-ShaderToggler"], fx.Managed.EffectiveAddons(game)!.Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FromTheShell_ItInstallsThroughTheGate_WithAHistoryEntry_AndAWatchedFileIsRemovedAfterwards()
    {
        var game = Game(Path.Combine(_root, "shell"), "Foo Game", 0x8664);
        var native = Game(Path.Combine(_root, "shell"), "Native Game", 0x8664);
        native.Runner = "Native Linux";
        using var fx = await ViewModelFixture.StartAsync([game, native]);
        game = fx.Vm.AllLibraryGames.Single(g => g.Name == "Foo Game");
        var watch = Directory.CreateDirectory(Path.Combine(fx.Root, "watch")).FullName;
        var watched = Addon(watch, "renodx-foogame.addon64");

        var offer = (await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, watched, watch))!;
        Assert.Equal(["Foo Game"], offer.GameNames); // a native Linux game cannot take a RenoDX add-on
        Assert.Equal(0, offer.MatchedIndex);
        Assert.True(offer.DeleteSourceAfterInstall);
        Assert.False((await fx.Vm.DroppedRenoDx.OfferForAsync(fx.Vm, Addon(fx.Root, "renodx-foogame.addon64"), watch))!.DeleteSourceAfterInstall); // dropped, not watched
        Assert.EndsWith(Path.Combine("Foo Game", "bin"), DroppedRenoDxPresenter.FolderFor(offer.Games[0]), StringComparison.Ordinal);

        // The lines the install writes (a selection analysis' "Ready" line may replace them a moment later).
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };
        var installed = await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, offer.Games[0]);

        Assert.True(installed, string.Join(" | ", lines));
        Assert.False(File.Exists(watched)); // upstream deleteSourceAfterInstall
        Assert.True(lines.Any(line => line.StartsWith(RenoDxDeploymentService.LocalInstalledPrefix, StringComparison.Ordinal)
                                      && line.Contains(UiLocalizationService.Text("Sm.DropSourceRemoved"), StringComparison.Ordinal)), // a pending notice may ride along
            string.Join(" | ", lines));
        Assert.True(RenoDxDeploymentService.IsLocalPackage(fx.Records.Find(game, ManagedComponentKind.RenoDX)));
        Assert.Equal("Replaces the RenoDX add-on RHI installed: renodx-foogame.addon64", fx.Vm.DroppedRenoDx.ReplacesFor(offer.Games[0]));
        var history = new ComponentUpdateLogStore(Path.Combine(Path.GetDirectoryName(fx.Settings.SettingsPath)!, "prefs", "update-log.json")).GetAll();
        Assert.Contains(history, e => e.Component == ManagedComponentKind.RenoDX && e.Action == ComponentUpdateAction.Install);

        using (var held = fx.Vm.Operations.TryBegin("test"))
        {
            var again = Addon(fx.Root, "renodx-foogame.addon64");
            lines.Clear();
            Assert.False(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer with { SourcePath = again, DeleteSourceAfterInstall = false }, offer.Games[0]));
            Assert.Contains(lines, line => line.StartsWith(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), StringComparison.Ordinal));
            Assert.True(File.Exists(again));
        }
    }
}
