using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #204 (w8 follow-up C11, final review 1): a success line the UI words itself (Luma ⚙, peak brightness, the RenoDX ⚙
/// Engine.ini rows, per-game Apply, Shaders/Add-ons Apply, a preset with its shader packs, batches, Update All) for an
/// operation whose save reported an error after the change was written: the line keeps what changed and adds the
/// operation's own note (<c>DeploymentScope.Landed</c>, with its PT-BR pair), so the user learns the change is in place
/// although the save complained.
/// </summary>
public static class LandedLine
{
    public static string With(string line, ComponentOperationResult result) => line + Note(result);

    /// <summary>As <see cref="With(string, ComponentOperationResult)"/>, for a game's shader packs or add-ons brought to its set.</summary>
    public static string With(string line, ShaderSetApplyResult result)
        => result.Landed ? line + " " + UiLocalizationService.Present(result.Message) : line;

    /// <summary>The note (with its leading space) when <paramref name="result"/> landed; empty otherwise.</summary>
    public static string Note(ComponentOperationResult result)
        => result.Landed ? " " + UiLocalizationService.Present(result.Message) : "";

    /// <summary>A batch line, with the note naming the games whose save reported an error after their change was written.</summary>
    public static string With(string line, IReadOnlyList<string>? landedGames)
        => line + BatchNote(UiLocalizationService.CurrentLanguage, landedGames);

    /// <summary>The batch note (with its leading space) in <paramref name="language"/>; empty when no game landed.</summary>
    public static string BatchNote(string? language, IReadOnlyList<string>? landedGames)
        => landedGames is { Count: > 0 }
            ? " " + UiLocalizationService.PresentFor(language, DeploymentScope.Landed(string.Join(", ", landedGames)).Message)
            : "";
}
