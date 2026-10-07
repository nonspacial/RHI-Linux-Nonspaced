using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Detection;

public sealed class LinuxGameRuntimeProbe : IGameRuntimeProbe
{
    /// <summary>How long a selection waits for a live PCGamingWiki answer before it carries on without it.</summary>
    public static readonly TimeSpan DefaultPcgwSelectionBudget = TimeSpan.FromSeconds(1);

    /// <summary>How long a selection waits for pcgw_data.json (a fresh disk copy loads well within it).</summary>
    public static readonly TimeSpan DefaultPcgwDataSelectionBudget = TimeSpan.FromSeconds(3);

    private readonly UpstreamRhiManifestService? _upstreamManifest;
    private readonly PcGamingWikiGraphicsApiService? _pcGamingWiki;
    private readonly PcgwCentralDataService? _pcgwData;
    private readonly TimeSpan _pcgwSelectionBudget;
    private readonly TimeSpan _pcgwDataSelectionBudget;
    private readonly Dictionary<string, Task<GraphicsApiDetection>> _pcgwLookups = new(StringComparer.Ordinal);

    public LinuxGameRuntimeProbe(
        UpstreamRhiManifestService? upstreamManifest = null,
        PcGamingWikiGraphicsApiService? pcGamingWiki = null,
        PcgwCentralDataService? pcgwData = null,
        TimeSpan? pcgwSelectionBudget = null,
        TimeSpan? pcgwDataSelectionBudget = null)
    {
        _upstreamManifest = upstreamManifest;
        _pcGamingWiki = pcGamingWiki;
        _pcgwData = pcgwData;
        _pcgwSelectionBudget = pcgwSelectionBudget ?? DefaultPcgwSelectionBudget;
        _pcgwDataSelectionBudget = pcgwDataSelectionBudget ?? DefaultPcgwDataSelectionBudget;
    }

    public event Action<DetectedGame>? LateMetadataAvailable;

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
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Keep local probing authoritative when upstream metadata is temporarily unavailable.
                AppLog.Warn("probe", $"Upstream manifest metadata unavailable for {game.Name}", ex);
            }
        }

        await Task.Run(
            () => DetectionHelpers.ApplyRuntimeMetadata(game, indexedFiles, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // Upstream RHI 2.7.6+: the single pcgw_data.json answers API and engine without any
        // per-game request. Games it covers never reach the live PCGW fallback below. The first
        // download is a shared background load: the selection waits a bounded time and is told
        // later (LateMetadataAvailable) when the data lands with an entry for this game (Refs #102).
        var inCentralData = false;
        var centralDataPending = false;
        if (_pcgwData is not null)
        {
            var load = _pcgwData.GetAsync(CancellationToken.None);
            try
            {
                var data = await load.WaitAsync(_pcgwDataSelectionBudget, cancellationToken).ConfigureAwait(false);
                if (LookupCentralData(data, game) is { } found)
                {
                    inCentralData = true;
                    ApplyCentralData(game, found.PageTitle, found.Entry);
                }
            }
            catch (TimeoutException)
            {
                centralDataPending = true;
                _ = load.ContinueWith(done =>
                {
                    if (done.IsCompletedSuccessfully && LookupCentralData(done.Result, game) is not null)
                        LateMetadataAvailable?.Invoke(game);
                }, TaskScheduler.Default);
            }
        }

        // PCGamingWiki is a network fallback only. Strong local evidence (logs, Unity
        // boot.config, PE/import signatures, configured Wine overrides) always wins, and so does a
        // native Linux result or a local candidate list: PCGW's Windows API table must not replace a
        // native "Vulkan or OpenGL" answer or an ambiguous local one (Refs #54).
        if (_pcGamingWiki is not null
            && !inCentralData
            && !centralDataPending // the data file may still cover it: no per-game request yet
            && game.GraphicsApi == GraphicsApi.Unknown
            && game.GraphicsApis.Count == 0
            && !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyPcgwFallbackAsync(game, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The live lookup runs on its own (bounded by the service's request timeouts) and the selection
    /// waits at most <see cref="DefaultPcgwSelectionBudget"/>: a cached answer applies at once, a slow
    /// one raises <see cref="LateMetadataAvailable"/> when it lands (it is then cached).
    /// </summary>
    private async Task ApplyPcgwFallbackAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var steamAppId = game.SteamAppId ?? game.ManifestSteamAppIdOverride;
        var key = $"{game.Name}|{steamAppId}|{game.PcgwUrlOverride}";
        Task<GraphicsApiDetection> lookup;
        lock (_pcgwLookups)
        {
            if (!_pcgwLookups.TryGetValue(key, out lookup!))
            {
                lookup = _pcGamingWiki!.ResolveAndFetchAsync(game.Name, steamAppId, game.PcgwUrlOverride, CancellationToken.None);
                _pcgwLookups[key] = lookup;
                _ = lookup.ContinueWith(_ => { lock (_pcgwLookups) _pcgwLookups.Remove(key); }, TaskScheduler.Default);
            }
        }

        try
        {
            var fallback = await lookup.WaitAsync(_pcgwSelectionBudget, cancellationToken).ConfigureAwait(false);
            ApplyPcgwResult(game, fallback);
        }
        catch (TimeoutException)
        {
            game.GraphicsApiEvidence = AppendEvidence(game.GraphicsApiEvidence, "PCGamingWiki lookup still running");
            _ = lookup.ContinueWith(done =>
            {
                if (done.IsCompletedSuccessfully && done.Result.Primary != GraphicsApi.Unknown)
                    LateMetadataAvailable?.Invoke(game);
                else if (done.Exception is { } error)
                    AppLog.Warn("pcgw", $"PCGamingWiki lookup failed for {game.Name}", error.GetBaseException());
            }, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open: an unavailable/changed PCGamingWiki page must not break scanning.
            AppLog.Warn("pcgw", $"PCGamingWiki lookup failed for {game.Name}", ex);
        }
    }

    private static (string PageTitle, PcgwCentralEntry Entry)? LookupCentralData(PcgwCentralData? data, DetectedGame game)
    {
        var hit = data?.TryLookup(game.Name, game.SteamAppId ?? game.ManifestSteamAppIdOverride);
        if (hit is null && !string.IsNullOrWhiteSpace(game.CanonicalManifestName)
            && !game.CanonicalManifestName.Equals(game.Name, StringComparison.OrdinalIgnoreCase))
            hit = data?.TryLookup(game.CanonicalManifestName, null);
        return hit;
    }

    private static void ApplyPcgwResult(DetectedGame game, GraphicsApiDetection fallback)
    {
        if (fallback.Primary == GraphicsApi.Unknown) return;
        game.GraphicsApi = fallback.Primary;
        game.GraphicsApis = fallback.Candidates.ToArray();
        game.GraphicsApiConfidence = fallback.Confidence;
        game.GraphicsApiEvidence = fallback.Evidence;
    }

    private static string AppendEvidence(string? evidence, string note)
        => string.IsNullOrWhiteSpace(evidence) ? note : $"{evidence}; {note}";

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
