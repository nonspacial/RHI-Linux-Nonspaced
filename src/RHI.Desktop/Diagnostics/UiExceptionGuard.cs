using Avalonia.Threading;
using RHI.Desktop.Localization;

namespace RHI.Desktop.Diagnostics;

/// <summary>
/// Last line of defence for UI work (UI-05, #73): every unexpected exception from a UI handler, a background task or
/// the dispatcher is written to the rotating <see cref="UiErrorLog"/> and shown as a localised, non-fatal status line
/// instead of terminating the app.
/// </summary>
public static class UiExceptionGuard
{
    private static UiErrorLog _log = UiErrorLog.CreateDefault();
    private static Action<string>? _reporter;
    private static int _installed;

    // Loop protection (review m1): an exception thrown on every layout pass, or by a binding the status update itself
    // re-evaluates, must not turn into a log write and a status change per frame.
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StatusRepeatInterval = TimeSpan.FromSeconds(5);
    internal const int BurstLimit = 20;
    private static readonly object ThrottleGate = new();
    private static readonly Dictionary<string, (DateTimeOffset LoggedAt, DateTimeOffset ReportedAt, int Suppressed, bool Kept)> Seen = new(StringComparer.Ordinal);
    private static readonly Queue<DateTimeOffset> RecentFailures = new();
    private static bool _reportingStopped;
    private static TimeProvider _time = TimeProvider.System;

    public static UiErrorLog Log => _log;

    /// <summary>Registers the process-wide handlers once. Call before the first window is created.</summary>
    public static void Install(UiErrorLog? log = null)
    {
        if (log is not null) _log = log;
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        // A terminating exception from a non-UI thread cannot be stopped; at least keep its trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) _log.TryWrite(e.IsTerminating ? "fatal" : "unhandled", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HandleBackgroundException(e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = HandleUiThreadException(e.Exception);
    }

    /// <summary>Points the guard at another log without installing the process-wide handlers (tests).</summary>
    internal static void UseLog(UiErrorLog log) => _log = log;

    /// <summary>Where reports go (the main window's status line). The reporter marshals to the UI thread itself.</summary>
    public static void SetReporter(Action<string>? reporter) => _reporter = reporter;

    /// <summary>An exception escaped to the dispatcher: log, report, and keep the app running.</summary>
    internal static bool HandleUiThreadException(Exception exception)
    {
        Report(exception, "ui-thread");
        return true;
    }

    /// <summary>A faulted task nobody awaited: log and report it; the process must not be torn down for it.</summary>
    internal static void HandleBackgroundException(Exception exception)
    {
        var inner = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : exception;
        Report(inner, "background-task");
    }

    /// <summary>
    /// Logs the exception and reports the localised message; returns that message. Loop protection: the same failure
    /// (context, type and stack) is logged once per <see cref="RepeatWindow"/> — repeats are counted and the count goes
    /// into its next entry — and shown again at most once per <see cref="StatusRepeatInterval"/>, so a user who retries
    /// still sees why. More than <see cref="BurstLimit"/> failures within <see cref="BurstWindow"/> (a failure on every
    /// frame) show once that RHI keeps failing and should be restarted; after that failures are only logged.
    /// </summary>
    public static string Report(Exception exception, string context)
    {
        var now = _time.GetUtcNow();
        var signature = Signature(exception, context);
        bool log, report, burst = false, kept = false;
        var repeats = 0;
        lock (ThrottleGate)
        {
            while (RecentFailures.Count > 0 && now - RecentFailures.Peek() > BurstWindow) RecentFailures.Dequeue();
            RecentFailures.Enqueue(now);
            if (!_reportingStopped && RecentFailures.Count > BurstLimit) _reportingStopped = burst = true;

            if (Seen.TryGetValue(signature, out var seen) && now - seen.LoggedAt < RepeatWindow)
            {
                log = false;
                report = !_reportingStopped && now - seen.ReportedAt >= StatusRepeatInterval;
                kept = seen.Kept;
                Seen[signature] = (seen.LoggedAt, report ? now : seen.ReportedAt, seen.Suppressed + 1, seen.Kept);
            }
            else
            {
                log = true;
                report = !_reportingStopped;
                repeats = seen.Suppressed;
                if (Seen.Count >= 512) Seen.Clear();
                Seen[signature] = (now, now, 0, false);
            }
        }

        if (log)
        {
            var detail = repeats > 0
                ? $"(repeated {repeats} more time(s) after its previous entry) {exception}"
                : exception.ToString();
            kept = _log.TryWrite(context, detail);
            lock (ThrottleGate)
                if (Seen.TryGetValue(signature, out var entry)) Seen[signature] = entry with { Kept = kept };
        }
        if (burst)
            _log.TryWrite("repeated-errors", $"{BurstLimit}+ failures within {BurstWindow.TotalSeconds:F0} s; status reports stopped until RHI restarts.");
        var message = Describe(exception, kept ? _log.CurrentPath : null);
        if (burst) message = UiLocalizationService.Format("Ui.RepeatedErrorsRestart", _log.CurrentPath);
        if (report || burst)
        {
            try { _reporter?.Invoke(message); }
            catch (Exception reporterFailure) { _log.TryWrite("reporter", reporterFailure); }
        }
        return message;
    }

    private static string Signature(Exception exception, string context)
    {
        var text = new System.Text.StringBuilder(context);
        for (var current = exception; current is not null; current = current.InnerException)
            text.Append('|').Append(current.GetType().FullName).Append('@').Append(current.StackTrace ?? current.Message);
        return text.ToString();
    }

    /// <summary>Test seam: a fake clock and a clean throttle.</summary>
    internal static void ResetThrottle(TimeProvider? time = null)
    {
        lock (ThrottleGate)
        {
            Seen.Clear();
            RecentFailures.Clear();
            _reportingStopped = false;
            _time = time ?? TimeProvider.System;
        }
    }

    public static string Describe(Exception exception, string? logPath)
        => logPath is null
            ? UiLocalizationService.Format("Ui.UnexpectedErrorNoLog", Detail(exception))
            : UiLocalizationService.Format("Ui.UnexpectedError", Detail(exception), logPath);

    /// <summary>
    /// Runs UI work (the body of an event handler) so nothing escapes it. A plain cancellation (dialog closed, user
    /// cancelled) is only logged; a timeout is a failure and is reported. A timeout is recognised only by an inner
    /// <see cref="TimeoutException"/> (what <c>HttpClient</c> raises): a cancellation from a
    /// <c>CancellationTokenSource</c> that merely expired carries none and is therefore only logged, by design — the
    /// code that owns such a token reports its own timeout.
    /// </summary>
    public static async Task RunAsync(Func<Task> work, string context)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException cancelled) when (cancelled.InnerException is not TimeoutException)
        {
            _log.TryWrite(context + " (cancelled)", cancelled.Message);
        }
        catch (Exception ex)
        {
            Report(ex, context);
        }
    }

    /// <summary>Fire-and-forget for a started task: its failure is reported like a handler's, never left unobserved.</summary>
    public static void Forget(Task task, string context) => _ = RunAsync(() => task, context);

    private static string Detail(Exception exception)
    {
        var text = exception.Message.Trim();
        if (text.Length == 0) text = exception.GetType().Name;
        return text.EndsWith('.') ? text : text + ".";
    }
}
