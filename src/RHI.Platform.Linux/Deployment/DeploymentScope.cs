using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// One deployment's owned files (#23, #31, #42): the journaled <see cref="DeploymentOperation"/> when the app has a
/// journal, otherwise an in-memory <see cref="OwnedFileSet"/>, behind one API, so a service has a single path for
/// tracking, declaring writes and rolling back. Every file is tracked (with a verified snapshot) before it changes and
/// every placed content is declared; a failure is undone by <see cref="RollbackAsync"/> (content-addressed: each path
/// goes back to its pre-operation bytes from whichever copy survived, and a failed restore keeps every copy and says
/// where), and a crash before the commit point is undone by start-up recovery. Disposing a scope that was neither
/// committed nor rolled back rolls it back.
/// <para>Commit points: installs and updates stamp the operation id into the record they save (<see cref="Stamp"/>);
/// removals stamp the record they take away when they begin (<see cref="BeginRemovalAsync"/>) and commit by removing it
/// (or by saving a narrowed record through <see cref="LinuxDeploymentRecovery.EndRemoval"/>).</para>
/// </summary>
public sealed class DeploymentScope : IAsyncDisposable
{
    private readonly OwnedFileSet? _local;
    private bool _completed;

    private DeploymentScope(DeploymentOperation? operation)
    {
        Operation = operation;
        _local = operation is null ? new OwnedFileSet() : null;
    }

    /// <summary>The journaled operation (null without a journal): pass it to <see cref="WineDllOverrideService"/>.</summary>
    public DeploymentOperation? Operation { get; }

    public OwnedFileSet Files => Operation?.Files ?? _local!;

    /// <summary>
    /// Opens an install or update whose commit point is saving <paramref name="component"/>'s record. A journal that
    /// cannot be written refuses before anything changes.
    /// </summary>
    public static async Task<(DeploymentScope? Scope, string? Refusal)> BeginAsync(
        DeploymentJournal? journal, string description, DetectedGame game, ManagedComponentKind component, CancellationToken cancellationToken)
    {
        var (operation, refusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(journal, description, game, component, cancellationToken).ConfigureAwait(false);
        return refusal is not null ? (null, refusal) : (new DeploymentScope(operation), null);
    }

    /// <summary>
    /// Opens a removal whose commit point is taking <paramref name="component"/>'s record out of the ledger
    /// (<see cref="LinuxDeploymentRecovery.BeginRecordRemovalAsync"/>). Refuses before anything changes when the journal
    /// or the ledger cannot be written.
    /// </summary>
    public static async Task<(DeploymentScope? Scope, string? Refusal)> BeginRemovalAsync(
        DeploymentJournal? journal, LinuxDeploymentRecordStore records, string description, DetectedGame game,
        ManagedComponentKind component, CancellationToken cancellationToken)
    {
        var (operation, refusal) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(journal, records, description, game, component, cancellationToken).ConfigureAwait(false);
        return refusal is not null ? (null, refusal) : (new DeploymentScope(operation), null);
    }

    /// <summary>
    /// w10 follow-up D15: the refusal <see cref="BeginAsync"/> would give for <paramref name="game"/>'s
    /// <paramref name="component"/> now (an earlier change still held), or null; nothing is started. Asked before a
    /// service's own checks of the game's files. A journal that cannot be read or written refuses as BeginAsync does.
    /// </summary>
    public static async Task<string?> HoldRefusalAsync(DeploymentJournal? journal, DetectedGame game, ManagedComponentKind component)
    {
        if (journal is null) return null;
        try
        {
            return await journal.HoldRefusalAsync(LinuxDeploymentRecovery.RecordCommitProbe(game, component)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{LinuxDeploymentRecovery.JournalUnavailablePrefix}{ex.Message}); nothing was changed.";
        }
    }

    /// <summary>A scope with no journal (callers that run inside another service's operation, tests).</summary>
    public static DeploymentScope Unjournaled() => new(null);

    /// <summary>
    /// Records <paramref name="path"/>'s pre-operation state with a verified copy (and of its <c>.original</c>) before it
    /// changes. Ends the cancellable phase (<see cref="OperationCancellation.BeginWrites"/>).
    /// </summary>
    public Task TrackAsync(string path, CancellationToken cancellationToken = default)
        => Operation is not null
            ? Operation.TrackFileAsync(path, cancellationToken)
            : Files.TrackWithSnapshotAsync(path, OperationCancellation.BeginWrites(cancellationToken));

    /// <summary>Tracks every path in order (see <see cref="TrackAsync"/>).</summary>
    public async Task TrackAllAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths) await TrackAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Declares content about to be placed at a tracked path (its SHA-256), before placing it.</summary>
    public Task DeclareWriteAsync(string path, string sha256, CancellationToken cancellationToken = default)
        => Operation is not null
            ? Operation.RecordWriteAsync(path, sha256, cancellationToken)
            : Files.RecordWriteAsync(path, sha256, OperationCancellation.BeginWrites(cancellationToken));

