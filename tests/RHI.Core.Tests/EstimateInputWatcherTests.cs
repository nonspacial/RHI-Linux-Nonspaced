using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RHI.Desktop.Views;
using Xunit;

namespace RHI.Core.Tests;

public sealed class EstimateInputWatcherTests
{
    private static (StackPanel Root, EstimateInputWatcher Watcher, Func<int> Changes) Watched(params Control[] children)
    {
        var root = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var c in children) root.Children.Add(c);
        var changes = 0;
        var watcher = new EstimateInputWatcher(root, (_, _) => true, () => changes++);
        watcher.Watch();
        return (root, watcher, () => changes);
    }

    [Fact]
    public void InputPropertyOnDescendant_ReportsChange_OtherPropertiesDoNot()
    {
        var button = new Button { Content = "Refresh" };
        var (_, _, changes) = Watched(button);

        button.Opacity = 0.5;
        Assert.Equal(0, changes());
        button.Content = "Refresh all";
        Assert.Equal(1, changes());
        button.Padding = new Thickness(3);
        Assert.Equal(2, changes());
    }

    [Fact]
    public void ChildAddedAfterWatch_IsReportedAndWatched()
    {
        var (root, watcher, changes) = Watched(new Button { Content = "A" });
        var late = new Button { Content = "Late" };

        root.Children.Add(late);
        Assert.Equal(1, changes());
        Assert.Equal(2, watcher.WatchedCount);

        late.Content = "Late, renamed";
        Assert.Equal(2, changes());
    }

    [Fact]
    public void GrandchildAddedToNestedPanel_IsReportedAndWatched()
    {
        var actions = new WrapPanel();
        var (_, _, changes) = Watched(actions);
        var late = new Button { Content = "New action" };

        actions.Children.Add(late);
        Assert.Equal(1, changes());
        late.FontSize = 20;
        Assert.Equal(2, changes());
    }

    [Fact]
    public void ContentSwappedForControl_WatchesTheNewControl()
    {
        var button = new Button { Content = "Text" };
        var (_, _, changes) = Watched(button);
        var inner = new TextBlock { Text = "Inner" };

        button.Content = inner;
        var afterSwap = changes();
        Assert.True(afterSwap >= 1);
        inner.Text = "Inner, longer";
        Assert.Equal(afterSwap + 1, changes());
    }

    [Fact]
    public void RemovedChild_IsReportedAndNoLongerWatched()
    {
        var gone = new Button { Content = "Gone" };
        var (root, watcher, changes) = Watched(gone, new Button { Content = "Stays" });

        root.Children.Remove(gone);
        Assert.Equal(1, changes());
        Assert.Equal(1, watcher.WatchedCount);
        gone.Content = "Still gone";
        Assert.Equal(1, changes());
    }

    [Fact]
    public void Unwatch_StopsAllReports()
    {
        var button = new Button { Content = "A" };
        var (root, watcher, changes) = Watched(button);

        watcher.Unwatch();
        button.Content = "B";
        root.Children.Add(new Button());
        Assert.Equal(0, changes());
        Assert.Equal(0, watcher.WatchedCount);
    }

    [Fact]
    public void Filter_SuppressesThePanelsOwnWrites()
    {
        var button = new Button { Content = "A" };
        var root = new StackPanel();
        root.Children.Add(button);
        var applying = false;
        var changes = 0;
        var watcher = new EstimateInputWatcher(root, (_, _) => !applying, () => changes++);
        watcher.Watch();

        applying = true;
        button.FontSize = 11;
        applying = false;
        Assert.Equal(0, changes);
        button.FontSize = 12;
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ExtraInputs_AreReportedLikeEstimatorInputs()
    {
        var item = new Button { Content = "A" };
        var root = new StackPanel();
        root.Children.Add(item);
        var changes = 0;
        var watcher = new EstimateInputWatcher(root, (_, _) => true, () => changes++,
            [ToolbarPanel.SlotProperty, ToolbarPanel.IsSeparatorProperty, ToolbarPanel.CollapsibleProperty, DetailHeaderPanel.RoleProperty]);
        watcher.Watch();

        ToolbarPanel.SetSlot(item, ToolbarSlot.Trailing);
        Assert.Equal(1, changes);
        ToolbarPanel.SetIsSeparator(item, true);
        Assert.Equal(2, changes);
        ToolbarPanel.SetCollapsible(item, true);
        Assert.Equal(3, changes);
        DetailHeaderPanel.SetRole(item, DetailHeaderRole.Action);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void PanelMarkers_AreNotReportedWithoutBeingListed()
    {
        var item = new Button { Content = "A" };
        var (_, _, changes) = Watched(item);

        ToolbarPanel.SetSlot(item, ToolbarSlot.Trailing);
        DetailHeaderPanel.SetRole(item, DetailHeaderRole.Action);
        Assert.Equal(0, changes());
    }

    [Fact]
    public void Inputs_CoverWhatTheEstimatorReads()
    {
        foreach (var p in new AvaloniaProperty[]
                 {
                     Visual.IsVisibleProperty, Layoutable.MarginProperty, Layoutable.WidthProperty, Layoutable.MinWidthProperty,
                     Layoutable.MaxWidthProperty, TextBlock.TextProperty, TextBlock.FontSizeProperty, TextBlock.PaddingProperty,
                     ContentControl.ContentProperty, Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty,
                     Avalonia.Controls.Primitives.TemplatedControl.BorderThicknessProperty, Border.ChildProperty,
                     Border.PaddingProperty, Border.BorderThicknessProperty, StackPanel.SpacingProperty,
                     StackPanel.OrientationProperty, WrapPanel.ItemSpacingProperty
                 })
            Assert.Contains(p, WidthEstimator.Inputs);
    }

    [Fact]
    public void ToolbarAndHeader_HaveNoPrivateInputListOfTheirOwn()
    {
        foreach (var type in new[] { typeof(ToolbarPanel), typeof(DetailHeaderPanel) })
            Assert.DoesNotContain(type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
                f => typeof(IEnumerable<AvaloniaProperty>).IsAssignableFrom(f.FieldType));
    }
}
