using RHI.Core.Models;

namespace RHI.Core.Services;

/// <param name="Started">#191: called with the launched process's id once it started.</param>
/// <param name="Exited">#191: called once when the launched command has ended (also after an early failure).</param>
/// <param name="AfterExit">
/// #191: an argument vector (absolute program path, then its arguments) the launch shell itself runs once the game's
/// command has ended, whatever its status and whether or not RHI still runs (the display switch goes back even when RHI was
/// closed meanwhile). Each entry is one argument, never parsed by the shell.
/// </param>
/// <param name="Arguments">
/// w14/layouts-c (upstream 2.9.0+ per-game launch arguments): the game's arguments, already split (one entry per argument).
/// The launcher adds them where they reach the game (a command RHI built, a Steam <c>-applaunch</c> hand-off) and leaves a
/// command the user typed and other launchers as they are. Never a refusal reason on a hand-off.
/// </param>
public sealed record GameLaunchOptions(
    IReadOnlyDictionary<string, string>? Environment = null,
    string? CommandPrefix = null,
    Action<int>? Started = null,
    Action? Exited = null,
    IReadOnlyList<string>? AfterExit = null,
    IReadOnlyList<string>? Arguments = null);

/// <summary>Optional capability for launchers that can apply an environment/wrapper without rewriting the saved launch command.</summary>
public interface IGameLaunchOptionsLauncher : IGameLauncher
{
    Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default);
}
