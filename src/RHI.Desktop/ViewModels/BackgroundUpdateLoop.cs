using RHI.Core.Services;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The automatic-update schedule (Refs #81). A pass that throws is logged and reported, then retried after a short
/// delay that doubles with each consecutive failure up to the configured interval; the loop only ends when it is
/// cancelled. It used to end for the rest of the session on its first exception, leaving auto-update silently off.
/// </summary>
public static class BackgroundUpdateLoop
{
    /// <summary>First retry delay after a failed pass (doubles per consecutive failure, capped at the interval).</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(15);

    /// <param name="pass">One update pass.</param>
    /// <param name="interval">Time between passes that succeed.</param>
    /// <param name="runImmediate">Run a pass before the first wait.</param>
    /// <param name="passFailed">Told about a failed pass and when the next try is (status line); runs on the caller's context.</param>
    /// <param name="delay">Waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test drives the clock.</param>
    public static async Task RunAsync(
        Func<CancellationToken, Task> pass,
        TimeSpan interval,
        bool runImmediate,
        Action<Exception, TimeSpan> passFailed,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(passFailed);
        delay ??= Task.Delay;
        var failures = 0;
        var next = runImmediate ? TimeSpan.Zero : interval;
        while (true)
        {
            try
            {
                if (next > TimeSpan.Zero) await delay(next, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await pass(cancellationToken);
                failures = 0;
                next = interval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Network, disk or a component service: none of them should stop automatic updates for the session.
                failures++;
                next = RetryDelay(failures, interval);
                AppLog.Warn("update", $"background update pass failed ({failures} in a row); next try in {next}", ex);
                passFailed(ex, next);
            }
        }
    }

    /// <summary>Status line for a failed pass: what went wrong and when the next try is, in whole minutes.</summary>
    public static string FailedStatus(Exception error, TimeSpan retry)
        => UiLocalizationService.Format("Vm.BackgroundUpdateFailedRetrying",
            UiLocalizationService.Present(error.Message), (int)Math.Ceiling(retry.TotalMinutes));

    /// <summary>15 min, 30 min, 1 h … after consecutive failures, never longer than <paramref name="interval"/>.</summary>
    public static TimeSpan RetryDelay(int consecutiveFailures, TimeSpan interval)
    {
        var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 16));
        var backoff = TimeSpan.FromTicks((long)Math.Min(FirstRetryDelay.Ticks * factor, TimeSpan.MaxValue.Ticks / 2.0));
        return backoff < interval ? backoff : interval;
    }
}
