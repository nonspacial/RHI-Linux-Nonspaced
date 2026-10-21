using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>One entry of the ReShade DLL-name dropdown: a proxy name, or null for automatic.</summary>
public sealed record DllNameOption(string? Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The ReShade "DLL name" dropdown of the selected game (#120, upstream's per-game DLL-name dropdown). Automatic uses the
/// RHI manifest's name for the game, else the file the game's graphics API loads; any offered name can be chosen
/// before ReShade is installed. While RHI's ReShade is installed the dropdown shows the name it was installed under, and
/// choosing another free name renames it in place (#196, one journaled operation with its Wine override and record);
/// when another component decides the name (or the file changed) it shows only that name, locked, with the reason.
/// <b>Reset DLL names</b> (#196, upstream) puts every ReShade / OptiScaler name of the game back to automatic; it is shown
/// only when something is not automatic. State is worked out off the UI thread after each selection or analysis.
/// </summary>
public sealed partial class DllNamePickerViewModel : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, DllNamePickerViewModel> Pickers = new();

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _components;
    private readonly Func<Action, string, bool, bool, string?> _persist;
    private DetectedGame? _game;
    private string? _savedChoice;
    private string? _installed;
    private string? _automatic;
    private bool _rebuilding;
    private int _generation;

    /// <summary>The picker of <paramref name="shell"/> (one per shell; the shell's one-line hook).</summary>
    /// <param name="persist">The shell's store-save helper (<c>TryPersist</c>): returns the refusal text, or null when saved.</param>
    public static DllNamePickerViewModel For(MainWindowViewModel shell, LinuxManagedComponentService components,
        Func<Action, string, bool, bool, string?> persist)
        => Pickers.GetValue(shell, s => new DllNamePickerViewModel(s, components, persist));

    private DllNamePickerViewModel(MainWindowViewModel shell, LinuxManagedComponentService components, Func<Action, string, bool, bool, string?> persist)
    {
        _shell = shell;
        _components = components;
        _persist = persist;
        _shell.PropertyChanged += OnShellChanged;
        // The graphics API override lives in the Runtime panel since the #89 split (w3 integration).
        _shell.Runtime.PropertyChanged += OnShellChanged;
        // The ReShade channel lives in the ReShade profile panel since the #89 split (w6).
        _shell.ReShadeProfile.PropertyChanged += OnShellChanged;
        Refresh();
    }

    public ObservableCollection<DllNameOption> Options { get; } = [];

    [ObservableProperty] private DllNameOption? selectedOption;

    /// <summary>The row is shown only for a Windows game whose graphics API has a ReShade proxy.</summary>
    [ObservableProperty] private bool isAvailable;

    /// <summary>False while RHI's ReShade is installed (the name is fixed until removal) or while RHI is busy.</summary>
    [ObservableProperty] private bool canChoose;

    /// <summary>The row's label, in the UI language of the last refresh.</summary>
    [ObservableProperty] private string label = UiLocalizationService.Text("Names.DllNameLabel");

    /// <summary>The 2 s tooltip: which name ReShade uses and why.</summary>
    [ObservableProperty] private string hint = "";

    // w7/reshade (#196): Reset DLL names.
    /// <summary>Reset DLL names is shown: a ReShade / OptiScaler name of the game is saved or installed away from automatic.</summary>
    [ObservableProperty] private bool canReset;

    /// <summary>The Reset button's label and 2 s tooltip, in the UI language of the last refresh.</summary>
    [ObservableProperty] private string resetLabel = UiLocalizationService.Text("Rx.ResetDllNames");

    [ObservableProperty] private string resetTip = UiLocalizationService.Text("Rx.ResetDllNamesTip");

    /// <summary>The latest rename or reset the picker started (test seam).</summary>
    public Task Changed { get; private set; } = Task.CompletedTask;

    /// <summary>The latest state refresh (test seam).</summary>
    public Task Refreshed { get; private set; } = Task.CompletedTask;

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selection, analysis (an install/removal re-analyses), busy state, the graphics API override (changes what
        // Automatic stands for) and the UI language (relabels the channel).
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedGame) or nameof(MainWindowViewModel.IsSelectionProbeBusy)
            or nameof(MainWindowViewModel.IsBusy) or nameof(ReShadeProfileViewModel.SelectedReShadeChannel)
            or nameof(RuntimePanelViewModel.SelectedGraphicsApiOverride))
            Refresh();
    }

    /// <summary>
    /// The UI thread the list is bound to: whether the caller is on it, and how to run work there. Avalonia's dispatcher
    /// while an application runs (the app, the UI harness); none in plain view-model tests, where refreshes run where
    /// they are raised (Apply is serialised by its lock). Test seam: a stand-in UI thread.
    /// </summary>
    internal (Func<bool> IsCurrent, Action<Action> Post)? UiThread { get; set; } = Avalonia.Application.Current is null
        ? null
        : (() => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(), action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

    private void Refresh()
    {
        // Options is bound to the UI: a change raised on another thread refreshes on the UI thread, so the list is only
        // ever rebuilt there (merge review nit 5; the await in RefreshAsync resumes on the UI thread's context).
        if (UiThread is { } ui && !ui.IsCurrent())
        {
            ui.Post(Refresh);
            return;
        }
        var game = _shell.SelectedGame;
        var generation = Interlocked.Increment(ref _generation);
        Refreshed = RefreshAsync(game, generation);
        UiExceptionGuard.Forget(Refreshed, "DLL name picker");
    }

    private sealed record PickerState(ProxyNameResolution Resolution, string? Choice, string? Installed, bool Is32Bit,
        string? Automatic, IReadOnlyList<string> RenameTargets, string? RenameRefusal, bool CanReset);

    private async Task RefreshAsync(DetectedGame? game, int generation)
    {
        PickerState? state = null;
        // #201: an XNA game takes ReShade as d3d9.dll only (the GAC path): no DLL-name row.
        if (game is not null && !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) && !XnaGacReShade.IsListed(game))
        {
            // Ledger, preference file and executable header: read off the UI thread.
            state = await Task.Run(() =>
            {
                if (_components.ResolveReShadeProxyName(game) is not { } resolution) return null;
                var architecture = GameArchitecture.Of(game);
                var installed = _components.InstalledReShadeProxyName(game);
                (IReadOnlyList<string> Targets, string? Refusal) rename = installed is null ? ([], null) : _components.ReShadeRenameOptions(game);
                var (targets, refusal) = rename;
                return new PickerState(resolution, _components.ReShadeDllNames.GetReShade(game), installed,
                    architecture == PeArchitecture.X86, _components.ReShadeAutomaticProxyName(game), targets, refusal,
                    _components.HasDllNamesToReset(game));
            }).ConfigureAwait(true);
        }
        // One Apply at a time, and only the latest refresh's: without a UI synchronisation context (tests, a caller off
        // the UI thread) two refreshes finish on pool threads and would rebuild Options at once (w3 integration: the
        // Runtime panel's override change and the re-analysis it starts refresh back to back).
        lock (_applyGate)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Apply(game, state);
        }
    }

    private readonly object _applyGate = new();

    private void Apply(DetectedGame? game, PickerState? state)
    {
        _game = game;
        _installed = state?.Installed;
        _automatic = state?.Automatic;
        Label = UiLocalizationService.Text("Names.DllNameLabel");
        ResetLabel = UiLocalizationService.Text("Rx.ResetDllNames");
        ResetTip = UiLocalizationService.Text("Rx.ResetDllNamesTip");
        CanReset = state?.CanReset == true;
        _rebuilding = true;
        try
        {
            Options.Clear();
            if (state is null)
            {
                SelectedOption = null;
                IsAvailable = false;
                CanChoose = false;
                Hint = "";
                return;
            }

            IsAvailable = true;
            _savedChoice = state.Choice;
            if (state.Installed is { } installed)
            {
                if (state.RenameRefusal is { } refusal)
                {
                    // Another component decides the name, or the file changed: only the installed name, locked, and why.
                    var only = new DllNameOption(installed, installed);
                    Options.Add(only);
                    SelectedOption = only;
                    CanChoose = false;
                    Hint = LockedHint(refusal, "ReShade");
                    return;
                }
                // #196: Automatic (when its name is free or is this one), the installed name, and every free offered name.
                var renameTo = new HashSet<string>(state.RenameTargets, StringComparer.OrdinalIgnoreCase) { installed };
                if (state.Automatic is { } automaticName && renameTo.Contains(automaticName))
                    Options.Add(new DllNameOption(null, UiLocalizationService.Format("Names.DllNameAutomatic", automaticName)));
                foreach (var name in ProxyDllNames.OfferedFor(state.Is32Bit).Where(renameTo.Contains))
                    Options.Add(new DllNameOption(name, name));
                var automaticSelected = state.Choice is null && string.Equals(installed, state.Automatic, StringComparison.OrdinalIgnoreCase);
                SelectedOption = Options.FirstOrDefault(o => automaticSelected ? o.Value is null : string.Equals(o.Value, installed, StringComparison.OrdinalIgnoreCase))
                                 ?? Options[0];
                CanChoose = !_shell.IsBusy && !_shell.IsSelectionProbeBusy;
                Hint = UiLocalizationService.Format("Rx.DllNameTipRename", installed);
                return;
            }

            var automatic = AutomaticResolution(state.Resolution);
            Options.Add(new DllNameOption(null, UiLocalizationService.Format("Names.DllNameAutomatic", automatic.Name)));
            foreach (var name in ProxyDllNames.OfferedFor(state.Is32Bit))
                Options.Add(new DllNameOption(name, name));
            SelectedOption = Options.FirstOrDefault(o => string.Equals(o.Value, state.Choice, StringComparison.OrdinalIgnoreCase)) ?? Options[0];
            CanChoose = !_shell.IsBusy && !_shell.IsSelectionProbeBusy;
            Hint = HintFor(state.Resolution, automatic, game!);
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>
    /// The tooltip of a locked DLL-name dropdown (#196): the refusal and, for a DLL that changed since RHI installed it,
    /// what lets RHI manage it again (review 4). No control in RHI changes a file that changed (rename, update and removal
    /// all leave it alone), so the tip says so and names Remove on <paramref name="component"/>'s row for after the file
    /// is put back or deleted; a DLL that is gone needs only that Remove.
    /// </summary>
    internal static string LockedHint(string refusal, string component)
    {
        var presented = UiLocalizationService.Present(refusal);
        if (!refusal.StartsWith(ProxyRename.ChangedPrefix, StringComparison.Ordinal)) return presented;
        var path = refusal[ProxyRename.ChangedPrefix.Length..];
        return presented + " " + UiLocalizationService.Format(File.Exists(path) ? "Rx.DllChangedTip" : "Rx.DllMissingTip", component);
    }

    // What Automatic stands for: the resolution without the user's choice.
    private ProxyNameResolution AutomaticResolution(ProxyNameResolution resolution)
        => resolution.Source != ProxyNameSource.User
            ? resolution
            : ProxyPayloadLocator.Resolve(_game!, null) ?? resolution;

    private static string HintFor(ProxyNameResolution resolution, ProxyNameResolution automatic, DetectedGame game)
    {
        var hint = resolution.Source switch
        {
            ProxyNameSource.User => UiLocalizationService.Format("Names.DllNameTipChosen", resolution.Name, automatic.Name),
            ProxyNameSource.Manifest => UiLocalizationService.Format("Names.DllNameTipManifest", resolution.Name),
            _ when resolution.IgnoredManifestName is { } ignored => UiLocalizationService.Format("Names.DllNameTipManifestIgnored", resolution.Name, ignored),
            _ => UiLocalizationService.Format("Names.DllNameTipApi", resolution.Name)
        };
        if (!ProxyDllNames.NeedsWineOverride(resolution.Name))
            hint += UiLocalizationService.Format("Names.DllNameTipOwnName", resolution.Name);
        else if (!ProxyDllNames.LoadsFor(resolution.Name, game.EffectiveGraphicsApi))
            hint += UiLocalizationService.Format("Names.DllNameTipOtherApi", resolution.Name);
        return hint;
    }

    partial void OnSelectedOptionChanged(DllNameOption? value)
    {
        // Rebuilding the list writes null and the restored entry here; neither is the user's choice.
        if (_rebuilding || value is null || _game is not { } game) return;
        // #196: RHI's ReShade is installed and the choice names another file: rename it in place.
        if (_installed is { } installed && !string.Equals(value.Value ?? _automatic, installed, StringComparison.OrdinalIgnoreCase))
        {
            Changed = RunAsync("dll-rename", "Rx.Renaming", token => _components.RenameReShadeProxyAsync(game, value.Value, token), game);
            UiExceptionGuard.Forget(Changed, "DLL name rename");
            return;
        }
        if (string.Equals(value.Value, _savedChoice, StringComparison.OrdinalIgnoreCase)) return;
        var refused = _persist(() => _components.ReShadeDllNames.SetReShade(game, value.Value), "Ui.WhatGamePreferences", false, true);
        if (refused is not null)
        {
            // Not saved: show the saved choice again.
            _rebuilding = true;
            try { SelectedOption = Options.FirstOrDefault(o => string.Equals(o.Value, _savedChoice, StringComparison.OrdinalIgnoreCase)) ?? Options.FirstOrDefault(); }
            finally { _rebuilding = false; }
            return;
        }
        _savedChoice = value.Value;
        Refresh();
    }

    /// <summary>Reset DLL names (#196): every ReShade / OptiScaler name of the selected game back to automatic.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task ResetAsync()
    {
        if (_game is not { } game) return Task.CompletedTask;
        Changed = RunAsync("dll-reset", "Rx.Resetting", token => _components.ResetDllNamesAsync(game, token), game);
        return Changed;
    }

    // One rename or reset as a shell operation: the busy gate, its status lines, then the game is analysed again (the
    // files changed) and the picker shows the result; a refused or failed change shows the name as it is again.
    private async Task RunAsync(string kind, string busyKey, Func<CancellationToken, Task<ComponentOperationResult>> change, DetectedGame game)
    {
        IPanelHost host = _shell;
        using (var op = host.BeginOperation(kind))
        {
            if (op is null)
            {
                Refresh();
                return;
            }
            try
            {
                host.StatusText = UiLocalizationService.Text(busyKey);
                var result = await change(op.Token).ConfigureAwait(true);
                host.StatusText = !result.Success && op.CancelRequested
                    ? MainWindowViewModel.OperationCancelledText()
                    : UiLocalizationService.Present(result.Message);
                await host.ReprobeKeepingStatusAsync(game).ConfigureAwait(true);
            }
            catch (Exception ex) when (op.WasCancelled(ex))
            {
                host.StatusText = MainWindowViewModel.OperationCancelledText();
            }
        }
        Refresh();
    }
}
