using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/ini-presets (#188): Settings → Shaders/Add-ons → ReLimiter (upstream 2.8.7 Settings "ReLimiter"): the on-screen
/// display key, shared presets, DLSS info on the display, a global FPS cap (upstream's VRR presets or a custom value,
/// whose box shows only for Custom) and "Apply to all games". The Settings dialog reads the four values it saves.
/// </summary>
internal sealed class ReLimiterSettingsRows
{
    private readonly ReShadeHotkeyEditor _osdKey;
    private readonly ComboBox _shared;
    private readonly ComboBox _dlssHooks;
    private readonly ComboBox _fps;
    private readonly NumericUpDown _customFps;
    private readonly Action<string> _showStatus;
    private int _savedFps;

    public ReLimiterSettingsRows(LinuxAppSettings settings, IniPresetsPresenter presenter, Action<string> showStatus)
    {
        _showStatus = showStatus;
        _savedFps = settings.ReLimiterTargetFps;
        _osdKey = new ReShadeHotkeyEditor(ReLimiterIniEditor.ToEditorValue(settings.ReLimiterOsdHotkey), "123,0,0,0");
        _shared = Combo("ReLimiterSharedPresets", settings.ReLimiterSharedPresets, "Ini.ReLimiterSharedPresetsTip");
        _dlssHooks = Combo("ReLimiterDlssHooks", settings.ReLimiterDlssHooks, "Ini.ReLimiterDlssHooksTip");
        _fps = new ComboBox { Name = "ReLimiterTargetFps", ItemsSource = OptionLists.ReLimiterTargetFps.Items, MinWidth = 230 };
        ToolTip.SetTip(_fps, UiLocalizationService.Text("Ini.ReLimiterTargetFpsTip"));
        ToolTip.SetShowDelay(_fps, 2000);
        _customFps = new NumericUpDown
        {
            Name = "ReLimiterCustomFps",
            Minimum = ReLimiterIniEditor.MinTargetFps,
            Maximum = ReLimiterIniEditor.MaxTargetFps,
            Increment = 1,
            FormatString = "0",
            MinWidth = 140,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _fps.SelectionChanged += (_, _) => _customFps.IsVisible = OptionLists.ReLimiterTargetFps.Value(_fps.SelectedItem?.ToString()) == OptionLists.CustomTargetFps;
        SetFps(settings.ReLimiterTargetFps);

        var apply = new Button
        {
            Name = "ReLimiterApplyAll",
            Content = UiLocalizationService.Text("Set.WriteReLimiterToAll"),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(apply, UiLocalizationService.Text("Ini.ReLimiterApplyTip"));
        ToolTip.SetShowDelay(apply, 2000);
        async Task ApplyAll()
        {
            apply.IsEnabled = false;
            try
            {
                var current = presenter.SavedSettings();
                ReadInto(current);
                presenter.Save(current);
                showStatus(await presenter.ApplyReLimiterSettingsToAllAsync(current));
            }
            finally { apply.IsEnabled = true; }
        }
        // Review finding 5: asks inline with the count before writing into every game.
        var confirm = new InlineConfirmation("ReLimiterApplyAll", nameof(ReLimiterSettingsRows));
        apply.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            var current = presenter.SavedSettings();
            ReadInto(current);
            if (presenter.ApplyToAllQuestion(IniApplyAll.ReLimiterSettings, current) is { } question) confirm.Ask(question, ApplyAll);
            else await ApplyAll();
        }, nameof(ReLimiterSettingsRows));

        var choices = new StackPanel { Spacing = 8 };
        foreach (var (label, control) in new (string, Control)[]
                 {
                     (UiLocalizationService.Text("Ini.ReLimiterSharedPresets"), _shared),
                     (UiLocalizationService.Text("Ini.ReLimiterDlssHooks"), _dlssHooks)
                 })
        {
            choices.Children.Add(Labeled(label, control));
        }
        var fpsRow = new WrapPanel { Orientation = Orientation.Horizontal };
        _fps.Margin = new Thickness(0, 0, 8, 6);
        fpsRow.Children.Add(_fps);
        fpsRow.Children.Add(_customFps);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SettingsLayout.SubHeading("ReLimiter", UiLocalizationService.Text("Ini.ReLimiterApplyNote")));
        // Upstream 2.9.4 (MainWindow.xaml, Components › ReLimiter): Overlay key, Target FPS, Shared presets, DLSS hooks.
        panel.Children.Add(Labeled(UiLocalizationService.Text("Set.OverlayKey"), _osdKey.Control));
        panel.Children.Add(Labeled(UiLocalizationService.Text("Ini.ReLimiterTargetFps"), fpsRow));
        panel.Children.Add(choices);
        apply.Classes.Add("settingsPrimary");
        panel.Children.Add(SettingsLayout.Block(apply));
        panel.Children.Add(confirm.Control);
        panel.Children.Add(SettingsLayout.Help(UiLocalizationService.Text("Ini.ReLimiterApplyNote")));
        Control = panel;
    }

    public Control Control { get; }

    public string OsdHotkey => ReLimiterIniEditor.FromEditorValue(_osdKey.Value);
    public bool SharedPresets => OptionLists.OffOn.Value(_shared.SelectedItem?.ToString());
    public bool DlssHooks => OptionLists.OffOn.Value(_dlssHooks.SelectedItem?.ToString());

    public int TargetFps
    {
        get
        {
            var selected = OptionLists.ReLimiterTargetFps.Value(_fps.SelectedItem?.ToString());
            if (selected != OptionLists.CustomTargetFps) return selected;
            // #295: an invalid custom value keeps the saved cap and says so (it used to become "no cap" silently).
            var fps = IniPresetsPresenter.CustomTargetFps(_customFps.Value, _savedFps, out var problem);
            if (problem is not null) _showStatus(problem);
            return fps;
        }
    }

    public void ReadInto(LinuxAppSettings settings)
    {
        settings.ReLimiterOsdHotkey = OsdHotkey;
        settings.ReLimiterSharedPresets = SharedPresets;
        settings.ReLimiterDlssHooks = DlssHooks;
        settings.ReLimiterTargetFps = TargetFps;
        _savedFps = settings.ReLimiterTargetFps;
    }

    public void Reset(LinuxAppSettings defaults)
    {
        _osdKey.SetValue(ReLimiterIniEditor.ToEditorValue(defaults.ReLimiterOsdHotkey));
        _shared.SelectedItem = OptionLists.OffOn.Display(defaults.ReLimiterSharedPresets);
        _dlssHooks.SelectedItem = OptionLists.OffOn.Display(defaults.ReLimiterDlssHooks);
        SetFps(defaults.ReLimiterTargetFps);
    }

    // A saved cap that is not one of the presets shows as Custom with its value in the box.
    private void SetFps(int fps)
    {
        var preset = fps == 0 || OptionLists.ReLimiterTargetFps.Values.Contains(fps) ? fps : OptionLists.CustomTargetFps;
        _fps.SelectedItem = OptionLists.ReLimiterTargetFps.Display(preset);
        _customFps.Value = preset == OptionLists.CustomTargetFps ? fps : 165;
        _customFps.IsVisible = preset == OptionLists.CustomTargetFps;
    }

    private static ComboBox Combo(string name, bool value, string tipKey)
    {
        var combo = new ComboBox { Name = name, ItemsSource = OptionLists.OffOn.Items, SelectedItem = OptionLists.OffOn.Display(value), MinWidth = 140 };
        ToolTip.SetTip(combo, UiLocalizationService.Text(tipKey));
        ToolTip.SetShowDelay(combo, 2000);
        return combo;
    }

    // #296: the Settings row layout (name left, control right).
    private static Control Labeled(string label, Control control) => SettingsLayout.Row(label, control, tip: ToolTip.GetTip(control) as string);
}
