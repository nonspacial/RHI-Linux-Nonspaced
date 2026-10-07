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

    /// <summary>Test seam: scans the given Steam roots instead of the user's.</summary>
    internal static IReadOnlyList<DetectedGame> DetectIn(IEnumerable<string> steamRoots, CancellationToken cancellationToken)
    {
        var games = new List<DetectedGame>();
        foreach (var steamRoot in steamRoots)
        {
            foreach (var library in FindLibraries(steamRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                string[] manifests;
                try { manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable library (EACCES/EIO on a mount) must not hide every other library.
                    DetectorDiagnostics.Warn($"Steam library skipped: {library}", ex);
                    continue;
                }
                foreach (var acf in manifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var fields = DetectionHelpers.ParseFlatVdf(File.ReadAllText(acf));
                        if (!fields.TryGetValue("name", out var name) || !fields.TryGetValue("installdir", out var installDir))
                            continue;
                        if (DetectionHelpers.IsKnownSteamTool(name, installDir))
                            continue;
                        var gameRoot = Path.Combine(steamapps, "common", installDir);
                        if (!Directory.Exists(gameRoot)) continue;
                        var appId = ExtractAppIdFromAcfFilename(Path.GetFileName(acf));
                        var prefix = appId is int id ? Path.Combine(steamapps, "compatdata", id.ToString(), "pfx") : null;
                        if (prefix is not null && !Directory.Exists(prefix)) prefix = null;
                        var index = GameFileIndexer.Build(gameRoot, 8, cancellationToken);
                        var nativeExecutable = prefix is null
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
                            LaunchCommand = appId is int launchId ? $"steam -applaunch {launchId}" : null
                        };
                        games.Add(game);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        DetectorDiagnostics.Warn($"Steam entry skipped: {Path.GetFileName(acf)} in {library}", ex);
                    }
                }
            }
        }
        return games;
    }

    public static int? ExtractAppIdFromAcfFilename(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        return stem.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(stem["appmanifest_".Length..], out var id) ? id : null;
    }

    private static IEnumerable<string> FindLibraries(string steamRoot)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal) { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            string text;
            try { text = File.ReadAllText(vdf); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DetectorDiagnostics.Warn($"Steam library list unreadable: {vdf}", ex);
                return roots;
            }
            foreach (Match match in LibraryPathRegex().Matches(text))
            {
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) roots.Add(path);
            }
        }
        return roots;
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
