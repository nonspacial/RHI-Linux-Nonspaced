using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #196 (upstream <c>DllOverrideService.UpdateDllOverrideNames</c> / Reset DLL Names): renames the proxy DLL of an
/// installed RHI component (ReShade, OptiScaler) in place, in ONE journaled operation: the file moves to the new name,
/// the per-game Wine override moves with it (the new name's <c>native,builtin</c> set, the old one restored while it still
/// holds RHI's value), the saved DLL-name choice changes, and the component's record names the new file — the stamped
/// record save is the commit point. A failure, or a crash before that save, puts the old name, override, choice and
/// record back. Refused, with nothing changed, when the new name is taken by any file or another RHI record, when the
/// file no longer matches its record, when RHI keeps the game's own file under the old name, while Wine runs for the
/// prefix, or under the Xbox write gate (the caller asks it first). Cancel is honoured only before the first write.
/// </summary>
public static class ProxyRename
{
    /// <summary>What one rename needs from its component service.</summary>
    /// <param name="Payload">The record's proxy file (the file that is renamed).</param>
    /// <param name="NewName">The offered name to rename to (already checked against the component's list).</param>
    /// <param name="AfterRename">Changes the component's own record fields for the new name (OptiScaler's metadata).</param>
    /// <param name="Choice">The saved DLL-name choice to write with the rename (null: unchanged).</param>
    internal sealed record Request(
        DetectedGame Game,
        LinuxDeploymentRecord Record,
        LinuxDeployedFile Payload,
        string NewName,
        Action<LinuxDeploymentRecord>? AfterRename,
        StagedFileWrite? Choice);

    /// <summary>Fixed text before the component when RHI has not installed it on the game (PT-BR presentation pair).</summary>
    public const string NotInstalledPrefix = "Nothing was renamed: RHI has not installed this component on this game: ";

    /// <summary>Fixed text before the name when the DLL already has the chosen name; only the choice is saved (PT-BR pair).</summary>
    public const string NameKeptPrefix = "The DLL already has this name; the choice is saved: ";

    /// <summary>Fixed text before the file when the proxy changed since RHI installed it (PT-BR presentation pair).</summary>
    public const string ChangedPrefix = "The DLL was not renamed: the file changed since RHI installed it. File: ";

    /// <summary>Fixed text before the file when RHI keeps the game's own file under the old name (PT-BR presentation pair).</summary>
    public const string OriginalKeptPrefix =
        "The DLL was not renamed: RHI keeps the game's own file of that name as a backup, so the name must stay; remove the component and install it again under the new name. File: ";

    /// <summary>Fixed text before the file when another file already uses the new name (PT-BR presentation pair).</summary>
    public const string TargetTakenPrefix = "The DLL was not renamed: another file already uses that name in the game folder. File: ";

    /// <summary>Fixed text before "name (component)" when another RHI component holds the new name (PT-BR presentation pair).</summary>
    public const string TargetHeldPrefix = "The DLL was not renamed: another component RHI installed on this game uses that name. Name and component: ";

    /// <summary>The prefix cannot take the Wine override (PT-BR presentation pair).</summary>
    public const string NoPrefixMessage = "The DLL was not renamed: the game's Wine/Proton prefix is not set up; start the game once first.";

    /// <summary>Fixed text before "old → new" after a rename (PT-BR presentation pair).</summary>
    public const string RenamedPrefix = "DLL renamed in place; its Wine override and RHI's record follow it: ";

    /// <summary>Fixed text before the reason of a rename that failed and was rolled back (PT-BR presentation pair).</summary>
    public const string FailedPrefix = "The DLL rename failed and was rolled back; the DLL keeps its name: ";

    /// <summary>
    /// The refusal of renaming <paramref name="request"/>'s payload, or null when it can go ahead (read only). Checked again
    /// by <see cref="RunAsync"/>.
    /// </summary>
    internal static string? Refusal(LinuxDeploymentRecordStore records, Request request)
        => PayloadRefusal(request.Payload) ?? TargetRefusal(records, request);

    /// <summary>Why the installed file cannot move under any name (changed since RHI installed it, or a kept original), or null.</summary>
    internal static string? PayloadRefusal(LinuxDeployedFile payload)
    {
        if (payload.ExistedBeforeInstall || File.Exists(payload.Path + TransactionalFileDeploymentService.BackupExtension))
            return OriginalKeptPrefix + payload.Path;
        if (!File.Exists(payload.Path)) return ChangedPrefix + payload.Path;
        if (!string.IsNullOrWhiteSpace(payload.Sha256) && !FileHash.HexEquals(FileHash.Sha256Hex(payload.Path), payload.Sha256))
            return ChangedPrefix + payload.Path;
        return null;
    }

