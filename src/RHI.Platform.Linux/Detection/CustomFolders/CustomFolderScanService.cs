using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.CustomFolders;

/// <summary>A folder that looks like a game RHI does not list yet, offered to the user before it is added.</summary>
public sealed class CustomGameCandidate
{
    /// <summary>The game's root folder (a direct subfolder of the custom folder, or the custom folder itself).</summary>
    public string FolderPath { get; init; } = "";

    /// <summary>The custom folder (from the list) this candidate was found in.</summary>
    public string CustomFolder { get; init; } = "";

    /// <summary>Suggested display name (cleaned folder name, or the matching PCGamingWiki title).</summary>
    public string SuggestedName { get; init; } = "";

    /// <summary>"Unreal Engine", "Unity", "RE Engine", or null when the folder does not say.</summary>
    public string? Engine { get; init; }

    /// <summary>Exes ranked best first. Never empty for an offered candidate.</summary>
    public IReadOnlyList<ScoredExe> Exes { get; init; } = [];

    /// <summary>True when the top two exes scored close together, so the user should check the pick.</summary>
    public bool IsAmbiguous { get; init; }
}

/// <summary>What the scan needs to know about the library (a snapshot taken on the UI thread).</summary>
public sealed class CustomFolderScanRequest
{
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>Candidate folders the user ignored (or removed after adding), as saved.</summary>
    public IReadOnlyCollection<string> IgnoredFolders { get; init; } = [];

    /// <summary>Install folders of games already in the library (every source, manual games included).</summary>
    public IReadOnlyList<string> LibraryPaths { get; init; } = [];

    /// <summary>Names (<see cref="CustomGameExeScorer.Norm"/>) of games already in the library or removed from RHI.</summary>
    public IReadOnlySet<string> KnownNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Exact title lookup (PCGamingWiki); null when unknown.</summary>
    public Func<string, string?>? CanonicalTitle { get; init; }

    /// <summary>Test seam: the PE probe (default <see cref="CustomGameExeScorer.ProbeExe"/>).</summary>
    public Func<string, ExeProbe>? Probe { get; init; }
}

/// <summary>Why a folder cannot go on the custom game folders list.</summary>
public enum CustomFolderProblem
{
    None,
    NoFolder,
    Missing,
    /// <summary>The filesystem root, a system folder or a whole drive (mount point).</summary>
    WholeDrive,
    /// <summary>The home folder itself or a folder above it.</summary>
    HomeFolder,
    /// <summary>A whole Wine prefix or its drive_c.</summary>
    WinePrefix,
    /// <summary>A Steam library folder (any path through steamapps): RHI finds Steam games on its own.</summary>
    SteamLibrary,
    AlreadyListed,
    /// <summary>Inside a listed folder (<see cref="CustomFolderRefusal.Other"/>).</summary>
    InsideListed,
    /// <summary>Holds a listed folder (<see cref="CustomFolderRefusal.Other"/>).</summary>
    ContainsListed
}

public sealed record CustomFolderRefusal(CustomFolderProblem Problem, string? Other = null);

/// <summary>
/// #293 (upstream 2.9.0 <c>CustomFolderScanService</c>): scans the user's custom game folders for DRM-free and other
/// non-store games. Each direct subfolder is one candidate (or the folder itself when it is one game). Candidates already
/// in the library, ignored, removed from RHI, or without an exe are left out. Nothing is added here: the shell offers the
/// results to the user. Linux: paths compare as the library does (<see cref="PathIdentity"/>: symlinks resolved, case
/// sensitive), and a symlinked game folder inside a custom folder counts as a candidate (upstream skips junctions).
/// </summary>
public static class CustomFolderScanService
{
    public static Task<IReadOnlyList<CustomGameCandidate>> ScanAsync(CustomFolderScanRequest request, CancellationToken ct)
        => Task.Run(() => Scan(request, ct), ct);

