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
}
