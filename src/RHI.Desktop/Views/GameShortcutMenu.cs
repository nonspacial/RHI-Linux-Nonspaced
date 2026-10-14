using Avalonia.Controls;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

/// <summary>
/// The library card's right-click "Create shortcut" item (#162): "On the desktop" and "In the applications menu", each
/// written only when chosen (owner decision). vmsplit's #161 card menu adds it with one line at its named slot
/// (<c>GameShortcutMenu.Create(vm, row.Game)</c>); hidden when the app was built without the parity features.
/// </summary>
internal static class GameShortcutMenu
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static MenuItem Create(MainWindowViewModel vm, DetectedGame game)
    {
        var item = new MenuItem { Header = T("Parity.ShortcutCreate"), IsVisible = vm.Parity is not null, Name = "CreateShortcutMenuItem" };
        ToolTip.SetTip(item, T("Parity.ShortcutTip"));
        ToolTip.SetShowDelay(item, MiddleEllipsisText.TooltipDelay);
        if (vm.Parity is not { } parity) return item;
        item.Items.Add(Place(T("Parity.ShortcutOnDesktopItem"), ShortcutPlace.Desktop, "CreateShortcutDesktopMenuItem"));
        item.Items.Add(Place(T("Parity.ShortcutInMenuItem"), ShortcutPlace.ApplicationsMenu, "CreateShortcutMenuMenuItem"));
        return item;

        MenuItem Place(string header, ShortcutPlace place, string name)
        {
            var choice = new MenuItem { Header = header, Name = name };
            choice.Click += (_, _) => UiExceptionGuard.Forget(CreateAsync(place), "CreateShortcut");
            return choice;
        }

        async Task CreateAsync(ShortcutPlace place) => vm.StatusText = await parity.Shortcuts.CreateAsync(game, place);
    }
}
