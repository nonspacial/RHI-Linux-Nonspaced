namespace RHI.Core.Services;

/// <summary>
/// Append-only diagnostics log (Linux: <c>$XDG_STATE_HOME/rhi-linux/rhi.log</c>) for failures the UI
/// only summarises: detector exceptions, skipped entries, unavailable upstream data and unhandled
/// exceptions. <see cref="Current"/> is set once at start-up; until then (and in tests) writes go nowhere.
/// The file rotates to <c>rhi.log.1</c> at 1 MiB, so it never grows without bound.
/// </summary>
public sealed class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly object _gate = new();
    private int _droppedWrites;

    public AppLog(string path) => FilePath = path;

    public static AppLog? Current { get; set; }

    public string FilePath { get; }

    /// <summary>Writes that failed (disk full, read-only state dir); the log cannot report its own failures elsewhere.</summary>
    public int DroppedWrites => Volatile.Read(ref _droppedWrites);

    public static void Warn(string area, string message, Exception? error = null) => Current?.Write(area, message, error);

    public void Write(string area, string message, Exception? error = null)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{area}] {message}"
                   + (error is null ? "" : $" | {error.GetType().Name}: {error.Message}")
                   + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _droppedWrites);
            }
        }
    }
}
