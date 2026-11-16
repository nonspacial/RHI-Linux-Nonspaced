using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #291 (upstream 2.8.9 "Hidden games"): a hidden game is never changed by anything that runs over the whole library:
/// Update All, the automatic component pass, Follow newest DLSS / Streamline and Settings' "Apply to all games" buttons.
/// Its own buttons still work.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class HiddenGamesUnattendedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-hidden-" + Guid.NewGuid().ToString("N"));

    public HiddenGamesUnattendedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Unattended_LeavesHiddenGamesOut_EvenFavourites()
    {
        var shown = new DetectedGame { Name = "Shown" };
        var hidden = new DetectedGame { Name = "Hidden", IsHidden = true };
        var hiddenFavourite = new DetectedGame { Name = "Hidden favourite", IsHidden = true, IsFavourite = true };

        Assert.Equal([shown], GameLibraryFilter.Unattended([shown, hidden, hiddenFavourite]));
    }

    private DetectedGame IniGame(string name, int appId)
    {
        var bin = Path.Combine(_root, name, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[OVERLAY]\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        return new DetectedGame { Name = name, InstallPath = Path.Combine(_root, name), BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", SteamAppId = appId, Source = "Steam" };
    }

    private static void Hide(ViewModelFixture fx, string name)
    {
        fx.Vm.LibraryFilterMode = "All Games";
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == name);
        fx.Vm.ToggleHiddenCommand.Execute(null);
        Assert.True(fx.Vm.AllLibraryGames.Single(game => game.Name == name).IsHidden);
    }

    [Fact]
    public async Task ApplyToAll_SkipsHiddenGames_AndTheQuestionCountsOnlyTheOthers()
    {
        using var fx = await ViewModelFixture.StartAsync([IniGame("Shown Game", 77101), IniGame("Hidden Game", 77102)]);
        foreach (var game in fx.Vm.AllLibraryGames) fx.Records.Manage(game, ManagedComponentKind.ReShade);
        Hide(fx, "Hidden Game");
        var settings = fx.Vm.IniPresets.SavedSettings();
        settings.ReShadeVariableListUseTabs = false;

        var question = fx.Vm.IniPresets.ApplyToAllQuestion(IniApplyAll.ReShadeSettings, settings);
        var line = await fx.Vm.IniPresets.ApplyReShadeSettingsToAllAsync(settings);

        Assert.Equal(UiLocalizationService.Format("Ini.ApplyAllAsk", UiLocalizationService.Text("Ini.ReShadeSettingsWhat"), 1), question);
        Assert.Equal(UiLocalizationService.Format("Ini.BatchDone", UiLocalizationService.Text("Ini.ReShadeSettingsWhat"), 1), line);
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Path.Combine(_root, "Shown Game", "bin", "ReShade.ini")));
        Assert.Equal("[OVERLAY]\n", File.ReadAllText(Path.Combine(_root, "Hidden Game", "bin", "ReShade.ini")));
    }

    [Fact]
    public async Task UpdateAll_IsOff_WhenOnlyHiddenGamesHaveRhiComponents()
    {
        using var fx = await ViewModelFixture.StartAsync([IniGame("Only Managed", 77111), IniGame("Unmanaged", 77112)]);
        fx.Records.Manage(fx.Vm.AllLibraryGames.Single(game => game.Name == "Only Managed"), ManagedComponentKind.ReShade);
        await fx.Vm.RefreshLibraryFactsForTestingAsync();
        Assert.True(fx.Vm.Updates.CanUpdateAll);

        Hide(fx, "Only Managed");
        await fx.Vm.RefreshLibraryFactsForTestingAsync();

        Assert.False(fx.Vm.Updates.CanUpdateAll);
    }

    [Fact]
    public async Task FollowNewest_LeavesTheHiddenGamesDlssAlone()
    {
        var shown = DlssGame("follow-shown");
        var hidden = DlssGame("follow-hidden");
        using var fx = await ViewModelFixture.StartAsync([shown, hidden], httpResponse: DlssAutoFollowServiceTests.ManifestHandler.Answer);
        await fx.Vm.Updates.RequestFollowNewest();
        Hide(fx, "follow-hidden");
        var store = new DlssAutoFollowStore(Path.Combine(fx.ConfigDir, "prefs", "dlss-follow.json"));
        store.Save(new(StringComparer.OrdinalIgnoreCase)
        {
            ["dlss"] = new() { Newest = "310.4.0" }, ["dlssd"] = new() { Newest = "310.4.0" }, ["dlssg"] = new() { Newest = "310.4.0" }
        });
        var summary = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(fx.Vm.StatusText) && fx.Vm.StatusText.StartsWith("Follow newest DLSS/Streamline:", StringComparison.Ordinal))
                summary.TrySetResult(fx.Vm.StatusText);
        };

        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.FollowNewestDlss = true;
        fx.Vm.Settings.SaveLinuxSettings(settings);

        Assert.Equal("Follow newest DLSS/Streamline: 1 file(s) updated.", await summary.Task.WaitAsync(TimeSpan.FromMinutes(2)));
        Assert.Equal("310.5.2.0", DlssAutoFollowService.PeFileVersion(Path.Combine(shown.InstallPath, "nvngx_dlss.dll")));
        Assert.Equal("310.4.0.0", DlssAutoFollowService.PeFileVersion(Path.Combine(hidden.InstallPath, "nvngx_dlss.dll")));
        fx.Vm.Updates.StopBackgroundLoop();
    }

    private DetectedGame DlssGame(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.4.0"));
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Source = "Manual", Runner = "Manual Windows" };
    }
}
