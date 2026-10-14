namespace RHI.Core.Deployment;

/// <summary>Pre-operation state of one owned path and its <c>.original</c> sidecar.</summary>
public sealed class OwnedFileState
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    /// <summary>SHA-256 of the file before the operation; null when it did not exist.</summary>
    public string? PreSha256 { get; set; }
    public bool BackupExisted { get; set; }
    /// <summary>SHA-256 of the <c>.original</c> before the operation; null until something is about to change it.</summary>
    public string? BackupPreSha256 { get; set; }
    /// <summary>
    /// Hashes of the content the operation itself placed at <see cref="Path"/>, declared through
    /// <see cref="OwnedFileSet.RecordWriteAsync"/> (before the write) or
    /// <see cref="OwnedFileSet.RecordWrittenContentAsync"/> (straight after an in-place edit). In crash
    /// recovery a file holding anything other than these or <see cref="PreSha256"/> was changed by
    /// someone else after the crash and is left as it is (see <see cref="RollbackMode.CrashRecovery"/>).
    /// </summary>
    public List<string> WrittenSha256 { get; set; } = [];
}

/// <summary>
/// The set of files one deployment operation owns, with what each looked like before the operation
/// (content hash of the file and of its <c>.original</c>). Rollback is content-addressed: every path
/// is put back to its recorded hash from whichever surviving copy holds it (the file itself, its
/// <c>.original</c>, or a <c>.rhi-rollback-&lt;id&gt;</c> snapshot), so it is correct whatever step the
/// operation reached and can run again after a crash. Nothing is deleted unless every tracked path
/// was put back first; a failed rollback keeps all copies and says which path could not be restored.
/// </summary>
public sealed class OwnedFileSet
{
    public const string BackupExtension = ".original";
    public const string SnapshotMarker = ".rhi-rollback-";

    private readonly Dictionary<string, OwnedFileState> _files = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task>? _persist;

    public OwnedFileSet(string? id = null, Func<CancellationToken, Task>? persist = null)
    {
        Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
        _persist = persist;
    }

    internal OwnedFileSet(string id, IEnumerable<OwnedFileState> files)
    {
        Id = id;
        foreach (var file in files) _files[file.Path] = file;
    }

    public string Id { get; }

    public IReadOnlyCollection<OwnedFileState> Files => _files.Values;

    public string SnapshotPathOf(string path) => path + SnapshotMarker + Id;

    public string TempPathOf(string path) => AtomicFile.TempPathFor(path, Id);

    public OwnedFileState? Find(string path) => _files.GetValueOrDefault(System.IO.Path.GetFullPath(path));

