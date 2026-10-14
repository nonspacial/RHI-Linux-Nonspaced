using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The confirmation shown before RenoDX is installed on Control Ultimate Edition (upstream
/// <c>ControlUePostInstallService</c> dialog: 2.7.9, the DLSS/RT/SSAO line from 2.8.1 <c>e10099f</c>, the required
/// in-game settings from 2.8.6 <c>ae5603c</c>). Text only, in the UI language at construction; the shell shows
/// <see cref="Message"/> in its confirmation today, and a dialog template can bind the parts (#160).
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
