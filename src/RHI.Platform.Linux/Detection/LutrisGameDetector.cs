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
        var games = new List<DetectedGame>();
        foreach (var configRoot in LinuxPaths.LutrisConfigRoots())
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
                    string? install = exe is null ? null : Path.GetDirectoryName(exe);
                    if (install is null || !Directory.Exists(install))
                    {
                        if (prefix is not null && Directory.Exists(prefix)) install = prefix;
                        else continue;
                    }
                    // A whole Wine prefix is not a game directory. Indexing it walks the entire
                    // Windows image and (through dosdevices symlinks) every mounted drive.
                    if (IsWinePrefixRoot(install)) continue;
                    var slug = Path.GetFileNameWithoutExtension(file);
                    var configuredRunner = MatchValue(text, "runner");
                    var wineVersion = MatchSectionValue(text, "wine", "version");
                    var hasWineRuntime = string.Equals(configuredRunner, "wine", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(wineVersion)
                        || !string.IsNullOrWhiteSpace(prefix);
                    var layerType = hasWineRuntime
                        ? (LooksLikeProton(wineVersion) ? "proton" : "wine")
                        : null;
                    var wineBinary = hasWineRuntime ? ResolveLutrisWineBinary(wineVersion) : null;
                    var detectedExecutable = File.Exists(exe) ? exe : DetectionHelpers.FindLikelyExecutable(install);

                    var game = new DetectedGame
                    {
                        Name = name,
                        InstallPath = install,
                        Source = "Lutris",
                        ExecutablePath = detectedExecutable,
                        LaunchExecutablePath = detectedExecutable,
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
                    games.Add(game);
                }
                catch { }
            }
        }
        return games;
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
