namespace RHI.Desktop.ViewModels;

/// <summary>
/// A notice raised in the middle of an operation (e.g. a rejected GitHub token) would be
/// replaced by that operation's progress and result lines a moment later. While any operation
/// is running the notice is appended to every status line, so the operation's final result line
/// carries it. It is consumed once that line is left on screen with no operation running
/// (checked after a deferral, so an operation started straight afterwards keeps it), or once it
/// has been appended to a line shown while nothing is running. UI thread only.
/// </summary>
public sealed class OneShotStatusNotice
{
    private const string Separator = " · ";
    private readonly Action<Action> _defer;
    private string? _pending;
    private bool _operationRunning;

    /// <param name="defer">
    /// Runs the settle check after work already queued on the UI thread, so an operation
    /// started straight after another one ends is still seen as running.
    /// </param>
    public OneShotStatusNotice(Action<Action> defer)
    {
        ArgumentNullException.ThrowIfNull(defer);
        _defer = defer;
    }

    /// <summary>Queues <paramref name="notice"/> and returns it for immediate display.</summary>
    public string Raise(string notice)
    {
        _pending = notice;
        return notice;
    }

    /// <summary>
    /// Returns <paramref name="status"/> with the pending notice appended, or
    /// <paramref name="status"/> itself (same instance) when nothing needs adding. The notice is
    /// consumed only when no operation is running, i.e. when the line is not a progress line.
    /// </summary>
    public string Decorate(string status)
    {
        var notice = _pending;
        if (notice is null || status.Contains(notice, StringComparison.Ordinal))
            return status;

        if (!_operationRunning)
            _pending = null;
        return string.IsNullOrWhiteSpace(status) ? notice : status + Separator + notice;
    }

    /// <summary>
    /// Reports whether any operation is running. When the last one ends, the line on screen
    /// already carries the notice and is the operation's final line; the notice is consumed if
    /// no new operation has started by the time the deferred check runs.
    /// </summary>
    public void SetOperationRunning(bool running)
    {
        _operationRunning = running;
        if (running || _pending is null)
            return;

        _defer(() =>
        {
            if (!_operationRunning)
                _pending = null;
        });
    }
}
