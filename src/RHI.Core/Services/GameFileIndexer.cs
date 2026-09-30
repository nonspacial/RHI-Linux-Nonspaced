namespace RHI.Core.Services;

/// <summary>
/// Builds one bounded file index for a selected game. Expensive consumers (component
/// detection, DLSS discovery and platform probes) reuse the same list instead of walking
/// a large game tree independently on every tab switch.
/// </summary>
public static class GameFileIndexer
{
    /// <summary>
    /// Hard ceiling on indexed files per game root. A game tree that exceeds this is almost
    /// certainly not a game tree (a whole Wine prefix, a drive root reached through a symlink)
    /// and indexing further only burns RAM without improving detection.
    /// </summary>
    public const int DefaultMaxFiles = 200_000;

    public static IReadOnlyList<string> Build(
        string root,
        int maxDepth = 8,
        CancellationToken cancellationToken = default)
        => Build(root, maxDepth, DefaultMaxFiles, cancellationToken);

    public static IReadOnlyList<string> Build(
        string root,
        int maxDepth,
        int maxFiles,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) return [];

        var result = new List<string>(2048);
        var rootDepth = Depth(root);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.Count >= maxFiles) return result;
                    result.Add(file);
                }
            }
            catch { }

            var depth = Depth(dir) - rootDepth;
            if (depth >= maxDepth) continue;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShouldSkipDirectory(Path.GetFileName(sub))) continue;
                    // Never follow directory symlinks. Wine prefixes expose dosdevices/z: -> /
                    // and x: -> $HOME; following those indexes the whole machine.
                    if (IsSymbolicLink(sub)) continue;
                    pending.Push(sub);
                }
            }
            catch { }
        }

        return result;
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool ShouldSkipDirectory(string name)
        => name.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Redist", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("__Installer", StringComparison.OrdinalIgnoreCase) ||
           name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
           // Wine prefix plumbing: drive letter symlinks and the Windows system image.
           name.Equals("dosdevices", StringComparison.OrdinalIgnoreCase);

    private static int Depth(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
}
