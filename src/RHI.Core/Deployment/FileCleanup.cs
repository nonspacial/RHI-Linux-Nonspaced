using RHI.Core.Services;

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
            // An Xbox package file RHI must never change is never deleted either (Refs #90): reported, not removed.
            RHI.Core.Services.PackageFileProtection.ThrowIfProtected(path); // w3/scan hook
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// Removes the empty directories among <paramref name="directories"/> (deepest first, so a parent
    /// emptied by its child goes too). Directories that still hold anything are kept; never recursive.
    /// Returns the empty directories that could not be removed (logged by the caller if it matters).
    /// </summary>
    public static IReadOnlyList<string> TryDeleteEmptyDirectories(IEnumerable<string> directories)
    {
        var failed = new List<string>();
        foreach (var directory in directories.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Length))
            if (!TryDeleteEmptyDirectory(directory)) failed.Add(directory);
        return failed;
    }

    /// <summary>
    /// Removes every empty directory under <paramref name="root"/> (symlinked folders are not walked) and then
    /// <paramref name="root"/> itself when it ended up empty. Anything holding a file is kept; never recursive.
    /// </summary>
    public static IReadOnlyList<string> TryDeleteEmptyTree(string root)
    {
        if (!Directory.Exists(root)) return [];
        List<string> under;
        try { under = SafeFileEnumeration.Directories(root).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return [root]; }
        under.Add(root);
        return TryDeleteEmptyDirectories(under);
    }

    /// <summary>Removes <paramref name="directory"/> if it exists and is empty; false only when an empty one could not be removed.</summary>
    public static bool TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) return true;
            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
