using System.Text;

namespace RHI.Core.Services;

/// <summary>
/// RHI's one diagnostics log (Linux: <c>$XDG_STATE_HOME/rhi-linux/logs/rhi.log</c>). It holds everything the UI
/// only summarises: recovered-from failures (detectors, upstream data, caches, deployment recovery problems),
/// refused or failed store writes, and every unexpected exception the UI guard caught (with its stack).
/// <see cref="Current"/> is set once at start-up; until then (and in tests) <see cref="Warn"/> goes nowhere.
/// The file rotates by size (<c>rhi.log.1</c> … <c>rhi.log.{keep}</c>, oldest dropped) and every entry is
/// flushed to disk, so the trace of a crash survives it. Writing never throws.
/// </summary>
public sealed class AppLog
{
    public const long DefaultMaxBytes = 1024 * 1024;
    public const int DefaultKeep = 3;

    private static readonly AsyncLocal<AppLog?> FlowLog = new();
    private static AppLog? s_current;

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _keep;
    private int _droppedWrites;

    public AppLog(string path, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        FilePath = path;
        _maxBytes = Math.Max(1024, maxBytes);
        _keep = Math.Max(1, keep);
    }

    /// <summary>
    /// The process log, set once at start-up. A log attached with <see cref="UseInThisFlow"/> takes
    /// precedence inside that async flow.
    /// </summary>
    public static AppLog? Current
    {
        get => FlowLog.Value ?? s_current;
        set => s_current = value;
    }

    /// <summary>
    /// Sends <see cref="Warn"/> from this async flow (and the tasks it starts) to <paramref name="log"/>
    /// until the result is disposed. Lets parallel tests each read their own log without swapping the
    /// process-wide <see cref="Current"/> under one another.
    /// </summary>
    public static IDisposable UseInThisFlow(AppLog log)
    {
        var previous = FlowLog.Value;
        FlowLog.Value = log;
        return new FlowRestore(previous);
    }

    private sealed class FlowRestore(AppLog? previous) : IDisposable
    {
        public void Dispose() => FlowLog.Value = previous;
    }

    public string FilePath { get; }

    /// <summary>Rotated file <paramref name="index"/> (1 = newest).</summary>
    public string RotatedPath(int index) => $"{FilePath}.{index}";

    /// <summary>Writes that failed (disk full, read-only state dir); the log cannot report its own failures elsewhere.</summary>
    public int DroppedWrites => Volatile.Read(ref _droppedWrites);

    /// <summary>A recovered-from failure: one line with the exception's type and message.</summary>
    public static void Warn(string area, string message, Exception? error = null) => Current?.Write(area, message, error);

    public void Write(string area, string message, Exception? error = null)
        => TryWrite(area, error is null ? message : $"{message} | {error.GetType().Name}: {error.Message}");

    /// <summary>An unexpected exception with its full stack; false when the entry could not be kept.</summary>
    public bool TryWrite(string area, Exception exception) => TryWrite(area, exception.ToString());

    /// <summary>Appends one entry; false (never an exception) when it could not be written.</summary>
    public bool TryWrite(string area, string detail)
    {
        var bytes = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} [{area}] {detail}{Environment.NewLine}");
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var current = new FileInfo(FilePath);
                if (current.Exists && current.Length > 0 && current.Length + bytes.Length > _maxBytes)
                    Rotate();
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Interlocked.Increment(ref _droppedWrites);
                return false;
            }
        }
    }

    private void Rotate()
    {
        File.Delete(RotatedPath(_keep));
        for (var i = _keep - 1; i >= 1; i--)
            if (File.Exists(RotatedPath(i)))
                File.Move(RotatedPath(i), RotatedPath(i + 1));
        File.Move(FilePath, RotatedPath(1));
    }
}
