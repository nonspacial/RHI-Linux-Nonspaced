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
/// Reads an operation's commit point again after a failure (#204): the failure may have come from the commit write itself
/// after it landed (a directory fsync that failed after the rename). The answer must come from a FRESH read of what holds
/// the commit point (never a cache), with the same rule start-up recovery uses.
/// </summary>
public interface IDeploymentCommitCheck
{
    /// <summary>
    /// Taken when an operation begins and handed back to <see cref="Decide"/>: tells an unreadable commit store that no
    /// write has landed in since (none of the operation can be there: roll back) from one written to since (undecided).
    /// Null for an entry an earlier session left (only a readable commit store decides it).
    /// </summary>
    string? Mark();

    CommitDecision Decide(JournalEntryInfo entry, string? mark);

    /// <summary>
    /// Whether an undecided operation with commit probe <paramref name="waitingProbe"/> must be settled before a new
    /// operation with <paramref name="newProbe"/> may begin (they change the same game).
    /// </summary>
    bool HoldsUp(string? waitingProbe, string? newProbe);
}

/// <summary>
/// An operation was refused before it changed anything: an earlier operation on the same game is still undecided (its
/// commit point could not be read after it failed; <see cref="DeploymentJournal.EarlierChangeUndecidedMessage"/>), or its
/// undo, run before this operation, failed (<see cref="DeploymentJournal.EarlierUndoFailedMessage"/>, w8 follow-up C8).
/// </summary>
public sealed class DeploymentDecisionPendingException(string message) : IOException(message)
{
    public DeploymentDecisionPendingException() : this(DeploymentJournal.EarlierChangeUndecidedMessage)
    {
    }
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

    /// <param name="commitCheck">
    /// Reads a failed operation's commit point again (#204); without one, a failure always rolls back (as before).
    /// </param>
    public DeploymentJournal(string directory, IEnumerable<IDeploymentCompensator>? compensators = null, IDeploymentCommitCheck? commitCheck = null)
    {
        Directory = directory;
        _compensators = (compensators ?? []).ToDictionary(c => c.Kind, StringComparer.Ordinal);
        CommitCheck = commitCheck;
    }

    public string Directory { get; }

    /// <summary>Asked by <see cref="DeploymentOperation.SettleAfterFailureAsync"/>; null = every failure rolls back.</summary>
    public IDeploymentCommitCheck? CommitCheck { get; }

    /// <summary>Test seam: every operation of this journal "crashes" when it reaches its commit (see <see cref="SimulatedCrashException"/>).</summary>
    internal bool CrashAtCommitForTesting { get; set; }

    /// <summary>Test seam: runs for each listed entry before recovery takes its lock (another process may act there).</summary>
    internal Action<string>? BeforeEntryLockForTesting { get; set; }

    /// <summary>Test seam: observes each operation as it starts.</summary>
    internal Action<DeploymentOperation>? OperationStartedForTesting { get; set; }

