using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #296 (upstream 2.8.9/2.9.3 Settings page): the eight sections in upstream's order. The keys are upstream's
/// (<c>Tag="Section:&lt;Key&gt;"</c> in its MainWindow.xaml), so a saved last section means the same section on both.
/// </summary>
public static class SettingsSections
{
    public const string General = "General";
    public const string Accounts = "Accounts";
    public const string ReShade = "ReShade";
    public const string Display = "Display";
    public const string Dlss = "Dlss";
    public const string Driver = "Driver";
    public const string Components = "Components";
    public const string Tools = "Tools";
    /// <summary>w14/layouts-b (#307, upstream 2.9.2): Achievements, just before Tools &amp; Maintenance.</summary>
    public const string Achievements = "Achievements";

    /// <summary>Upstream's order: General, Accounts &amp; Data, ReShade &amp; RenoDX, Display &amp; HDR, DLSS / Streamline,
    /// Driver Settings, Components, Achievements (2.9.2), Tools &amp; Maintenance.</summary>
    public static IReadOnlyList<string> Order { get; } = [General, Accounts, ReShade, Display, Dlss, Driver, Components, Achievements, Tools];

    public static string Title(string key) => UiLocalizationService.Text(key switch
    {
        General => "Set.SectionGeneral",
        Accounts => "Set.SectionAccounts",
        ReShade => "Set.SectionReShade",
        Display => "Set.SectionDisplay",
        Dlss => "Set.SectionDlss",
        Driver => "Set.SectionDriver",
        Components => "Set.SectionComponents",
        Achievements => "Ach.Section",
        _ => "Set.SectionTools",
    });

    public static string Description(string key) => UiLocalizationService.Text(key switch
    {
        General => "Set.SectionGeneralDesc",
        Accounts => "Set.SectionAccountsDesc",
        ReShade => "Set.SectionReShadeDesc",
        Display => "Set.SectionDisplayDesc",
        Dlss => "Set.SectionDlssDesc",
        Driver => "Set.SectionDriverDesc",
        Components => "Set.SectionComponentsDesc",
        Achievements => "Ach.SectionDesc",
        _ => "Set.SectionToolsDesc",
    });

    /// <summary>
    /// The section Settings opens on: the one left last time when it still exists (a key from an older build, or none,
    /// opens General). Matched ignoring case, as upstream's <c>FindSection</c>.
    /// </summary>
    public static string Restore(string? saved)
        => Order.FirstOrDefault(key => string.Equals(key, saved?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? General;
}

/// <summary>
/// One searchable text of the Settings page: a setting's name (label, button or check box text) with its help line or
/// tooltip, where it is (<see cref="Context"/>: "Section › Card") and the element to scroll to and outline.
/// </summary>
public sealed record SettingsSearchEntry(string Label, string? Tooltip, string SectionKey, string Context, object? Target = null, object? Card = null);

/// <summary>One "Find a setting" result: the entry and the line shown for it.</summary>
public sealed record SettingsSearchResult(SettingsSearchEntry Entry, string Display);

/// <summary>
/// #296: "Find a setting" (upstream SettingsHandler.Navigation): searches names first, then help and tooltips, one result
/// per setting, the label text winning over its control's tooltip; at most <see cref="MaxResults"/> are shown.
/// </summary>
public static class SettingsSearch
{
    public const int MaxResults = 60;

    /// <summary>The matching entries in page order. An empty or blank query matches nothing (the page shows again).</summary>
    public static IReadOnlyList<SettingsSearchResult> Match(IEnumerable<SettingsSearchEntry> entries, string? query)
    {
        var q = query?.Trim() ?? "";
        var results = new List<SettingsSearchResult>();
        if (q.Length == 0) return results;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Label)) continue;
            var inLabel = e.Label.Contains(q, StringComparison.OrdinalIgnoreCase);
            var inTip = !inLabel && e.Tooltip is { } tip && tip.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!inLabel && !inTip) continue;
            // One row per label/control: the label (earlier in the page) wins over its control's tooltip
            if (!seen.Add(e.Context + "\u001F" + e.Label.Trim())) continue;
            var display = inLabel
                ? Excerpt(e.Label.Trim(), q, 160)
                : $"{Excerpt(e.Label.Trim(), e.Label.Trim(), 60)} — {Excerpt(e.Tooltip!.Trim(), q, 110)}";
            results.Add(new(e, display));
        }
        return results;
    }

    /// <summary>"No settings match", "1 setting matches", "N settings match", plus "showing the first 60" when cut.</summary>
    public static string CountText(int count, string query)
    {
        var q = query.Trim();
        var line = count switch
        {
            0 => UiLocalizationService.Format("Set.FindNone", q),
            1 => UiLocalizationService.Format("Set.FindOne", q),
            _ => UiLocalizationService.Format("Set.FindMany", count, q),
        };
        return count > MaxResults ? line + UiLocalizationService.Format("Set.FindCapped", MaxResults) : line;
    }

    /// <summary>Shortens <paramref name="text"/> to about <paramref name="max"/> characters around the first match.</summary>
    public static string Excerpt(string text, string q, int max)
    {
        if (text.Length <= max) return text;
        var idx = Math.Max(0, text.IndexOf(q, StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, Math.Min(idx - max / 3, text.Length - max));
        var part = text.Substring(start, max).Trim();
        return (start > 0 ? "…" : "") + part + (start + max < text.Length ? "…" : "");
    }
}

/// <summary>
/// #296: which section the index highlights while the page scrolls (upstream's scroll spy): the last section whose top
/// has reached the top quarter of the view; at the very bottom, the last section (it may be too short to reach the top).
/// A section jumped to stays highlighted while the view stays where the jump put it.
/// </summary>
public static class SettingsScrollSpy
{
    /// <param name="sectionTops">Each section's top in the scrolled content, in page order.</param>
    public static string? Highlight(IReadOnlyList<(string Key, double Top)> sectionTops, double offset, double viewport, double extent,
        string? pinnedKey = null, double pinnedOffset = double.NaN)
    {
        if (sectionTops.Count == 0) return null;
        if (pinnedKey is not null && !double.IsNaN(pinnedOffset) && Math.Abs(offset - pinnedOffset) < 2) return pinnedKey;
        if (extent > 0 && viewport > 0 && offset + viewport >= extent - 2 && offset > 0) return sectionTops[^1].Key;
        var line = offset + Math.Max(24, viewport / 4);
        var current = sectionTops[0].Key;
        foreach (var (key, top) in sectionTops)
            if (top <= line) current = key;
        return current;
    }

    /// <summary>
    /// The scroll offset that centres an element of <paramref name="height"/> at <paramref name="top"/> in the view (upstream
    /// GoToSearchResult), clamped to the scrollable range. A tall element is aligned to the top instead.
    /// </summary>
    public static double CentreOffset(double top, double height, double viewport, double extent)
    {
        var shown = Math.Min(height, viewport);
        var margin = viewport > 0 ? (viewport - shown) / 2 : 80;
        return Math.Clamp(top - margin, 0, Math.Max(0, extent - viewport));
    }
}
