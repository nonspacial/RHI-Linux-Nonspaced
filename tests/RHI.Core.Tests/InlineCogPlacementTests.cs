using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-c (upstream 2.9.0 Tabs: "A component's ⚙ settings open right under it on the page"): the ⚙ opens across the
/// width right under the line of cards that holds the clicked card, and the lines after it move down; a ⚙ clicked in a row of
/// a vertical list opens right after that row (upstream's rule: a row at least 400 px wide); closing puts everything back.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class InlineCogPlacementTests
{
    // ── The cards panel's plan (pure) ──────────────────────────────────────────

    [Fact]
    public void Plan_PutsTheInsetUnderTheLineOfItsCard_AndMovesTheLinesAfterItDown()
    {
        // Six cards three to a line: the inset follows card 4's line (cards 3–5), the second of two lines.
        var heights = new double[] { 100, 120, 90, 80, 80, 80 };
        var (cards, inset, height) = AutoFitPanel.Plan(heights, columns: 3, columnWidth: 290, spacing: 12, width: 894, insetAfter: 1, insetHeight: 300);

        Assert.Equal(new Rect(0, 132, 894, 300), inset); // under line 1 (120 tall) and one spacing
        Assert.Equal(new Rect(0, 444, 290, 80), cards[3]); // line 2 starts below the inset
        Assert.Equal(new Rect(302, 0, 290, 120), cards[1]); // line 1 is where it was
        Assert.Equal(524, height);
    }

    [Fact]
    public void Plan_WithoutAnInset_IsTheOldLayout()
    {
        var (cards, inset, height) = AutoFitPanel.Plan([50, 70, 60], columns: 2, columnWidth: 100, spacing: 10, width: 210);
        Assert.Null(inset);
        Assert.Equal([new Rect(0, 0, 100, 70), new Rect(110, 0, 100, 70), new Rect(0, 80, 100, 60)], cards);
        Assert.Equal(140, height);
    }

    [Fact]
    public void Plan_ACardThatIsGone_PutsTheInsetUnderTheLastLine()
    {
        var (_, inset, height) = AutoFitPanel.Plan([50, 50, 50], columns: 2, columnWidth: 100, spacing: 10, width: 210, insetAfter: int.MaxValue, insetHeight: 40);
        Assert.Equal(new Rect(0, 120, 210, 40), inset);
        Assert.Equal(160, height);
    }

    // ── The panel itself ───────────────────────────────────────────────────────

    [Fact]
    public void Panel_ShowsTheInsetUnderTheClickedCardsLine_AndHidesItAgain()
    {
        var panel = new AutoFitPanel { MinColumnWidth = 260, Spacing = 12 };
        var cards = Enumerable.Range(0, 5).Select(_ => new Border { Height = 100 }).ToList();
        foreach (var card in cards) panel.Children.Add(card);
        Layout(panel, 900); // three to a line
        Assert.Equal(112, cards[3].Bounds.Y);

        var inset = new Border { Height = 250 };
        panel.ShowInset(inset, cards[1]);
        Layout(panel, 900);

        Assert.Equal(new Rect(0, 112, 900, 250), inset.Bounds);
        Assert.Equal(374, cards[3].Bounds.Y); // the next line moved down
        Assert.Equal(0, cards[1].Bounds.Y);
        Assert.Same(panel, inset.Parent);
        Assert.DoesNotContain(inset, panel.Children); // never one of the ItemsControl's children

        panel.HideInset();
        Layout(panel, 900);
        Assert.Null(inset.Parent);
        Assert.Equal(112, cards[3].Bounds.Y);
    }

    // ── Where the ⚙ goes ───────────────────────────────────────────────────────

    [Fact]
    public void Placement_ACardsGear_OpensUnderItsLine()
    {
        var section = new StackPanel();
        var cards = new AutoFitPanel { MinColumnWidth = 260 };
        var gear = new Button();
        var card = new Border { Height = 100, Child = gear };
        cards.Children.Add(new Border { Height = 100 });
        cards.Children.Add(card);
        section.Children.Add(cards);
        Layout(section, 900);
        var host = new Border { Height = 200 };

        MainWindow.PlaceInlineCog(host, gear, section);

        Assert.Same(host, cards.Inset);
        Assert.DoesNotContain(host, section.Children);
    }

    [Fact]
    public void Placement_ARowsGear_OpensRightAfterThatRow()
    {
        var section = new StackPanel();
        var rows = new StackPanel { Orientation = Orientation.Vertical };
        var gear = new Button();
        var first = new Border { Height = 40, Child = gear };
        var second = new Border { Height = 40 };
        rows.Children.Add(first);
        rows.Children.Add(second);
        section.Children.Add(rows);
        section.Children.Add(new Border { Height = 40 });
        Layout(section, 900);
        var host = new Border();

        MainWindow.PlaceInlineCog(host, gear, section);

        Assert.Equal([first, host, second], rows.Children);
    }

    [Fact]
    public void Placement_NoRowOrCard_OpensAtTheEndOfTheSection()
    {
        var section = new StackPanel();
        var gear = new Button { Width = 30 };
        var narrow = new StackPanel { Width = 200 };
        narrow.Children.Add(gear);
        section.Children.Add(narrow);
        Layout(section, 900);
        var host = new Border();

        MainWindow.PlaceInlineCog(host, gear, section);

        Assert.Same(host, section.Children[^1]);
    }

    // ── w14 final review M1: a ⚙ on the page belongs to its game ─────────────────

    [Fact]
    public async Task OpenCog_ClosesWhenTheSelectionMovesToAnotherGame_NotOnOtherChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-cogwatch-{Guid.NewGuid():N}");
        try
        {
            using var fx = await ViewModelFixture.StartAsync(
            [
                new RHI.Core.Models.DetectedGame { Name = "Game A", InstallPath = Directory.CreateDirectory(Path.Combine(root, "A")).FullName, Source = "Steam" },
                new RHI.Core.Models.DetectedGame { Name = "Game B", InstallPath = Directory.CreateDirectory(Path.Combine(root, "B")).FullName, Source = "Steam" },
            ]);
            var vm = fx.Vm;
            vm.SelectedGame = vm.AllLibraryGames.Single(g => g.Name == "Game A");
            var closed = 0;
            using var watch = new CogGameWatch(vm, () => closed++);

            vm.StatusText = "something else";
            vm.SelectedGame = vm.SelectedGame; // the same game
            Assert.Equal(0, closed);

            vm.SelectedGame = vm.AllLibraryGames.Single(g => g.Name == "Game B");
            Assert.Equal(1, closed);
            vm.SelectedGame = vm.AllLibraryGames.Single(g => g.Name == "Game A");
            Assert.Equal(1, closed); // closed once, then it stops listening
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void Layout(Control control, double width)
    {
        control.Measure(new Size(width, double.PositiveInfinity));
        control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
    }
}
