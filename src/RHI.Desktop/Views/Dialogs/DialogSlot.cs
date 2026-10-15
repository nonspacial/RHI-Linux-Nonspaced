using Avalonia.Controls;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>
/// Puts a code-built row from another lane (e.g. the OptiScaler nightly-build row) straight into a dialog template's
/// panel, in place of a named placeholder, so the panel's spacing and the row's own IsVisible behave exactly as when
/// the dialog was built in code (a wrapping ContentControl would stay visible, and spaced, while the row hides itself).
/// </summary>
internal static class DialogSlot
{
    /// <summary>Replaces <paramref name="current"/> with <paramref name="row"/> (or a hidden placeholder when null).</summary>
    public static Control Fill(Control current, Control? row)
    {
        var replacement = row ?? new Panel { IsVisible = false };
        if (ReferenceEquals(current, replacement) || current.Parent is not Panel panel) return current;
        var index = panel.Children.IndexOf(current);
        panel.Children[index] = replacement;
        return replacement;
    }
}
