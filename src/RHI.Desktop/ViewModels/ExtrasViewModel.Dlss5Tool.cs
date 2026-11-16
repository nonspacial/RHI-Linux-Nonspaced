using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w14/nr-2 (#317, #276): the DLSS5 Tool's per-game controls beside the other Neural Rendering versions (upstream's Neural
/// Rendering section: "DLSS5 Tool Version", which becomes "DLSS5 Tool Version (Foveated)" and lists FoveatedDLSS5's
/// releases while the Foveated switch is on). Install / Update / Remove are the DLSS5 Tool row's (and the DX11 Bridge row's,
/// for "DLSS5 Tool + DX11 Bridge") in Managed components.
/// </summary>
public sealed partial class ExtrasViewModel
{
    private IReadOnlyList<GitHubReleaseVersion> _dlss5ToolStockVersions = [];
    private IReadOnlyList<GitHubReleaseVersion> _dlss5ToolFoveatedVersions = [];

    public ObservableCollection<string> Dlss5ToolVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];

    [ObservableProperty] private string selectedDlss5ToolVersion = UiLocalizationService.Text("Ui.OptionLatest");

    /// <summary>Shown only where the app was built with the DLSS5 Tool service.</summary>
    public bool ShowDlss5Tool => _managedComponents.Dlss5Tool is not null;

    public bool CanConfigureDlss5ToolVersionPin => ShowDlss5Tool && SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    public bool CanConfigureDlss5ToolFoveated => CanConfigureDlss5ToolVersionPin && SelectedGame!.HasConfirmedPatchTarget;

    /// <summary>The game's DLSS5 Tool build: the installed one, else the saved switch.</summary>
    public bool Dlss5ToolFoveated
    {
        get => SelectedGame is { } game && ShowDlss5Tool && _managedComponents.GetDlss5ToolBuild(game) == Dlss5ToolBuild.Foveated;
        set
        {
            if (SelectedGame is not { } game || !ShowDlss5Tool || value == Dlss5ToolFoveated) return;
            UiExceptionGuard.Forget(SetDlss5ToolFoveatedAsync(game, value), "SetDlss5ToolFoveatedAsync");
        }
    }

    /// <summary>"DLSS5 Tool Version", or "DLSS5 Tool Version (Foveated)" while the switch is on (upstream's label).</summary>
    public string Dlss5ToolVersionLabel => T(Dlss5ToolFoveated ? "Ui.Dlss5ToolVersionFoveated" : "Ui.Dlss5ToolVersion");

    partial void OnSelectedDlss5ToolVersionChanged(string value)
    {
        if (ShowDlss5Tool) SaveNeuralVersionPin(ManagedComponentKind.Dlss5Tool, value);
    }

    private async Task SetDlss5ToolFoveatedAsync(DetectedGame game, bool on)
    {
        using var op = BeginOperation("dlss5-tool-foveated");
        if (op is null)
        {
            OnPropertyChanged(nameof(Dlss5ToolFoveated));
            return;
        }
        try
        {
            if (on && _dlss5ToolFoveatedVersions.Count == 0) await LoadFoveatedVersionsAsync(op.Token);
            StatusText = T(on ? "Vm.Dlss5ToolSwitchingFoveated" : "Vm.Dlss5ToolSwitchingStock");
            var result = await _managedComponents.SetDlss5ToolFoveatedAsync(game, on, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            if (result.Success && _managedComponents.Dlss5Tool?.GetRecord(game) is not null) await ReprobeGameAsync(game);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        finally
        {
            if (ReferenceEquals(SelectedGame, game)) LoadDlss5Tool(game); // the list of the build now in effect, from Latest
            Host.NotifyDlss();
        }
    }

    // The Foveated list is read when it is first needed (upstream: when the switch is turned on with no list known yet).
    private async Task LoadFoveatedVersionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _dlss5ToolFoveatedVersions = await _managedComponents.ListDlss5ToolVersionsAsync(Dlss5ToolBuild.Foveated, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("nr-versions", "The FoveatedDLSS5 version list could not be read", ex);
        }
    }

    [RelayCommand]
    private void OpenFoveatedDlss5Link()
    {
        if (!LinuxDesktopOpener.TryOpenUri(Dlss5ToolDeploymentService.FoveatedRepositoryUrl, out var error))
            StatusText = UiLocalizationService.Format("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }

    private void NotifyDlss5Tool()
    {
        OnPropertyChanged(nameof(CanConfigureDlss5ToolVersionPin));
        OnPropertyChanged(nameof(CanConfigureDlss5ToolFoveated));
        OnPropertyChanged(nameof(Dlss5ToolFoveated));
        OnPropertyChanged(nameof(Dlss5ToolVersionLabel));
    }

    private void LoadDlss5Tool(DetectedGame game)
    {
        if (!ShowDlss5Tool) return;
        var foveated = _managedComponents.GetDlss5ToolBuild(game) == Dlss5ToolBuild.Foveated;
        var pin = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Tool) ?? LatestDisplay();
        var wasRebuilding = _rebuildingOptionLists;
        _rebuildingOptionLists = true;
        try { ReplaceVersionOptions(Dlss5ToolVersionOptions, foveated ? _dlss5ToolFoveatedVersions : _dlss5ToolStockVersions, pin); }
        finally { _rebuildingOptionLists = wasRebuilding; }
        SelectedDlss5ToolVersion = pin;
        NotifyDlss5Tool();
        if (foveated && _dlss5ToolFoveatedVersions.Count == 0)
            UiExceptionGuard.Forget(RefreshFoveatedListAsync(game), "RefreshFoveatedListAsync");
    }

    private async Task RefreshFoveatedListAsync(DetectedGame game)
    {
        await LoadFoveatedVersionsAsync(CancellationToken.None);
        if (_dlss5ToolFoveatedVersions.Count > 0 && ReferenceEquals(SelectedGame, game)) LoadDlss5Tool(game);
    }

    private void ResetDlss5Tool()
    {
        ReplaceLatestLabel(Dlss5ToolVersionOptions, LatestDisplay());
        SelectedDlss5ToolVersion = LatestDisplay();
        NotifyDlss5Tool();
    }

    private void RefreshDlss5ToolLocalized()
    {
        var pin = IsLatestSelection(SelectedDlss5ToolVersion) ? null : SelectedDlss5ToolVersion;
        ReplaceLatestLabel(Dlss5ToolVersionOptions, pin ?? LatestDisplay());
        SelectedDlss5ToolVersion = pin ?? LatestDisplay();
        OnPropertyChanged(nameof(Dlss5ToolVersionLabel));
    }

    private void ApplyDlss5ToolVersions(IReadOnlyList<GitHubReleaseVersion>? versions)
    {
        if (versions is null || versions.Count == 0) return;
        _dlss5ToolStockVersions = versions;
        if (Dlss5ToolFoveated) return; // the list on show is the Foveated one
        var selected = SelectedDlss5ToolVersion;
        ReplaceVersionOptions(Dlss5ToolVersionOptions, versions, selected);
        SelectedDlss5ToolVersion = selected ?? LatestDisplay();
    }
}
