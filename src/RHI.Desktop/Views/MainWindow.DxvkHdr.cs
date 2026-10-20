using Avalonia.Controls;
using Avalonia.Interactivity;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>#200: the DX9 HDR row's ⚙ (Views/DxvkHdrSettingsDialog.cs).</summary>
public sealed partial class MainWindow
{
    private void DxvkHdrSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(()
        => sender is Button { DataContext: ComponentRowViewModel { HasDxvkHdrSettings: true } } && DataContext is MainWindowViewModel { SelectedGame: { } game } vm
            ? ShowFittedDialogAsync(DxvkHdrSettingsDialog.Create(vm.DxvkHdr, game.Name))
            : Task.CompletedTask);
}
