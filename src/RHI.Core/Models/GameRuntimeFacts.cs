namespace RHI.Core.Models;

/// <summary>
/// The <see cref="DetectedGame"/> fields the runtime probe works out (executable and binary folder, engine, graphics
/// API and the upstream manifest's per-game metadata), captured as one immutable value. The probe runs on a working
/// copy of the game off the UI thread; the UI thread then applies this snapshot to the shared instance in one step, so
/// the UI never sees half of a probe's writes and two probes never write the same instance (Refs #82).
/// </summary>
public sealed record GameRuntimeFacts(
    string? ExecutablePath,
    string? BinaryDirectory,
    string? EngineHint,
    GraphicsApi GraphicsApi,
    IReadOnlyList<GraphicsApi> GraphicsApis,
    GraphicsApiConfidence GraphicsApiConfidence,
    string? GraphicsApiEvidence,
    string? CanonicalManifestName,
    PeArchitecture? ForcedArchitecture,
    string? EngineIniProjectOverride,
    GraphicsApi? ManifestGraphicsApiOverride,
    IReadOnlyList<GraphicsApi> ManifestGraphicsApis,
    bool DofFixForcedByManifest,
    bool DofFixSkippedByManifest)
{
    /// <summary>The names of the <see cref="DetectedGame"/> properties this snapshot owns (contract test).</summary>
    public static readonly IReadOnlyList<string> OwnedProperties =
    [
        nameof(DetectedGame.ExecutablePath), nameof(DetectedGame.BinaryDirectory), nameof(DetectedGame.EngineHint),
        nameof(DetectedGame.GraphicsApi), nameof(DetectedGame.GraphicsApis), nameof(DetectedGame.GraphicsApiConfidence),
        nameof(DetectedGame.GraphicsApiEvidence), nameof(DetectedGame.CanonicalManifestName), nameof(DetectedGame.ForcedArchitecture),
        nameof(DetectedGame.EngineIniProjectOverride), nameof(DetectedGame.ManifestGraphicsApiOverride),
        nameof(DetectedGame.ManifestGraphicsApis), nameof(DetectedGame.DofFixForcedByManifest), nameof(DetectedGame.DofFixSkippedByManifest)
    ];

    public static GameRuntimeFacts Capture(DetectedGame game) => new(
        game.ExecutablePath, game.BinaryDirectory, game.EngineHint,
        game.GraphicsApi, game.GraphicsApis.ToArray(), game.GraphicsApiConfidence, game.GraphicsApiEvidence,
        game.CanonicalManifestName, game.ForcedArchitecture, game.EngineIniProjectOverride,
        game.ManifestGraphicsApiOverride, game.ManifestGraphicsApis.ToArray(),
        game.DofFixForcedByManifest, game.DofFixSkippedByManifest);

    /// <summary>Writes the snapshot onto <paramref name="game"/>. UI thread (or the only owner of the instance).</summary>
    public void ApplyTo(DetectedGame game)
    {
        game.ExecutablePath = ExecutablePath;
        game.BinaryDirectory = BinaryDirectory;
        game.EngineHint = EngineHint;
        game.GraphicsApi = GraphicsApi;
        game.GraphicsApis = GraphicsApis;
        game.GraphicsApiConfidence = GraphicsApiConfidence;
        game.GraphicsApiEvidence = GraphicsApiEvidence;
        game.CanonicalManifestName = CanonicalManifestName;
        game.ForcedArchitecture = ForcedArchitecture;
        game.EngineIniProjectOverride = EngineIniProjectOverride;
        game.ManifestGraphicsApiOverride = ManifestGraphicsApiOverride;
        game.ManifestGraphicsApis = ManifestGraphicsApis;
        game.DofFixForcedByManifest = DofFixForcedByManifest;
        game.DofFixSkippedByManifest = DofFixSkippedByManifest;
    }
}
