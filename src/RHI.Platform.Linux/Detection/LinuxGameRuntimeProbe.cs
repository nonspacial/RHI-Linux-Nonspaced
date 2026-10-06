using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Detection;

public sealed class LinuxGameRuntimeProbe : IGameRuntimeProbe
{
    private readonly UpstreamRhiManifestService? _upstreamManifest;
    private readonly PcGamingWikiGraphicsApiService? _pcGamingWiki;
    private readonly PcgwCentralDataService? _pcgwData;

    public LinuxGameRuntimeProbe(
        UpstreamRhiManifestService? upstreamManifest = null,
        PcGamingWikiGraphicsApiService? pcGamingWiki = null,
        PcgwCentralDataService? pcgwData = null)
    {
        _upstreamManifest = upstreamManifest;
        _pcGamingWiki = pcGamingWiki;
        _pcgwData = pcgwData;
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

        // Upstream RHI 2.7.6+: the single pcgw_data.json answers API and engine without any
        // per-game request. Games it covers never reach the live PCGW fallback below.
        var inCentralData = false;
        if (_pcgwData is not null)
        {
            try
            {
                var data = await _pcgwData.GetAsync(cancellationToken).ConfigureAwait(false);
                var hit = data?.TryLookup(game.Name, game.SteamAppId ?? game.ManifestSteamAppIdOverride);
                if (hit is null && !string.IsNullOrWhiteSpace(game.CanonicalManifestName)
                    && !game.CanonicalManifestName.Equals(game.Name, StringComparison.OrdinalIgnoreCase))
                    hit = data?.TryLookup(game.CanonicalManifestName, null);
                if (hit is { } found)
                {
                    inCentralData = true;
                    ApplyCentralData(game, found.PageTitle, found.Entry);
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Fail open, as for the manifest: PCGW data only refines local detection.
            }
        }

        // PCGamingWiki is a network fallback only. Strong local evidence (logs, Unity
        // boot.config, PE/import signatures, configured Wine overrides) always wins.
        if (_pcGamingWiki is not null
            && !inCentralData
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

    /// <summary>
    /// Applies a pcgw_data.json entry: engine only when no local/manifest hint exists, API only
    /// when local detection found none, and the upstream 2.7.7 DX9 false-positive correction when
    /// a PE scan reported DX9 for a game PCGW lists only on DX11/DX12/Vulkan.
    /// </summary>
    internal static void ApplyCentralData(DetectedGame game, string pageTitle, PcgwCentralEntry entry)
    {
        if (string.IsNullOrWhiteSpace(game.EngineHint)
            && PcgwCentralData.NormaliseEngineName(entry.Engine) is { } engine)
            game.EngineHint = engine;

        var apis = entry.GraphicsApis();
        if (apis.Count == 0)
            return;

        if (game.GraphicsApi == GraphicsApi.Unknown)
        {
            game.GraphicsApi = apis[0];
            game.GraphicsApis = apis.ToArray();
            game.GraphicsApiConfidence = GraphicsApiConfidence.Medium;
            game.GraphicsApiEvidence = $"PCGamingWiki data ({pageTitle}): {string.Join(", ", apis)}";
            return;
        }

        if (game.GraphicsApi == GraphicsApi.DirectX9
            && !entry.Dx9
            && (entry.Dx12 || entry.Vulkan || entry.Dx11)
            && GraphicsApiDetector.IsBinarySignatureEvidence(game.GraphicsApiEvidence))
        {
            // Priority order puts DX12, then Vulkan, then DX11 first, as upstream picks.
            var corrected = apis[0];
            game.GraphicsApiEvidence = $"PCGamingWiki data ({pageTitle}) lists no DirectX 9; corrected to {corrected} from {game.GraphicsApiEvidence}";
            game.GraphicsApi = corrected;
            game.GraphicsApis = game.GraphicsApis.Where(static api => api != GraphicsApi.DirectX9)
                .Prepend(corrected).Distinct().ToArray();
            game.GraphicsApiConfidence = GraphicsApiConfidence.Medium;
        }
    }
}
