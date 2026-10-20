using RHI.Core.Models;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A game's effective Addon Manager add-ons (#179); <see cref="HeldByNoAddons"/>: ReShade is the No Addons build, so none load and nothing is changed.</summary>
public sealed record ReShadeAddonEffectiveSet(IReadOnlyList<string> Ids, bool HeldByNoAddons);

/// <summary>
/// #179 (upstream <c>GetPerGameAddonMode</c> + <c>EnabledGlobalAddons</c>, <c>DeployAddonsForCard</c>): the one answer to
/// "which Addon Manager add-ons does this game get". Every consumer — the Shaders/Add-ons dialog, the per-game mode, the
/// global set's batch, Update All's Addon Manager family (#194) and the add-ons after a new ReShade install — reads it.
/// </summary>
public sealed partial class ReShadeAddonManagerService
{
    /// <summary>The global add-ons and each game's Global / Select / Off choice; null (tests): the recorded selection stands.</summary>
    public GlobalShaderAddonStore? Sets { get; set; }

    /// <summary>
    /// Off → none; Select → the game's own list; Global → the global add-ons. A game whose ReShade is the No Addons build
    /// (#91/#170) gets none and is held (its turned-off add-ons stay as they are). Null when no store is attached or its
    /// file cannot be read.
    /// </summary>
    public ReShadeAddonEffectiveSet? EffectiveSelectionFor(DetectedGame game)
    {
        if (Sets is not { } sets || sets.ChoiceFor(game) is not { } choice) return null;
        if (ReShadeDeploymentService.LacksAddonSupport(_records.Find(game, ManagedComponentKind.ReShade))) return new([], HeldByNoAddons: true);
        return new(EffectiveIds(choice, sets.GlobalAddonIds), HeldByNoAddons: false);
    }

    internal static IReadOnlyList<string> EffectiveIds(ShaderAddonGameChoice choice, IReadOnlyList<string> globalSet)
        => choice.AddonMode switch
        {
            ShaderAddonMode.Off => [],
            ShaderAddonMode.Select => choice.AddonIds,
            _ => globalSet,
        };

    /// <summary>The selection the game's Addon Manager record holds now (empty without a record); never reconciles.</summary>
    public IReadOnlyList<string> RecordedSelection(DetectedGame game) => SelectedIdsOf(_records.Find(game, RecordKind));

    /// <summary>True when the game has an Addon Manager record.</summary>
    public bool HasRecord(DetectedGame game) => _records.Find(game, RecordKind) is not null;
}
