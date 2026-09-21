using System.Security.Cryptography;

namespace RHI.Core.Services;

/// <summary>
/// Safe replacement primitive for RHI-managed game files.
/// The first real replacement preserves the true original as .original. Identical files are
/// treated as a no-op and never create a pointless backup. Every copy is SHA-256 verified and
/// restore keeps the .original until the restored target has been verified byte-for-byte.
/// </summary>
public sealed class TransactionalFileDeploymentService : IFileDeploymentService
{
    public const string BackupExtension = ".original";

    public async Task<FileDeploymentResult> ReplaceAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            return new(false, targetPath, null, $"Source file does not exist: {sourcePath}");

        var targetDir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            return new(false, targetPath, null, $"Target directory does not exist: {targetDir}");

        var backupPath = targetPath + BackupExtension;
        var transactionId = Guid.NewGuid().ToString("N");
        var tempPath = targetPath + $".rhi-tmp-{transactionId}";
        var currentSnapshot = targetPath + $".rhi-replace-current-{transactionId}";
        var hadCurrent = File.Exists(targetPath);
        var backupExistedBefore = File.Exists(backupPath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Do not create .original for an already-identical file. This matters for games that
            // already ship the same Streamline/DLSS build selected in RHI.
            if (File.Exists(targetPath) && await FilesEqualAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false))
                return new(true, targetPath, File.Exists(backupPath) ? backupPath : null, null, Changed: false);

            if (hadCurrent)
                await CopyVerifiedAsync(targetPath, currentSnapshot, cancellationToken).ConfigureAwait(false);

            if (hadCurrent && !File.Exists(backupPath))
                await CopyVerifiedAsync(targetPath, backupPath, cancellationToken).ConfigureAwait(false);

