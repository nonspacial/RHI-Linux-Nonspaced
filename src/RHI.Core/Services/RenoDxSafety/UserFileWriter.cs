namespace RHI.Core.Services.RenoDxSafety;

/// <summary>
/// Crash-safe replacement of a user file: the new bytes go to a temp file in the same folder, are flushed to
/// disk (fsync), take the original's Unix mode, and are renamed over the original. A failure at any point
/// leaves the original bytes untouched and removes the temp file. A symlinked path is written through to its
/// final target so the link itself survives.
/// </summary>
public static class UserFileWriter
{
    /// <param name="wrapOutput">Test seam: wraps the temp-file stream (e.g. to fail mid-write like a full disk).</param>
    public static void WriteAllBytes(string path, byte[] bytes, Func<Stream, Stream>? wrapOutput = null)
    {
        var target = ResolveFinalTarget(path);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory)) throw new IOException($"Cannot resolve the folder of {path}.");
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(target))
            mode = File.GetUnixFileMode(target);

        var temp = Path.Combine(directory, "." + Path.GetFileName(target) + $".rhi-tmp-{Guid.NewGuid():N}");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var output = wrapOutput?.Invoke(file) ?? file;
                try
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
                finally
                {
                    if (!ReferenceEquals(output, file)) output.Dispose();
                }
                file.Flush(flushToDisk: true);
            }
            if (mode is { } unixMode && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, unixMode);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (IOException) { /* the original is intact; a stray hidden temp file is the only leftover */ }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static string ResolveFinalTarget(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null) return path;
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
    }
}
