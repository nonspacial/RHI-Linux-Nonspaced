using RHI.Core.Models;

namespace RHI.Core.Services;

/// <param name="Started">#191: called with the launched process's id once it started.</param>
/// <param name="Exited">#191: called once when the launched command has ended (also after an early failure).</param>
public sealed record GameLaunchOptions(
    IReadOnlyDictionary<string, string>? Environment = null,
    string? CommandPrefix = null,
    Action<int>? Started = null,
    Action? Exited = null);

/// <summary>Optional capability for launchers that can apply an environment/wrapper without rewriting the saved launch command.</summary>
public interface IGameLaunchOptionsLauncher : IGameLauncher
{
    Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default);
}
