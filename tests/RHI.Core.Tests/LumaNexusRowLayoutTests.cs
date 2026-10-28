using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// #245 correction (review F1; coordinator correction 2): the component row's Nexus button has a column of its own and comes
/// FIRST among the row's buttons — Nexus → ⚙ → Install/Remove → ⓘ — so a Luma Engine.ini ⚙ (Unreal games, #189) and
/// "Update/Open on Nexus" are both shown, side by side and never on top of each other, and every other row keeps the
/// ⚙ → Install/Remove → ⓘ order and columns it had before wave 12 (8f15e44). Only the Nexus column takes no spacing while
/// empty, so a row without the Nexus button lays out exactly as at 8f15e44, with or without a ⚙.
/// </summary>
public sealed class LumaNexusRowLayoutTests
{
    /// <summary>The component row as it was at 8f15e44: ⚙ column 3, Install/Remove 4, ⓘ 5, no skipped column.</summary>
    private const string BaseColumns = "220,180,*,Auto,Auto,Auto";
    private const int BaseGear = 3, BaseAction = 4, BaseInfo = 5;

    internal static XElement ComponentRow()
    {
        var path = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "Views", "MainWindow.axaml");
        var doc = XDocument.Load(path);
        // The component row: the ResponsiveRow that holds the Nexus button.
        return doc.Descendants().Single(e => e.Name.LocalName == "ResponsiveRow"
                                             && e.Elements().Any(c => ((string?)c.Attribute("Content"))?.Contains("LumaNexus.Label", StringComparison.Ordinal) == true));
    }

    private static int Column(XElement e) => int.Parse((string?)e.Attribute("Grid.Column") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
    private static bool Shows(XElement e, string text) => ((string?)e.Attribute("Content"))?.Contains(text, StringComparison.Ordinal) == true;
    private static bool IsNexus(XElement e) => Shows(e, "LumaNexus.Label");
    private static bool IsAction(XElement e) => Shows(e, "{Binding Action}");
    private static bool IsGear(XElement e) => (string?)e.Attribute("Content") == "⚙";
    private static bool IsInfo(XElement e) => (string?)e.Attribute("Content") == "ⓘ";

    [Fact]
    public void Template_NexusFirst_ThenGear_InstallRemove_Info_EachInItsOwnColumn()
    {
        var row = ComponentRow();
        var children = row.Elements().ToArray();
        var nexus = children.Single(IsNexus);
        var action = children.Single(IsAction);
        var gears = children.Where(IsGear).ToArray();
        var info = children.Single(IsInfo);

        Assert.NotEmpty(gears);
        Assert.Single(children, e => Column(e) == Column(nexus)); // nothing else is drawn in the Nexus button's place
        Assert.Single(gears.Select(Column).Distinct());           // every ⚙ in the one ⚙ column, as before
        Assert.All(gears, gear => Assert.True(Column(nexus) < Column(gear), "the Nexus button comes before every ⚙"));
        Assert.All(gears, gear => Assert.True(Column(gear) < Column(action), "every ⚙ comes before Install/Remove (as at 8f15e44)"));
        Assert.True(Column(action) < Column(info), "Install/Remove comes before ⓘ");
        Assert.Equal([Column(nexus) + 1, Column(nexus) + 2, Column(nexus) + 3], new[] { Column(gears[0]), Column(action), Column(info) });
        // Keyboard order = what the eye sees: the document order is the column order.
        var order = children.Where(e => IsNexus(e) || IsGear(e) || IsAction(e) || IsInfo(e)).Select(Column).ToArray();
        Assert.Equal(order.Order(), order);
        // Same look as the row's other buttons.
        Assert.Equal("rhi", (string?)nexus.Attribute("Classes"));
        Assert.Equal("2000", (string?)nexus.Attribute("ToolTip.ShowDelay"));
        Assert.Equal((string?)info.Attribute("Padding"), (string?)nexus.Attribute("Padding"));
        // The 8f15e44 columns with one Auto column (the Nexus one) put in front of the buttons; only that one is skipped.
        var columns = ((string?)row.Attribute("Columns"))!.Split(',');
        Assert.Equal(BaseColumns.Split(',').Take(Column(nexus)).Append("Auto").Concat(BaseColumns.Split(',').Skip(Column(nexus))), columns);
        Assert.Equal(Column(nexus).ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)row.Attribute("SkipEmptyColumns"));
    }

    // Every row without the Nexus button (all rows but a Nexus-only Luma mod's) lays out exactly as at 8f15e44: same
    // bounds for the name, status, note, ⚙, Install/Remove and ⓘ, at every width, with the ⚙ shown or hidden.
    [Theory]
    [InlineData(2061.0, true)]
    [InlineData(1600.0, true)]
    [InlineData(900.0, true)]
    [InlineData(700.0, true)]
    [InlineData(520.0, true)]
    [InlineData(300.0, true)]
    [InlineData(1600.0, false)]
    [InlineData(900.0, false)]
    [InlineData(740.0, false)]
    [InlineData(548.0, false)]
    [InlineData(520.0, false)]
    [InlineData(300.0, false)]
    public void ARowWithoutTheNexusButton_LaysOutExactlyAsAt8f15e44(double width, bool gearShown)
    {
        var now = FromTemplate(path => path switch { "LumaNexus.IsVisible" => false, "HasRenoDxSettings" => gearShown, _ => !path.StartsWith("Has", StringComparison.Ordinal) || path is "HasAction" or "HasInfo" });
        var before = new ResponsiveRow { Columns = BaseColumns, ColumnSpacing = 12, LineSpacing = 8, HeadColumns = 2, MinStarWidth = 160 };
        Border Cell(int column, double w, bool visible = true)
        {
            var b = new Border { Width = w, Height = 30, IsVisible = visible };
            if (w <= 0) b.Width = double.NaN;
            Grid.SetColumn(b, column);
            before.Children.Add(b);
            return b;
        }
        Border[] old = [Cell(0, 220), Cell(1, 180), Cell(2, 0), Cell(BaseGear, GearWidth, gearShown), Cell(BaseAction, ActionWidth), Cell(BaseInfo, InfoWidth)];

        Layout(now.Row, width);
        Layout(before, width);

        Border[] current = [now.Name, now.Status, now.Note, now.Gear, now.Action, now.Info];
        Assert.Equal(before.DesiredSize, now.Row.DesiredSize);
        for (var i = 0; i < old.Length; i++)
            Assert.True(old[i].Bounds == current[i].Bounds, $"cell {i}: 8f15e44 {old[i].Bounds}, now {current[i].Bounds} at {width} (⚙ shown={gearShown})");
    }

    // The real panel with the row's own column spec: the ⚙ and the Nexus button are both whole, apart and each the hit
    // target at its own centre at every width (one line wide, wrapped narrow), in the order Nexus → ⚙ → Install/Remove → ⓘ.
    [Theory]
    [InlineData(1600.0)]
    [InlineData(900.0)]
    [InlineData(520.0)]
    [InlineData(300.0)]
    public void GearAndNexus_AreBothPlaced_SideBySide_NeverOnTopOfEachOther(double width)
    {
        var row = FromTemplate(path => path is "LumaNexus.IsVisible" or "HasIniSettings" or "HasAction" or "HasInfo");

        AssertNexusFirst_BothWholeAndHitTargets(row, width);
    }

    internal const double NexusWidth = 140, GearWidth = 36, ActionWidth = 90, InfoWidth = 36;

    internal sealed record TemplateRow(ResponsiveRow Row, Border Name, Border Status, Border Note, Border Nexus, Border Gear, Border Action, Border Info);

    /// <summary>
    /// The component row built from the template itself: one fixed-size cell per template child, in the template's column
    /// and document order, shown when <paramref name="visible"/> says so for its <c>IsVisible</c> binding path (cells
    /// without one are always shown). <see cref="TemplateRow.Gear"/> is the first ⚙ that is shown (else the first ⚙).
    /// </summary>
    internal static TemplateRow FromTemplate(Func<string, bool> visible)
    {
        var xml = ComponentRow();
        var row = new ResponsiveRow
        {
            Columns = (string)xml.Attribute("Columns")!,
            ColumnSpacing = double.Parse((string)xml.Attribute("ColumnSpacing")!, System.Globalization.CultureInfo.InvariantCulture),
            LineSpacing = double.Parse((string)xml.Attribute("LineSpacing")!, System.Globalization.CultureInfo.InvariantCulture),
            HeadColumns = int.Parse((string)xml.Attribute("HeadColumns")!, System.Globalization.CultureInfo.InvariantCulture),
            MinStarWidth = double.Parse((string)xml.Attribute("MinStarWidth")!, System.Globalization.CultureInfo.InvariantCulture),
        };
        // Set as the template does (by the property's own type, so this file also builds against older layouts for red proofs).
        if ((string?)xml.Attribute("SkipEmptyColumns") is { } skip && typeof(ResponsiveRow).GetProperty("SkipEmptyColumns") is { } property)
            property.SetValue(row, property.PropertyType == typeof(bool) ? bool.Parse(skip) : skip);
        Border? name = null, status = null, note = null, nexus = null, action = null, info = null;
        var gears = new List<Border>();
        foreach (var e in xml.Elements())
        {
            var binding = (string?)e.Attribute("IsVisible");
            var path = binding is null ? null : binding.Replace("{Binding ", "", StringComparison.Ordinal).TrimEnd('}');
            var column = Column(e);
            var width = IsNexus(e) ? NexusWidth : IsGear(e) ? GearWidth : IsAction(e) ? ActionWidth : IsInfo(e) ? InfoWidth
                : column switch { 0 => 220, 1 => 180, _ => double.NaN };
            var cell = new Border { Width = width, Height = 30, IsVisible = path is null || visible(path) };
            Grid.SetColumn(cell, column);
            row.Children.Add(cell);
            if (IsNexus(e)) nexus = cell;
            else if (IsGear(e)) gears.Add(cell);
            else if (IsAction(e)) action = cell;
            else if (IsInfo(e)) info = cell;
            else if (column == 0) name = cell;
            else if (column == 1) status = cell;
            else if (column == 2) note = cell;
        }
        var gear = gears.FirstOrDefault(g => g.IsVisible) ?? gears[0];
        return new TemplateRow(row, name!, status!, note!, nexus!, gear, action!, info!);
    }

    /// <summary>What the pointer reaches at <paramref name="point"/>: the topmost shown child there (later children are on top).</summary>
    internal static Control? HitAt(Panel row, Point point) => row.Children.LastOrDefault(c => c.IsVisible && c.Bounds.Contains(point));

    internal static void AssertNexusFirst_BothWholeAndHitTargets(TemplateRow row, double width)
    {
        Layout(row.Row, width);

        Border[] shown = [row.Nexus, row.Gear, row.Action, row.Info];
        Assert.All(shown, b => Assert.True(b.IsVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0, b.Bounds.ToString()));
        Assert.Equal(row.Nexus.Width, row.Nexus.Bounds.Width);
        Assert.Equal(row.Gear.Width, row.Gear.Bounds.Width);
        Assert.True(row.Nexus.Bounds.Right <= width && row.Gear.Bounds.Right <= width, $"inside the row at {width}");
        for (var i = 0; i < shown.Length; i++)
            for (var j = i + 1; j < shown.Length; j++)
                Assert.False(shown[i].Bounds.Intersects(shown[j].Bounds), $"{shown[i].Bounds} overlaps {shown[j].Bounds} at {width}");
        Assert.Same(row.Nexus, HitAt(row.Row, row.Nexus.Bounds.Center));
        Assert.Same(row.Gear, HitAt(row.Row, row.Gear.Bounds.Center));
        // Reading order Nexus → ⚙ → Install/Remove → ⓘ: left to right on a line, a later line below.
        for (var i = 0; i + 1 < shown.Length; i++)
            Assert.True(shown[i].Bounds.Y < shown[i + 1].Bounds.Y || (shown[i].Bounds.Y == shown[i + 1].Bounds.Y && shown[i].Bounds.Right < shown[i + 1].Bounds.X),
                $"{shown[i].Bounds} is not before {shown[i + 1].Bounds} at {width}");
    }

    internal static void Layout(ResponsiveRow row, double width)
    {
        row.Measure(new Size(width, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
    }
}
