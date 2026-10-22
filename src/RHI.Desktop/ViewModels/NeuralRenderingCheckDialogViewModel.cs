using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>One line of a section: a value, a warning (amber) or a smaller detail/fix line; provenance in its ~2 s tooltip.</summary>
public sealed record NrCheckLine(string Text, bool IsWarning = false, bool IsDetail = false, string? Tip = null)
{
    public bool HasTip => !string.IsNullOrEmpty(Tip);
}

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
            new(DllSection, T("Nrc.DllLabel"), DllLines(diagnosis.Dll)),
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

    private static IReadOnlyList<NrCheckLine> DllLines(NrCheckDll dll)
    {
        if (dll.Fit == NrDllFit.NoDll) return [new(T("Nrc.DllNone"))];
        var tip = DllTip(dll);
        var lines = new List<NrCheckLine>
        {
            dll.Fit switch
            {
                NrDllFit.Fits => new(F("Nrc.DllFits", dll.Version!), Tip: tip),
                NrDllFit.NotForThisCard => new(F("Nrc.DllNotForCard", dll.Version!), IsWarning: true, Tip: tip),
                NrDllFit.CardUnknown => new(F("Nrc.DllCardUnknown", dll.Version!), Tip: tip),
                _ => new(T("Nrc.DllVersionUnknown"), IsWarning: true, Tip: tip)
            },
            new(dll.OwnerName is { } owner ? F("Nrc.DllOwner", owner) : T("Nrc.DllGameOwn"), IsDetail: true, Tip: tip)
        };
        if (dll.PickedVersion is { } picked) lines.Add(new(F("Nrc.DllPicked", picked), IsDetail: true));
        if (dll.FaultsOnThisDriver) lines.Add(new(T("Nrc.DllFaults"), IsWarning: true, IsDetail: true));
        if (dll.Fit == NrDllFit.NotForThisCard || dll.FaultsOnThisDriver)
        {
            if (dll.LatestForCard is { } latest) lines.Add(new(F("Nrc.DllForCard", latest), IsDetail: true));
            lines.Add(new(F("Nrc.Fix", FixText(NrFixHint.NrDllForCard, dll)), IsDetail: true));
        }
        return lines;
    }

    private static string DllTip(NrCheckDll dll)
    {
        var file = F("Nrc.DllFile", dll.Path ?? "");
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
                lines.Add(new(F("Nrc.Fix", FixText(finding.Fix, diagnosis.Dll)), IsDetail: true, Tip: tip));
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
        _ => "Nrc.SigCrash"
    });

    /// <summary>
    /// The control a fix hint names, as a key of <see cref="FixControls"/>: every hint names a control that exists in the
    /// main window and does exactly that (a test reads the window's layout for each one).
    /// </summary>
    internal static string FixControl(NrFixHint hint, NrCheckDll dll) => hint switch
    {
        NrFixHint.NativeD3DCompiler => "d3dcompiler",
        NrFixHint.NrDllForCard when dll.Owner is ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder => "nr-dll-list",
        NrFixHint.NrDllForCard when dll.Owner is ManagedComponentKind.OptiScaler => "component-remove",
        NrFixHint.NrDllForCard when dll.Owner is ManagedComponentKind.RtxEncore => "component-remove",
        NrFixHint.NrDllForCard => "dlss-nr-line",
        _ when dll.OwnerName is not null => "component-remove",
        _ => "dlss-nr-line"
    };

    /// <summary>
    /// Each control a fix hint can name, with the markers of <c>MainWindow.axaml</c> that prove it is there (bindings and
    /// handlers, not texts). <c>FixHintControlTests</c> checks every one.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> FixControls = new Dictionary<string, string[]>
    {
        // The NR DLL list under NR versions (ReShade profile section): swaps only ShortFuse's / the Feeder's NR DLL.
        ["nr-dll-list"] = ["x:Name=\"ReShadeProfileSection\"", "Text=\"{DynamicResource Ui.NrVersions}\"", "ItemsSource=\"{Binding NeuralRenderingDllVersionOptions}\""],
        // The DLSS / Streamline section's Neural rendering line: its version list and Apply.
        ["dlss-nr-line"] = ["x:Name=\"DlssStreamlineSection\"", "ItemsSource=\"{Binding DlssNrVersions}\"", "Command=\"{Binding SwapDlssComponentCommand}\" CommandParameter=\"NR\""],
        // The D3DCompiler 47 line of the ReShade profile section: Protontricks, Winetricks and Browse.
        ["d3dcompiler"] = ["Text=\"D3DCompiler 47\"", "InstallD3DCompilerWithProtontricksCommand", "InstallD3DCompilerWithWinetricksCommand", "Click=\"BrowseD3DCompiler47_Click\""],
        // A Managed components row's action button (Remove while installed).
        ["component-remove"] = ["x:Name=\"ManagedComponentsSection\"", "ItemsSource=\"{Binding Components}\""],
    };

    internal static string FixText(NrFixHint hint, NrCheckDll dll) => FixControl(hint, dll) switch
    {
        "d3dcompiler" => T("Nrc.FixD3DCompiler"),
        "nr-dll-list" => dll.LatestForCard is { } latest ? F("Nrc.FixNrDllList", latest) : T("Nrc.FixNrDllListAny"),
        "dlss-nr-line" => dll.LatestForCard is { } latest ? F("Nrc.FixDlssLine", latest) : T("Nrc.FixDlssLineAny"),
        _ when hint == NrFixHint.NrDllForCard && dll.Owner == ManagedComponentKind.OptiScaler => T("Nrc.FixOptiScaler"),
        _ when hint == NrFixHint.NrDllForCard && dll.Owner == ManagedComponentKind.RtxEncore => T("Nrc.FixRtxEncore"),
        _ => F("Nrc.FixRemove", dll.OwnerName ?? "")
    };

    private static string T(string key) => UiLocalizationService.Text(key);
    private static string F(string key, params object[] args) => UiLocalizationService.Format(key, args);
}
