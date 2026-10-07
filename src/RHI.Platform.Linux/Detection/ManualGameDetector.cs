using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Makes manually added games first-class scanner input so they pass through the same
/// upstream manifest policy, dedupe and runtime enrichment as launcher-detected games.
/// </summary>
public sealed class ManualGameDetector : IGameDetector
{
    private readonly LinuxGameLibraryStore _store;
    public string Name => "Manual";

    public ManualGameDetector(LinuxGameLibraryStore store) => _store = store;

    /// <summary>
    /// Removes the saved manual entries behind a listed game and returns how many were removed. Matches
    /// the folder the entry was saved with (the game's library identity), not the current InstallPath,
    /// which a game edit or the manifest can rewrite.
    /// </summary>
    public static int RemoveEntries(LinuxGameLibraryState state, DetectedGame game)
    {
        var identities = new[]
            {
                string.IsNullOrWhiteSpace(game.LibraryIdentityPath) ? game.InstallPath : game.LibraryIdentityPath,
                // The manifest can move InstallPath before the identity is taken; it keeps the original here.
                game.ManifestInstallRoot
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Normalise(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return state.ManualGames.RemoveAll(entry =>
            !string.IsNullOrWhiteSpace(entry.InstallPath) && identities.Contains(Normalise(entry.InstallPath)));
    }

    private static string Normalise(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var games = _store.Load().ManualGames
            // Unchanged from main: an entry whose folder no longer exists is not listed (it stays saved).
            .Where(entry => Directory.Exists(entry.InstallPath))
            .Select(entry =>
            {
                // Add/Edit refuse $HOME, /, a whole Wine prefix… as a game folder. An older entry saved with
                // one stays listed (never hidden: it is the user's entry) as "folder not allowed", with no
                // patch target, so nothing indexes or patches that folder; Remove or Edit repairs it.
                var notAllowed = !GameRootGuard.IsAcceptable(entry.InstallPath);
                var exe = !notAllowed && File.Exists(entry.ExecutablePath) ? entry.ExecutablePath : null;
                return new DetectedGame
                {
                    Name = entry.Name,
                    InstallPath = entry.InstallPath,
                    Source = "Manual",
                    IsManuallyAdded = true,
                    GameRootNotAllowed = notAllowed,
                    RequiresExplicitPatchTarget = notAllowed,
                    ExecutablePath = exe,
                    LaunchExecutablePath = File.Exists(entry.ExecutablePath) ? entry.ExecutablePath : null,
                    BinaryDirectory = notAllowed
                        ? null
                        : exe is not null ? Path.GetDirectoryName(exe) : entry.InstallPath,
                    PrefixPath = Directory.Exists(entry.PrefixPath) ? entry.PrefixPath : null,
                    Runner = entry.Runner ?? "Manual Windows",
                    LaunchCommand = entry.LaunchCommand,
                    CompatibilityLayerType = Directory.Exists(entry.PrefixPath) ? "wine" : null,
                    UserGraphicsApiOverride = entry.UserGraphicsApiOverride
                };
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<DetectedGame>>(games);
    }
}
