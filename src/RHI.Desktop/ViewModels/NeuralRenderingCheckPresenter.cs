using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #234 (owner pick 2026-10-22: BOTH entry points, one page): the detail panel's "Check…" button right after the Neural
/// rendering recommendation and the card menu's last item, "Check Neural Rendering…", open the same read-only page
/// (<see cref="NeuralRenderingCheckDialogViewModel"/>). Both are hidden wherever the page would have nothing to show (no
/// NVIDIA card, a native Linux game, no confirmed executable, no Neural Rendering DLL and no Neural Rendering method in
/// the game) — the owner's hide rule; the diagnosis runs off the UI thread and writes nothing.
/// </summary>
public sealed partial class NeuralRenderingCheckPresenter : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, NeuralRenderingCheckPresenter> Presenters = new();

    /// <summary>How long opening the page waits for a game's analysis that is still running (then it opens without it).</summary>
    internal static readonly TimeSpan AnalysisWait = TimeSpan.FromSeconds(30);

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _managed;
    private bool _isAvailable;
    private bool _opening;

    /// <summary>The presenter of <paramref name="shell"/> (one per shell; the shell's one-line hook).</summary>
    public static NeuralRenderingCheckPresenter For(MainWindowViewModel shell, LinuxManagedComponentService managed)
        => Presenters.GetValue(shell, s => new NeuralRenderingCheckPresenter(s, managed));

    private NeuralRenderingCheckPresenter(MainWindowViewModel shell, LinuxManagedComponentService managed)
    {
        _shell = shell;
        _managed = managed;
        // The Neural rendering line is re-raised whenever the selection, its analysis, a component or the language
        // changes (NotifyDlss): the button follows it.
        _shell.Runtime.PropertyChanged += OnRuntimeChanged;
        Refresh();
    }

    /// <summary>The window's dialog (set by the window; tests and the harness set their own).</summary>
    public Func<NeuralRenderingCheckDialogViewModel, Task>? ShowDialogAsync { get; set; }

    /// <summary>The panel button shows: the selected game's page has something to show.</summary>
    public bool IsAvailable => _isAvailable;

    public string ButtonText => UiLocalizationService.Text("Nrc.Button");
    public string ButtonTip => UiLocalizationService.Text("Nrc.ButtonTip");

    private void OnRuntimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(RuntimePanelViewModel.NeuralRenderingRecommendation)) Refresh();
    }

    /// <summary>Works the panel button's visibility out again (no game file is read: the analysis and RHI's records).</summary>
    internal void Refresh()
    {
        var host = (IPanelHost)_shell;
        var available = host.SelectedGame is { } game && host.CurrentAnalysis is { } analysis
                        && _managed.NeuralRenderingCheckApplies(game, analysis.Dlss.DlssnrPaths.Count > 0);
        if (available != _isAvailable)
        {
            _isAvailable = available;
            OnPropertyChanged(nameof(IsAvailable));
        }
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonTip));
    }

    /// <summary>
    /// The card menu item shows for <paramref name="game"/>: its analysis (when RHI has one) or, before it, a Neural
    /// Rendering DLL beside its executable, or a Neural Rendering method RHI installed there.
    /// </summary>
    public bool AvailableFor(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        bool nrDll;
        if (((IPanelHost)_shell).Session.TryGetCached(game, out var analysis)) nrDll = analysis.Dlss.DlssnrPaths.Count > 0;
        else
        {
            var directory = NeuralRenderingDiagnosis.LogDirectory(game);
            nrDll = directory is not null && File.Exists(Path.Combine(directory, NeuralRenderingRuntimeOwnership.DllName));
        }
        return _managed.NeuralRenderingCheckApplies(game, nrDll);
    }

    /// <summary>The panel button: the selected game's page.</summary>
    [RelayCommand]
    private Task Open() => _shell.SelectedGame is { } game ? OpenForAsync(game) : Task.CompletedTask;

    /// <summary>
    /// Opens the page for <paramref name="game"/> (both entry points): waits for the game's analysis when it is still
    /// running (bounded), works the findings out off the UI thread and shows them. A second press while one opens is
    /// ignored. Never writes anything.
    /// </summary>
    public async Task OpenForAsync(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (_opening || ShowDialogAsync is not { } show || !AvailableFor(game)) return;
        _opening = true;
        try
        {
            var analysis = await AnalysisOfAsync(game).ConfigureAwait(true);
            var hasNativeDlss = analysis?.Facts.HasNativeDlssForNeuralRendering;
            var nrDlls = analysis?.Dlss.DlssnrPaths;
            NrCheckDiagnosis diagnosis;
            try
            {
                diagnosis = await Task.Run(() => _managed.DiagnoseNeuralRenderingAsync(game, hasNativeDlss, nrDlls)).ConfigureAwait(true);
                // Follow-up F2: the rows the window shows for this game's analysis, by the rows' own rule (none known
                // without an analysis: the page then names no row).
                var removable = analysis?.Components
                    .Select(component => new ComponentRowViewModel(component, _shell.ManageComponentCommand))
                    .Where(row => row.OffersRemove).Select(row => row.Kind).ToArray();
                diagnosis = diagnosis with { Controls = diagnosis.Controls with { RemovableRows = removable } };
            }
            catch (Exception ex)
            {
                // The page only reads; an unexpected failure says so on the status line (details in rhi.log), never a crash.
                RHI.Core.Services.AppLog.Warn("nr-check", $"Check Neural Rendering could not look at {game.Name}", ex);
                ((IPanelHost)_shell).StatusText = UiLocalizationService.Format("Nrc.Failed", game.Name);
                return;
            }
            await show(new NeuralRenderingCheckDialogViewModel(game, diagnosis)).ConfigureAwait(true);
        }
        finally { _opening = false; }
    }

    private async Task<GameAnalysis?> AnalysisOfAsync(DetectedGame game)
    {
        var session = ((IPanelHost)_shell).Session;
        if (session.TryGetCached(game, out var cached)) return cached;
        if (!ReferenceEquals(session.Game, game) || !session.IsProbeBusy) return null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (ReferenceEquals(outcome.Game, game)) done.TrySetResult();
        }
        session.Completed += OnCompleted;
        try
        {
            if (session.TryGetCached(game, out cached)) return cached;
            await Task.WhenAny(done.Task, Task.Delay(AnalysisWait)).ConfigureAwait(true);
        }
        finally { session.Completed -= OnCompleted; }
        return session.TryGetCached(game, out cached) ? cached : null;
    }

    /// <summary>The card menu's item (last, after Copy game report), or null where the page has nothing to show.</summary>
    internal CardMenuItem? MenuItemFor(DetectedGame game)
        => AvailableFor(game)
            ? new CardMenuItem("nr-check", UiLocalizationService.Text("Nrc.MenuItem"),
                new AsyncRelayCommand(() => OpenForAsync(game)), Tip: UiLocalizationService.Text("Nrc.ButtonTip"))
            : null;
}
