using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Game folders inside one Wine prefix, at per-game granularity. Shared by the standalone Wine
/// detector (whole prefix) and the Lutris detector (games inside a launcher's prefix).
/// </summary>
internal static class PrefixGameFolders
{
    // Executable-holding folders that belong to one game (besides bin*), so their parent is that
    // game rather than a vendor folder listing several products.
    private static readonly string[] GameInternalFolderNames =
    [
        "binaries", "x64", "x86", "x86_64", "win32", "win64", "32bit", "64bit", "engine", "game", "retail",
        "support", "redist", "_commonredist", "commonredist", "directx", "tools"
    ];

    // Default store libraries under Program Files that hold only games, one per folder
    // (the EA app installs to `EA Games`, Origin to `Origin Games`; the clients live elsewhere).
    private static readonly string[] StoreLibraryFolderNames = ["EA Games", "Origin Games"];

    /// <summary>
    /// One candidate game folder. <c>Excluded</c> is a library subtree a launcher's own executable must
    /// not be picked from. <c>Library</c> is the store library the folder sits in (a launcher's
    /// <c>games</c> folder, <c>drive_c/Games</c>, <c>EA Games</c>); null for any other Program Files entry.
    /// </summary>
    internal readonly record struct Entry(string Directory, string? Excluded, string? Library);

    /// <summary>
    /// Each folder under Program Files* and drive_c/Games (Battle.net's default library). A vendor folder
    /// with no executable of its own (Ubisoft, Electronic Arts) is replaced by its product folders, and a
    /// launcher's `games` library or a store library folder adds one entry per game.
    /// </summary>
    internal static IEnumerable<Entry> Find(string driveC)
    {
        var unreadable = new UnreadableFolders();
        foreach (var baseDir in new[] { "Program Files", "Program Files (x86)" })
            foreach (var top in Children(Path.Combine(driveC, baseDir), unreadable))
            {
                var library = StoreLibraryFolderNames.Contains(Path.GetFileName(top), StringComparer.OrdinalIgnoreCase);
                foreach (var dir in library ? Children(top, unreadable) : new[] { top })
                    foreach (var entry in Expand(dir, allowVendor: !library, library: library ? top : null, unreadable))
                        yield return entry;
            }

        var games = Path.Combine(driveC, "Games");
        foreach (var top in Children(games, unreadable))
            foreach (var entry in Expand(top, allowVendor: false, library: games, unreadable))
                yield return entry;
    }

    /// <summary>
    /// Likely game executable of an entry. Indexes only that folder (never the prefix root) and skips the
    /// library subtree of a launcher entry.
    /// </summary>
    internal static string? FindExecutable(Entry entry, CancellationToken ct)
    {
        var index = GameFileIndexer.Build(entry.Directory, 8, ct);
        if (entry.Excluded is not null)
            index = index.Where(file => !file.StartsWith(entry.Excluded + "/", StringComparison.Ordinal)).ToArray();
        return DetectionHelpers.FindLikelyExecutable(entry.Directory, index, ct);
    }

    private static IEnumerable<Entry> Expand(string dir, bool allowVendor, string? library, UnreadableFolders unreadable)
    {
        var games = LibraryDirectory(dir, unreadable);
        if (games is not null)
        {
            yield return new Entry(dir, games, library);
            foreach (var game in Children(games, unreadable))
                yield return new Entry(game, null, games);
            yield break;
        }

        if (allowVendor && !HasOwnExecutable(dir, unreadable))
        {
            var products = Children(dir, unreadable)
                .Where(child => HasOwnExecutable(child, unreadable) || LibraryDirectory(child, unreadable) is not null)
                .ToArray();
            // An exe under `bin`/`Binaries` means this is one game whose exe lives deeper, not a vendor.
            if (products.Length > 0 && !products.Any(child => IsGameInternalFolder(Path.GetFileName(child))))
            {
                foreach (var product in products)
                    foreach (var entry in Expand(product, allowVendor: false, library, unreadable))
                        yield return entry;
                yield break;
            }
        }

        yield return new Entry(dir, null, library);
    }

    private static IEnumerable<string> Children(string dir, UnreadableFolders unreadable)
    {
        string[] children;
        try { children = Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Report(dir, ex);
            yield break;
        }
        foreach (var child in children.Order(StringComparer.Ordinal))
            if (!DetectionHelpers.IsKnownNonGameWineDirectory(Path.GetFileName(child)))
                yield return child;
    }

    private static string? LibraryDirectory(string dir, UnreadableFolders unreadable)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .FirstOrDefault(child => Path.GetFileName(child).Equals("games", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Report(dir, ex);
            return null;
        }
    }

    private static bool HasOwnExecutable(string dir, UnreadableFolders unreadable)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Any(file => !Path.GetFileName(file).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Report(dir, ex);
            return false;
        }
    }

    /// <summary>
    /// Reports each unreadable folder once per prefix scan (it is probed for a library, an executable and its
    /// children): games in it are missing from the library, so the scan's problems notice names it (Refs #144).
    /// </summary>
    private sealed class UnreadableFolders
    {
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        public void Report(string dir, Exception error)
        {
            if (_reported.Add(dir))
                DetectorDiagnostics.Warn($"Wine prefix folder {dir} unreadable; games in it are not listed", error);
        }
    }

    private static bool IsGameInternalFolder(string name)
        => name.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
           GameInternalFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
