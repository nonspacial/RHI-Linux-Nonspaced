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

/// <summary>
/// Labels and values of the OptiScaler ⚙ \"Nightly build\" dropdown (#156): \"Latest (auto-update)\" (null) and the
/// builds as <c>yyyy-MM-dd</c>. Builds are dates, so they are shown the same in every language.
/// </summary>
public static class OptiScalerNightlyBuildOptions
{
    public static string Latest => UiLocalizationService.Text("Sync.OptiScalerNightlyLatest");

    public static string Display(string? build)
        => OptiScalerPackageProvider.IsNightlyBuild(build) ? $"{build![..4]}-{build[4..6]}-{build[6..8]}" : Latest;

    /// <summary>The build behind <paramref name="display"/>; null for Latest or anything that is not a shown build.</summary>
    public static string? Value(string? display)
    {
        var compact = display?.Trim().Replace("-", "", StringComparison.Ordinal);
        return display is { Length: 10 } && OptiScalerPackageProvider.IsNightlyBuild(compact) ? compact : null;
    }

    /// <summary>Latest, then <paramref name="builds"/> newest first, plus <paramref name="current"/> when the list lacks it.</summary>
    public static IReadOnlyList<string> Items(IReadOnlyList<string> builds, string? current)
    {
        var items = new List<string> { Latest };
        items.AddRange(builds.Where(OptiScalerPackageProvider.IsNightlyBuild).Select(Display));
        if (OptiScalerPackageProvider.IsNightlyBuild(current) && !items.Contains(Display(current))) items.Add(Display(current));
        return items;
    }
}

