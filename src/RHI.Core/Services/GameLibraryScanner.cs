using RHI.Core.Models;

namespace RHI.Core.Services;

public sealed class GameLibraryScanner(IEnumerable<IGameDetector> detectors)
{
    private readonly IGameDetector[] _detectors = detectors.ToArray();

    public async Task<IReadOnlyList<DetectedGame>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _detectors.Select(async detector =>
        {
            try
            {
                return await detector.DetectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return Array.Empty<DetectedGame>();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return results
            .SelectMany(x => x)
            .Where(g => !string.IsNullOrWhiteSpace(g.Name) && Directory.Exists(g.InstallPath))
            .GroupBy(GameKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => Merge(g.ToArray()))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string GameKey(DetectedGame g)
        => g.SteamAppId is int id ? $"steam:{id}" : $"path:{Path.GetFullPath(g.InstallPath)}";

    private static DetectedGame Merge(DetectedGame[] games)
    {
        var first = games[0];
        foreach (var candidate in games.Skip(1))
        {
            first.PrefixPath ??= candidate.PrefixPath;
            first.ExecutablePath ??= candidate.ExecutablePath;
            first.Runner ??= candidate.Runner;
            first.LaunchCommand ??= candidate.LaunchCommand;
            first.CompatibilityTool ??= candidate.CompatibilityTool;
            if (first.GraphicsApi == GraphicsApi.Unknown)
                first.GraphicsApi = candidate.GraphicsApi;
        }
        return first;
    }
}
