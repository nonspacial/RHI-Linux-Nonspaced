using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #149: after a damaged library file is restored while RHI runs, the first favourite/hidden/HDR change acts on what the
/// card and controls showed, not on the restored values the user never saw; a filter chosen during a Repair after move
/// that failed part-way is saved over the file as the repair left it; reverse (PT→EN) presentation pairs never rewrite a
/// folder named after a message.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LibraryRestoreFirstChangeTests
{
    [Theory]
    [InlineData("favourite")]
    [InlineData("hidden")]
    public async Task FirstToggleAfterRestore_UsesTheCardsShownState(string flag)
    {
        var tunic = DetectedFolder("Tunic");
        try
        {
            using var fx = await ViewModelFixture.StartAsync([tunic], seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Tunic").Game;
            var card = fx.Vm.SelectedGame;
            var key = LinuxGameLibraryStore.GameKey(card);
            Assert.False(flag == "favourite" ? card.IsFavourite : card.IsHidden);
            var restored = new LinuxGameLibraryState();
            (flag == "favourite" ? restored.FavouriteGames : restored.HiddenGames).Add(key);
            Restore(fx, restored);

            // The card shows the flag off; the user switches it on.
            if (flag == "favourite") fx.Vm.ToggleFavouriteCommand.Execute(null);
            else fx.Vm.ToggleHiddenCommand.Execute(null);

            var saved = fx.Library.Load();
            Assert.Contains(key, flag == "favourite" ? saved.FavouriteGames : saved.HiddenGames);
            Assert.True(flag == "favourite" ? card.IsFavourite : card.IsHidden);
        }
        finally { Directory.Delete(Path.GetDirectoryName(tunic.InstallPath)!, recursive: true); }
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("mode")]
    public async Task FirstHdrChangeAfterRestore_ChangesOnlyTheTouchedControl(string touched)
    {
        var tunic = DetectedFolder("Tunic");
        try
        {
            using var fx = await ViewModelFixture.StartAsync([tunic], seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Tunic").Game;
            var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame);
            Assert.Equal(UiLocalizationService.Text("Ui.HdrOff"), fx.Vm.Runtime.SelectedHdrMode.Display);
            var restored = new LinuxGameLibraryState();
            restored.HdrProfiles[key] = touched == "legacy"
                ? new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope, EnableLegacyWsi = false }
                : new LinuxHdrGameProfile { Mode = LinuxHdrModes.Off, EnableLegacyWsi = true };
            Restore(fx, restored);

            if (touched == "legacy") fx.Vm.Runtime.HdrLegacyWsi = true;
            else fx.Vm.Runtime.SelectedHdrMode = fx.Vm.Runtime.HdrModeOptions.ForDisplay(UiLocalizationService.Text("Ui.HdrNativeWayland"));

            var profile = fx.Library.Load().HdrProfiles[key];
            if (touched == "legacy")
            {
                Assert.Equal(LinuxHdrModes.Gamescope, profile.Mode); // the restored mode is kept
                Assert.True(profile.EnableLegacyWsi);
                Assert.Equal(UiLocalizationService.Text("Ui.HdrGamescope"), fx.Vm.Runtime.SelectedHdrMode.Display);
            }
            else
            {
                Assert.Equal(LinuxHdrModes.NativeWayland, profile.Mode);
                Assert.True(profile.EnableLegacyWsi); // the restored legacy flag is kept
                Assert.True(fx.Vm.Runtime.HdrLegacyWsi);
            }
        }
        finally { Directory.Delete(Path.GetDirectoryName(tunic.InstallPath)!, recursive: true); }
    }

    [Fact]
    public async Task FilterChosenDuringAFailedRepair_IsSavedOverTheFileTheRepairLeft()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var celeste = fx.Game("Celeste");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = "Celeste", InstallPath = celeste.InstallPath, ExecutablePath = celeste.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);

        fx.Vm.LibraryRepairRunning = true;
        fx.Vm.LibraryFilterMode = "Favourites";
        // The repair wrote part of its work into the library file before it failed (files not put back).
        var partly = fx.Library.Load();
        partly.FavouriteGames.Add("moved-key");
        partly.HdrProfiles["moved-key"] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope };
        fx.Library.Save(partly);
        fx.Vm.EndLibraryRepair(repaired: false);

        var saved = fx.Library.Load();
        Assert.Equal("Favourites", saved.FilterMode);
        Assert.Contains("moved-key", saved.FavouriteGames);
        Assert.Equal(LinuxHdrModes.Gamescope, saved.HdrProfiles["moved-key"].Mode);
        Assert.Equal("Celeste", Assert.Single(saved.ManualGames).Name);
    }

    [Theory]
    [InlineData("/mnt/Jogos/A pasta não existe: Teste")]
    [InlineData("/home/u/Link não suportado: x")]
    public void ReversePairs_LeaveAFolderNamedAfterAMessageAlone(string path)
    {
        var english = UiLocalizationService.PresentFor("en-US", "Could not open the game folder: " + path);
        Assert.EndsWith(path, english, StringComparison.Ordinal);
    }

    [Fact]
    public void ReversePairs_StillTranslateAStandaloneFragment()
    {
        var english = UiLocalizationService.PresentFor("en-US", "A pasta não existe: /mnt/Games/Tunic");
        Assert.Equal(RHI.Platform.Linux.Paths.LinuxDesktopOpener.DirectoryMissingPrefix + "/mnt/Games/Tunic", english);
        Assert.Equal("x Fix y", UiLocalizationService.ReplaceAnchored("x Conserto y", "Conserto", "Fix"));
        Assert.Equal("xConserto y", UiLocalizationService.ReplaceAnchored("xConserto y", "Conserto", "Fix"));
        Assert.Equal("x Consertos", UiLocalizationService.ReplaceAnchored("x Consertos", "Conserto", "Fix"));
        Assert.Equal("a/Conserto", UiLocalizationService.ReplaceAnchored("a/Conserto", "Conserto", "Fix"));
    }

    private static void Restore(ViewModelFixture fx, LinuxGameLibraryState state)
    {
        var staged = Path.Combine(fx.Root, "restored-" + Guid.NewGuid().ToString("N") + ".json");
        new LinuxGameLibraryStore(staged).Save(state);
        File.Copy(staged, fx.Library.LibraryPath, overwrite: true);
    }

    private static DetectedGame DetectedFolder(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rhi-detected-{Guid.NewGuid():N}", name)).FullName;
        return new DetectedGame { Name = name, Source = "Heroic", InstallPath = dir, Runner = "Proton" };
    }
}
