using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #270 (upstream 2.9.2 <c>BuildOsReleaseCell</c>): the OptiScaler ⚙ "Release" row for Stable, DLSS NR and F5 (Nightly
/// keeps its Nightly build row). "Latest (auto-update)" or a release tag, saved per game with its channel; the next
/// install takes it and a pinned game is never updated. As upstream, it can be changed only while RHI's OptiScaler is
/// not installed on the game; while installed it shows the installed pin, locked.
/// </summary>
public sealed partial class OptiScalerReleasePresenter : ObservableObject
{
    private readonly MainWindowViewModel _vm;
    private readonly LinuxManagedComponentService _managed;
    private readonly DetectedGame _game;
    private readonly OptiScalerVariant _variant;
    private readonly Func<Action, string, bool, bool, string?> _tryPersist;
    private IReadOnlyList<OptiScalerRelease> _releases = [];
    private bool _filling;

    internal OptiScalerReleasePresenter(MainWindowViewModel vm, LinuxManagedComponentService managed, DetectedGame game, OptiScalerVariant variant,
        Func<Action, string, bool, bool, string?> tryPersist)
    {
        _vm = vm;
        _managed = managed;
        _game = game;
        _variant = variant;
        _tryPersist = tryPersist;
        var installed = managed.GetInstalledOptiScalerVariant(game) is not null;
        IsVisible = OptiScalerPackageProvider.ReleaseRepository(variant) is not null;
        IsEnabled = !installed;
        Note = UiLocalizationService.Text(installed ? "Osv.ReleaseLocked" : "Osv.ReleaseNote");
        Fill(Current);
    }

    public static string Latest => UiLocalizationService.Text("Sync.OptiScalerNightlyLatest");

    public ObservableCollection<string> Options { get; } = [];
    [ObservableProperty] private string? selected;
    public bool IsVisible { get; }
    public bool IsEnabled { get; }
    public string Note { get; }

    private string? Current => _managed.GetInstalledOptiScalerVariant(_game) is not null
        ? _managed.GetInstalledOptiScalerReleasePin(_game)
        : _managed.GetOptiScalerReleasePin(_game, _variant);

    /// <summary>Reads the channel's releases from GitHub (metadata only) and lists them; a failure keeps Latest and the pin.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsVisible || !IsEnabled) return;
        try
        {
            _releases = await _managed.ListOptiScalerReleasesAsync(_variant, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("optiscaler-release", "The OptiScaler release list could not be loaded", ex);
            return;
        }
        Fill(Current);
    }

    private void Fill(string? current)
    {
        _filling = true;
        try
        {
            var items = new List<string> { Latest };
            items.AddRange(_releases.Select(release => release.Tag));
            if (current is not null && !items.Contains(current)) items.Insert(1, current);
            if (!Options.SequenceEqual(items))
            {
                Options.Clear();
                foreach (var item in items) Options.Add(item);
            }
            Selected = current ?? Latest;
        }
        finally { _filling = false; }
    }

    partial void OnSelectedChanged(string? value)
    {
        // A list rebuild writes null into the bound selection: never a user choice.
        if (_filling || string.IsNullOrWhiteSpace(value) || !IsEnabled) return;
        var tag = value == Latest ? null : value;
        var current = _managed.GetOptiScalerReleasePin(_game, _variant);
        if (string.Equals(tag, current, StringComparison.Ordinal)) return;
        if (_tryPersist(() => _managed.SetOptiScalerReleasePin(_game, _variant, tag), "Ui.WhatGamePreferences", false, true) is not null)
        {
            Fill(current);
            return;
        }
        _vm.StatusText = tag is null
            ? UiLocalizationService.Format("Osv.ReleaseLatestChosen", _game.Name)
            : UiLocalizationService.Format("Osv.ReleasePinned", _game.Name, tag);
    }
}

