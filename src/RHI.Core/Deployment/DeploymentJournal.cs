using System.Text.Json;

namespace RHI.Core.Deployment;

/// <summary>
/// Undoes one kind of non-file side effect (for example a Wine DLL override) recorded in the
/// journal before it was made. <see cref="Compensate"/> must be idempotent: it runs whether or not
/// the side effect happened before the crash, and may run again if a later step of the same
/// recovery fails.
/// </summary>
public interface IDeploymentCompensator
{
    string Kind { get; }
    void Compensate(string data);
}

/// <summary>
/// Write-ahead journal for deployment operations. An operation's owned files (pre-operation hashes)
/// and compensations are written here, fsynced, before anything in the game or prefix changes;
/// the entry is removed once the operation is committed or rolled back. Each live operation holds
/// an exclusive lock on <c>&lt;id&gt;.lock</c>, so recovery (and a second RHI process) never touches an
/// operation that is still running. On start-up <see cref="RecoverAsync"/> rolls back every entry
/// whose owner died, unless <c>isCommitted</c> says the operation's commit point (its record) was
/// reached, in which case only its leftovers are cleaned.
/// </summary>
public sealed class DeploymentJournal
{
    private const string EntryExtension = ".json";
    private const string LockExtension = ".lock";
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Dictionary<string, IDeploymentCompensator> _compensators;

    public DeploymentJournal(string directory, IEnumerable<IDeploymentCompensator>? compensators = null)
    {
        Directory = directory;
        _compensators = (compensators ?? []).ToDictionary(c => c.Kind, StringComparer.Ordinal);
    }

    public string Directory { get; }

