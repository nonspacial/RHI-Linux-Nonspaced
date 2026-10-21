using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// One RenoDX compatibility setting: its label, a 2 s tooltip, and the dropdown. A choice is written at once; a refused
/// write puts the dropdown back on the value that is in the file.
/// </summary>
public sealed partial class RenoDxSettingRow : ObservableObject
{
    private readonly RenoDxCompatibilitySetting _setting;
    private readonly RenoDxCompatibilityOption[] _options;
    private readonly Func<string, string, ComponentOperationResult> _set;
    private readonly Action<string> _show;
    private int _accepted;
    private bool _reverting;

    internal RenoDxSettingRow(RenoDxCompatibilitySetting setting, Func<string, string, ComponentOperationResult> set, Action<string> show)
    {
        _setting = setting;
        _set = set;
        _show = show;
        _options = setting.Options.ToArray();
        Options = _options.Select(option => UiLocalizationService.Present(option.Name)).ToArray();
        var index = Array.FindIndex(_options, option => string.Equals(option.Value, setting.CurrentValue, StringComparison.Ordinal));
        _accepted = selectedIndex = index >= 0 ? index : 0;
        Label = UiLocalizationService.Present(setting.Label);
        Tooltip = RenoDxSettingsDialogViewModel.CompatibilityTooltip(setting);
    }

    public string Label { get; }
    public string Tooltip { get; }
    public IReadOnlyList<string> Options { get; }
    [ObservableProperty] private int selectedIndex;

    partial void OnSelectedIndexChanged(int value)
    {
        if (_reverting || value < 0 || value >= _options.Length) return;
        var result = _set(_setting.Key, _options[value].Value);
        _show(UiLocalizationService.Present(result.Message));
        if (result.Success)
        {
            _accepted = value;
            return;
        }
        _reverting = true;
        try { SelectedIndex = _accepted; }
        finally { _reverting = false; }
    }
}

/// <summary>
/// The RenoDX ⚙ dialog (epic #89: was built in the window's code-behind): the compatibility settings of the selected
/// game's <c>renodx</c> INI section, written at once, with the line that says why there is nothing to change when the
/// section is missing or empty. The peak-brightness / UE-Extended rows (#182) are the ini-presets lane's control,
/// hosted by the view; their result lines go to <see cref="Message"/>.
/// </summary>
public sealed partial class RenoDxSettingsDialogViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    internal static RenoDxSettingsDialogViewModel For(MainWindowViewModel shell, RenoDxCompatibilitySnapshot snapshot)
        => new(snapshot, shell.SelectedGame?.Name, shell.SetRenoDxCompatibilitySetting, shell.IniPresets, shell.RenoDxEngineIni);

    internal RenoDxSettingsDialogViewModel(RenoDxCompatibilitySnapshot snapshot, string? gameName,
        Func<string, string, ComponentOperationResult> set, IniPresetsPresenter? iniPresets = null, RenoDxEngineIniPresenter? engineIni = null)
    {
        IniPresets = iniPresets;
        // w8/renodx (#217): the Engine.ini rows do not depend on ReShade.ini; shown only where they apply.
        EngineIniRows = engineIni?.RowsFor(line => Message = line) ?? [];
        WindowTitle = TF("Win.RenoDxCompatibilityTitle", gameName ?? T("Win.Game"));
        Warning = string.IsNullOrWhiteSpace(snapshot.Warning) ? null : UiLocalizationService.Present(snapshot.Warning);
        IniPath = string.IsNullOrWhiteSpace(snapshot.IniPath) ? null : snapshot.IniPath;
        if (!snapshot.HasRenoDxSection)
            message = Warning ?? T("Win.RunGameOnceRenoDXInstalledGenerate");
        else if (snapshot.Settings.Count == 0)
            message = T("Win.RenodxSectionExistsButNoCompatibility");
        Rows = snapshot.HasRenoDxSection
            ? snapshot.Settings.Select(setting => new RenoDxSettingRow(setting, set, line => Message = line)).ToArray()
            : [];
    }

    public string WindowTitle { get; }
    public string? Warning { get; }
    public bool HasWarning => Warning is not null;
    public IReadOnlyList<RenoDxSettingRow> Rows { get; }
    public bool HasRows => Rows.Count > 0;
    public string? IniPath { get; }
    public bool HasIniPath => IniPath is not null;

    /// <summary>w8/renodx (#217): the Engine.ini On/Off rows (none where nothing applies).</summary>
    public IReadOnlyList<RenoDxEngineIniRow> EngineIniRows { get; }
    public bool HasEngineIniRows => EngineIniRows.Count > 0;

    /// <summary>The #182 rows' presenter (hosted by the view); null in tests.</summary>
    public IniPresetsPresenter? IniPresets { get; }

    /// <summary>Why there is nothing to change, or the last write's result.</summary>
    [ObservableProperty] private string? message;

    /// <summary>The 2 s tooltip of a setting: what it does, then its RenoDX key (and DirectX format for upgrades).</summary>
    internal static string CompatibilityTooltip(RenoDxCompatibilitySetting setting)
    {
        var description = setting.Key switch
        {
            "Upgrade_R10G10B10A2_TYPELESS" => T("Win.N10BitPerChannelRGBResources"),
            "Upgrade_R10G10B10A2_UNORM" => T("Win.Normalized10BitRGBCommonlyUsed"),
            "Upgrade_R11G11B10_FLOAT" => T("Win.CompactFloatingPointRGBFormatCommonly"),
            "Upgrade_R16G16B16A16_TYPELESS" => T("Win.HighPrecisionRGBAResourcesWhoseFinal"),
            "Upgrade_R8G8B8A8_TYPELESS" => T("Win.N8BitRGBAResourcesWhoseFinal"),
            "Upgrade_R8G8B8A8_UNORM" => T("Win.Normalized8BitRGBAColorWithout"),
            "Upgrade_R8G8B8A8_UNORM_SRGB" => T("Win.N8BitRGBAColorSRGBConversion"),
            "Upgrade_CopyDestinations" => T("Win.AllowsUpgradeLogicApplyResourcesUsed"),
            "Upgrade_UseSCRGB" => T("Win.ControlsRenoDXUseScRGBOutputPath"),
            "Upgrade_SwapChainCompatibility" => T("Win.ControlsSwapchainUpgradeCompatibilityMode"),
            "Set_Path" => T("Win.SelectsHDRSDRProcessingPathUsed"),
            "DumpLUTShaders" => T("Win.ControlsLUTShaderExportDiagnostics"),
            _ when setting.ManifestDriven => T("Win.CompatibilitySettingProvidedByRHI"),
            _ => T("Win.AdvancedRenoDXCompatibilitySetting")
        };

        var format = setting.Key.StartsWith("Upgrade_R", StringComparison.OrdinalIgnoreCase)
            ? setting.Key["Upgrade_".Length..]
            : null;
        var technical = format is null
            ? TF("Win.RenoDXKey", setting.Key)
            : TF("Win.DirectXFormatRenoDXKey", format, setting.Key);
        return $"{description}\n\n{technical}";
    }
}
