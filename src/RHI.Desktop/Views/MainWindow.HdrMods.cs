using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>Set by App from the RenoDX and Luma catalogues the installers already use.</summary>
    public AvailableHdrModsCatalog? HdrModsCatalog { get; set; }

    private const string HdrModsColumns = "*,64,64,80";
    // Upstream 2.8.5: headers and ✗ marks use the secondary text colour (#A0AABB, ~8:1 on the #121518 dialog),
    // not faded or muted text (~3.4:1), so they stay readable.
    private const string HdrModsSecondaryTextKey = "RhiSecondaryTextBrush";

    // Upstream 2.7.6 "Available HDR Mods": searchable catalogue-wide list with RenoDX/Luma columns and a link.
    private void AvailableHdrMods_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var title = T("Win.RHILinuxAvailableHDRMods");
        if (HdrModsCatalog is not { } catalog)
        {
            await ShowSimpleMessageAsync(title, T("Win.ModCataloguesNotAvailableSession"));
            return;
        }

        var count = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 10), Text = T("Win.LoadingCatalogues") };
        var search = new TextBox { PlaceholderText = T("Win.SearchGames"), Margin = new Thickness(0, 0, 0, 10), IsEnabled = false };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(HdrModsColumns), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        AddHdrModsHeader(header, T("Win.Game"), 0);
        AddHdrModsHeader(header, "RenoDX", 1);
        AddHdrModsHeader(header, "Luma", 2);
        AddHdrModsHeader(header, "Download", 3);
        var list = new StackPanel { Spacing = 2 };
        var empty = new TextBlock { Text = T("Win.NoModsFound"), Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        var close = new Button { Content = T("Win.Close"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 7), Margin = new Thickness(0, 10, 0, 0) };

        var top = new StackPanel();
        top.Children.Add(count);
        top.Children.Add(search);
        top.Children.Add(header);
        top.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E2630")), Margin = new Thickness(0, 0, 0, 6) });
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
                count.Text = ex.Message;
                return;
            }

            foreach (var entry in snapshot.Entries)
            {
                var row = BuildHdrModRow(entry);
                rows.Add((entry, row));
                list.Children.Add(row);
            }
            count.Text = TF("Win.GamesHDRMods", snapshot.Entries.Count);
            var missing = (snapshot.RenoDxUnavailable, snapshot.LumaUnavailable) switch
            {
                (true, true) => T("Win.NeitherCatalogueCouldLoaded"),
                (true, false) => T("Win.RenoDXCatalogueCouldNotLoadedList"),
                (false, true) => T("Win.LumaCatalogueCouldNotLoadedList"),
                _ => null
            };
            if (missing is not null) count.Text += "  ·  " + missing;
            search.IsEnabled = true;
            ApplyFilter();
            search.Focus();
        });

        await ShowFittedDialogAsync(dialog);
    });

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

    private Control BuildHdrModRow(HdrModEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(HdrModsColumns), ColumnSpacing = 8, Margin = new Thickness(0, 2) };
        var name = new TextBlock { Text = entry.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, entry.Name);
        row.Children.Add(name);
        AddHdrModStatus(row, entry.RenoDx, "#5ECB7D", 1);
        AddHdrModStatus(row, entry.Luma, "#B898E8", 2);

        Control link;
        if (entry.DownloadUrl is { } url)
        {
            var button = new Button { Content = "Link", FontSize = 11, Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(button, url);
            button.Click += (_, _) => OpenProjectLink(url);
            link = button;
        }
        else
        {
            link = new TextBlock { Text = "—", FontSize = 11, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        row.Children.Add(link); Grid.SetColumn(link, 3);
        return row;
    }

    private static void AddHdrModStatus(Grid row, HdrModAvailability availability, string availableColour, int column)
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
            block.Foreground = new SolidColorBrush(Color.Parse(availability == HdrModAvailability.Available ? availableColour : "#D4A856"));
        ToolTip.SetTip(block, availability switch
        {
            HdrModAvailability.Available => T("Win.Available"),
            HdrModAvailability.WorkInProgress => T("Win.WorkProgress"),
            _ => T("Win.NotAvailable")
        });
        row.Children.Add(block);
        Grid.SetColumn(block, column);
    }
}
