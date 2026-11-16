using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Upstream 2.9.4 Game settings: a ⚙ beside the game's title holds Block Steam updates; Remove from RHI is in the Management
/// section (w14/layouts-a, #304), as upstream. Neither is a header action beside Favourite / Hide any more.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class GameSettingsCogTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-cog-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private (DetectedGame Steam, DetectedGame Other, string Manifest) Games()
    {
        var steamapps = Path.Combine(_root, "steamapps");
        var install = Directory.CreateDirectory(Path.Combine(steamapps, "common", "ELDEN RING")).FullName;
        var exe = Path.Combine(install, "eldenring.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var manifest = Path.Combine(steamapps, "appmanifest_1245620.acf");
        File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"appid\"\t\t\"1245620\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"ELDEN RING\"\n}\n");
        var steam = new DetectedGame { Name = "Elden Ring", InstallPath = install, ExecutablePath = exe, BinaryDirectory = install, SteamAppId = 1245620, Source = "Steam", Runner = "Proton" };
        var other = new DetectedGame { Name = "Heroic One", InstallPath = Directory.CreateDirectory(Path.Combine(_root, "heroic")).FullName, Source = "Heroic", Runner = "Wine" };
        return (steam, other, manifest);
    }

    [Fact]
    public async Task ASteamGame_GetsTheSteamRow_AndTheSwitchBlocksAndAllowsUpdates()
    {
        UiLocalizationService.Apply("en-US");
        var (steam, other, manifest) = Games();
        using var fx = await ViewModelFixture.StartAsync([steam, other]);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Elden Ring");

        Assert.True(fx.Vm.ShowGameSettings);
        Assert.Equal("Game settings", fx.Vm.GameSettingsTitle);
        var closed = false;
        var parts = GameSettingsView.Build(fx.Vm, () => closed = true);
        Assert.NotNull(parts.SteamSwitch);
        Assert.DoesNotContain(parts.Root.GetLogicalDescendants().OfType<Control>(), c => c.Name == "RemoveFromRhiButton"); // in Management
        Assert.False(parts.SteamSwitch!.IsChecked);

        parts.SteamSwitch.IsChecked = true;

        Assert.True(fx.Vm.SteamUpdatesBlocked);
        Assert.Equal(0, (int)(File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite));
        parts.SteamSwitch.IsChecked = false;
        Assert.False(fx.Vm.SteamUpdatesBlocked);
        Assert.NotEqual(0, (int)(File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite));
        Assert.False(closed);
    }

    [Fact]
    public async Task ARefusedBlock_SpringsTheSwitchBack()
    {
        UiLocalizationService.Apply("en-US");
        var (steam, other, manifest) = Games();
        using var fx = await ViewModelFixture.StartAsync([steam, other]);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Elden Ring");
        var parts = GameSettingsView.Build(fx.Vm, () => { });
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"4\"", "\"1026\"", StringComparison.Ordinal)); // Steam is updating it

        parts.SteamSwitch!.IsChecked = true;

        Assert.False(parts.SteamSwitch.IsChecked);
        Assert.StartsWith(UiLocalizationService.Text("Steam.UpdatingNow"), fx.Vm.StatusText); // a pending notice may follow
    }

    [Fact]
    public async Task ADetectedGameWithoutASteamManifest_HasNoCog_RemoveFromRhiIsInManagement()
    {
        UiLocalizationService.Apply("en-US");
        var (steam, other, _) = Games();
        using var fx = await ViewModelFixture.StartAsync([steam, other]);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Heroic One");
        MainWindowViewModel.RemoveFromRhiPrompt? asked = null;
        fx.Vm.ConfirmRemoveFromRhiAsync = prompt => { asked = prompt; return Task.FromResult<bool?>(null); };

        Assert.False(fx.Vm.ShowGameSettings);
        Assert.True(fx.Vm.CanRemoveFromRhiAny);
        fx.Vm.RemoveFromRhiAnyCommand.Execute(null);
        Assert.NotNull(asked); // the existing confirmation (with its uninstall offer) runs
    }

    [Fact]
    public async Task AGameAddedByHand_HasNoCog_ManagementRemovesIt()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var mine = fx.Game("Mine");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = "Mine", InstallPath = mine.InstallPath, ExecutablePath = mine.ExecutablePath });
        Assert.True(added, message);

        Assert.True(fx.Vm.SelectedGame?.IsManuallyAdded);
        Assert.False(fx.Vm.ShowGameSettings);
    }
}