    /// <summary>Starts an operation: takes its lock and writes an empty pending entry.</summary>
    /// <param name="commitProbe">Opaque marker handed to the recovery's <c>isCommitted</c> check (e.g. the record that commits it).</param>
    /// <exception cref="DeploymentDecisionPendingException">An earlier operation on the same game is still undecided.</exception>
    public async Task<DeploymentOperation> BeginAsync(string description, string? commitProbe = null, CancellationToken cancellationToken = default)
    {
        if (await SettleWaitingAsync(commitProbe).ConfigureAwait(false) is { } heldUp)
            throw new DeploymentDecisionPendingException(heldUp);
        AtomicFile.CreateDirectoryDurable(Directory);
        var id = Guid.NewGuid().ToString("N");
        var lockStream = new FileStream(LockPath(id), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var operation = new DeploymentOperation(this, id, description, commitProbe, lockStream);
        try
        {
            // Before the first write of the operation (the entry itself is not part of the commit store).
            operation.CommitMark = CommitCheck?.Mark();
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

    // Review w8 finding 2: operations of this session left undecided (and entries a deferred start-up recovery kept), by id,
    // with the commit check's mark taken when they began (null: unknown, e.g. a previous session's).
    private readonly Dictionary<string, string?> _waiting = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _settling = new(1, 1);

    /// <summary>Operation <paramref name="id"/> ended undecided: its entry waits, settled before its game's next operation.</summary>
    internal void Wait(string id, string? mark)
    {
        lock (_waiting) _waiting[id] = mark;
    }

    /// <summary>
    /// Start-up recovery could not run (its commit store could not be read): every entry on disk waits, as an undecided
    /// operation of this session does, so it is settled before its game's next operation once the store reads again.
    /// No-op without a <see cref="CommitCheck"/>.
    /// </summary>
    public void WaitOnKeptEntries()
    {
        if (CommitCheck is null || !System.IO.Directory.Exists(Directory)) return;
        foreach (var entry in System.IO.Directory.EnumerateFiles(Directory, "*" + EntryExtension))
        {
            var id = Path.GetFileNameWithoutExtension(entry);
            lock (_waiting) _waiting.TryAdd(id, null);
        }
    }

    /// <summary>Ids of the entries waiting to be settled (tests).</summary>
    internal IReadOnlyCollection<string> WaitingForTesting
    {
        get { lock (_waiting) return _waiting.Keys.ToArray(); }
    }

    /// <summary>
    /// Before a new operation (review w8 finding 2): settles every waiting entry as start-up recovery would — from the
    /// commit store as it reads now; committed → copies and entry dropped, not committed → rolled back in crash-recovery
    /// mode (a file changed since keeps its change). One that still cannot be decided (or whose rollback failed) keeps
    /// waiting; returns the refusal when such an entry holds up an operation with <paramref name="newProbe"/> (same game):
    /// <see cref="EarlierChangeUndecidedMessage"/> while one is still undecided, else <see cref="EarlierUndoFailedMessage"/>
    /// (w8 follow-up C8: the ledger reads, the undo failed). Null: nothing holds it up.
    /// </summary>
    private async Task<string?> SettleWaitingAsync(string? newProbe)
    {
        if (CommitCheck is not { } check) return null;
        KeyValuePair<string, string?>[] waiting;
        lock (_waiting)
        {
            if (_waiting.Count == 0) return null;
        }
        await _settling.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_waiting) waiting = _waiting.ToArray();
            string? heldUp = null;
            foreach (var (id, mark) in waiting)
            {
                var (settled, probe, undoFailed) = await SettleWaitingEntryAsync(id, mark, check).ConfigureAwait(false);
                if (settled)
                {
                    lock (_waiting) _waiting.Remove(id);
                }
                else if (check.HoldsUp(probe, newProbe))
                {
                    if (!undoFailed) heldUp = EarlierChangeUndecidedMessage;
                    else heldUp ??= EarlierUndoFailedMessage;
                }
            }
            return heldUp;
        }
        finally
        {
            _settling.Release();
        }
    }

    // UndoFailed: the entry was decided (not committed) but its rollback failed; else it is still undecided or locked.
    private async Task<(bool Settled, string? Probe, bool UndoFailed)> SettleWaitingEntryAsync(string id, string? mark, IDeploymentCommitCheck check)
    {
        var entryPath = EntryPath(id);
        if (!File.Exists(entryPath)) return (true, null, false); // recovered elsewhere (another instance's start)
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(LockPath(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ProbeOf(entryPath), false); // another process works on it now
        }
        try
        {
            await using (lockStream.ConfigureAwait(false))
            {
                if (!File.Exists(entryPath)) return (true, null, false);
                var (outcome, probe) = await RecoverEntryAsync(id, entryPath, info =>
                {
                    try
                    {
                        return check.Decide(info, mark);
                    }
                    catch (Exception ex)
                    {
                        RHI.Core.Services.AppLog.Warn("deployment-journal", $"The commit point of {info.Description} could not be read again", ex);
                        return CommitDecision.Undecided;
                    }
                }).ConfigureAwait(false);
                if (outcome is null) return (false, probe, false);
                RHI.Core.Services.AppLog.Warn("deployment-journal",
                    $"Earlier undecided operation settled before the next one: {outcome.Outcome}: {outcome.Description} [{id}]"
                    + (outcome.Problems.Count > 0 ? " " + string.Join("; ", outcome.Problems) : ""));
                // A rollback that failed keeps its entry for the next start, and keeps holding up its game meanwhile.
                var failed = outcome.Outcome == JournalRecoveryOutcome.RollbackFailed;
                return (!failed, probe, failed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("deployment-journal", $"An earlier undecided operation could not be settled: {id}", ex);
            return (false, ProbeOf(entryPath), false);
        }
        finally
        {
            if (!File.Exists(entryPath)) FileCleanup.TryDelete(LockPath(id));
        }
    }

    /// <summary>The commit probe of the entry at <paramref name="entryPath"/>, or null when it cannot be read.</summary>
    private static string? ProbeOf(string entryPath)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalDocument>(File.ReadAllBytes(entryPath), JsonOptions)?.CommitProbe;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
                        results.Add((await RecoverEntryAsync(id, entryPath,
                            info => isCommitted?.Invoke(info) ?? false ? CommitDecision.Committed : CommitDecision.NotCommitted).ConfigureAwait(false)).Entry!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    results.Add(new(id, "", JournalRecoveryOutcome.RollbackFailed, [$"Journal entry recovery failed: {id}: {ex.Message}"], []));
                }
            }
            if (!File.Exists(entryPath)) FileCleanup.TryDelete(LockPath(id));
        }
        return new(results);
    }

