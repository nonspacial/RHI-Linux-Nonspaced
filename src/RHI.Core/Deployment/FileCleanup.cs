namespace RHI.Core.Deployment;

/// <summary>
/// Best-effort removal of files RHI itself created (temporary copies, partial downloads, consumed
/// snapshots). Only I/O and permission failures are tolerated, and they are reported through the
/// return value instead of being swallowed; never use this for a file that may be the only copy of
/// user data — move that aside (quarantine) instead.
/// </summary>
public static class FileCleanup
{
    public static bool TryDelete(string path) => TryDelete(path, out _);

    public static bool TryDelete(string path, out Exception? error)
    {
        error = null;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>Deletes every path, returning those that could not be removed.</summary>
    public static IReadOnlyList<string> TryDeleteAll(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var path in paths)
            if (!TryDelete(path)) failed.Add(path);
        return failed;
    }
}
