using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>The component dialogs that share one layout: a proxy DLL (and GPU generation) choice, then check / update.</summary>
public enum ProxyComponentDialogKind { Dlssg2030, DlssEnabler, UniversalMfg, RtxEncore, MfgUnlock }

/// <summary>
/// The DLSS FG 20/30 unlock, standalone DLSS Enabler and Universal MFG settings dialogs (epic #89 unit 7): one view
/// model per open dialog, shown through the <c>ProxyComponentDialogViewModel</c> data template (App.axaml). Choices
/// are saved through the shell's preference helper and put back when refused; Check / Update run through the shell's
/// busy gate, and the dialog has its own Cancel for its update (the main window's overlay Cancel is behind the modal
/// dialog), shown while that update can still stop.
/// </summary>
public sealed partial class ProxyComponentDialogViewModel : ObservableObject, IDisposable
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly MainWindowViewModel _shell;
    private readonly ProxyComponentDialogKind _kind;
    private bool _loading = true;
    private bool _reverting;

    internal ProxyComponentDialogViewModel(MainWindowViewModel shell, ProxyComponentDialogKind kind)
    {
        _shell = shell;
        _kind = kind;
        var game = shell.SelectedGame?.Name ?? "";
        switch (kind)
        {
            case ProxyComponentDialogKind.Dlssg2030:
            {
                // #281 (upstream 2.8.9): no GPU generation; the mod's main settings in dlssg_sm86.ini, Open ini, Open log.
                var preferences = shell.GetDlssg2030Preferences();
                var record = shell.GetDlssg2030Record();
                Title = T("Ui.Dlssg2030Title");
                Intro = T("Ui.Dlssg2030Intro");
                RouteNote = T("Ui.Dlssg2030RouteNote");
                ProxyLabel = T("Ui.Dlssg2030Proxy");
                ProxyOptions = Dlssg2030UnlockDeploymentService.ProxyNames;
                SelectedProxy = preferences.ProxyName;
                ProxyMinWidth = 220;
                IsInstalled = record is not null;
                InstalledText = record is null ? null : TF("Ui.UmfgInstalledAs", record.Version ?? "?",
                    record.Metadata.GetValueOrDefault("FG2030.ProxyName") ?? preferences.ProxyName);
                var content = FgSettingsBuilder.Dlssg2030(shell.GetDlssg2030IniLines(), record?.BinaryDirectory, shell.SetDlssg2030IniSetting, ShowMessage);
                (Sections, Lines, FileButtons) = (content.Sections, content.Lines, content.Files);
                CheckLabel = T("Ui.Dlssg2030Check");
                UpdateLabel = T("Ui.Dlssg2030Update");
                FooterNote = T("Ui.Dlssg2030CompatibilityNote");
                FooterIsWarning = false;
                (Width, Height, MinWidth, MinHeight) = (720, 640, 560, 420);
                break;
            }
            case ProxyComponentDialogKind.DlssEnabler:
            {
                var preferences = shell.GetStandaloneDlssEnablerPreferences();
                var record = shell.GetStandaloneDlssEnablerRecord();
                Title = T("Ui.DlssEnablerStandaloneTitle");
                Intro = T("Ui.DlssEnablerStandaloneIntro");
                ProxyLabel = T("Ui.DlssEnablerStandaloneProxy");
                ProxyOptions = StandaloneDlssEnablerDeploymentService.ProxyNames;
                SelectedProxy = preferences.ProxyName;
                ProxyMinWidth = 240;
                IsInstalled = record is not null;
                InstalledText = record is null ? null : TF("Win.InstalledChangeProxyRemoveReinstallRHI", record.Version ?? "?",
                    record.Metadata.GetValueOrDefault("DLSSEnabler.ProxyName") ?? preferences.ProxyName);
                CheckLabel = T("Ui.DlssEnablerStandaloneCheck");
                UpdateLabel = T("Ui.DlssEnablerStandaloneUpdate");
                FooterNote = T("Ui.DlssEnablerStandaloneCompatibilityNote");
                FooterIsWarning = true;
                (Width, Height, MinWidth, MinHeight) = (660, 470, 540, 380);
                break;
            }
            case ProxyComponentDialogKind.MfgUnlock: // w14/extras-fg (#279): settings only, no proxy name
            {
                var record = shell.GetMfgUnlockRecord();
                Title = "MFG Unlock"; // a product name, the same in every language
                Intro = T("Mfgu.Intro");
                ProxyLabel = "";
                ProxyOptions = [];
                HasProxy = false;
                IsInstalled = record is not null;
                ShowsCheck = false;
                var content = FgSettingsBuilder.MfgUnlock(shell.SelectedGame!, record?.BinaryDirectory ?? shell.SelectedGame?.BinaryDirectory,
                    record is null ? [] : shell.ManagedForFgSettings.MfgUnlockXessState(record.BinaryDirectory), record is not null, ShowMessage);
                (Sections, Lines, FileButtons) = (content.Sections, content.Lines, content.Files);
                CheckLabel = "";
                UpdateLabel = "";
                FooterNote = "";
                (Width, Height, MinWidth, MinHeight) = (700, 560, 560, 420);
                break;
            }
            case ProxyComponentDialogKind.RtxEncore: // w4/encore (#158)
            {
                var record = shell.GetRtxEncoreRecord();
                Title = RtxEncoreDeploymentService.DisplayName; // a product name, the same in every language
                Intro = T("Ui.RtxEncoreIntro");
                ProxyLabel = T("Ui.RtxEncoreName");
                ProxyTip = T("Ui.RtxEncoreNameTip");
                var chosen = shell.RtxEncoreChosenName();
                var offered = shell.RtxEncoreOfferedNames();
                // The saved/installed name is always listed, so the dropdown never blanks it.
                ProxyOptions = offered.Contains(chosen, StringComparer.OrdinalIgnoreCase) ? offered : [chosen, .. offered];
                SelectedProxy = chosen;
                ProxyMinWidth = 240;
                HasNeuralRendering = true;
                NeuralRenderingLabel = T("Ui.RtxEncoreNeuralRendering");
                NeuralRenderingTip = T("Ui.RtxEncoreNeuralRenderingTip");
                NeuralRenderingChosen = shell.RtxEncoreNeuralRenderingChosen();
                IsInstalled = record is not null;
                InstalledText = record is null ? null : TF("Win.RtxEncoreInstalled", record.Version ?? "?", chosen);
                CheckLabel = T("Ui.RtxEncoreCheck");
                UpdateLabel = T("Vm.Update");
                FooterNote = T("Ui.RtxEncoreFooter");
                FooterIsWarning = true;
                (Width, Height, MinWidth, MinHeight) = (660, 500, 540, 400);
                break;
            }
            default:
            {
                // #278 (upstream 2.8.9): no GPU generation; the mod's modes, its XeMFG files and its OptiScaler-plugin placement.
                var preferences = shell.GetRtx40MfgPreferences();
                var record = shell.GetRtx40MfgRecord();
                var status = shell.GetUniversalMfgStatus();
                Title = T("Ui.Rtx40MfgTitle");
                Intro = T("Ui.Rtx40MfgIntro");
                ProxyLabel = T("Ui.Rtx40MfgProxy");
                ProxyOptions = Rtx40MfgStandaloneDeploymentService.SelectableProxyNames;
                SelectedProxy = preferences.ProxyName;
                ProxyMinWidth = 240;
                HasProxy = status is not { AsPlugin: true };
                CompatibilityText = T("Ui.Rtx40MfgCompatibility");
                IsInstalled = record is not null;
                InstalledText = record is null ? null : TF("Ui.UmfgInstalledAs", record.Version ?? "?", status?.InstalledAs ?? preferences.ProxyName);
                Lines = UniversalMfgLines(status, IsInstalled);
                CheckLabel = T("Ui.Rtx40MfgCheck");
                UpdateLabel = T("Ui.Rtx40MfgUpdate");
                FooterNote = T("Ui.Rtx40MfgCompatibilityNote");
                FooterIsWarning = true;
                (Width, Height, MinWidth, MinHeight) = (700, 520, 560, 400);
                break;
            }
        }
        WindowTitle = $"{Title} — {game}";
        foreach (var row in Sections.SelectMany(section => section.Rows))
        {
            row.Changed = ShowMessage;
            row.Refresh();
        }
        _loading = false;
        _shell.Operations.PropertyChanged += OnOperationsChanged;
    }

    /// <summary>
    /// w14/extras-fg: the dialog's extra lines under the choices (headings, notes, warnings): Universal MFG Unlocker's
    /// plugin and XeMFG sections (#278), and the status lines of the other FG unlocks.
    /// </summary>
    public IReadOnlyList<ComponentDialogLine> Lines { get; } = [];
    public bool HasLines => Lines.Count > 0;

    /// <summary>The proxy-name row shows (hidden while Universal MFG Unlocker is, or would be, an OptiScaler plugin).</summary>
    public bool HasProxy { get; } = true;

    /// <summary>w14/extras-fg: the mod's own settings (#279 MFG Unlock, #281 20/30 FG Unlock, #280 RTX Encore), by section.</summary>
    public IReadOnlyList<ComponentSettingSection> Sections { get; } = [];
    public bool HasSections => Sections.Count > 0;

    /// <summary>w14/extras-fg: the "Open &lt;file&gt;" buttons (the mod's ini, log or jsonc).</summary>
    public IReadOnlyList<ComponentFileButton> FileButtons { get; } = [];
    public bool HasFileButtons => FileButtons.Count > 0;

    /// <summary>Check / Update show (installed, and the dialog has them: MFG Unlock updates through its row and Update All).</summary>
    public bool ShowsCheck { get => _showsCheck ?? IsInstalled; private init => _showsCheck = value; }
    private readonly bool? _showsCheck;

    /// <summary>A setting's or a file button's line; every row's enabled state and every button follow it.</summary>
    private void ShowMessage(string message)
    {
        Message = message;
        foreach (var row in Sections.SelectMany(section => section.Rows)) row.Refresh();
        foreach (var button in FileButtons) button.Refresh();
    }

    /// <summary>#278: Universal MFG Unlocker's ⚙ sections, in upstream's order.</summary>
    internal static IReadOnlyList<ComponentDialogLine> UniversalMfgLines(UniversalMfgStatus? status, bool installed)
    {
        var lines = new List<ComponentDialogLine>();
        if (status is { AsPlugin: true })
        {
            lines.Add(ComponentDialogLine.Heading(T("Ui.UmfgPluginHeading")));
            lines.Add(ComponentDialogLine.Plain(installed ? T("Ui.UmfgPluginText") : T("Ui.UmfgPluginBeforeInstall")));
            lines.Add(ComponentDialogLine.Plain(T("Ui.UmfgPluginNote")));
        }
        lines.Add(ComponentDialogLine.Heading(T("Ui.UmfgXessHeading")));
        lines.Add(ComponentDialogLine.Plain(T(status is { AsPlugin: true } ? "Ui.UmfgXessPlugin" : "Ui.UmfgXessStandalone")));
        if (status?.XessInstalled is { } xess)
            lines.Add(xess ? ComponentDialogLine.Plain(T("Ui.UmfgXessInstalled")) : ComponentDialogLine.Warning(T("Ui.UmfgXessMissing")));
        if (status is { ReShadeAsDxgi: true })
            lines.Add(ComponentDialogLine.Warning(T("Ui.UmfgReShadeDxgi")));
        return lines;
    }

    public string WindowTitle { get; }
    public double Width { get; }
    public double Height { get; }
    public double MinWidth { get; }
    public double MinHeight { get; }

    public string Title { get; }
    public string Intro { get; }
    public string? RouteNote { get; }
    public bool HasRouteNote => RouteNote is not null;
    public string GpuLabel { get; } = "";
    public IReadOnlyList<string> GpuOptions { get; } = [];
    public bool HasGpu => GpuOptions.Count > 0;
    public double GpuMinWidth { get; }
    public string ProxyLabel { get; }
    public string? ProxyTip { get; }
    public IReadOnlyList<string> ProxyOptions { get; }
    public double ProxyMinWidth { get; }
    public string? CompatibilityText { get; }
    public bool HasCompatibility => CompatibilityText is not null;
    /// <summary>Installed by RHI: the choices are locked (remove and reinstall to change them) and Check / Update show.</summary>
    public bool IsInstalled { get; }
    public bool CanChoose => !IsInstalled;
    public string? InstalledText { get; }
    public string CheckLabel { get; }
    public string UpdateLabel { get; }
    public string FooterNote { get; }
    /// <summary>The footer is a warning (gold) rather than a plain note.</summary>
    public bool FooterIsWarning { get; }
    public bool FooterIsPlain => !FooterIsWarning;

    /// <summary>RTX Encore's "Neural Rendering file" choice (#158); hidden in the other dialogs.</summary>
    public bool HasNeuralRendering { get; }
    /// <summary>RTX Encore installed: the name is shown as text (it cannot change until removal).</summary>
    public bool ShowsProxyAsText => HasNeuralRendering && IsInstalled;
    public string NeuralRenderingLabel { get; } = "";
    public string? NeuralRenderingTip { get; }
    [ObservableProperty] private bool neuralRenderingChosen;
    [ObservableProperty] private bool canChangeNeuralRendering = true;

    [ObservableProperty] private string? selectedGpu;
    [ObservableProperty] private string? selectedProxy;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool canCheck = true;
    [ObservableProperty] private bool canUpdate;
    [ObservableProperty] private bool isUpdating;

    /// <summary>The busy gate, for the dialog's own Cancel.</summary>
    public OperationCoordinator Operations => _shell.Operations;

    /// <summary>Cancel shows while this dialog's update runs and can still stop (before it changes any file).</summary>
    public bool ShowCancel => IsUpdating && Operations.CanCancel;

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(ShowCancel));

    private void OnOperationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationCoordinator.CanCancel)) OnPropertyChanged(nameof(ShowCancel));
    }

    // An empty selection is never a choice: the ComboBox writes null back through the TwoWay binding when the stored
    // value is not among its options (e.g. a Bink proxy name Universal MFG no longer offers) or its items are reset.
    partial void OnSelectedGpuChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) SavePreferences();
    }

    partial void OnSelectedProxyChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) SavePreferences();
    }

    // Saved before install; once installed the file goes in or out at once (busy gate, journaled). Refused = put back.
    partial void OnNeuralRenderingChosenChanged(bool value)
    {
        if (_loading || _reverting || !HasNeuralRendering) return;
        PendingNeuralRendering = ApplyNeuralRenderingAsync(value);
        UiExceptionGuard.Forget(PendingNeuralRendering, "RtxEncoreNeuralRendering");
    }

    /// <summary>The last Neural Rendering change started (tests await it).</summary>
    internal Task? PendingNeuralRendering { get; private set; }

    private async Task ApplyNeuralRenderingAsync(bool value)
    {
        CanChangeNeuralRendering = false;
        try
        {
            var (result, shown) = await _shell.SetRtxEncoreNeuralRenderingAsync(value);
            Message = shown;
            if (result.Success) return;
            _reverting = true;
            try { NeuralRenderingChosen = _shell.RtxEncoreNeuralRenderingChosen(); }
            finally { _reverting = false; }
        }
        finally { CanChangeNeuralRendering = true; }
    }

    /// <summary>
    /// Saves the dropdowns' choice. A dropdown left empty keeps the stored value — never the component's default, which
    /// would overwrite the user's saved preference just by opening the dialog.
    /// </summary>
    private void SavePreferences()
    {
        if (_loading || IsInstalled || _reverting) return;
        var result = Save();
        Message = UiLocalizationService.Present(result.Message);
        if (result.Success) return;
        // Not saved: the dropdowns go back to what is stored.
        _reverting = true;
        try
        {
            switch (_kind)
            {
                case ProxyComponentDialogKind.Dlssg2030:
                    var fg = _shell.GetDlssg2030Preferences();
                    SelectedGpu = fg.GpuGeneration;
                    SelectedProxy = fg.ProxyName;
                    break;
                case ProxyComponentDialogKind.DlssEnabler:
                    SelectedProxy = _shell.GetStandaloneDlssEnablerPreferences().ProxyName;
                    break;
                case ProxyComponentDialogKind.RtxEncore:
                    SelectedProxy = _shell.RtxEncoreChosenName();
                    break;
                default:
                    var mfg = _shell.GetRtx40MfgPreferences();
                    SelectedGpu = mfg.GpuGeneration;
                    SelectedProxy = mfg.ProxyName;
                    break;
            }
        }
        finally { _reverting = false; }

        ComponentOperationResult Save()
        {
            switch (_kind)
            {
                case ProxyComponentDialogKind.Dlssg2030:
                    var storedFg = _shell.GetDlssg2030Preferences();
                    return _shell.SetDlssg2030Preferences(Chosen(SelectedGpu, storedFg.GpuGeneration), Chosen(SelectedProxy, storedFg.ProxyName));
                case ProxyComponentDialogKind.DlssEnabler:
                    return _shell.SetStandaloneDlssEnablerPreferences(Chosen(SelectedProxy, _shell.GetStandaloneDlssEnablerPreferences().ProxyName));
                case ProxyComponentDialogKind.RtxEncore:
                    return _shell.SetRtxEncoreName(Chosen(SelectedProxy, _shell.RtxEncoreChosenName()));
                default:
                    var storedMfg = _shell.GetRtx40MfgPreferences();
                    return _shell.SetRtx40MfgPreferences(Chosen(SelectedGpu, storedMfg.GpuGeneration), Chosen(SelectedProxy, storedMfg.ProxyName));
            }
        }
    }

    private static string Chosen(string? selected, string stored) => string.IsNullOrEmpty(selected) ? stored : selected;

    [RelayCommand]
    private async Task CheckAsync()
    {
        CanCheck = false;
        try
        {
            var (success, hasUpdate, text) = _kind switch
            {
                ProxyComponentDialogKind.Dlssg2030 => await _shell.CheckDlssg2030UpdateAsync() is var a ? (a.Success, a.HasUpdate, a.Message) : default,
                ProxyComponentDialogKind.DlssEnabler => await _shell.CheckStandaloneDlssEnablerUpdateAsync() is var b ? (b.Success, b.HasUpdate, b.Message) : default,
                ProxyComponentDialogKind.RtxEncore => await _shell.CheckRtxEncoreUpdateAsync() is var e ? (e.Success, e.HasUpdate, e.Message) : default,
                _ => await _shell.CheckRtx40MfgUpdateAsync() is var c ? (c.Success, c.HasUpdate, c.Message) : default
            };
            Message = UiLocalizationService.Present(text);
            CanUpdate = success && hasUpdate;
        }
        finally { CanCheck = true; }
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        CanUpdate = false;
        IsUpdating = true;
        try
        {
            // Already the shown line: "Operation cancelled." when this dialog's Cancel stopped the update.
            Message = await _shell.UpdateProxyComponentAsync(_kind);
        }
        finally { IsUpdating = false; }
    }

    public void Dispose() => _shell.Operations.PropertyChanged -= OnOperationsChanged;
}

/// <summary>One extra line of a component dialog (w14/extras-fg): a heading, a note or a (gold) warning.</summary>
public sealed record ComponentDialogLine(string Text, bool IsHeading, bool IsWarning)
{
    public bool IsPlain => !IsHeading && !IsWarning;
    public static ComponentDialogLine Heading(string text) => new(text, true, false);
    public static ComponentDialogLine Plain(string text) => new(text, false, false);
    public static ComponentDialogLine Warning(string text) => new(text, false, true);
}