    public static IReadOnlyList<CustomGameCandidate> Scan(CustomFolderScanRequest request, CancellationToken ct = default)
    {
        var found = new List<CustomGameCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ignored = request.IgnoredFolders.Select(PathIdentity.Canonicalize).ToHashSet(StringComparer.Ordinal);
        var library = request.LibraryPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathIdentity.Canonicalize)
            .Where(p => p.Length > 1) // never let a root install path hide everything
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var raw in request.Folders)
        {
            ct.ThrowIfCancellationRequested();
            var folder = NormalizePath(raw);
            try
            {
                if (!Directory.Exists(folder))
                {
                    AppLog.Warn("custom-folders", $"Folder not found, skipped: {folder}");
                    continue;
                }
                foreach (var root in GetCandidateRoots(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(PathIdentity.Canonicalize(root))) continue;
                    try
                    {
                        if (Examine(root, folder, request, ignored, library, ct) is { } candidate) found.Add(candidate);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Warn("custom-folders", $"Could not read {root}", ex);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("custom-folders", $"Could not read {folder}", ex);
            }
        }
        return found;
    }

    /// <summary>The folder itself when it is one game, otherwise its direct subfolders (hidden and installer folders left out).</summary>
    public static List<string> GetCandidateRoots(string folder)
    {
        if (IsSingleGameFolder(folder)) return [NormalizePath(folder)];
        var list = new List<string>();
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (CustomGameExeScorer.IsSkippedFolderName(Path.GetFileName(sub))) continue;
            list.Add(NormalizePath(sub));
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>
    /// True when the folder is one game rather than a folder of games: it has store or engine markers, or it has its own
    /// exe and at most one subfolder that looks like a game.
    /// </summary>
    public static bool IsSingleGameFolder(string folder)
    {
        if (ManualGamePathInference.LooksLikeGameRoot(folder)) return true;
        if (!CustomGameExeScorer.TopLevelGameExes(folder).Any()) return false;
        var gameLike = 0;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            try
            {
                if (ManualGamePathInference.LooksLikeGameRoot(sub) || CustomGameExeScorer.TopLevelGameExes(sub).Any()) gameLike++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable subfolder: not counted */ }
            if (gameLike >= 2) return false;
        }
        return true;
    }

    private static CustomGameCandidate? Examine(string root, string customFolder, CustomFolderScanRequest req, HashSet<string> ignored,
        List<string> library, CancellationToken ct)
    {
        var identity = PathIdentity.Canonicalize(root);
        if (ignored.Contains(identity)) return null;
        foreach (var lib in library)
            if (PathIdentity.IsSameOrInside(lib, identity) || PathIdentity.IsSameOrInside(identity, lib)) return null;

        var folderName = Path.GetFileName(root);
        var cleaned = CustomGameNameCleaner.Clean(folderName);
        if (req.KnownNames.Contains(CustomGameExeScorer.Norm(cleaned))) return null;

        var ranking = CustomGameExeScorer.Rank(root, cleaned, req.Probe, ct);
        if (ranking.Exes.Count == 0) return null;
        var top = ranking.Exes[0];
        var engine = DetectEngine(root, top);

        // Name: the folder's first; for a meaningless folder name, the exe's (Unreal project) name.
        var exeName = CustomGameNameCleaner.CleanFolderName(CustomGameNameCleaner.CleanUnrealExeName(Path.GetFileNameWithoutExtension(top.FullPath)));
        var name = CustomGameNameCleaner.IsGenericFolderName(cleaned) ? exeName : cleaned;
        var canonical = req.CanonicalTitle?.Invoke(name);
        if (canonical is null && CustomGameExeScorer.Norm(exeName).Length >= 4 && !CustomGameNameCleaner.IsGenericFolderName(exeName))
            canonical = req.CanonicalTitle?.Invoke(exeName);
        if (!string.IsNullOrWhiteSpace(canonical)) name = canonical;
        if (string.IsNullOrWhiteSpace(name)) name = folderName;
        if (req.KnownNames.Contains(CustomGameExeScorer.Norm(name))) return null;

        return new CustomGameCandidate
        {
            FolderPath = root,
            CustomFolder = customFolder,
            SuggestedName = name,
            Engine = engine,
            Exes = ranking.Exes,
            IsAmbiguous = ranking.IsAmbiguous
        };
    }

    /// <summary>The engine the folder's layout shows (the label upstream's review dialog prints), or null.</summary>
    internal static string? DetectEngine(string root, ScoredExe top)
    {
        if (Path.GetFileNameWithoutExtension(top.FullPath).EndsWith("Shipping", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(root, "Engine")))
            return "Unreal Engine";
        var exeDir = Path.GetDirectoryName(top.FullPath) ?? root;
        if (CustomGameExeScorer.HasFileIgnoringCase(exeDir, "UnityPlayer.dll")) return "Unity";
        if (File.Exists(Path.Combine(root, "re_chunk_000.pak"))) return "RE Engine";
        return null;
    }

    // ── Path helpers (public for the shell and tests) ──────────────────

    /// <summary>Absolute, no trailing '/' (except the root itself).</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim();
        try { p = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { /* keep as typed */ }
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    /// <summary>
    /// The candidate folder (as the scan would offer it) that holds <paramref name="gamePath"/>, if the path is inside one of
    /// <paramref name="customFolders"/>; null when it is under none of them.
    /// </summary>
    public static string? FindCandidateRoot(string gamePath, IEnumerable<string> customFolders)
    {
        var p = PathIdentity.Canonicalize(gamePath);
        foreach (var raw in customFolders)
        {
            var f = PathIdentity.Canonicalize(raw);
            if (!PathIdentity.IsSameOrInside(p, f)) continue;
            if (p.Equals(f, StringComparison.Ordinal)) return NormalizePath(raw);
            var first = p[(f.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            bool single;
            try { single = Directory.Exists(f) && IsSingleGameFolder(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { single = false; }
            return single || string.IsNullOrEmpty(first) ? NormalizePath(raw) : Path.Combine(NormalizePath(raw), first);
        }
        return null;
    }

    /// <summary>Why <paramref name="path"/> cannot be added, or null when it can. <paramref name="existing"/> is the current list.</summary>
    public static CustomFolderRefusal? ValidateFolder(string path, IEnumerable<string> existing, string? home = null)
    {
        var p = NormalizePath(path);
        if (p.Length == 0) return new(CustomFolderProblem.NoFolder);
        if (!Directory.Exists(p)) return new(CustomFolderProblem.Missing, p);
        if (p.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s.Equals("steamapps", StringComparison.OrdinalIgnoreCase)))
            return new(CustomFolderProblem.SteamLibrary);
        switch (GameRootGuard.Check(p, home))
        {
            case GameRootProblem.FilesystemRoot or GameRootProblem.SystemFolder: return new(CustomFolderProblem.WholeDrive);
            case GameRootProblem.HomeFolder: return new(CustomFolderProblem.HomeFolder);
            case GameRootProblem.SteamLibraryFolder: return new(CustomFolderProblem.SteamLibrary);
        }
        // A whole prefix or its drive_c holds Windows itself; drive_c/Games and the like are fine as game folders.
        if (LutrisGameDetector.IsWinePrefixRoot(p)
            || (Path.GetFileName(p).Equals("drive_c", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(p) is { } prefix && LutrisGameDetector.IsWinePrefixRoot(prefix)))
            return new(CustomFolderProblem.WinePrefix);
        foreach (var raw in existing)
        {
            var e = NormalizePath(raw);
            if (e.Length == 0) continue;
            if (PathIdentity.AreSame(p, e)) return new(CustomFolderProblem.AlreadyListed);
            if (PathIdentity.IsSameOrInside(p, e)) return new(CustomFolderProblem.InsideListed, e);
            if (PathIdentity.IsSameOrInside(e, p)) return new(CustomFolderProblem.ContainsListed, e);
        }
        return null;
    }
}