/// <summary>
/// The OptiScaler ⚙ \"Nightly build\" row (#156, upstream 2.8.6): shown only for the Nightly channel, saving the choice
/// per game through the view model's <c>TryPersist</c>; the build downloads when OptiScaler is installed. While RHI's
/// OptiScaler Nightly is installed the dropdown stays live (#232, upstream 2.8.7): a choice other than the installed build
/// shows Apply, which switches the installed build in place under the busy gate (wizardkof v0.9.25 \"Apply selected build
/// now\"); the note says the two differ until then. Locked only for Control frame generation's pinned build and another
/// installed channel. Hosted by the Runtime panel (<see cref="RuntimePanelViewModel.OptiScalerNightlyBuild"/>), whose
/// OptiScaler channel it follows (w3 integration: re-homed from the shell after the #89 split).
/// </summary>
public sealed partial class OptiScalerNightlyBuildPresenter : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, OptiScalerNightlyBuildPresenter> Instances = new();

    private readonly MainWindowViewModel _vm;
    private readonly RuntimePanelViewModel _runtime;
    private readonly LinuxManagedComponentService _managed;
    private readonly Func<Action, string, bool, bool, string?> _tryPersist;
    private IReadOnlyList<string> _builds = [];
    private bool _filling;
    private OptiScalerNightlySwitchState _state;

    [ObservableProperty] private string? selected;
    [ObservableProperty] private bool isVisible;
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private string note = "";
    /// <summary>#232: the chosen build differs from the installed Nightly's, and Apply can switch it in place.</summary>
    [ObservableProperty] private bool canApply;
    /// <summary>#232: a switch started by Apply is running (the dropdown and Apply wait for it).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanCancel))] private bool isApplying;

    public ObservableCollection<string> Options { get; } = [];

    /// <summary>The busy gate the switch runs under (its Cancel works while the build is still downloading).</summary>
    public OperationCoordinator Operations => _vm.Operations;

    /// <summary>#232: the row's own Cancel shows while the switch can still stop (the overlay Cancel is behind the dialog).</summary>
    public bool CanCancel => IsApplying && _vm.Operations.CanCancel;

    /// <summary>The latest switch Apply started (test seam).</summary>
    internal Task<bool> Applied { get; private set; } = Task.FromResult(false);

    public static OptiScalerNightlyBuildPresenter For(
        MainWindowViewModel vm,
        RuntimePanelViewModel runtime,
        LinuxManagedComponentService managed,
        Func<Action, string, bool, bool, string?> tryPersist)
        => Instances.GetValue(vm, owner => new OptiScalerNightlyBuildPresenter(owner, runtime, managed, tryPersist));

    private OptiScalerNightlyBuildPresenter(
        MainWindowViewModel vm,
        RuntimePanelViewModel runtime,
        LinuxManagedComponentService managed,
        Func<Action, string, bool, bool, string?> tryPersist)
    {
        _vm = vm;
        _runtime = runtime;
        _managed = managed;
        _tryPersist = tryPersist;
        _vm.PropertyChanged += OnViewModelChanged;
        _runtime.PropertyChanged += OnViewModelChanged;
        _vm.Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationCoordinator.CanCancel)) OnPropertyChanged(nameof(CanCancel));
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RuntimePanelViewModel.SelectedOptiScalerVariant) or nameof(MainWindowViewModel.SelectedGame))
            UiExceptionGuard.Forget(RefreshAsync(fetchList: false), "optiscaler-nightly-build");
    }

    /// <summary>Re-reads visibility, lock and the current build; <paramref name="fetchList"/> also asks GitHub for the builds.</summary>
    public async Task RefreshAsync(bool fetchList, CancellationToken cancellationToken = default)
    {
        var game = _vm.SelectedGame;
        IsVisible = game is not null && _runtime.SelectedOptiScalerVariant?.Value == OptiScalerVariant.Nightly;
        if (!IsVisible || game is null)
        {
            CanApply = false;
            return;
        }
        _state = _managed.GetOptiScalerNightlySwitchState(game);
        IsEnabled = _state is OptiScalerNightlySwitchState.NotInstalled or OptiScalerNightlySwitchState.Switchable;
        Fill(Shown(game));
        ShowApplyState(game);
        if (!fetchList) return;

        try
        {
            _builds = await _managed.ListOptiScalerNightlyBuildsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("optiscaler-nightly", "The OptiScaler nightly build list could not be loaded", ex);
            if (ReferenceEquals(game, _vm.SelectedGame)) Note = UiLocalizationService.Text("Sync.OptiScalerNightlyListFailed");
            return;
        }
        if (ReferenceEquals(game, _vm.SelectedGame)) Fill(Shown(game));
    }

    // The saved choice, except where it cannot apply: Control frame generation's build shows what is installed.
    private string? Shown(DetectedGame game)
        => _state == OptiScalerNightlySwitchState.HeldForControlUe
            ? _managed.GetInstalledOptiScalerNightlyBuild(game)
            : _managed.GetOptiScalerNightlyChoice(game);

    // Apply and the note line follow the installed build and the saved choice (#232).
    private void ShowApplyState(DetectedGame game)
    {
        var installed = _managed.GetInstalledOptiScalerNightlyBuild(game);
        var differs = !string.Equals(_managed.GetOptiScalerNightlyChoice(game), installed, StringComparison.Ordinal);
        CanApply = _state == OptiScalerNightlySwitchState.Switchable && differs && !IsApplying;
        Note = _state switch
        {
            OptiScalerNightlySwitchState.Switchable when differs
                => UiLocalizationService.Format("Nsw.NightlyDiffers", OptiScalerNightlyBuildOptions.Display(installed)),
            OptiScalerNightlySwitchState.Switchable => UiLocalizationService.Text("Sync.OptiScalerNightlyLocked"),
            OptiScalerNightlySwitchState.HeldForControlUe => UiLocalizationService.Text("Nsw.ControlUeHeld"),
            OptiScalerNightlySwitchState.OtherChannel => UiLocalizationService.Text("Nsw.OtherChannelLocked"),
            _ => UiLocalizationService.Text("Sync.OptiScalerNightlyNote")
        };
    }

    private void Fill(string? current)
    {
        _filling = true;
        try
        {
            var items = OptiScalerNightlyBuildOptions.Items(_builds, current);
            if (!Options.SequenceEqual(items))
            {
                Options.Clear();
                foreach (var item in items) Options.Add(item);
            }
            // Set only after the option exists (a ComboBox drops a selection it cannot find).
            Selected = OptiScalerNightlyBuildOptions.Display(current);
        }
        finally { _filling = false; }
    }

    partial void OnSelectedChanged(string? value)
    {
        // A list rebuild writes null into the bound selection: never a user choice (it would unpin the game).
        if (_filling || string.IsNullOrWhiteSpace(value) || !IsVisible || !IsEnabled || IsApplying || _vm.SelectedGame is not { } game) return;
        var build = OptiScalerNightlyBuildOptions.Value(value);
        var current = _managed.GetOptiScalerNightlyChoice(game);
        if (string.Equals(build, current, StringComparison.Ordinal)) return;
        if (_tryPersist(() => _managed.SetOptiScalerNightlyBuild(game, build), "Ui.WhatGamePreferences", false, true) is not null)
        {
            Fill(current); // back to the saved choice; the status line already says why
            return;
        }
        ShowApplyState(game);
        _vm.StatusText = CanApply
            ? build is null
                ? UiLocalizationService.Format("Nsw.ChosenLatest", game.Name)
                : UiLocalizationService.Format("Nsw.Chosen", game.Name, OptiScalerNightlyBuildOptions.Display(build))
            : build is null
                ? UiLocalizationService.Format("Sync.OptiScalerNightlyUnpinned", game.Name)
                : UiLocalizationService.Format("Sync.OptiScalerNightlyPinned", game.Name, OptiScalerNightlyBuildOptions.Display(build));
    }

    /// <summary>
    /// #232: Apply — switches RHI's installed OptiScaler Nightly on the selected game to the chosen build in place, as a
    /// shell operation (busy gate, Cancel while it downloads, status line, the game analysed again). The outcome line also
    /// shows under the row (the status line is behind the ⚙ dialog). True when the switch landed.
    /// </summary>
    public Task<bool> ApplyAsync()
    {
        Applied = ApplyCoreAsync();
        return Applied;
    }

    private async Task<bool> ApplyCoreAsync()
    {
        IPanelHost host = _vm;
        if (host.SelectedGame is not { } game || !CanApply) return false;
        using var op = host.BeginOperation("optiscaler-nightly-switch");
        if (op is null) return false;
        var target = _managed.GetOptiScalerNightlyChoice(game);
        string line;
        var switched = false;
        IsApplying = true;
        CanApply = false;
        try
        {
            host.StatusText = UiLocalizationService.Format("Nsw.Switching", game.Name, OptiScalerNightlyBuildOptions.Display(target));
            var result = await _managed.ApplyOptiScalerNightlyBuildAsync(game, op.Token).ConfigureAwait(true);
            switched = result.Success;
            // Worded from the result's flags, never from its message (a landed switch keeps its note).
            line = result.Success
                ? LandedLine.With(UiLocalizationService.Format("Nsw.Switched", game.Name, OptiScalerNightlyBuildOptions.Display(target)), result)
                : op.CancelRequested
                    ? UiLocalizationService.Format("Nsw.SwitchCancelled", game.Name)
                    : UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            line = UiLocalizationService.Format("Nsw.SwitchCancelled", game.Name);
        }
        finally
        {
            IsApplying = false;
        }
        host.StatusText = line;
        if (switched) await host.ReprobeKeepingStatusAsync(game).ConfigureAwait(true);
        if (ReferenceEquals(game, _vm.SelectedGame))
        {
            await RefreshAsync(fetchList: false).ConfigureAwait(true);
            Note = line;
        }
        return switched;
    }
}
