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

    // #180: the presenter installs a stored preset into the selected game under the busy gate.
    [Fact]
    public async Task InstallPreset_HoldsTheGate_AndInstallsIntoAGameWithRhiReShade()
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-ini-preset-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "Preset Card", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", SteamAppId = 77002, Source = "Steam"
        };
        using var fx = await ViewModelFixture.StartAsync([game]);
        var card = fx.Vm.AllLibraryGames.Single();
        fx.Vm.SelectedGame = card;
        Assert.False(fx.Vm.IniPresets.PresetsApply()); // ReShade not from RHI: the ⚙ hides the preset rows
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(card), GameName = card.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = bin, Files = []
        });
        var presenter = fx.Vm.IniPresets;
        Directory.CreateDirectory(presenter.PresetsFolder);
        var preset = Path.Combine(presenter.PresetsFolder, "Look.ini");
        File.WriteAllText(preset, "Techniques=SMAA@SMAA.fx\n");
        Assert.Equal([card], presenter.PresetTargets());
        Assert.True(presenter.PresetsApply());
        Assert.Equal([preset], presenter.AvailablePresets());

        using (fx.Vm.Operations.TryBegin("test"))
            Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), await presenter.InstallPresetAsync(preset, withShaderPacks: false, card));
        Assert.False(File.Exists(Path.Combine(bin, "Look.ini")));

        var line = await presenter.InstallPresetAsync(preset, withShaderPacks: false, card);

        Assert.Equal(UiLocalizationService.Present(ReShadePresetService.InstalledPrefix + Path.Combine(bin, "Look.ini")), line);
        Assert.True(File.Exists(Path.Combine(bin, "Look.ini")));
        Assert.False(fx.Vm.IsBusy);
    }

    // #189: the Luma ⚙ writes under the busy gate and reports a localised line.
    [Fact]
    public async Task LumaHdr_HoldsTheGate_AndSaysWhatWasSet()
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-ini-luma-" + Guid.NewGuid().ToString("N"), "Binaries", "Win64");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[Luma]\nEnableHDR=1\nDisplayMode=1\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "Luma Card", InstallPath = Path.GetDirectoryName(Path.GetDirectoryName(bin))!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", SteamAppId = 77003, Source = "Steam", EngineHint = "Unreal Engine 4"
        };
        using var fx = await ViewModelFixture.StartAsync([game]);
        var card = fx.Vm.AllLibraryGames.Single();
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(card), GameName = card.Name, Component = ManagedComponentKind.LumaFramework,
            BinaryDirectory = bin, Files = []
        });
        fx.Vm.SelectedGame = card;

        using (fx.Vm.Operations.TryBegin("test"))
            Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), await fx.Vm.IniPresets.SetLumaHdrAsync(LumaHdrMode.Off));
        Assert.Contains("EnableHDR=1", File.ReadAllText(Path.Combine(bin, "ReShade.ini")));

        var line = await fx.Vm.IniPresets.SetLumaHdrAsync(LumaHdrMode.Off);

        Assert.Equal(UiLocalizationService.Format("Ini.LumaHdrSet", OptionLists.LumaHdrMode.Display(LumaHdrMode.Off)), line);
        Assert.Equal(LumaHdrMode.Off, fx.Vm.IniPresets.LumaSettings()!.Hdr);
    }
}
