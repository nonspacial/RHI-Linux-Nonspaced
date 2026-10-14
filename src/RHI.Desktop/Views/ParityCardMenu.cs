using Avalonia.Controls;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

/// <summary>
/// The w3/parity items of a library card's right-click menu: "Create shortcut" → "On the desktop" / "In the applications
/// menu" (#162; each written only when chosen, owner decision) and "Nexus Mods page" (#99; only for a game the upstream
/// manifest gives a Nexus page). vmsplit's #161 card menu adds them with one line at its named slot:
/// <c>foreach (var item in ParityCardMenu.Items(vm, row.Game)) menu.Items.Add(item);</c>. Hidden when the app was built
/// without the parity features.
/// </summary>
internal static class ParityCardMenu
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static IReadOnlyList<MenuItem> Items(MainWindowViewModel vm, DetectedGame game) => [CreateShortcut(vm, game), NexusPage(vm, game)];

    public static MenuItem CreateShortcut(MainWindowViewModel vm, DetectedGame game)
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

    public static MenuItem NexusPage(MainWindowViewModel vm, DetectedGame game)
    {
        var url = NexusLinkPresenter.PageFor(game);
        var item = new MenuItem { Header = T("Parity.NexusPage"), IsVisible = url is not null, Name = "NexusPageMenuItem" };
        if (url is null) return item;
        ToolTip.SetTip(item, url);
        ToolTip.SetShowDelay(item, MiddleEllipsisText.TooltipDelay);
        item.Click += (_, _) =>
        {
            if (!LinuxDesktopOpener.TryOpenUri(url, out var error))
                vm.StatusText = UiLocalizationService.Format("Parity.LinkNotOpened", UiLocalizationService.Present(error));
        };
        return item;
    }
}
