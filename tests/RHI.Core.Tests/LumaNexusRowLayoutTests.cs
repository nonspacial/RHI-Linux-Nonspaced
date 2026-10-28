using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// #245 correction (review F1, coordinator layout decision): the component row's Nexus button has a column of its own, in
/// the order Install/Remove → Nexus → ⚙ → ⓘ, so a Luma Engine.ini ⚙ (Unreal games, #189) and "Update/Open on Nexus" are
/// both shown, side by side and never on top of each other. A column with nothing shown takes no spacing, so rows without
/// the optional buttons keep one gap between the ones they show.
/// </summary>
public sealed class LumaNexusRowLayoutTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static XElement ComponentRow()
    {
        var path = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "Views", "MainWindow.axaml");
        var doc = XDocument.Load(path);
        // The component row: the ResponsiveRow that holds the Nexus button.
        return doc.Descendants().Single(e => e.Name.LocalName == "ResponsiveRow"
                                             && e.Elements().Any(c => ((string?)c.Attribute("Content"))?.Contains("LumaNexus.Label", StringComparison.Ordinal) == true));
    }

    private static int Column(XElement e) => int.Parse((string?)e.Attribute("Grid.Column") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void NexusButton_HasItsOwnColumn_InTheOrderInstallRemove_Nexus_Gear_Info()
    {
        var row = ComponentRow();
        var children = row.Elements().ToArray();
        bool HasContent(XElement e, string text) => ((string?)e.Attribute("Content"))?.Contains(text, StringComparison.Ordinal) == true;
        var nexus = children.Single(e => HasContent(e, "LumaNexus.Label"));
        var action = children.Single(e => HasContent(e, "{Binding Action}"));
        var gears = children.Where(e => (string?)e.Attribute("Content") == "⚙").ToArray();
        var info = children.Single(e => (string?)e.Attribute("Content") == "ⓘ");

        Assert.NotEmpty(gears);
        Assert.Single(children, e => Column(e) == Column(nexus)); // nothing else is drawn in the Nexus button's place
        Assert.True(Column(action) < Column(nexus), "Install/Remove comes before the Nexus button");
        Assert.All(gears, gear => Assert.True(Column(nexus) < Column(gear), "the Nexus button comes before every ⚙"));
        Assert.All(gears, gear => Assert.True(Column(gear) < Column(info), "every ⚙ comes before ⓘ"));
        // Same look as the row's other buttons; keyboard order = what the eye sees.
        Assert.Equal("rhi", (string?)nexus.Attribute("Classes"));
        Assert.Equal("2000", (string?)nexus.Attribute("ToolTip.ShowDelay"));
        Assert.Equal((string?)info.Attribute("Padding"), (string?)nexus.Attribute("Padding"));
        var order = children.Where(e => e == action || e == nexus || gears.Contains(e) || e == info).Select(Column).ToArray();
        Assert.Equal(order.Order(), order);
        var columns = ((string?)row.Attribute("Columns"))!.Split(',');
        Assert.Equal("Auto", columns[Column(nexus)]);
        Assert.Equal("True", (string?)row.Attribute("SkipEmptyColumns"));
    }

    // The real panel with the row's own column spec: the ⚙ and the Nexus button are both whole and apart at every width
    // (one line wide, wrapped narrow), and hidden optional buttons leave no extra gap.
    [Theory]
    [InlineData(1600.0)]
    [InlineData(900.0)]
    [InlineData(520.0)]
    [InlineData(300.0)]
    public void GearAndNexus_AreBothPlaced_SideBySide_NeverOnTopOfEachOther(double width)
    {
        var (row, action, nexus, gear, info) = Row(nexusShown: true, gearShown: true);

        Layout(row, width);

        Rect[] shown = [action.Bounds, nexus.Bounds, gear.Bounds, info.Bounds];
        Assert.All(shown, b => Assert.True(b.Width > 0 && b.Height > 0, b.ToString()));
        Assert.Equal(nexus.Width, nexus.Bounds.Width);
        Assert.Equal(gear.Width, gear.Bounds.Width);
        for (var i = 0; i < shown.Length; i++)
            for (var j = i + 1; j < shown.Length; j++)
                Assert.False(shown[i].Intersects(shown[j]), $"{shown[i]} overlaps {shown[j]} at {width}");
        // Reading order Install/Remove → Nexus → ⚙ → ⓘ: left to right on a line, a later line below.
        for (var i = 0; i + 1 < shown.Length; i++)
            Assert.True(shown[i].Y < shown[i + 1].Y || (shown[i].Y == shown[i + 1].Y && shown[i].Right < shown[i + 1].X),
                $"{shown[i]} is not before {shown[i + 1]} at {width}");
    }

    [Fact]
    public void HiddenOptionalButtons_TakeNoSpacing()
    {
        var (row, action, _, _, info) = Row(nexusShown: false, gearShown: false);

        Layout(row, 1600);

        Assert.Equal(12, info.Bounds.X - action.Bounds.Right, 3);
    }

    private static (ResponsiveRow Row, Border Action, Border Nexus, Border Gear, Border Info) Row(bool nexusShown, bool gearShown)
    {
        var row = new ResponsiveRow { Columns = "220,180,*,Auto,Auto,Auto,Auto", ColumnSpacing = 12, LineSpacing = 8, HeadColumns = 2, MinStarWidth = 160 };
        typeof(ResponsiveRow).GetProperty("SkipEmptyColumns")?.SetValue(row, true);
        Border Cell(int column, double w, bool visible = true)
        {
            var b = new Border { Width = w, Height = 30, IsVisible = visible };
            Grid.SetColumn(b, column);
            row.Children.Add(b);
            return b;
        }
        Cell(0, 220);
        Cell(1, 180);
        Cell(2, 200);
        // The template's columns: Install/Remove, Nexus, ⚙, ⓘ (base layout: ⚙ and Nexus shared column 3).
        var xml = ComponentRow().Elements().ToArray();
        int Col(Func<XElement, bool> pick) => Column(xml.First(pick));
        var action = Cell(Col(e => ((string?)e.Attribute("Content"))?.Contains("{Binding Action}", StringComparison.Ordinal) == true), 90);
        var nexus = Cell(Col(e => ((string?)e.Attribute("Content"))?.Contains("LumaNexus.Label", StringComparison.Ordinal) == true), 140, nexusShown);
        var gear = Cell(Col(e => (string?)e.Attribute("Content") == "⚙"), 36, gearShown);
        var info = Cell(Col(e => (string?)e.Attribute("Content") == "ⓘ"), 36);
        return (row, action, nexus, gear, info);
    }

    private static void Layout(ResponsiveRow row, double width)
    {
        row.Measure(new Size(width, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
    }
}
