using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>One line of a section: a value, a warning (amber) or a smaller detail/fix line; provenance in its ~2 s tooltip.</summary>
public sealed record NrCheckLine(string Text, bool IsWarning = false, bool IsDetail = false, string? Tip = null);

/// <summary>One label/value row of the page.</summary>
public sealed record NrCheckSection(string Id, string Label, IReadOnlyList<NrCheckLine> Lines);

/// <summary>
/// #234 (owner pick: both entry points open this one page): the read-only Check Neural Rendering page for one game —
/// sections in the order GPU · NR DLL · Recommended method · Anti-cheat folders · Log findings, label/value rows, amber
/// warnings, technical detail and provenance only in ~2 s tooltips, Close only (no install button, no install wording).
/// Its layout is <c>Views/Dialogs/NeuralRenderingCheckDialogView.axaml</c>. Every text is worded here, once, at open.
/// </summary>
public sealed partial class NeuralRenderingCheckDialogViewModel : ObservableObject
{
    public const double Width = 640;
    public const double MinWidth = 360;
    public const double MinHeight = 320;

    public const string GpuSection = "gpu";
    public const string DllSection = "dll";
    public const string MethodSection = "method";
    public const string AntiCheatSection = "anti-cheat";
    public const string LogsSection = "logs";

    public NeuralRenderingCheckDialogViewModel(DetectedGame game, NrCheckDiagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(diagnosis);
        Game = game;
        Diagnosis = diagnosis;
        Intro = F("Nrc.Intro", game.Name);
        Sections =
        [
            new(GpuSection, T("Nrc.GpuLabel"), GpuLines(diagnosis.Gpu)),
            new(DllSection, T("Nrc.DllLabel"), DllLines(diagnosis)),
            new(MethodSection, T("Nrc.MethodLabel"), MethodLines(diagnosis.Method)),
            new(AntiCheatSection, T("Nrc.AntiCheatLabel"), AntiCheatLines(diagnosis.AntiCheat, game)),
            new(LogsSection, T("Nrc.LogsLabel"), LogLines(diagnosis)),
        ];
    }

    public DetectedGame Game { get; }
    public NrCheckDiagnosis Diagnosis { get; }

    public string WindowTitle => T("Nrc.Title");
    public string Intro { get; }
    public IReadOnlyList<NrCheckSection> Sections { get; }
    public string Footer => T("Nrc.Footer");

    public event Action? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    // ---- sections ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<NrCheckLine> GpuLines(NrCheckGpu gpu)
    {
        var tip = gpu.DriverVersion is { } driver ? F("Nrc.GpuTip", driver) : T("Nrc.GpuTipNoDriver");
        var lines = new List<NrCheckLine> { new(gpu.Model ?? T("Nrc.GpuNoModel"), Tip: tip) };
        lines.Add(gpu.Generation == RtxGeneration.Unknown
            ? new(T("Nrc.GpuGenerationUnknown"), IsWarning: true, IsDetail: true, Tip: tip)
            : new(F("Nrc.GpuGeneration", NeuralRenderingGpuPolicy.Display(gpu.Generation)), IsDetail: true, Tip: tip));
        return lines;
    }

    private static IReadOnlyList<NrCheckLine> DllLines(NrCheckDiagnosis diagnosis)
    {
        var dll = diagnosis.Dll;
        if (dll.Fit == NrDllFit.NoDll) return [new(T("Nrc.DllNone"))];
        var tip = DllTip(dll);
        var changed = dll.Fit == NrDllFit.ChangedOutsideRhi;
        var lines = new List<NrCheckLine>
        {
            dll.Fit switch
            {
                NrDllFit.Fits => new(F("Nrc.DllFits", dll.Version!), Tip: tip),
                NrDllFit.NotForThisCard => new(F("Nrc.DllNotForCard", dll.Version!), IsWarning: true, Tip: tip),
                NrDllFit.CardUnknown => new(F("Nrc.DllCardUnknown", dll.Version!), Tip: tip),
                // Follow-up F1: Update All's answer for this file — no version, no fit verdict, no swap hint.
                NrDllFit.ChangedOutsideRhi => new(T("Nrc.DllChangedOutside"), IsWarning: true, Tip: tip),
                _ => new(T("Nrc.DllVersionUnknown"), IsWarning: true, Tip: tip)
            },
            new(dll.OwnerName is { } owner ? F(changed ? "Nrc.DllOwnerChanged" : "Nrc.DllOwner", owner) : T("Nrc.DllGameOwn"), IsDetail: true, Tip: tip)
        };
        if (dll.PickedVersion is { } picked) lines.Add(new(F("Nrc.DllPicked", picked), IsDetail: true));
        if (dll.FaultsOnThisDriver) lines.Add(new(T("Nrc.DllFaults"), IsWarning: true, IsDetail: true));
        if (dll.Fit == NrDllFit.NotForThisCard || dll.FaultsOnThisDriver)
        {
            if (dll.LatestForCard is { } latest) lines.Add(new(F("Nrc.DllForCard", latest), IsDetail: true));
            lines.Add(new(F("Nrc.Fix", FixText(NrFixHint.NrDllForCard, diagnosis)), IsDetail: true));
        }
        return lines;
    }

