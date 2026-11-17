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
    /// The index of the control <paramref name="direction"/> of <paramref name="current"/>, -1 when there is none. Up / down go
    /// by rows (upstream 2.9.4 MainWindow.Controller.Rows): to the nearest row that way — the controls level with the nearest
    /// one — and in it the control nearest across (overlapping first, then by centres), so a row's control on the right
    /// (a setting's drop-down) is reached before a button further down. Left / right go along: the nearest control whose
    /// centre lies that way, a control off the row counting three times its offset.
    /// </summary>
    public static int Next(IReadOnlyList<Rect> candidates, Rect current, PadAction direction)
    {
        if (direction is PadAction.Up or PadAction.Down) return NextRow(candidates, current, direction == PadAction.Down);
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
                case PadAction.Right when p.X > c.X + 1 && r.Left >= current.Left + 1: along = p.X - c.X; across = Overlap(r.Top, r.Bottom, current.Top, current.Bottom, p.Y - c.Y); break;
                case PadAction.Left when p.X < c.X - 1 && r.Right <= current.Right - 1: along = c.X - p.X; across = Overlap(r.Top, r.Bottom, current.Top, current.Bottom, p.Y - c.Y); break;
                default: continue;
            }
            var score = along + 3 * across;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private static int NextRow(IReadOnlyList<Rect> candidates, Rect current, bool down)
    {
        var c = current.Center;
        bool That(Rect r) => r != current && (down ? r.Center.Y > c.Y + 1 && r.Top >= current.Top + 1 : r.Center.Y < c.Y - 1 && r.Bottom <= current.Bottom - 1);
        // The nearest row that way: the control whose near edge is closest, and everything level with it.
        var nearest = -1;
        for (var i = 0; i < candidates.Count; i++)
            if (That(candidates[i]) && (nearest < 0 || (down ? candidates[i].Top < candidates[nearest].Top : candidates[i].Bottom > candidates[nearest].Bottom)))
                nearest = i;
        if (nearest < 0) return -1;
        var row = candidates[nearest];
        var best = -1;
        (double Gap, double Centre) bestScore = (double.MaxValue, double.MaxValue);
        for (var i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i];
            if (!That(r) || !(r.Top < row.Bottom - 1 && r.Bottom > row.Top + 1)) continue; // not level with the row
            var gap = c.X < r.Left ? r.Left - c.X : c.X > r.Right ? c.X - r.Right : 0;
            var score = (gap, Math.Abs(r.Center.X - c.X));
            if (score.CompareTo(bestScore) < 0) { bestScore = score; best = i; }
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
