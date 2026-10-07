using System.Globalization;
using System.Text;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Diagnostics;

/// <summary>
/// Append-only error log under the XDG state dir (<c>~/.local/state/rhi-linux/logs/errors.log</c>), rotated by size
/// (<c>errors.1.log</c> … <c>errors.{keep}.log</c>, oldest dropped). Writing never throws: a log that cannot be written
/// (read-only or full disk) returns false so the caller can say the details were not kept.
/// </summary>
public sealed class UiErrorLog
{
    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _keep;

    public UiErrorLog(string directory, long maxBytes = 1024 * 1024, int keep = 3)
    {
        Directory = directory;
        _maxBytes = Math.Max(1024, maxBytes);
        _keep = Math.Max(1, keep);
    }

    public static UiErrorLog CreateDefault() => new(Path.Combine(LinuxPaths.StateHome, "rhi-linux", "logs"));

    public string Directory { get; }
    public string CurrentPath => Path.Combine(Directory, "errors.log");

    public string RotatedPath(int index) => Path.Combine(Directory, $"errors.{index}.log");

    public bool TryWrite(string context, Exception exception)
        => TryWrite(context, exception.ToString());

    public bool TryWrite(string context, string detail)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(context).Append("] ")
            .AppendLine(detail)
            .ToString();
        var bytes = Encoding.UTF8.GetBytes(entry);
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var current = new FileInfo(CurrentPath);
                if (current.Exists && current.Length > 0 && current.Length + bytes.Length > _maxBytes)
                    Rotate();
                using var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
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
        File.Move(CurrentPath, RotatedPath(1));
    }
}
