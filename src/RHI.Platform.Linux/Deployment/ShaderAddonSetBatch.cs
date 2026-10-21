using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>Which half of a game's choice a #179 batch brings up to date.</summary>
public enum ShaderAddonSetPart { Shaders, Addons }

/// <summary>
/// What a #179 batch did, counted per game: brought up to date, already up to date, held (No Addons), skipped because the
/// game was running, failed (its files kept), not reached because Cancel stopped the batch, and (a global edit) left alone
/// because the game is left out of Update All for that component. <paramref name="Landed"/> (w8 final review 1): the games
/// brought up to date whose save reported an error after the change was written (counted in <paramref name="Changed"/>).
/// </summary>
public sealed record ShaderAddonSetBatchResult(
    int Changed,
    int Unchanged,
    int Held,
    int Running,
    int Failed,
    int NotRun,
    bool Cancelled,
    IReadOnlyList<(string Game, string Reason)> Failures,
    IReadOnlyList<string> RunningGames,
    int Excluded = 0,
    IReadOnlyList<string>? Landed = null);

/// <summary>
/// #179 (upstream <c>DeployAllShaders</c> / <c>DeployAllAddons</c>): after the global shader set, its mode or the global
/// add-ons change, every game with RHI's ReShade that follows the global choice is brought to its effective set — and
/// only those: a Select or Off game is never touched, nor a game left out of Update All for ReShade (shaders) or the
/// Addon Manager (add-ons) (#177/#178, review major 3). One game is one unit with its own
/// <see cref="OperationCancellation"/> (Cancel stops the batch between games; a game that started changing files runs
/// to completion or full rollback), a running game is skipped and said so, and a game that fails keeps its files.
/// </summary>
public sealed class ShaderAddonSetBatch(LinuxManagedComponentService managed, IGameRunningProbe? running = null)
{
    private readonly IGameRunningProbe _running = running ?? new LinuxGameRunningProbe();

    /// <param name="games">The library (games without RHI's ReShade, or not following the global choice, are left alone).</param>
    /// <param name="only">When given, only these games (a per-game mode change); otherwise games whose choice is Global.</param>
    public async Task<ShaderAddonSetBatchResult> ApplyAsync(IReadOnlyList<DetectedGame> games, ShaderAddonSetPart part,
        CancellationToken batchToken, bool globalOnly = true)
    {
        int changed = 0, unchanged = 0, held = 0, failed = 0, notRun = 0, excluded = 0;
        var cancelled = false;
        var failures = new List<(string, string)>();
        var runningGames = new List<string>();
        var landed = new List<string>();
        var targets = games.Where(managed.HasRhiReShade).ToList();
        for (var index = 0; index < targets.Count; index++)
        {
            var game = targets[index];
            if (batchToken.IsCancellationRequested)
            {
                cancelled = true;
                notRun = targets.Skip(index).Count(game => !globalOnly || Follows(game, part) != false);
                break;
            }
            if (globalOnly)
            {
                var follows = Follows(game, part);
                if (follows is null)
                {
                    failed++;
                    failures.Add((game.Name, LinuxManagedComponentService.ShaderSetsUnreadableMessage));
                    continue;
                }
                if (follows == false) continue;
                // A global edit is a mass update: a game left out of Update All for this component is left as it is.
                if (managed.UpdateExclusionFor(game, part == ShaderAddonSetPart.Shaders
                        ? ManagedComponentKind.ReShade : ManagedComponentKind.ReShadeAddonManager) != UpdateExclusion.None)
                {
                    excluded++;
                    continue;
                }
            }
            if (_running.IsRunning(game))
            {
                runningGames.Add(game.Name);
                continue;
            }

            using var unit = new OperationCancellation(batchToken);
            ShaderSetApplyResult result;
            try
            {
                result = part == ShaderAddonSetPart.Shaders
                    ? await managed.ApplyShaderSetAsync(game, unit.Token).ConfigureAwait(false)
                    : await managed.ApplyAddonSetAsync(game, unit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (unit.CancelAccepted)
            {
                // Cancel was accepted before this game changed anything: it and the rest are not run.
                cancelled = true;
                notRun = targets.Skip(index).Count(game => !globalOnly || Follows(game, part) != false);
                break;
            }
            catch (Exception ex)
            {
                result = new(ShaderSetOutcome.Failed, ex.Message);
            }
            if (result.Outcome == ShaderSetOutcome.Failed && unit.CancelAccepted)
            {
                // Review nit 7: Cancel accepted during this game's download or compiler step (the service said “failed”):
                // nothing of it changed, so it and the rest count as not run.
                cancelled = true;
                notRun = targets.Skip(index).Count(game => !globalOnly || Follows(game, part) != false);
                break;
            }
            switch (result.Outcome)
            {
                case ShaderSetOutcome.Changed:
                    changed++;
                    if (result.Landed) landed.Add(game.Name);
                    break;
                case ShaderSetOutcome.Unchanged or ShaderSetOutcome.NotApplicable: unchanged++; break;
                case ShaderSetOutcome.Held: held++; break;
                default:
                    failed++;
                    failures.Add((game.Name, result.Message));
                    break;
            }
        }
        return new(changed, unchanged, held, runningGames.Count, failed, notRun, cancelled, failures, runningGames, excluded, landed);
    }

    // True when the game's choice for this part is Global; null when the choices cannot be read.
    private bool? Follows(DetectedGame game, ShaderAddonSetPart part)
        => managed.ShaderSets?.ChoiceFor(game) is { } choice
            ? (part == ShaderAddonSetPart.Shaders ? choice.ShaderMode : choice.AddonMode) == ShaderAddonMode.Global
            : null;
}
