using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#181: the Settings snapshot carries the effect list style, the INI ⚙ shows where it applies, the presenter's batch.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class IniPresetsPresenterTests
{
    [Fact]
    public async Task SettingsSnapshot_CarriesTheEffectListStyle_SoSavingTheDialogKeepsIt()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        Assert.True(settings.ReShadeVariableListUseTabs);
        settings.ReShadeVariableListUseTabs = false;
        settings.UiLanguage = "en-US";

        fx.Vm.Settings.SaveLinuxSettings(settings);

        Assert.False(fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings.ReShadeVariableListUseTabs);
        Assert.False(fx.Settings.Load().ReShadeVariableListUseTabs);
    }

    [Theory]
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.Installed, true)]
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.UpdateAvailable, true)]
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.Available, false)]
    [InlineData(ManagedComponentKind.ReLimiter, ComponentAvailability.Installed, true)]
    [InlineData(ManagedComponentKind.DisplayCommander, ComponentAvailability.Installed, true)]
    [InlineData(ManagedComponentKind.DisplayCommander, ComponentAvailability.Unsupported, false)]
    [InlineData(ManagedComponentKind.RenoDX, ComponentAvailability.Installed, false)]
    public void IniSettings_ShowOnlyForAnInstalledComponentWithAnIni(ManagedComponentKind kind, ComponentAvailability availability, bool shown)
    {
        var row = new ComponentRowViewModel(new ManagedComponent { Kind = kind, DisplayName = kind.ToString(), Availability = availability }, null!);
        Assert.Equal(shown, row.HasIniSettings);
    }

    [Fact]
    public async Task ApplyToAll_HoldsTheBusyGate_AndReportsTheCounts()
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-ini-presenter-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[OVERLAY]\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "Ini Game", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", SteamAppId = 77001, Source = "Steam"
        };
        using var fx = await ViewModelFixture.StartAsync([game]);
        var settings = fx.Vm.IniPresets.SavedSettings();
        settings.ReShadeVariableListUseTabs = false;

        using (fx.Vm.Operations.TryBegin("test"))
            Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), await fx.Vm.IniPresets.ApplyReShadeSettingsToAllAsync(settings));
        Assert.Equal("[OVERLAY]\n", File.ReadAllText(Path.Combine(bin, "ReShade.ini")));

        var line = await fx.Vm.IniPresets.ApplyReShadeSettingsToAllAsync(settings);

        Assert.Equal(UiLocalizationService.Format("Ini.BatchDone", UiLocalizationService.Text("Ini.ReShadeSettingsWhat"), 1), line);
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Path.Combine(bin, "ReShade.ini")));
        Assert.False(fx.Vm.IsBusy);
    }

    [Fact]
    public void BatchSummary_SaysWhatHappened()
    {
        Assert.Equal(UiLocalizationService.Format("Ini.BatchNone", "x"), IniPresetsPresenter.BatchSummary("x", new IniBatchResult(0, 0, 0)));
        Assert.Equal(UiLocalizationService.Format("Ini.BatchDone", "x", 3), IniPresetsPresenter.BatchSummary("x", new IniBatchResult(3, 0, 0)));
        Assert.Equal(UiLocalizationService.Format("Ini.BatchPartial", "x", 3, 1, 2), IniPresetsPresenter.BatchSummary("x", new IniBatchResult(3, 1, 2)));
    }
}
