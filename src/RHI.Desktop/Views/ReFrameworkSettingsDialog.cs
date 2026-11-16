using Avalonia.Controls;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// RE Framework ⚙ dialog (#159, upstream 2.8.6; #297 shared layout, upstream 2.9.0 <c>RefCogButton_Click</c>): the Files card
/// with what the <c>_storage_</c> folder is, where it is or that there is none, and one red "Delete _storage_ folder" button.
/// The button is hidden when there is nothing to delete (the owner's rule: buttons that do not apply are hidden, not
/// disabled; the "No _storage_ folder" line says why); after a delete the line is read again and the result shows in the
/// ⚙'s status line. Paths are one line with a middle ellipsis and the full path in a 2 s tooltip.
/// </summary>
public static class ReFrameworkSettingsDialog
{
    // Button content strings treat "_" as an access-key marker: doubled, "_storage_" is drawn with both underscores and
    // keeps the button's own (remove) text style.
    private static string Label(string key) => UiLocalizationService.Text(key).Replace("_", "__", StringComparison.Ordinal);

    public static Window Create(ReFrameworkStoragePresenter presenter, string gameName) => Build(presenter, gameName).Build(height: 380);

    /// <summary>The ⚙'s content (tests read it without a window).</summary>
    internal static CogDialog Build(ReFrameworkStoragePresenter presenter, string gameName)
    {
        var cog = new CogDialog("RE Framework", gameName);
        cog.AddSection(UiLocalizationService.Text("Cog.Files"));
        cog.AddIntro(UiLocalizationService.Text("Journal.RefStorageIntro"));
        var found = cog.AddNote("");
        var folder = cog.AddContent(new MiddleEllipsisText { Classes = { "muted" }, FontSize = 12, WrapNonPath = false, MiddleNonPath = true });
        Button? delete = null;
        delete = cog.MakeButton(Label("Journal.RefStorageDelete"), CogButtonKind.Danger, async () =>
        {
            delete!.IsEnabled = false;
            delete.Content = Label("Journal.RefStorageDeleting");
            try
            {
                var outcome = await presenter.DeleteAsync();
                cog.SetResult(outcome.Success, outcome.Message);
            }
            finally
            {
                delete.Content = Label("Journal.RefStorageDelete");
                Refresh();
            }
        }, name: "RefStorageDelete");
        cog.AddButtons(delete);

        void Refresh()
        {
            var path = presenter.FolderPath;
            found.Text = presenter.StatusLine;
            folder.FullText = path;
            folder.IsVisible = path is not null;
            delete.IsVisible = path is not null;
            delete.IsEnabled = true;
        }
        Refresh();
        return cog;
    }
}