    private static string DllTip(NrCheckDll dll)
    {
        var file = F("Nrc.DllFile", dll.Path ?? "");
        if (dll.Fit == NrDllFit.ChangedOutsideRhi) return file + "\n" + T("Nrc.SourceChanged");
        return dll.VersionSource switch
        {
            InstalledNrVersionSource.Metadata => file + "\n" + T("Nrc.SourceRecord"),
            InstalledNrVersionSource.CacheStamp => file + "\n" + T("Nrc.SourceCache"),
            InstalledNrVersionSource.PeVersion => file + "\n" + T("Nrc.SourcePe"),
            _ => file
        };
    }

    private static IReadOnlyList<NrCheckLine> MethodLines(NrCheckMethod method)
    {
        var tip = T("Nrc.MethodTip");
        var lines = new List<NrCheckLine>
        {
            method.RecommendedName is { } name ? new(name, Tip: tip) : new(T("Nrc.MethodNotYet"), Tip: tip)
        };
        lines.Add(method.InUse.Count > 0
            ? new(F("Nrc.MethodInUse", string.Join(", ", method.InUse)), IsDetail: true)
            : new(T("Nrc.MethodNone"), IsDetail: true));
        return lines;
    }

    private static IReadOnlyList<NrCheckLine> AntiCheatLines(IReadOnlyList<NrCheckAntiCheat> found, DetectedGame game)
    {
        if (found.Count == 0)
        {
            var looked = string.Join("\n", new[] { game.InstallPath, game.BinaryDirectory }
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal));
            return [new(T("Nrc.AntiCheatNone"), Tip: F("Nrc.AntiCheatLookedTip", looked))];
        }
        var tip = string.Join("\n", found.Select(item => item.Path));
        return
        [
            new(F("Nrc.AntiCheatFound", string.Join(", ", found.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase))), Tip: tip),
            new(T("Nrc.AntiCheatInfo"), IsDetail: true, Tip: tip)
        ];
    }

    private static IReadOnlyList<NrCheckLine> LogLines(NrCheckDiagnosis diagnosis)
    {
        var findings = diagnosis.Findings;
        var looked = diagnosis.Logs.Count == 0
            ? T("Nrc.LogsNoFolder")
            : F("Nrc.LogsTip", string.Join("\n", diagnosis.Logs.Select(log => log.Path)));
        var problems = diagnosis.Logs.Where(log => log.State is NrLogState.Unreadable or NrLogState.NotRegular or NrLogState.OutsideGame).ToArray();
        var anyRead = diagnosis.Logs.Any(log => log.State is NrLogState.Read or NrLogState.ReadEnd);
        var lines = new List<NrCheckLine>
        {
            findings.Count switch
            {
                0 when !anyRead && problems.Length == 0 => new(T("Nrc.LogsNoLogs"), Tip: looked),
                0 => new(T("Nrc.LogsNone"), Tip: looked),
                1 => new(T("Nrc.LogsOne"), Tip: looked),
                _ => new(F("Nrc.LogsMany", findings.Count), Tip: looked)
            }
        };
        foreach (var log in diagnosis.Logs)
        {
            foreach (var finding in log.Findings)
            {
                var headline = finding.Line is { } line
                    ? F("Nrc.LogAt", finding.Log, line, SignatureText(finding.Signature))
                    : F("Nrc.LogNearEnd", finding.Log, SignatureText(finding.Signature));
                var tip = log.Path + "\n" + finding.Excerpt;
                lines.Add(new(headline, IsWarning: true, Tip: tip));
                lines.Add(new(F("Nrc.Fix", FixText(finding.Fix, diagnosis)), IsDetail: true, Tip: tip));
            }
        }
        foreach (var log in problems)
            lines.Add(new(F(log.State switch
            {
                NrLogState.NotRegular => "Nrc.LogNotRegular",
                NrLogState.OutsideGame => "Nrc.LogOutside",
                _ => "Nrc.LogUnreadable"
            }, log.Name), IsWarning: true, IsDetail: true, Tip: log.Path));
        return lines;
    }

    internal static string SignatureText(NrLogSignature signature) => T(signature switch
    {
        NrLogSignature.NeuralRenderingSkipped => "Nrc.SigNrSkipped",
        NrLogSignature.FeatureNotSupported => "Nrc.SigFeatureNotSupported",
        NrLogSignature.ShaderCompiler => "Nrc.SigShaderCompiler",
        NrLogSignature.NeuralRenderingNotImplemented => "Nrl.SigNotImplemented",
        NrLogSignature.FlatDepth => "Nrl.SigFlatDepth",
        NrLogSignature.EvaluateCrashAfterDisplayChange => "Nrl.SigEvaluateCrash",
        _ => "Nrc.SigCrash"
    });

    // ---- fix hints (follow-up F2): only a control that is enabled and does that for THIS game, else a plain step ----

    /// <summary>The fix hint's control: a key of <see cref="FixControls"/> (<see cref="NoControl"/>: a plain step).</summary>
    internal const string NoControl = "none";

    /// <summary>
    /// The control a fix hint names for this game, decided from what each control does in the game's state
    /// (<see cref="NrCheckDiagnosis.Controls"/>, worked out by the controls' own rules): the NR DLL list only where it swaps
    /// the owner's DLL in place, the DLSS / Streamline Neural rendering line only where it is enabled, a component row only
    /// where it offers an enabled Remove, the NR Cost Scaler row first while it holds the owner — otherwise a plain step.
    /// </summary>
    internal static string FixControl(NrFixHint hint, NrCheckDiagnosis diagnosis) => Route(hint, diagnosis).Control;

    internal static string FixText(NrFixHint hint, NrCheckDiagnosis diagnosis) => Route(hint, diagnosis).Text;

    private static (string Control, string Text) Route(NrFixHint hint, NrCheckDiagnosis diagnosis)
    {
        var dll = diagnosis.Dll;
        var controls = diagnosis.Controls;
        var latest = dll.LatestForCard;
        // A component row's Remove, only where the row shows one that is enabled; rows not known yet (no analysis): ask
        // for one; a row without an enabled Remove: say so (its note says why), naming nothing to press.
        (string, string) Row(ManagedComponentKind kind, string control, string text)
            => controls.RemovableRows is null ? (NoControl, T("Nrc.FixSelectFirst"))
                : controls.RemovableRows.Contains(kind) ? (control, text)
                : (NoControl, F("Nrc.FixNoRemove", RowName(kind, diagnosis)));

        // #248: plain steps outside RHI — nothing to change in RHI (driver/Proton), ReShade's own in-game overlay, the
        // game's own display options; no RHI control is named.
        switch (hint)
        {
            case NrFixHint.NothingToChangeInRhi: return (NoControl, T("Nrl.FixNothingInRhi"));
            case NrFixHint.GenericDepthInReShade: return (NoControl, T("Nrl.FixGenericDepth"));
            case NrFixHint.RestartWithDisplayModeSet: return (NoControl, T("Nrl.FixRestartDisplayMode"));
        }

        if (hint == NrFixHint.NativeD3DCompiler)
            return ("d3dcompiler", T((controls.Protontricks, controls.Winetricks) switch
            {
                (true, true) => "Nrc.FixD3DCompilerBoth",
                (true, false) => "Nrc.FixD3DCompilerProtontricks",
                (false, true) => "Nrc.FixD3DCompilerWinetricks",
                _ => "Nrc.FixD3DCompilerBrowse"
            }));

        if (hint == NrFixHint.NrDllForCard)
        {
            // Update All's answer: RHI leaves a file changed outside it as it is.
            if (dll.Fit == NrDllFit.ChangedOutsideRhi) return (NoControl, T("Nrc.FixChangedOutside"));
            if (dll.Owner is { } owner)
            {
                if (controls.HeldByScaler.Contains(owner)) return ("nr-cost-scaler-remove", T("Nrc.FixScalerFirst"));
                return owner switch
                {
                    ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder when controls.NrDllListSwaps
                        => ("nr-dll-list", latest is not null ? F("Nrc.FixNrDllList", latest) : T("Nrc.FixNrDllListAny")),
                    // An older add-on-only DLSS5 Feeder: the list only saves the choice, taken when the Feeder is added again.
                    ManagedComponentKind.Dlss5Feeder => Row(owner, "nr-dll-list+component-remove",
                        F("Nrc.FixFeederAgain", latest ?? T("Nrc.FixAnyForCard"), RowName(owner, diagnosis))),
                    ManagedComponentKind.OptiScaler => Row(owner, "component-remove", T("Nrc.FixOptiScaler")),
                    ManagedComponentKind.RtxEncore => Row(owner, "component-remove", F("Nrc.FixRtxEncore", RowName(owner, diagnosis))),
                    _ => (NoControl, T("Nrc.FixReplaceYourself"))
                };
            }
            if (controls.RemovableRows is null) return (NoControl, T("Nrc.FixSelectFirst"));
            if (controls.DlssNrLineSwaps)
                return ("dlss-nr-line", latest is not null ? F("Nrc.FixDlssLine", latest) : T("Nrc.FixDlssLineAny"));
            return (NoControl, T(dll.Path is null ? "Nrc.FixNoDll" : "Nrc.FixReplaceYourself"));
        }

        // A crash or a lost device: remove the method (the DLL's owner, else the first one RHI set up here).
        if (RemovableMethod(diagnosis) is not { } method) return (NoControl, T("Nrc.FixNoMethod"));
        if (controls.HeldByScaler.Contains(method)) return ("nr-cost-scaler-remove", T("Nrc.FixScalerFirst"));
        return Row(method, "component-remove", F("Nrc.FixRemove", RowName(method, diagnosis)));
    }

    // The method a "remove it" hint names: the DLL's owner, else the first Neural Rendering method RHI set up here.
    private static ManagedComponentKind? RemovableMethod(NrCheckDiagnosis diagnosis)
        => diagnosis.Dll.Owner ?? (diagnosis.Method.InUseKinds.Count > 0 ? diagnosis.Method.InUseKinds[0] : null);

    // Declared before FixControls (static fields initialise in order).
    private static readonly string[] NrDllListMarkers =
        ["x:Name=\"ReShadeProfileSection\"", "Text=\"{DynamicResource Ui.NrVersions}\"", "ItemsSource=\"{Binding NeuralRenderingDllVersionOptions}\""];
    private static readonly string[] ComponentRowMarkers = ["x:Name=\"ManagedComponentsSection\"", "ItemsSource=\"{Binding Components}\""];

    /// <summary>
    /// Each control a fix hint can name, with the markers of <c>MainWindow.axaml</c> that prove it is there (bindings and
    /// handlers, not texts). <c>FixHintControlTests</c> checks every one; whether it is enabled for a game is pinned per
    /// state against the live controls (<c>NeuralRenderingCheckFollowUpTests</c>).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> FixControls = new Dictionary<string, string[]>
    {
        // The NR DLL list under NR versions (ReShade profile section): swaps only ShortFuse's / the Feeder Full's NR DLL.
        ["nr-dll-list"] = NrDllListMarkers,
        // The DLSS / Streamline section's Neural rendering line: its version list and Apply.
        ["dlss-nr-line"] = ["x:Name=\"DlssStreamlineSection\"", "ItemsSource=\"{Binding DlssNrVersions}\"", "Command=\"{Binding SwapDlssComponentCommand}\" CommandParameter=\"NR\""],
        // The D3DCompiler 47 line of the ReShade profile section: Protontricks, Winetricks (each only where shown) and Browse.
        ["d3dcompiler"] = ["Text=\"D3DCompiler 47\"", "InstallD3DCompilerWithProtontricksCommand", "InstallD3DCompilerWithWinetricksCommand", "Click=\"BrowseD3DCompiler47_Click\""],
        // A Managed components row's action button (Remove while installed).
        ["component-remove"] = ComponentRowMarkers,
        // The NR DLL list (saves the choice), then the component row's Remove (the older Feeder takes it when added again).
        ["nr-dll-list+component-remove"] = [.. NrDllListMarkers, .. ComponentRowMarkers],
        // The NR Cost Scaler row under NR versions (its Remove): while it wraps the DLL, the owner is held.
        ["nr-cost-scaler-remove"] = ["<views:NrCostScalerRow/>"],
        // A plain step: no control.
        [NoControl] = [],
    };

    // Follow-up F7: the row as the Managed components list shows it ("OptiScaler", never the owner's "OptiScaler DLSS NR").
    private static string RowName(ManagedComponentKind kind, NrCheckDiagnosis diagnosis)
        => ComponentProbe.NeuralRenderingRowName(kind)
           ?? (diagnosis.Dll.Owner == kind ? diagnosis.Dll.OwnerName : null)
           ?? kind.ToString();

    private static string T(string key) => UiLocalizationService.Text(key);
    private static string F(string key, params object[] args) => UiLocalizationService.Format(key, args);
}
