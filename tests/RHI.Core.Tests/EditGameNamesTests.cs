using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #190 (parity T-15): Edit game's display name, wiki mod name and bitness. Library-state edits (kept across rescans and
/// moves, cleared by Reset); the display name changes only what the card, header, search and sort show (never the
/// library key); the wiki name wins over the manifest's mapping and clearing it brings that back; the bitness is read only
/// through <see cref="GameArchitecture.Of"/>, ahead of the manifest's override and the exe header (upstream's order).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class EditGameNamesTests : IDisposable
{
    private const int TunicAppId = 553420;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-edit-names-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ---- identity resolution ---------------------------------------------------------------------------------------

    [Fact]
    public void WikiName_WinsOverTheManifests_AndClearingItBringsTheManifestsBack_EvenOnTheSameInstance()
    {
        var game = new DetectedGame { Name = "Tunic", Source = "Steam", SteamAppId = TunicAppId, InstallPath = "/g/Tunic", WikiNameOverride = "TUNIC (manifest)" };

        LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { WikiName = " Tunic Wiki " }.Normalize());
        Assert.Equal("Tunic Wiki", game.WikiNameOverride);
        Assert.Equal("Tunic Wiki", game.UserWikiName);

        // Repair after move resolves the same instance again: the manifest's name was captured once, never the user's.
        LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { WikiName = "Other" });
        Assert.Equal("Other", game.WikiNameOverride);
        LinuxGameIdentityService.ApplyDetectedIdentity(game, null);
        Assert.Equal("TUNIC (manifest)", game.WikiNameOverride);
        Assert.Null(game.UserWikiName);
    }

    [Fact]
    public void DisplayName_ChangesWhatTheCardShows_NeverTheNameOrTheKey()
    {
        var split = new DetectedGame
        {
            Name = "Child", Source = "Steam", SteamAppId = 7, InstallPath = "/g/Bundle/Child", IsManifestSplitChild = true
        };
        var keyBefore = LinuxGameLibraryStore.GameKey(split);
        var sessionBefore = LinuxGameLibraryStore.SessionKey(split);

        LinuxGameIdentityService.ApplyDetectedIdentity(split, new LinuxGameEditOverride { DisplayName = "  My Child  " }.Normalize());

        Assert.Equal("My Child", split.DisplayName());
        Assert.Equal("Child", split.Name);
        Assert.Equal(keyBefore, LinuxGameLibraryStore.GameKey(split)); // a split child's key carries its name
        Assert.Equal(sessionBefore, LinuxGameLibraryStore.SessionKey(split));
        LinuxGameIdentityService.ApplyDetectedIdentity(split, null);
        Assert.Equal("Child", split.DisplayName());
    }

    [Fact]
    public void Bitness_UserChoiceWinsOverTheManifestAndTheHeader_AutomaticFallsBack()
    {
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "Game.exe");
        File.WriteAllBytes(exe, Pe(x64: true));
        var game = new DetectedGame { Name = "G", InstallPath = _root, ExecutablePath = exe, ForcedArchitecture = PeArchitecture.X64 };
        Assert.Equal(PeArchitecture.X64, GameArchitecture.Of(game));

        LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { Architecture = PeArchitecture.X86 });
        Assert.Equal(PeArchitecture.X86, GameArchitecture.Of(game));
        Assert.Null(game.ForcedArchitecture is PeArchitecture.X86 ? "stored in the manifest field" : null); // never ForcedArchitecture

        game.ForcedArchitecture = null;
        Assert.Equal(PeArchitecture.X86, GameArchitecture.Of(game)); // over the header too
        LinuxGameIdentityService.ApplyDetectedIdentity(game, null);
        Assert.Equal(PeArchitecture.X64, GameArchitecture.Of(game));
        Assert.Equal(PeArchitecture.X86, GameArchitecture.With(game, PeArchitecture.X86));
        Assert.Equal(PeArchitecture.X64, GameArchitecture.Of(game)); // With never changes the card
    }

    [Fact]
    public void Search_FindsTheCardByEitherName_AndByTheChosenBitness()
    {
        var game = new DetectedGame { Name = "Tunic", Source = "Steam", InstallPath = "/g/Tunic" };
        LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { DisplayName = "Fox Adventure", Architecture = PeArchitecture.X86 });

        Assert.True(GameLibraryFilter.MatchesSearch(game, "fox adv"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "tunic"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "32-bit"));
        Assert.False(GameLibraryFilter.MatchesSearch(game, "64-bit"));
    }

    // ---- library state ---------------------------------------------------------------------------------------------

    [Fact]
    public void NamesOnlyEdit_IsKept_RoundTrips_AndFollowsAMove()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var oldDir = Directory.CreateDirectory(Path.Combine(_root, "old", "G")).FullName;
        var game = new DetectedGame { Name = "G", Source = "Manual", InstallPath = oldDir, IsManuallyAdded = true };
        var state = store.Load();
        state.GameEdits[LinuxGameLibraryStore.GameKey(game)] = new LinuxGameEditOverride
        {
            DisplayName = "Shown", WikiName = "Wiki", Architecture = PeArchitecture.X86
        };
        store.Save(state);
        Assert.True(File.ReadAllText(store.LibraryPath).Contains("\"Architecture\": \"X86\"", StringComparison.Ordinal));

        var newDir = Directory.CreateDirectory(Path.Combine(_root, "new", "G")).FullName;
        var moved = new DetectedGame { Name = "G", Source = "Manual", InstallPath = newDir, IsManuallyAdded = true };
        var write = store.StageMove(new GameStateMove(moved, [(oldDir, newDir)], []));
        Assert.NotNull(write);
        RHI.Platform.Linux.Persistence.StagedFileCommit.Commit([write!]);

        var edit = store.Load().GameEdits[LinuxGameLibraryStore.GameKey(moved)];
        Assert.Equal("Shown", edit.DisplayName);
        Assert.Equal("Wiki", edit.WikiName);
        Assert.Equal(PeArchitecture.X86, edit.Architecture);
    }

    [Fact]
    public void Normalize_DropsBlankNamesAndAnUnknownBitness_AndAnEditWithNothingLeftIsEmpty()
    {
        var edit = new LinuxGameEditOverride { DisplayName = " ", WikiName = "\t", Architecture = PeArchitecture.Unknown }.Normalize();
        Assert.True(edit.IsEmpty());
        Assert.False(new LinuxGameEditOverride { DisplayName = "x" }.Normalize().IsEmpty());
        Assert.False(new LinuxGameEditOverride { WikiName = "x" }.Normalize().IsEmpty());
        Assert.False(new LinuxGameEditOverride { Architecture = PeArchitecture.X64 }.Normalize().IsEmpty());
    }

    // ---- Edit game (view model) ------------------------------------------------------------------------------------

    [Fact]
    public async Task EditGameNames_AreSavedWithBlankTargetFields_ShownSortedAndKept_UntilReset()
    {
        var games = new List<DetectedGame> { SteamGame("Tunic", TunicAppId), SteamGame("Celeste", 504230) };
        using var fx = await ViewModelFixture.StartAsync(games);
        await SelectAsync(fx, "Tunic");
        Assert.Equal(["Celeste", "Tunic"], fx.Vm.Games.Select(row => row.Name));

        var (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync("", "", "", null, null,
            names: new GameNameEdit("Arcade Fox", "Tunic (wiki)", PeArchitecture.X64));

        Assert.True(saved, message);
        Assert.Equal("Tunic: changes saved.", message);
        var edit = fx.Library.Load().GameEdits[$"steam:{TunicAppId}"];
        Assert.Equal(("Arcade Fox", "Tunic (wiki)", PeArchitecture.X64), (edit.DisplayName, edit.WikiName, edit.Architecture));
        Assert.Null(edit.PatchExecutablePath);
        var card = fx.Vm.Games.Single(row => row.Game.SteamAppId == TunicAppId);
        Assert.Equal("Arcade Fox", card.Name);
        Assert.Equal("Tunic", card.Game.Name);
        Assert.Equal("Tunic (wiki)", card.Game.WikiNameOverride);
        Assert.Equal(PeArchitecture.X64, GameArchitecture.Of(card.Game));
        Assert.Equal(["Arcade Fox", "Celeste"], fx.Vm.Games.Select(row => row.Name)); // sorted by the shown name

        // A full rescan builds the card again from the detector; the names apply again.
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        card = fx.Vm.Games.Single(row => row.Game.SteamAppId == TunicAppId);
        Assert.Equal("Arcade Fox", card.Name);
        Assert.Equal("Tunic (wiki)", card.Game.WikiNameOverride);
        fx.Vm.SelectedGame = card.Game;
        Assert.Equal("Arcade Fox", fx.Vm.SelectedTitle);

        // The dialog shows the shown name; saving it unchanged keeps it, and typing the detected name clears it.
        await SelectAsync(fx, "Tunic");
        (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync("", "", "", null, null,
            names: new GameNameEdit("Tunic", "Tunic (wiki)", PeArchitecture.X64));
        Assert.True(saved, message);
        Assert.Null(fx.Library.Load().GameEdits[$"steam:{TunicAppId}"].DisplayName);

        var (reset, resetMessage) = await fx.Vm.ManualGames.ResetSelectedGameEditAsync();
        Assert.True(reset, resetMessage);
        Assert.Empty(fx.Library.Load().GameEdits);
        card = fx.Vm.Games.Single(row => row.Game.SteamAppId == TunicAppId);
        Assert.Null(card.Game.WikiNameOverride);
        Assert.Null(card.Game.UserArchitectureOverride);
    }

    [Fact]
    public async Task ABitnessChangeThatMovesTheBitness_IsRefusedWhileComponentsAreInstalled_ASameBitnessChoiceIsNot()
    {
        var tunic = SteamGame("Tunic", TunicAppId);
        tunic.ForcedArchitecture = PeArchitecture.X64; // the manifest says 64-bit
        using var fx = await ViewModelFixture.StartAsync([tunic]);
        await SelectAsync(fx, "Tunic");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fx.Vm.SelectedGame!), GameName = "Tunic",
            Component = ManagedComponentKind.ReShade, BinaryDirectory = tunic.BinaryDirectory!, Version = "6.0"
        });

        var (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync("", "", "", null, null,
            names: new GameNameEdit(null, null, PeArchitecture.X86));
        Assert.False(saved);
        Assert.Equal(UiLocalizationService.Text("Shell.RemoveComponentsBeforeBitness"), message);
        Assert.Empty(fx.Library.Load().GameEdits);

        (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync("", "", "", null, null,
            names: new GameNameEdit(null, null, PeArchitecture.X64));
        Assert.True(saved, message);
        Assert.Equal(PeArchitecture.X64, fx.Library.Load().GameEdits[$"steam:{TunicAppId}"].Architecture);
    }

    [Fact]
    public async Task NothingChangedWithBlankTargetFields_IsStillRefused()
    {
        using var fx = await ViewModelFixture.StartAsync([SteamGame("Tunic", TunicAppId)]);
        await SelectAsync(fx, "Tunic");

        var (saved, _) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync("", "", "", null, null,
            names: new GameNameEdit("Tunic", null, null));

        Assert.False(saved);
        Assert.Empty(fx.Library.Load().GameEdits);
    }

    [Fact]
    public async Task PortugueseUi_ShowsTheNewLines()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Tunic: alterações salvas.", UiLocalizationService.Format("Shell.EditSavedNames", "Tunic"));
            Assert.Contains("arquitetura", UiLocalizationService.Text("Shell.RemoveComponentsBeforeBitness"));
            Assert.Equal("32 bits", RHI.Desktop.ViewModels.OptionLists.Bitness.Display(PeArchitecture.X86));
        }
        finally { UiLocalizationService.Apply("en-US"); }
        await Task.CompletedTask;
    }

    private DetectedGame SteamGame(string name, int appId)
    {
        var install = Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", name)).FullName;
        var exe = Path.Combine(install, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = "Steam", SteamAppId = appId, InstallPath = install, ExecutablePath = exe, BinaryDirectory = install,
            Runner = "Proton"
        };
    }

    private static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
        for (var i = 0; i < 400 && fx.Vm.IsSelectionProbeBusy; i++)
            await Task.Delay(25);
        Assert.False(fx.Vm.IsSelectionProbeBusy);
    }

    /// <summary>A minimal PE: MZ header pointing at a PE signature and a COFF machine field.</summary>
    private static byte[] Pe(bool x64)
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)(x64 ? 0x8664 : 0x014C)).CopyTo(bytes, 0x84);
        return bytes;
    }
}
