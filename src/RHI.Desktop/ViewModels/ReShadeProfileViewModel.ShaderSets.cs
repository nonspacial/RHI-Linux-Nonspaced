using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The game's shader and add-on choice as the Shaders/Add-ons dialog shows it (#179, layout B): per tab "Use the global
/// set / Choose for this game / None", the game's own lists, the global lists, and whether the deployed files lag behind
/// the choice (review minor 4).
/// </summary>
public sealed record ShaderAddonSetChoices(
    ShaderAddonMode ShaderMode,
    IReadOnlyList<string> OwnShaders,
    IReadOnlyList<string> GlobalShaders,
    bool ShadersBehind,
    ShaderAddonMode AddonMode,
    IReadOnlyList<string> OwnAddons,
    IReadOnlyList<string> GlobalAddons,
    bool AddonsHeld,
    bool AddonsBehind);

/// <summary>What the per-game dialog's Apply sends: each tab's choice and the ticked lists (no add-on choice while held).</summary>
public sealed record ShaderAddonSetSelection(
    ShaderAddonMode ShaderMode,
    IReadOnlyList<string> Shaders,
    ShaderAddonMode? AddonMode,
    IReadOnlyList<string> Addons);

/// <summary>
/// #179 (upstream 2.8.7 per-game Shaders / Add-ons: Global / Select / Off, and the global sets in Settings), layout B
/// (owner): the choice lives at the top of each tab of the game's Shaders/Add-ons dialog, and Settings opens the same
/// dialog for the global set. A change reaches the game's files only through the journaled paths: the shaders-only sync
/// and the Addon Manager (<see cref="LinuxManagedComponentService.ApplyShaderSetAsync"/>,
/// <see cref="LinuxManagedComponentService.ApplyAddonSetAsync"/>, <see cref="ShaderAddonSetBatch"/>); ReShade itself is
/// never re-installed for it.
/// </summary>
public sealed partial class ReShadeProfileViewModel
{
    private GlobalShaderAddonStore? Sets => _components.ShaderSets;

    /// <summary>The global sets are wired (the app; tests without them hide the Settings rows and the dialog's choices).</summary>
    internal bool HasShaderSets => Sets is not null;

    /// <summary>The game's choice for the dialog; null without a readable store (the dialog then has no choice row).</summary>
    internal ShaderAddonSetChoices? SetChoicesFor(DetectedGame? game)
    {
        if (game is null || Sets is not { } sets || sets.ChoiceFor(game) is not { } choice) return null;
        return new ShaderAddonSetChoices(
            choice.ShaderMode, choice.ShaderPackIds, sets.GlobalShaderPackIds, _components.ShaderSetBehind(game),
            choice.AddonMode, choice.AddonIds, sets.GlobalAddonIds, _components.EffectiveAddons(game)?.HeldByNoAddons == true,
            _components.AddonSetBehind(game));
    }

