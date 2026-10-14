using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// In-place replacement of files already owned by an RHI deployment record (upstream RHI 2.7.6
/// "swap while installed"). Only the named files change; every other file of the record is left
/// alone. Each target must still match its recorded hash, so a file edited outside RHI is never
/// overwritten. The caller's record is never mutated: the new hashes go into a copy, which the
/// persist callback updates and saves. Every target is tracked with a verified copy before it changes and the new
/// content is placed through a verified, durable rename (#31); with a journal the swap is an operation whose commit
/// point is the persisted record (#23), so a crash before it is saved puts the previous files back at the next start.
/// When copying or persisting fails, the previous bytes are put back; a file that cannot be put back keeps its copy and
/// is reported, never hidden.
/// </summary>
public static class ManagedFileSwap
{
    /// <summary>Returns the record file matching <paramref name="fileName"/> (case-insensitive leaf name).</summary>
    public static LinuxDeployedFile? FindFile(LinuxDeploymentRecord record, string fileName)
        => record.Files.FirstOrDefault(file =>
            Path.GetFileName(file.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Replaces each recorded file (by its recorded path) with its source, then hands a copy of
    /// <paramref name="record"/> carrying the new hashes (and, journaled, the operation id) to <paramref name="persist"/>,
    /// which must apply any other changes to that copy and save it. Returns the number of files whose bytes
    /// changed. <paramref name="record"/> itself is left as it was, whatever happens.
    /// </summary>
    /// <param name="journal">The app's journal; with <paramref name="game"/>, the swap is journaled.</param>
    /// <exception cref="ManagedFileRestoreException">A previous file could not be put back; its copy was kept.</exception>
    public static async Task<int> ReplaceAsync(
        LinuxDeploymentRecord record,
        IReadOnlyList<(string RecordedPath, string SourcePath)> items,
        Action<LinuxDeploymentRecord> persist,
        CancellationToken cancellationToken = default,
        DeploymentJournal? journal = null,
        DetectedGame? game = null)
    {
        var updated = LinuxDeploymentRecordStore.Clone(record);

        // Verify everything before touching anything.
        var plan = new List<(LinuxDeployedFile File, string Source, string NewHash, string? OldHash)>();
        foreach (var (recordedPath, source) in items)
        {
            var file = updated.Files.FirstOrDefault(f => string.Equals(f.Path, recordedPath, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The deployment record does not own {recordedPath}.");
            if (!File.Exists(source))
                throw new FileNotFoundException("Swap source is missing.", source);
            if (!File.Exists(file.Path))
                throw new InvalidOperationException($"Managed file is missing: {file.Path}. Reinstall the component instead.");
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(file.Sha256) &&
                !string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Managed file was modified outside RHI and will not be replaced: {file.Path}");
            plan.Add((file, source, await FileHash.Sha256HexAsync(source, cancellationToken).ConfigureAwait(false), file.Sha256));
        }

        DeploymentScope scope;
        if (journal is not null && game is not null)
        {
            var (opened, refusal) = await DeploymentScope.BeginAsync(
                journal, $"{record.Component} swap: {game.Name}", game, record.Component, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) throw new IOException(refusal);
            scope = opened!;
        }
        else scope = DeploymentScope.Unjournaled();

        await using (scope.ConfigureAwait(false))
        {
            // Verified: from the first swap on, Cancel is refused and the swap runs to completion or full rollback.
            _ = OperationCancellation.BeginWrites(cancellationToken);
            var swapped = 0;
            try
            {
                foreach (var (file, source, newHash, oldHash) in plan)
                {
                    if (string.Equals(newHash, oldHash, StringComparison.OrdinalIgnoreCase)) continue;
                    file.Sha256 = await scope.PlaceCopyAsync(source, file.Path, CancellationToken.None, newHash).ConfigureAwait(false);
                    swapped++;
                }
                persist(scope.Stamp(updated));
            }
            catch (Exception failure)
            {
                // Compensation never honours cancellation: a half-swapped component must be restored.
                var rollback = await scope.RollbackAsync().ConfigureAwait(false);
                if (!rollback.Success)
                    throw new ManagedFileRestoreException(
                        plan.Select(item => scope.Files.SnapshotPathOf(item.File.Path)).Where(File.Exists).ToArray(), failure);
                throw;
            }
            await scope.CommitAsync().ConfigureAwait(false);
            return swapped;
        }
    }

}

/// <summary>
/// A swap failed and at least one previous file could not be put back. Its verified copy is kept beside
/// it; the deployment record still describes the previous file.
/// </summary>
public sealed class ManagedFileRestoreException(IReadOnlyList<string> backupPaths, Exception cause)
    : IOException(
        $"The previous file could not be restored. Its backup is kept at {string.Join(", ", backupPaths)}; " +
        $"move it back over the original name before retrying. Cause: {cause.Message}", cause)
{
    public IReadOnlyList<string> BackupPaths { get; } = backupPaths;
}
