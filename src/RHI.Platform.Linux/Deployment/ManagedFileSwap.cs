using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// In-place replacement of files already owned by an RHI deployment record (upstream RHI 2.7.6
/// "swap while installed"). Only the named files change; every other file of the record is left
/// alone. Each target must still match its recorded hash, so a file edited outside RHI is never
/// overwritten. The caller's record is never mutated: the new hashes go into a copy, which the
/// persist callback updates and saves. When copying or persisting fails, the previous bytes are
/// put back; a file that cannot be put back keeps its backup and is reported, never hidden.
/// </summary>
public static class ManagedFileSwap
{
    public const string BackupSuffix = ".rhi-swap-old";
    private const string StagingSuffix = ".rhi-swap-new";

    /// <summary>Returns the record file matching <paramref name="fileName"/> (case-insensitive leaf name).</summary>
    public static LinuxDeployedFile? FindFile(LinuxDeploymentRecord record, string fileName)
        => record.Files.FirstOrDefault(file =>
            Path.GetFileName(file.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Replaces each recorded file (by its recorded path) with its source, then hands a copy of
    /// <paramref name="record"/> carrying the new hashes to <paramref name="persist"/>, which must
    /// apply any other changes to that copy and save it. Returns the number of files whose bytes
    /// changed. <paramref name="record"/> itself is left as it was, whatever happens.
    /// </summary>
    /// <exception cref="ManagedFileRestoreException">A previous file could not be put back; its backup was kept.</exception>
    public static async Task<int> ReplaceAsync(
        LinuxDeploymentRecord record,
        IReadOnlyList<(string RecordedPath, string SourcePath)> items,
        Action<LinuxDeploymentRecord> persist,
        CancellationToken cancellationToken = default)
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

        // Verified: from the first swap on, Cancel is refused and the swap runs to completion or full rollback.
        _ = OperationCancellation.BeginWrites(cancellationToken);
        var swapped = new List<LinuxDeployedFile>();
        var keptBackups = new List<string>();
        try
        {
            foreach (var (file, source, newHash, oldHash) in plan)
            {
                if (string.Equals(newHash, oldHash, StringComparison.OrdinalIgnoreCase)) continue;
                var staging = file.Path + StagingSuffix;
                File.Copy(file.Path, file.Path + BackupSuffix, overwrite: true);
                File.Copy(source, staging, overwrite: true);
                File.Move(staging, file.Path, overwrite: true);
                swapped.Add(file);
                file.Sha256 = newHash;
            }
            persist(updated);
        }
        catch (Exception failure)
        {
            // Compensation never honours cancellation: a half-swapped component must be restored.
            foreach (var file in swapped.AsEnumerable().Reverse())
            {
                var backup = file.Path + BackupSuffix;
                // A rename is atomic and consumes the backup only once it has taken the file's place.
                try { File.Move(backup, file.Path, overwrite: true); }
                catch { keptBackups.Add(backup); }
            }
            if (keptBackups.Count > 0)
                throw new ManagedFileRestoreException(keptBackups, failure);
            throw;
        }
        finally
        {
            foreach (var (file, _, _, _) in plan)
            {
                FileCleanup.TryDelete(file.Path + StagingSuffix);
                if (!keptBackups.Contains(file.Path + BackupSuffix))
                    FileCleanup.TryDelete(file.Path + BackupSuffix);
            }
        }
        return swapped.Count;
    }

}

/// <summary>
/// A swap failed and at least one previous file could not be put back. Its backup is kept beside
/// it; the deployment record still describes the previous file.
/// </summary>
public sealed class ManagedFileRestoreException(IReadOnlyList<string> backupPaths, Exception cause)
    : IOException(
        $"The previous file could not be restored. Its backup is kept at {string.Join(", ", backupPaths)}; " +
        $"move it back over the original name before retrying. Cause: {cause.Message}", cause)
{
    public IReadOnlyList<string> BackupPaths { get; } = backupPaths;
}
