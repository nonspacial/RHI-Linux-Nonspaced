using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// What a detail panel needs from the shell (epic #89 units 4–7): the selected game and its published analysis, the
/// busy gate, the status line, the one store-write helper and the re-analysis paths. The shell
/// (<see cref="MainWindowViewModel"/>) is the only implementation; panels never reach the shell's other state.
/// </summary>
internal interface IPanelHost
{
    DetectedGame? SelectedGame { get; }
    /// <summary>The selected game's published analysis, or null while it is being worked out.</summary>
    GameAnalysis? CurrentAnalysis { get; }
    bool IsBusy { get; }
    bool IsSelectionProbeBusy { get; }
    string StatusText { get; set; }
    LinuxAppSettings Settings { get; }
    /// <summary>Every game in the library (filtered or not).</summary>
    IReadOnlyList<DetectedGame> AllGames { get; }
    SelectedGameSession Session { get; }
    OperationScope? BeginOperation(string kind, bool cancellable = true, CancellationToken linkedToken = default);
    string? TryPersist(Action save, string whatKey, bool sessionOnly = true, bool reportStatus = true);
    Task ReprobeGameAsync(DetectedGame game);
    Task ReprobeKeepingStatusAsync(DetectedGame game);
    Task InvalidateAllAndReprobeSelectedAsync();
    Task<bool> AcceptManifestInstallWarningsAsync(DetectedGame game, ManagedComponentKind target,
        string displayName, bool installsTarget, IReadOnlyList<string>? indexedFiles);
    /// <summary>Says so and returns true while Repair after move rewrites the library file.</summary>
    bool LibraryWritesPaused();
    /// <summary>Re-reads an unreadable library file before a library write; true when a restored file was adopted.</summary>
    bool AdoptRestoredLibraryFile(bool reloadHdrControls = true);
    LinuxGameLibraryState LibraryState { get; }
    /// <summary>Raises the shell's selection-wide notifications (header, paths, every panel).</summary>
    void NotifySelected();
    /// <summary>Raises the DLSS stack's notifications (DLSS section, pin dropdowns, NR recommendation).</summary>
    void NotifyDlss();
    /// <summary>Saves the in-memory library state (HDR profiles); the localised refusal, or null when saved.</summary>
    string? PersistLibraryState(string whatKey);
    /// <summary>The library filter facts of <paramref name="game"/> changed (e.g. its Luma preference).</summary>
    void ForgetFilterFacts(DetectedGame game);
}

/// <summary>
/// Base of the detail panels split out of the shell: forwards the shell state the moved code reads under the same
/// names, so a panel's members read exactly as they did in the shell.
/// </summary>
public abstract class PanelViewModel : ObservableObject
{
    private protected PanelViewModel(IPanelHost host) => Host = host;

    private protected IPanelHost Host { get; }

    private protected static string T(string key) => UiLocalizationService.Text(key);
    private protected static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private protected static string PresentOr(string? value, string fallback)
    {
        var presented = UiLocalizationService.Present(value);
        return string.IsNullOrWhiteSpace(presented) ? fallback : presented;
    }

    private protected DetectedGame? SelectedGame => Host.SelectedGame;
    private protected GameAnalysis? CurrentAnalysis => Host.CurrentAnalysis;
    /// <summary>The selected game's disk facts, or null while the analysis runs; getters read only these.</summary>
    private protected SelectionFacts? Facts => CurrentAnalysis?.Facts;
    private protected bool IsBusy => Host.IsBusy;
    private protected bool IsSelectionProbeBusy => Host.IsSelectionProbeBusy;
    private protected bool HasConfirmedPatchTarget => SelectedGame?.HasConfirmedPatchTarget == true;

    private protected string StatusText
    {
        get => Host.StatusText;
        set => Host.StatusText = value;
    }

    private protected string ShowStatus(string message)
    {
        StatusText = message;
        return message;
    }

    private protected OperationScope? BeginOperation(string kind, bool cancellable = true, CancellationToken linkedToken = default)
        => Host.BeginOperation(kind, cancellable, linkedToken);

    private protected string? TryPersist(Action save, string whatKey, bool sessionOnly = true, bool reportStatus = true)
        => Host.TryPersist(save, whatKey, sessionOnly, reportStatus);

    private protected static string OperationCancelledText() => MainWindowViewModel.OperationCancelledText();

    private protected static string OutcomeText(OperationScope op, bool success, string? message)
        => !success && op.CancelRequested ? OperationCancelledText() : UiLocalizationService.Present(message);

    private protected Task ReprobeGameAsync(DetectedGame game) => Host.ReprobeGameAsync(game);
    private protected Task ReprobeKeepingStatusAsync(DetectedGame game) => Host.ReprobeKeepingStatusAsync(game);
    private protected Task InvalidateAllAndReprobeSelectedAsync() => Host.InvalidateAllAndReprobeSelectedAsync();

    // In-session caches (probe, selection) are per card and folder; saved library state uses GameKey (Refs #147).
    private protected static string Key(DetectedGame g) => RHI.Platform.Linux.Settings.LinuxGameLibraryStore.SessionKey(g);

    private protected static bool IsLatestSelection(string? value) => OptionLists.IsLatest(value);

    private protected static string LatestDisplay() => OptionLists.LatestDisplay();

    private protected static void EnsureOption(System.Collections.ObjectModel.ObservableCollection<string> target, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
            target.Add(value);
    }
}
