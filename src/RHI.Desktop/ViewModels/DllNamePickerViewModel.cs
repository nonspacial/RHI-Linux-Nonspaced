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
/// before ReShade is installed. While RHI's ReShade is installed the dropdown shows the name it was installed under and
/// is disabled (no in-place rename). State is worked out off the UI thread after each selection or analysis.
/// </summary>
public sealed partial class DllNamePickerViewModel : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, DllNamePickerViewModel> Pickers = new();

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _components;
    private readonly Func<Action, string, bool, bool, string?> _persist;
    private DetectedGame? _game;
    private string? _savedChoice;
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

    /// <summary>The latest state refresh (test seam).</summary>
    public Task Refreshed { get; private set; } = Task.CompletedTask;

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selection, analysis (an install/removal re-analyses), busy state, the graphics API override (changes what
        // Automatic stands for) and the UI language (relabels the channel).
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedGame) or nameof(MainWindowViewModel.IsSelectionProbeBusy)
            or nameof(MainWindowViewModel.IsBusy) or nameof(MainWindowViewModel.SelectedReShadeChannel)
            or nameof(MainWindowViewModel.SelectedGraphicsApiOverride))
            Refresh();
    }

    private void Refresh()
    {
        var game = _shell.SelectedGame;
        var generation = ++_generation;
        Refreshed = RefreshAsync(game, generation);
        UiExceptionGuard.Forget(Refreshed, "DLL name picker");
    }

    private sealed record PickerState(ProxyNameResolution Resolution, string? Choice, string? Installed, bool Is32Bit);

    private async Task RefreshAsync(DetectedGame? game, int generation)
    {
        PickerState? state = null;
        if (game is not null && !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
        {
            // Ledger, preference file and executable header: read off the UI thread.
            state = await Task.Run(() =>
            {
                if (_components.ResolveReShadeProxyName(game) is not { } resolution) return null;
                var architecture = string.IsNullOrWhiteSpace(game.ExecutablePath) ? PeArchitecture.Unknown : PeArchitectureDetector.Detect(game.ExecutablePath);
                return new PickerState(resolution, _components.ReShadeDllNames.GetReShade(game), _components.InstalledReShadeProxyName(game),
                    architecture == PeArchitecture.X86);
            }).ConfigureAwait(true);
        }
        if (generation != _generation) return;
        Apply(game, state);
    }

    private void Apply(DetectedGame? game, PickerState? state)
    {
        _game = game;
        Label = UiLocalizationService.Text("Names.DllNameLabel");
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
                var only = new DllNameOption(installed, installed);
                Options.Add(only);
                SelectedOption = only;
                CanChoose = false;
                Hint = UiLocalizationService.Format("Names.DllNameTipInstalled", installed);
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
}
