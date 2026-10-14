using RHI.Core.Deployment;

namespace RHI.Core.Services;

/// <summary>
/// Safe replacement primitive for RHI-managed game files.
/// The first real replacement preserves the true original as .original. Identical files are
/// treated as a no-op and never create a pointless backup. New content is staged beside the
/// target as a verified, fsynced copy and renamed into place; the previous content is moved
/// aside by rename (to .original on the first replacement, otherwise to a rollback snapshot), so
/// no step ever leaves a half-written game file and a restore consumes .original atomically.
/// Every call runs as one transaction over an <see cref="OwnedFileSet"/>: a failure puts every
/// target and .original back to its exact pre-call content, and with a <see cref="DeploymentJournal"/>
/// the same rollback runs on the next start if the process dies mid-call. Every call ends the caller's cancellable
/// phase (<see cref="OperationCancellation.BeginWrites"/>) before its first change and then runs to completion or full
/// rollback.
/// </summary>
public sealed class TransactionalFileDeploymentService : IFileDeploymentService
{
    public const string BackupExtension = OwnedFileSet.BackupExtension;

    private readonly DeploymentJournal? _journal;

    public TransactionalFileDeploymentService(DeploymentJournal? journal = null) => _journal = journal;

    public async Task<FileDeploymentResult> ReplaceAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var precheck = CheckReplaceInputs(sourcePath, targetPath);
        if (precheck is not null) return precheck;
        if (XboxWriteGate.Refusal(targetPath) is { } xbox) return new(false, targetPath, null, xbox); // w3/scan hook (Refs #90)
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);

        var outcome = await RunAsync($"replace {targetPath}", async set =>
        {
            var result = await ReplaceCoreAsync(set, sourcePath, targetPath, null, cancellationToken).ConfigureAwait(false);
            return new[] { result };
        }).ConfigureAwait(false);

        if (outcome.Error is null) return outcome.Results[0];
        var backupPath = targetPath + BackupExtension;
        return new(false, targetPath, File.Exists(backupPath) ? backupPath : null, outcome.Error);
    }

    public async Task<FileDeploymentResult> RestoreAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        var backupPath = targetPath + BackupExtension;
        if (!File.Exists(backupPath))
            return new(false, targetPath, null, "No .original backup exists.");
        if (XboxWriteGate.Refusal(targetPath) is { } xbox) return new(false, targetPath, null, xbox); // w3/scan hook (Refs #90)
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);

        var outcome = await RunAsync($"restore {targetPath}", async set =>
        {
            var result = await RestoreCoreAsync(set, targetPath, cancellationToken).ConfigureAwait(false);
            return new[] { result };
        }).ConfigureAwait(false);

        return outcome.Error is null
            ? outcome.Results[0]
            : new(false, targetPath, File.Exists(backupPath) ? backupPath : null, outcome.Error);
    }

    public async Task<FileDeploymentBatchResult> ReplaceBatchAsync(
        IEnumerable<(string Source, string Target)> files,
        CancellationToken cancellationToken = default)
    {
        var requested = files.ToArray();
        if (requested.Length == 0) return new(true, Array.Empty<FileDeploymentResult>());
        // The whole batch is refused before anything changes (Refs #90).
        if (XboxWriteGate.Refusal(requested.Select(file => file.Target)) is { } xbox) // w3/scan hook
            return new(false, Array.Empty<FileDeploymentResult>(), xbox);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);

        var results = new List<FileDeploymentResult>(requested.Length);
        var outcome = await RunAsync($"replace {requested.Length} file(s), first {requested[0].Target}", async set =>
        {
            // Each distinct source is hashed at most once for the whole batch.
            var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (source, target) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var precheck = CheckReplaceInputs(source, target);
                if (precheck is not null)
                {
                    results.Add(precheck);
                    throw new IOException(precheck.Error);
                }
                var result = await ReplaceCoreAsync(set, source, target, sourceHashes, cancellationToken).ConfigureAwait(false);
                results.Add(result);
            }
            return results.ToArray();
        }).ConfigureAwait(false);

        if (outcome.Canceled) throw new OperationCanceledException(outcome.Error, cancellationToken);
        return outcome.Error is null ? new(true, outcome.Results) : new(false, results, outcome.Error);
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
        if (XboxWriteGate.Refusal(requested) is { } xbox) // w3/scan hook (Refs #90)
            return new(false, Array.Empty<FileDeploymentResult>(), xbox);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);

        var results = new List<FileDeploymentResult>(requested.Length);
        var outcome = await RunAsync($"restore {requested.Length} file(s), first {requested[0]}", async set =>
        {
            foreach (var target in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await RestoreCoreAsync(set, target, cancellationToken).ConfigureAwait(false));
            }
            return results.ToArray();
        }).ConfigureAwait(false);

        if (outcome.Canceled) throw new OperationCanceledException(outcome.Error, cancellationToken);
        return outcome.Error is null ? new(true, outcome.Results) : new(false, results, outcome.Error);
    }

    private static FileDeploymentResult? CheckReplaceInputs(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
            return new(false, targetPath, null, $"Source file does not exist: {sourcePath}");
        var targetDir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            return new(false, targetPath, null, $"Target directory does not exist: {targetDir}");
        return null;
    }

    /// <summary>
    /// One replacement inside <paramref name="set"/>. I/O for an existing target with no backup:
    /// read the target once (pre-state hash), read the source once while copying, write once,
    /// re-read the staged copy once to verify it; the old content becomes .original by rename.
    /// </summary>
    private static async Task<FileDeploymentResult> ReplaceCoreAsync(
        OwnedFileSet set,
        string sourcePath,
        string targetPath,
        Dictionary<string, string>? sourceHashes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(targetPath);
        var backupPath = target + BackupExtension;
        string? sourceSha = sourceHashes?.GetValueOrDefault(sourcePath);
        string? targetSha = null;

        // Do not create .original for an already-identical file. This matters for games that
        // already ship the same Streamline/DLSS build selected in RHI.
        if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(sourcePath).Length)
        {
            sourceSha ??= await FileHash.Sha256HexAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            targetSha = await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false);
            if (sourceHashes is not null) sourceHashes[sourcePath] = sourceSha;
            if (FileHash.HexEquals(sourceSha, targetSha))
                return new(true, targetPath, File.Exists(backupPath) ? backupPath : null, null, Changed: false, Sha256: targetSha);
        }

        await set.TrackAsync(target, cancellationToken, set.Find(target) is null ? targetSha : null).ConfigureAwait(false);
        var staged = await AtomicFile.StageCopyAsync(sourcePath, target, sourceSha, set.Id, cancellationToken).ConfigureAwait(false);
        if (sourceHashes is not null) sourceHashes[sourcePath] = staged.Sha256;
        try
        {
            await set.RecordWriteAsync(target, staged.Sha256, cancellationToken).ConfigureAwait(false);
            if (File.Exists(target))
            {
                if (!File.Exists(backupPath))
                    AtomicFile.Rename(target, backupPath);
                else if (!File.Exists(set.SnapshotPathOf(target)))
                    AtomicFile.Rename(target, set.SnapshotPathOf(target));
                // Otherwise this set already preserved the target's pre-call content; overwrite.
            }
            // A rename cannot change content, and the staged copy was verified against what was
            // read from the source, so the target needs no second read here.
            AtomicFile.MoveIntoPlace(staged.Path, target);
        }
        finally
        {
            FileCleanup.TryDelete(staged.Path);
        }

        return new(true, targetPath, File.Exists(backupPath) ? backupPath : null, null, Changed: true, Sha256: staged.Sha256);
    }

    /// <summary>
    /// One restore inside <paramref name="set"/>: the managed file is moved aside, then .original is
    /// renamed over the target, which consumes the backup in the same atomic step that restores it.
    /// </summary>
    private static async Task<FileDeploymentResult> RestoreCoreAsync(OwnedFileSet set, string targetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(targetPath);
        var backupPath = target + BackupExtension;
        if (!File.Exists(backupPath))
            throw new IOException($"No .original backup exists for {targetPath}.");

        var backupSha = await FileHash.Sha256HexAsync(backupPath, cancellationToken).ConfigureAwait(false);
        await set.TrackAsync(target, cancellationToken).ConfigureAwait(false);
        await set.CaptureBackupAsync(target, cancellationToken, backupSha).ConfigureAwait(false);
        await set.RecordWriteAsync(target, backupSha, cancellationToken).ConfigureAwait(false);

        if (File.Exists(target) && !File.Exists(set.SnapshotPathOf(target)))
            AtomicFile.Rename(target, set.SnapshotPathOf(target));
        AtomicFile.Rename(backupPath, target, overwrite: true);
        return new(true, targetPath, null, null, Changed: true, Sha256: backupSha);
    }

    private sealed record Outcome(FileDeploymentResult[] Results, string? Error, bool Canceled);

    /// <summary>
    /// Runs <paramref name="body"/> as one transaction. On failure (including cancellation) every
    /// tracked path is rolled back with no cancellation; a failed rollback keeps its copies (and its
    /// journal entry, so the next start retries) and says so in the error.
    /// </summary>
    private async Task<Outcome> RunAsync(string description, Func<OwnedFileSet, Task<FileDeploymentResult[]>> body)
    {
        DeploymentOperation? operation = null;
        if (_journal is not null)
        {
            try
            {
                operation = await _journal.BeginAsync(description).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without a write-ahead entry a crash could not be undone: refuse before changing anything.
                return new([], $"The deployment journal could not be written ({ex.Message}); nothing was changed.", false);
            }
        }
        var set = operation?.Files ?? new OwnedFileSet();
        try
        {
            var results = await body(set).ConfigureAwait(false);
            if (operation is not null) await operation.CommitAsync().ConfigureAwait(false);
            else set.Release();
            return new(results, null, false);
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            var rollback = operation is not null
                ? await operation.RollbackAsync().ConfigureAwait(false)
                : await set.RollbackAsync().ConfigureAwait(false);
            var message = rollback.Success
                ? ex.Message
                : $"{ex.Message} Rollback was incomplete ({string.Join("; ", rollback.Failures)}); the pre-change copies (*{OwnedFileSet.SnapshotMarker}{set.Id}) were kept next to the affected files.";
            return new([], message, ex is OperationCanceledException);
        }
    }
}