/// <summary>
/// #270 (upstream 2.9.2 <c>BuildOsVersionLine</c>): the line at the top of the OptiScaler ⚙ — "Installed: x · Latest: y
/// (state)" — with Check now (asks GitHub again) and Update (shown only when an update applies; it runs the same update as
/// the component row). Checked once when the ⚙ opens.
/// </summary>
public sealed partial class OptiScalerVersionLinePresenter : ObservableObject
{
    private readonly LinuxManagedComponentService _managed;
    private readonly DetectedGame _game;
    private readonly Func<OptiScalerVariant> _variant;
    private readonly Func<CancellationToken, Task<bool>> _update;

    internal OptiScalerVersionLinePresenter(LinuxManagedComponentService managed, DetectedGame game, Func<OptiScalerVariant> variant,
        Func<CancellationToken, Task<bool>> update)
    {
        _managed = managed;
        _game = game;
        _variant = variant;
        _update = update;
        text = Line(Installed(), UiLocalizationService.Text("Osv.Checking"));
    }

    [ObservableProperty] private string text;
    [ObservableProperty] private bool canUpdate;
    [ObservableProperty] private bool isChecking;

    /// <summary>The last check (test seam).</summary>
    internal Task Checked { get; private set; } = Task.CompletedTask;

    public Task CheckAsync()
    {
        Checked = CheckCoreAsync();
        return Checked;
    }

    private async Task CheckCoreAsync()
    {
        IsChecking = true;
        CanUpdate = false;
        Text = Line(Installed(), UiLocalizationService.Text("Osv.Checking"));
        try
        {
            var status = await _managed.GetOptiScalerVersionStatusAsync(_game, _variant()).ConfigureAwait(true);
            Text = Describe(status);
            CanUpdate = status.State == OptiScalerVersionState.UpdateAvailable;
        }
        catch (Exception ex)
        {
            Text = Line(Installed(), UiLocalizationService.Text("Osv.CouldNotCheck"));
            AppLog.Warn("optiscaler-version", "The OptiScaler version line could not be checked", ex);
        }
        finally { IsChecking = false; }
    }

    /// <summary>The Check now button: asks GitHub again (its answers are revalidated, never served stale).</summary>
    [RelayCommand]
    private Task CheckNow() => CheckAsync();

    /// <summary>The Update button.</summary>
    [RelayCommand]
    private Task Update() => UpdateAsync();

    /// <summary>Update: the component row's update; afterwards the line is checked again.</summary>
    public async Task<bool> UpdateAsync()
    {
        if (!CanUpdate) return false;
        CanUpdate = false;
        var updated = await _update(CancellationToken.None).ConfigureAwait(true);
        await CheckAsync().ConfigureAwait(true);
        return updated;
    }

    private string Installed() => _managed.GetInstalledOptiScalerVariant(_game) is not null
        ? _managed.GetInstalledOptiScalerVersion(_game) ?? UiLocalizationService.Text("Osv.InstalledUnknown")
        : UiLocalizationService.Text("Osv.NotInstalled");

    private static string Line(string installed, string latest) => UiLocalizationService.Format("Osv.Line", installed, latest);

    internal static string Describe(OptiScalerVersionStatus status)
    {
        var installed = status.Installed ?? UiLocalizationService.Text(status.IsInstalled ? "Osv.InstalledUnknown" : "Osv.NotInstalled");
        if (status.State == OptiScalerVersionState.CouldNotCheck || status.Latest is null)
            return Line(installed, UiLocalizationService.Text("Osv.CouldNotCheckToken"));
        var state = status.State switch
        {
            OptiScalerVersionState.Pinned => UiLocalizationService.Format("Osv.StatePinned", status.Pin),
            OptiScalerVersionState.NotInstalled => UiLocalizationService.Text("Osv.StateInstalls"),
            OptiScalerVersionState.UpdateAvailable => UiLocalizationService.Text("Osv.StateUpdate"),
            _ => UiLocalizationService.Text("Osv.StateCurrent")
        };
        return Line(installed, $"{status.Latest} ({state})");
    }
}
