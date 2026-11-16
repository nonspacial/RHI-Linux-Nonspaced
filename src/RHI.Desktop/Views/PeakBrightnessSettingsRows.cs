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
/// w5/ini-presets (#182): Settings → ReShade → RenoDX peak brightness (upstream "HDR &amp; Peak Brightness"): the nits
/// value, "Auto" (from the monitor: its EDID, or the peak set in KDE's display settings; several monitors show a picker),
/// whether installs write it (Off / On) into which RenoDX presets (1–3; the preset boxes show only while it is on), and
/// "Apply to all games". The Settings dialog reads <see cref="ReadInto"/>.
/// </summary>
internal sealed class PeakBrightnessSettingsRows
{
    private readonly NumericUpDown _nits;
    private readonly ComboBox _enabled;
    private readonly CheckBox[] _presets;
    private readonly WrapPanel _presetRow;

    public PeakBrightnessSettingsRows(LinuxAppSettings settings, IniPresetsPresenter presenter, Action<string> showStatus)
    {
        _nits = new NumericUpDown
        {
            Name = "PeakNits", Minimum = 0, Maximum = RenoDxPeakNits.MaxNits, Increment = 10, FormatString = "0",
            Value = settings.PeakNits, MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(_nits, UiLocalizationService.Text("Ini.PeakNitsTip"));
        ToolTip.SetShowDelay(_nits, 2000);
        var auto = new Button { Name = "PeakNitsAuto", Content = UiLocalizationService.Text("Ini.PeakAuto"), Padding = new Thickness(12, 6), Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(auto, UiLocalizationService.Text("Ini.PeakAutoTip"));
        ToolTip.SetShowDelay(auto, 2000);
        // Several monitors: a picker appears under the value (it exists only then, with its monitors in it).
        var monitorHost = new StackPanel();
        auto.Click += (_, _) =>
        {
            var peaks = presenter.DisplayPeaks();
            monitorHost.Children.Clear();
            if (peaks.Count == 0)
            {
                showStatus(UiLocalizationService.Text("Ini.PeakAutoNone"));
                return;
            }
            _nits.Value = peaks[0].PeakNits;
            showStatus(UiLocalizationService.Format("Ini.PeakAutoFound", Label(peaks[0])));
            if (peaks.Count < 2) return;
            var monitors = new ComboBox { Name = "PeakNitsMonitor", MinWidth = 220, Margin = new Thickness(0, 6, 0, 0), ItemsSource = peaks.Select(Label).ToList(), SelectedIndex = 0 };
            monitors.SelectionChanged += (_, _) =>
            {
                if (monitors.SelectedIndex >= 0 && monitors.SelectedIndex < peaks.Count) _nits.Value = peaks[monitors.SelectedIndex].PeakNits;
            };
            monitorHost.Children.Add(monitors);
        };

        _enabled = new ComboBox { Name = "PeakNitsEnabled", ItemsSource = OptionLists.OffOn.Items, SelectedItem = OptionLists.OffOn.Display(settings.PeakNitsEnabled), MinWidth = 140 };
        ToolTip.SetTip(_enabled, UiLocalizationService.Text("Ini.PeakEnabledTip"));
        ToolTip.SetShowDelay(_enabled, 2000);
        _presets = [.. Enumerable.Range(1, 3).Select(n => new CheckBox
        {
            Name = $"PeakNitsPreset{n}",
            Content = UiLocalizationService.Format("Ini.PeakPreset", n),
            IsChecked = settings.PeakNitsPresets.Contains(n),
            Margin = new Thickness(0, 0, 12, 0)
        })];
        _presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var box in _presets) _presetRow.Children.Add(box);
        _enabled.SelectionChanged += (_, _) => SyncPresets();
        SyncPresets();

        var apply = new Button { Name = "PeakNitsApplyAll", Content = UiLocalizationService.Text("Set.WritePeakToAll"), Padding = new Thickness(12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(apply, UiLocalizationService.Text("Ini.PeakApplyTip"));
        ToolTip.SetShowDelay(apply, 2000);
        async Task ApplyAll()
        {
            apply.IsEnabled = false;
            try
            {
                var current = presenter.SavedSettings();
                ReadInto(current);
                presenter.Save(current);
                showStatus(await presenter.ApplyPeakNitsToAllAsync(current));
            }
            finally { apply.IsEnabled = true; }
        }
        // Review finding 5: asks inline with the count before writing into every game.
        var confirm = new InlineConfirmation("PeakNitsApplyAll", nameof(PeakBrightnessSettingsRows));
        apply.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            var current = presenter.SavedSettings();
            ReadInto(current);
            if (presenter.ApplyToAllQuestion(IniApplyAll.PeakNits, current) is { } question) confirm.Ask(question, ApplyAll);
            else await ApplyAll();
        }, nameof(PeakBrightnessSettingsRows));

        var nitsRow = new WrapPanel { Orientation = Orientation.Horizontal };
        nitsRow.Children.Add(_nits);
        nitsRow.Children.Add(auto);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SettingsLayout.SubHeading(UiLocalizationService.Text("Ini.PeakSection"), UiLocalizationService.Text("Ini.PeakNote")));
        panel.Children.Add(Labeled(UiLocalizationService.Text("Ini.PeakNits"), nitsRow));
        panel.Children.Add(monitorHost);
        panel.Children.Add(Labeled(UiLocalizationService.Text("Ini.PeakEnabled"), _enabled));
        panel.Children.Add(_presetRow);
        apply.Classes.Add("settingsPrimary");
        panel.Children.Add(SettingsLayout.Block(apply));
        panel.Children.Add(confirm.Control);
        panel.Children.Add(SettingsLayout.Help(UiLocalizationService.Text("Ini.PeakNote")));
        Control = panel;
    }

    public Control Control { get; }

    public int Nits => (int)(_nits.Value ?? 0);
    public bool Enabled => OptionLists.OffOn.Value(_enabled.SelectedItem?.ToString());
    public List<int> Presets => [.. Enumerable.Range(1, 3).Where(n => _presets[n - 1].IsChecked == true)];

    public void ReadInto(LinuxAppSettings settings)
    {
        settings.PeakNits = Nits;
        settings.PeakNitsEnabled = Enabled;
        settings.PeakNitsPresets = Presets;
    }

    public void Reset(LinuxAppSettings defaults)
    {
        _nits.Value = defaults.PeakNits;
        _enabled.SelectedItem = OptionLists.OffOn.Display(defaults.PeakNitsEnabled);
        for (var n = 1; n <= 3; n++) _presets[n - 1].IsChecked = defaults.PeakNitsPresets.Contains(n);
        SyncPresets();
    }

    // Owner rule: options that cannot apply are hidden — the preset choice only matters while installs write the value.
    private void SyncPresets() => _presetRow.IsVisible = Enabled;

    private static string Label(RHI.Platform.Linux.Hdr.DisplayPeak peak)
        => UiLocalizationService.Format(peak.Source == RHI.Platform.Linux.Hdr.DisplayPeakSource.KdeDisplaySettings ? "Ini.PeakFromKde" : "Ini.PeakFromMonitor",
            peak.Label, peak.PeakNits);

    // #296: the Settings row layout (name left, control right).
    private static Control Labeled(string label, Control control) => SettingsLayout.Row(label, control, tip: ToolTip.GetTip(control) as string);
}
