using System.Windows.Input;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// One entry of a library card's right-click menu (Refs #161): the existing actions, shown for the card the menu was
/// opened on (opening it selects that card, as upstream). An entry exists only where it applies, so there is no
/// disabled state: an action that does not apply to the card is left out of the menu.
/// </summary>
/// <param name="Id">Stable name for tests and accessibility (<c>favourite</c>, <c>hide</c>, <c>open-folder</c>, <c>notes</c>,
/// <c>show-in-owning-app</c>, <c>create-shortcut</c> → <c>shortcut-desktop</c>/<c>shortcut-menu</c>, <c>nexus-page</c>).</param>
/// <param name="SeparatorBefore">A separator line goes above this entry.</param>
/// <param name="Children">Sub-entries (a submenu); the entry's own <paramref name="Command"/> is then not used.</param>
/// <param name="Tip">Tooltip (shown after the app's 2 s delay), or null.</param>
public sealed record CardMenuItem(string Id, string Label, ICommand Command, bool SeparatorBefore = false,
    IReadOnlyList<CardMenuItem>? Children = null, string? Tip = null)
{
    /// <summary>The command of an entry that only opens its submenu.</summary>
    public static ICommand OpensSubmenu { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
}

/// <summary>What View notes shows: the manifest's note for the game and its optional link (upstream's card notes).</summary>
public sealed record GameNotesViewModel(string Title, string Notes, string? Url, string LinkLabel);
