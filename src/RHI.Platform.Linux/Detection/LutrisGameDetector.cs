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
        => DetectIn(LinuxPaths.LutrisConfigRoots(), LinuxPaths.Home, ct);

    internal static IReadOnlyList<DetectedGame> DetectIn(IEnumerable<string> configRoots, string home, CancellationToken ct)
    {
        var games = new List<(DetectedGame Game, string Slug, string ConfigFile)>();
        foreach (var configRoot in configRoots)
        {
            foreach (var file in Directory.EnumerateFiles(configRoot, "*.yml").Concat(Directory.EnumerateFiles(configRoot, "*.yaml")))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = File.ReadAllText(file);
                    var name = MatchValue(text, "name") ?? Path.GetFileNameWithoutExtension(file).Replace('-', ' ');
                    var exe = MatchValue(text, "exe");
                    var prefix = MatchValue(text, "prefix");
                    var workingDir = MatchValue(text, "working_dir");
                    exe = Expand(exe);
                    prefix = Expand(prefix);
                    workingDir = Expand(workingDir);
                    exe = ResolveRelativeExe(exe, prefix, workingDir);
                    var install = ResolveInstallRoot(exe, workingDir, prefix, home);
                    var launchOnly = install is null;
                    if (launchOnly)
                    {
                        // No real game folder (e.g. a wrapper script in ~/.local/bin driving a whole prefix).
                        // Keep it launchable through Lutris when its launcher exists, anchored on the Lutris
                        // config folder, and require an explicit patch target before anything is probed or
                        // deployed. A config whose launcher is gone stays hidden, as before.
                        if (exe is null || !File.Exists(exe)) continue;
                        install = configRoot;
                    }
                    var slug = ResolveLutrisSlug(Path.GetFileNameWithoutExtension(file));
                    var configuredRunner = MatchValue(text, "runner");
                    var wineVersion = MatchSectionValue(text, "wine", "version");
                    var hasWineRuntime = string.Equals(configuredRunner, "wine", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(wineVersion)
                        || !string.IsNullOrWhiteSpace(prefix);
                    var layerType = hasWineRuntime
                        ? (LooksLikeProton(wineVersion) ? "proton" : "wine")
                        : null;
                    var wineBinary = hasWineRuntime ? ResolveLutrisWineBinary(wineVersion) : null;
                    var exeExists = exe is not null && File.Exists(exe);
                    string? detectedExecutable = launchOnly
                        ? null
                        : exeExists && IsWithin(install!, exe!) ? exe : DetectionHelpers.FindLikelyExecutable(install!);

                    var game = new DetectedGame
                    {
                        Name = name,
                        InstallPath = install!,
                        Source = "Lutris",
                        ExecutablePath = detectedExecutable,
                        LaunchExecutablePath = exeExists ? exe : detectedExecutable,
                        LibraryIdentityPath = launchOnly ? file : null,
                        RequiresExplicitPatchTarget = launchOnly,
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
                    games.Add((game, slug, file));
                }
                catch { }
            }
        }

        // Distinct Lutris games that resolve to one folder must not share a library identity
        // (favourites, edits, probe cache) or merge into one card. Duplicate configs of the same
        // slug (e.g. two Battle.net installs into one prefix) still share it, as before.
        foreach (var sameFolder in games.GroupBy(x => NormalisePath(x.Game.InstallPath), StringComparer.Ordinal))
        {
            if (sameFolder.Select(x => x.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
            foreach (var (game, _, configFile) in sameFolder)
                game.LibraryIdentityPath ??= configFile;
        }
        return games.Select(x => x.Game).ToArray();
    }

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
        return candidates.FirstOrDefault(dir =>
            !string.IsNullOrWhiteSpace(dir) &&
            Directory.Exists(dir) &&
            !IsSharedDirectory(dir, home) &&
            // A whole Wine prefix is not a game directory. Indexing it walks the entire
            // Windows image and (through dosdevices symlinks) every mounted drive.
            !IsWinePrefixRoot(dir));
    }

    /// <summary>
    /// Folders that hold executables for many programs (or everything): a wrapper living there says
    /// nothing about where the game is, and deploying DLLs there would pollute PATH.
    /// </summary>
    internal static bool IsSharedDirectory(string path, string home)
    {
        var normalised = NormalisePath(path);
        string[] shared =
        [
            "/", "/bin", "/sbin", "/usr/bin", "/usr/sbin", "/usr/local/bin", "/usr/local/sbin",
            "/usr/games", "/usr/local/games",
            NormalisePath(home), NormalisePath(Path.Combine(home, "bin")), NormalisePath(Path.Combine(home, ".local", "bin"))
        ];
        if (shared.Contains(normalised, StringComparer.Ordinal)) return true;
        try
        {
            var target = new DirectoryInfo(normalised).ResolveLinkTarget(returnFinalTarget: true);
            return target is not null && shared.Contains(NormalisePath(target.FullName), StringComparer.Ordinal);
        }
        catch { return false; }
    }

    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static bool IsWithin(string root, string path)
        => NormalisePath(path).StartsWith(NormalisePath(root) + "/", StringComparison.Ordinal);

    private static string NormalisePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }


    private static string? MatchSectionValue(string text, string section, string key)
    {
        var sectionMatch = Regex.Match(text, $@"(?m)^(?<indent>[ \t]*){Regex.Escape(section)}\s*:\s*$");
        if (!sectionMatch.Success) return null;

        var sectionIndent = sectionMatch.Groups["indent"].Value.Length;
        var tailStart = sectionMatch.Index + sectionMatch.Length;
        var tail = text.Substring(tailStart);
        foreach (var rawLine in tail.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var indent = rawLine.TakeWhile(char.IsWhiteSpace).Count();
            if (indent <= sectionIndent) break;
            var trimmed = rawLine.Trim();
            var match = Regex.Match(trimmed, $"^{Regex.Escape(key)}\\s*:\\s*(?<v>.+)$");
            if (match.Success) return match.Groups["v"].Value.Trim().Trim('"', '\'');
        }
        return null;
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

    private static string? MatchValue(string text, string key)
    {
        var pattern = $@"(?m)^\s*{Regex.Escape(key)}\s*:\s*[""']?(?<v>[^\r\n""']+)";
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string? Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("~/")) return Path.Combine(LinuxPaths.Home, value[2..]);
        return Environment.ExpandEnvironmentVariables(value);
    }

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
        if (exe.StartsWith("$GAMEDIR/", StringComparison.Ordinal))
            exe = exe["$GAMEDIR/".Length..];
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
        var version = MatchSectionValue(text, "wine", "version");
        return (version, string.IsNullOrWhiteSpace(version) ? null : (LooksLikeProton(version) ? "proton" : "wine"));
    }
}
