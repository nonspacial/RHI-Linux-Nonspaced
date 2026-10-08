using System.Collections.Concurrent;

namespace RHI.Core.Deployment;

/// <summary>
/// The cancellation of one user operation that may change game files (an install, a removal, one game of Update All).
/// The user's Cancel is honoured only in the operation's preparation phase (pre-checks, downloads, staging, hashing):
/// the moment the operation is about to change the first game file, user INI or Wine override it calls
/// <see cref="BeginWrites"/>, and from then on Cancel is refused, so the operation always runs to completion or to its
/// full rollback, never stops half-way. A batch (Update All, the DLSS batch) gives each unit its own instance linked to
/// the batch's token, so Cancel still stops the batch between units.
/// </summary>
public sealed class OperationCancellation : IDisposable
{
    private static readonly ConcurrentDictionary<CancellationToken, OperationCancellation> Live = new();
    private static readonly AsyncLocal<Action<OperationCancellation>?> WritePhaseObserver = new();

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _linked;
    private bool _writing;
    private bool _cancelAccepted;
    private bool _disposed;

    /// <param name="linkedToken">A token that also asks the operation to stop (the batch's token, a dialog's own token); it is gated the same way.</param>
    public OperationCancellation(CancellationToken linkedToken = default)
    {
        Token = _cts.Token;
        Live[Token] = this;
        if (linkedToken.CanBeCanceled) _linked = linkedToken.Register(() => TryCancel());
    }

    /// <summary>The token handed to the services; cancelled only by a Cancel accepted before the write phase.</summary>
    public CancellationToken Token { get; }

    /// <summary>True once the operation has started changing game files; Cancel is refused from then on.</summary>
    public bool WritesStarted
    {
        get { lock (_gate) return _writing; }
    }

    /// <summary>True when a Cancel was accepted (the operation stopped, or stops, before changing anything).</summary>
    public bool CancelAccepted
    {
        get { lock (_gate) return _cancelAccepted; }
    }

    /// <summary>Raised once, on the thread that enters the write phase, right before the first change is made.</summary>
    public event Action? WritesStarting;

    /// <summary>
    /// Test seam: honour a Cancel even after the write phase began, as a caller's plain token would. Tests use it to
    /// prove that a service stops listening to its token once it calls <see cref="BeginWrites"/>.
    /// </summary>
    internal bool HonourLateCancelForTesting { get; init; }

    /// <summary>
    /// Test seam: called whenever an operation started from the current async flow enters its write phase, batch units
    /// included (their instances are created inside the services). Flows with the async context, so parallel tests
    /// never see each other's operations.
    /// </summary>
    internal static Action<OperationCancellation>? WritePhaseObserverForTesting
    {
        get => WritePhaseObserver.Value;
        set => WritePhaseObserver.Value = value;
    }

    /// <summary>Asks the operation to stop. False (and nothing happens) once it has started changing files.</summary>
    public bool TryCancel()
    {
        lock (_gate)
        {
            if (_disposed || _cancelAccepted) return false;
            if (_writing && !HonourLateCancelForTesting) return false;
            _cancelAccepted = true;
        }
        // Outside the lock: the token's callbacks may run service code inline.
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* disposed meanwhile: the operation is over */ }
        return true;
    }

    /// <summary>
    /// Call right before the first change to a game file, user INI or Wine override. Throws
    /// <see cref="OperationCanceledException"/> when Cancel was already accepted (nothing has been changed yet); otherwise
    /// closes the cancellable phase of <paramref name="cancellationToken"/>'s operation and returns
    /// <see cref="CancellationToken.None"/>, which the caller uses for every later step, rollback included:
    /// <c>cancellationToken = OperationCancellation.BeginWrites(cancellationToken);</c>. A token that does not belong to
    /// an <see cref="OperationCancellation"/> (a caller's plain token) is checked once and then ignored the same way.
    /// </summary>
    public static CancellationToken BeginWrites(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled && Live.TryGetValue(cancellationToken, out var operation))
            operation.EnterWritePhase();
        else
            cancellationToken.ThrowIfCancellationRequested();
        return CancellationToken.None;
    }

    private void EnterWritePhase()
    {
        bool first;
        lock (_gate)
        {
            if (_cancelAccepted) throw new OperationCanceledException(Token);
            first = !_writing;
            _writing = true;
        }
        if (!first) return;
        WritesStarting?.Invoke();
        WritePhaseObserver.Value?.Invoke(this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Live.TryRemove(new KeyValuePair<CancellationToken, OperationCancellation>(Token, this));
        _linked.Dispose();
        _cts.Dispose();
    }
}