    /// <summary>Starts an operation: takes its lock and writes an empty pending entry.</summary>
    /// <param name="commitProbe">Opaque marker handed to the recovery's <c>isCommitted</c> check (e.g. the record that commits it).</param>
    public async Task<DeploymentOperation> BeginAsync(string description, string? commitProbe = null, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var id = Guid.NewGuid().ToString("N");
        var lockStream = new FileStream(LockPath(id), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var operation = new DeploymentOperation(this, id, description, commitProbe, lockStream);
        try
        {
            await operation.PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await lockStream.DisposeAsync().ConfigureAwait(false);
            FileCleanup.TryDelete(LockPath(id));
            throw;
        }
        return operation;
    }

    /// <summary>Ids of every entry still on disk (running, or kept after a failed rollback); their snapshots must survive sweeps.</summary>
    public IReadOnlySet<string> ReferencedOperationIds()
    {
        if (!System.IO.Directory.Exists(Directory)) return new HashSet<string>();
        return System.IO.Directory.EnumerateFiles(Directory, "*" + EntryExtension)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Rolls back (or, when committed, cleans up) every entry left by a process that died. Entries
    /// whose lock is held by a running operation are skipped. A failed rollback keeps its entry and
    /// copies so the next start tries again.
    /// </summary>
    public async Task<JournalRecoveryReport> RecoverAsync(Func<JournalEntryInfo, bool>? isCommitted = null)
    {
        var results = new List<JournalRecoveryEntry>();
        if (!System.IO.Directory.Exists(Directory)) return new(results);
        foreach (var entryPath in System.IO.Directory.EnumerateFiles(Directory, "*" + EntryExtension).Order(StringComparer.Ordinal).ToArray())
        {
            var id = Path.GetFileNameWithoutExtension(entryPath);
            FileStream? lockStream;
            try
            {
                lockStream = new FileStream(LockPath(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                continue; // a running operation owns it
            }

            await using (lockStream.ConfigureAwait(false))
            {
                try
                {
                    results.Add(await RecoverEntryAsync(id, entryPath, isCommitted).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    results.Add(new(id, "", JournalRecoveryOutcome.RollbackFailed, [$"Recovery of journal entry {id} failed: {ex.Message}"], []));
                }
            }
            if (!File.Exists(entryPath)) FileCleanup.TryDelete(LockPath(id));
        }
        return new(results);
    }

    private async Task<JournalRecoveryEntry> RecoverEntryAsync(string id, string entryPath, Func<JournalEntryInfo, bool>? isCommitted)
    {
        JournalDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<JournalDocument>(await File.ReadAllBytesAsync(entryPath).ConfigureAwait(false), JsonOptions);
            if (document is null || document.Format != FormatVersion || document.Id != id)
                throw new InvalidDataException("unrecognised journal entry");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            // Keep the evidence; never guess at what an unreadable entry owned.
            var kept = entryPath + ".unreadable";
            File.Move(entryPath, kept, overwrite: true);
            return new(id, "", JournalRecoveryOutcome.Unreadable, [$"Unreadable journal entry kept at {kept}: {ex.Message}"], []);
        }

        var set = new OwnedFileSet(id, document.Files);
        var paths = document.Files.Select(file => file.Path).ToArray();
        var info = new JournalEntryInfo(id, document.Description, document.CommitProbe, document.Committed, paths);
        if (document.Committed || (isCommitted?.Invoke(info) ?? false))
        {
            set.Release();
            Delete(id);
            return new(id, document.Description, JournalRecoveryOutcome.AlreadyCommitted, [], paths);
        }

        var failures = (await RollbackAsync(set, document.Compensations).ConfigureAwait(false)).ToList();
        if (failures.Count > 0)
            return new(id, document.Description, JournalRecoveryOutcome.RollbackFailed, failures, paths);
        Delete(id);
        return new(id, document.Description, JournalRecoveryOutcome.RolledBack, [], paths);
    }

    internal async Task<IReadOnlyList<string>> RollbackAsync(OwnedFileSet files, IReadOnlyList<JournalCompensation> compensations)
    {
        var failures = new List<string>((await files.RollbackAsync().ConfigureAwait(false)).Failures);
        foreach (var compensation in compensations.Reverse())
        {
            if (!_compensators.TryGetValue(compensation.Kind, out var compensator))
            {
                failures.Add($"No handler is registered for the '{compensation.Kind}' step; it was left for the next start.");
                continue;
            }
            try
            {
                compensator.Compensate(compensation.Data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                failures.Add($"Undoing '{compensation.Kind}' failed: {ex.Message}");
            }
        }
        return failures;
    }

    internal Task WriteAsync(JournalDocument document, CancellationToken cancellationToken)
        => AtomicFile.WriteAllBytesAsync(EntryPath(document.Id), JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), cancellationToken);

    internal void Delete(string id) => AtomicFile.Delete(EntryPath(id));

    internal string EntryPath(string id) => Path.Combine(Directory, id + EntryExtension);

    internal string LockPath(string id) => Path.Combine(Directory, id + LockExtension);
}

/// <summary>
/// One journaled deployment operation. Track every file before changing it, add a compensation
/// before every non-file side effect, then <see cref="CommitAsync"/> once the operation's record is
/// saved. Disposing an operation that was neither committed nor rolled back rolls it back.
/// </summary>
public sealed class DeploymentOperation : IAsyncDisposable
{
    private readonly DeploymentJournal _journal;
    private readonly List<JournalCompensation> _compensations = [];
    private FileStream? _lock;
    private bool _completed;
    private bool _committed;

    internal DeploymentOperation(DeploymentJournal journal, string id, string description, string? commitProbe, FileStream lockStream)
    {
        _journal = journal;
        Id = id;
        Description = description;
        CommitProbe = commitProbe;
        _lock = lockStream;
        Files = new OwnedFileSet(id, PersistAsync);
    }

    public string Id { get; }
    public string Description { get; }
    public string? CommitProbe { get; }
    public OwnedFileSet Files { get; }

    /// <summary>Tracks <paramref name="path"/> with a verified snapshot, journaled before returning.</summary>
    public Task TrackFileAsync(string path, CancellationToken cancellationToken = default, bool includeBackup = true)
        => Files.TrackWithSnapshotAsync(path, cancellationToken, includeBackup);

    /// <summary>Journals how to undo a side effect; call before making it.</summary>
    public async Task AddCompensationAsync(string kind, string data, CancellationToken cancellationToken = default)
    {
        _compensations.Add(new JournalCompensation { Kind = kind, Data = data });
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the operation committed and removes its snapshots and entry. Never throws: the record the
    /// caller saved is the real commit point, and any leftover is cleaned on the next start.
    /// </summary>
    public async Task<IReadOnlyList<string>> CommitAsync()
    {
        if (_completed) return [];
        _completed = true;
        _committed = true;
        var problems = new List<string>();
        try
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            problems.AddRange(Files.Release().Select(path => $"Could not remove {path}"));
            _journal.Delete(Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Journal entry {Id} could not be finalised ({ex.Message}); it is cleaned on the next start.");
        }
        ReleaseLock(deleteLockFile: problems.Count == 0);
        return problems;
    }

    /// <summary>
    /// Rolls back every tracked file and compensation. On success the entry is removed; on failure it
    /// is kept (and the lock released) so the next start retries, and the failures are returned.
    /// </summary>
    public async Task<OwnedFileSetRollback> RollbackAsync()
    {
        if (_completed) return new([]);
        _completed = true;
        var failures = await _journal.RollbackAsync(Files, _compensations).ConfigureAwait(false);
        if (failures.Count == 0)
        {
            try
            {
                _journal.Delete(Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures = [$"Rolled back, but journal entry {Id} could not be removed ({ex.Message})."];
            }
        }
        ReleaseLock(deleteLockFile: failures.Count == 0);
        return new(failures);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed) await RollbackAsync().ConfigureAwait(false);
    }

    /// <summary>Test seam: drops the lock without committing or rolling back, as a crash would.</summary>
    internal void AbandonAsIfCrashed()
    {
        _completed = true;
        ReleaseLock(deleteLockFile: false);
    }

    internal Task PersistAsync(CancellationToken cancellationToken)
        => _journal.WriteAsync(new JournalDocument
        {
            Format = 1,
            Id = Id,
            Description = Description,
            CommitProbe = CommitProbe,
            CreatedUtc = DateTime.UtcNow,
            Committed = _committed,
            Files = Files.Files.ToList(),
            Compensations = _compensations.ToList()
        }, cancellationToken);

    private void ReleaseLock(bool deleteLockFile)
    {
        _lock?.Dispose();
        _lock = null;
        if (deleteLockFile) FileCleanup.TryDelete(_journal.LockPath(Id));
    }
}

public sealed class JournalCompensation
{
    public string Kind { get; set; } = "";
    public string Data { get; set; } = "";
}

internal sealed class JournalDocument
{
    public int Format { get; set; }
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string? CommitProbe { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool Committed { get; set; }
    public List<OwnedFileState> Files { get; set; } = [];
    public List<JournalCompensation> Compensations { get; set; } = [];
}

public sealed record JournalEntryInfo(string Id, string Description, string? CommitProbe, bool Committed, IReadOnlyList<string> Paths);

public enum JournalRecoveryOutcome
{
    RolledBack,
    AlreadyCommitted,
    RollbackFailed,
    Unreadable
}

public sealed record JournalRecoveryEntry(
    string Id,
    string Description,
    JournalRecoveryOutcome Outcome,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Paths);

public sealed record JournalRecoveryReport(IReadOnlyList<JournalRecoveryEntry> Entries)
{
    public int RolledBack => Entries.Count(entry => entry.Outcome == JournalRecoveryOutcome.RolledBack);
    public int Failed => Entries.Count(entry => entry.Outcome is JournalRecoveryOutcome.RollbackFailed or JournalRecoveryOutcome.Unreadable);
}
