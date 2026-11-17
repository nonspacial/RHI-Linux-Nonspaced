using Avalonia;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Controller;

namespace RHI.Desktop.ViewModels;

/// <summary>Where the controller's focus is, for the footer's hints.</summary>
public enum PadContext
{
    /// <summary>On the covers.</summary>
    Shelf,
    /// <summary>On the game page (banner, tiles).</summary>
    Page,
    /// <summary>In the side panel (a section, Screenshots, …).</summary>
    Panel,
    /// <summary>A ⚙ open in the side panel.</summary>
    PanelCog,
    /// <summary>A menu or a drop-down list is open.</summary>
    Menu,
}

/// <summary>One footer hint: the button and what it does.</summary>
public sealed record PadHint(string Button, string Action);

/// <summary>
/// w14/layouts-b (#308, upstream 2.9.0–2.9.2 MainWindow.Controller / .Rows / .Hints): the controller's rules that do not need
/// the window — the next control in a direction (row navigation: straight up / down and left / right first), and the
/// footer's contextual hints in upstream's words.
/// </summary>
public static class ControllerPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>
    /// The index of the control <paramref name="direction"/> of <paramref name="current"/>: only controls whose centre lies that
    /// way; the nearest in that direction wins, a control off the row (or column) counting three times its offset, so up /
    /// down go straight to the row above or below (upstream). -1 when there is none.
    /// </summary>
    public static int Next(IReadOnlyList<Rect> candidates, Rect current, PadAction direction)
    {
        var c = current.Center;
        var best = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i];
            if (r == current) continue;
            var p = r.Center;
            double along, across;
            switch (direction)
            {
                case PadAction.Down when p.Y > c.Y + 1 && r.Top >= current.Top + 1: along = p.Y - c.Y; across = Overlap(r.Left, r.Right, current.Left, current.Right, p.X - c.X); break;
                case PadAction.Up when p.Y < c.Y - 1 && r.Bottom <= current.Bottom - 1: along = c.Y - p.Y; across = Overlap(r.Left, r.Right, current.Left, current.Right, p.X - c.X); break;
                case PadAction.Right when p.X > c.X + 1 && r.Left >= current.Left + 1: along = p.X - c.X; across = Overlap(r.Top, r.Bottom, current.Top, current.Bottom, p.Y - c.Y); break;
                case PadAction.Left when p.X < c.X - 1 && r.Right <= current.Right - 1: along = c.X - p.X; across = Overlap(r.Top, r.Bottom, current.Top, current.Bottom, p.Y - c.Y); break;
                default: continue;
            }
            var score = along + 3 * across;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    // Controls that share the row (or column) are not off it at all; otherwise the centres' offset counts.
    private static double Overlap(double a1, double a2, double b1, double b2, double centreOffset)
        => Math.Min(a2, b2) - Math.Max(a1, b1) > 0 ? 0 : Math.Abs(centreOffset);

    /// <summary>The footer's hints where the focus is (upstream's lists).</summary>
    public static IReadOnlyList<PadHint> Hints(PadContext context, bool hasCollections, PadStyle style)
    {
        string N(string xbox) => ButtonName(xbox, style);
        return context switch
        {
            PadContext.Menu => [new(N("A"), T("Pad.Choose")), new(N("B"), T("Pad.Close"))],
            PadContext.PanelCog or PadContext.Panel =>
            [
                new(N("A"), T("Pad.Select")), new(N("B"), T(context == PadContext.PanelCog ? "Pad.Back" : "Pad.Close")),
                new(N("LB") + " / " + N("RB"), T("Pad.Section")), new(N("Y"), T("Pad.Launch")), new(T("Pad.RightStick"), T("Pad.Scroll")),
            ],
            _ =>
            [
                new(N("A"), T("Pad.Select")),
                .. context == PadContext.Shelf ? Array.Empty<PadHint>() : [new(N("B"), T("Pad.Covers"))],
                new(N("Y"), T("Pad.Launch")), new(N("X"), T("Pad.GameMenu")),
                new(N("LB") + " / " + N("RB"), T("Pad.Game")), new(N("LT") + " / " + N("RT"), T("Pad.TenGames")),
                new("LS", T("Pad.Filter")), new("RS", T(hasCollections ? "Pad.Collection" : "Pad.Covers")),
                new(N("View"), T("Pad.Filters")), new(N("Menu"), T("Pad.Settings")),
            ],
        };
    }

    /// <summary>The button's name on the connected pad: Xbox letters, PlayStation shapes (upstream 86e609b).</summary>
    public static string ButtonName(string xbox, PadStyle style) => style == PadStyle.PlayStation
        ? xbox switch
        {
            "A" => "✕", "B" => "○", "X" => "□", "Y" => "△", "LB" => "L1", "RB" => "R1", "LT" => "L2", "RT" => "R2",
            "View" => "Create", "Menu" => "Options", _ => xbox,
        }
        : xbox;

    /// <summary>The connected pad's style from its name (SDL's), for the hints.</summary>
    public static PadStyle StyleOf(string? name)
        => name is not null && (name.Contains("PS4", StringComparison.OrdinalIgnoreCase) || name.Contains("PS5", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase) || name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("PlayStation", StringComparison.OrdinalIgnoreCase))
            ? PadStyle.PlayStation
            : PadStyle.Xbox;

    /// <summary>One line of hints for the footer.</summary>
    public static string Line(IEnumerable<PadHint> hints) => string.Join("   ", hints.Select(h => $"{h.Button} {h.Action}"));
}

/// <summary>Whose button names the hints use.</summary>
public enum PadStyle { Xbox, PlayStation }
