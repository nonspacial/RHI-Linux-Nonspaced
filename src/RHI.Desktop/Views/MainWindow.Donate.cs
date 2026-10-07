using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>Set by App: the shared upstream manifest service, so a library Refresh also refreshes the author list.</summary>
    public UpstreamRhiManifestService? UpstreamManifest { get; set; }

    // Upstream 2.8.2 "Donate": mod authors from the manifest (donationUrls + authorRoles), sorted, each with a Ko-fi link.
    private async void Donate_Click(object? sender, RoutedEventArgs e)
    {
        var title = L("RHI-Linux — Apoie os autores dos mods", "RHI-Linux — Support the mod authors");
        if (UpstreamManifest is not { } manifest)
        {
            await ShowSimpleMessageAsync(title, L("O manifesto do RHI não está disponível nesta sessão.", "The RHI manifest is not available in this session."));
            return;
        }

        var intro = new TextBlock
        {
            Text = L("Estas são as pessoas que criam os mods que o RHI gerencia. Se você gosta do trabalho delas, considere apoiá-las.",
                "These are the people who make the mods RHI manages. If you enjoy their work, consider supporting them."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var warning = new TextBlock
        {
            Text = L("⚠ Importante: para receber acesso antecipado aos mods RenoDX, vincule sua conta do Discord ao Ko-fi ANTES de doar. Doações feitas sem esse vínculo não podem ser creditadas depois.",
                "⚠ Important: to receive early access to RenoDX mods, link your Discord account to Ko-fi BEFORE donating. Donations made without linking first cannot be credited afterwards."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#D7B65A")),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var status = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Text = L("Carregando…", "Loading…") };
        var close = new Button { Content = L("Fechar", "Close"), Padding = new Thickness(14, 7) };

        var top = new StackPanel();
        top.Children.Add(intro);
        top.Children.Add(warning);
        top.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E2630")), Margin = new Thickness(0, 0, 0, 4) });
        // Right margin keeps the overlay scroll bar off the Ko-fi buttons.
        var list = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        Grid.SetIsSharedSizeScope(list, true);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(status);
        footer.Children.Add(close); Grid.SetColumn(close, 1);

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18) };
        panel.Children.Add(top);
        panel.Children.Add(scroll); Grid.SetRow(scroll, 1);
        panel.Children.Add(footer); Grid.SetRow(footer, 2);

        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 600,
            MinWidth = 360,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        close.Click += (_, _) => dialog.Close();

        dialog.Opened += async (_, _) =>
        {
            IReadOnlyList<UpstreamDonationAuthor> authors;
            try
            {
                authors = await manifest.FetchDonationAuthorsAsync();
            }
            catch (Exception ex)
            {
                status.Text = "";
                list.Children.Add(new TextBlock
                {
                    Text = LF("Não foi possível carregar a lista de autores: {0}", "Could not load the author list: {0}", ex.Message),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.OrangeRed,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }

            if (authors.Count == 0)
            {
                status.Text = "";
                list.Children.Add(new TextBlock
                {
                    Text = L("Nenhum link de doação disponível.", "No donation links available."),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }
            foreach (var author in authors)
            {
                if (list.Children.Count > 0)
                    list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E2630")), Opacity = 0.6 });
                list.Children.Add(BuildDonationRow(author, status));
            }
            status.Text = LF("{0} autores", "{0} authors", authors.Count);
        };

        await ShowFittedDialogAsync(dialog);
    }

    private static Control BuildDonationRow(UpstreamDonationAuthor author, TextBlock status)
    {
        var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "DonateName" });
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var name = new TextBlock
        {
            Text = author.Name,
            FontWeight = FontWeight.SemiBold,
            MaxWidth = 180,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var role = new TextBlock
        {
            Text = author.Role,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var link = author.DonationUrl.AbsoluteUri;
        var kofi = new Button { Content = "Ko-fi", FontSize = 11, Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(kofi, link);
        ToolTip.SetShowDelay(kofi, MiddleEllipsisText.TooltipDelay);
        AutomationProperties.SetName(kofi, LF("Ko-fi de {0}", "{0} on Ko-fi", author.Name));
        kofi.Click += (_, _) =>
        {
            // Same opener as the Links dialog: http(s) only, argument list, no shell.
            if (!LinuxDesktopOpener.TryOpenUri(link, out var error))
                status.Text = error ?? LF("Não foi possível abrir {0}", "Could not open {0}", link);
        };

        row.Children.Add(name);
        row.Children.Add(role); Grid.SetColumn(role, 1);
        row.Children.Add(kofi); Grid.SetColumn(kofi, 2);
        return row;
    }
}
