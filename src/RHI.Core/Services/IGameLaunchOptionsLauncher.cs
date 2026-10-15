using RHI.Core.Models;

namespace RHI.Core.Services;

/// <param name="Started">#191: called with the launched process's id once it started.</param>
/// <param name="Exited">#191: called once when the launched command has ended (also after an early failure).</param>
/// <param name="AfterExit">
/// #191: an argument vector (absolute program path, then its arguments) the launch shell itself runs once the game's
/// command has ended, whatever its status and whether or not RHI still runs (the display switch goes back even when RHI was
/// closed meanwhile). Each entry is one argument, never parsed by the shell.
/// </param>
public sealed record GameLaunchOptions(
    IReadOnlyDictionary<string, string>? Environment = null,
    string? CommandPrefix = null,
    Action<int>? Started = null,
    Action? Exited = null,
    IReadOnlyList<string>? AfterExit = null);

/// <summary>Optional capability for launchers that can apply an environment/wrapper without rewriting the saved launch command.</summary>
public interface IGameLaunchOptionsLauncher : IGameLauncher
{
    Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default);
}
