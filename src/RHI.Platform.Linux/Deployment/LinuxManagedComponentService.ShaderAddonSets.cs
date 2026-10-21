using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    public Task<ReShadeAddonManagerSnapshot> GetReShadeAddonManagerSnapshotAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => _addonManager.GetSnapshotAsync(game, cancellationToken);

    public Task<ComponentOperationResult> ApplyReShadeAddonSelectionAsync(
        DetectedGame game,
        IReadOnlyList<string> selectedIds,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        // w3/dllnames (#91): no add-on selection for the No Addons build (clearing the selection stays possible).
        if (guard is null && selectedIds.Count > 0 && ReShadeDeploymentService.LacksAddonSupport(_records.Find(game, ManagedComponentKind.ReShade)))
            guard = ComponentOperationResult.Fail(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + "ReShade Addon Manager");
        return guard is null
            ? (AddonSelectionForTesting ?? _addonManager.ApplySelectionAsync)(game, selectedIds, cancellationToken)
            : Task.FromResult(guard);
    }

    /// <summary>Test seam: the Addon Manager's own apply inside <see cref="ApplyReShadeAddonSelectionAsync"/> (VM tests run offline).</summary>
    internal Func<DetectedGame, IReadOnlyList<string>, CancellationToken, Task<ComponentOperationResult>>? AddonSelectionForTesting { get; set; }

    // ── w7/shader-sets (#179): global shader / add-on sets, per-game Global / Select / Off ──────────────────────────

    /// <summary>Fixed text before the reason when a game's own shader or add-on choice could not be saved (PT-BR pair).</summary>
    public const string ShaderChoiceNotSavedPrefix = "Nothing was changed: the game's shader and add-on choice could not be saved: ";
    /// <summary>Fixed text before the reason when ReShade was installed but the game's add-ons were not (PT-BR pair).</summary>
    public const string AddonSetNotAppliedPrefix = " The game's add-ons were not applied: ";
    /// <summary>The note after ReShade's message when the game's add-ons were applied with it (PT-BR pair).</summary>
    public const string AddonSetAppliedNote = " The game's add-ons were applied.";

    private GlobalShaderAddonStore? _shaderSets;

    /// <summary>
    /// #179: the global sets and per-game choices (set by the app's composition; the Addon Manager reads the same store).
    /// The shader-pack service ReShade uses is given it by the composition too (<see cref="ReShadeShaderPackService.Sets"/>).
    /// </summary>
    public GlobalShaderAddonStore? ShaderSets
    {
        get => _shaderSets;
        set
        {
            _shaderSets = value;
            if (_addonManager is not null) _addonManager.Sets = value;
        }
    }

    /// <summary>True when RHI's ReShade is installed on the game (the only games a shader or add-on set reaches).</summary>
    public bool HasRhiReShade(DetectedGame game) => _records.Find(game, ManagedComponentKind.ReShade) is not null;

    /// <summary>The packs <paramref name="game"/> gets now (null without a readable store).</summary>
    public IReadOnlyList<string>? EffectiveShaderPacks(DetectedGame game)
        => _shaderSets is { } sets && sets.ChoiceFor(game) is { } choice
            ? ReShadeShaderPackService.EffectivePackIds(choice, sets.GlobalShaderPackIds)
            : null;

    /// <summary>The Addon Manager add-ons <paramref name="game"/> gets now (null without a readable store).</summary>
    public ReShadeAddonEffectiveSet? EffectiveAddons(DetectedGame game) => _addonManager?.EffectiveSelectionFor(game);

    /// <summary>True when the game's Addon Manager record holds exactly <paramref name="addonIds"/> (none: no record either).</summary>
    public bool AddonSelectionMatches(DetectedGame game, IReadOnlyList<string> addonIds)
        => _addonManager is not null && SameIds(_addonManager.RecordedSelection(game), addonIds) && (addonIds.Count == 0) != _addonManager.HasRecord(game);

    /// <summary>The Addon Manager's catalogue (the global add-ons dialog has no game).</summary>
    public Task<IReadOnlyList<ReShadeAddonCatalogItem>> RefreshReShadeAddonCatalogAsync(CancellationToken cancellationToken = default)
        => _addonManager.RefreshCatalogItemsAsync(cancellationToken);

    /// <summary>The selection the game's Addon Manager record holds now (empty without one).</summary>
    public IReadOnlyList<string> RecordedAddonSelection(DetectedGame game) => _addonManager?.RecordedSelection(game) ?? [];

    /// <summary>
    /// A pack list the user chose for this game (profile Apply, the Shaders/Add-ons dialog, a preset with its shaders):
    /// when it differs from what the game gets now, the game switches to Select with exactly that list (upstream). Returns
    /// why it could not be saved (nothing may then be installed), or null.
    /// </summary>
    public string? RecordExplicitShaderChoice(DetectedGame game, IReadOnlyList<string> packIds)
    {
        if (_shaderSets is not { } sets || EffectiveShaderPacks(game) is not { } now || SamePacks(now, packIds)) return null;
        return SaveChoice(() => sets.SetShaderMode(game, ShaderAddonMode.Select, packIds));
    }

    /// <summary>As <see cref="RecordExplicitShaderChoice"/>, for the game's Addon Manager add-ons.</summary>
    public string? RecordExplicitAddonChoice(DetectedGame game, IReadOnlyList<string> addonIds)
    {
        if (_shaderSets is not { } sets || EffectiveAddons(game) is not { HeldByNoAddons: false } now || SameIds(now.Ids, addonIds)) return null;
        return SaveChoice(() => sets.SetAddonMode(game, ShaderAddonMode.Select, addonIds));
    }

    /// <summary>Saves a game's shader mode (and its Select list when given); returns why it was not saved, or null.</summary>
    public string? SetShaderMode(DetectedGame game, ShaderAddonMode mode, IReadOnlyList<string>? packIds = null)
        => _shaderSets is { } sets ? SaveChoice(() => sets.SetShaderMode(game, mode, packIds)) : null;

    /// <summary>Saves a game's add-on mode (and its Select list when given); returns why it was not saved, or null.</summary>
    public string? SetAddonMode(DetectedGame game, ShaderAddonMode mode, IReadOnlyList<string>? addonIds = null)
        => _shaderSets is { } sets ? SaveChoice(() => sets.SetAddonMode(game, mode, addonIds)) : null;

    private static string? SaveChoice(Action save)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shader-sets", "Could not save a game's shader and add-on choice", ex);
            return ShaderChoiceNotSavedPrefix + ex.Message;
        }
    }

    private static bool SameIds(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
        => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));

    // Two pack lists that deploy the same packs (a ReShade record holds the list with its dependencies added).
    private bool SamePacks(IEnumerable<string> a, IEnumerable<string> b)
        => ReShadeShaderPackService.DeployedPackIds(a, _reshade.AvailableShaderPacks)
            .SetEquals(ReShadeShaderPackService.DeployedPackIds(b, _reshade.AvailableShaderPacks));

    /// <summary>
    /// Brings the game's RHI shader packs to its effective set WITHOUT re-running ReShade's install (review major 3:
    /// <see cref="ReShadeDeploymentService.SyncShaderPacksAsync"/>, one journaled operation on ReShade's record; ReShade's
    /// DLL and version stay). A native D3DCompiler_47 is got first when packs arrive, as profile Apply does. Logged in the
    /// update history as ReShade (reshade-shaders) when the pack list changes. Nothing without RHI's ReShade or when the
    /// packs are already the effective set.
    /// </summary>
    public async Task<ShaderSetApplyResult> ApplyShaderSetAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null) return new(ShaderSetOutcome.NotApplicable, "");
        if (EffectiveShaderPacks(game) is not { } packs) return new(ShaderSetOutcome.Failed, ShaderSetsUnreadableMessage);
        if (SamePacks(ReShadeDeploymentService.RecordedShaderPacks(record), packs)) return new(ShaderSetOutcome.Unchanged, "");
        if (PatchTargetGuard(game) is { } guard) return new(ShaderSetOutcome.Failed, guard.Message);
        if (!ReShadeVulkanLayer.IsLayerRecord(record) && packs.Count > 0
            && string.IsNullOrWhiteSpace(_reshade.GetInstalledOptions(game).D3DCompiler47Path)
            && await EnsureNativeD3DCompilerDependencyAsync(game, null, "ReShade shader packs", cancellationToken).ConfigureAwait(false) is { } compiler)
            return new(ShaderSetOutcome.Failed, compiler.Message);
        Task<ComponentOperationResult> Sync() => _reshade.SyncShaderPacksAsync(game, packs, cancellationToken);
        var result = await (_history is null
            ? Sync()
            : _history.TrackOwnedFileAsync(game, ManagedComponentKind.ReShade, ShaderPackHistoryLabel, ShaderPackHistoryVersion, Sync)).ConfigureAwait(false);
        return new(result.Success ? ShaderSetOutcome.Changed : ShaderSetOutcome.Failed, result.Message, result.Landed);
    }

    /// <summary>
    /// Review minor 4: the game's deployed packs are not its effective set (a batch was cancelled, the game was running, or
    /// a crash rolled a change back). The Shaders/Add-ons dialog says so; its Apply, or the global set's Save, catches up.
    /// </summary>
    public bool ShaderSetBehind(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.ReShade) is { } record && EffectiveShaderPacks(game) is { } packs
           && !SamePacks(ReShadeDeploymentService.RecordedShaderPacks(record), packs);

    /// <summary>As <see cref="ShaderSetBehind"/>, for the game's Addon Manager add-ons (never while No Addons holds them).</summary>
    public bool AddonSetBehind(DetectedGame game)
        => _addonManager is not null && _records.Find(game, ManagedComponentKind.ReShade) is not null
           && EffectiveAddons(game) is { HeldByNoAddons: false } effective
           && !(SameIds(_addonManager.RecordedSelection(game), effective.Ids) && (effective.Ids.Count == 0 || _addonManager.HasRecord(game)));

    /// <summary>The update history's name for ReShade's shader packs ("ReShade (reshade-shaders)"; the folder's own name).</summary>
    internal const string ShaderPackHistoryLabel = "reshade-shaders";

    // The "version" the history shows for a pack change: the deployed pack ids, or a dash for none.
    private static string ShaderPackHistoryVersion(LinuxDeploymentRecord record)
        => ReShadeDeploymentService.RecordedShaderPacks(record) is { Count: > 0 } ids ? string.Join(", ", ids) : "—";

    /// <summary>
    /// Brings the game's Addon Manager add-ons to its effective set (one journaled Addon Manager operation, logged in the
    /// update history like any Addon Manager change). Held while ReShade is the No Addons build; nothing without RHI's
    /// ReShade or when the add-ons already match.
    /// </summary>
    public async Task<ShaderSetApplyResult> ApplyAddonSetAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (_records.Find(game, ManagedComponentKind.ReShade) is null) return new(ShaderSetOutcome.NotApplicable, "");
        if (EffectiveAddons(game) is not { } effective) return new(ShaderSetOutcome.Failed, ShaderSetsUnreadableMessage);
        if (effective.HeldByNoAddons) return new(ShaderSetOutcome.Held, ReShadeAddonSwitch.HeldPrefix + ReShadeAddonManagerService.DisplayName);
        if (!AddonSetBehind(game)) return new(ShaderSetOutcome.Unchanged, "");
        var result = await Track(game, ManagedComponentKind.ReShadeAddonManager, ComponentUpdateAction.Update,
            () => ApplyReShadeAddonSelectionAsync(game, effective.Ids, cancellationToken)).ConfigureAwait(false);
        return new(result.Success ? ShaderSetOutcome.Changed : ShaderSetOutcome.Failed, result.Message, result.Landed);
    }

    /// <summary>The message when the shader and add-on choices cannot be read (PT-BR pair).</summary>
    public const string ShaderSetsUnreadableMessage = "The shader and add-on choices cannot be read; nothing was changed.";

    /// <summary>
    /// Appended to a ReShade install or update that could not take the game's shader packs because no native
    /// D3DCompiler_47 could be had for its prefix (review major 2; PT-BR pair). ReShade itself is installed.
    /// </summary>
    public const string ShaderPacksNotInstalledNote =
        " The game's shader packs were not installed: they need a native D3DCompiler_47 in its Wine prefix. Install it with Protontricks or Winetricks on the D3DCompiler 47 row in the ReShade section, then Apply in Shaders/Add-ons; a DLL picked there with Browse takes effect with the ReShade profile's Apply instead.";

    /// <summary>
    /// Review major 2: ReShade's install and update read the game's effective set inside ReShade's own install
    /// (<see cref="ReShadeShaderPackService.WithEffectiveSelection"/>), so every path that reaches it — the ReShade row,
    /// ReShade installed for RenoDX/Luma/ShortFuse/Feeder, Update All and a managed update — first gets a native
    /// D3DCompiler_47 for that set, as profile Apply and the shader batch do. When none can be had ReShade still installs
    /// (the hook then keeps the caller's own list) and the result says why the packs are missing.
    /// </summary>
    private async Task<ComponentOperationResult> InstallReShadeForShaderSetAsync(DetectedGame game, ReShadeInstallOptions options, bool update,
        CancellationToken cancellationToken)
    {
        if (_shaderSets is not null && !options.VulkanLayer && string.IsNullOrWhiteSpace(options.D3DCompiler47Path)
            && !ReShadeVulkanLayer.IsLayerRecord(_records.Find(game, ManagedComponentKind.ReShade))
            && EffectiveShaderPacks(game) is { Count: > 0 }
            && await EnsureNativeD3DCompilerDependencyAsync(game, null, "ReShade shader packs", cancellationToken).ConfigureAwait(false) is { } compiler)
            AppLog.Warn("shader-sets", $"{game.Name}: ReShade goes on without the game's shader packs: {compiler.Message}");
        var result = update
            ? await _reshade.UpdateAsync(game, cancellationToken).ConfigureAwait(false)
            : await _reshade.InstallAsync(game, options, cancellationToken).ConfigureAwait(false);
        return result.Success && EffectiveShaderPacks(game) is { Count: > 0 } effective
               && !SamePacks(_reshade.GetInstalledOptions(game).ShaderPackIds, effective)
            ? result with { Message = result.Message + ShaderPacksNotInstalledNote }
            : result;
    }

    /// <summary>
    /// After a ReShade install (the ReShade row, profile Apply, or ReShade installed for another component): a game with
    /// no Addon Manager add-ons yet gets its effective add-ons, as upstream deploys the game's add-ons after ReShade. Its
    /// own journaled operation; a failure leaves ReShade installed and is said after ReShade's message. A landed add-on
    /// save is carried on the returned result (#222 item 2).
    /// </summary>
    private async Task<ComponentOperationResult> ApplyAddonSetAfterReShadeAsync(DetectedGame game, ManagedComponentKind target,
        IReadOnlyCollection<ManagedComponentKind> installedThisAttempt, ComponentOperationResult result, CancellationToken cancellationToken)
    {
        if (_shaderSets is null || _addonManager is null || !result.Success) return result;
        if (target != ManagedComponentKind.ReShade && !installedThisAttempt.Contains(ManagedComponentKind.ReShade)) return result;
        if (_addonManager.HasRecord(game) || EffectiveAddons(game) is not { HeldByNoAddons: false, Ids.Count: > 0 } effective) return result;
        ComponentOperationResult addons;
        try
        {
            addons = await ApplyReShadeAddonSelectionAsync(game, effective.Ids, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            addons = ComponentOperationResult.Fail(ex is OperationCanceledException ? "cancelled." : ex.Message);
        }
        if (!addons.Success) return result with { Message = result.Message + AddonSetNotAppliedPrefix + addons.Message };
        // #222 item 2: the add-ons' save reported an error after their change was written: the result carries their note and
        // Landed flag (a caller that words its own line appends the whole message as the note), as ReShade's own does.
        return addons.Landed
            ? result with { Message = result.Message + AddonSetAppliedNote + " " + addons.Message, Landed = true }
            : result with { Message = result.Message + AddonSetAppliedNote };
    }
}

/// <summary>#179: what bringing one game to its effective shader or add-on set did.</summary>
public enum ShaderSetOutcome { Changed, Unchanged, Held, NotApplicable, Failed }

/// <summary>
/// #179: one game's outcome and the message of the operation (empty when nothing ran). <paramref name="Landed"/> (w8 final
/// review 1): the change was written and the save then reported an error; Message is the note saying so
/// (<see cref="ComponentOperationResult.Landed"/>).
/// </summary>
public sealed record ShaderSetApplyResult(ShaderSetOutcome Outcome, string Message, bool Landed = false);
