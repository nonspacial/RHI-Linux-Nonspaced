namespace RHI.Core.Services;

/// <summary>
/// Safe replacement primitive for RHI-managed game files.
/// The first replacement preserves the true original as .original. Each write goes through
/// a same-directory temporary file followed by an atomic rename when the filesystem allows it.
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
        var tempPath = targetPath + $".rhi-tmp-{Guid.NewGuid():N}";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(targetPath) && !File.Exists(backupPath))
                File.Copy(targetPath, backupPath, overwrite: false);

            await CopyVerifiedAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);

            if (File.Exists(targetPath))
                File.Move(tempPath, targetPath, overwrite: true);
            else
                File.Move(tempPath, targetPath);

            return new(true, targetPath, File.Exists(backupPath) ? backupPath : null);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return new(false, targetPath, File.Exists(backupPath) ? backupPath : null, ex.Message);
        }
    }

    public Task<FileDeploymentResult> RestoreAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        var backupPath = targetPath + BackupExtension;
        if (!File.Exists(backupPath))
            return Task.FromResult(new FileDeploymentResult(false, targetPath, null, "No .original backup exists."));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(backupPath, targetPath, overwrite: true);
            TryDelete(targetPath + ".rhi_custom");
            return Task.FromResult(new FileDeploymentResult(true, targetPath, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileDeploymentResult(false, targetPath, backupPath, ex.Message));
        }
    }

    public async Task<FileDeploymentBatchResult> ReplaceBatchAsync(
        IEnumerable<(string Source, string Target)> files,
        CancellationToken cancellationToken = default)
    {
        var requested = files.ToArray();
        if (requested.Length == 0) return new(true, Array.Empty<FileDeploymentResult>());

        // Snapshot the current state of every target first. This is separate from the long-lived
        // .original backup: rollback must restore the exact pre-batch state, which might itself
        // already be an RHI-managed replacement.
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
                    File.Copy(target, snapshot, overwrite: false);
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
            // A cancelled multi-file operation must be just as atomic as a failed one.
            // Restore every target to the exact state captured before the batch began.
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

                    // If this transaction created the long-lived .original backup, roll that
                    // metadata back too. Existing backups from earlier RHI operations are kept.
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

    private static async Task CopyVerifiedAsync(string source, string target, CancellationToken ct)
    {
        const int BufferSize = 1024 * 1024;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await input.CopyToAsync(output, BufferSize, ct).ConfigureAwait(false);

        var sourceLength = new FileInfo(source).Length;
        var targetLength = new FileInfo(target).Length;
        if (sourceLength != targetLength)
            throw new IOException($"Copy verification failed: {sourceLength} != {targetLength} bytes.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
