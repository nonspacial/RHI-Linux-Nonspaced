using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>Finds standalone Wine prefixes not already represented by Steam/Heroic/Lutris.</summary>
public sealed class WinePrefixDetector : IGameDetector
{
    public string Name => "Wine";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private static IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var roots = new[]
        {
            Path.Combine(LinuxPaths.Home, ".wine"),
            Path.Combine(LinuxPaths.Home, "Games"),
            Path.Combine(LinuxPaths.Home, ".local", "share", "bottles", "bottles")
        };
        var games = new List<DetectedGame>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var prefix in FindPrefixes(root, 3))
            {
                ct.ThrowIfCancellationRequested();

                // Steam and Heroic already have authoritative detectors. Walking their prefixes
                // again turns Windows support applications into fake game entries.
                if (DetectionHelpers.IsLauncherManagedPrefix(prefix))
                    continue;

                var driveC = Path.Combine(prefix, "drive_c");
                var candidates = new[] { Path.Combine(driveC, "Program Files"), Path.Combine(driveC, "Program Files (x86)") }
                    .Where(Directory.Exists);
                foreach (var baseDir in candidates)
                {
                    string[] appDirs;
                    try { appDirs = Directory.GetDirectories(baseDir); } catch { continue; }
                    foreach (var appDir in appDirs)
                    {
                        var appName = Path.GetFileName(appDir);
                        if (DetectionHelpers.IsKnownNonGameWineDirectory(appName))
                            continue;

                        var exe = DetectionHelpers.FindLikelyExecutable(appDir);
                        if (exe is null) continue;
                        var game = new DetectedGame
                        {
                            Name = appName,
                            InstallPath = appDir,
                            Source = "Wine",
                            PrefixPath = prefix,
                            ExecutablePath = exe,
                            LaunchExecutablePath = exe,
                            Runner = "Wine",
                            LaunchCommand = $"WINEPREFIX={Shell(prefix)} wine {Shell(exe)}"
                        };
                        games.Add(game);
                    }
                }
            }
        }
        return games;
    }

    private static IEnumerable<string> FindPrefixes(string root, int maxDepth)
    {
        if (Directory.Exists(Path.Combine(root, "drive_c")) && File.Exists(Path.Combine(root, "system.reg")))
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
            if (Directory.Exists(Path.Combine(dir, "drive_c")) && File.Exists(Path.Combine(dir, "system.reg")))
            {
                yield return dir;
                continue;
            }
            var depth = dir.Count(c => c == Path.DirectorySeparatorChar) - startDepth;
            if (depth >= maxDepth) continue;
            try { foreach (var sub in Directory.EnumerateDirectories(dir)) pending.Push(sub); }
            catch { }
        }
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
