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

    /// <summary>w14/layouts-a (#297 owner decision): ASI Loader's ⚙ has nothing to set (upstream 2.9.0 BuildUalRow).</summary>
    private void AsiLoaderSettings_Click(object? sender, RoutedEventArgs e) => RunUiSafe(()
        => DataContext is MainWindowViewModel vm
            ? ShowFittedDialogAsync(new CogDialog(ExtrasViewModel.UalCogName, vm.SelectedGame?.Name).Build(height: 260))
            : Task.CompletedTask);
}