    /// <summary>Declares the content now at a tracked path as this operation's (straight after an in-place edit).</summary>
    public Task DeclareWrittenContentAsync(string path, CancellationToken cancellationToken = default)
        => Operation is not null
            ? Operation.RecordWrittenContentAsync(path, cancellationToken)
            : Files.RecordWrittenContentAsync(path, OperationCancellation.BeginWrites(cancellationToken));

    /// <summary>
    /// Copies <paramref name="source"/> to the tracked-and-declared <paramref name="target"/> (verified, durable,
    /// atomic), replacing what is there unless <paramref name="overwrite"/> is false (then a file that appeared meanwhile
    /// makes it throw and stays as it is). Returns the placed content's SHA-256.
    /// </summary>
    public async Task<string> PlaceCopyAsync(
        string source, string target, CancellationToken cancellationToken = default, string? sourceSha256 = null, bool overwrite = true)
    {
        await TrackAsync(target, cancellationToken).ConfigureAwait(false);
        var sha = sourceSha256 ?? await FileHash.Sha256HexAsync(source, CancellationToken.None).ConfigureAwait(false);
        await DeclareWriteAsync(target, sha, cancellationToken).ConfigureAwait(false);
        return await AtomicFile.CopyAsync(source, target, sha, CancellationToken.None, overwrite).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames a verified copy staged beside <paramref name="target"/> (<see cref="AtomicFile.StageCopyAsync"/>) into
    /// place after tracking the target and declaring the staged content. With <paramref name="overwrite"/> false a file
    /// that appeared meanwhile makes it throw and stays as it is.
    /// </summary>
    public async Task PlaceStagedAsync(StagedFile staged, string target, CancellationToken cancellationToken = default, bool overwrite = true)
    {
        await TrackAsync(target, cancellationToken).ConfigureAwait(false);
        await DeclareWriteAsync(target, staged.Sha256, cancellationToken).ConfigureAwait(false);
        AtomicFile.MoveIntoPlace(staged.Path, target, overwrite);
    }

    /// <summary>
    /// The catch of a failed removal. First reads the commit point again (#204, <see cref="SettleAfterFailureAsync"/>): a
    /// removal whose commit write landed and then reported an error is finished as removed (no record is put back, no
    /// file restored — the ledger already says removed, as start-up recovery would), and one the ledger cannot tell is
    /// left for the next start. Otherwise it puts <paramref name="record"/> back when the ledger no longer has it (with a
    /// journal that cannot read its commit point again: the save that took it away failed after replacing the file),
    /// together with <paramref name="alongside"/> (records the commit point changed in the same write, as they were before
    /// it) — still carrying this removal's stamp (<see cref="LinuxDeploymentRecovery.RemovalOperationIdMetadataKey"/>) —
    /// and only then rolls back. Start-up recovery therefore counts the removal uncommitted until the rollback has
    /// finished: a crash during it, or a rollback left for the next start, puts the files back instead of releasing the
    /// copies the user was told were kept, and the restored files are never left without their record. A rolled-back
    /// removal's stamp is inert (the journal entry is gone), and the next removal stamps the record again. Putting the
    /// record back never throws (logged).
    /// </summary>
    public async Task<OwnedFileSetRollback> RollbackRemovalAsync(
        LinuxDeploymentRecordStore records, DetectedGame game, LinuxDeploymentRecord record, string logArea,
        IReadOnlyList<LinuxDeploymentRecord>? alongside = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(record);
        if (await SettleAfterFailureAsync().ConfigureAwait(false) != CommitDecision.NotCommitted)
            return await RollbackAsync().ConfigureAwait(false);
        try
        {
            if (records.Find(game, record.Component) is null)
            {
                var back = LinuxDeploymentRecordStore.Clone(record);
                if (Operation is not null) back.Metadata[LinuxDeploymentRecovery.RemovalOperationIdMetadataKey] = Operation.Id;
                else LinuxDeploymentRecovery.EndRemoval(back);
                records.Commit([back, .. alongside ?? []]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn(logArea, "Removal rollback could not put the deployment record back", ex);
        }
        return await RollbackAsync().ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="bytes"/> to the tracked-and-declared <paramref name="target"/> (verified, durable, atomic).</summary>
    public async Task<string> PlaceBytesAsync(string target, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await TrackAsync(target, cancellationToken).ConfigureAwait(false);
        var sha = FileHash.Sha256HexOf(bytes);
        await DeclareWriteAsync(target, sha, cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAllBytesAsync(target, bytes, CancellationToken.None).ConfigureAwait(false);
        return sha;
    }

    /// <summary>Deletes the tracked <paramref name="path"/> (tracked first, so a rollback puts it back).</summary>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        await TrackAsync(path, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path)) AtomicFile.Delete(path);
    }

    /// <summary>Puts the operation id into <paramref name="record"/>'s metadata (its commit point); returns the record.</summary>
    public LinuxDeploymentRecord Stamp(LinuxDeploymentRecord record) => LinuxDeploymentRecovery.StampOperation(Operation, record);

    /// <summary>
    /// After the commit point (record saved or removed): drops the snapshots and the journal entry. Never throws;
    /// returns what could not be cleaned (swept later).
    /// </summary>
    public async Task<IReadOnlyList<string>> CommitAsync()
    {
        if (_completed) return [];
        _completed = true;
        _decision = CommitDecision.Committed;
        return Operation is not null ? await Operation.CommitAsync().ConfigureAwait(false) : Files.Release();
    }

    /// <summary>
    /// Puts every tracked file back (and, journaled, undoes every journaled Wine step). Never cancelled. A failure keeps
    /// every copy (and the journal entry, so the next start retries) and is returned.
    /// <para>#204: journaled, the commit point is read again first (<see cref="SettleAfterFailureAsync"/>): when the commit
    /// write landed and then reported an error nothing is undone and the result says
    /// <see cref="OwnedFileSetRollback.Committed"/> (report <see cref="Landed"/>); when the ledger cannot tell, nothing is
    /// undone and the entry waits — settled before the game's next operation, or at the next start
    /// (<see cref="CommitDecision.Undecided"/>). Run any further in-process undo step of
    /// a catch only while <see cref="OwnedFileSetRollback.RolledBack"/>.</para>
    /// </summary>
    public async Task<OwnedFileSetRollback> RollbackAsync()
    {
        if (_completed) return Settled(_decision);
        _completed = true;
        var result = Operation is not null
            ? await Operation.RollbackAsync().ConfigureAwait(false)
            : await Files.RollbackAsync(RollbackMode.InProcess).ConfigureAwait(false);
        _decision = result.Decision;
        return result;
    }

    /// <summary>
    /// After a failure, before a catch undoes anything of its own (#204): reads the operation's commit point again from
    /// the ledger file. <see cref="CommitDecision.Committed"/>: the commit write landed and then reported an error — the
    /// scope is committed now (copies and journal entry dropped); report success (<see cref="Landed"/>) and undo nothing.
    /// <see cref="CommitDecision.Undecided"/>: the ledger cannot be read — the journal entry and every copy stay for the
    /// next start; undo nothing. <see cref="CommitDecision.NotCommitted"/>: roll back as usual. Unjournaled scopes (no
    /// ledger to read) always answer NotCommitted. Never throws.
    /// </summary>
    public async Task<CommitDecision> SettleAfterFailureAsync()
    {
        if (_completed) return _decision;
        if (Operation is null) return CommitDecision.NotCommitted;
        var decision = await Operation.SettleAfterFailureAsync().ConfigureAwait(false);
        if (decision != CommitDecision.NotCommitted)
        {
            _completed = true;
            _decision = decision;
        }
        return decision;
    }

    private CommitDecision _decision;

    private static OwnedFileSetRollback Settled(CommitDecision decision) => decision == CommitDecision.Undecided
        ? new([DeploymentOperation.UndecidedNote]) { Decision = decision }
        : new([]) { Decision = decision };

    /// <summary>The text a failure message appends for a rollback that could not finish (empty on success).</summary>
    public static string RollbackNote(OwnedFileSetRollback rollback)
        => rollback.Decision == CommitDecision.Undecided ? " " + DeploymentOperation.UndecidedNote
            : rollback.Success ? "" : $"{RollbackIncompletePrefix}{string.Join("; ", rollback.Failures)}); every copy was kept.";

    /// <summary>Fixed text before the failures of an incomplete rollback (PT-BR pair exists since w1).</summary>
    public const string RollbackIncompletePrefix = " Rollback was incomplete (";

    /// <summary>
    /// The result of an operation whose commit write landed and then reported an error (#204): it is finished as
    /// committed, so it reports success — the row, Update All and the update history then match the ledger — with a note.
    /// <paramref name="component"/> (a product name) follows the fixed text.
    /// </summary>
    public static ComponentOperationResult Landed(string component, bool removal = false, params string[] changedFiles)
        => ComponentOperationResult.Ok((removal ? LandedRemovalPrefix : LandedChangePrefix) + component, changedFiles) with { Landed = true };

    /// <summary>Fixed text of <see cref="Landed"/> for an install, update or change; the component follows (PT-BR pair).</summary>
    public const string LandedChangePrefix =
        "Done; the deployment ledger reported an error after the change was written, so nothing was undone: ";

    /// <summary>Fixed text of <see cref="Landed"/> for a removal; the component follows (PT-BR pair).</summary>
    public const string LandedRemovalPrefix =
        "Removed; the deployment ledger reported an error after the removal was written, so nothing was undone: ";

    public async ValueTask DisposeAsync()
    {
        if (!_completed) await RollbackAsync().ConfigureAwait(false);
    }
}