    /// <summary>
    /// The per-game dialog's Apply: saves each tab's choice (Choose: exactly the ticked list; the global set: the game's own
    /// pack list is forgotten, as upstream; None: the list is kept for a later Choose), then brings the game to it: a game
    /// with RHI's ReShade through the shaders-only sync and the Addon Manager, a game without it by installing ReShade with
    /// that set (warning first, as before).
    /// </summary>
    public async Task<ComponentOperationResult> ApplyShaderAddonChoicesAsync(ShaderAddonSetSelection selection, CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null) return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        if (Sets is not { } sets) return await ApplyShaderAddonManagerAsync(selection.Shaders, selection.Addons, cancellationToken);
        using var op = BeginOperation("shaders-addons", linkedToken: cancellationToken);
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        if (TryPersist(() =>
            {
                sets.SetShaderMode(game, selection.ShaderMode, selection.ShaderMode switch
                {
                    ShaderAddonMode.Select => selection.Shaders,
                    ShaderAddonMode.Global => [],
                    _ => null,
                });
                if (selection.AddonMode is { } addonMode)
                    sets.SetAddonMode(game, addonMode, addonMode == ShaderAddonMode.Select ? selection.Addons : null);
            }, "Sets.WhatChoice", sessionOnly: false) is { } refused)
            return ComponentOperationResult.Fail(ShowStatus(refused));
        try
        {
            var result = _components.IsReShadeManaged(game) ? await SyncChoicesAsync(game, op) : await InstallWithChoicesAsync(game, op);
            if (result.Success) await ReprobeGameAsync(game);
            ShowEffectivePacks(game);
            return result;
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

    // RHI's ReShade is installed: only the pack files and the Addon Manager's add-ons change (review major 3).
    private async Task<ComponentOperationResult> SyncChoicesAsync(DetectedGame game, OperationScope op)
    {
        StatusText = T("Vm.ApplyingShaderPackSelection");
        var shaders = await _components.ApplyShaderSetAsync(game, op.Token);
        if (shaders.Outcome == ShaderSetOutcome.Failed)
            return ComponentOperationResult.Fail(ShowStatus(OutcomeText(op, false, shaders.Message)));
        StatusText = T("Vm.ApplyingReShadeAddSelection");
        var addons = await _components.ApplyAddonSetAsync(game, op.Token);
        if (addons.Outcome == ShaderSetOutcome.Failed)
            return ComponentOperationResult.Fail(ShowStatus(TF("Vm.ShadersAppliedButAddOnsFailed", UiLocalizationService.Present(addons.Message))));
        return ComponentOperationResult.Ok(ShowStatus(AppliedLine(game, fromCache: false)));
    }

    // No RHI ReShade yet: Apply installs it (warning first) with the game's set; its add-ons follow.
    private async Task<ComponentOperationResult> InstallWithChoicesAsync(DetectedGame game, OperationScope op)
    {
        var installsReShade = !Host.Session.TryGetCached(game, out var probe)
                              || probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ReShade) is not { } row
                              || ManifestInstallWarning.IsFirstInstall(row.Availability);
        if (!await Host.AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, "ReShade", installsReShade, Host.Session.CachedFiles(game)))
            return ComponentOperationResult.Fail(StatusText);
        StatusText = T("Vm.ApplyingShaderPackSelection");
        var shaderResult = await _components.ApplyReShadeAsync(game, _components.GetReShadeOptions(game), op.Token);
        if (!shaderResult.Success)
        {
            StatusText = OutcomeText(op, shaderResult.Success, shaderResult.Message);
            return shaderResult;
        }
        StatusText = T("Vm.ApplyingReShadeAddSelection");
        var addons = await _components.ApplyAddonSetAsync(game, op.Token);
        if (addons.Outcome == ShaderSetOutcome.Failed)
            return ComponentOperationResult.Fail(ShowStatus(TF("Vm.ShadersAppliedButAddOnsFailed", UiLocalizationService.Present(addons.Message))));
        return ComponentOperationResult.Ok(ShowStatus(AppliedLine(game, shaderResult.FromCachedSource)));
    }

    private string AppliedLine(DetectedGame game, bool fromCache)
        => UiLocalizationService.ShaderAddonAppliedFor(UiLocalizationService.CurrentLanguage,
            _components.EffectiveShaderPacks(game)?.Count ?? 0, _components.EffectiveAddons(game)?.Ids.Count ?? 0, fromCache);

    // The ReShade section's quick packages follow what the game gets now.
    private void ShowEffectivePacks(DetectedGame game)
    {
        if (!ReferenceEquals(SelectedGame, game)) return;
        _selectedShaderPackIds.Clear();
        _selectedShaderPackIds.UnionWith(_components.GetReShadeOptions(game).ShaderPackIds);
        ShaderCrosireMaster = _selectedShaderPackIds.Contains("CrosireMaster");
        ShaderCrosireLegacy = _selectedShaderPackIds.Contains("CrosireLegacy");
        ShaderClshortfuse = _selectedShaderPackIds.Contains("ClshortfuseShaders");
        ShaderMaxG2D = _selectedShaderPackIds.Contains("MaxG2DSimpleHDR");
    }

    // -- Global sets (Settings -> Shaders/Add-ons -> Global shaders and add-ons...) ---------------------------------------

    /// <summary>The global dialog's lists as saved now (the packs and add-ons games set to the global set get).</summary>
    internal (IReadOnlyList<string> Shaders, IReadOnlyList<string> Addons) GlobalLists()
        => Sets is { } sets ? (sets.GlobalShaderPackIds, sets.GlobalAddonIds) : ([], []);

    /// <summary>
    /// The global dialog's Save: saves the global set and add-ons and brings every game set to the global set up to date,
    /// both halves every time, so a game a cancelled or skipped run left behind catches up too (review minor 4). Returns
    /// the status line.
    /// </summary>
    public async Task<ComponentOperationResult> ApplyGlobalSetsAsync(IReadOnlyList<string> shaderIds, IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default)
    {
        if (Sets is not { } sets) return ComponentOperationResult.Fail(UiLocalizationService.Present(LinuxManagedComponentService.ShaderSetsUnreadableMessage));
        using var op = BeginOperation("shader-sets", linkedToken: cancellationToken);
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        if (TryPersist(() =>
            {
                if (!Same(sets.GlobalShaderPackIds, shaderIds)) sets.SetGlobalShaderPackIds(shaderIds);
                if (!Same(sets.GlobalAddonIds, addonIds)) sets.SetGlobalAddonIds(addonIds);
            }, "Sets.WhatChoice", sessionOnly: false) is { } refused)
            return ComponentOperationResult.Fail(refused);
        var shaders = await ApplySetsAsync(op, Host.AllGames, ShaderAddonSetPart.Shaders);
        var addons = op.CancelRequested ? "" : await ApplySetsAsync(op, Host.AllGames, ShaderAddonSetPart.Addons);
        if (SelectedGame is { } game) await ReprobeKeepingStatusAsync(game);
        return ComponentOperationResult.Ok(ShowStatus(string.Join(" ", new[] { shaders, addons }.Where(line => line.Length > 0))));
    }

    private static bool Same(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
        => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private async Task<string> ApplySetsAsync(OperationScope op, IReadOnlyList<DetectedGame> games, ShaderAddonSetPart part)
    {
        StatusText = T("Sets.Applying");
        ShaderAddonSetBatchResult result;
        try
        {
            result = await new ShaderAddonSetBatch(_components, RunningProbe).ApplyAsync(games, part, op.Token);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            return ShowStatus(OperationCancelledText());
        }
        return ShowStatus(SummaryText(part, result));
    }

    /// <summary>Which games are running (a test seam; the real probe reads /proc).</summary>
    internal IGameRunningProbe? RunningProbe { get; set; }

    /// <summary>One status line from a batch's counts: "Shader packs: 2 game(s) updated; 1 failed, files kept: Game (why)."</summary>
    internal static string SummaryText(ShaderAddonSetPart part, ShaderAddonSetBatchResult result)
    {
        var parts = new List<string>();
        if (result.Changed > 0) parts.Add(TF("Sets.SumChanged", result.Changed));
        if (result.Unchanged > 0) parts.Add(TF("Sets.SumUnchanged", result.Unchanged));
        if (result.Held > 0) parts.Add(TF("Sets.SumHeld", result.Held));
        if (result.Running > 0) parts.Add(TF("Sets.SumRunning", result.Running, string.Join(", ", result.RunningGames)));
        if (result.Failed > 0)
            parts.Add(TF("Sets.SumFailed", result.Failed,
                string.Join("; ", result.Failures.Select(f => $"{f.Game} ({UiLocalizationService.Present(f.Reason).TrimEnd('.')})"))));
        if (result.NotRun > 0) parts.Add(TF("Sets.SumCancelled", result.NotRun));
        if (result.Excluded > 0) parts.Add(TF("Sets.SumExcluded", result.Excluded));
        if (parts.Count == 0) parts.Add(T("Sets.SumNone"));
        return $"{T(part == ShaderAddonSetPart.Shaders ? "Win.ShaderPacks" : "Win.ReShadeAddOns")}: {string.Join("; ", parts)}.";
    }
}
