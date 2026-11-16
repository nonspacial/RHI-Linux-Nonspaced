using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-a (#304, upstream 2.9.0 DetailLayoutHost): where the game page's sections go. Classic+ stacks every section,
/// collapsible and reorderable; Tabs shows a header, a tab strip in upstream's fixed order and one tab at a time (Overview:
/// the components as cards and At a glance tiles). Every layout shows the same section containers: a section is built once
/// and switching layout or tab only changes what shows (never rebuilds). A ⚙ opens on the page under its component in Tabs,
/// in its own window in Classic+ (as upstream). Spotlight (#305, layouts-b) arranges the same containers.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly FuncTemplate<Panel?> RowsPanel = new(() => new StackPanel());
    private static readonly FuncTemplate<Panel?> CardsPanel = new(() => new AutoFitPanel { MinColumnWidth = 260, Spacing = 12 });

    private Control? _lastPressed;
    private InlineCog? _inlineCog;
    private bool _cardsShown;
    private bool _cardsHooked;

    /// <summary>A component row's columns: the row's fixed ones in Classic+; as a card (Tabs), the name and status on the
    /// first line, then the description, then the buttons (the same controls, so every rule stays the row's).</summary>
    private const string RowColumns = "220,180,*,Auto,Auto,Auto,Auto", CardColumns = "*,Auto,*,Auto,Auto,Auto,Auto";

    private void ShapeComponentRow(Control container)
    {
        if (container.GetVisualDescendants().OfType<ResponsiveRow>().FirstOrDefault() is { } row)
            row.Columns = _cardsShown ? CardColumns : RowColumns;
    }

    private sealed record InlineCog(CogHostWindow Window, Border Host, Control? Source, TaskCompletionSource Done);

    /// <summary>Arranges the game page for the saved layout and tab.</summary>
    private void ApplyGameLayout()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var layout = vm.Layout;
        var tabs = layout.IsTabs;
        SetVisible("ClassicHeader", !tabs);
        SetVisible("TabsHeader", tabs);
        SetVisible("GameTabStripHost", tabs);
        if (this.FindControl<ScrollViewer>("DetailScroller") is { } scroller) scroller.Classes.Set("tabsPage", tabs);

        var collapsed = _detailLayout.CollapsedSections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DetailSectionLayoutStore.DefaultSectionOrder)
        {
            if (FindSectionContainer(key) is not Border container) continue;
            container.IsVisible = layout.IsSectionShown(key);
            container.Classes.Set("tabsCard", tabs);
            // The tab names the section in Tabs: no ≡, title, summary or arrow, and never collapsed (upstream).
            if (container.Child is Panel { Children.Count: > 0 } stack && stack.Children[0] is Grid header) header.IsVisible = !tabs;
            if (FindSectionBody(key) is { } body) body.IsVisible = tabs || !collapsed.Contains(key);
        }

        // Overview: the Components rows as cards (the same rows, so every button keeps its rules; upstream copies them).
        if (this.FindControl<ItemsControl>("ManagedComponentsSectionBody") is { } components && _cardsShown != tabs)
        {
            _cardsShown = tabs;
            if (!_cardsHooked)
            {
                _cardsHooked = true;
                // A new row's template is built after it is prepared: shape it once it is laid out.
                components.ContainerPrepared += (_, e) => Dispatcher.UIThread.Post(() => ShapeComponentRow(e.Container), DispatcherPriority.Loaded);
            }
            components.ItemsPanel = tabs ? CardsPanel : RowsPanel;
            components.Classes.Set("cards", tabs);
            foreach (var container in components.GetRealizedContainers()) ShapeComponentRow(container);
        }
        if (this.FindControl<AutoFitPanel>("ManagementActions") is { } management)
        {
            management.MinColumnWidth = tabs ? 220 : 150;
            management.Classes.Set("tiles", tabs);
        }

        BuildTabStrip(vm);
        SetVisible("GlancePanel", layout.ShowsOverview);
        if (layout.ShowsOverview) RebuildGlance(vm);
        if (_inlineCog is { } open && (!tabs || open.Host.Parent is not Control { IsEffectivelyVisible: true })) CloseInlineCog(false);
    }

    private void SetVisible(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control) control.IsVisible = visible;
    }

    private void BuildTabStrip(MainWindowViewModel vm)
    {
        if (this.FindControl<WrapPanel>("GameTabStrip") is not { } strip) return;
        var tabs = vm.Layout.Tabs;
        if (strip.Children.Count != tabs.Count)
        {
            strip.Children.Clear();
            foreach (var tab in tabs)
            {
                var key = tab.Key;
                var button = new Button { Classes = { "tabButton" }, Name = "Tab" + key };
                button.Click += (_, _) =>
                {
                    if (vm.Layout.SelectTab(key) is { } refused) vm.StatusText = refused;
                };
                strip.Children.Add(button);
            }
        }
        for (var i = 0; i < tabs.Count; i++)
        {
            var button = (Button)strip.Children[i];
            button.Content = tabs[i].Title;
            button.Classes.Set("on", tabs[i].IsSelected);
        }
    }

    /// <summary>Overview's At a glance: a tile per other section with its summary; a click opens that tab.</summary>
    private void RebuildGlance(MainWindowViewModel vm)
    {
        if (this.FindControl<AutoFitPanel>("GlanceTiles") is not { } tiles) return;
        if (tiles.Children.Count != GamePageSections.GlanceSections.Count)
        {
            tiles.Children.Clear();
            foreach (var section in GamePageSections.GlanceSections)
            {
                var key = section;
                var title = new TextBlock { Classes = { "glanceTitle" } };
                var summary = new TextBlock { Classes = { "glanceSummary" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxLines = 3 };
                var tile = new Button
                {
                    Name = "Glance" + key, Classes = { "glanceTile" }, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                    Content = new StackPanel { Spacing = 4, Children = { title, summary } },
                };
                tile.Click += (_, _) =>
                {
                    if (vm.Layout.ShowSection(key) is { } refused) vm.StatusText = refused;
                };
                tiles.Children.Add(tile);
            }
        }
        for (var i = 0; i < GamePageSections.GlanceSections.Count; i++)
        {
            var key = GamePageSections.GlanceSections[i];
            var tile = (Button)tiles.Children[i];
            var parts = ((StackPanel)tile.Content!).Children;
            ((TextBlock)parts[0]).Text = GamePageSections.Title(key);
            ((TextBlock)parts[1]).Text = vm.GlanceText(key);
            ToolTip.SetTip(tile, UiLocalizationService.Format("Sec.OpenTab", GamePageSections.Title(key)));
        }
    }

    // ---- the ⚙ on the page (Tabs) -------------------------------------------------------------------------------------

    /// <summary>Remembers the control a click started on, so a ⚙ that opens can go under it.</summary>
    private void InitializeInlineCogs()
    {
        AddHandler(PointerPressedEvent, (_, e) => _lastPressed = e.Source as Control, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) _lastPressed = FocusManager?.GetFocusedElement() as Control;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Tabs (upstream 2.9.0): a component's ⚙ opens on the page under it instead of in a window; clicking the same ⚙ again,
    /// or Close, closes it. False when the ⚙ belongs in a window (Classic+, or not a component ⚙).
    /// </summary>
    private bool TryShowCogInline(Window dialog, out Task shown)
    {
        shown = Task.CompletedTask;
        if (dialog is not CogHostWindow cog || DataContext is not MainWindowViewModel { Layout.CogsOpenInline: true }) return false;
        var source = _lastPressed?.FindAncestorOfType<Button>(includeSelf: true);
        if (_inlineCog is { } open)
        {
            var same = source is not null && ReferenceEquals(open.Source, source);
            CloseInlineCog(false);
            if (same)
            {
                cog.RaiseClosedInline(); // the second click on the same ⚙ closes it (upstream)
                return true;
            }
        }
        var section = source?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Tag is string key && FindSectionContainer(key) == b)
                      ?? FindSectionContainer(GamePageSections.Components) as Border;
        if (section?.Child is not Panel panel || cog.Content is not Control frame) return false;
        cog.Content = null;
        var close = new Button { Classes = { "rhi", "inlineCogClose" }, Content = "✕", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 12, 0) };
        ToolTip.SetTip(close, T("Cog.CloseInline"));
        var host = new Border { Name = "InlineCog", Classes = { "inlineCog" }, Child = new Grid { Children = { frame, close } } };
        var done = new TaskCompletionSource();
        _inlineCog = new InlineCog(cog, host, source, done);
        close.Click += (_, _) => CloseInlineCog(false);
        cog.InlineClose = result => CloseInlineCog(result);
        panel.Children.Add(host);
        cog.RaiseOpenedInline();
        Dispatcher.UIThread.Post(() => host.BringIntoView());
        shown = done.Task;
        return true;
    }

    private void CloseInlineCog(bool result)
    {
        if (_inlineCog is not { } open) return;
        _inlineCog = null;
        if (open.Host.Parent is Panel panel) panel.Children.Remove(open.Host);
        open.Window.InlineClose = null;
        try { open.Window.RaiseClosedInline(); }
        finally { open.Done.TrySetResult(); }
    }

    // ---- Management and the Tabs header -------------------------------------------------------------------------------

    /// <summary>Management › Reset install folder: back to the folder RHI detected; RHI offers to move the installs (#293).</summary>
    private void ResetInstallFolder_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel { CanResetInstallFolder: true } vm) return;
        var (_, message) = await vm.ManualGames.ResetSelectedGameEditAsync();
        vm.StatusText = message;
    });

    /// <summary>The Tabs header's Launch menu: executable and launch command, the launch profile, a shortcut.</summary>
    private void TabsLaunchMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || DataContext is not MainWindowViewModel vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterClasses = { "navy" } };
        var edit = new MenuItem { Header = T("Tabs.EditLaunch"), IsEnabled = vm.CanEditSelectedGame };
        edit.Click += (_, args) => EditGame_Click(anchor, args);
        menu.Items.Add(edit);
        var profile = new MenuItem { Header = T("Ui.CopyLaunchProfile"), IsEnabled = vm.Runtime.CanCopyHdrLaunchOptions };
        profile.Click += (_, args) => CopyHdrLaunchOptions_Click(anchor, args);
        menu.Items.Add(profile);
        foreach (var item in vm.LaunchMenuItems()) menu.Items.Add(CardMenuView(item));
        menu.ShowAt(anchor);
    }

    private static MenuItem CardMenuView(CardMenuItem item)
    {
        var view = new MenuItem { Header = item.Label };
        if (item.Tip is { } tip) ToolTip.SetTip(view, tip);
        if (item.Children is { Count: > 0 } children)
            foreach (var child in children) view.Items.Add(CardMenuView(child));
        else view.Command = item.Command;
        return view;
    }
}