    /// <summary>
    /// Recovers one entry (its lock held): unreadable → kept as evidence; committed → cleaned; not committed → rolled back
    /// in crash-recovery mode. <paramref name="decide"/> answering <see cref="CommitDecision.Undecided"/> leaves the entry as
    /// it is and returns no entry (with the entry's probe).
    /// </summary>
    private async Task<(JournalRecoveryEntry? Entry, string? Probe)> RecoverEntryAsync(string id, string entryPath, Func<JournalEntryInfo, CommitDecision> decide)
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
            return (new(id, "", JournalRecoveryOutcome.Unreadable, [$"Unreadable journal entry kept at {kept}: {ex.Message}"], []), null);
        }

        var set = new OwnedFileSet(id, document.Files);
        var paths = document.Files.Select(file => file.Path).ToArray();
        var info = new JournalEntryInfo(id, document.Description, document.CommitProbe, document.Committed, paths);
        var decision = document.Committed ? CommitDecision.Committed : decide(info);
        if (decision == CommitDecision.Undecided) return (null, document.CommitProbe);
        if (decision == CommitDecision.Committed)
        {
            set.Release();
            Delete(id);
            return (new(id, document.Description, JournalRecoveryOutcome.AlreadyCommitted, [], paths), document.CommitProbe);
        }

        var outcome = await RollbackAsync(set, document, RollbackMode.CrashRecovery).ConfigureAwait(false);
        if (outcome.Failures.Count > 0)
            return (new(id, document.Description, JournalRecoveryOutcome.RollbackFailed, outcome.Failures, paths, outcome.Kept), document.CommitProbe);
        if (outcome.Deferred.Count > 0)
            return (new(id, document.Description, JournalRecoveryOutcome.Deferred, outcome.Deferred, paths, outcome.Kept), document.CommitProbe);
        Delete(id);
        return (new(id, document.Description,
            outcome.Kept.Count > 0 ? JournalRecoveryOutcome.KeptLaterChanges : JournalRecoveryOutcome.RolledBack,
            [], paths, outcome.Kept), document.CommitProbe);
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
                failures.Add($"No handler is registered for this step; it was left for the next start: {compensation.Kind}");
                remaining.Add(compensation);
                continue;
            }
            try
            {
                var result = compensator.Compensate(compensation.Data);
                switch (result.Status)
                {
                    case CompensationStatus.KeptLaterChange:
                        kept.Add(result.Note ?? $"Changed after the interruption and left as it is: {compensation.Kind}");
                        break;
                    case CompensationStatus.Deferred:
                        deferred.Add(result.Note ?? $"Undo deferred to the next start: {compensation.Kind}");
                        remaining.Add(compensation);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                failures.Add($"Could not undo this step: {compensation.Kind} ({ex.Message})");
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

    /// <summary>
    /// Fixed text of the refusal of an operation on a game whose earlier change is still undecided (PT-BR pair): the
    /// deployment ledger could not be read after that change failed, so RHI cannot yet tell whether to finish or undo it.
    /// </summary>
    public const string EarlierChangeUndecidedMessage =
        "RHI has not yet finished or undone an earlier change to this game, because the deployment ledger cannot be read. Nothing was changed. Repair the ledger or restart RHI, then try again.";

    /// <summary>
    /// w8 follow-up C8: fixed text of the refusal while the undo of an earlier change to this game failed (PT-BR pair): the
    /// ledger reads, so it is not the ledger the user has to look at, but the game's files.
    /// </summary>
    public const string EarlierUndoFailedMessage =
        "RHI could not finish undoing an earlier change to this game, so it makes no new change here yet. Nothing was changed. Check that RHI can write to the game's folders and files (rhi.log names the file that failed), then try again: RHI tries the undo again first.";

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

    /// <summary>
    /// Tracks <paramref name="path"/> with a verified snapshot, journaled before returning. Tracking means the file is
    /// about to change, so it ends the operation's cancellable phase (<see cref="OperationCancellation.BeginWrites"/>).
    /// </summary>
    public Task TrackFileAsync(string path, CancellationToken cancellationToken = default, bool includeBackup = true)
        => Files.TrackWithSnapshotAsync(RHI.Core.Services.XboxWriteGate.Checked(path), OperationCancellation.BeginWrites(cancellationToken), includeBackup); // w3/scan hook: Xbox package files (Refs #90)

    /// <summary>Journals how to undo a side effect; call before making it. Ends the cancellable phase.</summary>
    public async Task AddCompensationAsync(string kind, string data, CancellationToken cancellationToken = default)
    {
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
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
        return await FinishCommittedAsync().ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> FinishCommittedAsync()
    {
        _completed = true;
        _committed = true;
        _settled = CommitDecision.Committed;
        var problems = new List<string>();
        try
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            problems.AddRange(Files.Release().Select(path => $"Could not remove {path}"));
            _journal.Delete(Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The journal entry could not be finalised; it is cleaned on the next start: {Id} ({ex.Message})");
        }
        // The lock file only matters while the entry exists; snapshots that could not be removed are swept later.
        ReleaseLock(deleteLockFile: !File.Exists(_journal.EntryPath(Id)));
        return problems;
    }

    /// <summary>
    /// Rolls back every tracked file and compensation. On success the entry is removed; on failure it
    /// is kept (and the lock released) so the next start retries, and the failures are returned.
    /// <para>#204: first reads the commit point again (<see cref="SettleAfterFailureAsync"/>): a commit write that landed
    /// and then reported an error is finished as committed (nothing undone), and one that cannot be told is left for
    /// the next start; the returned <see cref="OwnedFileSetRollback.Decision"/> says which.</para>
    /// </summary>
    public async Task<OwnedFileSetRollback> RollbackAsync()
    {
        if (_completed) return new([]) { Decision = _settled };
        switch (await SettleAfterFailureAsync().ConfigureAwait(false))
        {
            case CommitDecision.Committed:
                return new([]) { Decision = CommitDecision.Committed };
            case CommitDecision.Undecided:
                return new([UndecidedNote]) { Decision = CommitDecision.Undecided };
        }
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
                failures = [$"Rolled back, but the journal entry could not be removed: {Id} ({ex.Message})"];
            }
        }
        ReleaseLock(deleteLockFile: failures.Count == 0);
        return new(failures, outcome.Kept);
    }

    /// <summary>
    /// After a failure (#204), before anything is undone: reads the commit point again through the journal's
    /// <see cref="DeploymentJournal.CommitCheck"/> (a fresh read, the rule start-up recovery uses).
    /// <see cref="CommitDecision.Committed"/> — the commit write landed and then threw — finishes the operation as committed
    /// (snapshots and entry dropped; nothing is rolled back). <see cref="CommitDecision.Undecided"/> — the commit store
    /// cannot be read and a write of it landed since the operation began — keeps the entry and every copy and releases the
    /// lock; the entry waits: settled before the same game's next operation of this session (refused while it still
    /// cannot be decided), or at the next start. <see cref="CommitDecision.NotCommitted"/> changes nothing: roll back. Without a check, always
    /// NotCommitted. Never throws.
    /// </summary>
    public async Task<CommitDecision> SettleAfterFailureAsync()
    {
        if (_completed) return _settled;
        CommitDecision decision;
        try
        {
            decision = _journal.CommitCheck?.Decide(Info(), CommitMark) ?? CommitDecision.NotCommitted;
        }
        catch (Exception ex)
        {
            // A check that cannot answer is an unreadable commit point: keep everything for the next start.
            RHI.Core.Services.AppLog.Warn("deployment-journal", $"The commit point of {Description} could not be read again", ex);
            decision = CommitDecision.Undecided;
        }
        switch (decision)
        {
            case CommitDecision.Committed:
                await FinishCommittedAsync().ConfigureAwait(false);
                break;
            case CommitDecision.Undecided:
                _completed = true;
                _settled = CommitDecision.Undecided;
                ReleaseLock(deleteLockFile: false);
                // Review w8 finding 2: settled before this game's next operation in this session (or refuses it).
                _journal.Wait(Id, CommitMark);
                break;
        }
        return decision;
    }

    /// <summary>Fixed text of a failure whose commit point could not be read again: nothing was undone now (PT-BR pair).</summary>
    public const string UndecidedNote =
        "The deployment ledger could not be read after the error, so nothing was undone now; RHI finishes or undoes this change before the game's next change, or at the next start.";

    /// <summary>The value <see cref="IDeploymentCommitCheck.Mark"/> returned when the operation began.</summary>
    internal string? CommitMark { get; set; }

    private CommitDecision _settled;

    private JournalEntryInfo Info() => new(Id, Description, CommitProbe, _committed, Files.Files.Select(file => file.Path).ToArray());

    public async ValueTask DisposeAsync()
    {
        if (!_completed) await RollbackAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam: drops the lock without committing or rolling back, as a crash would. A dead thread let go afterwards
    /// finds its operation <see cref="CommitDecision.Undecided"/>, so its catch undoes nothing in process (the next start
    /// owns the entry).
    /// </summary>
    internal void AbandonAsIfCrashed()
    {
        _completed = true;
        _settled = CommitDecision.Undecided;
        ReleaseLock(deleteLockFile: false);
    }

    /// <summary>Declares the content now at a tracked <paramref name="path"/> as written by this operation (call right after an in-place edit).</summary>
    public Task RecordWrittenContentAsync(string path, CancellationToken cancellationToken = default)
        => Files.RecordWrittenContentAsync(path, OperationCancellation.BeginWrites(cancellationToken));

    /// <summary>Declares (and journals) content about to be placed at a tracked path, before placing it. Ends the cancellable phase.</summary>
    public Task RecordWriteAsync(string path, string sha256, CancellationToken cancellationToken = default)
        => Files.RecordWriteAsync(path, sha256, OperationCancellation.BeginWrites(cancellationToken));

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
