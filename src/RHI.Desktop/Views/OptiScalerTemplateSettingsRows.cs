using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// w3/dllnames (#97): the Settings rows for OptiScaler's per-GPU INI templates (upstream 2.8.7 Settings "GPU Type" and
/// "DLSS Inputs", shown only for AMD/Intel) and a button that creates any missing template and opens the INIs folder.
/// The Settings dialog reads <see cref="GpuType"/>/<see cref="DlssInputs"/> into the settings it saves.
/// </summary>
internal sealed class OptiScalerTemplateSettingsRows
{
    private readonly ComboBox _gpu;
    private readonly ComboBox _dlss;
    private readonly StackPanel _dlssPanel;
    private readonly ComboBox _defaultVersion;
    private readonly CheckBox _removeOldNightlies;

    public OptiScalerTemplateSettingsRows(LinuxAppSettings settings, string templatesFolder, Func<string, string?> openFolder, Action<string> showStatus)
    {
        _gpu = new ComboBox
        {
            Name = "OptiScalerGpuType",
            ItemsSource = OptionLists.OptiScalerGpuType.Items,
            SelectedItem = OptionLists.OptiScalerGpuType.Display(settings.OptiScalerGpuType),
            MinWidth = 230
        };
        _dlss = new ComboBox
        {
            Name = "OptiScalerDlssInputs",
            ItemsSource = OptionLists.OptiScalerDlssInputs.Items,
            SelectedItem = OptionLists.OptiScalerDlssInputs.Display(settings.OptiScalerDlssInputs),
            MinWidth = 230
        };
        _dlssPanel = Labeled(UiLocalizationService.Text("Names.OptiScalerDlssInputs"), _dlss);
        _gpu.SelectionChanged += (_, _) => SyncDlssVisibility();
        SyncDlssVisibility();

        // #270 (upstream 2.9.0/2.9.2): Default version, and Remove old Nightly builds (off by default).
        _defaultVersion = new ComboBox
        {
            Name = "OptiScalerDefaultVersion",
            ItemsSource = OptionLists.OptiScalerVariant.Items,
            SelectedItem = DefaultVersionDisplay(settings.OptiScalerDefaultVersion),
            MinWidth = 230
        };
        ToolTip.SetTip(_defaultVersion, UiLocalizationService.Text("Osv.DefaultVersionTip"));
        ToolTip.SetShowDelay(_defaultVersion, 2000);
        _removeOldNightlies = new CheckBox
        {
            Name = "OptiScalerRemoveOldNightlies",
            Content = UiLocalizationService.Text("Osv.RemoveOldNightlies"),
            IsChecked = settings.OptiScalerRemoveOldNightlyBuilds
        };
        ToolTip.SetTip(_removeOldNightlies, UiLocalizationService.Text("Osv.RemoveOldNightliesTip"));
        ToolTip.SetShowDelay(_removeOldNightlies, 2000);

        var open = new Button { Content = UiLocalizationService.Text("Names.OpenOptiScalerTemplates"), Padding = new Thickness(12, 6) };
        open.Click += (_, _) =>
        {
            try
            {
                new OptiScalerIniTemplates(templatesFolder, () => new(OptiScalerGpuType.Nvidia, true)).SeedAll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                showStatus(UiLocalizationService.Present(ex.Message));
                return;
            }
            if (openFolder(templatesFolder) is { } error) showStatus(error);
        };

        var choices = new WrapPanel { Orientation = Orientation.Horizontal };
        var defaultPanel = Labeled(UiLocalizationService.Text("Osv.DefaultVersion"), _defaultVersion);
        defaultPanel.Margin = new Thickness(0, 0, 16, 8);
        choices.Children.Add(defaultPanel);
        var gpuPanel = Labeled(UiLocalizationService.Text("Names.OptiScalerGpuType"), _gpu);
        gpuPanel.Margin = new Thickness(0, 0, 16, 8);
        _dlssPanel.Margin = new Thickness(0, 0, 16, 8);
        choices.Children.Add(gpuPanel);
        choices.Children.Add(_dlssPanel);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Names.OptiScalerTemplateHeading"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(choices);
        panel.Children.Add(_removeOldNightlies);
        panel.Children.Add(open);
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Names.OptiScalerTemplateNote"), FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Control = panel;
    }

    public Control Control { get; }

    public string GpuType => OptionLists.OptiScalerGpuType.Value(_gpu.SelectedItem?.ToString());

    public bool DlssInputs => OptionLists.OptiScalerDlssInputs.Value(_dlss.SelectedItem?.ToString());

    /// <summary>#270: the Default version as saved (the channel's name: Stable, Nightly, DlssNr, F5).</summary>
    public string DefaultVersion => OptionLists.OptiScalerVariant.Value(_defaultVersion.SelectedItem?.ToString()).ToString();

    public bool RemoveOldNightlyBuilds => _removeOldNightlies.IsChecked == true;

    private static string DefaultVersionDisplay(string saved)
        => OptionLists.OptiScalerVariant.Display(Enum.TryParse<OptiScalerVariant>(saved, true, out var variant) ? variant : OptiScalerVariant.Stable);

    public void Reset(LinuxAppSettings defaults)
    {
        _gpu.SelectedItem = OptionLists.OptiScalerGpuType.Display(defaults.OptiScalerGpuType);
        _dlss.SelectedItem = OptionLists.OptiScalerDlssInputs.Display(defaults.OptiScalerDlssInputs);
        _defaultVersion.SelectedItem = DefaultVersionDisplay(defaults.OptiScalerDefaultVersion);
        _removeOldNightlies.IsChecked = defaults.OptiScalerRemoveOldNightlyBuilds;
        SyncDlssVisibility();
    }

    // Upstream shows "DLSS Inputs" only for AMD and Intel: NVIDIA always has DLSS.
    private void SyncDlssVisibility()
        => _dlssPanel.IsVisible = !string.Equals(GpuType, "NVIDIA", StringComparison.OrdinalIgnoreCase);

    private static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(control);
        return panel;
    }
}
