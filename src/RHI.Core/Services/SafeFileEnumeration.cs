namespace RHI.Core.Services;

/// <summary>
/// Recursive enumeration that never descends into a directory symlink. <c>SearchOption.AllDirectories</c>
/// follows them, so a link such as a Wine prefix's <c>dosdevices/z: -> /</c>, or a user's shader folder
/// linked into a game, takes the walk (and anything it deletes or copies) outside the tree.
/// Symlinked files are still returned, as before; pattern matching and error behaviour match the
/// <c>SearchOption.AllDirectories</c> overloads unless <paramref name="ignoreInaccessible"/> is set.
/// </summary>
public static class SafeFileEnumeration
{
    public static IEnumerable<string> Files(string root, string pattern = "*", bool ignoreInaccessible = false)
    {
        var options = Options(ignoreInaccessible);
        foreach (var directory in DirectoriesAndRoot(root, options))
            foreach (var file in Directory.EnumerateFiles(directory, pattern, options))
                yield return file;
    }

    /// <summary>Every real subdirectory below <paramref name="root"/> (not the root, not symlinked ones or anything below them).</summary>
    public static IEnumerable<string> Directories(string root, bool ignoreInaccessible = false)
        => DirectoriesAndRoot(root, Options(ignoreInaccessible)).Skip(1);

    /// <summary>
    /// Every file, directory and symlink below <paramref name="root"/>; a directory symlink is returned
    /// as an entry (so callers can refuse it) but never walked into.
    /// </summary>
    public static IEnumerable<FileSystemInfo> Entries(string root, bool ignoreInaccessible = false)
    {
        var options = Options(ignoreInaccessible);
        foreach (var directory in DirectoriesAndRoot(root, options))
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                yield return entry;
    }

    private static IEnumerable<string> DirectoriesAndRoot(string root, EnumerationOptions options)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            yield return directory;
            foreach (var sub in Directory.EnumerateDirectories(directory, "*", options))
                if (!IsLink(sub)) pending.Push(sub);
        }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    // The settings the SearchOption overloads use (nothing skipped, Win32 wildcards, errors thrown).
    private static EnumerationOptions Options(bool ignoreInaccessible) => new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = ignoreInaccessible,
        MatchType = MatchType.Win32,
        MatchCasing = MatchCasing.PlatformDefault
    };
}
