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
            // Add/Edit refuse these roots; an older entry with one is not listed rather than indexing
            // $HOME, / or a whole Wine prefix on every selection.
            .Where(entry => GameRootGuard.IsAcceptable(entry.InstallPath))
            .Select(entry => new DetectedGame
            {
                Name = entry.Name,
                InstallPath = entry.InstallPath,
                Source = "Manual",
                IsManuallyAdded = true,
                ExecutablePath = File.Exists(entry.ExecutablePath) ? entry.ExecutablePath : null,
                LaunchExecutablePath = File.Exists(entry.ExecutablePath) ? entry.ExecutablePath : null,
                BinaryDirectory = File.Exists(entry.ExecutablePath)
                    ? Path.GetDirectoryName(entry.ExecutablePath)
                    : entry.InstallPath,
                PrefixPath = Directory.Exists(entry.PrefixPath) ? entry.PrefixPath : null,
                Runner = entry.Runner ?? "Manual Windows",
                LaunchCommand = entry.LaunchCommand,
                CompatibilityLayerType = Directory.Exists(entry.PrefixPath) ? "wine" : null,
                UserGraphicsApiOverride = entry.UserGraphicsApiOverride
            })
            .Cast<DetectedGame>()
            .ToArray();
        return Task.FromResult<IReadOnlyList<DetectedGame>>(games);
    }
}
