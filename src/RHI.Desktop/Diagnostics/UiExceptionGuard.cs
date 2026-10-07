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

    /// <summary>Logs the exception and reports the localised message; returns that message.</summary>
    public static string Report(Exception exception, string context)
    {
        var logged = _log.TryWrite(context, exception);
        var message = Describe(exception, logged ? _log.CurrentPath : null);
        try { _reporter?.Invoke(message); }
        catch (Exception reporterFailure) { _log.TryWrite("reporter", reporterFailure); }
        return message;
    }

    public static string Describe(Exception exception, string? logPath)
        => logPath is null
            ? UiLocalizationService.Format("Ui.UnexpectedErrorNoLog", Detail(exception))
            : UiLocalizationService.Format("Ui.UnexpectedError", Detail(exception), logPath);

    /// <summary>
    /// Runs UI work (the body of an event handler) so nothing escapes it. A plain cancellation (dialog closed, user
    /// cancelled) is only logged; a timeout is a failure and is reported.
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
