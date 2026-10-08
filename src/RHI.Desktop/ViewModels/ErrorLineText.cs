using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Lines the windows build around a platform or .NET error text (folder openers, the update history and HDR mods
/// dialogs). The error text always goes through the UI language's presentation: known platform messages have PT-BR
/// pairs, and unmapped English becomes the localised pointer to the app log instead of an English sentence in a
/// Portuguese UI.
/// </summary>
public static class ErrorLineText
{
    /// <summary>The error text alone, presented (a dialog line with no surrounding sentence).</summary>
    public static string Of(Exception error) => UiLocalizationService.Present(error.Message);

    public static string FolderNotOpened(string? error)
        => UiLocalizationService.Format("Ui.FolderNotOpened", UiLocalizationService.Present(error));

    public static string CustomShadersNotOpened(string? error)
        => UiLocalizationService.Format("Win.CouldNotOpenCustomShaders", UiLocalizationService.Present(error));

    /// <summary>A link the desktop opener refused or could not hand over.</summary>
    public static string LinkNotOpened(string link, string? error)
        => error is null ? UiLocalizationService.Format("Win.CouldNotOpen", link) : UiLocalizationService.Present(error);

    public static string CustomAddonsNotOpened(string? error)
        => UiLocalizationService.Format("Win.CouldNotOpenCustomAddOns", UiLocalizationService.Present(error));
}
