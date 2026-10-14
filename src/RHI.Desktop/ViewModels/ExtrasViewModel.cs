using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The Extras section and the NR version pins (epic #89 unit 5): Ultimate ASI Loader, ShortFuse / DLSS FG, the
/// per-game Feeder / DX11 Bridge / ShortFuse / NR DLL version pins and the session's NR version lists and latest-version
/// caches (UI-19 TTL, Full Refresh reloads them). State comes from the selected game's published analysis; changes go
/// through the shell's busy gate and store-write helper.
/// </summary>
public sealed partial class ExtrasViewModel : PanelViewModel
{
    private readonly LinuxManagedComponentService _managedComponents;
    private readonly DlssPanelViewModel _dlssPanel;

    /// <summary>Raised while an option list bound as ItemsSource is rebuilt (a ComboBox writes null back meanwhile).</summary>
    private bool _rebuildingOptionLists;

    internal ExtrasViewModel(IPanelHost host, LinuxManagedComponentService managedComponents, DlssPanelViewModel dlssPanel) : base(host)
    {
        _managedComponents = managedComponents;
        _dlssPanel = dlssPanel;
        _versions = new UpstreamVersionCatalog((kind, ct) => _managedComponents.ListNeuralAddonVersionsAsync(kind, ct), ApplyNeuralVersionLists);
    }

    public bool HasSelectedGame => SelectedGame is not null;

    /// <summary>A game was selected (or none): every Extras control shows that game's saved state.</summary>
    internal void OnSelectionChanged(DetectedGame? value)
    {
        if (value is null)
        {
            ResetUalState();
            ResetShortFuseState();
            ResetNeuralVersionPins();
            return;
        }
        RefreshUalState(value);
        RefreshShortFuseState(value);
        LoadNeuralVersionPins(value);
        UiExceptionGuard.Forget(RefreshSelectedLatestVersionsAsync(value), "RefreshSelectedLatestVersionsAsync");
    }

    /// <summary>The selected game's analysis was published: UAL and ShortFuse follow its facts.</summary>
    internal void OnAnalysisPublished(DetectedGame game)
    {
        RefreshUalState(game);
        RefreshShortFuseState(game);
    }

    /// <summary>Busy state, probe state or the selection changed: every derived property of the section.</summary>
    internal void NotifySection()
    {
        OnPropertyChanged(nameof(HasSelectedGame));
        NotifyUal();
        NotifyShortFuse();
    }

    internal void NotifyUalAndShortFuse()
    {
        NotifyUal();
        NotifyShortFuse();
    }

    /// <summary>The pin dropdowns' enabled state and the ShortFuse fact (raised with the DLSS section).</summary>
    internal void NotifyPins()
    {
        OnPropertyChanged(nameof(ShortFuseInstalled));
        OnPropertyChanged(nameof(CanConfigureFeederVersionPin));
        OnPropertyChanged(nameof(CanConfigureBridgeVersionPin));
        OnPropertyChanged(nameof(CanConfigureShortFuseVersionPin));
        OnPropertyChanged(nameof(CanConfigureNeuralRenderingDllVersion));
        OnPropertyChanged(nameof(CanLoadNeuralVersionHistory));
    }

    /// <summary>The DLSS manifest's NR DLL versions, “Latest” first; the selected pin stays an option.</summary>
    internal void ApplyNrDllVersions(IEnumerable<string> versions)
    {
        var nrDllSelected = SelectedNeuralRenderingDllVersion;
        _rebuildingOptionLists = true;
        try
        {
            DlssPanelViewModel.FillVersions(NeuralRenderingDllVersionOptions, versions.Prepend(LatestDisplay()));
            EnsureOption(NeuralRenderingDllVersionOptions, nrDllSelected);
            SelectedNeuralRenderingDllVersion = nrDllSelected ?? LatestDisplay();
        }
        finally { _rebuildingOptionLists = false; }
    }

    /// <summary>The UI language changed: “Latest” labels and the latest-version lines are rebuilt, pins kept.</summary>
    internal void RefreshLocalized()
    {
        _rebuildingOptionLists = true;
        try
        {
            var feederPin = IsLatestSelection(SelectedDlss5FeederVersion) ? null : SelectedDlss5FeederVersion;
            var bridgePin = IsLatestSelection(SelectedDlss5BridgeVersion) ? null : SelectedDlss5BridgeVersion;
            var shortFusePin = IsLatestSelection(SelectedShortFuseVersion) ? null : SelectedShortFuseVersion;
            var nrDllPin = IsLatestSelection(SelectedNeuralRenderingDllVersion) ? null : SelectedNeuralRenderingDllVersion;
            // Rebuild the lists (new "Latest" label, pin kept as an option) before selecting (UI-12, Refs #80).
            ReplaceLatestLabel(Dlss5FeederVersionOptions, feederPin ?? LatestDisplay());
            ReplaceLatestLabel(Dlss5BridgeVersionOptions, bridgePin ?? LatestDisplay());
            ReplaceLatestLabel(ShortFuseVersionOptions, shortFusePin ?? LatestDisplay());
            ReplaceLatestLabel(NeuralRenderingDllVersionOptions, nrDllPin ?? LatestDisplay());
            SelectedDlss5FeederVersion = feederPin ?? LatestDisplay();
            SelectedDlss5BridgeVersion = bridgePin ?? LatestDisplay();
            SelectedShortFuseVersion = shortFusePin ?? LatestDisplay();
            SelectedNeuralRenderingDllVersion = nrDllPin ?? LatestDisplay();

            if (SelectedGame is { } game)
            {
                RefreshUalState(game);
                RefreshShortFuseState(game);
            }
            else
            {
                UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
                ShortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
            }
        }
        finally { _rebuildingOptionLists = false; }
    }

    public ObservableCollection<string> Dlss5FeederVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];

    public ObservableCollection<string> Dlss5BridgeVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];

    public ObservableCollection<string> ShortFuseVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];

    public ObservableCollection<string> NeuralRenderingDllVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];

    public ObservableCollection<string> UalProxyNames { get; } = [];

    // NR version lists and UAL/ShortFuse latest versions (TTL; Full Refresh reloads them) (Refs #86, #117).
    private readonly UpstreamVersionCatalog _versions;

    /// <summary>The session's NR version lists and latest-version caches (Full Refresh reloads them).</summary>
    internal UpstreamVersionCatalog Versions => _versions;

    [ObservableProperty] private string selectedDlss5FeederVersion = UiLocalizationService.Text("Ui.OptionLatest");

    [ObservableProperty] private string selectedDlss5BridgeVersion = UiLocalizationService.Text("Ui.OptionLatest");

    [ObservableProperty] private string selectedShortFuseVersion = UiLocalizationService.Text("Ui.OptionLatest");

    [ObservableProperty] private string selectedNeuralRenderingDllVersion = UiLocalizationService.Text("Ui.OptionLatest");

    [ObservableProperty] private string selectedUalProxyName = "";

    [ObservableProperty] private string ualLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");

    [ObservableProperty] private bool shortFuseAutoConfigEnabled = false;

    [ObservableProperty] private bool shortFuseZzzLoadOrder;

    private bool _loadingShortFuseZzzLoadOrder;

    [ObservableProperty] private string shortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");

    public bool UalInstalled => SelectedGame is not null && Facts?.UalRecord is not null;

    public string UalInstalledProxy => SelectedGame is null
        ? ""
        : Facts?.UalRecord?.Metadata.GetValueOrDefault("UAL.ProxyName") ?? "";

    public string UalInstalledVersion => SelectedGame is null
        ? ""
        : Facts?.UalRecord?.Version ?? "";

    public string UalStatusText => SelectedGame switch
    {
        null => T("Vm.NoGameSelected"),
        { Runner: "Native Linux" } => T("Vm.UnsupportedNativeLinuxTargets"),
        _ when UalInstalled => TF("Vm.InstalledAs", UalInstalledVersion, UalInstalledProxy),
        _ when UalProxyNames.Count == 0 => T("Vm.RequiresDetectedX86X64WindowsExecutable"),
        _ => T("Vm.Ready")
    };

    public bool CanInstallUal => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !UalInstalled
                                 && UalProxyNames.Count > 0 && !string.IsNullOrWhiteSpace(SelectedUalProxyName);

    public bool CanUpdateUal => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && UalInstalled;

    public bool CanInstallOrUpdateUal => UalInstalled ? CanUpdateUal : CanInstallUal;

    public bool CanRemoveUal => CanUpdateUal && !ShortFuseInstalled;

    public bool CanSelectUalProxy => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !UalInstalled && UalProxyNames.Count > 0;

    public string UalActionText => UalInstalled ? T("Vm.Update") : T("Vm.Install");

    public string UalProxyHint
    {
        get
        {
            if (SelectedGame is null) return T("Vm.SelectWindowsGame");
            if (UalInstalled) return TF("Vm.RHIManagesRemoveFirstChooseAnother", UalInstalledProxy);
            if (string.IsNullOrWhiteSpace(SelectedUalProxyName)) return T("Vm.ChooseProxyDLLNameASILoader");
            var notes = new List<string>();
            if (UltimateAsiLoaderDeploymentService.RecommendedNames.Contains(SelectedUalProxyName, StringComparer.OrdinalIgnoreCase))
                notes.Add(T("Vm.RecommendedByRHI"));
            if (UltimateAsiLoaderDeploymentService.RhiConflictNames.Contains(SelectedUalProxyName, StringComparer.OrdinalIgnoreCase))
                notes.Add(T("Vm.MayConflictReShadeOptiScaler"));
            if (Facts?.UalProxyFilesPresent.Contains(SelectedUalProxyName) == true)
                notes.Add(TF("Vm.ExistingWillPreservedAsUnlessManaged", SelectedUalProxyName, UltimateAsiLoaderDeploymentService.HookedName(SelectedUalProxyName)));
            return notes.Count == 0
                ? T("Vm.WineOverrideScopedSelectedGameNative")
                : string.Join(" · ", notes) + ".";
        }
    }

    public bool ShortFuseInstalled => SelectedGame is not null && Facts?.ShortFuseManaged == true;

    public string ShortFuseInstalledVersion => SelectedGame is null ? "" : Facts?.ShortFuseRecord?.Version ?? "";

    private ManagedComponent? CurrentShortFuseComponent
        => CurrentAnalysis?.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ShortFuse);

    public string ShortFuseStatusText => SelectedGame switch
    {
        null => T("Vm.NoGameSelected"),
        { Runner: "Native Linux" } => T("Vm.UnsupportedNativeLinuxTargets"),
        _ when ShortFuseInstalled => TF("Vm.ShortFuseEnabledSummary", ShortFuseInstalledVersion, ShortFuseAutoConfigEnabled ? T("Vm.AutomaticASIConfiguration") : T("Vm.ManualASIConfiguration")),
        _ => TF("Vm.ShortFuseDisabledSummary", ShortFuseAutoConfigEnabled ? T("Vm.Enabled") : T("Vm.OptionalDisabled"))
    };

    public string ShortFuseActionText => ShortFuseInstalled ? T("Vm.Remove") : T("Vm.Install");

    public bool ShortFuseNotInstalled => !ShortFuseInstalled;

    public bool CanInstallOrRemoveShortFuse
        => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy
           && (ShortFuseInstalled || (CurrentShortFuseComponent?.Availability == ComponentAvailability.Available
                                      && CurrentShortFuseComponent.ActionAllowed));

    public string ShortFuseActionHint
        => ShortFuseInstalled
            ? T("Vm.RemovesShortFuseTransactionallyRestoresPreviousD")
            : CurrentShortFuseComponent?.ActionAllowed == true
                ? PresentOr(CurrentShortFuseComponent.Note, T("Vm.ShortFuseAvailableGame"))
                : PresentOr(CurrentShortFuseComponent?.Note, T("Vm.ShortFuseWillBecomeAvailableAfterGame"));

    public bool CanConfigureShortFuse => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !ShortFuseInstalled;

    // Pins stay editable while installed: a change swaps the add-on in place (upstream 2.7.6).
    public bool CanConfigureFeederVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    public bool CanConfigureBridgeVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    public bool CanConfigureShortFuseVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    public bool CanConfigureNeuralRenderingDllVersion => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    public bool CanLoadNeuralVersionHistory => !IsBusy;

    partial void OnSelectedDlss5FeederVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.Dlss5Feeder, value);

    partial void OnSelectedDlss5BridgeVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.Dlss5Dx11Bridge, value);

    partial void OnSelectedShortFuseVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.ShortFuse, value);

    partial void OnSelectedNeuralRenderingDllVersionChanged(string value)
        => SaveNeuralRenderingDllPin(value);

    private static void ReplaceLatestLabel(ObservableCollection<string> options, string selected)
    {
        var tags = options.Where(x => !IsLatestSelection(x)).ToArray();
        options.Clear();
        options.Add(LatestDisplay());
        foreach (var tag in tags)
            if (!options.Contains(tag, StringComparer.OrdinalIgnoreCase)) options.Add(tag);
        EnsureOption(options, selected);
    }

    [RelayCommand]
    private async Task CheckUalUpdateAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore");
            return;
        }
        using var op = BeginOperation("ual-check");
        if (op is null) return;
        NotifyUalCommands();
        try
        {
            StatusText = T("Vm.RefreshingLatestUltimateASILoaderVersion");
            var game = SelectedGame;
            var status = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game, op.Token);
            if (status.Success && GetUalArchitecture(game) is { } architecture)
                _versions.UalLatest.Set(architecture, status.LatestVersion);
            UalLatestVersionText = status.Success
                ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, status.LatestVersion)
                : op.CancelRequested
                    ? UiLocalizationService.Text("Ui.NotCheckedYet")
                    : TF("Vm.LatestVersionUnavailable", UiLocalizationService.Present(status.Message));
            StatusText = OutcomeText(op, status.Success, status.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            UalLatestVersionText = TF("Vm.UpdateCheckFailed", UiLocalizationService.Present(ex.Message));
            StatusText = UalLatestVersionText;
        }
        finally
        {
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task InstallOrUpdateUalAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        using var op = BeginOperation("ual");
        if (op is null) return;
        NotifyUalCommands();
        try
        {
            ComponentOperationResult result;
            if (_managedComponents.GetUltimateAsiLoaderRecord(game) is not null)
            {
                StatusText = T("Vm.UpdatingUltimateASILoader");
                result = await _managedComponents.UpdateUltimateAsiLoaderAsync(game, op.Token);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SelectedUalProxyName))
                {
                    StatusText = T("Vm.ChooseProxyNameUltimateASILoader");
                    return;
                }
                StatusText = TF("Vm.InstallingUltimateASILoaderAs", SelectedUalProxyName);
                result = await _managedComponents.InstallUltimateAsiLoaderAsync(game, SelectedUalProxyName, op.Token);
            }

            StatusText = OutcomeText(op, result.Success, result.Message);
            if (result.Success)
            {
                await ReprobeKeepingStatusAsync(game); // the new proxy DLL is in the file index (Refs #83)
                var update = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game, op.Token);
                if (update.Success && GetUalArchitecture(game) is { } architecture)
                    _versions.UalLatest.Set(architecture, update.LatestVersion);
                UalLatestVersionText = update.Success
                    ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, update.LatestVersion)
                    : UalLatestVersionText;
            }
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.UltimateASILoader", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task RemoveUalAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        using var op = BeginOperation("ual-remove");
        if (op is null) return;
        NotifyUalCommands();
        try
        {
            StatusText = T("Vm.RemovingUltimateASILoaderRestoringChained");
            var result = await _managedComponents.RemoveUltimateAsiLoaderAsync(game, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            if (result.Success)
            {
                await ReprobeKeepingStatusAsync(game); // Refs #83
                UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
            }
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.UltimateASILoaderRemovalFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task CheckShortFuseUpdateAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore2");
            return;
        }
        using var op = BeginOperation("shortfuse-check");
        if (op is null) return;
        try
        {
            var game = SelectedGame;
            var status = await _managedComponents.CheckShortFuseLatestReleaseAsync(game, op.Token);
            if (status.Success)
                _versions.ShortFuseLatest.Set(ManagedComponentKind.ShortFuse, status.LatestVersion);
            ShortFuseLatestVersionText = status.Success
                ? FormatLatestVersionSummary(_managedComponents.GetShortFuseRecord(game)?.Version, status.LatestVersion)
                : op.CancelRequested
                    ? UiLocalizationService.Text("Ui.NotCheckedYet")
                    : TF("Vm.LatestVersionUnavailable", UiLocalizationService.Present(status.Message));
            StatusText = OutcomeText(op, status.Success, status.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex) { ShortFuseLatestVersionText = TF("Vm.ShortFuseUpdateCheckFailed", UiLocalizationService.Present(ex.Message)); StatusText = ShortFuseLatestVersionText; }
        finally { NotifyShortFuse(); }
    }

    [RelayCommand]
    private async Task InstallOrRemoveShortFuseAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        using var op = BeginOperation("shortfuse");
        if (op is null) return;
        try
        {
            var removing = _managedComponents.IsShortFuseManaged(game);
            // ShortFuse has no manifest key of its own, but installing it adds RHI-owned ReShade when absent.
            if (!removing && !await Host.AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ShortFuse, "ShortFuse", installsTarget: true, Host.Session.CachedFiles(game))) return;
            StatusText = removing
                ? T("Vm.RemovingShortFuseRestoringPreviousStack")
                : T("Vm.InstallingShortFuseDLSSFrameGenerationOrchestrat");
            var result = removing
                ? await _managedComponents.RemoveShortFuseAsync(game, op.Token)
                : await _managedComponents.InstallShortFuseAsync(game, ShortFuseAutoConfigEnabled, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            if (result.Success)
            {
                await ReprobeGameAsync(game);
                RefreshShortFuseState(game);
            }
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex) { StatusText = TF("Vm.ShortFuse", UiLocalizationService.Present(ex.Message)); }
        finally { NotifyShortFuse(); NotifyUal(); Host.NotifyDlss(); }
    }

    private void ResetNeuralVersionPins()
    {
        ReplaceLatestLabel(Dlss5FeederVersionOptions, LatestDisplay());
        ReplaceLatestLabel(Dlss5BridgeVersionOptions, LatestDisplay());
        ReplaceLatestLabel(ShortFuseVersionOptions, LatestDisplay());
        ReplaceLatestLabel(NeuralRenderingDllVersionOptions, LatestDisplay());
        SelectedDlss5FeederVersion = LatestDisplay();
        SelectedDlss5BridgeVersion = LatestDisplay();
        SelectedShortFuseVersion = LatestDisplay();
        SelectedNeuralRenderingDllVersion = LatestDisplay();
    }

    private void LoadNeuralVersionPins(DetectedGame game)
    {
        // Option first, then the selection: a ComboBox drops a SelectedItem missing from its items (UI-12, Refs #80).
        var feeder = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Feeder) ?? LatestDisplay();
        var bridge = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Dx11Bridge) ?? LatestDisplay();
        var shortFuse = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.ShortFuse) ?? LatestDisplay();
        var nrDll = _managedComponents.GetNeuralRenderingDllVersionPin(game) ?? LatestDisplay();
        EnsureOption(Dlss5FeederVersionOptions, feeder);
        EnsureOption(Dlss5BridgeVersionOptions, bridge);
        EnsureOption(ShortFuseVersionOptions, shortFuse);
        EnsureOption(NeuralRenderingDllVersionOptions, nrDll);
        SelectedDlss5FeederVersion = feeder;
        SelectedDlss5BridgeVersion = bridge;
        SelectedShortFuseVersion = shortFuse;
        SelectedNeuralRenderingDllVersion = nrDll;
    }

    private void SaveNeuralVersionPin(ManagedComponentKind kind, string value)
    {
        // A null/empty value only ever arrives from an ItemsSource rebuild, never from the user.
        if (_rebuildingOptionLists || string.IsNullOrWhiteSpace(value) || SelectedGame is null) return;
        var game = SelectedGame;
        var pin = IsLatestSelection(value) ? null : value;
        var previous = _managedComponents.GetNeuralAddonVersionPin(game, kind);
        // Loading a game's pins re-sets the same value; only a real change persists or swaps.
        if (string.Equals(previous, pin, StringComparison.OrdinalIgnoreCase)) return;
        if (TryPersist(() => _managedComponents.SetNeuralAddonVersionPin(game, kind, pin), "Ui.WhatGamePreferences", sessionOnly: false) is not null)
        {
            LoadNeuralVersionPins(game); // the dropdown goes back to what is saved
            return;
        }
        // A legacy add-on-only DLSS5 Feeder keeps the pin for its reinstall instead of a swap that must fail (#115).
        var change = _managedComponents.PlanNeuralPinChange(game, kind);
        if (change == NeuralPinChange.SwapInPlace)
            UiExceptionGuard.Forget(SwapNeuralVersionInPlaceAsync(game, kind, previous), "SwapNeuralVersionInPlaceAsync");
        else if (change == NeuralPinChange.ReinstallNeeded)
            StatusText = T("Vm.Dlss5FeederLegacyPinSaved");
    }

    private void SaveNeuralRenderingDllPin(string value)
    {
        if (_rebuildingOptionLists || string.IsNullOrWhiteSpace(value) || SelectedGame is null) return;
        var game = SelectedGame;
        var pin = IsLatestSelection(value) ? null : value;
        var previous = _managedComponents.GetNeuralRenderingDllVersionPin(game);
        if (string.Equals(previous, pin, StringComparison.Ordinal)) return;
        if (TryPersist(() => _managedComponents.SetNeuralRenderingDllVersionPin(game, pin), "Ui.WhatGamePreferences", sessionOnly: false) is not null)
        {
            LoadNeuralVersionPins(game);
            return;
        }
        if (_dlssPanel.OrchestratedDlssStackManaged)
            UiExceptionGuard.Forget(SwapNeuralVersionInPlaceAsync(game, kind: null, previous), "SwapNeuralVersionInPlaceAsync");
    }

    /// <summary>
    /// Applies a changed pin to an installed NR method by swapping only the add-on (kind set) or
    /// the orchestrated nvngx_dlssnr.dll (kind null). On failure the previous pin and selection
    /// are restored so the dropdown keeps describing what is installed.
    /// </summary>
    private async Task SwapNeuralVersionInPlaceAsync(DetectedGame game, ManagedComponentKind? kind, string? previousPin)
    {
        void RevertPin()
        {
            if (kind is { } addon) TryPersist(() => _managedComponents.SetNeuralAddonVersionPin(game, addon, previousPin), "Ui.WhatGamePreferences", sessionOnly: false);
            else TryPersist(() => _managedComponents.SetNeuralRenderingDllVersionPin(game, previousPin), "Ui.WhatGamePreferences", sessionOnly: false);
            if (ReferenceEquals(SelectedGame, game)) LoadNeuralVersionPins(game);
        }

        using var op = BeginOperation("nr-swap");
        if (op is null)
        {
            RevertPin();
            return;
        }
        try
        {
            StatusText = T("Vm.SwappingNRVersionPlace");
            var result = kind is { } addon
                ? await _managedComponents.SwapNeuralAddonVersionAsync(game, addon, op.Token)
                : await _managedComponents.SwapNeuralRenderingDllAsync(game, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            if (result.Success)
            {
                await ReprobeGameAsync(game);
                RefreshShortFuseState(game);
                return;
            }
            RevertPin();
        }
        catch (Exception ex)
        {
            StatusText = op.WasCancelled(ex) ? OperationCancelledText() : TF("Vm.FailedSwapNRVersion", UiLocalizationService.Present(ex.Message));
            try { RevertPin(); }
            catch (Exception revertError) when (revertError is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn("nr-swap", "Could not put the previous NR version pin back", revertError); // the status line already reports the failure
            }
        }
        finally
        {
            Host.NotifyDlss();
        }
    }

    [RelayCommand]
    private async Task LoadNeuralVersionHistoryAsync()
    {
        using var op = BeginOperation("nr-versions");
        if (op is null) return;
        try
        {
            StatusText = T("Vm.RefreshingNeuralRenderingAddVersions");
            await EnsureNeuralVersionCatalogAsync(forceRefresh: true, reportStatus: true, op.Token);
        }
        finally
        {
            Host.NotifyDlss();
        }
    }

    internal Task EnsureNeuralVersionCatalogAsync(bool forceRefresh = false, bool reportStatus = false, CancellationToken cancellationToken = default)
        => ReportNeuralCatalogLoadAsync(_versions.NeuralVersions.EnsureAsync(forceRefresh, cancellationToken), reportStatus, cancellationToken);

    internal async Task ReportNeuralCatalogLoadAsync(Task<NeuralCatalogOutcome> load, bool reportStatus = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var outcome = await load;
            if (!reportStatus) return;
            if (outcome == NeuralCatalogOutcome.Loaded) StatusText = T("Vm.NRVersionsRefreshedLatestRemainsDefault");
            else if (outcome == NeuralCatalogOutcome.AlreadyLoaded) StatusText = T("Vm.NRVersionsUpDate");
        }
        catch (Exception ex)
        {
            if (reportStatus && ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                StatusText = OperationCancelledText();
            else if (reportStatus)
                StatusText = TF("Vm.FailedRefreshNRVersions", UiLocalizationService.Present(ex.Message));
            else
                AppLog.Warn("nr-versions", "Automatic NR version catalog refresh failed", ex);
        }
    }

    private void ApplyNeuralVersionLists(NeuralVersionLists lists)
    {
        var feederSelected = SelectedDlss5FeederVersion;
        var bridgeSelected = SelectedDlss5BridgeVersion;
        var shortFuseSelected = SelectedShortFuseVersion;
        _rebuildingOptionLists = true;
        try
        {
            ReplaceVersionOptions(Dlss5FeederVersionOptions, lists.Dlss5Feeder, feederSelected);
            ReplaceVersionOptions(Dlss5BridgeVersionOptions, lists.Dlss5Bridge, bridgeSelected);
            ReplaceVersionOptions(ShortFuseVersionOptions, lists.ShortFuse, shortFuseSelected);
            // Clearing the list nulled the bound selection; restore it now that the option exists again.
            SelectedDlss5FeederVersion = feederSelected ?? LatestDisplay();
            SelectedDlss5BridgeVersion = bridgeSelected ?? LatestDisplay();
            SelectedShortFuseVersion = shortFuseSelected ?? LatestDisplay();
        }
        finally { _rebuildingOptionLists = false; }
    }

    private static void ReplaceVersionOptions(ObservableCollection<string> target, IReadOnlyList<GitHubReleaseVersion> versions, string selected)
    {
        target.Clear();
        target.Add(LatestDisplay());
        foreach (var version in versions)
            if (!target.Contains(version.Tag, StringComparer.OrdinalIgnoreCase))
                target.Add(version.Tag);
        EnsureOption(target, selected);
    }

    /// <summary>The executable's architecture for UAL's per-architecture release (off the UI thread when it is read).</summary>
    private static PeArchitecture? GetUalArchitecture(DetectedGame game)
        => UalArchitecture(game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath));

    private static PeArchitecture? UalArchitecture(PeArchitecture architecture)
        => architecture is PeArchitecture.X86 or PeArchitecture.X64 ? architecture : null;

    private string FormatLatestVersionSummary(string? installedVersion, string? latestVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedVersion) && !string.IsNullOrWhiteSpace(latestVersion))
        {
            if (string.Equals(installedVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
                return TF("Vm.InstalledAlreadyLatest", installedVersion);
            return TF("Vm.InstalledLatest", installedVersion, latestVersion);
        }
        if (!string.IsNullOrWhiteSpace(latestVersion))
            return TF("Vm.LatestVersion", latestVersion);
        if (!string.IsNullOrWhiteSpace(installedVersion))
            return TF("Vm.InstalledLoadingLatestVersion", installedVersion);
        return T("Vm.LoadingLatestVersion");
    }

    private string? CachedUalLatestVersion(PeArchitecture architecture)
        => UalArchitecture(architecture) is { } ual ? _versions.UalLatest.TryGetFresh(ual) : null;

    private async Task<string?> GetUalLatestVersionAsync(DetectedGame game, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var architecture = GetUalArchitecture(game);
        if (architecture is null) return null;
        return await _versions.UalLatest.GetAsync(architecture.Value, async () =>
        {
            var status = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game, cancellationToken);
            return status.Success ? status.LatestVersion : null;
        }, forceRefresh);
    }

    private async Task<string?> GetShortFuseLatestVersionAsync(DetectedGame game, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        return await _versions.ShortFuseLatest.GetAsync(ManagedComponentKind.ShortFuse, async () =>
        {
            var status = await _managedComponents.CheckShortFuseLatestReleaseAsync(game, cancellationToken);
            return status.Success ? status.LatestVersion : null;
        }, forceRefresh);
    }

    private async Task RefreshSelectedLatestVersionsAsync(DetectedGame game)
    {
        try
        {
            var ualTask = GetUalLatestVersionAsync(game);
            var shortFuseTask = GetShortFuseLatestVersionAsync(game);
            await Task.WhenAll(ualTask, shortFuseTask);
            if (!ReferenceEquals(SelectedGame, game)) return;

            UalLatestVersionText = !string.IsNullOrWhiteSpace(ualTask.Result)
                ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, ualTask.Result)
                : GetUalArchitecture(game) is null
                    ? T("Vm.LatestVersionUnavailableNoX86X64")
                    : T("Vm.LatestVersionCurrentlyUnavailable");
            ShortFuseLatestVersionText = !string.IsNullOrWhiteSpace(shortFuseTask.Result)
                ? FormatLatestVersionSummary(_managedComponents.GetShortFuseRecord(game)?.Version, shortFuseTask.Result)
                : T("Vm.LatestVersionCurrentlyUnavailable");
            NotifyUal();
            NotifyShortFuse();
        }
        catch (Exception ex)
        {
            AppLog.Warn("latest-version", "Automatic latest-version refresh failed", ex);
        }
    }

    private void ResetShortFuseState()
    {
        ShortFuseAutoConfigEnabled = false;
        SetShortFuseZzzLoadOrderSilently(false);
        ShortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        NotifyShortFuse();
    }

    private void RefreshShortFuseState(DetectedGame game)
    {
        // Read from the selected game's analysis (no disk access on the UI thread); defaults until it is published.
        var facts = ReferenceEquals(game, SelectedGame) ? Facts : null;
        ShortFuseAutoConfigEnabled = facts?.ShortFuseAutoConfigEnabled ?? false;
        SetShortFuseZzzLoadOrderSilently(facts?.ShortFuseZzzLoadOrderInEffect ?? false);
        ShortFuseLatestVersionText = FormatLatestVersionSummary(facts?.ShortFuseRecord?.Version, _versions.ShortFuseLatest.TryGetFresh(ManagedComponentKind.ShortFuse));
        NotifyShortFuse();
    }

    private void NotifyShortFuse()
    {
        OnPropertyChanged(nameof(ShortFuseInstalled));
        OnPropertyChanged(nameof(ShortFuseInstalledVersion));
        OnPropertyChanged(nameof(ShortFuseStatusText));
        OnPropertyChanged(nameof(ShortFuseActionText));
        OnPropertyChanged(nameof(ShortFuseNotInstalled));
        OnPropertyChanged(nameof(CanInstallOrRemoveShortFuse));
        OnPropertyChanged(nameof(ShortFuseActionHint));
        OnPropertyChanged(nameof(CanConfigureShortFuse));
        OnPropertyChanged(nameof(CanConfigureShortFuseZzzLoadOrder));
        _dlssPanel.NotifyStackManaged();
        CheckShortFuseUpdateCommand.NotifyCanExecuteChanged();
        InstallOrRemoveShortFuseCommand.NotifyCanExecuteChanged();
    }

    // Upstream 2.7.9 ZZZ load order: unlike Auto Config it stays live while ShortFuse is
    // installed, because toggling renames the owned add-on in place.
    public bool CanConfigureShortFuseZzzLoadOrder => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    private void SetShortFuseZzzLoadOrderSilently(bool value)
    {
        _loadingShortFuseZzzLoadOrder = true;
        try { ShortFuseZzzLoadOrder = value; }
        finally { _loadingShortFuseZzzLoadOrder = false; }
    }

    partial void OnShortFuseZzzLoadOrderChanged(bool value)
    {
        if (_loadingShortFuseZzzLoadOrder || SelectedGame is null) return;
        UiExceptionGuard.Forget(ApplyShortFuseZzzLoadOrderAsync(SelectedGame, value), "ApplyShortFuseZzzLoadOrderAsync");
    }

    private async Task ApplyShortFuseZzzLoadOrderAsync(DetectedGame game, bool enabled)
    {
        using var op = BeginOperation("shortfuse-load-order");
        if (op is null)
        {
            if (ReferenceEquals(SelectedGame, game))
                SetShortFuseZzzLoadOrderSilently(_managedComponents.GetShortFuseZzzLoadOrderInEffect(game));
            return;
        }
        try
        {
            var result = await _managedComponents.SetShortFuseZzzLoadOrderAsync(game, enabled, op.Token);
            StatusText = !result.Success
                ? UiLocalizationService.Present(result.Message)
                : enabled
                    ? T("Vm.ShortFuseWillLoadLastZzzRenodx")
                    : T("Vm.ShortFuseUsesDefaultNameRenodxDlss");
            if (result.Success && ReferenceEquals(SelectedGame, game)) await ReprobeKeepingStatusAsync(game); // the add-on was renamed (Refs #83)
        }
        catch (Exception ex) when (op.WasCancelled(ex)) { StatusText = OperationCancelledText(); }
        catch (Exception ex) { StatusText = TF("Vm.ShortFuse", UiLocalizationService.Present(ex.Message)); }
        finally
        {
            if (ReferenceEquals(SelectedGame, game))
                SetShortFuseZzzLoadOrderSilently(_managedComponents.GetShortFuseZzzLoadOrderInEffect(game));
        }
    }

    private void ResetUalState()
    {
        UalProxyNames.Clear();
        SelectedUalProxyName = "";
        UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        NotifyUal();
    }

    private void RefreshUalState(DetectedGame game)
    {
        // Read from the selected game's analysis (no disk access on the UI thread); empty until it is published.
        var facts = ReferenceEquals(game, SelectedGame) ? Facts : null;
        var previous = SelectedUalProxyName;
        UalProxyNames.Clear();
        foreach (var name in facts?.UalProxyNames ?? [])
            UalProxyNames.Add(name);

        var record = facts?.UalRecord;
        var installed = record?.Metadata.GetValueOrDefault("UAL.ProxyName");
        if (!string.IsNullOrWhiteSpace(installed))
            SelectedUalProxyName = installed;
        else if (!string.IsNullOrWhiteSpace(previous) &&
                 UalProxyNames.Contains(previous, StringComparer.OrdinalIgnoreCase))
            SelectedUalProxyName = UalProxyNames.First(x => x.Equals(previous, StringComparison.OrdinalIgnoreCase));
        else
            SelectedUalProxyName = facts?.UalRecommendedProxy ?? "";

        UalLatestVersionText = FormatLatestVersionSummary(record?.Version, facts is null ? null : CachedUalLatestVersion(facts.Architecture));
        NotifyUal();
    }

    private void NotifyUal()
    {
        OnPropertyChanged(nameof(UalInstalled));
        OnPropertyChanged(nameof(UalInstalledProxy));
        OnPropertyChanged(nameof(UalInstalledVersion));
        OnPropertyChanged(nameof(UalStatusText));
        OnPropertyChanged(nameof(CanInstallUal));
        OnPropertyChanged(nameof(CanUpdateUal));
        OnPropertyChanged(nameof(CanInstallOrUpdateUal));
        OnPropertyChanged(nameof(CanRemoveUal));
        OnPropertyChanged(nameof(CanSelectUalProxy));
        OnPropertyChanged(nameof(UalActionText));
        OnPropertyChanged(nameof(UalProxyHint));
        NotifyUalCommands();
    }

    private void NotifyUalCommands()
    {
        CheckUalUpdateCommand.NotifyCanExecuteChanged();
        InstallOrUpdateUalCommand.NotifyCanExecuteChanged();
        RemoveUalCommand.NotifyCanExecuteChanged();
    }
}
