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
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.Installed, true, true)]
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.UpdateAvailable, true, true)]
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.Available, true, false)]
    [InlineData(ManagedComponentKind.ReLimiter, ComponentAvailability.Installed, true, true)]
    [InlineData(ManagedComponentKind.DisplayCommander, ComponentAvailability.Installed, true, true)]
    [InlineData(ManagedComponentKind.DisplayCommander, ComponentAvailability.Unsupported, true, false)]
    [InlineData(ManagedComponentKind.RenoDX, ComponentAvailability.Installed, true, false)]
    // Review finding 1: a copy the user installed (detected on disk, not RHI's) gets no ⚙.
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.Installed, false, false)]
    [InlineData(ManagedComponentKind.ReLimiter, ComponentAvailability.Installed, false, false)]
    [InlineData(ManagedComponentKind.DisplayCommander, ComponentAvailability.UpdateAvailable, false, false)]
    public void IniSettings_ShowOnlyForAComponentRhiManagesWithAnIni(ManagedComponentKind kind, ComponentAvailability availability, bool managedByRhi, bool shown)
    {
        var row = new ComponentRowViewModel(new ManagedComponent { Kind = kind, DisplayName = kind.ToString(), Availability = availability, ManagedByRhi = managedByRhi }, null!);
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
        fx.Records.Manage(fx.Vm.AllLibraryGames.Single(), ManagedComponentKind.ReShade);
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

    // ini-presets re-review nit 2: the mass deploy's question reads every game's add-on INI off the UI thread; a file it
    // cannot read is a status line, never an exception for the global handler.
    [Fact]
    public async Task DeployQuestion_AnUnreadableAddonIni_IsAStatusLine()
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-ini-question-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(bin);
        var ini = Path.Combine(bin, "relimiter.ini");
        File.WriteAllText(ini, "[FrameLimiter]\nmine=1\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "Question Card", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", SteamAppId = 77005, Source = "Steam"
        };
        using var fx = await ViewModelFixture.StartAsync([game]);
        // The card's analysis writes its "Ready —" line from another thread; a handler reading StatusText then reads that
        // line for both writes (w11 integration: 1 run in many under load). Let the analysis finish before the question.
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome) { if (outcome.Game.Name == game.Name) analysed.TrySetResult(); }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
            if (!(fx.Vm.Session.Game?.Name == game.Name && fx.Vm.Session.Analysis is not null))
                await analysed.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromMinutes(2));
        fx.Records.Manage(fx.Vm.AllLibraryGames.Single(), ManagedComponentKind.ReLimiter);
        File.SetUnixFileMode(ini, UnixFileMode.None);
        // The selection analysis' "Ready —" line can replace ours at any moment: collect every line written instead.
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };
        try
        {
            var question = await fx.Vm.IniPresets.DeployQuestionAsync(ManagedComponentKind.ReLimiter);

            Assert.Equal(0, question.Count);
            Assert.NotNull(question.Error);
            Assert.StartsWith(UiLocalizationService.Format("Ini.DeployFailed", "relimiter.ini", ""), question.Error, StringComparison.Ordinal);
            Assert.Contains(lines, line => line.Contains(question.Error, StringComparison.Ordinal)); // a pending one-shot notice may ride on it
        }
        finally { File.SetUnixFileMode(ini, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    // Review finding 2: the ⚙'s Deploy asks before replacing a relimiter.ini the user changed, keeps a backup, restores it.
    [Fact]
    public async Task DeployAddonIni_AsksForAChangedFile_KeepsABackup_AndRestoresIt()
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-ini-deploy-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(bin);
        var ini = Path.Combine(bin, "relimiter.ini");
        File.WriteAllText(ini, "[FrameLimiter]\nmine=1\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "Deploy Card", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", SteamAppId = 77004, Source = "Steam"
        };
        using var fx = await ViewModelFixture.StartAsync([game]);
        var card = fx.Vm.AllLibraryGames.Single();
        fx.Records.Manage(card, ManagedComponentKind.ReLimiter);
        fx.Vm.SelectedGame = card;
        var presenter = fx.Vm.IniPresets;

        Assert.True(presenter.DeployNeedsConfirmation(ManagedComponentKind.ReLimiter));
        Assert.False(presenter.DeployNeedsConfirmation(ManagedComponentKind.ReShade));
        Assert.Equal(1, presenter.DeployChangedCount(ManagedComponentKind.ReLimiter));
        Assert.Equal(new IniPresetsPresenter.DeployQuestion(1, 1, null), await presenter.DeployQuestionAsync(ManagedComponentKind.ReLimiter));
        Assert.Equal(new IniPresetsPresenter.DeployQuestion(0, 0, null), await presenter.DeployQuestionAsync(ManagedComponentKind.DisplayCommander));
        Assert.Equal(UiLocalizationService.Present(UserIniService.ChangedPrefix + ini), await presenter.DeployForSelectedAsync(ManagedComponentKind.ReLimiter));
        Assert.Equal("[FrameLimiter]\nmine=1\n", File.ReadAllText(ini));
        Assert.Null(presenter.AddonIniBackup(ManagedComponentKind.ReLimiter));

        var line = await presenter.DeployForSelectedAsync(ManagedComponentKind.ReLimiter, replaceChanged: true);

        var backup = presenter.AddonIniBackup(ManagedComponentKind.ReLimiter);
        Assert.NotNull(backup);
        Assert.Equal(UiLocalizationService.Present(UserIniService.DeployedBackupPrefix + backup), line);
        Assert.False(presenter.DeployNeedsConfirmation(ManagedComponentKind.ReLimiter));
        Assert.Equal(UiLocalizationService.Present(UserIniService.RestoredPrefix + ini), await presenter.RestoreAddonIniAsync(ManagedComponentKind.ReLimiter));
        Assert.Equal("[FrameLimiter]\nmine=1\n", File.ReadAllText(ini));
        Assert.False(File.Exists(backup));
        Assert.False(fx.Vm.IsBusy);
    }

    // Review finding 5: each "Apply to all games" names how many games it writes into before it writes.
    [Fact]
    public async Task ApplyToAllQuestion_CountsTheGamesEachButtonWritesInto()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-ini-ask-" + Guid.NewGuid().ToString("N"));
        DetectedGame Game(string name, int appId)
        {
            var bin = Path.Combine(root, name, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[OVERLAY]\n");
            File.WriteAllText(Path.Combine(bin, "relimiter.ini"), "[FrameLimiter]\n");
            var exe = Path.Combine(bin, "Game.exe");
            PeArchitectureDetectorTests.WritePe(exe, 0x8664);
            return new DetectedGame { Name = name, InstallPath = Path.Combine(root, name), BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", SteamAppId = appId, Source = "Steam" };
        }
        using var fx = await ViewModelFixture.StartAsync([Game("Ask One", 77011), Game("Ask Two", 77012), Game("Ask Three", 77013)]);
        var cards = fx.Vm.AllLibraryGames.OrderBy(game => game.Name, StringComparer.Ordinal).ToList(); // One, Three, Two
        fx.Records.Manage(cards[0], ManagedComponentKind.ReShade, ManagedComponentKind.ReLimiter, ManagedComponentKind.RenoDX)
            .Manage(cards[1], ManagedComponentKind.ReShade)
            .Manage(cards[2], ManagedComponentKind.ReShade);
        var presenter = fx.Vm.IniPresets;
        var settings = presenter.SavedSettings();

        Assert.Equal(UiLocalizationService.Format("Ini.ApplyAllAsk", UiLocalizationService.Text("Ini.ReShadeSettingsWhat"), 3),
            presenter.ApplyToAllQuestion(IniApplyAll.ReShadeSettings, settings));
        Assert.Equal(UiLocalizationService.Format("Ini.ApplyAllAsk", UiLocalizationService.Text("Ini.ReLimiterSettingsWhat"), 1),
            presenter.ApplyToAllQuestion(IniApplyAll.ReLimiterSettings, settings));
        settings.PeakNits = 0;
        Assert.Null(presenter.ApplyToAllQuestion(IniApplyAll.PeakNits, settings)); // not set: runs at once and says so
        settings.PeakNits = 1180;
        settings.PeakNitsEnabled = true;
        settings.PeakNitsPresets = [1];
        Assert.Equal(UiLocalizationService.Format("Ini.ApplyAllAsk", UiLocalizationService.Format("Ini.PeakWhat", 1180), 1),
            presenter.ApplyToAllQuestion(IniApplyAll.PeakNits, settings));
        fx.Records.Remove(cards[0], ManagedComponentKind.ReLimiter);
        Assert.Null(presenter.ApplyToAllQuestion(IniApplyAll.ReLimiterSettings, settings));
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

    // Review finding 3: a dropped preset is stored only once the dialog is confirmed, never over a preset already there.
    [Fact]
    public async Task DroppedPreset_CancelStoresNothing_InstallKeepsBothPresets()
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-ini-drop-" + Guid.NewGuid().ToString("N"), "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = "Drop Card", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", SteamAppId = 77005, Source = "Steam"
        };
        using var fx = await ViewModelFixture.StartAsync([game]);
        var card = fx.Vm.AllLibraryGames.Single();
        fx.Records.Manage(card, ManagedComponentKind.ReShade);
        var presenter = fx.Vm.IniPresets;
        Directory.CreateDirectory(presenter.PresetsFolder);
        var mine = Path.Combine(presenter.PresetsFolder, "ReShadePreset.ini");
        File.WriteAllText(mine, "Techniques=Mine@Mine.fx\nMyTweak=1\n");
        var drop = Path.Combine(Path.GetDirectoryName(bin)!, "download", "ReShadePreset.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(drop)!);
        File.WriteAllText(drop, "Techniques=SMAA@SMAA.fx\n");
        IReadOnlyList<DetectedGame>? offered = null;

        var cancelled = await presenter.OfferDroppedPresetAsync(drop, (targets, _) => { offered = targets; return Task.FromResult<(DetectedGame, bool)?>(null); });

        Assert.Null(cancelled);
        Assert.Equal([card], offered);
        Assert.Equal([mine], presenter.AvailablePresets());
        Assert.Equal("Techniques=Mine@Mine.fx\nMyTweak=1\n", File.ReadAllText(mine));
        Assert.False(File.Exists(Path.Combine(bin, "ReShadePreset.ini")));

        var line = await presenter.OfferDroppedPresetAsync(drop, (targets, _) => Task.FromResult<(DetectedGame, bool)?>((targets[0], false)));

        var stored = Path.Combine(presenter.PresetsFolder, "ReShadePreset (2).ini");
        Assert.Equal("Techniques=Mine@Mine.fx\nMyTweak=1\n", File.ReadAllText(mine));
        Assert.Equal("Techniques=SMAA@SMAA.fx\n", File.ReadAllText(stored));
        Assert.StartsWith(UiLocalizationService.Format("Ini.PresetStoredAs", "ReShadePreset.ini", "ReShadePreset (2).ini"), line);
        Assert.True(File.Exists(Path.Combine(bin, "ReShadePreset (2).ini")));
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
