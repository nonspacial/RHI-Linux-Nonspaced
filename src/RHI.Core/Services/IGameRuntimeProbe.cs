using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// Platform-specific lazy enrichment for a detected game. The operation is asynchronous
/// so filesystem/binary inspection never blocks the desktop UI.
/// </summary>
public interface IGameRuntimeProbe
{
    Task ProbeAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised, on a background thread, when network-only metadata for a game arrives after
    /// <see cref="ProbeAsync"/> returned without it (a slow PCGamingWiki lookup never holds up the
    /// selection). Probing the game again applies it from cache at once.
    /// </summary>
    event Action<DetectedGame>? LateMetadataAvailable { add { } remove { } }
}