    /// <summary>
    /// Records the pre-operation state of <paramref name="path"/> (and whether its <c>.original</c>
    /// exists) before the first change; later calls for the same path keep the first state.
    /// </summary>
    /// <param name="knownSha256">Hash the caller already computed for the file as it is now.</param>
    public async Task<OwnedFileState> TrackAsync(string path, CancellationToken cancellationToken, string? knownSha256 = null)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (_files.TryGetValue(full, out var existing)) return existing;
        // A path enters a deployment only through here: an Xbox package file, or a title PartingX is working on, is
        // refused before the operation owns (and so before it changes) anything of it (Refs #90).
        RHI.Core.Services.XboxWriteGate.ThrowIfRefused(full); // w3/scan hook
        var exists = File.Exists(full);
        var state = new OwnedFileState
        {
            Path = full,
            Existed = exists,
            PreSha256 = exists ? knownSha256 ?? await FileHash.Sha256HexAsync(full, cancellationToken).ConfigureAwait(false) : null,
            BackupExisted = File.Exists(full + BackupExtension)
        };
        _files[full] = state;
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Captures the hash of a pre-existing <c>.original</c> before something consumes or rewrites it.
    /// Must be called while the <c>.original</c> is still in its pre-operation state.
    /// </summary>
    public async Task CaptureBackupAsync(string path, CancellationToken cancellationToken, string? knownSha256 = null)
    {
        var state = Find(path) ?? throw new InvalidOperationException($"Track {path} before capturing its backup.");
        if (!state.BackupExisted || state.BackupPreSha256 is not null) return;
        state.BackupPreSha256 = knownSha256 ?? await FileHash.Sha256HexAsync(state.Path + BackupExtension, cancellationToken).ConfigureAwait(false);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Declares (and journals) content about to be placed at a tracked path, before placing it.</summary>
    public async Task RecordWriteAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        var state = Find(path) ?? throw new InvalidOperationException($"Track {path} before writing it.");
        if (state.WrittenSha256.Contains(sha256, StringComparer.OrdinalIgnoreCase)) return;
        state.WrittenSha256.Add(sha256);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Declares the content now at a tracked path as the operation's own, for writers that edit a
    /// file in place and cannot know its hash beforehand (INI editors). Call straight after the
    /// write. A crash between the write and this call leaves content recovery cannot attribute: it
    /// then keeps the file as it is and sets RHI's pre-operation copy aside, losing neither.
    /// </summary>
    public async Task RecordWrittenContentAsync(string path, CancellationToken cancellationToken)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) return; // a removal needs no declaration: recovery restores missing files
        await RecordWriteAsync(full, await FileHash.Sha256HexAsync(full, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="TrackAsync"/> plus a verified copy of the file (and, when
    /// <paramref name="includeBackup"/>, of its <c>.original</c>) for callers that change the file in
    /// ways this set does not control (in-place edits, deletes, other services' writes).
    /// </summary>
    public async Task<OwnedFileState> TrackWithSnapshotAsync(string path, CancellationToken cancellationToken, bool includeBackup = true)
    {
        var alreadyTracked = Find(path) is not null;
        var state = await TrackAsync(path, cancellationToken).ConfigureAwait(false);
        if (alreadyTracked) return state;
        if (state.Existed)
            await AtomicFile.CopyAsync(state.Path, SnapshotPathOf(state.Path), state.PreSha256, cancellationToken).ConfigureAwait(false);
        if (includeBackup && state.BackupExisted)
        {
            var backup = state.Path + BackupExtension;
            await CaptureBackupAsync(state.Path, cancellationToken).ConfigureAwait(false);
            await AtomicFile.CopyAsync(backup, SnapshotPathOf(backup), state.BackupPreSha256, cancellationToken).ConfigureAwait(false);
        }
        return state;
    }

    /// <summary>Every snapshot/temporary path this set can have created (for cleanup and sweeps).</summary>
    public IEnumerable<string> ArtifactPaths()
    {
        foreach (var state in _files.Values)
        {
            var backup = state.Path + BackupExtension;
            yield return SnapshotPathOf(state.Path);
            yield return SnapshotPathOf(backup);
            yield return TempPathOf(state.Path);
            yield return TempPathOf(backup);
        }
    }

    /// <summary>After a successful operation: drops the snapshots and temporaries. Returns paths that could not be removed.</summary>
    public IReadOnlyList<string> Release() => FileCleanup.TryDeleteAll(ArtifactPaths());

    /// <summary>
    /// Puts every tracked path (and its <c>.original</c>) back to its pre-operation content.
    /// Always runs to completion (no cancellation); never deletes a copy before the restore it
    /// could be needed for has succeeded.
    /// <para>
    /// <see cref="RollbackMode.CrashRecovery"/> (a later start undoing a dead process's operation)
    /// also protects whatever happened after the crash: a path whose content is neither its
    /// pre-operation content nor anything the operation declared it wrote (<see cref="RecordWriteAsync"/>)
    /// was changed by someone else (a user edit, a game update). It is left exactly as it is, and the
    /// copy RHI holds of the pre-operation content is moved, not deleted, to
    /// <see cref="RecoveredCopies.FolderName"/> beside it, so neither version is lost. That outcome is
    /// final (reported in <see cref="OwnedFileSetRollback.Kept"/>), not a failure retried at every start.
    /// </para>
    /// </summary>
    public async Task<OwnedFileSetRollback> RollbackAsync(RollbackMode mode = RollbackMode.InProcess, DateTime? nowUtc = null)
    {
        var failures = new List<string>();
        var kept = new List<string>();
        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var state in _files.Values.Reverse())
        {
            var error = await RollbackOneAsync(state, mode, now, kept).ConfigureAwait(false);
            if (error is not null) failures.Add(error);
        }
        return new OwnedFileSetRollback(failures, kept);
    }

    private async Task<string?> RollbackOneAsync(OwnedFileState state, RollbackMode mode, DateTime now, List<string> kept)
    {
        var target = state.Path;
        var backup = target + BackupExtension;
        var targetSnapshot = SnapshotPathOf(target);
        var backupSnapshot = SnapshotPathOf(backup);
        var candidates = new[] { target, backup, targetSnapshot, backupSnapshot };
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        async Task<string?> HashOf(string path)
        {
            if (!hashes.TryGetValue(path, out var hash))
                hashes[path] = hash = await FileHash.Sha256HexOrNullAsync(path, CancellationToken.None).ConfigureAwait(false);
            return hash;
        }

        var staged = new List<(string Target, StagedFile File)>();
        try
        {
            // 0. Crash recovery only: content that is neither the pre-operation state nor a declared
            //    write was put there by someone else after the crash. A missing file is never
            //    "someone else's" content: restoring it loses nothing.
            var strict = mode == RollbackMode.CrashRecovery;
            var currentTarget = await HashOf(target).ConfigureAwait(false);
            var foreignTarget = strict && currentTarget is not null
                                && !(state.Existed && FileHash.HexEquals(currentTarget, state.PreSha256))
                                && !state.WrittenSha256.Any(written => FileHash.HexEquals(currentTarget, written));
            var backupMayHaveChanged = !state.BackupExisted || state.BackupPreSha256 is not null;
            var currentBackup = await HashOf(backup).ConfigureAwait(false);
            var foreignBackup = strict && backupMayHaveChanged && currentBackup is not null
                                && !(state.BackupExisted && FileHash.HexEquals(currentBackup, state.BackupPreSha256))
                                // the operation moves the target's old content to .original
                                && !(state.Existed && FileHash.HexEquals(currentBackup, state.PreSha256));

            // 1. Stage a verified copy of the pre-operation content for every path that differs.
            var wanted = new List<(string Path, string Sha256)>();
            if (state.Existed && state.PreSha256 is { } preTarget && !foreignTarget) wanted.Add((target, preTarget));
            if (state.BackupExisted && state.BackupPreSha256 is { } preBackup && !foreignBackup) wanted.Add((backup, preBackup));

            foreach (var (path, sha) in wanted)
            {
                if (FileHash.HexEquals(await HashOf(path).ConfigureAwait(false), sha)) continue;
                string? source = null;
                foreach (var candidate in candidates)
                {
                    if (candidate == path) continue;
                    if (FileHash.HexEquals(await HashOf(candidate).ConfigureAwait(false), sha)) { source = candidate; break; }
                }
                if (source is null)
                {
                    if (foreignTarget && path == backup)
                    {
                        // The operation placed the old .original at the target, which has changed since.
                        kept.Add($"{backup}: not put back; its content was last placed in {target}, which was changed after the interruption.");
                        continue;
                    }
                    return $"{path}: no surviving copy with the pre-operation content ({sha}) was found; every copy was kept.";
                }
                staged.Add((path, await AtomicFile.StageCopyAsync(source, path, sha, null, CancellationToken.None).ConfigureAwait(false)));
            }
            if (state.BackupExisted && state.BackupPreSha256 is null && !File.Exists(backup))
            {
                if (!strict)
                    return $"{backup}: the backup disappeared although this operation never changed it; every copy was kept.";
                kept.Add($"{backup}: removed by something else after the interruption (this operation never changed it); nothing to put back.");
            }

            // 2. Place them.
            foreach (var (path, file) in staged) AtomicFile.MoveIntoPlace(file.Path, path);
            staged.Clear();

            // 3. Remove what the operation created; set aside, never delete, copies that may be the
            //    only pre-operation version of a file someone else changed since.
            if (!state.Existed && !foreignTarget) AtomicFile.Delete(target);
            if (!state.BackupExisted && !foreignBackup && File.Exists(backup))
            {
                if (foreignTarget) kept.Add(SetAside(backup, target, now));
                else AtomicFile.Delete(backup);
            }
            if (foreignTarget)
            {
                var copy = File.Exists(targetSnapshot) ? SetAside(targetSnapshot, target, now) : null;
                kept.Add($"{target}: changed after the interruption (neither its earlier content nor what RHI wrote); left as it is."
                         + (copy is not null ? $" {copy}" : state.Existed ? "" : " It did not exist before the operation."));
            }
            if (foreignBackup)
            {
                var copy = File.Exists(backupSnapshot) ? SetAside(backupSnapshot, backup, now) : null;
                kept.Add($"{backup}: changed after the interruption; left as it is." + (copy is not null ? $" {copy}" : ""));
            }

            // 4. Drop the now-redundant copies.
            FileCleanup.TryDeleteAll([targetSnapshot, backupSnapshot, TempPathOf(target), TempPathOf(backup)]);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"{target}: rollback failed ({ex.Message}); every copy was kept.";
        }
        finally
        {
            foreach (var (_, file) in staged) FileCleanup.TryDelete(file.Path);
        }
    }

    private static string SetAside(string copy, string of, DateTime now)
    {
        var moved = RecoveredCopies.MoveAside(copy, now, Path.GetFileName(of) + RecoveredCopies.PreOperationSuffix);
        return $"RHI's copy of its earlier content was moved to {moved}.";
    }

    private Task PersistAsync(CancellationToken cancellationToken)
        => _persist is null ? Task.CompletedTask : _persist(cancellationToken);
}

public sealed record OwnedFileSetRollback(IReadOnlyList<string> Failures, IReadOnlyList<string>? KeptChanges = null)
{
    public bool Success => Failures.Count == 0;

    /// <summary>Paths left as someone else changed them after a crash, and where RHI's copy went.</summary>
    public IReadOnlyList<string> Kept => KeptChanges ?? [];
}

public enum RollbackMode
{
    /// <summary>The operation's own process undoes it (failure or cancellation): every change is its own.</summary>
    InProcess,

    /// <summary>A later start undoes a dead process's operation: changes made after the crash are kept.</summary>
    CrashRecovery
}
