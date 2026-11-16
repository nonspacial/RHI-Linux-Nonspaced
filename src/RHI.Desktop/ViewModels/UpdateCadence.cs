namespace RHI.Desktop.ViewModels;

/// <summary>
/// #292 (upstream 2.8.9 / 2.9.2 <c>PeriodicUpdateCheckInterval</c>, <c>ComputeFirstPeriodicCheckDelay</c>): how often the
/// automatic update pass runs, and when the first one after a start runs.
/// </summary>
public static class UpdateCadence
{
    /// <summary>Without a GitHub token: a pass uses much of the 60 requests an hour GitHub allows.</summary>
    public static readonly TimeSpan WithoutToken = TimeSpan.FromHours(4);

    /// <summary>With a GitHub token (5,000 requests an hour).</summary>
    public static readonly TimeSpan WithToken = TimeSpan.FromHours(1);

    public static TimeSpan Interval(bool hasGitHubToken) => hasGitHubToken ? WithToken : WithoutToken;

    /// <summary>
    /// The wait before the first pass of a start: none when no pass is recorded or the last one is an interval or more
    /// ago, else what is left of the interval, so a pass runs at least once per interval across restarts and a restart
    /// never adds a pass. A last pass in the future (the clock went back) waits a whole interval, as upstream.
    /// </summary>
    public static TimeSpan FirstDelay(DateTimeOffset? lastPass, DateTimeOffset now, TimeSpan interval)
    {
        if (lastPass is not { } last) return TimeSpan.Zero;
        if (last > now) return interval;
        var due = last + interval - now;
        return due <= TimeSpan.Zero ? TimeSpan.Zero : due;
    }
}
