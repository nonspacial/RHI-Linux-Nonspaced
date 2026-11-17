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

    /// <summary>
    /// w14 final review (upstream 2.9.4 OverviewCard: the buttons are a row of their own under the description): a card's
    /// description never shares its line with the buttons, whatever the card's width, so every card places them alike.
    /// </summary>
    internal const double CardMinStarWidth = 100_000, RowMinStarWidth = 160;

    internal static void ShapeComponentRow(ResponsiveRow row, bool cards)
    {
        row.Columns = cards ? CardColumns : RowColumns;
        row.MinStarWidth = cards ? CardMinStarWidth : RowMinStarWidth;
    }

    private void ShapeComponentRow(Control container)
    {
        if (container.GetVisualDescendants().OfType<ResponsiveRow>().FirstOrDefault() is { } row)
            ShapeComponentRow(row, _cardsShown);
    }

    private sealed record InlineCog(CogHostWindow Window, Border Host, Control? Source, TaskCompletionSource Done, CogGameWatch Watch);

    /// <summary>Arranges the game page for the saved layout and tab.</summary>
    private void ApplyGameLayout()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var layout = vm.Layout;
        var tabs = layout.IsTabs;
        var chrome = layout.UsesTabsChrome; // w14/layouts-b (#305): Spotlight's banner holds the Tabs header; its sections are cards
        SetVisible("ClassicHeader", !chrome);
        SetVisible("TabsHeader", chrome);
        SetVisible("GameTabStripHost", tabs);
        if (this.FindControl<ScrollViewer>("DetailScroller") is { } scroller) scroller.Classes.Set("tabsPage", chrome);

        var collapsed = _detailLayout.CollapsedSections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DetailSectionLayoutStore.DefaultSectionOrder)
        {
            if (FindSectionContainer(key) is not Border container) continue;
            container.IsVisible = layout.IsSectionShown(key);
            container.Classes.Set("tabsCard", chrome);
            // The tab names the section in Tabs: no ≡, title, summary or arrow, and never collapsed (upstream).
            if (container.Child is Panel { Children.Count: > 0 } stack && stack.Children[0] is Grid header) header.IsVisible = !chrome;
            if (FindSectionBody(key) is { } body) body.IsVisible = chrome || !collapsed.Contains(key);
        }

        // Overview: the Components rows as cards (the same rows, so every button keeps its rules; upstream copies them).
        if (this.FindControl<ItemsControl>("ManagedComponentsSectionBody") is { } components && _cardsShown != chrome)
        {
            _cardsShown = chrome;
            if (!_cardsHooked)
            {
                _cardsHooked = true;
                // A new row's template is built after it is prepared: shape it once it is laid out.
                components.ContainerPrepared += (_, e) => Dispatcher.UIThread.Post(() => ShapeComponentRow(e.Container), DispatcherPriority.Loaded);
            }
            components.ItemsPanel = chrome ? CardsPanel : RowsPanel;
            components.Classes.Set("cards", chrome);
            foreach (var container in components.GetRealizedContainers()) ShapeComponentRow(container);
        }
        if (this.FindControl<AutoFitPanel>("ManagementActions") is { } management)
        {
            management.MinColumnWidth = chrome ? 220 : 150;
            management.Classes.Set("tiles", chrome);
            ArrangeManagement(management, chrome);
        }

        BuildTabStrip(vm);
        SetVisible("GlancePanel", layout.ShowsOverview);
        SetVisible("OverviewComponentsHeading", layout.ShowsOverview);
        if (layout.ShowsOverview) RebuildGlance(vm);
        if (_inlineCog is { } open && (!tabs || open.Host.Parent is not Control { IsEffectivelyVisible: true })) CloseInlineCog(false);
        ApplySpotlightLayout(vm); // w14/layouts-b (#305)
    }

    /// <summary>Management's actions in the look's order and colours (upstream: <see cref="ViewModels.ManagementActions"/>).</summary>
    internal static void ArrangeManagement(Panel management, bool tiles)
    {
        var byName = management.Children.OfType<Control>().Where(c => c.Name is not null).ToDictionary(c => c.Name!);
        var order = ViewModels.ManagementActions.Order(tiles).Where(byName.ContainsKey).Select(n => byName[n]).ToList();
        if (!order.SequenceEqual(management.Children.Where(byName.ContainsValue)))
        {
            foreach (var action in order) management.Children.Remove(action);
            management.Children.AddRange(order);
        }
        foreach (var action in order) action.Classes.Set("remove", ViewModels.ManagementActions.IsDanger(action.Name!, tiles));
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
        if (TryShowCogInSpotlightPanel(dialog, out shown)) return true; // w14/layouts-b (#305): a ⚙ in Spotlight's side panel
        if (dialog is not CogHostWindow cog || DataContext is not MainWindowViewModel { Layout.CogsOpenInline: true } vm) return false;
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
        // M1: the ⚙ acts on the game it opened for; another game closes it (as Spotlight's panel).
        _inlineCog = new InlineCog(cog, host, source, done, new CogGameWatch(vm, () => CloseInlineCog(false)));
        close.Click += (_, _) => CloseInlineCog(false);
        cog.InlineClose = result => CloseInlineCog(result);
        PlaceInlineCog(host, source, panel);
        cog.RaiseOpenedInline();
        Dispatcher.UIThread.Post(() => host.BringIntoView());
        shown = done.Task;
        return true;
    }

    /// <summary>
    /// w14/layouts-c (upstream 2.9.0 <c>ShowCogInPlace</c>: "right under the row (or the Overview cards) whose ⚙ was clicked"):
    /// under the line of cards that holds the clicked card (the cards flow in an <see cref="AutoFitPanel"/>, so the ⚙ spans the
    /// width under that line and the lines after it move down); else right after the row it was clicked in, when that row is
    /// one of a vertical list's own children at least 400 px wide (upstream's rule); else at the end of the section.
    /// </summary>
    internal static void PlaceInlineCog(Border host, Control? source, Panel section)
    {
        if (source?.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.GetVisualParent() is AutoFitPanel) is { } card
            && card.GetVisualParent() is AutoFitPanel cards)
        {
            cards.ShowInset(host, card);
            return;
        }
        var row = source?.GetVisualAncestors().OfType<Control>().FirstOrDefault(c =>
            c.GetVisualParent() is StackPanel { Orientation: Orientation.Vertical } list && list.Children.Contains(c) && !IsItemsHost(list)
            && c.Bounds.Width >= 400 && section.IsVisualAncestorOf(c));
        if (row?.GetVisualParent() is StackPanel rows)
        {
            rows.Children.Insert(rows.Children.IndexOf(row) + 1, host);
            return;
        }
        section.Children.Add(host);
    }

    // An ItemsControl's panel belongs to it: nothing else is put among its children.
    private static bool IsItemsHost(Panel panel) => panel.GetVisualParent() is Avalonia.Controls.Presenters.ItemsPresenter;

    private void CloseInlineCog(bool result)
    {
        if (_inlineCog is not { } open) return;
        _inlineCog = null;
        open.Watch.Dispose();
        if (open.Host.Parent is AutoFitPanel cards) cards.HideInset();
        else if (open.Host.Parent is Panel panel) panel.Children.Remove(open.Host);
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

    /// <summary>
    /// The Tabs header's Launch menu (upstream 2.9.0: Executable, Launch arguments..., Create desktop shortcut): the executable
    /// (opens Edit game, where Linux chooses it and the launch command), Launch arguments... (Game Overrides' box), the launch
    /// profile (Linux), a shortcut.
    /// </summary>
    private void TabsLaunchMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || DataContext is not MainWindowViewModel vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterClasses = { "navy" } };
        var edit = new MenuItem { Header = vm.LaunchMenuExecutableText, IsEnabled = vm.CanEditSelectedGame, Name = "LaunchMenuExecutable" };
        ToolTip.SetTip(edit, T("LcMenu.ExecutableTip"));
        edit.Click += (_, args) => EditGame_Click(anchor, args);
        menu.Items.Add(edit);
        var arguments = new MenuItem { Header = T("LcMenu.Arguments"), IsEnabled = vm.HasSelectedGame, Name = "LaunchMenuArguments" };
        arguments.Click += (_, _) => ShowLaunchArguments(vm);
        menu.Items.Add(arguments);
        var profile = new MenuItem { Header = T("Ui.CopyLaunchProfile"), IsEnabled = vm.Runtime.CanCopyHdrLaunchOptions };
        profile.Click += (_, args) => CopyHdrLaunchOptions_Click(anchor, args);
        menu.Items.Add(profile);
        foreach (var item in vm.LaunchMenuItems()) menu.Items.Add(CardMenuView(item));
        menu.ShowAt(anchor);
    }

    /// <summary>Launch ▾ › Launch arguments...: Game Overrides' tab (upstream), with the box focused.</summary>
    private void ShowLaunchArguments(MainWindowViewModel vm)
    {
        if (vm.Layout.ShowSection(GamePageSections.GameOverrides) is { } refused)
        {
            vm.StatusText = refused;
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("LaunchArgumentsBox") is not { } box) return;
            box.BringIntoView();
            box.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The Launch arguments box lost focus: its text is saved (upstream saves on focus lost).</summary>
    private void LaunchArgumentsBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.LaunchArguments.Commit();
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
