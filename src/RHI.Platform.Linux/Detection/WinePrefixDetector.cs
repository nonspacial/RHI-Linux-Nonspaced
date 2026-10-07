using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>Finds standalone Wine prefixes not already represented by Steam/Heroic/Lutris/umu.</summary>
public sealed class WinePrefixDetector : IGameDetector
{
    private readonly string _home;
    private readonly Func<IEnumerable<string>> _lutrisConfigRoots;

    public WinePrefixDetector() : this(LinuxPaths.Home, LinuxPaths.LutrisConfigRoots) { }

    internal WinePrefixDetector(string home, IEnumerable<string> lutrisConfigRoots)
        : this(home, () => lutrisConfigRoots) { }

    private WinePrefixDetector(string home, Func<IEnumerable<string>> lutrisConfigRoots)
    {
        _home = home;
        _lutrisConfigRoots = lutrisConfigRoots;
    }

    public string Name => "Wine";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var roots = new[]
        {
            Path.Combine(_home, ".wine"),
            Path.Combine(_home, "Games"),
            Path.Combine(_home, ".local", "share", "bottles", "bottles")
        };
        var owned = LauncherOwnedPrefixes.Load(_home, _lutrisConfigRoots());
        var games = new List<DetectedGame>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var prefix in FindPrefixes(root, 3))
            {
                ct.ThrowIfCancellationRequested();

                // Steam, Heroic, Lutris and umu already have authoritative entries for these prefixes.
                // Walking them again duplicates games and turns support applications into fake entries.
                if (DetectionHelpers.IsLauncherManagedPrefix(prefix) || owned.Owns(prefix))
                    continue;

                var runtime = PrefixRuntime.Read(prefix);
                foreach (var entry in PrefixGameFolders.Find(Path.Combine(prefix, "drive_c")))
                {
                    ct.ThrowIfCancellationRequested();
                    var appDir = entry.Directory;
                    var exe = PrefixGameFolders.FindExecutable(entry, ct);
                    if (exe is null) continue;
                    games.Add(new DetectedGame
                    {
                        Name = Path.GetFileName(appDir),
                        InstallPath = appDir,
                        Source = "Wine",
                        PrefixPath = prefix,
                        ExecutablePath = exe,
                        LaunchExecutablePath = exe,
                        Runner = runtime.IsProton ? "Proton" : "Wine",
                        CompatibilityTool = runtime.Version,
                        CompatibilityLayerType = runtime.IsProton ? "proton" : null,
                        WineBinaryPath = runtime.WineBinary,
                        WineServerPath = runtime.WineServer,
                        LaunchCommand = BuildLaunchCommand(prefix, exe, runtime)
                    });
                }
            }
        }
        return games;
    }

    /// <summary>
    /// A Proton-built prefix is only started with that Proton's own Wine: host `wine` runs
    /// wineboot over it and can break it. Without that Wine the entry is listed but not launchable.
    /// </summary>
    private static string? BuildLaunchCommand(string prefix, string exe, PrefixRuntime runtime)
    {
        if (runtime.IsProton && runtime.WineBinary is null) return null;
        return $"WINEPREFIX={Shell(prefix)} {(runtime.WineBinary is null ? "wine" : Shell(runtime.WineBinary))} {Shell(exe)}";
    }

    private static IEnumerable<string> FindPrefixes(string root, int maxDepth)
    {
        if (IsPrefix(root))
        {
            yield return root;
            yield break;
        }
        var startDepth = root.Count(c => c == Path.DirectorySeparatorChar);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (IsPrefix(dir))
            {
                yield return dir;
                continue;
            }
            var depth = dir.Count(c => c == Path.DirectorySeparatorChar) - startDepth;
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // A symlinked folder is followed only when it is itself a prefix (a prefix kept on
                    // another disk); any other link could lead anywhere, / included.
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0 && !IsPrefix(sub)) continue;
                    pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder is skipped.
            }
        }
    }

    private static bool IsPrefix(string dir)
        => Directory.Exists(Path.Combine(dir, "drive_c")) && File.Exists(Path.Combine(dir, "system.reg"));

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Runtime that built a prefix. Proton/umu write `config_info` (line 1: version, line 2: the
    /// dist's share/fonts/) and `version`; plain Wine writes neither.
    /// </summary>
    internal sealed record PrefixRuntime(bool IsProton, string? Version, string? WineBinary, string? WineServer)
    {
        public static PrefixRuntime Read(string prefix)
        {
            // Steam-style layout keeps the markers beside `pfx/`.
            var dirs = Path.GetFileName(prefix) == "pfx" ? new[] { prefix, Path.GetDirectoryName(prefix)! } : [prefix];
            foreach (var dir in dirs)
            {
                var configInfo = ReadLines(Path.Combine(dir, "config_info"));
                var version = ReadLines(Path.Combine(dir, "version"));
                if (configInfo is null && version is null) continue;

                var name = configInfo?.FirstOrDefault()?.Trim() ?? version!.FirstOrDefault()?.Trim();
                string? wine = null, server = null;
                var fonts = configInfo?.ElementAtOrDefault(1)?.Trim();
                if (!string.IsNullOrEmpty(fonts) && Path.IsPathRooted(fonts))
                {
                    var bin = Path.GetFullPath(Path.Combine(fonts.TrimEnd('/'), "..", "..", "bin"));
                    wine = new[] { "wine", "wine64" }.Select(n => Path.Combine(bin, n)).FirstOrDefault(File.Exists);
                    var serverPath = Path.Combine(bin, "wineserver");
                    server = wine is not null && File.Exists(serverPath) ? serverPath : null;
                }
                return new PrefixRuntime(true, string.IsNullOrEmpty(name) ? null : name, wine, server);
            }
            return new PrefixRuntime(false, null, null, null);
        }

        private static string[]? ReadLines(string path)
        {
            try { return File.Exists(path) ? File.ReadAllLines(path) : null; }
            catch { return null; }
        }
    }
}
