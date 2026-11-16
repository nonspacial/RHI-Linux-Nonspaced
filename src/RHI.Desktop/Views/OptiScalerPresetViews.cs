using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// The OptiScaler ⚙ Presets panel (#269, upstream 2.8.9; the four slots from #187): the recommended presets as one-click
/// buttons (the one matching the game is ticked, the description is the tooltip), then the user's four slots as one
/// dropdown with Apply, Save current (Overwrite for a slot in use) and a "…" button to rename or clear the slot. After an
/// apply the ⚙ reopens so every dropdown shows the applied values. All logic is the presenter's.
/// </summary>
internal static class OptiScalerPresetsRow
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static Control Create(OptiScalerPresetsPresenter presets, OptiScalerAdvancedSnapshot snapshot, Action<string> show, Action reopen)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var heading = new TextBlock { Text = T("Shell.PresetsHeading"), FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(heading, T("Shell.PresetsTip"));
        ToolTip.SetShowDelay(heading, MiddleEllipsisText.TooltipDelay);
        panel.Children.Add(heading);

        var busy = new List<Button>();
        async Task RunAsync(Func<Task<string>> action)
        {
            foreach (var button in busy) button.IsEnabled = false;
            try
            {
                show(await action());
                reopen();
            }
            finally { foreach (var button in busy) button.IsEnabled = true; }
        }

        // Recommended presets
        var recommended = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        var recommendedLabel = new TextBlock { Text = T("Osp.Recommended"), Opacity = 0.78 };
        ToolTip.SetTip(recommendedLabel, T("Osp.RecommendedTip"));
        ToolTip.SetShowDelay(recommendedLabel, MiddleEllipsisText.TooltipDelay);
        foreach (var preset in presets.Recommended)
        {
            var pill = new Button
            {
                Content = (presets.IsActive(preset) ? "✓ " : "") + preset.Name.Replace("_", "__", StringComparison.Ordinal),
                Padding = new Thickness(14, 4),
                CornerRadius = new CornerRadius(15),
                Name = "OptiScalerRecommended" + preset.Id
            };
            ToolTip.SetTip(pill, UiLocalizationService.Present(preset.Description));
            ToolTip.SetShowDelay(pill, MiddleEllipsisText.TooltipDelay);
            pill.Click += (_, _) => UiExceptionGuard.Forget(RunAsync(() => presets.ApplyRecommendedAsync(preset)), "OptiScalerRecommendedPreset");
            busy.Add(pill);
            recommended.Children.Add(pill);
        }
        panel.Children.Add(recommendedLabel);
        panel.Children.Add(recommended);

        // Your presets: one dropdown, Apply, Save current / Overwrite, "…" (rename or clear)
        var slots = new ComboBox { ItemsSource = presets.SlotLabels, SelectedIndex = 0, MinWidth = 180, Name = "OptiScalerPresetSlot" };
        var apply = new Button { Content = T("Win.Apply"), Padding = new Thickness(10, 4), Name = "OptiScalerPresetApply" };
        ToolTip.SetTip(apply, T("Shell.PresetApplyTip"));
        var save = new Button { Padding = new Thickness(10, 4), Name = "OptiScalerPresetSave" };
        ToolTip.SetTip(save, T("Shell.PresetSaveTip"));
        var more = new Button { Content = "…", Padding = new Thickness(10, 4), Name = "OptiScalerPresetMore" };
        ToolTip.SetTip(more, T("Osp.More"));
        foreach (var control in new Control[] { apply, save, more }) ToolTip.SetShowDelay(control, MiddleEllipsisText.TooltipDelay);
        busy.AddRange([apply, save]);

        int Index() => Math.Clamp(slots.SelectedIndex, 0, OptiScalerPresetsPresenter.SlotCount - 1);
        void Refresh()
        {
            var index = Index();
            slots.ItemsSource = presets.SlotLabels;
            slots.SelectedIndex = index;
            var used = presets.Slot(index) is not null;
            apply.IsVisible = used;
            save.Content = T(used ? "Osp.Overwrite" : "Osp.SaveCurrent");
        }
        slots.SelectionChanged += (_, _) =>
        {
            if (slots.SelectedIndex < 0) return;
            var used = presets.Slot(Index()) is not null;
            apply.IsVisible = used;
            save.Content = T(used ? "Osp.Overwrite" : "Osp.SaveCurrent");
        };
        apply.Click += (_, _) => UiExceptionGuard.Forget(RunAsync(() => presets.ApplyAsync(Index())), "OptiScalerPresetApply");
        save.Click += (_, _) =>
        {
            show(presets.SaveSlot(Index()));
            Refresh();
        };

        var name = new TextBox { MinWidth = 160, Name = "OptiScalerPresetName" };
        var rename = new Button { Content = T("Parity.ShaderProfileRename"), Padding = new Thickness(10, 4) };
        var clear = new Button { Content = T("Win.Clear"), Padding = new Thickness(10, 4) };
        var flyoutRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        flyoutRow.Children.Add(name);
        flyoutRow.Children.Add(rename);
        flyoutRow.Children.Add(clear);
        var flyout = new Flyout { Content = flyoutRow };
        more.Flyout = flyout;
        flyout.Opened += (_, _) => name.Text = presets.Slot(Index())?.Name ?? "";
        void Rename()
        {
            if (presets.Rename(Index(), name.Text) is { } refused) show(refused);
            Refresh();
            flyout.Hide();
        }
        rename.Click += (_, _) => Rename();
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true; // Enter must not close the ⚙
            Rename();
        };
        clear.Click += (_, _) =>
        {
            var index = Index();
            show(presets.Clear(index) ?? UiLocalizationService.Format("Osp.Cleared", index + 1));
            Refresh();
            flyout.Hide();
        };

        var yours = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var control in new Control[] { slots, apply, save, more }) yours.Children.Add(control);
        panel.Children.Add(new TextBlock { Text = T("Osp.Yours"), Opacity = 0.78 });
        panel.Children.Add(yours);
        Refresh();
        return panel;
    }
}

