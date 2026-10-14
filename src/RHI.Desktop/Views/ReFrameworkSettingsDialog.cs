using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// RE Framework ⚙ dialog (#159, upstream 2.8.6): one "Delete _storage_ folder" button and a line saying where the
/// folder is or that there is none. The button is off when there is nothing to delete; after a delete the line is read
/// again. Paths are one line with a middle ellipsis and the full path in a 2 s tooltip.
/// </summary>
public static class ReFrameworkSettingsDialog
{
    public static Window Create(ReFrameworkStoragePresenter presenter, string gameName)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Journal.RefSettingsTitle"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text("Journal.RefStorageIntro"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        });

        var found = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var folder = new MiddleEllipsisText { Classes = { "muted" }, FontSize = 12, WrapNonPath = false, MiddleNonPath = true };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82, IsVisible = false };
        var delete = new Button
        {
            Classes = { "rhi", "remove" },
            Content = UiLocalizationService.Text("Journal.RefStorageDelete"),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        void Refresh()
        {
            var path = presenter.FolderPath;
            found.Text = presenter.StatusLine;
            folder.FullText = path;
            folder.IsVisible = path is not null;
            delete.IsEnabled = path is not null;
        }

        delete.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            delete.IsEnabled = false;
            delete.Content = UiLocalizationService.Text("Journal.RefStorageDeleting");
            try
            {
                var outcome = await presenter.DeleteAsync();
                result.Text = outcome.Message;
                result.IsVisible = true;
            }
            finally
            {
                delete.Content = UiLocalizationService.Text("Journal.RefStorageDelete");
                Refresh();
            }
        }, nameof(ReFrameworkSettingsDialog));

        Refresh();
        panel.Children.Add(delete);
        panel.Children.Add(found);
        panel.Children.Add(folder);
        panel.Children.Add(result);

        return new Window
        {
            Title = $"{UiLocalizationService.Text("Journal.RefSettingsTitle")} — {gameName}",
            Width = 600,
            Height = 300,
            MinWidth = 420,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
    }
}
