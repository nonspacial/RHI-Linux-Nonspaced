using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

// w7/reshade (#201): the XNA GAC copies of ReShade's install, update and removal (see XnaGacReShade).
public sealed partial class ReShadeDeploymentService
{
    /// <summary>
    /// Why the GAC folder cannot take ReShade's copies, or null: a <c>d3d9.dll</c> or <c>ReShade.ini</c> RHI did not
    /// place there (any spelling), or RHI's DLL copy changed since it was placed. Read only, before any download.
    /// </summary>
    private async Task<string?> XnaGacRefusalAsync(LinuxDeploymentRecord? existing, string folder, CancellationToken cancellationToken)
    {
        var ours = XnaGacReShade.IsGacRecord(existing)
                   && string.Equals(existing!.Metadata[XnaGacReShade.FolderMetadataKey], folder, StringComparison.Ordinal);
        if (FindFileCaseInsensitive(folder, XnaGacReShade.ProxyName) is { } dll)
        {
            var recorded = ours ? existing!.Files.FirstOrDefault(file => string.Equals(file.Path, dll, StringComparison.Ordinal)) : null;
            if (recorded is null) return XnaGacReShade.ForeignFilePrefix + dll;
            if (!string.IsNullOrWhiteSpace(recorded.Sha256)
                && !FileHash.HexEquals(await FileHash.Sha256HexAsync(dll, cancellationToken).ConfigureAwait(false), recorded.Sha256))
                return XnaGacReShade.ChangedCopyPrefix + dll;
        }
        if (FindFileCaseInsensitive(folder, "ReShade.ini") is { } ini
            && (!ours || !string.Equals(ini, Path.Combine(folder, "ReShade.ini"), StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(existing!.Metadata.GetValueOrDefault(XnaGacReShade.IniSha256MetadataKey))))
            return XnaGacReShade.ForeignFilePrefix + ini;
        return null;
    }

    /// <summary>
    /// Copies <paramref name="source"/> (verified, <paramref name="sha256"/>) to <paramref name="target"/> in the GAC
    /// folder, tracked (journal, else <paramref name="local"/>) and declared before the copy, so a failure or a crash puts
    /// the folder back as it was.
    /// </summary>
    private static async Task PlaceGacCopyAsync(DeploymentOperation? operation, OwnedFileSet? local, string source, string target,
        string sha256, CancellationToken cancellationToken)
    {
        await TrackGacWriteAsync(operation, local, target, sha256, cancellationToken).ConfigureAwait(false);
        await AtomicFile.CopyAsync(source, target, sha256, CancellationToken.None, overwrite: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> (RHI's GAC ReShade.ini) to <paramref name="target"/>, tracked (journal, else
    /// <paramref name="local"/>) and declared before the write; returns their SHA-256.
    /// </summary>
    private static async Task<string> PlaceGacBytesAsync(DeploymentOperation? operation, OwnedFileSet? local, string target,
        byte[] bytes, CancellationToken cancellationToken)
    {
        var sha256 = FileHash.Sha256HexOf(bytes);
        await TrackGacWriteAsync(operation, local, target, sha256, cancellationToken).ConfigureAwait(false);
        AtomicFile.WriteAllBytes(target, bytes);
        return sha256;
    }

    // Tracks a GAC folder file (journal, else the local set) and declares what is about to be written there.
    private static async Task TrackGacWriteAsync(DeploymentOperation? operation, OwnedFileSet? local, string target, string sha256,
        CancellationToken cancellationToken)
    {
        if (operation is not null)
        {
            await operation.TrackFileAsync(target, cancellationToken, includeBackup: false).ConfigureAwait(false);
            await operation.RecordWriteAsync(target, sha256, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await local!.TrackWithSnapshotAsync(target, cancellationToken, includeBackup: false).ConfigureAwait(false);
            await local.RecordWriteAsync(target, sha256, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// #214: Repair after move re-points the records but writes no file, so RHI's GAC ReShade.ini copy still names the old
    /// game folder as <c>[INSTALL] BasePath</c>. Called with the repair's result (the one hook in
    /// <c>LinuxManagedComponentService.RepairAfterMove</c>): after a successful repair that moved an XNA GAC record, the
    /// copy's BasePath is rewritten when it names another folder (<see cref="RepointGacIniAsync"/>; nothing for a game
    /// without an XNA ReShade); a note the user needs is added to the result line.
    /// The repair itself is never undone here; nothing is done after a repair that failed (the records still name the old
    /// folders).
    /// </summary>
    internal static RelocationRepairResult AfterRepair(ReShadeDeploymentService? service, DetectedGame game, RelocationRepairResult result)
    {
        if (service is null || !result.Success) return result;
        // Repair after move is synchronous (the view model runs it off the UI thread); the rewrite is one journaled step.
        var note = service.RepointGacIniAsync(game, CancellationToken.None).GetAwaiter().GetResult();
        return note.Length == 0 ? result : result with { Message = result.Message + XnaGacReShade.AfterMoveNoteSeparator + note, Note = note };
    }

    /// <summary>
    /// #214 (ticket option 1): rewrites <c>[INSTALL] BasePath</c> in RHI's GAC ReShade.ini copy to the game folder as the
    /// game sees it now (<see cref="XnaGacReShade.WindowsPathOf"/>, as the install writes it), only while that copy still
    /// holds RHI's last write (<see cref="XnaGacReShade.IniSha256MetadataKey"/>); journaled on ReShade's record (the bytes
    /// staged and declared before the write, the new hash saved with the stamped record: a crash leaves the old or the new
    /// file, and the record agrees). Returns "" when done or nothing to do, else the note for the repair's line: a copy
    /// the user changed is kept (what to set is said), Wine running for the prefix, or a write that failed (rolled back).
    /// </summary>
    internal async Task<string> RepointGacIniAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        // Find hands out a copy: the ledger changes only through the stamped Upsert below.
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (!XnaGacReShade.IsGacRecord(record) || string.IsNullOrWhiteSpace(game.PrefixPath) || string.IsNullOrWhiteSpace(game.BinaryDirectory))
            return "";
        var gacIni = Path.Combine(record!.Metadata[XnaGacReShade.FolderMetadataKey], "ReShade.ini");
        if (!File.Exists(gacIni)) return ""; // no copy (installed without managing ReShade.ini): nothing names a folder
        var basePath = XnaGacReShade.WindowsPathOf(game.PrefixPath, game.BinaryDirectory);
        byte[] current;
        bool rhis;
        try
        {
            // Review 2: the repair has already saved; a copy that cannot be read (permissions, I/O) is a note, never a throw.
            current = await File.ReadAllBytesAsync(gacIni, CancellationToken.None).ConfigureAwait(false);
            if (string.Equals(IniLines.Get(IniLines.FromBytes(current), "INSTALL", "BasePath"), basePath, StringComparison.Ordinal)) return "";
            rhis = GacIniIsRhis(gacIni, record.Metadata.GetValueOrDefault(XnaGacReShade.IniSha256MetadataKey));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return XnaGacReShade.GacIniNotUpdatedAfterMovePrefix + ex.Message;
        }
        if (!rhis) return XnaGacReShade.GacIniChangedAfterMove(gacIni, basePath);
        if (_overrides.EditRefusal(game.PrefixPath) is not null) return XnaGacReShade.GacIniWineRunningAfterMoveMessage;

        var bytes = ReShadeIniManager.WithInstallBasePath(current, basePath);
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(_journal, $"ReShade XNA settings file after a move: {game.Name}", game,
            ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return XnaGacReShade.GacIniNotUpdatedAfterMovePrefix + journalRefusal;
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            record.Metadata[XnaGacReShade.IniSha256MetadataKey] = await scope.PlaceBytesAsync(gacIni, bytes, cancellationToken).ConfigureAwait(false);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return XnaGacReShade.GacIniNotUpdatedAfterMovePrefix + ex.Message + DeploymentScope.RollbackNote(rollback);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return "";
    }

    // The GAC ReShade.ini may be (re)written: absent, or still the copy RHI last placed (a changed one is the user's).
    private static bool GacIniIsRhis(string gacIni, string? lastCopySha256)
        => !File.Exists(gacIni)
           || (lastCopySha256 is { Length: > 0 } && FileHash.HexEquals(FileHash.Sha256Hex(gacIni), lastCopySha256));
}
