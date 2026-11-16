using Avalonia.Controls;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/display-modes (#311): the window's side of display modes — the switch beside the HDR and resolution controls
/// (Classic+'s Game Overrides and the Tabs header), and the game's display mode settings (the ⚙ beside the switch, and
/// the game menu's "Display mode settings…"). Other page layouts host <see cref="DisplayModeSwitchView"/> the same way (<see cref="AttachDisplayModeSwitch"/>).
/// </summary>
public partial class MainWindow
{
    private void AttachDisplayModes()
        => DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.DisplayModes.OpenGameSettings = ShowDisplayModeGameSettingsAsync;
            // Classic+ (Game Overrides, under the resolution row) and Tabs (the game header, beside HDR on launch and the
            // resolution switch); Spotlight (#305) attaches its own the same way.
            foreach (var name in SwitchHosts)
                if (this.FindControl<DisplayModeSwitchView>(name) is { } view) AttachDisplayModeSwitch(view, vm);
        };

    /// <summary>The switches this window's layouts host, beside the HDR and resolution controls (upstream 2.9.3/2.9.4).</summary>
    internal static readonly string[] SwitchHosts = ["DisplayModeSwitch", "TabsDisplayModeSwitch"];

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
