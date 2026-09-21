using RHI.Core.Models;

namespace RHI.Core.Services;

public sealed record GameLaunchOptions(
    IReadOnlyDictionary<string, string>? Environment = null,
    string? CommandPrefix = null);

/// <summary>Optional capability for launchers that can apply an environment/wrapper without rewriting the saved launch command.</summary>
public interface IGameLaunchOptionsLauncher : IGameLauncher
{
    Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default);
}
