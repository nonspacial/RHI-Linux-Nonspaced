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
    /// </summary>
    public async Task<OwnedFileSetRollback> RollbackAsync()
    {
        var failures = new List<string>();
        foreach (var state in _files.Values.Reverse())
        {
            var error = await RollbackOneAsync(state).ConfigureAwait(false);
            if (error is not null) failures.Add(error);
        }
        return new OwnedFileSetRollback(failures);
    }

    private async Task<string?> RollbackOneAsync(OwnedFileState state)
    {
        var target = state.Path;
        var backup = target + BackupExtension;
        var candidates = new[] { target, backup, SnapshotPathOf(target), SnapshotPathOf(backup) };
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
            // 1. Stage a verified copy of the pre-operation content for every path that differs.
            var wanted = new List<(string Path, string Sha256)>();
            if (state.Existed && state.PreSha256 is { } preTarget) wanted.Add((target, preTarget));
            if (state.BackupExisted && state.BackupPreSha256 is { } preBackup) wanted.Add((backup, preBackup));
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
                    return $"{path}: no surviving copy with the pre-operation content ({sha}) was found; every copy was kept.";
                staged.Add((path, await AtomicFile.StageCopyAsync(source, path, sha, null, CancellationToken.None).ConfigureAwait(false)));
            }
            if (state.BackupExisted && state.BackupPreSha256 is null && !File.Exists(backup))
                return $"{backup}: the backup disappeared although this operation never changed it; every copy was kept.";

            // 2. Place them. 3. Remove what the operation created. 4. Drop the now-redundant copies.
            foreach (var (path, file) in staged) AtomicFile.MoveIntoPlace(file.Path, path);
            staged.Clear();
            if (!state.Existed) AtomicFile.Delete(target);
            if (!state.BackupExisted) AtomicFile.Delete(backup);
            FileCleanup.TryDeleteAll([SnapshotPathOf(target), SnapshotPathOf(backup), TempPathOf(target), TempPathOf(backup)]);
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

    private Task PersistAsync(CancellationToken cancellationToken)
        => _persist is null ? Task.CompletedTask : _persist(cancellationToken);
}

public sealed record OwnedFileSetRollback(IReadOnlyList<string> Failures)
{
    public bool Success => Failures.Count == 0;
}
