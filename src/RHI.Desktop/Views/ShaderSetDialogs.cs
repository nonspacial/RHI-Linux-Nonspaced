using Avalonia.Controls;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

/// <summary>
/// #179: opens the existing Shaders/Add-ons dialog (and a pack's Files dialog) for a game's own list (Select) or for the
/// global set and add-ons, from the ReShade section's choices and from Settings → ReShade. Same sizes and fitting as the
/// main window's own Shaders/Add-ons button.
/// </summary>
internal static class ShaderSetDialogs
{
    private const double SideMargin = 24;

    /// <summary>The game's own list of <paramref name="part"/>: Apply makes it Select.</summary>
    public static async Task ShowSelectListAsync(Control anchor, MainWindowViewModel vm, ShaderAddonSetPart part)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner || vm.SelectedGame is null) return;
        vm.StatusText = UiLocalizationService.Text("Win.LoadingShadersAddOnsCatalog");
        ShaderAddonManagerUiSnapshot? snapshot;
        try
        {
            snapshot = await vm.ReShadeProfile.GetShaderAddonManagerSnapshotAsync(selectPart: part);
        }
        catch (Exception ex)
        {
            vm.StatusText = UiLocalizationService.Format("Win.CouldNotLoadShadersAddOns", UiLocalizationService.Present(ex.Message));
            return;
        }
        if (snapshot is null) return;
        var model = ShaderAddonsDialogViewModel.For(vm, snapshot, OpenFolder, pack => ShowFilesAsync(owner, vm, pack), part);
        await ShowAsync(owner, model);
    }

    /// <summary>The global shader set and add-ons (Settings → ReShade), opened on <paramref name="part"/>'s tab.</summary>
    public static async Task ShowGlobalAsync(Control anchor, MainWindowViewModel vm, ShaderAddonSetPart part, Action<string> show)
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
        var model = ShaderAddonsDialogViewModel.ForGlobal(vm, snapshot, OpenFolder, pack => ShowFilesAsync(owner, vm, pack), part);
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

    private static (bool Opened, string? Error) OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, ex.Message);
        }
        return (LinuxDesktopOpener.TryOpenDirectory(path, out var error), error);
    }
}
