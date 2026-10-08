using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>Set by App: the shared upstream manifest service, so a library Refresh also refreshes the author list.</summary>
    public UpstreamRhiManifestService? UpstreamManifest { get; set; }

    // Upstream 2.8.2 "Donate": mod authors from upstream's built-in list merged with the manifest (donationUrls +
    // authorRoles), sorted, each with a Ko-fi link. With no manifest at all the built-in list still shows.
    private void Donate_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var title = T("Win.RHILinuxSupportModAuthors");
        var manifest = UpstreamManifest;

        var intro = new TextBlock
        {
            Text = T("Win.ThesePeopleWhoMakeModsRHI"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var warning = new TextBlock
        {
            Text = T("Win.ImportantReceiveEarlyAccessRenoDXMods"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#D7B65A")),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var status = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Text = T("Win.Loading") };
        var close = new Button { Content = T("Win.Close"), Padding = new Thickness(14, 7) };

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
        // Closing mid-load cancels the fetch instead of letting it finish into a closed window. Not disposed:
        // it has no timer, and disposing it here could race the Closed handler's Cancel.
        var loadCancel = new CancellationTokenSource();
        var loadToken = loadCancel.Token;
        dialog.Closed += (_, _) => loadCancel.Cancel();

        dialog.Opened += (_, _) => RunUiSafe(async () =>
        {
            UpstreamDonationList donations;
            try
            {
                donations = manifest is null
                    ? new UpstreamDonationList(UpstreamRhiManifestService.BuiltInDonationAuthors,
                        T("Win.RHIManifestNotAvailableSession"))
                    : await manifest.FetchDonationAuthorsAsync(loadToken);
            }
            catch (OperationCanceledException) when (loadToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                status.Text = "";
                list.Children.Add(new TextBlock
                {
                    Text = TF("Win.CouldNotLoadAuthorList", UiLocalizationService.Present(ex.Message)),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.OrangeRed,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }
            if (loadToken.IsCancellationRequested) return;

            if (donations.ManifestError is { } manifestError)
            {
                list.Children.Add(new TextBlock
                {
                    Text = TF("Win.CouldNotLoadCurrentAuthorList", manifestError),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#D7B65A")),
                    Margin = new Thickness(0, 8, 0, 6)
                });
            }

            var authors = donations.Authors;
            if (authors.Count == 0)
            {
                status.Text = "";
                list.Children.Add(new TextBlock
                {
                    Text = T("Win.NoDonationLinksAvailable"),
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
            status.Text = DonateText.AuthorCount(UiLocalizationService.CurrentLanguage, authors.Count);
        });

        await ShowFittedDialogAsync(dialog);
    });

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
        // "Ko-fi" for ko-fi.com links (all of upstream's), the site's host otherwise, so the label never misnames a link.
        var kofi = new Button { Content = author.LinkLabel, FontSize = 11, Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(kofi, link);
        ToolTip.SetShowDelay(kofi, MiddleEllipsisText.TooltipDelay);
        AutomationProperties.SetName(kofi, TF("Win.On", author.Name, author.LinkLabel));
        kofi.Click += (_, _) =>
        {
            // Same opener as the Links dialog: http(s) only, argument list, no shell.
            if (!LinuxDesktopOpener.TryOpenUri(link, out var error))
                status.Text = error ?? TF("Win.CouldNotOpen", link);
        };

        row.Children.Add(name);
        row.Children.Add(role); Grid.SetColumn(role, 1);
        row.Children.Add(kofi); Grid.SetColumn(kofi, 2);
        return row;
    }
}

/// <summary>Donate dialog wording that depends on a count (PT-BR and EN).</summary>
public static class DonateText
{
    public static string AuthorCount(string? language, int count)
    {
        var ptBr = RHI.Platform.Linux.Settings.LinuxAppSettings.NormalizeUiLanguage(language) == "pt-BR";
        var noun = count == 1 ? (ptBr ? "autor" : "author") : (ptBr ? "autores" : "authors");
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} {noun}");
    }
}