            await CopyVerifiedAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);

            if (File.Exists(targetPath))
                File.Move(tempPath, targetPath, overwrite: true);
            else
                File.Move(tempPath, targetPath);

            if (!await FilesEqualAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false))
                throw new IOException($"Post-replace SHA-256 verification failed for {targetPath}.");

            TryDelete(currentSnapshot);
            return new(true, targetPath, File.Exists(backupPath) ? backupPath : null, null, Changed: true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            try
            {
                if (hadCurrent && File.Exists(currentSnapshot))
                    File.Copy(currentSnapshot, targetPath, overwrite: true);
                else if (!hadCurrent)
                    TryDelete(targetPath);
            }
            catch { }
            TryDelete(currentSnapshot);
            if (!backupExistedBefore)
                TryDelete(backupPath);
            return new(false, targetPath, File.Exists(backupPath) ? backupPath : null, ex.Message);
        }
    }

    public async Task<FileDeploymentResult> RestoreAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        var backupPath = targetPath + BackupExtension;
        if (!File.Exists(backupPath))
            return new(false, targetPath, null, "No .original backup exists.");

        var transactionId = Guid.NewGuid().ToString("N");
        var restoreTemp = targetPath + $".rhi-restore-tmp-{transactionId}";
        var currentSnapshot = targetPath + $".rhi-restore-current-{transactionId}";
        var hadCurrent = File.Exists(targetPath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Preserve the currently active managed file until the original has been fully
            // restored and hash-verified. This makes a failed restore recoverable.
            if (hadCurrent)
                await CopyVerifiedAsync(targetPath, currentSnapshot, cancellationToken).ConfigureAwait(false);

            await CopyVerifiedAsync(backupPath, restoreTemp, cancellationToken).ConfigureAwait(false);
            var expectedOriginalHash = await ComputeSha256Async(backupPath, cancellationToken).ConfigureAwait(false);

            if (File.Exists(targetPath))
                File.Move(restoreTemp, targetPath, overwrite: true);
            else
                File.Move(restoreTemp, targetPath);

            var restoredHash = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedOriginalHash, restoredHash))
                throw new IOException($"Post-restore SHA-256 verification failed for {targetPath}.");

            // Only consume .original after the target is proven byte-identical to it.
            File.Delete(backupPath);
            TryDelete(currentSnapshot);
            TryDelete(targetPath + ".rhi_custom");
            return new(true, targetPath, null, null, Changed: true);
        }
        catch (Exception ex)
        {
            TryDelete(restoreTemp);
            try
            {
                if (hadCurrent && File.Exists(currentSnapshot))
                    File.Copy(currentSnapshot, targetPath, overwrite: true);
                else if (!hadCurrent)
                    TryDelete(targetPath);
            }
            catch { }
            TryDelete(currentSnapshot);
            return new(false, targetPath, backupPath, ex.Message);
        }
    }

    public async Task<FileDeploymentBatchResult> ReplaceBatchAsync(
        IEnumerable<(string Source, string Target)> files,
        CancellationToken cancellationToken = default)
    {
        var requested = files.ToArray();
        if (requested.Length == 0) return new(true, Array.Empty<FileDeploymentResult>());

        var transactionId = Guid.NewGuid().ToString("N");
        var snapshots = new Dictionary<string, (bool Existed, bool OriginalBackupExisted, string? Snapshot)>(StringComparer.Ordinal);
        var results = new List<FileDeploymentResult>(requested.Length);

        try
        {
            foreach (var (_, target) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshots.ContainsKey(target)) continue;
                var existed = File.Exists(target);
                var originalBackupExisted = File.Exists(target + BackupExtension);
                string? snapshot = null;
                if (existed)
                {
                    snapshot = target + $".rhi-rollback-{transactionId}";
                    await CopyVerifiedAsync(target, snapshot, cancellationToken).ConfigureAwait(false);
                }
                snapshots[target] = (existed, originalBackupExisted, snapshot);
            }

            foreach (var (source, target) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ReplaceAsync(source, target, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (!result.Success)
                    throw new IOException(result.Error ?? $"Failed to replace {target}");
            }

            foreach (var state in snapshots.Values)
                if (state.Snapshot is { } snapshot) TryDelete(snapshot);

            return new(true, results);
        }
        catch (Exception ex)
        {
            foreach (var (target, state) in snapshots.Reverse())
            {
                try
                {
                    if (state.Existed && state.Snapshot is { } snapshot && File.Exists(snapshot))
                    {
                        File.Copy(snapshot, target, overwrite: true);
                    }
                    else if (!state.Existed)
                    {
                        TryDelete(target);
                    }

                    if (!state.OriginalBackupExisted)
                        TryDelete(target + BackupExtension);
                }
                catch { }
            }

            foreach (var state in snapshots.Values)
                if (state.Snapshot is { } snapshot) TryDelete(snapshot);

            if (ex is OperationCanceledException) throw;
            return new(false, results, ex.Message);
        }
    }

    public async Task<FileDeploymentBatchResult> RestoreBatchAsync(
        IEnumerable<string> targetPaths,
        CancellationToken cancellationToken = default)
    {
        var requested = targetPaths.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0)
            return new(false, Array.Empty<FileDeploymentResult>(), "No targets were supplied for restore.");

        foreach (var target in requested)
        {
            if (!File.Exists(target + BackupExtension))
                return new(false, Array.Empty<FileDeploymentResult>(), $"No .original backup exists for {target}.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var snapshots = new Dictionary<string, (bool TargetExisted, string? TargetSnapshot, string BackupSnapshot)>(StringComparer.Ordinal);
        var results = new List<FileDeploymentResult>(requested.Length);

        try
        {
            foreach (var target in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? targetSnapshot = null;
                if (File.Exists(target))
                {
                    targetSnapshot = target + $".rhi-restore-target-{transactionId}";
                    await CopyVerifiedAsync(target, targetSnapshot, cancellationToken).ConfigureAwait(false);
                }

                var backup = target + BackupExtension;
                var backupSnapshot = target + $".rhi-restore-backup-{transactionId}";
                await CopyVerifiedAsync(backup, backupSnapshot, cancellationToken).ConfigureAwait(false);
                snapshots[target] = (File.Exists(target), targetSnapshot, backupSnapshot);
            }

            foreach (var target in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await RestoreAsync(target, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (!result.Success)
                    throw new IOException(result.Error ?? $"Failed to restore {target}");
            }

            CleanupRestoreSnapshots(snapshots);
            return new(true, results);
        }
        catch (Exception ex)
        {
            foreach (var (target, state) in snapshots.Reverse())
            {
                try
                {
                    if (state.TargetExisted && state.TargetSnapshot is { } targetSnapshot && File.Exists(targetSnapshot))
                        File.Copy(targetSnapshot, target, overwrite: true);
                    else if (!state.TargetExisted)
                        TryDelete(target);

                    if (File.Exists(state.BackupSnapshot))
                        File.Copy(state.BackupSnapshot, target + BackupExtension, overwrite: true);
                }
                catch { }
            }

            CleanupRestoreSnapshots(snapshots);
            if (ex is OperationCanceledException) throw;
            return new(false, results, ex.Message);
        }
    }

    private static void CleanupRestoreSnapshots(
        Dictionary<string, (bool TargetExisted, string? TargetSnapshot, string BackupSnapshot)> snapshots)
    {
        foreach (var state in snapshots.Values)
        {
            if (state.TargetSnapshot is { } targetSnapshot) TryDelete(targetSnapshot);
            TryDelete(state.BackupSnapshot);
        }
    }

    private static async Task<bool> FilesEqualAsync(string first, string second, CancellationToken ct)
    {
        if (!File.Exists(first) || !File.Exists(second)) return false;
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        var firstHash = await ComputeSha256Async(first, ct).ConfigureAwait(false);
        var secondHash = await ComputeSha256Async(second, ct).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static async Task CopyVerifiedAsync(string source, string target, CancellationToken ct)
    {
        const int BufferSize = 1024 * 1024;
        var sourceHash = await ComputeSha256Async(source, ct).ConfigureAwait(false);

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await input.CopyToAsync(output, BufferSize, ct).ConfigureAwait(false);

        var targetHash = await ComputeSha256Async(target, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
            throw new IOException($"SHA-256 copy verification failed: {source} -> {target}.");
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
