using System.Reflection;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// UI-14 (#88): the main window's bindings are compiled, so a renamed view-model property breaks the build instead of
/// leaving a blank control at runtime. The XAML compiler emits one typed accessor per compiled binding path; reflection
/// bindings emit none.
/// </summary>
public sealed class CompiledBindingsTests
{
    private static readonly HashSet<string> CompiledAccessors = typeof(MainWindowViewModel).Assembly
        .GetType("CompiledAvaloniaXaml.XamlIlHelpers")?
        .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Select(m => m.Name)
        .ToHashSet(StringComparer.Ordinal) ?? [];

    [Theory]
    [InlineData(typeof(MainWindowViewModel), nameof(MainWindowViewModel.GameCountText))]
    [InlineData(typeof(MainWindowViewModel), nameof(MainWindowViewModel.SelectedGameRow))]
    [InlineData(typeof(MainWindowViewModel), nameof(MainWindowViewModel.Components))]
    [InlineData(typeof(ComponentRowViewModel), nameof(ComponentRowViewModel.Status))]
    [InlineData(typeof(ComponentRowViewModel), nameof(ComponentRowViewModel.Command))]
    [InlineData(typeof(GameRowViewModel), nameof(GameRowViewModel.Name))]
    // Split panels (epic #89 units 4–7): each section binds its own view model through a compiled DataContext.
    [InlineData(typeof(DlssPanelViewModel), nameof(DlssPanelViewModel.DlssSrPath))]
    [InlineData(typeof(ExtrasViewModel), nameof(ExtrasViewModel.UalStatusText))]
    [InlineData(typeof(ExtrasViewModel), nameof(ExtrasViewModel.SelectedNeuralRenderingDllVersion))]
    [InlineData(typeof(RuntimePanelViewModel), nameof(RuntimePanelViewModel.HdrProfileHint))]
    [InlineData(typeof(RuntimePanelViewModel), nameof(RuntimePanelViewModel.ShowHdrLaunchProfile))]
    [InlineData(typeof(OptionItem), nameof(OptionItem.Display))]
    [InlineData(typeof(UpdatesViewModel), nameof(UpdatesViewModel.CanUpdateAll))]
    [InlineData(typeof(ManualGamesViewModel), nameof(ManualGamesViewModel.CanRemoveManualGame))]
    [InlineData(typeof(ProxyComponentDialogViewModel), nameof(ProxyComponentDialogViewModel.ShowCancel))]
    // Dialogs moved out of the window's code-behind (epic #89): Views/Dialogs/*.axaml.
    [InlineData(typeof(DlssBatchDialogViewModel), nameof(DlssBatchDialogViewModel.CloseLabel))]
    [InlineData(typeof(DlssBatchGameRow), nameof(DlssBatchGameRow.IsChecked))]
    [InlineData(typeof(VersionChoice), nameof(VersionChoice.Selected))]
    [InlineData(typeof(RenoDxSettingsDialogViewModel), nameof(RenoDxSettingsDialogViewModel.Message))]
    [InlineData(typeof(RenoDxSettingRow), nameof(RenoDxSettingRow.SelectedIndex))]
    [InlineData(typeof(OptiScalerSettingsDialogViewModel), nameof(OptiScalerSettingsDialogViewModel.ShowCancel))]
    [InlineData(typeof(OptiScalerIniSetting), nameof(OptiScalerIniSetting.SelectedIndex))]
    [InlineData(typeof(AddGameDialogViewModel), nameof(AddGameDialogViewModel.Prefix))]
    [InlineData(typeof(EditGameDialogViewModel), nameof(EditGameDialogViewModel.BinaryDirectory))]
    [InlineData(typeof(ShaderAddonsDialogViewModel), nameof(ShaderAddonsDialogViewModel.CanApply))]
    [InlineData(typeof(ShaderPackRow), nameof(ShaderPackRow.IsChecked))]
    [InlineData(typeof(ShaderPackFilesDialogViewModel), nameof(ShaderPackFilesDialogViewModel.Status))]
    [InlineData(typeof(SettingsDialogViewModel), nameof(SettingsDialogViewModel.CloseToTrayEnabled))]
    public void MainWindow_BindingPaths_AreCompiledAgainstTheirDataType(Type dataType, string property)
        => Assert.Contains($"{dataType.FullName},{dataType.Assembly.GetName().Name}.{property}!Getter", CompiledAccessors);
}
