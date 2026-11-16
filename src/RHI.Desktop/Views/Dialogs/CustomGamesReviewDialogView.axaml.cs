using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>#293: the review of the games found in the custom game folders (the view model holds the rows).</summary>
public sealed partial class CustomGamesReviewDialogView : UserControl
{
    public CustomGamesReviewDialogView() => AvaloniaXamlLoader.Load(this);

    /// <summary>The review as a dialog window (owned by the main window, or by Settings when opened from there).</summary>
    internal static Window CreateWindow(CustomGamesReviewViewModel model)
    {
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 760,
            Height = 620,
            MinWidth = 460,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };
        model.CloseRequested += dialog.Close;
        return dialog;
    }
}
