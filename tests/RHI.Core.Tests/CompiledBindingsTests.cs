using System.Reflection;
using RHI.Core.Models;
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
    [InlineData(typeof(MainWindowViewModel), nameof(MainWindowViewModel.SelectedGame))]
    [InlineData(typeof(MainWindowViewModel), nameof(MainWindowViewModel.Components))]
    [InlineData(typeof(ComponentRowViewModel), nameof(ComponentRowViewModel.Status))]
    [InlineData(typeof(ComponentRowViewModel), nameof(ComponentRowViewModel.Command))]
    [InlineData(typeof(DetectedGame), nameof(DetectedGame.Name))]
    public void MainWindow_BindingPaths_AreCompiledAgainstTheirDataType(Type dataType, string property)
        => Assert.Contains($"{dataType.FullName},{dataType.Assembly.GetName().Name}.{property}!Getter", CompiledAccessors);
}
