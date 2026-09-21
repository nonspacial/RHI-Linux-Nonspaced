using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Detection;

public sealed class LinuxGameRuntimeProbe : IGameRuntimeProbe
{
    private readonly UpstreamRhiManifestService? _upstreamManifest;
    private readonly PcGamingWikiGraphicsApiService? _pcGamingWiki;

    public LinuxGameRuntimeProbe(
        UpstreamRhiManifestService? upstreamManifest = null,
        PcGamingWikiGraphicsApiService? pcGamingWiki = null)
    {
        _upstreamManifest = upstreamManifest;
        _pcGamingWiki = pcGamingWiki;
    }

    public async Task ProbeAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        // Manifest enrichment is deliberately fail-open: losing GitHub/network access must never
        // make the local game library unusable. The upstream manifest only refines metadata.
        if (_upstreamManifest is not null)
        {
            try
            {
                var gameOverride = await _upstreamManifest
                    .ResolveGameOverrideAsync(game, cancellationToken)
                    .ConfigureAwait(false);
                if (gameOverride is not null)
                {
                    game.CanonicalManifestName ??= gameOverride.GameName;
                    game.EngineHint = gameOverride.EngineHint ?? gameOverride.EngineOverride;
                    game.ForcedArchitecture = gameOverride.ForcedArchitecture;
                    game.EngineIniProjectOverride = gameOverride.EngineIniProjectOverride;
                    game.ManifestGraphicsApiOverride = gameOverride.GraphicsApiOverride;
                    game.ManifestGraphicsApis = gameOverride.GraphicsApiOverrides;
                    game.DofFixForcedByManifest = gameOverride.DofFixForced;
                    game.DofFixSkippedByManifest = gameOverride.DofFixSkipped;
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Keep local probing authoritative when upstream metadata is temporarily unavailable.
            }
        }

        await Task.Run(
            () => DetectionHelpers.ApplyRuntimeMetadata(game, indexedFiles, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // PCGamingWiki is a network fallback only. Strong local evidence (logs, Unity
        // boot.config, PE/import signatures, configured Wine overrides) always wins.
        if (_pcGamingWiki is not null
            && game.GraphicsApi == GraphicsApi.Unknown)
        {
            try
            {
                var fallback = await _pcGamingWiki
                    .ResolveAndFetchAsync(
                        game.Name,
                        game.SteamAppId ?? game.ManifestSteamAppIdOverride,
                        game.PcgwUrlOverride,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fallback.Primary != GraphicsApi.Unknown)
                {
                    game.GraphicsApi = fallback.Primary;
                    game.GraphicsApis = fallback.Candidates.ToArray();
                    game.GraphicsApiConfidence = fallback.Confidence;
                    game.GraphicsApiEvidence = fallback.Evidence;
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Fail open: an unavailable/changed PCGamingWiki page must not break scanning.
            }
        }
    }
}
