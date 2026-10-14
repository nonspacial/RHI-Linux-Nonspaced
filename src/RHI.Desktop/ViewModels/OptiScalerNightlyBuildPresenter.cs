using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// The OptiScaler ⚙ \"Nightly build\" row (#156, upstream 2.8.6): shown only for the Nightly channel, locked while
/// OptiScaler is installed (like the channel selector: the installed build is fixed until removal), and saving the
/// choice per game through the view model's <c>TryPersist</c>. The build downloads when OptiScaler is installed.
/// </summary>
public sealed partial class OptiScalerNightlyBuildPresenter : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, OptiScalerNightlyBuildPresenter> Instances = new();

    private readonly MainWindowViewModel _vm;
    private readonly LinuxManagedComponentService _managed;
    private readonly Func<Action, string, bool, bool, string?> _tryPersist;
    private IReadOnlyList<string> _builds = [];
    private bool _filling;

    [ObservableProperty] private string? selected;
    [ObservableProperty] private bool isVisible;
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private string note = "";

    public ObservableCollection<string> Options { get; } = [];

    public static OptiScalerNightlyBuildPresenter For(
        MainWindowViewModel vm,
        LinuxManagedComponentService managed,
        Func<Action, string, bool, bool, string?> tryPersist)
        => Instances.GetValue(vm, owner => new OptiScalerNightlyBuildPresenter(owner, managed, tryPersist));

    private OptiScalerNightlyBuildPresenter(
        MainWindowViewModel vm,
        LinuxManagedComponentService managed,
        Func<Action, string, bool, bool, string?> tryPersist)
    {
        _vm = vm;
        _managed = managed;
        _tryPersist = tryPersist;
        _vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedOptiScalerVariant) or nameof(MainWindowViewModel.SelectedGame))
            UiExceptionGuard.Forget(RefreshAsync(fetchList: false), "optiscaler-nightly-build");
    }

    /// <summary>Re-reads visibility, lock and the current build; <paramref name="fetchList"/> also asks GitHub for the builds.</summary>
    public async Task RefreshAsync(bool fetchList, CancellationToken cancellationToken = default)
    {
        var game = _vm.SelectedGame;
        IsVisible = game is not null && OptionLists.OptiScalerVariant.Value(_vm.SelectedOptiScalerVariant) == OptiScalerVariant.Nightly;
        if (!IsVisible || game is null) return;
        var installed = _managed.GetInstalledOptiScalerVariant(game) is not null;
        IsEnabled = !installed;
        Note = UiLocalizationService.Text(installed ? "Sync.OptiScalerNightlyLocked" : "Sync.OptiScalerNightlyNote");
        Fill(_managed.GetOptiScalerNightlyBuild(game));
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
        if (ReferenceEquals(game, _vm.SelectedGame)) Fill(_managed.GetOptiScalerNightlyBuild(game));
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
        if (_filling || string.IsNullOrWhiteSpace(value) || !IsVisible || !IsEnabled || _vm.SelectedGame is not { } game) return;
        var build = OptiScalerNightlyBuildOptions.Value(value);
        var current = _managed.GetOptiScalerNightlyBuild(game);
        if (string.Equals(build, current, StringComparison.Ordinal)) return;
        if (_tryPersist(() => _managed.SetOptiScalerNightlyBuild(game, build), "Ui.WhatGamePreferences", false, true) is not null)
        {
            Fill(current); // back to the saved choice; the status line already says why
            return;
        }
        _vm.StatusText = build is null
            ? UiLocalizationService.Format("Sync.OptiScalerNightlyUnpinned", game.Name)
            : UiLocalizationService.Format("Sync.OptiScalerNightlyPinned", game.Name, OptiScalerNightlyBuildOptions.Display(build));
    }
}
