namespace RHI.Desktop.ViewModels;

/// <summary>
/// w14/layouts-b (#308, upstream 2.9.0 MainWindow.ReturnAfterGame): waits for a game started from RHI to close, so Spotlight's
/// controller mode can bring RHI back to the front. Linux launches often hand off (Steam, Lutris, Heroic start the game
/// themselves and return), so the game's own process is watched with the running probe: it must show up within
/// <see cref="AppearWithin"/>, then RHI waits until it is gone. Never a tight loop: one look every <see cref="Poll"/>.
/// </summary>
public static class GameExitWatcher
{
    public static readonly TimeSpan AppearWithin = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    /// <summary>True once the game ran and closed; false when it never showed up (or the wait was cancelled).</summary>
    public static async Task<bool> WaitForCloseAsync(Func<bool> isRunning, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken,
        TimeSpan? appearWithin = null)
    {
        try
        {
            var waited = TimeSpan.Zero;
            var limit = appearWithin ?? AppearWithin;
            while (!isRunning())
            {
                if (waited >= limit) return false;
                await delay(Poll, cancellationToken).ConfigureAwait(false);
                waited += Poll;
            }
            while (isRunning()) await delay(Poll, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
