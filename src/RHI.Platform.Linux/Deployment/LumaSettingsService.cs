using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What the Luma ⚙ shows for a game (#189).</summary>
/// <param name="EngineIniPath">The game's Engine.ini RHI would write, or null when none can be proven (TAA is then not offered).</param>
public sealed record LumaSettingsSnapshot(LumaHdrMode Hdr, bool TaaOn, string? EngineIniPath);

/// <summary>
/// The Luma ⚙ (#189; upstream 2.8.7 <c>LumaCogButton_Click</c>, Unreal Engine games with Luma): "HDR on first boot"
/// (Default / Off / On, <c>[Luma] EnableHDR</c>/<c>DisplayMode</c> in ReShade.ini) and "TAA" (upstream's two Engine.ini
/// keys). Changes go through Luma's record: the ReShade.ini and Engine.ini edits are journaled and the ownership state
/// is saved at the commit point, so removing Luma puts back what was there before RHI and an update keeps the choice.
/// Engine.ini is written only through <see cref="LumaEngineIniOverrideManager.WriteUserEngineIni"/> (a user's
/// read-only lock is kept and never blocks).
/// </summary>
public sealed class LumaSettingsService(LinuxDeploymentRecordStore records, DeploymentJournal? journal, Func<DetectedGame, string?> writeRefusal)
{
    public const string NotManagedPrefix = "Luma settings need Luma installed by RHI: ";
    public const string NoEngineIniPrefix = "No Engine.ini location could be found for this game, so TAA was not changed: ";

    public LumaSettingsSnapshot Read(DetectedGame game)
    {
        var record = records.Find(game, ManagedComponentKind.LumaFramework);
        var state = LumaEngineIniOverrideManager.Deserialize(record?.Metadata.GetValueOrDefault(LumaEngineIniOverrideManager.MetadataKey));
        var engineIni = state?.IniPath ?? LumaEngineIniOverrideManager.ResolveEngineIniPath(game);
        return new LumaSettingsSnapshot(
            string.IsNullOrWhiteSpace(game.BinaryDirectory) ? LumaHdrMode.Default : LumaIniOverrideManager.ReadHdrMode(game.BinaryDirectory),
            LumaEngineIniOverrideManager.IsTaaOn(state, engineIni),
            engineIni);
    }

    public Task<ComponentOperationResult> SetHdrAsync(DetectedGame game, LumaHdrMode mode, CancellationToken cancellationToken = default)
        => ChangeAsync(game, "HDR", async (record, scope, _, token) =>
        {
            var ini = RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory!)
                      ?? throw new InvalidOperationException("ReShade.ini is required before enabling Luma Framework.");
            // #213: the file the write replaces under the one ReShade.ini link rule (a shared ~/.config/ReShade file,
            // never the link), so a rollback keeps the link and puts the shared file back. Throws the rule's refusal
            // before anything is written.
            var written = ReShadeIniManager.ResolveWriteTarget(ini, game.BinaryDirectory!, game.InstallPath);
            await scope.TrackAsync(written, token).ConfigureAwait(false);
            var state = LumaIniOverrideManager.SetHdrMode(game.BinaryDirectory!, LumaIniOverrideManager.Deserialize(record.Metadata.GetValueOrDefault(LumaIniOverrideManager.MetadataKey)), mode, game.InstallPath);
            await scope.DeclareWrittenContentAsync(written, token).ConfigureAwait(false);
            record.Metadata[LumaIniOverrideManager.MetadataKey] = LumaIniOverrideManager.Serialize(state);
            return ini;
        }, cancellationToken);

    public Task<ComponentOperationResult> SetTaaAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
        => ChangeAsync(game, "TAA", async (record, scope, inis, token) =>
        {
            var previous = LumaEngineIniOverrideManager.Deserialize(record.Metadata.GetValueOrDefault(LumaEngineIniOverrideManager.MetadataKey));
            var ini = previous?.IniPath ?? LumaEngineIniOverrideManager.ResolveEngineIniPath(game)
                      ?? throw new InvalidOperationException(NoEngineIniPrefix + game.Name);
            // #213: Engine.ini follows its own link rule (a file named Engine.ini in the same prefix or UE project):
            // tracked at the file the write replaces, never the link. w8 integration: through RenoDX's one Engine.ini
            // tracker (#205), so its Unix mode (a user's read-only lock) and any folder the write creates are journaled too.
            var written = await inis.TrackEngineIniAsync(ini, token).ConfigureAwait(false);
            var state = LumaEngineIniOverrideManager.SetTaa(ini, previous, on);
            if (File.Exists(written)) await scope.DeclareWrittenContentAsync(written, token).ConfigureAwait(false);
            record.Metadata[LumaEngineIniOverrideManager.MetadataKey] = state is null ? "" : LumaEngineIniOverrideManager.Serialize(state);
            return ini;
        }, cancellationToken);

    private async Task<ComponentOperationResult> ChangeAsync(DetectedGame game, string what,
        Func<LinuxDeploymentRecord, DeploymentScope, RenoDxIniTracking, CancellationToken, Task<string>> change, CancellationToken cancellationToken)
    {
        if (writeRefusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        // Find hands out a copy; the ledger changes only through the stamped Upsert below.
        var record = records.Find(game, ManagedComponentKind.LumaFramework);
        if (record is null || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return ComponentOperationResult.Fail(NotManagedPrefix + game.Name);
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(journal, $"Luma {what}: {game.Name}", game, ManagedComponentKind.LumaFramework, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var inis = new RenoDxIniTracking(scope);
        string message;
        string? changed = null;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            message = changed = await change(record, scope, inis, cancellationToken).ConfigureAwait(false);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            // #204 (w8 integration): Luma's record landed and then the save reported an error: the change stands.
            if (rollback.Committed) return DeploymentScope.Landed("Luma", false, changed!);
            if (rollback.RolledBack) inis.RestoreModesAfterRollback(); // unjournaled: the Engine.ini lock back
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(message, message); // the changed file; the UI words the outcome
    }
}