    /// <summary>
    /// Why the payload cannot take <see cref="Request.NewName"/> (another RHI record lists it, a file or a kept original
    /// already uses it, the prefix cannot take its Wine override), or null. Reads no file content (the picker asks it for
    /// every name).
    /// </summary>
    internal static string? TargetRefusal(LinuxDeploymentRecordStore records, Request request)
    {
        var target = TargetPath(request);
        if (RecordHolding(records, request.Game, target, request.Record.Component) is { } holder)
            return TargetHeldPrefix + $"{request.NewName} ({LinuxManagedComponentService.DisplayDependencyName(holder.Component)})";
        var directory = Path.GetDirectoryName(target)!;
        if (ReShadeDeploymentService.FindFileCaseInsensitive(directory, request.NewName) is { } taken)
            return TargetTakenPrefix + taken;
        if (File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
            return TargetTakenPrefix + target + TransactionalFileDeploymentService.BackupExtension;
        if ((ProxyDllNames.NeedsWineOverride(request.NewName) || request.Record.WineOverride is not null)
            && (request.Game.PrefixPath is not { } prefix || !Directory.Exists(prefix) || !WineRegistryEditor.HasUserHive(prefix)))
            return NoPrefixMessage;
        return null;
    }

    /// <summary>Renames the payload (see the class summary). The caller checked the Xbox gate and its own component rules.</summary>
    internal static async Task<ComponentOperationResult> RunAsync(
        DeploymentJournal? journal,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        string componentName,
        Request request,
        CancellationToken cancellationToken)
    {
        var game = request.Game;
        if (overrides.EditRefusal(game.PrefixPath) is { } wineRunning) return ComponentOperationResult.Fail(wineRunning);
        if (Refusal(records, request) is { } refused) return ComponentOperationResult.Fail(refused);

        var record = request.Record;
        var payload = request.Payload;
        var oldPath = payload.Path;
        var oldName = Path.GetFileName(oldPath);
        var target = TargetPath(request);
        var oldOverride = record.WineOverride;
        var prefix = game.PrefixPath;
        var sha = payload.Sha256 is { Length: > 0 } recorded ? recorded : FileHash.Sha256Hex(oldPath);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            journal, $"{componentName} DLL rename: {game.Name}", game, record.Component, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;

        // Cancel is honoured only up to here: from the first change on the rename finishes or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        WineOverrideRecord? newOverride = null;
        WineOverrideRestoreResult? oldRestore = null;
        try
        {
            await scope.TrackAsync(oldPath, cancellationToken).ConfigureAwait(false);
            await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
            await scope.DeclareWriteAsync(target, sha, cancellationToken).ConfigureAwait(false);
            AtomicFile.Rename(oldPath, target);

            // The old name's override goes back first, then the new name's is set: a per-exe section RHI created stays
            // RHI's (removed with its last value), so removal later leaves user.reg as it was before the install.
            if (oldOverride is not null)
                oldRestore = await overrides.RestoreAsync(scope.Operation, prefix!, oldOverride, cancellationToken).ConfigureAwait(false);
            if (ProxyDllNames.NeedsWineOverride(request.NewName))
                newOverride = await overrides.EnsureNativeBuiltinAsync(
                    scope.Operation, prefix!, request.NewName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);

            // The saved choice goes before the stamped record save (the commit point), tracked and declared first.
            if (request.Choice is { } choice)
            {
                await scope.TrackAsync(choice.Path, cancellationToken).ConfigureAwait(false);
                await scope.DeclareWriteAsync(choice.Path, FileHash.Sha256HexOf(choice.After), cancellationToken).ConfigureAwait(false);
                AtomicFile.WriteStoreFile(choice.Path, choice.After);
            }

            payload.Path = target;
            payload.Sha256 = sha; // a rename cannot change content
            record.WineOverride = newOverride;
            request.AfterRename?.Invoke(record);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            // Never cancelled: files (the DLL under its old name, the saved choice) come back from the scope's copies,
            // the journaled override steps are undone; without a journal the two override steps are undone here.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (scope.Operation is null && prefix is not null)
            {
                if (newOverride is not null) overrides.TryRestore(prefix, newOverride);
                if (oldRestore?.Outcome == WineOverrideRestoreOutcome.Restored) overrides.TryReapply(prefix, oldOverride);
            }
            if (!rollback.Success)
                AppLog.Warn("dll-rename", $"Rename rollback did not complete: {string.Join("; ", rollback.Failures)}");
            return ComponentOperationResult.Fail(FailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        // The stamped record is saved: the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(RenamedPrefix + $"{oldName} → {request.NewName}" + WineDllOverrideService.KeptValueNotes(oldRestore), target);
    }

    private static string TargetPath(Request request)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.Payload.Path))!, request.NewName);

    // Another RHI record of the game (any component but the renamed one) that lists the target path.
    private static LinuxDeploymentRecord? RecordHolding(LinuxDeploymentRecordStore records, DetectedGame game, string target, ManagedComponentKind renamed)
        => records.FindAll(game).FirstOrDefault(other => other.Component != renamed
            && other.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path)
                && Path.GetFullPath(file.Path).Equals(target, StringComparison.OrdinalIgnoreCase)));
}
