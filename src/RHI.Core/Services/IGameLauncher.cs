using RHI.Core.Models;

namespace RHI.Core.Services;

public interface IGameLauncher
{
    bool CanLaunch(DetectedGame game);
    Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default);
}
