using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The ReShade profile section (epic #89, concern 12; was in the shell): channel, legacy version (#186), custom DLL,
/// D3DCompiler 47 path and its Protontricks / Winetricks installers, "Manage ReShade.ini", the quick shader packs, Check
/// update and Apply profile; plus the Shaders/Add-ons manager behind "Full shader manager" (snapshot, a pack's files,
/// Apply) and Settings' "Apply ReShade defaults to the selected game". Loads the selected game's profile (or the global
/// default with no game) when the selection changes.
/// </summary>
public sealed partial class ReShadeProfileViewModel : PanelViewModel
{
    private readonly LinuxManagedComponentService _components;
    private readonly HashSet<string> _selectedShaderPackIds = new(StringComparer.OrdinalIgnoreCase);

    internal ReShadeProfileViewModel(IPanelHost host, LinuxManagedComponentService components) : base(host)
        => _components = components;

    // Labels and values of every option list come from OptionLists (one mapping, shared with the dialogs; Refs #87).
    public ObservableCollection<string> ReShadeChannels { get; } = [.. OptionLists.ReShadeChannel.Items];
    [ObservableProperty] private string selectedReShadeChannel = UiLocalizationService.Text("Ui.OptionStable");
    [ObservableProperty] private string reShadeLegacyVersion = "6.4.1";
    [ObservableProperty] private string reShadeCustomDllPath = "";
    [ObservableProperty] private string reShadeD3DCompiler47Path = "";
    [ObservableProperty] private bool reShadeManageIni = true;
    [ObservableProperty] private bool shaderCrosireMaster;
    [ObservableProperty] private bool shaderCrosireLegacy;
    [ObservableProperty] private bool shaderClshortfuse;
    [ObservableProperty] private bool shaderMaxG2D;
    [ObservableProperty] private string reShadeUpdateText = UiLocalizationService.Text("Ui.ReShadeApplyHint");

    /// <summary>No game yet (start-up): the global default channel and its line.</summary>
    internal void ShowGlobalDefault()
    {
        SelectedReShadeChannel = ReShadeChannelDisplay(Host.Settings.ReShadeChannel);
        ReShadeManageIni = Host.Settings.ReShadeManageIni;
        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(Host.Settings.ReShadeChannel));
    }

    /// <summary>A game was selected (or none): its profile, or the global default.</summary>
    internal void OnSelectionChanged(DetectedGame? game)
    {
        if (game is null) ResetReShadeProfile();
        else LoadReShadeProfile(game);
    }

    /// <summary>The game's files changed (re-analysed): its saved profile is shown again.</summary>
    internal void Reload(DetectedGame game) => LoadReShadeProfile(game);

    /// <summary>The UI language changed: the channel list is relabelled, the selection and the default line follow.</summary>
    internal void RefreshLocalized()
    {
        var channel = ReShadeChannelValue(SelectedReShadeChannel);
        OptionLists.ReShadeChannel.Fill(ReShadeChannels);
        SelectedReShadeChannel = ReShadeChannelDisplay(channel);
        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(Host.Settings.ReShadeChannel));
        RelabelSetModes(); // #179
    }

    /// <summary>The selection or its analysis changed: the D3DCompiler 47 installers and their hint follow.</summary>
    internal void NotifyD3DCompiler()
    {
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithProtontricks));
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithWinetricks));
        NotifyD3DCompilerText();
    }

    internal void NotifyD3DCompilerText()
    {
        OnPropertyChanged(nameof(D3DCompilerWinetricksButtonText));
        OnPropertyChanged(nameof(D3DCompilerToolHint));
    }

    private static string OrphanedProtonText(DetectedGame game)
        => TF("Vm.WinetricksUnavailablePrefixWasBuiltBy",
              game.CompatibilityTool ?? game.Runner ?? "?");
    public bool CanInstallD3DCompilerWithProtontricks => SelectedGame?.UsesProton == true && HasConfirmedPatchTarget;
    public bool CanInstallD3DCompilerWithWinetricks
        => HasConfirmedPatchTarget && SelectedGame is not null && Facts is { CanInstallWithWinetricks: true };
    public string D3DCompilerWinetricksButtonText
        => SelectedGame is { } game && D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
            ? TF("Vm.InstallViaWinetricks", game.CompatibilityTool ?? game.Runner ?? game.Source)
            : T("Vm.InstallViaWinetricks2");
    public string D3DCompilerToolHint
        => SelectedGame switch
        {
            null => T("Vm.SelectGameDetermineCorrectD3DCompilerHelper"),
            { UsesProton: true } => T("Vm.SteamProtonTargetProtontricksUsedSteam"),
            { } game when Facts is { OrphanedProtonPrefix: true } => OrphanedProtonText(game),
            { } game when D3DCompilerToolInstaller.IsRuntimeUnknownPrefix(game) => T("Vm.WinetricksUnavailableRuntimeUnknown"),
            { SteamAppId: null } game when D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
                => TF("Vm.TargetWinetricksUsesActiveMatchingPrefix", game.Source, game.CompatibilityTool ?? game.Runner ?? T("Vm.LauncherRunner")),
            { SteamAppId: null, PrefixPath: not null } => T("Vm.StandaloneSystemWineTargetWinetricksRuns"),
            _ => T("Vm.NoInitializedWineProtonPrefixWas")
        };
    private static string ReShadeChannelDisplay(string value) => OptionLists.ReShadeChannel.Display(value);

    private static string ReShadeChannelValue(string? display) => OptionLists.ReShadeChannel.Value(display);

    public bool IsReShadeLegacy => ReShadeChannelValue(SelectedReShadeChannel).Equals("Legacy", StringComparison.OrdinalIgnoreCase);
    public bool IsReShadeCustom => ReShadeChannelValue(SelectedReShadeChannel).Equals("Custom", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedReShadeChannelChanged(string value)
    {
        OnPropertyChanged(nameof(IsReShadeLegacy));
        OnPropertyChanged(nameof(IsReShadeCustom));
    }

    public string ApplyGlobalReShadeSettingsToSelectedGame(LinuxAppSettings settings)
    {
        if (SelectedGame is null) return T("Vm.SelectGameFirst");
        if (IsBusy) return T("Vm.AnotherRHIOperationAlreadyRunning");
        if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return T("Vm.WindowsReShadeSettingsNotAppliedNative");
        if (!SelectedGame.HasConfirmedPatchTarget)
            return T("Vm.SetTargetGameExecutableEditGame");
        if (XboxWriteGate.Refusal(SelectedGame) is { } xbox) return UiLocalizationService.Present(xbox); // w3/scan hook (Refs #90)
        if (CurrentAnalysis is not { } probe ||
            !probe.Components.Any(component => component.Kind == ManagedComponentKind.ReShade &&
                component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable))
            return T("Vm.InstallDetectReShadeSelectedGameBefore");
        var path = ReShadeGlobalSettingsApplier.ApplyToGame(settings, SelectedGame);
        return TF("Vm.ReShadeCaptureHotkeyDefaultsAppliedAt", path);
    }

    [RelayCommand]
    private async Task ApplyReShadeProfileAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        if (CurrentAnalysis is not { } probe)
        {
            StatusText = T("Vm.WaitGameAnalysisFinishBeforeChanging2");
            return;
        }

        var component = probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ReShade);
        if (component is null)
        {
            StatusText = T("Vm.ReShadeComponentStateUnavailableGame");
            return;
        }
        if ((component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable) && !component.ManagedByRhi)
        {
            StatusText = T("Vm.ExternalReShadeDetectedRHIWillNot");
            return;
        }
        if (component.Availability == ComponentAvailability.Unsupported)
        {
            var note = UiLocalizationService.Present(component.Note);
            StatusText = string.IsNullOrWhiteSpace(note)
                ? T("Vm.ReShadeNotCompatibleTarget")
                : note;
            return;
        }

        if (!Enum.TryParse<ReShadeChannel>(ReShadeChannelValue(SelectedReShadeChannel), true, out var channel))
        {
            StatusText = T("Vm.UnknownReShadeChannel");
            return;
        }

        var packSet = new HashSet<string>(_selectedShaderPackIds, StringComparer.OrdinalIgnoreCase);
        SetPack(packSet, "CrosireMaster", ShaderCrosireMaster);
        SetPack(packSet, "CrosireLegacy", ShaderCrosireLegacy);
        SetPack(packSet, "ClshortfuseShaders", ShaderClshortfuse);
        SetPack(packSet, "MaxG2DSimpleHDR", ShaderMaxG2D);
        var packs = packSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var options = new ReShadeInstallOptions
        {
            Channel = channel,
            LegacyVersion = ReShadeLegacyVersion,
            CustomDllPath = ReShadeCustomDllPath,
            D3DCompiler47Path = ReShadeD3DCompiler47Path,
            ManageIni = ReShadeManageIni,
            ShaderPackIds = packs
        };

        using var op = BeginOperation("reshade");
        if (op is null) return;
        try
        {
            // Applying a profile installs ReShade when it is not there yet (Available or Unknown), and RE Framework on an
            // RE Engine game when that is missing; those are the installs the manifest warns about.
            if (!await Host.AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, component.DisplayName,
                    installsTarget: ManifestInstallWarning.IsFirstInstall(component.Availability), probe.Files)) return;
            StatusText = TF("Vm.ApplyingReShadeProfile", channel);
            var result = await _components.ApplyReShadeAsync(game, options, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                _selectedShaderPackIds.Clear();
                _selectedShaderPackIds.UnionWith(options.ShaderPackIds);
                await ReprobeGameAsync(game);
            }
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.ReShadeProfileFailed", UiLocalizationService.Present(ex.Message)));
        }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithProtontricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithProtontricks) return;
        var game = SelectedGame;
        using var op = BeginOperation("d3dcompiler");
        if (op is null) return;
        try
        {
            StatusText = TF("Vm.InstallingNativeD3DCompiler47Protontricks", game.Name);
            var result = await _components.InstallD3DCompilerWithProtontricksAsync(game, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.FailedInstallD3DCompilerProtontricks", UiLocalizationService.Present(ex.Message)));
        }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithWinetricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithWinetricks) return;
        var game = SelectedGame;
        using var op = BeginOperation("d3dcompiler");
        if (op is null) return;
        try
        {
            StatusText = TF("Vm.InstallingNativeD3DCompiler47", game.Name, game.CompatibilityTool ?? game.Runner ?? "Winetricks");
            var result = await _components.InstallD3DCompilerWithWinetricksAsync(game, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.FailedInstallD3DCompilerWinetricks", UiLocalizationService.Present(ex.Message)));
        }
    }

    [RelayCommand]
    private async Task CheckReShadeUpdateAsync()
    {
        if (SelectedGame is null || IsBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            ReShadeUpdateText = T("Vm.SetTargetExecutableEditGameBefore3");
            StatusText = ReShadeUpdateText;
            return;
        }
        using var op = BeginOperation("reshade-check");
        if (op is null) return;
        try
        {
            var result = await _components.CheckReShadeUpdateAsync(SelectedGame!, op.Token);
            ReShadeUpdateText = op.CancelRequested ? OperationCancelledText() : UiLocalizationService.Present(result.Message);
            StatusText = ReShadeUpdateText;
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = TF("Vm.UpdateCheckFailed", UiLocalizationService.Present(ex.Message));
            StatusText = ReShadeUpdateText;
        }
    }

    private void LoadReShadeProfile(DetectedGame game)
    {
        var managed = _components.IsReShadeManaged(game);
        var profile = _components.GetReShadeOptions(game);
        // #186: a game the RHI manifest pins to a legacy ReShade (Max Payne 3 → 6.4.1) starts on that version until ReShade
        // is installed; an installed ReShade always shows its own profile.
        var pin = managed ? null : ReShadeDeploymentService.DefaultOptionsFor(game) is { Channel: ReShadeChannel.Legacy } pinned ? pinned.LegacyVersion : null;
        var legacyVersion = pin ?? (string.IsNullOrWhiteSpace(profile.LegacyVersion) ? UpstreamRhiManifestService.DefaultLegacyReShadeVersion : profile.LegacyVersion);
        Shell.ReShadeLegacy.Offer(legacyVersion); // the dropdown's option exists before it is selected
        SelectedReShadeChannel = ReShadeChannelDisplay(managed ? profile.Channel.ToString() : pin is not null ? nameof(ReShadeChannel.Legacy) : Host.Settings.ReShadeChannel);
        ReShadeLegacyVersion = legacyVersion;
        ReShadeCustomDllPath = profile.CustomDllPath ?? "";
        ReShadeD3DCompiler47Path = profile.D3DCompiler47Path ?? "";
        ReShadeManageIni = managed ? profile.ManageIni : Host.Settings.ReShadeManageIni;
        var packs = profile.ShaderPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedShaderPackIds.Clear();
        _selectedShaderPackIds.UnionWith(packs);
        ShaderCrosireMaster = packs.Contains("CrosireMaster");
        ShaderCrosireLegacy = packs.Contains("CrosireLegacy");
        ShaderClshortfuse = packs.Contains("ClshortfuseShaders");
        ShaderMaxG2D = packs.Contains("MaxG2DSimpleHDR");
        ReShadeUpdateText = pin is not null // #186: the version a fresh install will use
            ? TF("Vm.Profile", ReShadeChannelDisplay(nameof(ReShadeChannel.Legacy)), $" {pin}")
            : TF("Vm.Profile", ReShadeChannelDisplay(profile.Channel.ToString()), string.IsNullOrWhiteSpace(profile.LegacyVersion) ? "" : $" {profile.LegacyVersion}");
        LoadSetModes(game); // #179
    }

    private void ResetReShadeProfile()
    {
        SelectedReShadeChannel = ReShadeChannelDisplay(Host.Settings.ReShadeChannel);
        Shell.ReShadeLegacy.Offer(UpstreamRhiManifestService.DefaultLegacyReShadeVersion);
        ReShadeLegacyVersion = UpstreamRhiManifestService.DefaultLegacyReShadeVersion;
        ReShadeCustomDllPath = "";
        ReShadeD3DCompiler47Path = "";
        ReShadeManageIni = Host.Settings.ReShadeManageIni;
        ShaderCrosireMaster = false;
        ShaderCrosireLegacy = false;
        ShaderClshortfuse = false;
        ShaderMaxG2D = false;
        _selectedShaderPackIds.Clear();
        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(Host.Settings.ReShadeChannel));
        LoadSetModes(null); // #179
    }

    /// <param name="selectPart">#179: the dialog opens for the game's own list of this part (Select): it starts from that
    /// list (shaders: else the global set, as upstream) and its Apply makes the part Select.</param>
    public async Task<ShaderAddonManagerUiSnapshot?> GetShaderAddonManagerSnapshotAsync(CancellationToken cancellationToken = default,
        ShaderAddonSetPart? selectPart = null)
    {
        if (SelectedGame is null) return null;
        var game = SelectedGame;
        var shaderCatalog = await _components.RefreshReShadeShaderPackCatalogAsync(cancellationToken);
        var addonSnapshot = await _components.GetReShadeAddonManagerSnapshotAsync(game, cancellationToken);
        IReadOnlyList<string> selectedShaders = _components.GetReShadeOptions(game).ShaderPackIds.ToArray();
        var selectedAddons = addonSnapshot.SelectedIds;
        if (selectPart is { } part && Sets?.ChoiceFor(game) is { } choice)
        {
            if (part == ShaderAddonSetPart.Shaders)
                selectedShaders = choice.ShaderPackIds.Count > 0 ? choice.ShaderPackIds
                    : Sets.GlobalShaderPackIds.Where(id => !id.Equals(ReShadeShaderPackCatalog.CustomPackId, StringComparison.OrdinalIgnoreCase)).ToArray();
            else
                selectedAddons = choice.AddonIds;
        }
        return new ShaderAddonManagerUiSnapshot(
            shaderCatalog,
            selectedShaders,
            addonSnapshot.Catalog,
            selectedAddons,
            _components.CustomShaderRoot,
            addonSnapshot.CustomAddonRoot);
    }

    /// <summary>#179: the global dialog's lists (the global set and add-ons) with both catalogues.</summary>
    public async Task<ShaderAddonManagerUiSnapshot> GetGlobalShaderAddonSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var shaderCatalog = await _components.RefreshReShadeShaderPackCatalogAsync(cancellationToken);
        var addonCatalog = await _components.RefreshReShadeAddonCatalogAsync(cancellationToken);
        var (shaders, addons) = GlobalLists();
        return new ShaderAddonManagerUiSnapshot(shaderCatalog, shaders, addonCatalog, addons, _components.CustomShaderRoot, _components.CustomAddonRoot);
    }

    public Task<IReadOnlyList<ReShadeShaderPackFileInfo>> GetShaderPackFilesAsync(
        string packId,
        CancellationToken cancellationToken = default)
        => _components.GetReShadeShaderPackFilesAsync(packId, cancellationToken);

    /// <summary>Saves the pack's file selection; returns the localised failure, or null when saved.</summary>
    public string? SetShaderPackExcludedFiles(string packId, IEnumerable<string> excluded)
        => TryPersist(() => _components.SetReShadeShaderPackExcludedFiles(packId, excluded), "Ui.WhatShaderPackSelection", sessionOnly: false);

    public async Task<ComponentOperationResult> ApplyShaderAddonManagerAsync(
        IReadOnlyList<string> shaderIds,
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default,
        ShaderAddonSetPart? selectPart = null)
    {
        if (SelectedGame is null) return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        using var op = BeginOperation("shaders-addons", linkedToken: cancellationToken);
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        // #179: the game's own lists. Chosen through Select: that part becomes Select with exactly these; otherwise a part
        // becomes Select only when its list differs from what the game gets now (the shader half is decided by
        // ApplyReShadeAsync itself).
        if (Sets is { } sets && TryPersist(() =>
            {
                if (selectPart == ShaderAddonSetPart.Shaders) sets.SetShaderMode(game, ShaderAddonMode.Select, shaderIds);
                if (selectPart == ShaderAddonSetPart.Addons
                    || (_components.EffectiveAddons(game) is { HeldByNoAddons: false } now && !Same(now.Ids, addonIds)))
                    sets.SetAddonMode(game, ShaderAddonMode.Select, addonIds);
            }, "Sets.WhatChoice", sessionOnly: false) is { } refused)
            return ComponentOperationResult.Fail(refused);
        try
        {
            // Apply installs ReShade when RHI does not manage it yet (plus RE Framework on RE Engine): warn first.
            var installsReShade = !_components.IsReShadeManaged(game) &&
                (Host.Session.TryGetCached(game, out var probe)
                    ? probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ReShade) is not { } row || ManifestInstallWarning.IsFirstInstall(row.Availability)
                    : true);
            if (!await Host.AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, "ReShade", installsReShade, Host.Session.CachedFiles(game)))
                return ComponentOperationResult.Fail(StatusText);

            var options = _components.GetReShadeOptions(game);
            options.ShaderPackIds = shaderIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            StatusText = T("Vm.ApplyingShaderPackSelection");
            var shaderResult = await _components.ApplyReShadeAsync(game, options, op.Token);
            if (!shaderResult.Success)
            {
                StatusText = OutcomeText(op, shaderResult.Success, shaderResult.Message);
                return shaderResult;
            }

            StatusText = T("Vm.ApplyingReShadeAddSelection");
            // #179: a new ReShade install may already have applied these add-ons (the game's effective set).
            var addonResult = installsReShade && _components.AddonSelectionMatches(game, addonIds)
                ? ComponentOperationResult.Ok("")
                : await _components.ApplyReShadeAddonSelectionAsync(game, addonIds, op.Token);
            if (!addonResult.Success)
            {
                return ComponentOperationResult.Fail(ShowStatus(TF("Vm.ShadersAppliedButAddOnsFailed", UiLocalizationService.Present(addonResult.Message))));
            }

            _selectedShaderPackIds.Clear();
            _selectedShaderPackIds.UnionWith(options.ShaderPackIds);
            ShaderCrosireMaster = _selectedShaderPackIds.Contains("CrosireMaster");
            ShaderCrosireLegacy = _selectedShaderPackIds.Contains("CrosireLegacy");
            ShaderClshortfuse = _selectedShaderPackIds.Contains("ClshortfuseShaders");
            ShaderMaxG2D = _selectedShaderPackIds.Contains("MaxG2DSimpleHDR");
            await ReprobeGameAsync(game);
            LoadSetModes(game); // #179: a changed list made the game Select
            return ComponentOperationResult.Ok(ShowStatus(UiLocalizationService.ShaderAddonAppliedFor(
                UiLocalizationService.CurrentLanguage, options.ShaderPackIds.Count, addonIds.Count, shaderResult.FromCachedSource)));
        }
        catch (Exception ex) when (op.WasCancelled(ex) && op.CancelRequested)
        {
            return ComponentOperationResult.Fail(ShowStatus(OperationCancelledText()));
        }
        catch (Exception ex) when (!op.Token.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(ShowStatus(TF("Vm.ShadersAddOnsFailed", UiLocalizationService.Present(ex.Message))));
        }
    }

    private static void SetPack(HashSet<string> packs, string id, bool enabled)
    {
        if (enabled) packs.Add(id);
        else packs.Remove(id);
    }
}
