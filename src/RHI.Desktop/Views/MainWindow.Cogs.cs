using Avalonia.Controls;
using Avalonia.Interactivity;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>w14/ui-settings (#297): the ⚙ of a component with nothing to set (upstream 2.9.0: DOF Fix) says so.</summary>
public sealed partial class MainWindow
{
    private void EmptySettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(()
        => sender is Button { DataContext: ComponentRowViewModel { HasEmptySettings: true } row } && DataContext is MainWindowViewModel vm
            ? ShowFittedDialogAsync(new CogDialog(row.Name, vm.SelectedGame?.Name).Build(height: 260))
            : Task.CompletedTask);
}
