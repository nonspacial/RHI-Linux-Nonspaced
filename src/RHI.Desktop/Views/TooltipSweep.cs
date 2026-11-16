using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using RHI.Desktop.Localization;

namespace RHI.Desktop.Views;

/// <summary>
/// #302 (upstream 2.8.9 tooltip sweep): the main window's filters, section headings, collapsed-section summaries (and
/// their abbreviations), the DLSS / Streamline summary and the graphics API and engine / architecture labels explain
/// themselves on hover. Applied from code to the controls by what they are (a filter's parameter, a section's tag), so
/// a control that already has its own tooltip keeps it and layouts that move these controls keep their tooltips.
/// </summary>
internal static class TooltipSweep
{
    private static readonly Dictionary<string, string> FilterTips = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All Games"] = "Tip.FilterAll",
        ["Favourites"] = "Tip.FilterFavourites",
        ["Installed"] = "Tip.FilterInstalled",
        ["Unreal"] = "Tip.FilterUnreal",
        ["Unity"] = "Tip.FilterUnity",
        ["Other"] = "Tip.FilterOther",
        ["RenoDX"] = "Tip.FilterRenoDx",
        ["Luma"] = "Tip.FilterLuma",
        ["Hidden"] = "Tip.FilterHidden",
    };

    private static readonly Dictionary<string, string> SectionTips = new(StringComparer.Ordinal)
    {
        // w14/layouts-a (#304): upstream's seven sections.
        ["ManagedComponents"] = "Tip.SectionComponents",
        ["Runtime"] = "Tip.SectionRuntime",
        ["NeuralRendering"] = "Tip.SectionNeural",
        ["DlssStreamline"] = "Tip.SectionDlss",
        ["DriverSettings"] = "Tip.SectionDriver",
        ["Management"] = "Sec.ManagementNote",
        ["Extras"] = "Tip.SectionExtras",
    };

    private static readonly Dictionary<string, string> LabelTips = new(StringComparer.Ordinal)
    {
        ["Ui.GraphicsApi"] = "Tip.GraphicsApi",
        ["Ui.EngineArchitecture"] = "Tip.EngineArchitecture",
    };

    /// <summary>The tooltip key for a library filter's parameter; null when it has none.</summary>
    internal static string? FilterTipKey(string? parameter) => parameter is not null && FilterTips.TryGetValue(parameter, out var key) ? key : null;

    /// <summary>The tooltip key for a section's tag; null when it has none.</summary>
    internal static string? SectionTipKey(string? tag) => tag is not null && SectionTips.TryGetValue(tag, out var key) ? key : null;

    public static void Apply(Control root)
    {
        foreach (var node in root.GetLogicalDescendants().OfType<Control>().ToList())
        {
            switch (node)
            {
                case Button { Classes: var classes } chip when classes.Contains("chip") && FilterTipKey(chip.CommandParameter as string) is { } filter:
                    Set(chip, filter);
                    break;
                case Border { Tag: string tag } card when card.Classes.Contains("card") && SectionTipKey(tag) is { } section:
                    // The section's title: the first large heading in its card.
                    if (card.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.FontSize >= 16 && t.FontWeight >= FontWeight.SemiBold) is { } title)
                        Set(title, section);
                    if (card.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name?.EndsWith("SectionSummary", StringComparison.Ordinal) == true) is { } summary)
                        Set(summary, "Tip.SectionSummary");
                    break;
                case TextBlock { Name: "DlssDefaultsSummaryText" } dlss:
                    Set(dlss, "Tip.DlssSummary");
                    break;
            }
        }
        // Labels identified by their text resource (the label rows of the game panel).
        foreach (var (resource, tip) in LabelTips)
        {
            var text = UiLocalizationService.Text(resource);
            foreach (var label in root.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Text == text))
                Set(label, tip);
        }
    }

    private static void Set(Control control, string key)
    {
        if (ToolTip.GetTip(control) is not null) return;
        ToolTip.SetTip(control, UiLocalizationService.Text(key));
        ToolTip.SetShowDelay(control, MiddleEllipsisText.TooltipDelay);
    }
}
