namespace RHI.Desktop.ViewModels;

/// <summary>
/// A notice raised in the middle of an operation (e.g. a rejected GitHub token) would be
/// replaced by that operation's own result line a moment later. The notice is kept pending
/// and appended to the next, different status line exactly once.
/// </summary>
public sealed class OneShotStatusNotice
{
    private const string Separator = " · ";
    private string? _pending;

    /// <summary>Queues <paramref name="notice"/> and returns it for immediate display.</summary>
    public string Raise(string notice)
    {
        _pending = notice;
        return notice;
    }

    /// <summary>
    /// Returns <paramref name="status"/> with the pending notice appended, consuming it, or
    /// <paramref name="status"/> itself (same instance) when nothing needs adding.
    /// </summary>
    public string Decorate(string status)
    {
        var notice = _pending;
        if (notice is null || status.Contains(notice, StringComparison.Ordinal))
            return status;

        _pending = null;
        return string.IsNullOrWhiteSpace(status) ? notice : status + Separator + notice;
    }
}
