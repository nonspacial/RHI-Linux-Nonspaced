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
    CompensationResult Compensate(string data);
}

public enum CompensationStatus
{
    /// <summary>Undone, or there was nothing to undo.</summary>
    Done,

    /// <summary>Someone changed the setting after the crash; their value was left in place.</summary>
    KeptLaterChange,

    /// <summary>Cannot be undone safely right now (e.g. the Wine prefix is in use); retried at the next start.</summary>
    Deferred
}

public sealed record CompensationResult(CompensationStatus Status, string? Note = null)
{
    public static readonly CompensationResult Done = new(CompensationStatus.Done);
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
    private const string UnreadableExtension = ".unreadable";
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Dictionary<string, IDeploymentCompensator> _compensators;

    public DeploymentJournal(string directory, IEnumerable<IDeploymentCompensator>? compensators = null)
    {
        Directory = directory;
        _compensators = (compensators ?? []).ToDictionary(c => c.Kind, StringComparer.Ordinal);
    }

    public string Directory { get; }

    /// <summary>Test seam: every operation of this journal "crashes" when it reaches its commit (see <see cref="SimulatedCrashException"/>).</summary>
    internal bool CrashAtCommitForTesting { get; set; }

    /// <summary>Test seam: runs for each listed entry before recovery takes its lock (another process may act there).</summary>
    internal Action<string>? BeforeEntryLockForTesting { get; set; }

    /// <summary>Test seam: observes each operation as it starts.</summary>
    internal Action<DeploymentOperation>? OperationStartedForTesting { get; set; }

    /// <summary>Starts an operation: takes its lock and writes an empty pending entry.</summary>
    /// <param name="commitProbe">Opaque marker handed to the recovery's <c>isCommitted</c> check (e.g. the record that commits it).</param>
    public async Task<DeploymentOperation> BeginAsync(string description, string? commitProbe = null, CancellationToken cancellationToken = default)
    {
        AtomicFile.CreateDirectoryDurable(Directory);
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
        OperationStartedForTesting?.Invoke(operation);
        return operation;
    }

