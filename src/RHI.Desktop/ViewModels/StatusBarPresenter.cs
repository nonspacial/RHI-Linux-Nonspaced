using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #298 (upstream 2.9.2 <c>SettingsHandler.FormatLastUpdateCheck</c> / <c>FormatNextUpdateCheck</c>,
/// <c>MainWindow.FormatIdleStatus</c>): the status bar's "when RHI last checked for updates and when the next check is due"
/// texts, localised. Pure: the clock is passed in.
/// </summary>
public static class StatusBarText
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>"Just now" / "18m ago" / "1h 18m ago" / "2d 3h ago"; "Not yet" when RHI has never checked.</summary>
    public static string LastChecked(DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is not { } at) return T("Sb.NotYet");
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return T("Sb.JustNow"); // includes a clock that went back (a future stamp)
        if (ago < TimeSpan.FromHours(1)) return TF("Sb.MinutesAgo", Whole(ago.TotalMinutes));
        if (ago < TimeSpan.FromDays(1)) return TF("Sb.HoursAgo", Whole(ago.TotalHours), ago.Minutes.ToString("00", CultureInfo.InvariantCulture));
        return TF("Sb.DaysAgo", Whole(ago.TotalDays), ago.Hours);
    }

    /// <summary>The status bar's "next check in": "under a minute", else "2h 41m".</summary>
    public static string NextCheckIn(DateTimeOffset next, DateTimeOffset now)
    {
        var remaining = next - now;
        return remaining <= TimeSpan.FromMinutes(1)
            ? T("Sb.UnderAMinute")
            : TF("Sb.HoursMinutes", Whole(remaining.TotalHours), remaining.Minutes.ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The Settings countdown (upstream's Next check panel; Settings is another lane's): "2h 41m 20s", rounded up so it
    /// reaches 0s when the check is due; "Any moment" once due; "After startup" while no pass is scheduled yet.
    /// </summary>
    public static string Countdown(DateTimeOffset? next, DateTimeOffset now)
    {
        if (next is not { } at) return T("Sb.AfterStartup");
        var remaining = at - now;
        if (remaining <= TimeSpan.Zero) return T("Sb.AnyMoment");
        var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
        return TF("Sb.Countdown", seconds / 3600, (seconds % 3600 / 60).ToString("00", CultureInfo.InvariantCulture),
            (seconds % 60).ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>"Last checked: 18m ago  ·  Next check in 2h 41m"; without the second part while no pass is scheduled.</summary>
    public static string Idle(DateTimeOffset? last, DateTimeOffset? next, DateTimeOffset now)
    {
        var text = last is null ? T("Sb.LastCheckedNotYet")
            : now - last.Value < TimeSpan.FromMinutes(1) ? T("Sb.LastCheckedJustNow")
            : TF("Sb.LastChecked", LastChecked(last, now));
        return next is { } at ? text + "  ·  " + TF("Sb.NextCheckIn", NextCheckIn(at, now)) : text;
    }

    private static long Whole(double value) => (long)Math.Floor(value);
}

/// <summary>
/// #298 (upstream <c>MainViewModel.Activity</c>): what RHI is doing in the background right now (scanning, checking for
/// updates, updating DLSS…). Activities may overlap; the newest one still running is shown. Thread-safe; <see cref="Changed"/>
/// is raised on the thread that began or ended the activity.
/// </summary>
public sealed class BackgroundActivity
{
    private readonly object _gate = new();
    private readonly List<(int Id, string Text)> _running = [];
    private int _nextId;

    /// <summary>Raised when an activity starts or ends.</summary>
    public event Action? Changed;

    /// <summary>The newest activity still running, or "" when idle.</summary>
    public string Current
    {
        get { lock (_gate) return _running.Count > 0 ? _running[^1].Text : ""; }
    }

    /// <summary>Marks an activity as running until the returned scope is disposed (disposing twice does nothing).</summary>
    public IDisposable Begin(string text)
    {
        int id;
        lock (_gate)
        {
            id = ++_nextId;
            _running.Add((id, text));
        }
        Changed?.Invoke();
        return new Scope(this, id);
    }

    private void End(int id)
    {
        lock (_gate) _running.RemoveAll(entry => entry.Id == id);
        Changed?.Invoke();
    }

    private sealed class Scope(BackgroundActivity owner, int id) : IDisposable
    {
        private BackgroundActivity? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.End(id);
    }
}

/// <summary>
/// #298 (upstream 2.9.2 <c>MainWindow.StatusBar</c>): the bottom-left status text. In order: a status line set in the
/// last <see cref="MessageDuration"/>; the newest background activity; the last status line while an operation holds the
/// window; otherwise when RHI last checked for updates and when the next check is due. Linux: RHI's status lines stay
/// on screen only for <see cref="MessageDuration"/>, as upstream's messages do; the last one stays readable in the
/// tooltip (upstream keeps only "offline" and "Error" lines, which Linux cannot tell from the others). UI thread only,
/// except <see cref="BackgroundActivity"/>, whose changes are posted here.
/// </summary>
public sealed partial class StatusBarPresenter : ObservableObject
{
    /// <summary>How long a status line stays before the bar goes back to activity or the idle line (upstream: 2–30 s).</summary>
    public static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(10);

    /// <summary>How often the window refreshes the bar while it is shown, so a status line leaves on time (upstream: 15 s,
    /// plus a refresh scheduled for each message's end).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<DateTimeOffset?> _lastCheck;
    private readonly Func<DateTimeOffset?> _nextCheck;
    private string _message = "";
    private DateTimeOffset _messageUntil;
    private bool _busy;

    /// <param name="lastCheck">When updates were last checked (this launch or an earlier one); null = never.</param>
    /// <param name="nextCheck">When the next automatic check runs; null while none is scheduled.</param>
    /// <param name="post">Runs an activity change on the UI thread (tests: inline).</param>
    public StatusBarPresenter(Func<DateTimeOffset> now, Func<DateTimeOffset?> lastCheck, Func<DateTimeOffset?> nextCheck,
        BackgroundActivity activity, Action<Action> post)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _lastCheck = lastCheck ?? throw new ArgumentNullException(nameof(lastCheck));
        _nextCheck = nextCheck ?? throw new ArgumentNullException(nameof(nextCheck));
        Activity = activity ?? throw new ArgumentNullException(nameof(activity));
        ArgumentNullException.ThrowIfNull(post);
        Activity.Changed += () => post(Refresh);
        Refresh();
    }

    public BackgroundActivity Activity { get; }

    /// <summary>The bar's text (it trims on a narrow window).</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>The text in full, then the last status line when it is no longer on the bar; null when empty.</summary>
    [ObservableProperty] private string? _toolTip;

    /// <summary>When RHI last checked for updates (Settings' countdown panel reads it too).</summary>
    public DateTimeOffset? LastCheck => _lastCheck();

    /// <summary>When the next automatic check is due; null while none is scheduled.</summary>
    public DateTimeOffset? NextCheck => _nextCheck();

    /// <summary>A status line was set (the shell's StatusText): it shows now, for <see cref="MessageDuration"/>.</summary>
    public void ShowMessage(string? message)
    {
        _message = message ?? "";
        _messageUntil = _now() + MessageDuration;
        Refresh();
    }

    /// <summary>An operation holds (or released) the window: its status line stays while it runs.</summary>
    public void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        Refresh();
    }

    /// <summary>Works the text out again (the window's timer, every <see cref="RefreshInterval"/>).</summary>
    public void Refresh()
    {
        var now = _now();
        var text = Compose(now);
        Text = text;
        var lastLine = _message.Length > 0 && !string.Equals(_message, text, StringComparison.Ordinal)
            ? UiLocalizationService.Format("Sb.LastMessage", _message)
            : null;
        ToolTip = text.Length == 0 ? lastLine : lastLine is null ? text : text + "\n" + lastLine;
    }

    private string Compose(DateTimeOffset now)
    {
        if (_message.Length > 0 && now < _messageUntil) return _message;
        var activity = Activity.Current;
        if (activity.Length > 0) return activity;
        if (_busy && _message.Length > 0) return _message;
        return StatusBarText.Idle(_lastCheck(), _nextCheck(), now);
    }
}
