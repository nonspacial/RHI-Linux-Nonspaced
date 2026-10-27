using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>
/// The Settings dialog's layout. The rows and tabs other lanes added are code-built controls: this view puts them into
/// the template's slots (and after the Maintenance tab), and registers how each writes its values into the settings
/// being saved and goes back to the defaults on Reset (<see cref="SettingsRowsHost"/>).
/// </summary>
public sealed partial class SettingsDialogView : UserControl
{
    private bool _composed;

    public SettingsDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        if (this.FindControl<CheckBox>("TrayIconBox") is { } tray)
            tray.Click += (_, _) => (DataContext as SettingsDialogViewModel)?.TrayIconClicked();
        DataContextChanged += (_, _) =>
        {
            if (!_composed && DataContext is SettingsDialogViewModel model) Compose(model);
        };
    }

    private void Fill(string slot, Control row)
    {
        if (this.FindControl<Control>(slot) is { } placeholder) DialogSlot.Fill(placeholder, row);
    }

    private void Compose(SettingsDialogViewModel model)
    {
        _composed = true;
        model.BeginHosting(this); // a rebuilt template's rows replace this model's earlier ones (w6/vmsplit review 2)
        var vm = model.Shell;
        var settings = model.Settings;
        void Show(string line) => model.Status = line;

        var overlayHotkey = new ReShadeHotkeyEditor(settings.OverlayHotkey, "36,0,0,0");
        var screenshotHotkey = new ReShadeHotkeyEditor(settings.ScreenshotHotkey, "44,0,0,0");
        Fill("OverlayHotkeySlot", overlayHotkey.Control);
        Fill("ScreenshotHotkeySlot", screenshotHotkey.Control);
        model.Host(new(
            current => { current.OverlayHotkey = overlayHotkey.Value; current.ScreenshotHotkey = screenshotHotkey.Value; },
            defaults => { overlayHotkey.SetValue(defaults.OverlayHotkey); screenshotHotkey.SetValue(defaults.ScreenshotHotkey); }));

        var optiScalerTemplates = new OptiScalerTemplateSettingsRows(settings, Path.Combine(model.Snapshot.ConfigRoot, "inis"), // w3/dllnames hook (#97)
            model.OpenFolder, Show);
        Fill("OptiScalerTemplatesSlot", optiScalerTemplates.Control);
        model.Host(new(current =>
        {
            current.OptiScalerGpuType = optiScalerTemplates.GpuType;
            current.OptiScalerDlssInputs = optiScalerTemplates.DlssInputs;
        }, optiScalerTemplates.Reset));

        // w5 lane rows: Settings rows (build your row object here, put it in its slot, host its values)
        // w5/update-policy rows
        var updatePolicyRows = new UpdatePolicySettingsRows(settings); // #177 Follow newest DLSS / Streamline, #178 Update All includes
        Fill("FollowNewestSlot", updatePolicyRows.FollowControl);
        Fill("UpdateInclusionSlot", updatePolicyRows.InclusionControl);
        model.Host(new(current =>
        {
            current.FollowNewestDlss = updatePolicyRows.FollowDlss;
            current.FollowNewestStreamline = updatePolicyRows.FollowStreamline;
            current.GlobalSkipReShadeUpdates = updatePolicyRows.Skips(ManagedComponentKind.ReShade);
            current.GlobalSkipRenoDxUpdates = updatePolicyRows.Skips(ManagedComponentKind.RenoDX);
            current.GlobalSkipReLimiterUpdates = updatePolicyRows.Skips(ManagedComponentKind.ReLimiter);
            current.GlobalSkipDisplayCommanderUpdates = updatePolicyRows.Skips(ManagedComponentKind.DisplayCommander);
            current.GlobalSkipOptiScalerUpdates = updatePolicyRows.Skips(ManagedComponentKind.OptiScaler);
            current.GlobalSkipReFrameworkUpdates = updatePolicyRows.Skips(ManagedComponentKind.REFramework);
        }, updatePolicyRows.Reset));
        // w5/catalogue-info rows
        // w5/ini-presets rows
        var iniRows = new ReShadeIniSettingsRows(settings, vm.IniPresets, current => // #181: effect list, apply to all, INI templates
        {
            model.WriteReShadeFields(current);
            current.OverlayHotkey = overlayHotkey.Value;
            current.ScreenshotHotkey = screenshotHotkey.Value;
        }, model.OpenFolder, Show);
        Fill("ReShadeIniSlot", iniRows.Control);
        model.Host(new(current => current.ReShadeVariableListUseTabs = iniRows.UseTabs, iniRows.Reset));
        var relimiterRows = new ReLimiterSettingsRows(settings, vm.IniPresets, Show); // #188
        Fill("ReLimiterSlot", relimiterRows.Control);
        model.Host(new(current =>
        {
            current.ReLimiterOsdHotkey = relimiterRows.OsdHotkey;
            current.ReLimiterSharedPresets = relimiterRows.SharedPresets;
            current.ReLimiterDlssHooks = relimiterRows.DlssHooks;
            current.ReLimiterTargetFps = relimiterRows.TargetFps;
        }, relimiterRows.Reset));
        var peakRows = new PeakBrightnessSettingsRows(settings, vm.IniPresets, Show); // #182
        Fill("PeakBrightnessSlot", peakRows.Control);
        model.Host(new(current =>
        {
            current.PeakNits = peakRows.Nits;
            current.PeakNitsEnabled = peakRows.Enabled;
            current.PeakNitsPresets = peakRows.Presets;
        }, peakRows.Reset));
        // w5/shell-launch rows
        var optiScalerMenuKey = new OptiScalerMenuKeyRow(settings, vm.OptiScalerPresets, vm.Settings, Show); // #187
        Fill("OptiScalerMenuKeySlot", optiScalerMenuKey.Control);
        model.Host(new(current => current.OptiScalerMenuKey = optiScalerMenuKey.Key, optiScalerMenuKey.Reset));
        // w5/reshade-core rows
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        var shaderSets = new ShaderSetsSettingsRows(vm, Show); // #179 global shader mode, set and add-ons (apply at once, not on Save)
        Fill("ShaderSetsSlot", shaderSets.Control);
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        // w10 lane rows: Settings rows (build the row, Fill its slot, Host its values)
        // w10/dlss-presets rows
        // w10/archives rows
        // w10/journal-holds rows
        // w11 lane rows: Settings rows (build the row, Fill its slot, Host its values)
        // w11/download-idle rows
        // w11/nr-gpu rows
        // w11/nightly-switch rows
        // w11/nr-check rows
        // w12 lane rows: Settings rows (build the row, Fill its slot, Host its values)
        // w12/mfg rows
        // w12/shaders rows
        // w12/update-all rows
        // w12/github-cache rows
        // w12/luma rows
        // w12/epic rows
        // w12/nr-logs rows
        // w12/pins rows

        // Tabs other lanes added, after Maintenance.
        if (this.FindControl<TabControl>("Tabs") is not { } tabs) return;
        tabs.Items.Add(XboxFoldersSettingsTab.Create(vm.Settings.XboxFolders, Show, model.PickFolder)); // w3/scan Settings row (#90)
        tabs.Items.Add(ParitySettingsTab.Create(vm.Settings.Parity)); // w3/parity Settings rows (#100 #163 #99)
        // w5/update-policy rows
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        tabs.Items.Add(SupportSettingsTab.Create(vm.Support, Show, model.OpenFolder, model.CopyText)); // #192 Copy logs, Open logs folder, Export game data
        // w5/reshade-core rows
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        // w10 lane rows: Settings tabs (after Maintenance)
        // w10/dlss-presets rows
        // w10/archives rows
        // w10/journal-holds rows
        // w11 lane rows: Settings tabs (after Maintenance)
        // w11/download-idle rows
        // w11/nr-gpu rows
        // w11/nightly-switch rows
        // w11/nr-check rows
        // w12 lane rows: Settings tabs (after Maintenance)
        // w12/mfg rows
        // w12/shaders rows
        // w12/update-all rows
        // w12/github-cache rows
        // w12/luma rows
        // w12/epic rows
        // w12/nr-logs rows
        // w12/pins rows
    }
}