/// <summary>
/// Settings → DLSS/Streamline: the OptiScaler overlay key (Refs #187, upstream's hotkey row): the key every INI template and
/// new install carries, and "Apply to all games" for the games RHI's OptiScaler is already on. The dialog's Save stores the
/// choice (<see cref="Key"/>); Apply to all games saves it too, then writes it everywhere.
/// </summary>
internal sealed class OptiScalerMenuKeyRow
{
    private readonly ComboBox _key;

    public OptiScalerMenuKeyRow(LinuxAppSettings settings, OptiScalerPresetsPresenter presets, SettingsViewModel settingsVm, Action<string> showStatus)
    {
        _key = new ComboBox
        {
            Name = "OptiScalerMenuKey",
            ItemsSource = OptionLists.OptiScalerMenuKey.Items,
            SelectedItem = OptionLists.OptiScalerMenuKey.Display(settings.OptiScalerMenuKey),
            MinWidth = 230
        };
        var apply = new Button { Content = UiLocalizationService.Text("Ini.ApplyToAllGames"), Padding = new Thickness(12, 6), Name = "OptiScalerMenuKeyApplyAll" };
        ToolTip.SetTip(apply, UiLocalizationService.Text("Shell.MenuKeyApplyAllTip"));
        ToolTip.SetShowDelay(apply, MiddleEllipsisText.TooltipDelay);
        apply.IsVisible = Key.Length > 0;
        _key.SelectionChanged += (_, _) => apply.IsVisible = Key.Length > 0;
        apply.Click += (_, _) => UiExceptionGuard.Forget(ApplyAsync(), "OptiScalerMenuKeyApplyAll");
        async Task ApplyAsync()
        {
            apply.IsEnabled = false;
            try
            {
                var key = Key;
                showStatus(settingsVm.SaveOptiScalerMenuKey(key) ?? await presets.ApplyMenuKeyToAllAsync(key));
            }
            finally { apply.IsEnabled = true; }
        }

        var label = new TextBlock { Text = UiLocalizationService.Text("Shell.MenuKey"), FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(label, UiLocalizationService.Text("Shell.MenuKeyTip"));
        ToolTip.SetShowDelay(label, MiddleEllipsisText.TooltipDelay);
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        row.Children.Add(_key);
        row.Children.Add(apply);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(label);
        panel.Children.Add(row);
        Control = panel;
    }

    public Control Control { get; }

    /// <summary>The stored value of the chosen key ("" = keep each template's own).</summary>
    public string Key => OptionLists.OptiScalerMenuKey.Value(_key.SelectedItem?.ToString());

    public void Reset(LinuxAppSettings defaults) => _key.SelectedItem = OptionLists.OptiScalerMenuKey.Display(defaults.OptiScalerMenuKey);
}
