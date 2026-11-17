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
    private readonly Control _dlssPanel;
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

        // #296 (upstream 2.9.0 Components › ReLimiter and OptiScaler › OptiScaler): Default version, Remove old Nightly
        // builds under it, GPU type, DLSS inputs (AMD and Intel only), then the templates.
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SettingsLayout.SubHeading("OptiScaler", UiLocalizationService.Text("Names.OptiScalerTemplateNote")));
        panel.Children.Add(Labeled(UiLocalizationService.Text("Osv.DefaultVersion"), _defaultVersion));
        panel.Children.Add(SettingsLayout.Block(_removeOldNightlies));
        panel.Children.Add(Labeled(UiLocalizationService.Text("Names.OptiScalerGpuType"), _gpu));
        panel.Children.Add(_dlssPanel);
        _afterDlssInputs = panel.Children.Count;
        panel.Children.Add(Labeled(UiLocalizationService.Text("Names.OptiScalerTemplateHeading"), open));
        panel.Children.Add(SettingsLayout.Help(UiLocalizationService.Text("Names.OptiScalerTemplateNote")));
        _panel = panel;
        Control = panel;
    }

    private readonly StackPanel _panel;
    private int _afterDlssInputs;

    /// <summary>Upstream 2.9.4 (Components › OptiScaler): the overlay key and its Write key to all come right after DLSS inputs.</summary>
    public void PlaceAfterDlssInputs(Control rows) => _panel.Children.Insert(_afterDlssInputs++, rows);

    public Control Control { get; }

    public string GpuType => OptionLists.OptiScalerGpuType.Value(_gpu.SelectedItem?.ToString());

    public bool DlssInputs => OptionLists.OptiScalerDlssInputs.Value(_dlss.SelectedItem?.ToString());

    /// <summary>#270/#271: the Default version as saved (its id: Stable, Nightly, DlssNr, F5, Fork:&lt;id&gt; or Custom:&lt;slug&gt;).</summary>
    public string DefaultVersion => OptionLists.OptiScalerVariant.Value(_defaultVersion.SelectedItem?.ToString()).ToString();

    public bool RemoveOldNightlyBuilds => _removeOldNightlies.IsChecked == true;

    private static string DefaultVersionDisplay(string saved)
        => OptionLists.OptiScalerVariant.Display(OptiScalerVariant.ParseOr(saved, OptiScalerVariant.Stable)); // #271: any version id

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

    private static Control Labeled(string label, Control control) => SettingsLayout.Row(label, control, tip: ToolTip.GetTip(control) as string);
}
