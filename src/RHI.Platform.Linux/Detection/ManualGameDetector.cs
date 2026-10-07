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
