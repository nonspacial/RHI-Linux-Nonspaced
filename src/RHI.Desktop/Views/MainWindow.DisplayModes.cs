using Avalonia.Controls;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/display-modes (#311): the window's side of display modes — the switch on the game page (beside the HDR and
/// resolution rows), and the game's display mode settings (the ⚙ beside the switch, and the game menu's "Display mode
/// settings…"). Other page layouts host <see cref="DisplayModeSwitchView"/> the same way (<see cref="AttachDisplayModeSwitch"/>).
/// </summary>
public partial class MainWindow
{
    private void AttachDisplayModes()
        => DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.DisplayModes.OpenGameSettings = ShowDisplayModeGameSettingsAsync;
            if (this.FindControl<DisplayModeSwitchView>("DisplayModeSwitch") is { } view) AttachDisplayModeSwitch(view, vm);
        };

    /// <summary>Connects a display mode switch (this page's, or one a page layout builds) to the selected game.</summary>
    internal void AttachDisplayModeSwitch(DisplayModeSwitchView view, MainWindowViewModel vm)
        => view.Attach(vm.DisplayModes, () => vm.SelectedGame, ShowDisplayModeGameSettingsAsync);

    /// <summary>The game's display mode settings: its mode, and a Custom mode just for it (upstream ShowDisplayModeGameDialogAsync).</summary>
    internal async Task ShowDisplayModeGameSettingsAsync(DetectedGame game)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var cog = await DisplayModeDialogs.GameSettingsAsync(vm.DisplayModes, game, vm.Updates.UpdateAll.IsRunning(game));
        await ShowFittedDialogAsync(cog.Build());
    }
}
