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

        var apply = new Button { Name = "PeakNitsApplyAll", Content = UiLocalizationService.Text("Ini.ApplyToAllGames"), Padding = new Thickness(12, 6), HorizontalAlignment = HorizontalAlignment.Left };
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
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.PeakSection"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(Labeled(UiLocalizationService.Text("Ini.PeakNits"), nitsRow));
        panel.Children.Add(monitorHost);
        panel.Children.Add(Labeled(UiLocalizationService.Text("Ini.PeakEnabled"), _enabled));
        panel.Children.Add(_presetRow);
        panel.Children.Add(apply);
        panel.Children.Add(confirm.Control);
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.PeakNote"), FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
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

    private static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(control);
        return panel;
    }
}

/// <summary>
/// w5/ini-presets (#182): the RenoDX ⚙ additions (upstream <c>RdxCogButton_Click</c>): "Set maximum nits" for this game
/// (every RenoDX preset in its ReShade.ini, or preset 1) and, for Unreal Engine games, the UE-Extended switch, which
/// reinstalls RenoDX with the other add-on.
/// </summary>
internal static class RenoDxPeakPanel
{
    public static Control Create(IniPresetsPresenter presenter, Action<string> show)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 6) };
        if (presenter.GameHasReShadeIni()) panel.Children.Add(NitsRow(presenter, show)); // no ReShade.ini yet: no presets to write
        if (presenter.OffersUeExtended()) panel.Children.Add(UeExtendedRow(presenter, show));
        return panel;
    }

    private static Control NitsRow(IniPresetsPresenter presenter, Action<string> show)
    {
        var panel = new StackPanel { Spacing = 8 };
        var nits = new NumericUpDown
        {
            Name = "RenoDxGameNits", Minimum = 1, Maximum = RenoDxPeakNits.MaxNits, Increment = 10, FormatString = "0",
            Value = presenter.GameNits() ?? 1000, MinWidth = 150
        };
        var set = new Button { Name = "RenoDxGameNitsSet", Classes = { "rhi" }, Content = UiLocalizationService.Text("Ini.PeakGameSetButton"), Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(set, UiLocalizationService.Text("Ini.PeakGameTip"));
        ToolTip.SetShowDelay(set, 2000);
        set.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            set.IsEnabled = false;
            try { show(await presenter.SetGameNitsAsync((int)(nits.Value ?? 0))); }
            finally { set.IsEnabled = true; }
        }, nameof(RenoDxPeakPanel));
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(nits);
        row.Children.Add(set);
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.PeakGame"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(row);
        return panel;
    }

    private static Control UeExtendedRow(IniPresetsPresenter presenter, Action<string> show)
    {
        var panel = new StackPanel { Spacing = 8 };
        var ue = new ComboBox { Name = "RenoDxUeExtended", ItemsSource = OptionLists.OffOn.Items, SelectedItem = OptionLists.OffOn.Display(presenter.UeExtendedInstalled()), MinWidth = 140 };
        ToolTip.SetTip(ue, UiLocalizationService.Text("Ini.UeExtendedTip"));
        ToolTip.SetShowDelay(ue, 2000);
        var reverting = false;
        ue.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (reverting || ue.SelectedItem is not string chosen) return;
            var on = OptionLists.OffOn.Value(chosen);
            if (on == presenter.UeExtendedInstalled()) return;
            ue.IsEnabled = false;
            try { show(await presenter.SetUeExtendedAsync(on)); }
            finally
            {
                reverting = true;
                ue.SelectedItem = OptionLists.OffOn.Display(presenter.UeExtendedInstalled());
                reverting = false;
                ue.IsEnabled = true;
            }
        }, nameof(RenoDxPeakPanel));
        panel.Children.Add(new TextBlock { Text = "UE-Extended", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(ue);
        return panel;
    }
}
