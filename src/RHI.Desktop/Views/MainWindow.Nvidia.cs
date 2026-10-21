using Avalonia.Controls;
using Avalonia.Interactivity;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>
    /// w7/nvapi-launch hook (#211, owner layout B): the DLSS section's "NVIDIA settings…" button opens the one NVIDIA
    /// settings dialog (template <c>Views/Dialogs/NvidiaSettingsDialogView.axaml</c>), fitted to this window.
    /// </summary>
    private void NvidiaSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || !vm.Dlss.Nvidia.IsShown) return;
        var model = new NvidiaSettingsDialogViewModel(vm.Dlss.Nvidia);
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = NvidiaSettingsDialogViewModel.Width,
            SizeToContent = SizeToContent.Height, // as tall as the rows this game shows; FitDialogToOwner caps it
            MinWidth = NvidiaSettingsDialogViewModel.MinWidth,
            MinHeight = NvidiaSettingsDialogViewModel.MinHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };
        model.CloseRequested += dialog.Close;
        await ShowFittedDialogAsync(dialog);
    });
}
