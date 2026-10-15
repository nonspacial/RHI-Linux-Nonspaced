using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// The OptiScaler ⚙, Add game, Edit game and Settings dialogs as view models (epic #89: were built in the window's
/// code-behind), over the real offline shell.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ShellDialogTests
{
    private static async Task<DetectedGame> AddAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
        return fx.Vm.SelectedGame!;
    }

    private static OptiScalerAdvancedSetting IniSetting(string id, string group, string current)
        => new(id, group, id + " label", current, [new("a", "A"), new("b", "B"), new("c", "C")], id == "s1" ? "help" : null);

    [Fact]
    public async Task OptiScaler_NotManaged_SaysSo_AndHasNoIniRows()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Alpha Quest");
        using var dialog = new OptiScalerSettingsDialogViewModel(fx.Vm, new("/g/OptiScaler.ini", false, [IniSetting("s1", "G", "a")]));

        Assert.False(dialog.HasSettings);
        Assert.Empty(dialog.IniItems);
        Assert.Equal(UiLocalizationService.Text("Win.NoRHIManagedOptiScalerSettingsAvailable"), dialog.Message);
        Assert.True(dialog.CanChooseVariant);
        Assert.Null(dialog.Presets);
        Assert.Equal(UiLocalizationService.Text("Win.SelectedVariantSavedPerGameWill"), dialog.VariantNote);
        Assert.Equal(UiLocalizationService.Format("Win.OptiScalerAdvancedTitle", "Alpha Quest"), dialog.WindowTitle);
        Assert.True(dialog.HasDllName); // the game has a folder to install into

        // The channel choice goes to the runtime panel (saved per game) and its line follows.
        var nightly = fx.Vm.Runtime.OptiScalerVariantOptions.For(OptiScalerVariant.Nightly);
        dialog.SelectedVariant = nightly;
        Assert.Same(nightly, fx.Vm.Runtime.SelectedOptiScalerVariant);
        Assert.Equal(RuntimePanelViewModel.OptiScalerChannelText(OptiScalerVariant.Nightly), dialog.ChannelText);
        dialog.SelectedVariant = null; // the dropdown's items reset: not a choice
        Assert.Same(nightly, fx.Vm.Runtime.SelectedOptiScalerVariant);
        Assert.Equal(RuntimePanelViewModel.OptiScalerChannelText(OptiScalerVariant.Nightly),
            OptiScalerSettingsDialogViewModel.ChannelTip.Convert(nightly, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OptiScaler_IniRows_ByGroup_WrittenAtOnce_WithTheDialogsOwnCancel_AndPutBackWhenRefused()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Alpha Quest");
        var pending = new TaskCompletionSource<ComponentOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(string, string)>();
        using var dialog = new OptiScalerSettingsDialogViewModel(fx.Vm,
            new("/g/OptiScaler.ini", true, [IniSetting("s1", "G1", "b"), IniSetting("s2", "G1", "zz"), IniSetting("s3", "G2", "c")], "careful"),
            (id, value) => { writes.Add((id, value)); return pending.Task; });

        Assert.False(dialog.CanChooseVariant); // installed: the channel is locked until removal
        Assert.Equal(UiLocalizationService.Text("Win.InstalledVariantSelectorStaysLockedUntil"), dialog.VariantNote);
        Assert.True(dialog.HasWarning);
        Assert.Null(dialog.Message);
        Assert.Null(dialog.Presets); // not a channel with presets (#187)
        Assert.Equal(["group G1", "s1", "s2", "divider", "group G2", "s3"], dialog.IniItems.Select(item => item switch
        {
            OptiScalerIniGroup g => "group " + g.Text,
            OptiScalerIniDivider => "divider",
            OptiScalerIniSetting s => s.Id,
            _ => "?"
        }));
        var s1 = dialog.IniItems.OfType<OptiScalerIniSetting>().First();
        Assert.Equal(1, s1.SelectedIndex);
        Assert.Equal("help", s1.Help);
        Assert.Equal(0, dialog.IniItems.OfType<OptiScalerIniSetting>().ElementAt(1).SelectedIndex);

        s1.SelectedIndex = 2;
        Assert.Equal(("s1", "c"), writes.Single());
        Assert.False(s1.IsEnabled);
        Assert.False(dialog.ShowCancel); // nothing cancellable holds the gate yet
        using (fx.Vm.Operations.TryBegin("download"))
        {
            Assert.True(dialog.ShowCancel);
            pending.SetResult(ComponentOperationResult.Fail("refused"));
            await s1.Applied.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(dialog.ShowCancel); // done: hidden again
        }
        Assert.True(s1.IsEnabled);
        Assert.Equal("refused", dialog.Message);
        Assert.Equal(1, s1.SelectedIndex); // back to the value in the file
        Assert.Single(writes);

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.SetResult(ComponentOperationResult.Ok("written"));
        s1.SelectedIndex = 0;
        await s1.Applied.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("written", dialog.Message);
        Assert.Equal(0, s1.SelectedIndex);
    }

    [Fact]
    public async Task OptiScaler_ThePresetsRow_IsOfferedForTheStreamlineChannels()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Alpha Quest");
        using var dialog = new OptiScalerSettingsDialogViewModel(fx.Vm,
            new("/g/OptiScaler.ini", true, [IniSetting(OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "G", "a")]));
        Assert.NotNull(dialog.Presets);
        var reopened = 0;
        dialog.ReopenRequested += () => reopened++;
        dialog.RequestReopen();
        Assert.Equal(1, reopened);
    }

    [Fact]
    public async Task AddGame_InfersNameAndRoot_ClosesWhenSaved_AndKeepsARefusalVisible()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        var prefix = Directory.CreateDirectory(Path.Combine(fx.Root, "pfx")).FullName;
        var dialog = new AddGameDialogViewModel(fx.Vm.ManualGames, game.ExecutablePath!,
            new(_ => Task.FromResult<string?>(prefix), _ => Task.FromResult<string?>(null)));
        var closes = 0;
        dialog.CloseRequested += () => closes++;

        Assert.Equal("Tunic", dialog.Name);
        Assert.Equal(game.InstallPath, dialog.InstallPath);
        await dialog.BrowsePrefixCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(prefix, dialog.Prefix);

        dialog.Name = "  ";
        await dialog.AddCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, closes);
        Assert.False(string.IsNullOrWhiteSpace(dialog.Status));
        Assert.True(dialog.CanAct);

        dialog.Name = " Tunic ";
        dialog.LaunchCommand = "  ";
        await dialog.AddCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, closes);
        var added = fx.Vm.AllLibraryGames.Single(g => g.Name == "Tunic");
        Assert.Equal(prefix, added.PrefixPath);
        Assert.Equal("Manual Wine", added.Runner);
    }

    [Fact]
    public async Task EditGame_StartsFromTheGame_DetectsTheFolders_AndSaves()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = await AddAsync(fx, "Tunic");
        var bin = Directory.CreateDirectory(Path.Combine(game.InstallPath, "bin")).FullName;
        var exe = Path.Combine(bin, "Tunic-Win64.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var picked = new List<string>();
        var dialog = new EditGameDialogViewModel(fx.Vm.ManualGames, game,
            new(title => { picked.Add(title); return Task.FromResult<string?>(null); }, _ => Task.FromResult<string?>(exe)));
        var closes = 0;
        dialog.CloseRequested += () => closes++;

        Assert.Equal(UiLocalizationService.Format("Win.EditGame", game.DisplayName()), dialog.WindowTitle);
        Assert.Equal(game.ExecutablePath, dialog.PatchExecutable);
        Assert.Equal(0, dialog.BitnessIndex); // Automatic
        Assert.Equal(new GameNameEdit(game.DisplayName(), game.UserWikiName, null), dialog.Names);

        await dialog.BrowseExecutableCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(exe, dialog.PatchExecutable);
        Assert.Equal(bin, dialog.BinaryDirectory);
        await dialog.BrowseRootCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60)); // cancelled: nothing changes
        Assert.Equal(bin, dialog.BinaryDirectory);
        Assert.Equal([UiLocalizationService.Text("Win.SelectRealGameRoot")], picked);
        dialog.BinaryDirectory = "";
        dialog.DetectBinaryCommand.Execute(null);
        Assert.Equal(bin, dialog.BinaryDirectory);

        dialog.GameRoot = game.InstallPath;
        await dialog.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, closes);
        Assert.Equal(exe, fx.Vm.SelectedGame!.ExecutablePath);

        fx.Vm.SelectedGame = null;
        await dialog.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(UiLocalizationService.Text("Vm.SelectGameFirst"), dialog.Status);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task Settings_ReadsTheForm_HostedRows_AndResetsEverything()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var opened = new List<string>();
        var dialog = new SettingsDialogViewModel(fx.Vm, folder => { opened.Add(folder); return "not opened"; }, _ => Task.FromResult<string?>("/picked"));
        var applied = 0;
        dialog.Applied += () => applied++;
        var hostedResets = new List<LinuxAppSettings>();
        dialog.Host(new(current => current.PeakNits = 777, defaults => hostedResets.Add(defaults)));

        Assert.True(dialog.TrayIconEnabled == dialog.CloseToTrayEnabled);
        dialog.TrayIconEnabled = true;
        dialog.TrayIconClicked();
        dialog.CloseToTray = true;
        dialog.TrayIconEnabled = false; // unticked by the user: close-to-tray clears and locks
        dialog.TrayIconClicked();
        Assert.False(dialog.CloseToTray);
        Assert.False(dialog.CloseToTrayEnabled);

        await dialog.BrowseScreenshotsCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
        dialog.ViewMode = OptionLists.ViewMode.Display("Compact");
        dialog.DefaultDlss.Selected = dialog.DefaultDlss.Items[^1];
        var read = dialog.Read();
        Assert.Equal("/picked", read.ScreenshotPath);
        Assert.Equal("Compact", read.ViewMode);
        Assert.Equal(777, read.PeakNits); // the hosted row wrote its value
        Assert.Equal(4, read.UpdateCheckIntervalHours);
        Assert.Equal(OptionLists.IsDoNotChange(dialog.DefaultDlss.Items[^1]) ? "" : dialog.DefaultDlss.Items[^1], read.DefaultDlssVersion);

        dialog.SaveCommand.Execute(null);
        Assert.Equal(1, applied);
        Assert.Equal("Compact", fx.Settings.Load().ViewMode);
        Assert.Equal("/picked", fx.Settings.Load().ScreenshotPath);

        dialog.OpenCacheCommand.Execute(null);
        Assert.Equal([dialog.Snapshot.CacheRoot], opened);
        Assert.Equal("not opened", dialog.Status);

        dialog.ResetCommand.Execute(null);
        Assert.Equal(2, applied);
        Assert.Single(hostedResets);
        Assert.Equal(OptionLists.ViewMode.Display(new LinuxAppSettings().ViewMode), dialog.ViewMode);
        Assert.Equal("", dialog.ScreenshotPath);
        Assert.Equal(OptionLists.DoNotChangeDisplay(), dialog.DefaultDlss.Selected);
        Assert.Equal(new LinuxAppSettings().ViewMode, fx.Settings.Load().ViewMode);
    }

    [Fact]
    public async Task Settings_ApplyToGame_SavesFirst_ThenSaysWhyNotWithoutAGame()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var dialog = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        Assert.False(dialog.CanApplyToGame);
        dialog.ScreenshotPath = "/shots";
        dialog.ApplyToGameCommand.Execute(null);
        Assert.Equal("/shots", fx.Settings.Load().ScreenshotPath);
        Assert.Equal(UiLocalizationService.Text("Vm.SelectGameFirst"), dialog.Status);
    }
}
