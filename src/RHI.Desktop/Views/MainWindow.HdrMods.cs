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

    // Upstream 2.7.6 "Available HDR Mods": searchable catalogue-wide list with RenoDX/Luma columns and a link.
    private async void AvailableHdrMods_Click(object? sender, RoutedEventArgs e)
    {
        var title = L("RHI-Linux — Mods HDR disponíveis", "RHI-Linux — Available HDR mods");
        if (HdrModsCatalog is not { } catalog)
        {
            await ShowSimpleMessageAsync(title, L("Os catálogos de mods não estão disponíveis nesta sessão.", "The mod catalogues are not available in this session."));
            return;
        }

        var count = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 10), Text = L("Carregando catálogos…", "Loading catalogues…") };
        var search = new TextBox { PlaceholderText = L("Pesquisar jogos…", "Search games…"), Margin = new Thickness(0, 0, 0, 10), IsEnabled = false };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(HdrModsColumns), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        AddHdrModsHeader(header, L("Jogo", "Game"), 0);
        AddHdrModsHeader(header, "RenoDX", 1);
        AddHdrModsHeader(header, "Luma", 2);
        AddHdrModsHeader(header, "Download", 3);
        var list = new StackPanel { Spacing = 2 };
        var empty = new TextBlock { Text = L("Nenhum mod encontrado.", "No mods found."), Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        var close = new Button { Content = L("Fechar", "Close"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 7), Margin = new Thickness(0, 10, 0, 0) };

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

        dialog.Opened += async (_, _) =>
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
            count.Text = LF("{0} jogos com mods HDR", "{0} games with HDR mods", snapshot.Entries.Count);
            var missing = (snapshot.RenoDxUnavailable, snapshot.LumaUnavailable) switch
            {
                (true, true) => L("Nenhum catálogo pôde ser carregado.", "Neither catalogue could be loaded."),
                (true, false) => L("O catálogo RenoDX não pôde ser carregado; a lista mostra só o Luma.", "The RenoDX catalogue could not be loaded; the list shows Luma only."),
                (false, true) => L("O catálogo Luma não pôde ser carregado; a lista mostra só o RenoDX.", "The Luma catalogue could not be loaded; the list shows RenoDX only."),
                _ => null
            };
            if (missing is not null) count.Text += "  ·  " + missing;
            search.IsEnabled = true;
            ApplyFilter();
            search.Focus();
        };

        await ShowFittedDialogAsync(dialog);
    }

    private static void AddHdrModsHeader(Grid header, string text, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center
        };
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
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(availability switch
            {
                HdrModAvailability.Available => availableColour,
                HdrModAvailability.WorkInProgress => "#D4A856",
                _ => "#6B7A8E"
            }))
        };
        ToolTip.SetTip(block, availability switch
        {
            HdrModAvailability.Available => L("Disponível", "Available"),
            HdrModAvailability.WorkInProgress => L("Em desenvolvimento", "Work in progress"),
            _ => L("Não disponível", "Not available")
        });
        row.Children.Add(block);
        Grid.SetColumn(block, column);
    }
}
