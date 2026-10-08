using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

public sealed partial class LutrisGameDetector : IGameDetector
{
    public string Name => "Lutris";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private static IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var roots = LinuxPaths.LutrisConfigRoots().ToList();
        return DetectIn(roots, LinuxPaths.Home, ct, LutrisPga.Candidates(roots, LinuxPaths.Home, LinuxPaths.DataHome));
    }

    /// <param name="pgaDatabases">Lutris <c>pga.db</c> files to read names from; defaults to the ones beside the roots and under <paramref name="home"/>.</param>
    internal static IReadOnlyList<DetectedGame> DetectIn(IEnumerable<string> configRoots, string home, CancellationToken ct, IEnumerable<string>? pgaDatabases = null)
    {
        var rootList = configRoots.ToList();
        return DetectWith(rootList, home, LutrisPga.Load(pgaDatabases ?? LutrisPga.Candidates(rootList, home)), ct);
    }

    /// <summary>Test seam: detection against already-read <c>pga.db</c> rows keyed by config stem.</summary>
    internal static IReadOnlyList<DetectedGame> DetectWith(IReadOnlyList<string> rootList, string home, IReadOnlyDictionary<string, LutrisPgaGame> library, CancellationToken ct)
    {
        var games = new List<(DetectedGame Game, string Slug, string ConfigFile, bool ExeResolves)>();
        foreach (var configRoot in rootList)
        {
            // Ordinal file-name order (slug, then timestamp): a stable order for picking a prefix's launcher.
            foreach (var file in Directory.EnumerateFiles(configRoot, "*.yml").Concat(Directory.EnumerateFiles(configRoot, "*.yaml")).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    library.TryGetValue(stem, out var pga);
                    // Lutris keeps a removed game's config file; pga.db says it is no longer installed.
                    if (pga?.Installed == false) continue;
                    var config = LutrisConfigFile.Parse(File.ReadAllText(file));
                    var gameDir = string.IsNullOrWhiteSpace(pga?.Directory) ? null : pga!.Directory;
                    // Only the top-level game: mapping; an installer script: repeats game/exe/prefix keys.
                    var exe = LutrisPathExpander.Expand(config.Section("game", "exe"), home, gameDir);
                    var prefix = LutrisPathExpander.Expand(config.Section("game", "prefix"), home, gameDir);
                    var workingDir = LutrisPathExpander.Expand(config.Section("game", "working_dir"), home, gameDir);
                    exe = ResolveRelativeExe(exe, prefix, workingDir);
                    var install = ResolveInstallRoot(exe, workingDir, prefix, home);
                    // No real game folder (e.g. a wrapper script in ~/.local/bin driving a whole prefix,
                    // such as an application installed through Lutris): not a game, not listed.
                    if (install is null) continue;
                    var slug = pga?.Slug ?? ResolveLutrisSlug(stem);
                    // Lutris shows (and lets the user rename) the pga.db name; the config's own name: is an installer copy.
                    var name = pga?.Name ?? config.Top("name") ?? ResolveLutrisSlug(stem).Replace('-', ' ');
                    var configuredRunner = config.Top("runner");
                    var wineVersion = config.Section("wine", "version");
                    var hasWineRuntime = string.Equals(configuredRunner, "wine", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(wineVersion)
                        || !string.IsNullOrWhiteSpace(prefix);
                    var layerType = hasWineRuntime
                        ? (LooksLikeProton(wineVersion) ? "proton" : "wine")
                        : null;
                    var wineBinary = hasWineRuntime ? ResolveLutrisWineBinary(wineVersion) : null;
                    var exeExists = exe is not null && File.Exists(exe);
                    var detectedExecutable = exeExists && IsWithin(install, exe!) ? exe : DetectionHelpers.FindLikelyExecutable(install);

                    var game = new DetectedGame
                    {
                        Name = name,
                        InstallPath = install,
                        Source = "Lutris",
                        ExecutablePath = detectedExecutable,
                        LaunchExecutablePath = exeExists ? exe : detectedExecutable,
                        PrefixPath = Directory.Exists(prefix) ? prefix : null,
                        Runner = layerType switch
                        {
                            "proton" => "Lutris Proton",
                            "wine" => "Lutris Wine",
                            _ => configuredRunner ?? "Lutris"
                        },
                        CompatibilityTool = wineVersion,
                        CompatibilityLayerType = layerType,
                        WineBinaryPath = wineBinary,
                        WineServerPath = ResolveMatchingWineServer(wineBinary),
                        LaunchCommand = "lutris " + Shell($"lutris:rungame/{slug}")
                    };
                    games.Add((game, slug, file, exeExists));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // One unreadable config skips that entry, not the whole Lutris scan.
                    DetectorDiagnostics.Warn($"Lutris config {file} skipped", ex);
                }
            }
        }

        // Distinct Lutris games that resolve to one folder must not share a library identity
        // (favourites, edits, probe cache) or merge into one card. Duplicate configs of the same
        // slug (e.g. two Battle.net installs into one prefix) still share it, as before.
        foreach (var sameFolder in games.GroupBy(x => NormalisePath(x.Game.InstallPath), StringComparer.Ordinal))
        {
            if (sameFolder.Select(x => x.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
            foreach (var (game, _, configFile, _) in sameFolder)
                game.LibraryIdentityPath ??= configFile;
        }
        var listed = games.Select(x => x.Game).ToList();
        return [.. listed, .. LauncherPrefixGames(games.Select(x => (x.Game, x.ExeResolves)).ToList(), ct)];
    }

    /// <summary>
    /// Games installed inside a listed launcher's prefix (Ubisoft Connect's <c>games</c> folder,
    /// Battle.net's <c>drive_c/Games</c>, <c>EA Games</c>): one card per game folder, with the launcher's
    /// prefix and runner. Launch starts the launcher; the user starts the game from there. A folder that is
    /// (or holds) another listed entry, such as the launcher itself or a game with its own Lutris config, is
    /// not listed again; neither is a folder with no game executable.
    /// </summary>
    private static List<DetectedGame> LauncherPrefixGames(IReadOnlyList<(DetectedGame Game, bool ExeResolves)> configs, CancellationToken ct)
    {
        var found = new List<DetectedGame>();
        var listedRoots = configs.Select(c => PathIdentity.Canonicalize(c.Game.InstallPath)).ToArray();
        var seenFolders = new HashSet<string>(StringComparer.Ordinal);
        var byPrefix = configs
            .Where(c => c.Game.PrefixPath is not null)
            .GroupBy(c => PathIdentity.Canonicalize(c.Game.PrefixPath!), StringComparer.Ordinal);
        foreach (var sharing in byPrefix)
        {
            try
            {
                var prefixPath = sharing.First().Game.PrefixPath!;
                // Only store-library game folders; a library entry that has a library of its own is a launcher.
                var libraryGames = PrefixGameFolders.Find(Path.Combine(prefixPath, "drive_c"))
                    .Where(entry => entry.Library is not null && entry.Excluded is null)
                    .Select(entry => (Entry: entry, Folder: PathIdentity.Canonicalize(entry.Directory)))
                    .ToList();
                if (libraryGames.Count == 0) continue;
                var launcher = PickParentLauncher(sharing, libraryGames.Select(x => x.Folder).ToArray());
                // Only games share the prefix (no launcher config): nothing to launch the others through.
                if (launcher is null) continue;
                // The parent launcher is not a game: no patch target or graphics API of its own until the user
                // picks one in Edit game (Refs #148; a Battle.net/Ubisoft client exe is already caught by name).
                launcher.RequiresExplicitPatchTarget = true;
                foreach (var (entry, folder) in libraryGames)
                {
                    ct.ThrowIfCancellationRequested();
                    if (listedRoots.Any(root => IsSameOrUnder(root, folder)))
                        continue;
                    if (!seenFolders.Add(folder)) continue;
                    var exe = PrefixGameFolders.FindExecutable(entry, ct);
                    if (exe is null) continue;
                    found.Add(new DetectedGame
                    {
                        Name = Path.GetFileName(entry.Directory),
                        InstallPath = entry.Directory,
                        Source = "Lutris",
                        ExecutablePath = exe,
                        // The launcher is what Launch starts; the game exe stays the patch target.
                        LaunchExecutablePath = launcher.LaunchExecutablePath,
                        PrefixPath = launcher.PrefixPath,
                        Runner = launcher.Runner,
                        CompatibilityTool = launcher.CompatibilityTool,
                        CompatibilityLayerType = launcher.CompatibilityLayerType,
                        WineBinaryPath = launcher.WineBinaryPath,
                        WineServerPath = launcher.WineServerPath,
                        LaunchCommand = launcher.LaunchCommand,
                        ParentLauncherName = launcher.Name
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DetectorDiagnostics.Warn($"Games inside the Lutris launcher prefix {sharing.Key} not listed", ex);
            }
        }
        return found;
    }

    /// <summary>
    /// The launcher among the configs sharing one prefix, chosen by role: a config whose game folder is a
    /// store-library game folder of that prefix (or inside one) is a game with its own Lutris entry, never
    /// the parent. Of the rest, one whose configured exe resolves wins; ties keep ordinal config file-name
    /// order (slug, then timestamp, so the oldest of one slug's duplicate configs). Null when only games
    /// share the prefix.
    /// </summary>
    private static DetectedGame? PickParentLauncher(IEnumerable<(DetectedGame Game, bool ExeResolves)> sharing, IReadOnlyList<string> libraryGameFolders)
        => sharing
            .Where(c => !libraryGameFolders.Any(folder => IsSameOrUnder(PathIdentity.Canonicalize(c.Game.InstallPath), folder)))
            .OrderBy(c => c.ExeResolves ? 0 : 1) // stable: keeps file-name order within each group
            .Select(c => c.Game)
            .FirstOrDefault();

    private static bool IsSameOrUnder(string path, string folder)
        => path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal);

    /// <summary>
    /// Picks the game folder from the config's structured fields. A Windows exe keeps its own folder
    /// (launchers such as Battle.net set <c>working_dir</c> to an unrelated library folder). A native
    /// launcher (wrapper script, ELF) prefers <c>working_dir</c>, which is where Lutris runs it. Shared
    /// executable folders and whole Wine prefixes are never game folders; null means none was found.
    /// </summary>
    internal static string? ResolveInstallRoot(string? exe, string? workingDir, string? prefix, string home)
    {
        var exeDir = exe is null ? null : Path.GetDirectoryName(exe);
        var nativeLauncher = exe is not null && File.Exists(exe) && !IsWindowsExecutable(exe);
        var candidates = nativeLauncher
            ? new[] { workingDir, exeDir, prefix }
            : new[] { exeDir, prefix };
        // Shared executable folders, $HOME, / and a whole Wine prefix are not game directories: indexing a
        // prefix walks the entire Windows image and (through dosdevices symlinks) every mounted drive.
        return candidates.FirstOrDefault(dir => GameRootGuard.IsAcceptable(dir, home));
    }

    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectorDiagnostics.Warn($"Lutris exe {path} unreadable; treated as a native launcher", ex);
            return false;
        }
    }

    private static bool IsWithin(string root, string path)
        => NormalisePath(path).StartsWith(NormalisePath(root) + "/", StringComparison.Ordinal);

    private static string NormalisePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }


    private static bool LooksLikeProton(string? version)
        => !string.IsNullOrWhiteSpace(version)
           && (version.Contains("proton", StringComparison.OrdinalIgnoreCase)
               || version.Contains("umu", StringComparison.OrdinalIgnoreCase));

    private static string? ResolveLutrisWineBinary(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "system", StringComparison.OrdinalIgnoreCase))
            return FindOnPath("wine");

        var expanded = Expand(version);
        if (!string.IsNullOrWhiteSpace(expanded) && Path.IsPathRooted(expanded) && File.Exists(expanded))
            return Path.GetFullPath(expanded);

        foreach (var root in LutrisWineRunnerRoots())
        {
            var baseDir = Path.Combine(root, version);
            foreach (var relative in new[]
            {
                Path.Combine("bin", "wine"),
                Path.Combine("files", "bin", "wine"),
                Path.Combine("dist", "bin", "wine")
            })
            {
                var candidate = Path.Combine(baseDir, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> LutrisWineRunnerRoots()
    {
        yield return Path.Combine(LinuxPaths.DataHome, "lutris", "runners", "wine");
        yield return Path.Combine(LinuxPaths.Home, ".local", "share", "lutris", "runners", "wine");
        yield return Path.Combine(LinuxPaths.Home, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "wine");
    }

    private static string? ResolveMatchingWineServer(string? wineBinary)
    {
        if (string.IsNullOrWhiteSpace(wineBinary)) return null;
        var candidate = Path.Combine(Path.GetDirectoryName(wineBinary) ?? string.Empty, "wineserver");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string? Expand(string? value) => LutrisPathExpander.Expand(value, LinuxPaths.Home);

    /// <summary>
    /// Lutris stores installer-created games with an exe path relative to the prefix
    /// (e.g. <c>drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe</c>) and
    /// sometimes <c>$GAMEDIR</c> placeholders. Resolve those against the prefix / working
    /// directory so the install directory is the game folder, never the whole prefix.
    /// </summary>
    internal static string? ResolveRelativeExe(string? exe, string? prefix, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        if (exe.Contains("_xXx_", StringComparison.Ordinal)) return null; // Lutris auto-detect placeholder
        // $GAMEDIR left unexpanded (no pga.db directory): the rest is relative to the working dir or prefix.
        foreach (var token in new[] { "$GAMEDIR/", "${GAMEDIR}/" })
            if (exe.StartsWith(token, StringComparison.Ordinal))
                exe = exe[token.Length..];
        if (Path.IsPathRooted(exe)) return exe;

        foreach (var baseDir in new[] { workingDir, prefix })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            var candidate = Path.GetFullPath(Path.Combine(baseDir, exe));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Lutris names a game's config file <c>&lt;slug&gt;-&lt;unix timestamp&gt;.yml</c>; the slug is what
    /// <c>lutris:rungame/&lt;slug&gt;</c> expects. Using the full stem launches nothing ("No game found").
    /// </summary>
    internal static string ResolveLutrisSlug(string configStem)
    {
        var match = Regex.Match(configStem, @"^(?<slug>.+)-(?<ts>\d{9,11})$");
        return match.Success ? match.Groups["slug"].Value : configStem;
    }

    internal static bool IsWinePrefixRoot(string path)
        => Directory.Exists(Path.Combine(path, "drive_c")) ||
           Directory.Exists(Path.Combine(path, "dosdevices")) ||
           File.Exists(Path.Combine(path, "system.reg"));

    internal static (string? Version, string? LayerType) ParseWineRuntimeForTests(string text)
    {
        var version = LutrisConfigFile.Parse(text).Section("wine", "version");
        return (version, string.IsNullOrWhiteSpace(version) ? null : (LooksLikeProton(version) ? "proton" : "wine"));
    }
}
