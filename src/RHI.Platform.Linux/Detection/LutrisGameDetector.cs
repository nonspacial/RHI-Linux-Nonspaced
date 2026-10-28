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
        var games = new List<(DetectedGame Game, string Slug, string ConfigFile, bool ExeResolves, IReadOnlySet<string> NamedGames)>();
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
                    var detectedExecutable = exeExists && IsWithin(install, exe!) ? exe : DetectionHelpers.FindLikelyExecutable(install, cancellationToken: ct);

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
                    // A store client's per-game entry (Lutris' store services) names its game in its arguments.
                    games.Add((game, slug, file, exeExists, Launchers.StoreLauncherRecords.NamedGames(config.Section("game", "args"))));
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
            foreach (var (game, _, configFile, _, _) in sameFolder)
                game.LibraryIdentityPath ??= configFile;
        }
        var (found, ownEntries) = LauncherPrefixGames(games.Select(x => new LutrisEntry(x.Game, x.ExeResolves, x.NamedGames)).ToList(), home, ct);
        // A store's per-game entry that became its game's card is not listed again at the launcher's folder.
        return [.. games.Select(x => x.Game).Where(game => !ownEntries.Contains(game)), .. found];
    }

    /// <summary>One Lutris config's card, whether its exe resolves, and the games its arguments tell a store client to start.</summary>
    private sealed record LutrisEntry(DetectedGame Game, bool ExeResolves, IReadOnlySet<string> NamedGames);

    /// <summary>
    /// Games installed inside a listed launcher's prefix: first every game a store launcher recorded there (Ubisoft
    /// Connect's install keys, Battle.net's product.db, the EA app's and Rockstar's records, Refs #90; the Epic Games
    /// Launcher's, #247), wherever its folder is, then the store-library folders by layout (Ubisoft Connect's <c>games</c>
    /// folder, Battle.net's <c>drive_c/Games</c>, <c>EA Games</c>). One card per game folder, with the launcher's prefix
    /// and runner. Launch starts the game's own Lutris store entry when Lutris has one (a store service's per-game config:
    /// the store client with this game's start argument), else the store's own client entry, else the prefix's parent
    /// launcher; the user starts the game from there. An entry whose arguments start another game is never used: when the
    /// prefix's only store entries start other games, the game is still listed, with no launch command (w12 A8). A
    /// folder that is (or holds) another listed entry, such as the launcher itself or a game with its own Lutris config,
    /// is not listed again; neither is a folder with no game executable. Also returns the per-game store entries that
    /// became their game's card (not listed again at the launcher's folder).
    /// </summary>
    private static (List<DetectedGame> Found, HashSet<DetectedGame> OwnEntries) LauncherPrefixGames(IReadOnlyList<LutrisEntry> configs, string home, CancellationToken ct)
    {
        var found = new List<DetectedGame>();
        var ownEntries = new HashSet<DetectedGame>(ReferenceEqualityComparer.Instance);
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
                var recorded = Launchers.StoreLauncherRecords.Find(prefixPath, home, ct)
                    .Select(record => (Record: record, Folder: PathIdentity.Canonicalize(record.InstallDirectory)))
                    .ToList();
                // Only store-library game folders; a library entry that has a library of its own is a launcher.
                var libraryGames = PrefixGameFolders.Find(Path.Combine(prefixPath, "drive_c"))
                    .Where(entry => entry.Library is not null && entry.Excluded is null)
                    .Select(entry => (Entry: entry, Folder: PathIdentity.Canonicalize(entry.Directory)))
                    .ToList();
                if (libraryGames.Count == 0 && recorded.Count == 0) continue;
                var gameFolders = libraryGames.Select(x => x.Folder).Concat(recorded.Select(x => x.Folder)).ToArray();
                var fallback = PickParentLauncher(sharing, gameFolders);
                // w12 integration A8: the prefix's only store entries start one game each (no client entry, no parent):
                // a game with no entry of its own is still listed, with the prefix's runtime but no launch command (Play
                // then says to set one in Edit game), never with another game's entry; one scan warning per prefix.
                var perGameEntriesOnly = fallback is null && sharing.Any(c => c.NamedGames.Count > 0 &&
                    !gameFolders.Any(folder => IsSameOrUnder(PathIdentity.Canonicalize(c.Game.InstallPath), folder)));
                var runtime = sharing.OrderBy(c => c.ExeResolves ? 0 : 1).First().Game;
                var listedWithoutCommand = new List<string>(); // install paths (w12 final review m1)

                foreach (var (record, folder) in recorded)
                {
                    ct.ThrowIfCancellationRequested();
                    // The game's own store entry, else the store's own client when Lutris lists it in this prefix, else
                    // the prefix's parent launcher.
                    var own = OwnStoreEntries(sharing, record, gameFolders);
                    var launcher = own.FirstOrDefault() ?? StoreClientConfig(sharing, record.Store, gameFolders) ?? fallback;
                    if (launcher is null && !perGameEntriesOnly) continue;
                    if (launcher is not null) launcher.RequiresExplicitPatchTarget = true;
                    if (listedRoots.Any(root => IsSameOrUnder(root, folder)) || !seenFolders.Add(folder)) continue;
                    var exe = record.GameExecutable ?? PrefixGameFolders.FindExecutable(new PrefixGameFolders.Entry(record.InstallDirectory, null, null), ct);
                    if (exe is null) continue;
                    if (launcher is null)
                    {
                        var listedGame = WithoutLaunchCommand(runtime, record.Name, record.InstallDirectory, exe);
                        found.Add(listedGame);
                        listedWithoutCommand.Add(listedGame.InstallPath);
                        continue;
                    }
                    var game = LauncherPrefixGame(launcher, own.Count > 0 ? launcher.Name : record.Name, record.InstallDirectory, exe);
                    // Its own entry starts the game itself (the store client's start argument): no "start it there".
                    if (own.Count > 0) game.ParentLauncherName = null;
                    found.Add(game);
                    ownEntries.UnionWith(own);
                }

                // Only games share the prefix (no launcher config, no store entry): nothing to launch the others
                // through, so the store-library folders without an entry of their own are not listed. A prefix whose
                // store entries each start one game (A8) still lists them, without a launch command.
                if (fallback is null && !perGameEntriesOnly) continue;
                // The parent launcher is not a game: no patch target or graphics API of its own until the user
                // picks one in Edit game (Refs #148; a Battle.net/Ubisoft client exe is already caught by name).
                if (libraryGames.Count > 0 && fallback is not null) fallback.RequiresExplicitPatchTarget = true;
                foreach (var (entry, folder) in libraryGames)
                {
                    ct.ThrowIfCancellationRequested();
                    if (listedRoots.Any(root => IsSameOrUnder(root, folder)))
                        continue;
                    if (!seenFolders.Add(folder)) continue;
                    var exe = PrefixGameFolders.FindExecutable(entry, ct);
                    if (exe is null) continue;
                    if (fallback is null)
                    {
                        var listed = WithoutLaunchCommand(runtime, Path.GetFileName(entry.Directory), entry.Directory, exe);
                        found.Add(listed);
                        listedWithoutCommand.Add(listed.InstallPath);
                        continue;
                    }
                    found.Add(LauncherPrefixGame(fallback, Path.GetFileName(entry.Directory), entry.Directory, exe));
                }
                // w12 follow-up (merge review nit 1): its own plain line in the scan notice, never counted as skipped.
                DetectorDiagnostics.ListedWithoutLaunchCommand(listedWithoutCommand,
                    $"Lutris prefix {prefixPath}: {listedWithoutCommand.Count} game(s) listed with no launch command " +
                    "(the prefix's store entries each start another game, and Lutris lists no entry for the store client itself)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DetectorDiagnostics.Warn($"Games inside the Lutris launcher prefix {sharing.Key} not listed", ex);
            }
        }
        return (found, ownEntries);
    }

    /// <summary>
    /// w12 integration A8: a game in a prefix whose store entries each start another game: the prefix's runtime (a
    /// config's prefix, runner and Wine fields — copies of the store client's), its own exe, but no launch command and no
    /// parent launcher, so Play says to set one in Edit game (as Heroic lists a store record without its client).
    /// </summary>
    private static DetectedGame WithoutLaunchCommand(DetectedGame runtime, string name, string folder, string exe) => new()
    {
        Name = name,
        InstallPath = folder,
        Source = "Lutris",
        ExecutablePath = exe,
        LaunchExecutablePath = exe,
        PrefixPath = runtime.PrefixPath,
        Runner = runtime.Runner,
        CompatibilityTool = runtime.CompatibilityTool,
        CompatibilityLayerType = runtime.CompatibilityLayerType,
        WineBinaryPath = runtime.WineBinaryPath,
        WineServerPath = runtime.WineServerPath,
        LaunchCommand = null,
        ParentLauncherName = null
    };

    private static DetectedGame LauncherPrefixGame(DetectedGame launcher, string name, string folder, string exe) => new()
    {
        Name = name,
        InstallPath = folder,
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
    };

    /// <summary>
    /// Configs in this prefix whose executable is a store client and is no game folder of the prefix (the store's
    /// entries, as Lutris' store services write them: a copy of the client's config, plus a start argument per game).
    /// </summary>
    private static IEnumerable<LutrisEntry> StoreEntries(IEnumerable<LutrisEntry> sharing, Launchers.StoreLauncher store, IReadOnlyList<string> gameFolders)
    {
        var provider = Launchers.StoreLauncherRecords.ProviderName(store);
        return sharing
            .Where(c => !gameFolders.Any(folder => IsSameOrUnder(PathIdentity.Canonicalize(c.Game.InstallPath), folder)))
            .Where(c => string.Equals(Settings.LinuxGameIdentityService.InferProviderFromExecutable(c.Game.LaunchExecutablePath), provider, StringComparison.Ordinal) ||
                        string.Equals(Settings.LinuxGameIdentityService.InferProviderFromExecutable(c.Game.ExecutablePath), provider, StringComparison.Ordinal))
            .OrderBy(c => c.ExeResolves ? 0 : 1);
    }

    /// <summary>The store's entries that start this record's game (its start argument names the game the record does).</summary>
    private static List<DetectedGame> OwnStoreEntries(IEnumerable<LutrisEntry> sharing, Launchers.StoreLauncherGame record, IReadOnlyList<string> gameFolders)
    {
        var names = Launchers.StoreLauncherRecords.NamedGames(record.PlayArguments);
        return StoreEntries(sharing, record.Store, gameFolders).Where(c => c.NamedGames.Overlaps(names)).Select(c => c.Game).ToList();
    }

    /// <summary>
    /// The Lutris entry for one store's own client in a prefix (its executable is that store's launcher, its folder is
    /// no game folder of the prefix, and its arguments start no game), so a prefix holding two store clients starts each
    /// game through its own store, and a per-game store entry never starts another game.
    /// </summary>
    private static DetectedGame? StoreClientConfig(IEnumerable<LutrisEntry> sharing, Launchers.StoreLauncher store, IReadOnlyList<string> gameFolders)
        => StoreEntries(sharing, store, gameFolders)
            .Where(c => c.NamedGames.Count == 0)
            .Select(c => c.Game)
            .FirstOrDefault();

    /// <summary>
    /// The launcher among the configs sharing one prefix, chosen by role: a config whose game folder is a
    /// store-library game folder of that prefix (or inside one) is a game with its own Lutris entry, never
    /// the parent; nor is a store's per-game entry (its arguments start one game). Of the rest, a store client RHI
    /// knows by its executable (GOG Galaxy, Ubisoft Connect, Battle.net, the EA app…) comes first, so a game installed
    /// outside the store libraries in the same prefix (a GOG offline install beside Galaxy) is not taken for the
    /// launcher; then one whose configured exe resolves; ties keep ordinal config file-name order (slug, then
    /// timestamp, so the oldest of one slug's duplicate configs). Null when only games share the prefix. Known
    /// limitation: with no known client among them, a game outside the libraries can still be picked over an unknown launcher.
    /// </summary>
    private static DetectedGame? PickParentLauncher(IEnumerable<LutrisEntry> sharing, IReadOnlyList<string> libraryGameFolders)
        => sharing
            .Where(c => !libraryGameFolders.Any(folder => IsSameOrUnder(PathIdentity.Canonicalize(c.Game.InstallPath), folder)))
            .Where(c => c.NamedGames.Count == 0)
            // Stable sorts: file-name order is kept within each group.
            .OrderBy(c => IsKnownStoreClient(c.Game) ? 0 : 1)
            .ThenBy(c => c.ExeResolves ? 0 : 1)
            .Select(c => c.Game)
            .FirstOrDefault();

    private static bool IsKnownStoreClient(DetectedGame config)
        => Settings.LinuxGameIdentityService.IsSharedLauncherExecutable(config.LaunchExecutablePath) ||
           Settings.LinuxGameIdentityService.IsSharedLauncherExecutable(config.ExecutablePath);

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
