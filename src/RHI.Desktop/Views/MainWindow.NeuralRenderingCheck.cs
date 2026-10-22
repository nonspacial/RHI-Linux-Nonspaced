using Avalonia.Controls;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>
    /// w11/nr-check (#234): shows the read-only Check Neural Rendering page (template
    /// <c>Views/Dialogs/NeuralRenderingCheckDialogView.axaml</c>) that both entry points open — the detail panel's
    /// "Check…" button and the card menu's "Check Neural Rendering…" — fitted to this window. Wired as
    /// <see cref="NeuralRenderingCheckPresenter.ShowDialogAsync"/>.
    /// </summary>
    private async Task ShowNeuralRenderingCheckAsync(NeuralRenderingCheckDialogViewModel model)
    {
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = NeuralRenderingCheckDialogViewModel.Width,
            SizeToContent = SizeToContent.Height, // as tall as its findings; FitDialogToOwner caps it, then it scrolls
            MinWidth = NeuralRenderingCheckDialogViewModel.MinWidth,
            MinHeight = NeuralRenderingCheckDialogViewModel.MinHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };
        model.CloseRequested += dialog.Close;
        await ShowFittedDialogAsync(dialog);
    }
}
