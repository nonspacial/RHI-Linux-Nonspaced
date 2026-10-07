namespace RHI.Desktop.ViewModels;

/// <summary>
/// A notice raised in the middle of an operation (e.g. a rejected GitHub token) would be
/// replaced by that operation's progress and result lines a moment later. While any operation
/// is running the notice is appended to every status line, so the operation's final result line
/// carries it. It is consumed once that line is left on screen with no operation running
/// (checked after a deferral, so an operation started straight afterwards keeps it), or once it
/// has been appended to a line shown while nothing is running. A notice is always shown appended
/// to the line on screen, never in its place. UI thread only.
/// </summary>
public sealed class OneShotStatusNotice
{
    private const string Separator = " · ";
    private readonly Action<Action> _defer;
    private string? _pending;
    private int _generation;
    private bool _busy;
    private bool _selectionProbeBusy;

    /// <param name="defer">
    /// Runs the settle check after work already queued on the UI thread, so an operation
    /// started straight after another one ends is still seen as running.
    /// </param>
    public OneShotStatusNotice(Action<Action> defer)
    {
        ArgumentNullException.ThrowIfNull(defer);
        _defer = defer;
    }

    /// <summary>True while the view model is busy or a selection probe is running.</summary>
    public bool OperationRunning => _busy || _selectionProbeBusy;

    /// <summary>
    /// Queues <paramref name="notice"/> and returns the line to display now: <paramref name="currentStatus"/>
    /// with the notice appended (the current line, e.g. an operation's result, is never replaced).
    /// </summary>
    public string Raise(string notice, string? currentStatus = null)
    {
        _pending = notice;
        _generation++;
        if (string.IsNullOrWhiteSpace(currentStatus)) return notice;
        return currentStatus.Contains(notice, StringComparison.Ordinal)
            ? currentStatus
            : currentStatus + Separator + notice;
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

        if (!OperationRunning)
            _pending = null;
        return string.IsNullOrWhiteSpace(status) ? notice : status + Separator + notice;
    }

    /// <summary>The view model's <c>IsBusy</c> changed.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        OnOperationStateChanged();
    }

    /// <summary>The view model's <c>IsSelectionProbeBusy</c> changed.</summary>
    public void SetSelectionProbeBusy(bool busy)
    {
        _selectionProbeBusy = busy;
        OnOperationStateChanged();
    }

    // When the last operation ends, the line on screen already carries the notice and is the
    // operation's final line; the notice is consumed if no new operation has started, and no new
    // notice has been raised, by the time the deferred check runs.
    private void OnOperationStateChanged()
    {
        if (OperationRunning || _pending is null)
            return;

        var generation = _generation;
        _defer(() =>
        {
            if (!OperationRunning && _generation == generation)
                _pending = null;
        });
    }
}
