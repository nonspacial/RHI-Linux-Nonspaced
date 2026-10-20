using System.Windows.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

public sealed class ComponentRowViewModel(ManagedComponent component, ICommand command)
{
    /// <summary>
    /// Whether the row is listed. Owner rule: options that cannot apply are hidden — RTX Encore (#158) is left out where
    /// it cannot work (not a 64-bit DX11/DX12/Vulkan Windows game with its own DLSS); an RTX Encore found on disk still shows.
    /// </summary>
    public static bool IsShown(ManagedComponent component)
        => !(component.Kind == ManagedComponentKind.RtxEncore && component.Availability == ComponentAvailability.Unsupported);

    public ManagedComponentKind Kind => component.Kind;
    public ComponentAvailability Availability => component.Availability;
    public string? InstalledVersion => component.InstalledVersion;
    public string Name => component.Kind == ManagedComponentKind.Streamline
        ? UiLocalizationService.Text("Ui.StreamlineUpdate")
        : component.DisplayName;
    public string Status
    {
        get
        {
            if (component.Kind == ManagedComponentKind.Streamline)
                return UiLocalizationService.Text(component.Availability == ComponentAvailability.Installed
                    ? "Ui.StatusDetected"
                    : "Ui.StatusNotDetected");

            var baseStatus = component.Availability switch
            {
                ComponentAvailability.Installed when component.ManagedByRhi => $"{UiLocalizationService.Text("Ui.StatusInstalled")} · RHI",
                ComponentAvailability.Installed => $"{UiLocalizationService.Text("Ui.StatusInstalled")} · {UiLocalizationService.Text("Ui.StatusExternal")}",
                ComponentAvailability.UpdateAvailable => UiLocalizationService.Text("Ui.StatusUpdateAvailable"),
                ComponentAvailability.Unsupported => UiLocalizationService.Text("Ui.StatusUnsupported"),
                ComponentAvailability.Available => UiLocalizationService.Text("Ui.StatusAvailable"),
                _ => UiLocalizationService.Text("Ui.StatusUnknown")
            };

            // API-driven add-ons expose their installed version in the picker.
            if (component.Kind is ManagedComponentKind.RenoDXUpgrade or ManagedComponentKind.MfgAdaUnlock or ManagedComponentKind.Dlssg2030Unlock or ManagedComponentKind.DlssEnablerStandalone or ManagedComponentKind.Rtx40MfgStandalone or ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse or ManagedComponentKind.DofFix or ManagedComponentKind.REFramework or ManagedComponentKind.LumaFramework or ManagedComponentKind.RtxEncore)
            {
                // Show the version actually installed on disk. If no installed version is
                // available yet, fall back to the newest resolved version as informational data.
                var version = component.InstalledVersion ?? component.LatestVersion;
                if (!string.IsNullOrWhiteSpace(version))
                    return $"{baseStatus} · {version}";
            }

            return baseStatus;
        }
    }

    /// <summary>Secondary line under the row: installed add-on file name(s) and the mod author, e.g.
    /// "renodx-hogwartslegacy.addon64 · by Marat". Empty when neither is known.</summary>
    public string Credit
    {
        get
        {
            var parts = new List<string>(2);
            if (component.InstalledAddonFiles.Count > 0) parts.Add(string.Join(", ", component.InstalledAddonFiles));
            if (!string.IsNullOrWhiteSpace(component.Author)) parts.Add(UiLocalizationService.Format("Ui.ModAuthorBy", component.Author));
            return string.Join(" · ", parts);
        }
    }

    public bool HasCredit => Credit.Length > 0;

