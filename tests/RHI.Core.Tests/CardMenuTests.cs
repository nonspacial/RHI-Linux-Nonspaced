using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// The library card's right-click menu (Refs #161, upstream 2.8.6): it selects the card, offers the header's existing
/// actions for it (Open folder, Favourite, Hide/Show) and View notes only when the game has a manifest note.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CardMenuTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-card-menu-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task OpeningTheMenu_SelectsTheCard_AndItsEntriesActOnIt()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Alpha Quest"), Game("Beta Quest")]);
        fx.Vm.SelectedGame = Row(fx, "Alpha Quest").Game;

        var menu = fx.Vm.CardMenuFor(Row(fx, "Beta Quest"));

        Assert.Equal("Beta Quest", fx.Vm.SelectedGame?.Name);
        Assert.Equal(["open-folder", "favourite", "hide"], menu.Select(i => i.Id));
        Assert.Equal([UiLocalizationService.Text("Ui.OpenFolder"), fx.Vm.FavouriteActionText, fx.Vm.HideActionText], menu.Select(i => i.Label));
        Assert.All(menu, item => Assert.True(item.Command.CanExecute(null), item.Id)); // nothing greyed out

        menu.Single(i => i.Id == "favourite").Command.Execute(null);
        Assert.True(Row(fx, "Beta Quest").Game.IsFavourite);
        Assert.False(Row(fx, "Alpha Quest").Game.IsFavourite);
        Assert.Equal(UiLocalizationService.Text("Ui.FavouriteOn"), fx.Vm.CardMenuFor(Row(fx, "Beta Quest")).Single(i => i.Id == "favourite").Label);

        var betaKey = LinuxGameLibraryStoreKey(fx, "Beta Quest");
        fx.Vm.CardMenuFor(Row(fx, "Beta Quest")).Single(i => i.Id == "hide").Command.Execute(null);
        Assert.Contains(betaKey, fx.Library.Load().HiddenGames);
        Assert.DoesNotContain(fx.Vm.Games, row => row.Name == "Beta Quest"); // hidden cards leave the list, as from the header
    }

    [Fact]
    public async Task ViewNotes_IsOfferedOnlyForAGameWithANote_AndShowsItWithItsLink()
    {
        var noted = Game("Noted Quest");
        noted.ManifestGameNote = "  Use the DX11 renderer.  ";
        noted.ManifestGameNoteUrl = "https://github.com/rhi-tests/notes";
        noted.ManifestGameNoteUrlLabel = "Guide";
        var missing = Game("Plain Quest");
        missing.ExecutablePath = null;
        missing.RequiresExplicitPatchTarget = true;
        using var fx = await ViewModelFixture.StartAsync([noted, missing]);
        GameNotesViewModel? shown = null;
        fx.Vm.ShowNotesAsync = notes => { shown = notes; return Task.CompletedTask; };

        var plain = fx.Vm.CardMenuFor(Row(fx, "Plain Quest"));
        Assert.DoesNotContain(plain, i => i.Id == "notes");
        Assert.Equal(["favourite", "hide"], plain.Select(i => i.Id)); // no confirmed game folder yet: Open folder is left out

        var menu = fx.Vm.CardMenuFor(Row(fx, "Noted Quest"));
        var notes = menu.Last();
        Assert.Equal(("notes", true), (notes.Id, notes.SeparatorBefore));
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)notes.Command).ExecuteAsync(null);

        Assert.Equal(new GameNotesViewModel(UiLocalizationService.Format("Card.NotesTitle", "Noted Quest"), "Use the DX11 renderer.",
            "https://github.com/rhi-tests/notes", "Guide"), shown);
    }

    /// <summary>
    /// Review w3 major 1: an action that does not apply to the card is left out, never shown greyed (the owner's rule for
    /// every button and option). Open folder needs a confirmed game folder: a folder-missing entry, a folder RHI may not
    /// touch and a card still waiting for its target in Edit game have none.
    /// </summary>
    [Theory]
    [InlineData("folder missing")]
    [InlineData("folder not allowed")]
    [InlineData("target not chosen")]
    public async Task OpenFolder_IsLeftOut_ForACardWithoutAConfirmedFolder(string state)
    {
        var game = Game("Odd Quest");
        switch (state)
        {
            case "folder missing": game.FolderMissing = true; break;
            case "folder not allowed": game.GameRootNotAllowed = true; break;
            default: game.RequiresExplicitPatchTarget = true; game.ExecutablePath = null; break;
        }
        using var fx = await ViewModelFixture.StartAsync([game, Game("Plain Quest")]);
        Assert.False(Row(fx, "Odd Quest").Game.HasConfirmedPatchTarget, state);

        var menu = fx.Vm.CardMenuFor(Row(fx, "Odd Quest"));

        Assert.Equal(["favourite", "hide"], menu.Select(i => i.Id));
        Assert.All(menu, item => Assert.True(item.Command.CanExecute(null), item.Id));
        Assert.Contains(fx.Vm.CardMenuFor(Row(fx, "Plain Quest")), i => i.Id == "open-folder"); // a confirmed folder keeps it
    }

    /// <summary>
    /// Review w3 nit 8: Avalonia turns only its platform's context-menu gestures into ContextRequested — on Linux the menu
    /// key alone — so Shift+F10 on a focused card is opened by the window itself, and never twice where the platform
    /// already handles it.
    /// </summary>
    [Theory]
    [InlineData(Avalonia.Input.Key.F10, Avalonia.Input.KeyModifiers.Shift, false, true)]   // Linux: the window opens it
    [InlineData(Avalonia.Input.Key.F10, Avalonia.Input.KeyModifiers.Shift, true, false)]   // a platform that maps it already
    [InlineData(Avalonia.Input.Key.Apps, Avalonia.Input.KeyModifiers.None, false, false)]  // the platform raises ContextRequested
    [InlineData(Avalonia.Input.Key.F10, Avalonia.Input.KeyModifiers.None, false, false)]
    [InlineData(Avalonia.Input.Key.F10, Avalonia.Input.KeyModifiers.Shift | Avalonia.Input.KeyModifiers.Control, false, false)]
    public void ShiftF10_OpensTheCardMenu_WhereThePlatformDoesNot(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers, bool platformMapsShiftF10, bool expected)
    {
        var linux = new List<Avalonia.Input.KeyGesture> { new(Avalonia.Input.Key.Apps) };
        if (platformMapsShiftF10) linux.Add(new(Avalonia.Input.Key.F10, Avalonia.Input.KeyModifiers.Shift));
        Assert.Equal(expected, RHI.Desktop.Views.CardMenuKeys.OpensMenuItself(key, modifiers, linux));
    }

    [Fact]
    public async Task TheMenuReadsInPortuguese()
    {
        var noted = Game("Noted Quest");
        noted.ManifestGameNote = "Nota.";
        try
        {
            using var fx = await ViewModelFixture.StartAsync([noted], uiLanguage: "pt-BR");
            UiLocalizationService.Apply("pt-BR");
            var menu = fx.Vm.CardMenuFor(Row(fx, "Noted Quest"));
            Assert.Equal(["Abrir pasta", "☆ Favorito", "Ocultar", "Ver notas"], menu.Select(i => i.Label));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task ShowInPartingX_IsOfferedOnlyForATitleWithAShowEntryPoint()
    {
        var xbox = Game("Xbox Quest");
        xbox.XboxPackage = new XboxPackageInfo("9ABC", "PartingX", null, null, ShowArgv: ["/opt/partingx/run.sh", "--show", "9ABC"]);
        var noShow = Game("Opener Quest");
        noShow.XboxPackage = new XboxPackageInfo("9DEF", "PartingX", "'/opt/partingx/run.sh'", null);
        using var fx = await ViewModelFixture.StartAsync([xbox, noShow, Game("Plain Quest")]);

        var menu = fx.Vm.CardMenuFor(Row(fx, "Xbox Quest"));

        var show = Assert.Single(menu, i => i.Id == "show-in-owning-app");
        Assert.Equal(UiLocalizationService.Format("Scan.ShowInOwningApp", "PartingX"), show.Label);
        Assert.Equal(["open-folder", "show-in-owning-app", "favourite", "hide"], menu.Select(i => i.Id));
        Assert.DoesNotContain(fx.Vm.CardMenuFor(Row(fx, "Opener Quest")), i => i.Id == "show-in-owning-app");
        Assert.DoesNotContain(fx.Vm.CardMenuFor(Row(fx, "Plain Quest")), i => i.Id == "show-in-owning-app");
    }

    [Fact]
    public async Task ParityEntries_CreateShortcutSubmenu_AndNexusPageOnlyWhereTheManifestNamesOne()
    {
        var nexus = Game("Nexus Quest");
        nexus.NexusUrlOverride = "https://www.nexusmods.com/games/nexusquest";
        using var fx = await ViewModelFixture.StartAsync([nexus, Game("Plain Quest")]);
        Assert.DoesNotContain(fx.Vm.CardMenuFor(Row(fx, "Plain Quest")), i => i.Id is "create-shortcut" or "nexus-page"); // built without parity
        using var http = new HttpClient(new OfflineHandler());
        var wine = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http,
            new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(Path.Combine(_root, "app-records.json")), wine,
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        fx.Vm.Parity = services.Parity;

        var plain = fx.Vm.CardMenuFor(Row(fx, "Plain Quest"));
        var noted = fx.Vm.CardMenuFor(Row(fx, "Nexus Quest"));

        var shortcut = Assert.Single(plain, i => i.Id == "create-shortcut");
        Assert.True(shortcut.SeparatorBefore);
        Assert.Equal(["shortcut-desktop", "shortcut-menu"], shortcut.Children!.Select(i => i.Id));
        Assert.Equal(UiLocalizationService.Text("Parity.ShortcutCreate"), shortcut.Label);
        Assert.DoesNotContain(plain, i => i.Id == "nexus-page");
        Assert.Equal(["open-folder", "favourite", "hide", "create-shortcut", "nexus-page"], noted.Select(i => i.Id));
        var page = noted.Single(i => i.Id == "nexus-page");
        Assert.Equal("https://www.nexusmods.com/games/nexusquest", page.Tip);
        Assert.False(page.SeparatorBefore); // one separator before the parity group
    }

    /// <summary>
    /// #174 (owner, 2026-10-14): "Nexus Mods page" never showed, not even for Skyrim Special Edition — it only came from
    /// the manifest's 25 overrides. Steam's name for it is not Nexus's, so the built-in table answers, with or without
    /// the parity services (opening a page needs no account).
    /// </summary>
    [Fact]
    public async Task NexusPage_IsOffered_ForSkyrimSpecialEdition_WithoutAManifestEntry()
    {
        var skyrim = Game("The Elder Scrolls V: Skyrim Special Edition");
        using var fx = await ViewModelFixture.StartAsync([skyrim, Game("Plain Quest")]);

        var bare = Assert.Single(fx.Vm.CardMenuFor(Row(fx, "The Elder Scrolls V: Skyrim Special Edition")), i => i.Id == "nexus-page");
        Assert.Equal("https://www.nexusmods.com/skyrimspecialedition", bare.Tip);

        using var http = new HttpClient(new OfflineHandler());
        var wine = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http,
            new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(Path.Combine(_root, "app-records.json")), wine,
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        fx.Vm.Parity = services.Parity;
        Assert.NotNull(services.Parity!.NexusLinks.Games); // the app wires Nexus's game list

        var page = Assert.Single(fx.Vm.CardMenuFor(Row(fx, "The Elder Scrolls V: Skyrim Special Edition")), i => i.Id == "nexus-page");
        Assert.Equal("https://www.nexusmods.com/skyrimspecialedition", page.Tip);
        Assert.Equal(UiLocalizationService.Text("Parity.NexusPage"), page.Label);
        Assert.True(page.Command.CanExecute(null));
        Assert.DoesNotContain(fx.Vm.CardMenuFor(Row(fx, "Plain Quest")), i => i.Id == "nexus-page"); // no page known: left out
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }

    private static GameRowViewModel Row(ViewModelFixture fx, string name) => fx.Vm.Games.Single(row => row.Name == name);

    private static string LinuxGameLibraryStoreKey(ViewModelFixture fx, string name)
        => RHI.Platform.Linux.Settings.LinuxGameLibraryStore.GameKey(Row(fx, name).Game);

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = "Wine", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Wine", LaunchCommand = "wine Game.exe"
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
