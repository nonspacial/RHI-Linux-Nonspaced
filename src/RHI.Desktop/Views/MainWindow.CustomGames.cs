using Avalonia.Interactivity;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views.Dialogs;

namespace RHI.Desktop.Views;

/// <summary>#293: the status line's "new games found in your custom folders · Review" link opens the review.</summary>
public partial class MainWindow
{
    private bool _customGamesReviewOpen;

    private void CustomGamesNotice_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || _customGamesReviewOpen) return;
        if (vm.CreateCustomGamesReview(DialogPickers().PickFolder) is not { } model) return;
        _customGamesReviewOpen = true;
        try { await ShowFittedDialogAsync(CustomGamesReviewDialogView.CreateWindow(model)); }
        finally { _customGamesReviewOpen = false; }
    });
}
