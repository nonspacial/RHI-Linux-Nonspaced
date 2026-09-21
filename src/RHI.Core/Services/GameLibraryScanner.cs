using RHI.Core.Models;

namespace RHI.Core.Services;

public sealed class GameLibraryScanner
{
    private readonly IGameDetector[] _detectors;
    private readonly IGameLibraryPolicy[] _policies;

    public GameLibraryScanner(
        IEnumerable<IGameDetector> detectors,
        IEnumerable<IGameLibraryPolicy>? policies = null)
    {
        _detectors = detectors.ToArray();
        _policies = policies?.ToArray() ?? [];
    }

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
        IReadOnlyList<DetectedGame> games = results.SelectMany(x => x).ToArray();

        foreach (var policy in _policies)
        {
            try
            {
                games = await policy.ApplyAsync(games, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Policies are metadata/enrichment layers. If an upstream policy source is
                // unavailable, keep the locally detected library exactly as it was.
            }
        }

        return games
            .Where(g => !string.IsNullOrWhiteSpace(g.Name) && Directory.Exists(g.InstallPath))
            .GroupBy(GameKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => Merge(g.ToArray()))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string GameKey(DetectedGame g)
    {
        if (g.SteamAppId is int id)
        {
            // Normal storefront duplicates still merge by AppID. Manifest splitGames intentionally
            // produces multiple cards from one AppID, so those children need independent keys.
            if (g.IsManifestSplitChild)
                return $"steam:{id}:split:{g.Name}:{Path.GetFullPath(g.InstallPath)}";
            return $"steam:{id}";
        }
        return $"path:{Path.GetFullPath(g.InstallPath)}";
    }

    private static DetectedGame Merge(DetectedGame[] games)
    {
        var first = games[0];
        foreach (var candidate in games.Skip(1))
        {
            first.PrefixPath ??= candidate.PrefixPath;
            first.ExecutablePath ??= candidate.ExecutablePath;
            first.LaunchExecutablePath ??= candidate.LaunchExecutablePath;
            first.LibraryIdentityPath ??= candidate.LibraryIdentityPath;
            first.LibraryProvider ??= candidate.LibraryProvider;
            first.LaunchProvider ??= candidate.LaunchProvider;
            first.Runner ??= candidate.Runner;
            first.LaunchCommand ??= candidate.LaunchCommand;
            first.CompatibilityTool ??= candidate.CompatibilityTool;
            first.CompatibilityLayerType ??= candidate.CompatibilityLayerType;
            first.WineBinaryPath ??= candidate.WineBinaryPath;
            first.WineServerPath ??= candidate.WineServerPath;
            first.CanonicalManifestName ??= candidate.CanonicalManifestName;
            first.WikiNameOverride ??= candidate.WikiNameOverride;
            first.ManifestInstallRoot ??= candidate.ManifestInstallRoot;
            first.IsManifestSplitChild |= candidate.IsManifestSplitChild;
            first.ManifestSteamAppIdOverride ??= candidate.ManifestSteamAppIdOverride;
            first.ManifestGraphicsApiOverride ??= candidate.ManifestGraphicsApiOverride;
            if (first.ManifestGraphicsApis.Count == 0) first.ManifestGraphicsApis = candidate.ManifestGraphicsApis;
            first.EngineHint ??= candidate.EngineHint;
            first.ForcedArchitecture ??= candidate.ForcedArchitecture;
            first.EngineIniProjectOverride ??= candidate.EngineIniProjectOverride;
            first.WikiUnlinkedByManifest |= candidate.WikiUnlinkedByManifest;
            first.DlssScanSkippedByManifest |= candidate.DlssScanSkippedByManifest;
            first.UeExtendedByManifest |= candidate.UeExtendedByManifest;
            first.NativeHdrByManifest |= candidate.NativeHdrByManifest;
            first.UeExtendedBlockedByManifest |= candidate.UeExtendedBlockedByManifest;
            first.UeExtendedHdrByManifest ??= candidate.UeExtendedHdrByManifest;
            first.UeExtendedLutByManifest ??= candidate.UeExtendedLutByManifest;
            first.ReShadeDllNameOverride ??= candidate.ReShadeDllNameOverride;
            first.DisplayCommanderDllNameOverride ??= candidate.DisplayCommanderDllNameOverride;
            first.OptiScalerDllNameOverride ??= candidate.OptiScalerDllNameOverride;
            first.PdUpscalerArtifact ??= candidate.PdUpscalerArtifact;
            first.ManifestLaunchExecutable ??= candidate.ManifestLaunchExecutable;
            first.ExternalModUrl ??= candidate.ExternalModUrl;
            first.ExternalModLabel ??= candidate.ExternalModLabel;
            first.NexusUrlOverride ??= candidate.NexusUrlOverride;
            first.PcgwUrlOverride ??= candidate.PcgwUrlOverride;
            first.UwFixUrlOverride ??= candidate.UwFixUrlOverride;
            first.UltraPlusUrlOverride ??= candidate.UltraPlusUrlOverride;
            first.WikiStatusOverride ??= candidate.WikiStatusOverride;
            first.AuthorOverride ??= candidate.AuthorOverride;
            first.ManifestGameNote ??= candidate.ManifestGameNote;
            first.ManifestGameNoteUrl ??= candidate.ManifestGameNoteUrl;
            first.ManifestGameNoteUrlLabel ??= candidate.ManifestGameNoteUrlLabel;
            if (first.ManifestInstallWarnings.Count == 0) first.ManifestInstallWarnings = candidate.ManifestInstallWarnings;
            if (first.GraphicsApi == GraphicsApi.Unknown)
                first.GraphicsApi = candidate.GraphicsApi;
        }
        return first;
    }
}
