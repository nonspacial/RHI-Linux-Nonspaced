using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Tolerant Heroic detector. Heroic has changed its cache/config layout and JSON schema
/// several times, so this reads both Heroic-managed metadata and Legendary's authoritative
/// installed.json locations instead of assuming all Epic metadata lives below ~/.config/heroic.
/// </summary>
public sealed class HeroicGameDetector : IGameDetector
{
    public string Name => "Heroic";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var result = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);
        var visitedFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var heroicRoot in LinuxPaths.HeroicRoots())
        {
            foreach (var file in SafeJsonFiles(heroicRoot))
            {
                ct.ThrowIfCancellationRequested();
                ParseFile(file, result, StoreHintFromPath(file), visitedFiles);
            }
        }

        // Epic/Legendary is the important special case: on native Heroic this is commonly
        // ~/.config/legendary/installed.json, i.e. outside HeroicRoots(). Newer Heroic builds
        // can instead use heroic/legendaryConfig/legendary/installed.json. Support both.
        foreach (var legendaryRoot in LinuxPaths.HeroicLegendaryRoots())
        {
            ct.ThrowIfCancellationRequested();
            var installed = Path.Combine(legendaryRoot, "installed.json");
            ParseFile(installed, result, "legendary", visitedFiles);
        }

        // Prefer exact prefix data from per-game Heroic settings when available, then fall
        // back to a conservative ~/Games/Heroic/Prefixes name match.
        AttachConfiguredPrefixes(result.Values);
        AttachFallbackPrefixes(result.Values.ToList(), LinuxPaths.HeroicPrefixRoots());

        return result.Values.ToArray();
    }

    private static void ParseFile(
        string file,
        IDictionary<string, DetectedGame> result,
        string? sourceHint,
        ISet<string>? visitedFiles = null)
    {
        if (!File.Exists(file)) return;
        var full = Path.GetFullPath(file);
        if (visitedFiles != null && !visitedFiles.Add(full)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Visit(doc.RootElement, result, sourceHint);
        }
        catch
        {
            // Heroic caches are not all game metadata and some can be transient/truncated.
            // A malformed optional cache should not abort the complete library scan.
        }
    }

    private static IEnumerable<string> SafeJsonFiles(string root)
    {
        foreach (var file in HeroicMetadataFileCandidates(root))
            if (File.Exists(file)) yield return file;

        var likely = new[]
        {
            "store_cache",
            "gog_store",
            "legendaryConfig",
            "nile_config",
            "GamesConfig",
            "sideload_apps",
            Path.Combine("store", "sideload_apps")
        };
        foreach (var key in likely)
        {
            var path = Path.Combine(root, key);
            if (!Directory.Exists(path)) continue;
            IEnumerable<string> files;
            try { files = SafeFileEnumeration.Files(path, "*.json", ignoreInaccessible: true).Take(500).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private static IEnumerable<string> HeroicMetadataFileCandidates(string root)
    {
        yield return Path.Combine(root, "installed.json");
        yield return Path.Combine(root, "gog_store", "installed.json");
        // Current Heroic sideload store (electron-store cwd='sideload_apps', name='library').
        yield return Path.Combine(root, "sideload_apps", "library.json");
        // Older/alternate layout retained for compatibility.
        yield return Path.Combine(root, "store", "sideload_apps", "library.json");
    }

    private static string? StoreHintFromPath(string file)
    {
        var normalized = file.Replace('\\', '/');
        if (normalized.Contains("/legendaryConfig/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/legendary/installed.json", StringComparison.OrdinalIgnoreCase))
            return "legendary";
        if (normalized.Contains("/gog_store/", StringComparison.OrdinalIgnoreCase))
            return "gog";
        if (normalized.Contains("/nile_config/", StringComparison.OrdinalIgnoreCase))
            return "nile";
        if (normalized.Contains("/sideload_apps/", StringComparison.OrdinalIgnoreCase))
            return "sideload";
        return null;
    }

    private static void Visit(JsonElement element, IDictionary<string, DetectedGame> result, string? sourceHint)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var props = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

            // Installed records are usually flat (Legendary/GOG installed.json). Some Heroic
            // library caches nest these fields in an "install" object, so merge that object for
            // detection while keeping the parent title/app_name fields.
            var merged = new Dictionary<string, JsonElement>(props, StringComparer.OrdinalIgnoreCase);
            if (props.TryGetValue("install", out var installObject) && installObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in installObject.EnumerateObject())
                    if (!merged.ContainsKey(p.Name)) merged[p.Name] = p.Value;
            }

            var executable = StringValue(merged, "executable", "exe");
            var install = StringValue(merged,
                "install_path", "installPath", "installLocation", "path",
                // Heroic sideload records keep the executable in install.executable and
                // its containing directory in the parent folder_name field.
                "folder_name", "folderName");
            if (string.IsNullOrWhiteSpace(install) && !string.IsNullOrWhiteSpace(executable))
            {
                var normalizedExecutable = executable.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalizedExecutable))
                    install = Path.GetDirectoryName(normalizedExecutable);
            }

            var name = StringValue(merged, "title", "name", "app_title", "appTitle");
            var isInstalled = BoolValue(merged, "is_installed", "isInstalled");
            // GOG and sideloaded games can be native Linux builds: they never run in a Wine prefix.
            var nativeLinux = string.Equals(StringValue(merged, "platform", "install_platform")?.Trim(), "linux", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(install)
                && !string.IsNullOrWhiteSpace(name)
                // A record pointing at $HOME, / or a whole prefix (a sideload exe dropped there) is not a game folder.
                && GameRootGuard.IsAcceptable(install)
                && isInstalled != false)
            {
                var explicitRunner = StringValue(merged, "runner", "store");
                var runner = NormalizeRunner(explicitRunner ?? sourceHint ?? "heroic");
                var appName = StringValue(merged, "app_name", "appName", "app_name_string");
                var executablePath = ResolveExecutable(install, executable)
                    ?? DetectionHelpers.FindLikelyExecutable(install);

                var game = new DetectedGame
                {
                    Name = name,
                    InstallPath = Path.GetFullPath(install),
                    Source = DisplaySource(runner),
                    EpicAppName = runner == "legendary" ? appName : null,
                    HeroicAppName = appName,
                    ExecutablePath = executablePath,
                    LaunchExecutablePath = executablePath,
                    Runner = nativeLinux ? NativeRunner : "Wine/Proton (Heroic)",
                    LaunchCommand = !string.IsNullOrWhiteSpace(appName)
                        ? $"xdg-open {EscapeShell(BuildHeroicUri(appName, runner))}"
                        : null
                };
                result[game.InstallPath] = game;
            }

            foreach (var p in props.Values) Visit(p, result, sourceHint);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Visit(item, result, sourceHint);
        }
    }

    private static string? ResolveExecutable(string installPath, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var normalized = executable.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.IsPathRooted(normalized) ? normalized : Path.Combine(installPath, normalized);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? StringValue(Dictionary<string, JsonElement> props, params string[] keys)
    {
        foreach (var key in keys)
            if (props.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static bool? BoolValue(Dictionary<string, JsonElement> props, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!props.TryGetValue(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    private static void AttachConfiguredPrefixes(IEnumerable<DetectedGame> games)
    {
        var byApp = games
            .Select(g => (game: g, appName: g.HeroicAppName ?? g.EpicAppName))
            .Where(x => !string.IsNullOrWhiteSpace(x.appName))
            .GroupBy(x => x.appName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().game, StringComparer.OrdinalIgnoreCase);
        if (byApp.Count == 0) return;

        foreach (var heroicRoot in LinuxPaths.HeroicRoots())
        {
            var settingsDir = Path.Combine(heroicRoot, "GamesConfig");
            if (!Directory.Exists(settingsDir)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(settingsDir, "*.json", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    ExtractConfiguredPrefix(doc.RootElement, byApp);
                }
                catch { }
            }
        }
    }

    private static void ExtractConfiguredPrefix(JsonElement element, IDictionary<string, DetectedGame> byApp)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object
                || !byApp.TryGetValue(property.Name, out var game)
                || IsNative(game)) // a native Linux build never runs in a Wine prefix
                continue;

            var props = property.Value.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

            var prefix = ExpandHome(StringValue(props, "winePrefix", "wine_prefix", "prefix"));
            if (!string.IsNullOrWhiteSpace(prefix) && Directory.Exists(prefix))
                game.PrefixPath = FindWinePrefix(prefix);

            if (!props.TryGetValue("wineVersion", out var wineVersion))
                continue;

            if (wineVersion.ValueKind == JsonValueKind.String)
            {
                game.CompatibilityTool = wineVersion.GetString();
                continue;
            }

            if (wineVersion.ValueKind != JsonValueKind.Object)
                continue;

            var wineProps = wineVersion.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var type = StringValue(wineProps, "type")?.Trim().ToLowerInvariant();
            var name = StringValue(wineProps, "name");
            var configuredBin = ExpandHome(StringValue(wineProps, "bin"));
            var configuredWineServer = ExpandHome(StringValue(wineProps, "wineserver"));

            game.CompatibilityLayerType = type;
            game.CompatibilityTool = name ?? game.CompatibilityTool;

            var wineBin = ResolveHeroicWineBinary(configuredBin, type);
            if (!string.IsNullOrWhiteSpace(wineBin))
            {
                game.WineBinaryPath = wineBin;
                game.WineServerPath = ResolveMatchingWineServer(wineBin, configuredWineServer);
            }

            game.Runner = type switch
            {
                "proton" => "Heroic Proton",
                "wine" => "Heroic Wine",
                "crossover" => "Heroic CrossOver",
                "toolkit" => "Heroic Toolkit",
                _ => game.Runner
            };
        }
    }

    private static string? ResolveHeroicWineBinary(string? configuredBin, string? type)
    {
        if (string.IsNullOrWhiteSpace(configuredBin)) return null;
        configuredBin = Path.GetFullPath(configuredBin);

        if (string.Equals(type, "proton", StringComparison.OrdinalIgnoreCase))
        {
            var baseDir = Path.GetDirectoryName(configuredBin);
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                foreach (var dist in new[] { "files", "dist" })
                {
                    var candidate = Path.Combine(baseDir, dist, "bin", "wine");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        return File.Exists(configuredBin) ? configuredBin : null;
    }

    private static string? ResolveMatchingWineServer(string wineBin, string? configuredWineServer)
    {
        if (!string.IsNullOrWhiteSpace(configuredWineServer) && File.Exists(configuredWineServer))
            return Path.GetFullPath(configuredWineServer);

        var sibling = Path.Combine(Path.GetDirectoryName(wineBin) ?? string.Empty, "wineserver");
        return File.Exists(sibling) ? sibling : null;
    }

    private static string? ExpandHome(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value == "~") return LinuxPaths.Home;
        if (value.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(LinuxPaths.Home, value[2..]);
        return Environment.ExpandEnvironmentVariables(value);
    }

    private const string NativeRunner = "Native Linux";

    private static bool IsNative(DetectedGame game) => string.Equals(game.Runner, NativeRunner, StringComparison.Ordinal);

    /// <summary>
    /// Name fallback for games without a configured prefix. A prefix's settings drive registry writes
    /// (DLL overrides), so a wrong match damages another game: the folder name must equal the title once
    /// normalised, or be within a 0.9 edit-distance ratio of it; a tie between folders, or two games
    /// claiming one folder equally, attaches nothing; a folder already in use is never reused; native
    /// Linux games get none.
    /// </summary>
    internal static void AttachFallbackPrefixes(IReadOnlyList<DetectedGame> games, IEnumerable<string> prefixRoots)
    {
        var taken = new HashSet<string>(
            games.Where(g => !string.IsNullOrWhiteSpace(g.PrefixPath)).Select(g => LauncherOwnedPrefixes.Canonicalise(g.PrefixPath!)),
            StringComparer.Ordinal);
        var folders = new List<(string Prefix, string Name)>();
        foreach (var root in prefixRoots)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs)
            {
                var prefix = FindWinePrefix(dir);
                if (!Directory.Exists(Path.Combine(prefix, "drive_c")) || taken.Contains(LauncherOwnedPrefixes.Canonicalise(prefix)))
                    continue;
                folders.Add((prefix, Normalize(Path.GetFileName(dir))));
            }
        }

        var claims = new List<(DetectedGame Game, string Prefix, double Score)>();
        foreach (var game in games.Where(g => string.IsNullOrWhiteSpace(g.PrefixPath) && !IsNative(g)))
        {
            var title = Normalize(game.Name);
            var scored = folders
                .Select(folder => (folder.Prefix, Score: Similarity(title, folder.Name)))
                .Where(x => x.Score >= 0.9)
                .OrderByDescending(x => x.Score)
                .ToList();
            if (scored.Count == 0) continue;
            if (scored.Count > 1 && scored[1].Score == scored[0].Score) continue; // two folders fit equally well
            claims.Add((game, scored[0].Prefix, scored[0].Score));
        }

        foreach (var contested in claims.GroupBy(c => LauncherOwnedPrefixes.Canonicalise(c.Prefix), StringComparer.Ordinal))
        {
            var ranked = contested.OrderByDescending(c => c.Score).ToList();
            if (ranked.Count > 1 && ranked[1].Score == ranked[0].Score) continue; // two games fit equally well
            ranked[0].Game.PrefixPath = ranked[0].Prefix;
        }
    }

    private static string FindWinePrefix(string root)
    {
        if (Directory.Exists(Path.Combine(root, "drive_c"))) return root;
        var pfx = Path.Combine(root, "pfx");
        return Directory.Exists(Path.Combine(pfx, "drive_c")) ? pfx : root;
    }

    private static string NormalizeRunner(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "epic" or "egs" => "legendary",
            "amazon" => "nile",
            _ => normalized
        };
    }

    private static string DisplaySource(string runner) => runner switch
    {
        "legendary" => "Heroic/Epic",
        "gog" => "Heroic/GOG",
        "nile" => "Heroic/Amazon",
        "sideload" => "Heroic/Sideload",
        _ => "Heroic"
    };

    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    /// <summary>1 − Levenshtein distance / longer length, on normalised names ("hades" vs "heads" = 0.6).</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static string BuildHeroicUri(string appName, string runner)
        => $"heroic://launch?appName={Uri.EscapeDataString(appName)}&runner={Uri.EscapeDataString(runner)}";

    private static string EscapeShell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    // Internal hooks for focused tests; production uses the same parser/path candidate code.
    internal static IReadOnlyList<DetectedGame> ParseInstalledJsonForTests(string file, string sourceHint)
    {
        var result = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);
        ParseFile(file, result, sourceHint);
        return result.Values.ToArray();
    }

    internal static IReadOnlyList<string> HeroicMetadataFileCandidatesForTests(string heroicRoot)
        => HeroicMetadataFileCandidates(heroicRoot).ToArray();

    internal static void ApplyConfiguredPrefixJsonForTests(string json, DetectedGame game)
    {
        var appName = game.HeroicAppName ?? game.EpicAppName;
        if (string.IsNullOrWhiteSpace(appName)) return;
        var byApp = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase)
        {
            [appName] = game
        };
        using var doc = JsonDocument.Parse(json);
        ExtractConfiguredPrefix(doc.RootElement, byApp);
    }
}
