using System.Windows.Input;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// One entry of a library card's right-click menu (Refs #161): the existing actions, shown for the card the menu was
/// opened on (opening it selects that card, as upstream).
/// </summary>
/// <param name="Id">Stable name for tests and accessibility (<c>favourite</c>, <c>hide</c>, <c>open-folder</c>, <c>notes</c>).</param>
/// <param name="SeparatorBefore">A separator line goes above this entry.</param>
public sealed record CardMenuItem(string Id, string Label, ICommand Command, bool IsEnabled = true, bool SeparatorBefore = false);

/// <summary>What View notes shows: the manifest's note for the game and its optional link (upstream's card notes).</summary>
public sealed record GameNotesViewModel(string Title, string Notes, string? Url, string LinkLabel);
