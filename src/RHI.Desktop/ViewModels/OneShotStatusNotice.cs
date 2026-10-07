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
    // Every notice raised and not yet consumed, oldest first: a second notice (a DLSS manifest failure
    // arriving after the scan's problems notice) is added, never put in the first one's place.
    private readonly List<string> _pending = [];
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
    /// with every pending notice it lacks appended (the current line, e.g. an operation's result or an
    /// earlier notice, is never replaced). With nothing running, an earlier notice already on the current
    /// line has been shown on a settled line and is consumed.
    /// </summary>
    public string Raise(string notice, string? currentStatus = null)
    {
        if (!OperationRunning && !string.IsNullOrWhiteSpace(currentStatus))
            _pending.RemoveAll(earlier => earlier != notice && currentStatus.Contains(earlier, StringComparison.Ordinal));
        if (!_pending.Contains(notice)) _pending.Add(notice);
        _generation++;
        return Append(currentStatus ?? "", Missing(currentStatus ?? ""));
    }

    /// <summary>
    /// Returns <paramref name="status"/> with the pending notices it lacks appended, or
    /// <paramref name="status"/> itself (same instance) when nothing needs adding. The notices are
    /// consumed only when no operation is running, i.e. when the line is not a progress line.
    /// </summary>
    public string Decorate(string status)
    {
        var missing = Missing(status);
        if (missing.Count == 0)
            return status;

        if (!OperationRunning)
            _pending.Clear();
        return Append(status, missing);
    }

    private List<string> Missing(string status)
        => _pending.Where(notice => !status.Contains(notice, StringComparison.Ordinal)).ToList();

    private static string Append(string status, IReadOnlyList<string> notices)
    {
        if (notices.Count == 0) return status;
        var joined = string.Join(Separator, notices);
        return string.IsNullOrWhiteSpace(status) ? joined : status + Separator + joined;
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

    // When the last operation ends, the line on screen already carries the notices and is the
    // operation's final line; they are consumed if no new operation has started, and no new
    // notice has been raised, by the time the deferred check runs.
    private void OnOperationStateChanged()
    {
        if (OperationRunning || _pending.Count == 0)
            return;

        var generation = _generation;
        _defer(() =>
        {
            if (!OperationRunning && _generation == generation)
                _pending.Clear();
        });
    }
}
