using RHI.Core.Models;

namespace RHI.Core.Services;

public interface IGameDetector
{
    string Name { get; }
    Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default);
}
