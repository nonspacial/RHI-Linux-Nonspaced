using Avalonia.Controls;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

/// <summary>
/// #179: opens the existing Shaders/Add-ons dialog (and a pack's Files dialog) for the global set and add-ons from
/// Settings → ReShade. Same sizes and fitting as the main window's own Shaders/Add-ons button.
/// </summary>
internal static class ShaderSetDialogs
{
    private const double SideMargin = 32; // as MainWindow.DialogSideMargin

    /// <summary>The global shader set and add-ons (Settings → ReShade → Global shaders and add-ons…).</summary>
    public static async Task ShowGlobalAsync(Control anchor, MainWindowViewModel vm, Action<string> show)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) return;
        ShaderAddonManagerUiSnapshot snapshot;
        try
        {
            snapshot = await vm.ReShadeProfile.GetGlobalShaderAddonSnapshotAsync();
        }
        catch (Exception ex)
        {
            show(UiLocalizationService.Format("Win.CouldNotLoadShadersAddOns", UiLocalizationService.Present(ex.Message)));
            return;
        }
        var model = ShaderAddonsDialogViewModel.ForGlobal(vm, snapshot, OpenFolder, pack => ShowFilesAsync(owner, vm, pack));
        await ShowAsync(owner, model);
        show(vm.StatusText);
    }

    private static Task ShowAsync(Window owner, ShaderAddonsDialogViewModel model)
    {
        // The layout is the view model's data template (Views/Dialogs/ShaderAddonsDialogView.axaml; epic #89).
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 820,
            Height = 720,
            MinWidth = 620,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };
        return ShowFittedAsync(owner, dialog);
    }

    private static async Task ShowFilesAsync(Window owner, MainWindowViewModel vm, ReShadeShaderPackInfo pack)
    {
        var files = await vm.ReShadeProfile.GetShaderPackFilesAsync(pack.Id);
        var model = new ShaderPackFilesDialogViewModel(pack, files, vm.ReShadeProfile.SetShaderPackExcludedFiles);
        var dialog = new Window
        {
            Title = model.WindowTitle,
            Width = 720,
            Height = 680,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = model
        };
        model.CloseRequested += dialog.Close;
        await ShowFittedAsync(owner, dialog);
    }

    // As the main window fits its dialogs: never larger than the owner (its own minimum shrinks with it).
    private static Task ShowFittedAsync(Window owner, Window dialog)
    {
        var maxWidth = Math.Max(200, (owner.ClientSize.Width > 0 ? owner.ClientSize.Width : owner.Width) - SideMargin);
        var maxHeight = Math.Max(160, owner.ClientSize.Height > 0 ? owner.ClientSize.Height : owner.Height);
        dialog.MaxWidth = Math.Min(dialog.MaxWidth, maxWidth);
        dialog.MinWidth = Math.Min(dialog.MinWidth, maxWidth);
        if (!double.IsNaN(dialog.Width)) dialog.Width = Math.Min(dialog.Width, maxWidth);
        dialog.MaxHeight = Math.Min(dialog.MaxHeight, maxHeight);
        dialog.MinHeight = Math.Min(dialog.MinHeight, maxHeight);
        if (!double.IsNaN(dialog.Height)) dialog.Height = Math.Min(dialog.Height, maxHeight);
        return dialog.ShowDialog(owner);
    }

    // The dialog presents the reason itself (ErrorLineText), as for the main window's own folder buttons.
    private static (bool Opened, string? Error) OpenFolder(string path) => (TryOpenFolder(path, out var error), error);

    private static bool TryOpenFolder(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
        return LinuxDesktopOpener.TryOpenDirectory(path, out error);
    }
}
