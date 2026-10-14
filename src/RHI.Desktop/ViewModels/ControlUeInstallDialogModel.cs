using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The confirmation shown before RenoDX is installed on Control Ultimate Edition (upstream
/// <c>ControlUePostInstallService</c> dialog: 2.7.9, the DLSS/RT/SSAO line from 2.8.1 <c>e10099f</c>, the required
/// in-game settings from 2.8.6 <c>ae5603c</c>) with its two choices, "Install OptiScaler FG" and "Using HDR?" (2.8.1
/// <c>ded2185</c>/<c>e10099f</c>, #136). Text in the UI language at construction; the window shows <see cref="Message"/>
/// with the two dropdowns, and a plain confirmation (no window hook) means FG = No.
/// </summary>
public sealed class ControlUeInstallDialogModel
{
    public string Title { get; } = UiLocalizationService.Text("Vm.ControlUltimateEditionRenoDXMod");

    /// <summary>"This is NOT an HDR mod."</summary>
    public string Warning { get; } = UiLocalizationService.Text("Names.ControlUeNotHdrMod");

    /// <summary>The two RR strategies and the DLSS/RT/SSAO line.</summary>
    public string Strategies { get; } = UiLocalizationService.Text("Names.ControlUeStrategies");

    public string RequiredSettingsHeading { get; } = UiLocalizationService.Text("Names.ControlUeRequiredSettingsHeading");

    /// <summary>Every in-game setting the RR fix needs, one per line (upstream 2.8.6).</summary>
    public IReadOnlyList<string> RequiredSettings { get; } = Lines("Names.ControlUeRequiredSettings");

    public string InstallAlsoHeading { get; } = UiLocalizationService.Text("Names.ControlUeInstallAlsoHeading");

    /// <summary>What the install changes besides the add-on (DLLs, renderer.ini).</summary>
    public IReadOnlyList<string> InstallAlso { get; } = Lines("Names.ControlUeInstallAlso");

    /// <summary>Linux differences: no driver-profile step; removal restores what RHI still owns.</summary>
    public string LinuxNote { get; } = UiLocalizationService.Text("Names.ControlUeLinuxNote");

    public string AcceptText { get; } = UiLocalizationService.Text("Vm.Install");

    // #136 (upstream 2.8.1 ded2185/e10099f): two choices under the text. "Using HDR?" is open only with FG = Yes and
    // goes back to No when FG is set to No, as upstream.
    public string FrameGenerationLabel { get; } = UiLocalizationService.Text("Names.ControlUeFgLabel");
    public string FrameGenerationTip { get; } = UiLocalizationService.Text("Names.ControlUeFgTip");
    public string HdrLabel { get; } = UiLocalizationService.Text("Names.ControlUeHdrLabel");
    public string HdrTip { get; } = UiLocalizationService.Text("Names.ControlUeHdrTip");

    /// <summary>The choices of both dropdowns, No first (index 0 = No, 1 = Yes).</summary>
    public IReadOnlyList<string> Choices { get; } = [UiLocalizationService.Text("Names.No"), UiLocalizationService.Text("Names.Yes")];

    private bool _installOptiScalerFg;

    /// <summary>"Install OptiScaler FG": No by default.</summary>
    public bool InstallOptiScalerFg
    {
        get => _installOptiScalerFg;
        set
        {
            _installOptiScalerFg = value;
            if (!value) _useHdr = false;
        }
    }

    private bool _useHdr;

    /// <summary>"Using HDR?": only with FG; setting it while FG is No is ignored.</summary>
    public bool UseHdr
    {
        get => _useHdr;
        set => _useHdr = value && _installOptiScalerFg;
    }

    public bool CanChooseHdr => _installOptiScalerFg;

    /// <summary>The whole confirmation as one text, for the plain confirmation dialog.</summary>
    public string Message => string.Join("\n\n",
        Warning,
        Strategies,
        RequiredSettingsHeading + "\n" + string.Join("\n", RequiredSettings.Select(line => "• " + line)),
        InstallAlsoHeading + "\n" + string.Join("\n", InstallAlso.Select(line => "• " + line)),
        LinuxNote);

    private static IReadOnlyList<string> Lines(string key)
        => UiLocalizationService.Text(key).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
