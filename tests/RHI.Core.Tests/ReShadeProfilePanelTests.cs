using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// The ReShade profile section as its own panel (epic #89, concern 12): it follows the selection (the game's profile,
/// the global default with none), Settings' "Manage ReShade.ini", and its channel still re-reads ReShade's DLL-name row.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ReShadeProfilePanelTests
{
    [Fact]
    public async Task NoGame_ShowsTheGlobalDefault_AndAGameShowsItsProfile()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var profile = fx.Vm.ReShadeProfile;
        var game = fx.Game("Tunic");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        Assert.NotNull(fx.Vm.SelectedGame);
        Assert.Equal(UiLocalizationService.Format("Vm.Profile", OptionLists.ReShadeChannel.Display("Stable"), ""), profile.ReShadeUpdateText);

        profile.SelectedReShadeChannel = OptionLists.ReShadeChannel.Display("Custom");
        profile.ReShadeCustomDllPath = "/x/ReShade64.dll";
        profile.ShaderCrosireMaster = true;
        Assert.True(profile.IsReShadeCustom);

        fx.Vm.SelectedGame = null;
        var settings = fx.Settings.Load();
        Assert.Equal(OptionLists.ReShadeChannel.Display(settings.ReShadeChannel), profile.SelectedReShadeChannel);
        Assert.Equal("", profile.ReShadeCustomDllPath);
        Assert.False(profile.ShaderCrosireMaster);
        Assert.False(profile.IsReShadeCustom);
        Assert.Equal(UiLocalizationService.Format("Ui.ReShadeGlobalDefault", OptionLists.ReShadeChannel.Display(settings.ReShadeChannel)),
            profile.ReShadeUpdateText);
        Assert.Equal(UiLocalizationService.Text("Vm.SelectGameDetermineCorrectD3DCompilerHelper"), profile.D3DCompilerToolHint);
        Assert.Equal(UiLocalizationService.Text("Vm.SelectGameFirst"), await profile.ApplyGlobalReShadeSettingsToSelectedGameAsync(new LinuxAppSettings(), fx.Vm.IniPresets.Service));
    }

    [Fact]
    public async Task SavedSettings_ManageIni_ReachesThePanel()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        Assert.True(fx.Vm.ReShadeProfile.ReShadeManageIni);
        var settings = fx.Settings.Load();
        settings.ReShadeManageIni = false;
        fx.Vm.Settings.SaveLinuxSettings(settings);
        Assert.False(fx.Vm.ReShadeProfile.ReShadeManageIni);
    }

    [Fact]
    public async Task AChannelChange_RereadsReShadesDllNameRow()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        var picker = fx.Vm.DllNamePicker;
        await picker.Refreshed.WaitAsync(TimeSpan.FromSeconds(30));
        var before = picker.Refreshed;
        fx.Vm.ReShadeProfile.SelectedReShadeChannel = OptionLists.ReShadeChannel.Display("Nightly");
        Assert.NotSame(before, picker.Refreshed);
    }
}
