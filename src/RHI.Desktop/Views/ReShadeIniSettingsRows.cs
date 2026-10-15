using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/ini-presets (#181): the Settings → ReShade rows for the effect list style (upstream "Effect list: Tabs/Tree"),
/// "Apply to all games" (screenshot folder, hotkeys, effect list style into every game's ReShade.ini, upstream
/// <c>ApplyScreenshotPath_Click</c>), the INI templates folder and the mass INI deploy (upstream <c>MassDeployHandler</c>),
/// which asks once, inline, before writing to every game. The Settings dialog reads <see cref="UseTabs"/>.
/// </summary>
internal sealed class ReShadeIniSettingsRows
{
    private readonly ComboBox _effectList;

    /// <param name="readReShadeControls">Copies the dialog's screenshot folder, per-game folder and hotkey controls into the
    /// settings given (the rest of the dialog is saved by Save, as before).</param>
    public ReShadeIniSettingsRows(LinuxAppSettings settings, IniPresetsPresenter presenter, Action<LinuxAppSettings> readReShadeControls,
        Func<string, string?> openFolder, Action<string> showStatus)
    {
        // The saved settings with this tab's ReShade values as shown; "Apply to all games" saves them first (upstream).
        LinuxAppSettings Current()
        {
            var current = presenter.SavedSettings();
            readReShadeControls(current);
            current.ReShadeVariableListUseTabs = UseTabs;
            return current;
        }

        _effectList = new ComboBox
        {
            Name = "ReShadeEffectListStyle",
            ItemsSource = OptionLists.ReShadeEffectListStyle.Items,
            SelectedItem = OptionLists.ReShadeEffectListStyle.Display(settings.ReShadeVariableListUseTabs),
            MinWidth = 190
        };

        var applyAll = Button("ReShadeApplyAll", UiLocalizationService.Text("Ini.ApplyToAllGames"), UiLocalizationService.Text("Ini.ApplyToAllGamesTip"));
        async Task ApplyAll()
        {
            applyAll.IsEnabled = false;
            try
            {
                var current = Current();
                presenter.Save(current);
                showStatus(await presenter.ApplyReShadeSettingsToAllAsync(current));
            }
            finally { applyAll.IsEnabled = true; }
        }
        // Review finding 5: asks inline with the count before writing into every game.
        var applyAllConfirm = new InlineConfirmation("ReShadeApplyAll", nameof(ReShadeIniSettingsRows));
        applyAll.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (presenter.ApplyToAllQuestion(IniApplyAll.ReShadeSettings, Current()) is { } question) applyAllConfirm.Ask(question, ApplyAll);
            else await ApplyAll();
        }, nameof(ReShadeIniSettingsRows));

        var open = Button("IniTemplatesFolder", UiLocalizationService.Text("Names.OpenOptiScalerTemplates"),
            UiLocalizationService.Format("Ini.TemplatesFolderTip", presenter.TemplatesFolder));
        open.Click += (_, _) =>
        {
            if ((presenter.PrepareTemplatesFolder() ?? openFolder(presenter.TemplatesFolder)) is { } error) showStatus(error);
        };

        // Mass deploy: one button per file; the first click shows how many games it writes and asks inline.
        var confirmText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var confirmYes = Button("IniDeployAllConfirm", UiLocalizationService.Text("Ini.DeployAllConfirm"), null);
        var confirmNo = Button("IniDeployAllCancel", UiLocalizationService.Text("Win.Cancel"), null);
        var confirmButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        confirmButtons.Children.Add(confirmYes);
        confirmButtons.Children.Add(confirmNo);
        var confirm = new StackPanel { Spacing = 6, IsVisible = false };
        confirm.Children.Add(confirmText);
        confirm.Children.Add(confirmButtons);
        var deployHost = new StackPanel();
        // The one confirmation shows under the row that asked.
        void ShowConfirm(Panel host)
        {
            if (confirm.Parent is Panel previous && !ReferenceEquals(previous, host)) previous.Children.Remove(confirm);
            if (!host.Children.Contains(confirm)) host.Children.Add(confirm);
            confirm.IsVisible = true;
        }
        ManagedComponentKind? pending = null;
        string? pendingPreset = null; // #180: a preset for every game is asked the same way
        confirmNo.Click += (_, _) => { pending = null; pendingPreset = null; confirm.IsVisible = false; };
        var withPacks = new CheckBox { Name = "PresetAllWithPacks", Content = UiLocalizationService.Text("Ini.PresetWithPacks"), IsChecked = true };
        confirmYes.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            confirm.IsVisible = false;
            if (pendingPreset is { } preset)
            {
                pendingPreset = null;
                showStatus(await presenter.InstallPresetToAllAsync(preset, withPacks.IsChecked == true));
                return;
            }
            if (pending is not { } kind) return;
            pending = null;
            // The question named the files that changed since RHI wrote them: confirmed, each is kept as a backup (review 2).
            showStatus(await presenter.DeployToAllAsync(kind, Current(), replaceChanged: true));
        }, nameof(ReShadeIniSettingsRows));

        var deployButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var kind in new[] { ManagedComponentKind.ReShade, ManagedComponentKind.ReLimiter, ManagedComponentKind.DisplayCommander })
        {
            var file = IniPresetsPresenter.IniFileName(kind);
            var button = Button("IniDeployAll" + kind, file, UiLocalizationService.Text(kind == ManagedComponentKind.ReShade ? "Ini.DeployReShadeTip" : "Ini.DeployAddonTip"));
            button.Click += (_, _) =>
            {
                var count = presenter.DeployCandidates(kind);
                if (count == 0)
                {
                    confirm.IsVisible = false;
                    showStatus(UiLocalizationService.Format("Ini.BatchNone", file));
                    return;
                }
                pending = kind;
                pendingPreset = null;
                var changed = kind == ManagedComponentKind.ReShade ? 0 : presenter.DeployChangedCount(kind);
                confirmText.Text = UiLocalizationService.Format(kind == ManagedComponentKind.ReShade ? "Ini.DeployAllReShadeAsk" : "Ini.DeployAllAddonAsk", file, count)
                                   + (changed > 0 ? " " + UiLocalizationService.Format("Ini.DeployAllChangedNote", changed, file) : "");
                ShowConfirm(deployHost);
            };
            deployButtons.Children.Add(button);
        }

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Labeled(UiLocalizationService.Text("Ini.EffectListStyle"), _effectList));
        panel.Children.Add(applyAll);
        panel.Children.Add(applyAllConfirm.Control);
        panel.Children.Add(Note(UiLocalizationService.Text("Ini.ApplyToAllGamesNote")));
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.TemplateSection"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(open);
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.DeployAllHeading"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(deployButtons);
        panel.Children.Add(deployHost);
        panel.Children.Add(Note(UiLocalizationService.Text("Ini.TemplatesNote")));

        // #180: one preset into every game whose ReShade RHI manages (upstream MassPresetInstall); hidden without presets.
        var presets = presenter.AvailablePresets();
        var presetPicker = new ComboBox { Name = "PresetAllPicker", ItemsSource = presets.Select(path => Path.GetFileName(path)).ToList(), MinWidth = 220, Margin = new Thickness(0, 0, 8, 6) };
        if (presets.Count > 0) presetPicker.SelectedIndex = 0;
        var presetPanel = new StackPanel { Spacing = 6 };
        var presetAll = Button("PresetAllInstall", UiLocalizationService.Text("Ini.PresetInstallAll"), UiLocalizationService.Text("Ini.PresetInstallAllTip"));
        presetAll.Click += (_, _) =>
        {
            if (presetPicker.SelectedIndex < 0) return;
            var count = presenter.PresetTargets().Count;
            if (count == 0)
            {
                confirm.IsVisible = false;
                showStatus(UiLocalizationService.Format("Ini.BatchNone", presetPicker.SelectedItem?.ToString() ?? ""));
                return;
            }
            pending = null;
            pendingPreset = presets[presetPicker.SelectedIndex];
            confirmText.Text = UiLocalizationService.Format("Ini.PresetAllAsk", Path.GetFileName(pendingPreset), count);
            ShowConfirm(presetPanel);
        };
        var presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        presetRow.Children.Add(presetPicker);
        presetRow.Children.Add(presetAll);
        presetPanel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.PresetSection"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        presetPanel.Children.Add(presetRow);
        presetPanel.Children.Add(withPacks);
        if (presets.Count > 0) panel.Children.Add(presetPanel); // no presets: nothing to pick, the rows are left out
        Control = panel;
    }

    public Control Control { get; }

    public bool UseTabs => OptionLists.ReShadeEffectListStyle.Value(_effectList.SelectedItem?.ToString());

    public void Reset(LinuxAppSettings defaults)
        => _effectList.SelectedItem = OptionLists.ReShadeEffectListStyle.Display(defaults.ReShadeVariableListUseTabs);

    private static Button Button(string name, string text, string? tip)
    {
        var button = new Button { Name = name, Content = text, Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 8, 6), HorizontalAlignment = HorizontalAlignment.Left };
        if (tip is not null)
        {
            ToolTip.SetTip(button, tip);
            ToolTip.SetShowDelay(button, 2000);
        }
        return button;
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(control);
        return panel;
    }
}
