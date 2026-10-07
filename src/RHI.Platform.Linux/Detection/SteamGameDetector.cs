using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

public sealed partial class SteamGameDetector : IGameDetector
{
    public string Name => "Steam";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private IReadOnlyList<DetectedGame> Detect(CancellationToken cancellationToken)
        => DetectIn(LinuxPaths.SteamRoots(), cancellationToken);

    /// <summary>
    /// Test seam: every library of the given Steam roots, each real library folder walked once however
    /// many roots or <c>libraryfolders.vdf</c> entries (symlinked or not) point at it.
    /// </summary>
    internal static IReadOnlyList<DetectedGame> DetectIn(IEnumerable<string> steamRoots, CancellationToken cancellationToken)
    {
        var games = new List<DetectedGame>();
        var seenLibraries = new HashSet<string>(StringComparer.Ordinal);
        var hostSteam = IsOnPath("steam");
        foreach (var steamRoot in LinuxPaths.DistinctExisting(steamRoots))
        {
            var libraries = FindLibraries(steamRoot).ToList();
            var classifier = SteamAppClassifier.Load(steamRoot, libraries);
            foreach (var library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenLibraries.Add(LinuxPaths.Canonicalise(library))) continue;
                var steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var fields = DetectionHelpers.ParseFlatVdf(File.ReadAllText(acf));
                        if (!fields.TryGetValue("name", out var name) || !fields.TryGetValue("installdir", out var installDir))
                            continue;
                        var appId = ExtractAppIdFromAcfFilename(Path.GetFileName(acf));
                        if (!SteamAppClassifier.IsFullyInstalled(fields.GetValueOrDefault("StateFlags")))
                            continue;
                        if (classifier.IsNotAGame(appId, name, installDir))
                            continue;
                        var gameRoot = Path.Combine(steamapps, "common", installDir);
                        // An empty installdir names steamapps/common itself.
                        if (!GameRootGuard.IsAcceptable(gameRoot)) continue;
                        var prefix = appId is int id ? Path.Combine(steamapps, "compatdata", id.ToString(), "pfx") : null;
                        if (prefix is not null && !Directory.Exists(prefix)) prefix = null;
                        // What Steam will actually run: the per-app compatibility tool, else the native build when
                        // the app ships one, else the global default tool. Unknown falls back to the prefix test.
                        var platform = classifier.Platform(appId);
                        if (platform == SteamAppClassifier.LaunchPlatform.Native) prefix = null;
                        var index = GameFileIndexer.Build(gameRoot, 8, cancellationToken);
                        var nativeExecutable = platform != SteamAppClassifier.LaunchPlatform.Windows && prefix is null
                            ? DetectionHelpers.FindLikelyNativeExecutable(gameRoot, index, cancellationToken)
                            : null;
                        var windowsExecutable = DetectionHelpers.FindLikelyExecutable(gameRoot, index, cancellationToken);

                        string? executable;
                        string runner;
                        if (nativeExecutable is not null)
                        {
                            executable = nativeExecutable;
                            runner = "Native Linux";
                        }
                        else
                        {
                            executable = windowsExecutable;
                            runner = prefix is not null
                                ? "Proton"
                                : windowsExecutable is not null
                                    ? "Windows/Proton (prefix not created)"
                                    : "Native/Unknown";
                        }

                        var game = new DetectedGame
                        {
                            Name = name,
                            InstallPath = gameRoot,
                            Source = "Steam",
                            SteamAppId = appId,
                            PrefixPath = prefix,
                            ExecutablePath = executable,
                            LaunchExecutablePath = executable,
                            Runner = runner,
                            CompatibilityTool = nativeExecutable is null ? classifier.CompatTool(appId) : null,
                            LaunchCommand = appId is int launchId ? LaunchCommandFor(launchId, steamRoot, hostSteam) : null
                        };
                        games.Add(game);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One unreadable manifest or game folder skips that app, not the whole Steam scan.
                        DetectorDiagnostics.Warn($"Steam manifest {acf} skipped", ex);
                    }
                }
            }
        }
        return games;
    }

    /// <summary>
    /// How to ask the Steam client that owns <paramref name="steamRoot"/> to start an app: Flatpak Steam
    /// through <c>flatpak run</c> (there is no host <c>steam</c> binary), host Steam through its binary,
    /// else the <c>steam://</c> URL handler. The root is judged by its spelling and by its real path, so a
    /// host symlink into <c>~/.var/app/com.valvesoftware.Steam</c> still reaches the Flatpak client.
    /// </summary>
    internal static string LaunchCommandFor(int appId, string steamRoot, bool hostSteamOnPath)
    {
        static bool IsFlatpak(string root)
            => (root.Replace('\\', '/').TrimEnd('/') + "/").Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal);
        if (IsFlatpak(steamRoot) || IsFlatpak(LinuxPaths.Canonicalise(steamRoot)))
            return $"flatpak run com.valvesoftware.Steam -applaunch {appId}";
        return hostSteamOnPath ? $"steam -applaunch {appId}" : $"xdg-open steam://rungameid/{appId}";
    }

    private static bool IsOnPath(string program)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, program)));

    public static int? ExtractAppIdFromAcfFilename(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        return stem.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(stem["appmanifest_".Length..], out var id) ? id : null;
    }

    private static IEnumerable<string> FindLibraries(string steamRoot)
    {
        var roots = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            var text = File.ReadAllText(vdf);
            foreach (Match match in LibraryPathRegex().Matches(text))
                roots.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }
        // The vdf names the root's own library too, often by another spelling of the same folder.
        return LinuxPaths.DistinctExisting(roots);
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
