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
        Assert.All(menu, item => Assert.True(item.IsEnabled, item.Id));

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
        Assert.False(plain.Single(i => i.Id == "open-folder").IsEnabled); // no confirmed game folder yet

        var menu = fx.Vm.CardMenuFor(Row(fx, "Noted Quest"));
        var notes = menu.Last();
        Assert.Equal(("notes", true), (notes.Id, notes.SeparatorBefore));
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)notes.Command).ExecuteAsync(null);

        Assert.Equal(new GameNotesViewModel(UiLocalizationService.Format("Card.NotesTitle", "Noted Quest"), "Use the DX11 renderer.",
            "https://github.com/rhi-tests/notes", "Guide"), shown);
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
