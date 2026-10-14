namespace RHI.Core.Services;

/// <summary>
/// Builds one bounded file index for a selected game. Expensive consumers (component
/// detection, DLSS discovery and platform probes) reuse the same list instead of walking
/// a large game tree independently on every tab switch. Library scans never build it: they
/// use <see cref="BuildUntilFound"/>, which stops at the shallowest level holding a launch target.
/// </summary>
public static class GameFileIndexer
{
    /// <summary>
    /// Hard ceiling on indexed files per game root. A game tree that exceeds this is almost
    /// certainly not a game tree (a whole Wine prefix, a drive root reached through a symlink)
    /// and indexing further only burns RAM without improving detection.
    /// </summary>
    public const int DefaultMaxFiles = 200_000;

    /// <summary>Depth of the full index a selected game gets (and the deepest a scan-time walk may go).</summary>
    public const int FullDepth = 8;

    private static readonly AsyncLocal<Action<IndexWalk>?> WalkObserver = new();

    /// <summary>
    /// Test seam: called after every walk started from the current async flow (detectors run on
    /// <c>Task.Run</c>, which carries it), with the depth limit the walk used and the token it was given.
    /// Lets a test count full-depth builds per scan and check the scan token reaches every walk.
    /// </summary>
    internal static Action<IndexWalk>? WalkObserverForTesting
    {
        get => WalkObserver.Value;
        set => WalkObserver.Value = value;
    }

    /// <summary>One finished walk: its root, the deepest folder level it was allowed into, and its token.</summary>
    internal readonly record struct IndexWalk(string Root, int DepthLimit, CancellationToken Token);

    public static IReadOnlyList<string> Build(
        string root,
        int maxDepth = FullDepth,
        CancellationToken cancellationToken = default)
        => Build(root, maxDepth, DefaultMaxFiles, cancellationToken);

    public static IReadOnlyList<string> Build(
        string root,
        int maxDepth,
        int maxFiles,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) return new IndexedFiles([], truncated: false);

        var result = new List<string>(2048);
        var rootDepth = Depth(root);
        var pending = new Stack<string>();
        var unreadable = new UnreadableFolders(root);
        pending.Push(root);
        var truncated = false;

        while (pending.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            truncated = !AddFiles(dir, result, maxFiles, unreadable, cancellationToken);
            if (truncated || Depth(dir) - rootDepth >= maxDepth) continue;
            foreach (var sub in WalkableSubdirectories(dir, unreadable, cancellationToken))
                pending.Push(sub);
        }

        unreadable.Report();
        WalkObserver.Value?.Invoke(new IndexWalk(root, maxDepth, cancellationToken));
        return new IndexedFiles(result, truncated);
    }

    /// <summary>
    /// Scan-time walk (Refs #48): breadth-first, every file down to <paramref name="shallowDepth"/> folder levels,
    /// then one more level at a time, stopping after the first level that leaves a file matching
    /// <paramref name="wanted"/> in the result, and never below <paramref name="maxDepth"/>. A game whose
    /// executable sits near its root costs a few directory reads instead of the full index; one whose
    /// executable sits deep is still found (its card is never dropped for being deep). The full index is
    /// built later, for the selected game only.
    /// </summary>
    public static IReadOnlyList<string> BuildUntilFound(
        string root,
        int shallowDepth,
        int maxDepth,
        Func<string, bool> wanted,
        int maxFiles = DefaultMaxFiles,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) return new IndexedFiles([], truncated: false);

        var result = new List<string>(256);
        var unreadable = new UnreadableFolders(root);
        var level = new List<string> { root };
        var depth = 0;
        var found = false;
        var truncated = false;

        while (level.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new List<string>();
            foreach (var dir in level)
            {
                var before = result.Count;
                truncated = !AddFiles(dir, result, maxFiles, unreadable, cancellationToken);
                for (var i = before; i < result.Count && !found; i++)
                    found = wanted(result[i]);
                if (truncated) break;
                if (depth < maxDepth)
                    next.AddRange(WalkableSubdirectories(dir, unreadable, cancellationToken));
            }

            if (truncated || (found && depth >= shallowDepth) || depth >= maxDepth) break;
            level = next;
            depth++;
        }

        unreadable.Report();
        WalkObserver.Value?.Invoke(new IndexWalk(root, depth, cancellationToken));
        return new IndexedFiles(result, truncated);
    }

    /// <summary>
    /// True when <paramref name="files"/> came from <see cref="Build(string, int, int, CancellationToken)"/>
    /// and stopped at the file cap: which files are missing depends on walk order, so callers say so.
    /// </summary>
    public static bool IsTruncated(IReadOnlyList<string> files) => files is IndexedFiles { Truncated: true };

    /// <summary>The indexed paths plus whether the walk stopped at the file cap.</summary>
    public sealed class IndexedFiles : System.Collections.ObjectModel.ReadOnlyCollection<string>
    {
        internal IndexedFiles(IList<string> files, bool truncated) : base(files) => Truncated = truncated;

        public bool Truncated { get; }
    }

    /// <summary>Adds the files of <paramref name="dir"/>; false when the cap was reached (the walk stops).</summary>
    private static bool AddFiles(string dir, List<string> result, int maxFiles, UnreadableFolders unreadable, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Count >= maxFiles) return false;
                result.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            unreadable.Add(dir, ex);
        }
        return true;
    }

    private static List<string> WalkableSubdirectories(string dir, UnreadableFolders unreadable, CancellationToken cancellationToken)
    {
        var subdirectories = new List<string>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipDirectory(Path.GetFileName(sub))) continue;
                // Never follow directory symlinks. Wine prefixes expose dosdevices/z: -> /
                // and x: -> $HOME; following those indexes the whole machine.
                if (IsSymbolicLink(sub)) continue;
                subdirectories.Add(sub);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            unreadable.Add(dir, ex);
        }
        return subdirectories;
    }

    /// <summary>
    /// Folders a walk could not read (their files are missing from the index). Logged once per walk, so a game
    /// with many locked folders does not flood the log on every selection.
    /// </summary>
    private sealed class UnreadableFolders(string root)
    {
        private int _count;
        private string? _first;
        private Exception? _firstError;

        public void Add(string dir, Exception error)
        {
            if (_count++ == 0)
            {
                _first = dir;
                _firstError = error;
            }
        }

        public void Report()
        {
            if (_count > 0)
                AppLog.Warn("index", $"{_count} folder(s) under {root} unreadable and not indexed; first: {_first}", _firstError);
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unknown is treated as a link: the walk never enters a folder it cannot vouch for.
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
