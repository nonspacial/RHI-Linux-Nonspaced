using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>Set by App from the catalogues the installers already use, the Source database and the Ultra+ list.</summary>
    public AvailableHdrModsCatalog? HdrModsCatalog { get; set; }

    internal const string AvailableModsColumns = "*,64,64,64";
    // Upstream 2.8.5: headers and ✗ marks use the secondary text colour (#A0AABB, ~8:1 on the #121518 dialog),
    // not faded or muted text (~3.4:1), so they stay readable.
    private const string HdrModsSecondaryTextKey = "RhiSecondaryTextBrush";

    // Upstream 2.8.8 "Available Mods" (2.7.6 "Available HDR Mods"): searchable catalogue-wide list with RenoDX, Luma and
    // Ultra+ columns; every ✓/🔨 opens that mod's page (#256).
    private void AvailableMods_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var title = T("Win.RHILinuxAvailableMods");
        if (HdrModsCatalog is not { } catalog)
        {
            await ShowSimpleMessageAsync(title, T("Win.ModCataloguesNotAvailableSession"));
            return;
        }

        var count = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 10), Text = T("Win.LoadingCatalogues"), TextWrapping = TextWrapping.Wrap };
        var search = new TextBox { PlaceholderText = T("Win.SearchGamesOrMods"), Margin = new Thickness(0, 0, 0, 10), IsEnabled = false };
        var header = BuildAvailableModsHeader();
        var list = new StackPanel { Spacing = 2 };
        var empty = new TextBlock { Text = T("Win.NoModsFound"), Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        var close = new Button { Content = T("Win.Close"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 7), Margin = new Thickness(0, 10, 0, 0) };

        var top = new StackPanel();
        top.Children.Add(count);
        top.Children.Add(search);
        top.Children.Add(header);
        top.Children.Add(new Border { Height = 1, Classes = { "divider" }, Margin = new Thickness(0, 0, 0, 6) });
        var body = new StackPanel();
        body.Children.Add(list);
        body.Children.Add(empty);
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18) };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        panel.Children.Add(top);
        panel.Children.Add(scroll); Grid.SetRow(scroll, 1);
        panel.Children.Add(close); Grid.SetRow(close, 2);

        // Fixed width, as upstream: the dialog no longer shifts while scrolling.
        var dialog = new Window
        {
            Title = title,
            Width = 640,
            Height = 640,
            MinHeight = 360,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        dialog.MinWidth = dialog.MaxWidth = dialog.Width;
        close.Click += (_, _) => dialog.Close();

        var rows = new List<(HdrModEntry Entry, Control Row)>();
        void ApplyFilter()
        {
            var visible = 0;
            foreach (var (entry, row) in rows)
            {
                row.IsVisible = AvailableHdrModsCatalog.Matches(entry, search.Text);
                if (row.IsVisible) visible++;
            }
            empty.IsVisible = visible == 0;
        }
        search.TextChanged += (_, _) => ApplyFilter();

        dialog.Opened += (_, _) => RunUiSafe(async () =>
        {
            AvailableHdrModsSnapshot snapshot;
            try
            {
                snapshot = await catalog.LoadAsync();
            }
            catch (Exception ex)
            {
                count.Text = ErrorLineText.Of(ex);
                return;
            }

            foreach (var entry in snapshot.Entries)
            {
                var row = BuildAvailableModRow(entry, OpenProjectLink);
                rows.Add((entry, row));
                list.Children.Add(row);
            }
            count.Text = AvailableModsCountLine(snapshot);
            search.IsEnabled = true;
            ApplyFilter();
            search.Focus();
        });

        await ShowFittedDialogAsync(dialog);
    });

    /// <summary>
    /// #256 (upstream 2.8.8): how many RenoDX, Luma and Ultra+ mods the list holds, then which source could not be loaded
    /// (the other columns still show).
    /// </summary>
    internal static string AvailableModsCountLine(AvailableHdrModsSnapshot snapshot)
    {
        var text = TF("Win.AvailableModsCounts", snapshot.RenoDxCount, snapshot.LumaCount, snapshot.UltraPlusCount);
        var missing = new List<string>();
        missing.Add((snapshot.RenoDxUnavailable, snapshot.LumaUnavailable) switch
        {
            (true, true) => T("Win.NeitherCatalogueCouldLoaded"),
            (true, false) => T("Win.RenoDXCatalogueCouldNotLoadedList"),
            (false, true) => T("Win.LumaCatalogueCouldNotLoadedList"),
            _ => ""
        });
        if (snapshot.SourceUnavailable) missing.Add(T("Win.SourceDatabaseCouldNotLoaded"));
        if (snapshot.UltraPlusUnavailable) missing.Add(T("Win.UltraPlusListCouldNotLoaded"));
        foreach (var line in missing.Where(line => line.Length > 0)) text += "  ·  " + line;
        return text;
    }

    internal static Grid BuildAvailableModsHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(AvailableModsColumns), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        AddHdrModsHeader(header, T("Win.Game"), 0);
        AddHdrModsHeader(header, "RenoDX", 1);
        AddHdrModsHeader(header, "Luma", 2);
        AddHdrModsHeader(header, "Ultra+", 3);
        return header;
    }

    private static void AddHdrModsHeader(Grid header, string text, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center
        };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(HdrModsSecondaryTextKey));
        header.Children.Add(block);
        Grid.SetColumn(block, column);
    }

    /// <summary>
    /// One row: the game, then RenoDX, Luma and Ultra+. A ✓ or 🔨 with a page is a button that opens it (#256, upstream
    /// 2.8.8: the Download column is gone); ✗ is plain text. Links are the catalogue's checked http(s) links (Ultra+: its
    /// own site only).
    /// </summary>
    internal static Grid BuildAvailableModRow(HdrModEntry entry, Action<string> open)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(AvailableModsColumns), ColumnSpacing = 8, Margin = new Thickness(0, 2) };
        var name = new TextBlock { Text = entry.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, entry.Name);
        row.Children.Add(name);
        AddModTick(row, entry.RenoDx, entry.RenoDxLink, "installedText", "RenoDX", 1, open);
        AddModTick(row, entry.Luma, entry.HasLuma ? entry.LumaUrl : null, "updateText", "Luma", 2, open);
        AddModTick(row, entry.HasUltraPlus ? HdrModAvailability.Available : HdrModAvailability.None, entry.UltraPlusUrl, "linkText", "Ultra+", 3, open);
        return row;
    }

    private static void AddModTick(Grid row, HdrModAvailability availability, string? url, string availableClass, string modName, int column,
        Action<string> open)
    {
        var block = new TextBlock
        {
            Text = availability switch
            {
                HdrModAvailability.Available => "✓",
                HdrModAvailability.WorkInProgress => "🔨",
                _ => "✗"
            },
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (availability == HdrModAvailability.None)
            block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(HdrModsSecondaryTextKey));
        else
            block.Classes.Add(availability == HdrModAvailability.Available ? availableClass : "warningStatusText");
        var status = availability switch
        {
            HdrModAvailability.Available => T("Win.Available"),
            HdrModAvailability.WorkInProgress => T("Win.WorkProgress"),
            _ => T("Win.NotAvailable")
        };

        Control cell = block;
        if (availability != HdrModAvailability.None && url is not null)
        {
            var button = new Button
            {
                Content = block,
                Padding = new Thickness(6, 0),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(button, status + " · " + string.Format(T("Win.OpenModPage"), modName) + "\n" + url);
            Avalonia.Automation.AutomationProperties.SetName(button, string.Format(T("Win.OpenModPage"), modName));
            button.Click += (_, _) => open(url);
            cell = button;
        }
        else
        {
            ToolTip.SetTip(block, status);
        }
        row.Children.Add(cell);
        Grid.SetColumn(cell, column);
    }
}