    public bool HasAction => component.Kind is not (ManagedComponentKind.Dlss or ManagedComponentKind.Streamline);
    public string Action => !HasAction
        ? string.Empty
        : component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable
            ? UiLocalizationService.Text("Ui.ActionRemove")
            : UiLocalizationService.Text("Ui.ActionInstall");
    public bool IsInstallAction => component.Availability is not (ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable);
    public bool IsRemoveAction => !IsInstallAction;
    /// <summary>The row's note, then (#178) what the update inclusion leaves out, so the row says why Update All skips it.</summary>
    public string Note => UpdateExclusionNote is { } leftOut
        ? string.IsNullOrWhiteSpace(ComponentNote) ? leftOut : ComponentNote.TrimEnd() + " " + leftOut
        : ComponentNote;
    private string ComponentNote => component.Kind == ManagedComponentKind.Streamline
        ? UiLocalizationService.Text(component.Availability == ComponentAvailability.Installed
            ? "Ui.StreamlineDetectedNote"
            : "Ui.StreamlineMissingNote")
        : UiLocalizationService.Present(component.Note);
    public bool IsActionEnabled => component.ActionAllowed;
    public bool HasRenoDxSettings => component.Kind == ManagedComponentKind.RenoDX
        && component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;
    public bool HasOptiScalerSettings => component.Kind == ManagedComponentKind.OptiScaler
        && component.Availability is not ComponentAvailability.Unsupported;
    public bool HasDlssg2030Settings => component.Kind == ManagedComponentKind.Dlssg2030Unlock
        && component.Availability is not ComponentAvailability.Unsupported;
    public bool HasStandaloneDlssEnablerSettings => component.Kind == ManagedComponentKind.DlssEnablerStandalone
        && component.Availability is not ComponentAvailability.Unsupported;
    public bool HasRtx40MfgSettings => component.Kind == ManagedComponentKind.Rtx40MfgStandalone
        && component.Availability is not ComponentAvailability.Unsupported;
    public bool HasRtxEncoreSettings => component.Kind == ManagedComponentKind.RtxEncore
        && component.Availability is not ComponentAvailability.Unsupported
        && (component.ManagedByRhi || component.Availability == ComponentAvailability.Available); // not for an RTX Encore RHI did not install
    public bool HasReFrameworkSettings => ReFrameworkStoragePresenter.HasSettings(component); // w3/journal hook (#159)
    public string ReFrameworkSettingsTip => ReFrameworkStoragePresenter.SettingsTip; // w3/journal hook (#159)
    // w5 lane rows: component-row members (one member each)
    // w5/update-policy rows
    /// <summary>#178: the held note — left out of Update All (this game / every game), or not followed to the newest DLSS.</summary>
    public string? UpdateExclusionNote => component.UpdateExclusion switch
    {
        UpdateExclusion.ThisGame when component.Kind is ManagedComponentKind.Dlss or ManagedComponentKind.Streamline
            => UiLocalizationService.Text("Upd.RowNotFollowed"),
        UpdateExclusion.Unreadable when component.Kind is ManagedComponentKind.Dlss or ManagedComponentKind.Streamline
            => UiLocalizationService.Text("Upd.RowNotFollowedUnreadable"),
        UpdateExclusion.ThisGame => UiLocalizationService.Text("Upd.RowLeftOutThisGame"),
        UpdateExclusion.Unreadable => UiLocalizationService.Text("Upd.RowLeftOutUnreadable"),
        UpdateExclusion.AllGames => UiLocalizationService.Text("Upd.RowLeftOutAllGames"),
        _ => null
    };
    // w5/catalogue-info rows
    public bool HasInfo => ComponentInfoPresenter.HasInfo(component.Kind, component.Availability); // #184: ⓘ dialog
    public string InfoTip => ComponentInfoPresenter.TipFor(Name); // #184
    // w5/ini-presets rows
    public bool HasIniSettings => IniPresetsPresenter.HasIniSettings(component); // #181: ReShade / ReLimiter / Display Commander INI ⚙
    public string IniSettingsTip => UiLocalizationService.Format("Ini.SettingsTip", component.DisplayName); // #181
    // w5/shell-launch rows
    // w5/reshade-core rows
    // w7 lane rows: component-row members (one member each)
    // w7/smalls rows
    public ComponentInfoHighlightState InfoHighlight { get; } = new(component.DisplayName); // #210: ⓘ highlight, set by ComponentInfoPresenter.FollowRows
    // w7/reshade rows
    // w7/shader-sets rows
    // w7/nvapi-launch rows
    // w7/dxvk-hdr rows
    public ICommand Command => command;
    public ManagedComponentKind CommandParameter => component.Kind;
}
