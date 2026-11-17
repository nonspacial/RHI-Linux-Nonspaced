namespace RHI.Desktop.Layout;

/// <summary>
/// Decides how many toolbar items stay in the row. Items overflow from the right end, in order; once anything
/// overflows, a '…' button of <c>overflowButtonWidth</c> is appended after the kept items. Separators never end
/// the kept row and never start the overflow menu.
/// </summary>
public static class ToolbarOverflow
{
    /// <summary>Number of leading items that remain in the row (0..widths.Count).</summary>
    /// <param name="widths">Desired width of each item, margins included, in authored order.</param>
    /// <param name="isSeparator">Whether each item is a group separator.</param>
    /// <param name="spacing">Gap between neighbouring items (and before the overflow button).</param>
    /// <param name="available">Width available to the items plus the overflow button.</param>
    /// <param name="overflowButtonWidth">Desired width of the '…' button.</param>
    public static int VisibleCount(IReadOnlyList<double> widths, IReadOnlyList<bool> isSeparator, double spacing,
        double available, double overflowButtonWidth)
    {
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(isSeparator);
        if (widths.Count != isSeparator.Count) throw new ArgumentException("One separator flag per item is required.", nameof(isSeparator));
        var n = widths.Count;
        if (n == 0) return 0;

        // prefix[k] = width of the first k items including the gaps between them.
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
            prefix[i + 1] = prefix[i] + widths[i] + (i > 0 ? spacing : 0);
        if (prefix[n] <= available) return n;

        for (var k = n - 1; k > 0; k--)
        {
            var kept = k;
            while (kept > 0 && isSeparator[kept - 1]) kept--;
            if (kept == 0) break;
            if (prefix[kept] + spacing + overflowButtonWidth <= available) return kept;
        }
        return 0;
    }

    /// <summary>Indexes of the items that go into the overflow menu, in authored order, separators excluded.</summary>
    public static IReadOnlyList<int> Hidden(IReadOnlyList<bool> isSeparator, int visibleCount)
    {
        ArgumentNullException.ThrowIfNull(isSeparator);
        var hidden = new List<int>();
        for (var i = Math.Max(0, visibleCount); i < isSeparator.Count; i++)
            if (!isSeparator[i]) hidden.Add(i);
        return hidden;
    }

    /// <summary>
    /// w14 final review m5: how many of the leading group's overflowable children (its last ones, in order) stay in the row
    /// once every item has gone into the menu. They go from the right end until the leading group fits
    /// <paramref name="maxLeadingWidth"/>.
    /// </summary>
    /// <param name="leadingWidth">The whole leading group's width, every overflowable child and the gaps included.</param>
    /// <param name="overflowableWidths">The overflowable children's widths, in order (the group's last children).</param>
    /// <param name="spacing">The leading group's gap.</param>
    public static int LeadingKept(double leadingWidth, IReadOnlyList<double> overflowableWidths, double spacing, double maxLeadingWidth)
    {
        ArgumentNullException.ThrowIfNull(overflowableWidths);
        var kept = overflowableWidths.Count;
        var width = leadingWidth;
        while (kept > 0 && width > maxLeadingWidth)
        {
            var w = overflowableWidths[kept - 1];
            if (w > 0) width -= w + spacing;
            kept--;
        }
        return kept;
    }
}
