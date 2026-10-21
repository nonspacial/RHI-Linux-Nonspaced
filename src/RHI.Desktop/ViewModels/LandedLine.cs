using RHI.Core.Models;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #204 (w8 follow-up C11): a success line the UI words itself (Luma ⚙, peak brightness, the RenoDX ⚙ Engine.ini rows,
/// per-game Apply, batches) for an operation whose save reported an error after the change was written: the line keeps
/// what changed and adds the operation's own note (<c>DeploymentScope.Landed</c>, with its PT-BR pair), so the user learns
/// the change is in place although the save complained.
/// </summary>
public static class LandedLine
{
    public static string With(string line, ComponentOperationResult result)
        => result.Landed ? line + " " + UiLocalizationService.Present(result.Message) : line;

    /// <summary>A batch line, with the note naming the games whose save reported an error after their change was written.</summary>
    public static string With(string line, IReadOnlyList<string>? landedGames)
        => landedGames is { Count: > 0 }
            ? line + " " + UiLocalizationService.Present(RHI.Platform.Linux.Deployment.DeploymentScope.Landed(string.Join(", ", landedGames)).Message)
            : line;
}
