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
/// The OptiScaler ⚙ Presets row (Refs #187, upstream's four slots): per slot a name box, Save (capture this game's
/// current settings into the slot) and the slot's button that applies it here (hidden while the slot is empty). After an
/// apply the ⚙ reopens so every dropdown shows the applied values.
/// </summary>
internal static class OptiScalerPresetsRow
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static Control Create(OptiScalerPresetsPresenter presets, OptiScalerAdvancedSnapshot snapshot, TextBlock message, Action reopen)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var heading = new TextBlock { Text = T("Shell.PresetsHeading"), FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(heading, T("Shell.PresetsTip"));
        ToolTip.SetShowDelay(heading, MiddleEllipsisText.TooltipDelay);
        panel.Children.Add(heading);
        var slots = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        for (var i = 0; i < OptiScalerPresetsPresenter.SlotCount; i++)
            slots.Children.Add(Slot(presets, snapshot, i, message, reopen));
        panel.Children.Add(slots);
        return panel;
    }

    private static Control Slot(OptiScalerPresetsPresenter presets, OptiScalerAdvancedSnapshot snapshot, int index, TextBlock message, Action reopen)
    {
        var name = new TextBox
        {
            Text = presets.Slot(index)?.Name ?? "",
            PlaceholderText = UiLocalizationService.Format("Shell.PresetSlot", index + 1),
            MinWidth = 120, Width = 150, Name = $"OptiScalerPresetName{index + 1}"
        };
        var save = new Button { Content = T("Win.Save"), Padding = new Thickness(8, 4), Name = $"OptiScalerPresetSave{index + 1}" };
        ToolTip.SetTip(save, T("Shell.PresetSaveTip"));
        ToolTip.SetShowDelay(save, MiddleEllipsisText.TooltipDelay);
        var apply = new Button
        {
            Content = presets.Label(index).Replace("_", "__", StringComparison.Ordinal),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4),
            IsVisible = presets.Slot(index) is not null,
            Name = $"OptiScalerPresetApply{index + 1}"
        };
        ToolTip.SetTip(apply, T("Shell.PresetApplyTip"));
        ToolTip.SetShowDelay(apply, MiddleEllipsisText.TooltipDelay);

        void Refresh()
        {
            apply.Content = presets.Label(index).Replace("_", "__", StringComparison.Ordinal);
            apply.IsVisible = presets.Slot(index) is not null;
        }
        void Rename()
        {
            if (string.Equals(name.Text?.Trim() ?? "", presets.Slot(index)?.Name ?? "", StringComparison.Ordinal)) return;
            if (presets.Rename(index, name.Text) is { } refused) message.Text = refused;
            Refresh();
        }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Rename();
        };
        save.Click += (_, _) =>
        {
            Rename();
            message.Text = presets.SaveSlot(index);
            Refresh();
        };
        apply.Click += (_, _) => UiExceptionGuard.Forget(ApplyAsync(), "OptiScalerPresetApply");
        async Task ApplyAsync()
        {
            apply.IsEnabled = save.IsEnabled = false;
            try
            {
                var line = await presets.ApplyAsync(index);
                message.Text = line;
                reopen();
            }
            finally { apply.IsEnabled = save.IsEnabled = true; }
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        top.Children.Add(name);
        top.Children.Add(save);
        Grid.SetColumn(save, 1);
        var slot = new StackPanel { Spacing = 4, Width = 210 };
        slot.Children.Add(top);
        slot.Children.Add(apply);
        return slot;
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
