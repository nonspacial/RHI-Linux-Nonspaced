using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w14/nr-2 (#317, #276): the DLSS5 Tool Neural Rendering method (and "DLSS5 Tool + DX11 Bridge" through the DX11 Bridge
/// row), with its per-game Foveated switch; install / update / remove go through the usual guards and update history.
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>The DLSS5 Tool service (set by the app's composition; null in builds and tests constructed without it).</summary>
    public Dlss5ToolDeploymentService? Dlss5Tool { get; set; }

    /// <summary>
    /// #317: the DLSS / Streamline section's swap service (the shell's, with its Update History), which a DLSS5 Tool install
    /// asks to bring the game's DLSS SR/RR/FG to the newest. DLSS5 Tool never owns those files. Null: nothing is upgraded.
    /// </summary>
    public LinuxDlssDefaultsService? DlssSection { get; set; }

    /// <summary>The DLSS5 Tool build for the game: the installed one, else the saved Foveated switch (upstream's per-game choice).</summary>
    public Dlss5ToolBuild GetDlss5ToolBuild(DetectedGame game)
        => Dlss5Tool?.InstalledBuild(game)
           ?? (_neuralVersionPins.GetDlss5ToolFoveated(game) ? Dlss5ToolBuild.Foveated : Dlss5ToolBuild.Stock);

    /// <summary>
    /// The Foveated switch (#276): saves the game's build and starts its version list from Latest (stock and Foveated have
    /// different releases, as upstream). With DLSS5 Tool installed, the add-on is swapped in place to the other build's
    /// Latest; if that fails, the switch and the pin go back to what is installed.
    /// </summary>
    public async Task<ComponentOperationResult> SetDlss5ToolFoveatedAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
    {
        if (Dlss5Tool is null) return ComponentOperationResult.Fail($"{Dlss5ToolDeploymentService.DisplayName} installation is not available yet.");
        if (PatchTargetGuard(game) is { } guard) return guard;
        var build = on ? Dlss5ToolBuild.Foveated : Dlss5ToolBuild.Stock;
        var previousPin = _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Tool);
        var previousOn = _neuralVersionPins.GetDlss5ToolFoveated(game);
        _neuralVersionPins.SetDlss5ToolFoveated(game, on);
        _neuralVersionPins.Set(game, ManagedComponentKind.Dlss5Tool, null);
        if (Dlss5Tool.InstalledBuild(game) is not { } installed)
            return ComponentOperationResult.Ok(on ? Dlss5ToolFoveatedSavedMessage : Dlss5ToolStockSavedMessage);
        if (installed == build)
            return ComponentOperationResult.Ok(Dlss5ToolBuildCurrentPrefix + Dlss5ToolDeploymentService.NameOf(build) + ".");
        if ((LayerGuard(game, ManagedComponentKind.Dlss5Tool) ?? AddonOffGuard(game, ManagedComponentKind.Dlss5Tool)) is { } held)
        {
            Revert();
            return held;
        }
        var result = await Track(game, ManagedComponentKind.Dlss5Tool, ComponentUpdateAction.Update,
            () => Dlss5Tool.UpdateAsync(game, build, pin: null, cancellationToken)).ConfigureAwait(false);
        if (!result.Success && !result.Landed) Revert();
        return result;

        void Revert()
        {
            _neuralVersionPins.SetDlss5ToolFoveated(game, previousOn);
            _neuralVersionPins.Set(game, ManagedComponentKind.Dlss5Tool, previousPin);
        }
    }

    /// <summary>#276: fixed lines of the Foveated switch when DLSS5 Tool is not installed (the choice is used by the install).</summary>
    public const string Dlss5ToolFoveatedSavedMessage = "Saved: DLSS5 Tool will be installed as the Foveated build (FoveatedDLSS5) in this game.";
    public const string Dlss5ToolStockSavedMessage = "Saved: DLSS5 Tool will be installed as the stock build in this game.";
    public const string Dlss5ToolBuildCurrentPrefix = "Already installed: ";

    /// <summary>The DLSS5 Tool version list of the game's build (upstream: the list switches to Foveated releases).</summary>
    public Task<IReadOnlyList<GitHubReleaseVersion>> ListDlss5ToolVersionsAsync(Dlss5ToolBuild build, CancellationToken cancellationToken = default)
        => Dlss5Tool is null
            ? Task.FromResult<IReadOnlyList<GitHubReleaseVersion>>([])
            : Dlss5Tool.ListVersionsAsync(build, cancellationToken);

    private async Task<ComponentOperationResult> InstallDlss5ToolCoreAsync(DetectedGame game, bool withBridge, CancellationToken cancellationToken)
    {
        if (Dlss5Tool is null) return ComponentOperationResult.Fail($"{ManagedComponentKind.Dlss5Tool} installation is not available yet.");
        // One nvngx_dlssnr.dll owner (#157): OptiScaler DLSS NR / F5 and RTX Encore's NR file are never switched away from.
        if (NeuralRenderingRuntimeOwnership.OptiScalerOwner(_records, game) is { } optiScaler)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(optiScaler));
        if (NeuralRenderingRuntimeOwnership.RtxEncoreOwner(_records, game) is { } encore)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(encore));
        // DLSS as the game shipped it (a ShortFuse/Feeder install's own DLL does not count), before the method switch.
        var hasDlss = HasNativeDlssForNeuralRendering(game, RtxEncoreDeploymentService.HasGameDlss(game));
        if (Dlss5ToolDeploymentService.IneligibleReason(game, hasDlss) is { } ineligible) return ComponentOperationResult.Fail(ineligible);
        var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, ManagedComponentKind.Dlss5Tool, cancellationToken).ConfigureAwait(false);
        if (cleanup is not null) return cleanup;
        var options = new Dlss5ToolOptions(
            GetDlss5ToolBuild(game),
            _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Tool),
            _neuralVersionPins.GetNeuralRenderingDllVersion(game),
            withBridge);
        var installed = await Dlss5Tool.InstallAsync(game, options, hasDlss, cancellationToken).ConfigureAwait(false);
        if (!installed.Success) return installed;
        // #317 (upstream: "Upgrade all DLSS DLLs to latest" after the add-on): through the DLSS section, never as DLSS5 Tool's.
        return installed with { Message = installed.Message + await BringDlssToNewestAsync(game, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>
    /// #317: the game's DLSS SR/RR/FG to the newest through <see cref="DlssSection"/> (its Quick Apply: the files the game
    /// has, .original backups, journaled swaps, Update History entries). A game whose update inclusion leaves DLSS out (#178)
    /// keeps its files, as Follow newest does. Returns the line added to the install's message ("" when nothing changed).
    /// A failure here never fails the install: the add-on is in place and the DLSS section can still do it.
    /// </summary>
    private async Task<string> BringDlssToNewestAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        if (DlssSection is not { } section) return "";
        if (_updateInclusion?.ExclusionFor(game, ManagedComponentKind.Dlss) is { } exclusion && exclusion != UpdateExclusion.None)
            return DlssKeptByInclusionNote;
        LinuxDlssBatchResult result;
        try
        {
            result = await section.ApplyNewestDlssAsync(game, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("dlss5-tool", $"DLSS5 Tool: the game's DLSS could not be brought to the newest for {game.Name}", ex);
            return DlssNewestFailedPrefix + (ex is OperationCanceledException ? "cancelled" : ex.Message);
        }
        var changed = result.Items.Where(item => item.Changed).Select(item => ShortName(item.Component)).ToArray();
        var failed = result.FailedItems.ToArray();
        var line = changed.Length > 0 ? DlssNewestPrefix + string.Join(", ", changed) + "." : "";
        if (failed.Length > 0) line += DlssNewestFailedPrefix + string.Join(" ", failed.Select(item => item.Message));
        if (result.DeferredCount > 0) line += DlssNewestDeferredNote;
        return line;

        static string ShortName(LinuxDlssBatchComponent component) => component switch
        {
            LinuxDlssBatchComponent.SuperResolution => "SR",
            LinuxDlssBatchComponent.RayReconstruction => "RR",
            LinuxDlssBatchComponent.FrameGeneration => "FG",
            _ => component.ToString()
        };
    }

    /// <summary>#317: the install's line after the game's DLSS files went to the newest; the families follow ("SR, FG.").</summary>
    public const string DlssNewestPrefix = " The game's DLSS was brought to the newest in the DLSS / Streamline section (originals kept as .original): ";
    /// <summary>#317: the newest DLSS could not be put in; the reason follows. The add-on is installed all the same.</summary>
    public const string DlssNewestFailedPrefix = " The game's DLSS could not be brought to the newest; use the DLSS / Streamline section: ";
    /// <summary>#317: the game runs, so its DLSS files were left for now.</summary>
    public const string DlssNewestDeferredNote = " The game's DLSS was left as it is while the game runs; use Quick Apply in the DLSS / Streamline section later.";
    /// <summary>#317: the game's update inclusion leaves DLSS out, so its DLSS files stay as they are.</summary>
    public const string DlssKeptByInclusionNote = " The game's DLSS was left as it is: its update inclusion leaves DLSS out.";

    /// <summary>
    /// The DX11 Bridge row installs upstream's "DLSS5 Tool + DX11 Bridge": the DLSS5 Tool first (the bridge only mirrors
    /// the game's DLSS onto a DX12 session for it), then the bridge add-on. If the bridge then fails, the DLSS5 Tool this
    /// install added is taken out again. A DLSS5 Tool already installed stays as it is.
    /// </summary>
    private async Task<ComponentOperationResult> InstallBridgeWithDlss5ToolAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        ComponentOperationResult? tool = null;
        if (Dlss5Tool is not null && Dlss5Tool.GetRecord(game) is null)
        {
            tool = await InstallDlss5ToolCoreAsync(game, withBridge: true, cancellationToken).ConfigureAwait(false);
            if (!tool.Success) return ComponentOperationResult.Fail(BridgeNeedsToolPrefix + tool.Message);
        }
        var bridge = await _dlss5Bridge.InstallAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Dx11Bridge), cancellationToken).ConfigureAwait(false);
        if (bridge.Success || tool is null) return tool is null ? bridge : bridge with { Message = bridge.Message + " " + tool.Message };
        var back = await Dlss5Tool!.RemoveAsync(game, CancellationToken.None).ConfigureAwait(false);
        return back.Success ? bridge : bridge with { Message = bridge.Message + " " + BridgeToolKeptPrefix + back.Message };
    }

    /// <summary>The DX11 Bridge's removal; a DLSS5 Tool the bridge row installed with it goes too (the method is one unit).</summary>
    private async Task<ComponentOperationResult> RemoveBridgeWithDlss5ToolAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var bridge = await _dlss5Bridge.RemoveAsync(game, cancellationToken).ConfigureAwait(false);
        if (!bridge.Success || Dlss5Tool is null || !Dlss5Tool.InstalledWithBridge(game)) return bridge;
        if (LayerGuard(game, ManagedComponentKind.Dlss5Tool) is { } layered)
            return bridge with { Message = bridge.Message + " " + BridgeToolKeptPrefix + layered.Message };
        var tool = await Track(game, ManagedComponentKind.Dlss5Tool, ComponentUpdateAction.Remove,
            () => Dlss5Tool.RemoveAsync(game, cancellationToken)).ConfigureAwait(false);
        return tool.Success
            ? bridge with { Message = bridge.Message + " " + tool.Message }
            : bridge with { Message = bridge.Message + " " + BridgeToolKeptPrefix + tool.Message };
    }

    /// <summary>The DX11 Bridge row's note when no DLSS5 Tool is installed with it (installed before the DLSS5 Tool methods).</summary>
    public const string BridgeWithoutToolNote = "The DX11 Bridge only passes the game's DLSS to DLSS5 Tool: install DLSS5 Tool (its own row) to use it.";

    /// <summary>The bridge row's DLSS5 Tool could not be installed, so the bridge was not either; the reason follows.</summary>
    public const string BridgeNeedsToolPrefix = "DLSS5 Tool + DX11 Bridge was not installed: the DLSS5 Tool it needs could not be installed: ";

    /// <summary>The DLSS5 Tool installed with the bridge stays installed (remove it from its own row); the reason follows.</summary>
    public const string BridgeToolKeptPrefix = "DLSS5 Tool stays installed (remove it from its own row): ";
}