    /// <summary>
    /// Ids of every entry still on disk (running, kept after a failed or deferred rollback, or kept
    /// as unreadable evidence); their snapshots must survive sweeps.
    /// </summary>
    public IReadOnlySet<string> ReferencedOperationIds()
    {
        if (!System.IO.Directory.Exists(Directory)) return new HashSet<string>();
        return System.IO.Directory.EnumerateFiles(Directory, "*" + EntryExtension)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Concat(System.IO.Directory.EnumerateFiles(Directory, "*" + EntryExtension + UnreadableExtension)
                .Select(path => Path.GetFileName(path)[..^(EntryExtension.Length + UnreadableExtension.Length)]))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether operation <paramref name="id"/> still has an entry (live or kept), read from disk now:
    /// a sweep asks per file, so an operation that started after the sweep began is still seen.
    /// </summary>
    public bool IsReferenced(string id)
        => File.Exists(EntryPath(id)) || File.Exists(EntryPath(id) + UnreadableExtension) || File.Exists(LockPath(id));

    /// <summary>
    /// Rolls back (or, when committed, cleans up) every entry left by a process that died, in
    /// <see cref="RollbackMode.CrashRecovery"/>: files changed by someone else since are kept and RHI's
    /// copies set aside (final). Entries whose lock is held by a running operation are skipped. A
    /// failed rollback keeps its entry and copies so the next start tries again; a deferred step keeps
    /// only itself.
    /// </summary>
    public async Task<JournalRecoveryReport> RecoverAsync(Func<JournalEntryInfo, bool>? isCommitted = null)
    {
        var results = new List<JournalRecoveryEntry>();
        if (!System.IO.Directory.Exists(Directory)) return new(results);
        foreach (var entryPath in System.IO.Directory.EnumerateFiles(Directory, "*" + EntryExtension).Order(StringComparer.Ordinal).ToArray())
        {
            var id = Path.GetFileNameWithoutExtension(entryPath);
            BeforeEntryLockForTesting?.Invoke(id);
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
                    // Another process may have recovered it between the listing and the lock.
                    if (File.Exists(entryPath))
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
            var kept = entryPath + UnreadableExtension;
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

        var outcome = await RollbackAsync(set, document, RollbackMode.CrashRecovery).ConfigureAwait(false);
        if (outcome.Failures.Count > 0)
            return new(id, document.Description, JournalRecoveryOutcome.RollbackFailed, outcome.Failures, paths, outcome.Kept);
        if (outcome.Deferred.Count > 0)
            return new(id, document.Description, JournalRecoveryOutcome.Deferred, outcome.Deferred, paths, outcome.Kept);
        Delete(id);
        return new(id, document.Description,
            outcome.Kept.Count > 0 ? JournalRecoveryOutcome.KeptLaterChanges : JournalRecoveryOutcome.RolledBack,
            [], paths, outcome.Kept);
    }

    internal sealed record RollbackOutcome(IReadOnlyList<string> Failures, IReadOnlyList<string> Kept, IReadOnlyList<string> Deferred);

    /// <summary>
    /// Rolls back the files, then runs the compensations in reverse. When the files are back but a
    /// compensation is deferred, the entry is rewritten to hold only the steps still to do, so the
    /// next start neither redoes the file rollback nor mistakes a later edit for one to protect.
    /// </summary>
    internal async Task<RollbackOutcome> RollbackAsync(OwnedFileSet files, JournalDocument document, RollbackMode mode)
    {
        var fileRollback = await files.RollbackAsync(mode).ConfigureAwait(false);
        var failures = new List<string>(fileRollback.Failures);
        var kept = new List<string>(fileRollback.Kept);
        var deferred = new List<string>();
        var remaining = new List<JournalCompensation>();
        foreach (var compensation in Enumerable.Reverse(document.Compensations))
        {
            if (!_compensators.TryGetValue(compensation.Kind, out var compensator))
            {
                failures.Add($"No handler is registered for the '{compensation.Kind}' step; it was left for the next start.");
                remaining.Add(compensation);
                continue;
            }
            try
            {
                var result = compensator.Compensate(compensation.Data);
                switch (result.Status)
                {
                    case CompensationStatus.KeptLaterChange:
                        kept.Add(result.Note ?? $"'{compensation.Kind}' was changed after the interruption and left as it is.");
                        break;
                    case CompensationStatus.Deferred:
                        deferred.Add(result.Note ?? $"Undoing '{compensation.Kind}' was deferred to the next start.");
                        remaining.Add(compensation);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                failures.Add($"Undoing '{compensation.Kind}' failed: {ex.Message}");
                remaining.Add(compensation);
            }
        }

        if (failures.Count == 0 && deferred.Count > 0)
        {
            remaining.Reverse();
            document.Files = [];
            document.Compensations = remaining;
            try
            {
                await WriteAsync(document, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The old entry stays; redoing the (now idempotent) file rollback next time is harmless.
                deferred.Add($"The journal entry could not be narrowed to the deferred steps ({ex.Message}).");
            }
        }
        return new(failures, kept, deferred);
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
    private readonly DateTime _createdUtc = DateTime.UtcNow;
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
        if (_journal.CrashAtCommitForTesting)
        {
            AbandonAsIfCrashed();
            throw new SimulatedCrashException();
        }
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
        // The lock file only matters while the entry exists; snapshots that could not be removed are swept later.
        ReleaseLock(deleteLockFile: !File.Exists(_journal.EntryPath(Id)));
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
        var outcome = await _journal.RollbackAsync(Files, Document(), RollbackMode.InProcess).ConfigureAwait(false);
        // A deferred step is not done: the entry (narrowed to it) stays for the next start.
        var failures = outcome.Failures.Concat(outcome.Deferred).ToList();
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
        return new(failures, outcome.Kept);
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

    /// <summary>Declares the content now at a tracked <paramref name="path"/> as written by this operation (call right after an in-place edit).</summary>
    public Task RecordWrittenContentAsync(string path, CancellationToken cancellationToken = default)
        => Files.RecordWrittenContentAsync(path, cancellationToken);

    /// <summary>Declares (and journals) content about to be placed at a tracked path, before placing it.</summary>
    public Task RecordWriteAsync(string path, string sha256, CancellationToken cancellationToken = default)
        => Files.RecordWriteAsync(path, sha256, cancellationToken);

    internal Task PersistAsync(CancellationToken cancellationToken)
        => _journal.WriteAsync(Document(), cancellationToken);

    private JournalDocument Document() => new()
    {
        Format = 1,
        Id = Id,
        Description = Description,
        CommitProbe = CommitProbe,
        CreatedUtc = _createdUtc,
        Committed = _committed,
        Files = Files.Files.ToList(),
        Compensations = _compensations.ToList()
    };

    private void ReleaseLock(bool deleteLockFile)
    {
        _lock?.Dispose();
        _lock = null;
        if (deleteLockFile) FileCleanup.TryDelete(_journal.LockPath(Id));
    }
}

/// <summary>Test-only: thrown where a fault-injection test makes the process "die"; no rollback may run for it.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException() : base("Simulated crash (test fault injection).") { }
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
    Unreadable,

    /// <summary>Rolled back, except files (or settings) changed after the crash, which were kept; final.</summary>
    KeptLaterChanges,

    /// <summary>Files rolled back; a step that cannot run safely now (Wine prefix in use) waits for the next start.</summary>
    Deferred
}

public sealed record JournalRecoveryEntry(
    string Id,
    string Description,
    JournalRecoveryOutcome Outcome,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string>? KeptChanges = null)
{
    /// <summary>What was left as someone else changed it after the crash, and where RHI's copies went.</summary>
    public IReadOnlyList<string> Kept => KeptChanges ?? [];
}

public sealed record JournalRecoveryReport(IReadOnlyList<JournalRecoveryEntry> Entries)
{
    public int RolledBack => Entries.Count(entry => entry.Outcome == JournalRecoveryOutcome.RolledBack);
    public int KeptLaterChanges => Entries.Count(entry => entry.Outcome == JournalRecoveryOutcome.KeptLaterChanges);
    public int Deferred => Entries.Count(entry => entry.Outcome == JournalRecoveryOutcome.Deferred);
    public int Failed => Entries.Count(entry => entry.Outcome is JournalRecoveryOutcome.RollbackFailed or JournalRecoveryOutcome.Unreadable);
}
