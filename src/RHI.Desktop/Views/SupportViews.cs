using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>Settings → Support (Refs #192): Copy logs, Open logs folder, Export game data.</summary>
internal static class SupportSettingsTab
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static TabItem Create(SupportPresenter support, TextBlock status, Func<string, string?> openFolder, Func<string, Task<bool>> copyText)
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("Shell.SupportTab"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = T("Shell.SupportIntro"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });

        var copyLogs = Button("Shell.CopyLogs", "Shell.CopyLogsTip", "CopyLogsButton");
        var openLogs = Button("Shell.OpenLogsFolder", "Shell.OpenLogsFolderTip", "OpenLogsFolderButton");
        var export = Button("Shell.ExportGameData", "Shell.ExportGameDataTip", "ExportGameDataButton");
        copyLogs.Click += (_, _) => UiExceptionGuard.Forget(Run(copyLogs, status, () => support.CopyLogsAsync(copyText)), "CopyLogs");
        export.Click += (_, _) => UiExceptionGuard.Forget(Run(export, status, () => support.ExportGameDataAsync(copyText)), "ExportGameData");
        openLogs.Click += (_, _) => status.Text = openFolder(support.LogDirectory) ?? "";

        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(copyLogs);
        buttons.Children.Add(openLogs);
        buttons.Children.Add(export);
        panel.Children.Add(buttons);
        return new TabItem { Header = T("Shell.SupportTab"), Content = new ScrollViewer { Content = panel } };
    }

    private static async Task Run(Button button, TextBlock status, Func<Task<string>> action)
    {
        button.IsEnabled = false;
        try { status.Text = await action(); }
        finally { button.IsEnabled = true; }
    }

    private static Button Button(string labelKey, string tipKey, string name)
    {
        var button = new Button { Content = T(labelKey), Padding = new Thickness(12, 6), Name = name };
        ToolTip.SetTip(button, T(tipKey));
        ToolTip.SetShowDelay(button, MiddleEllipsisText.TooltipDelay);
        return button;
    }
}

/// <summary>The fork's own CHANGELOG.md, embedded at build time (Refs #192: the What's new viewer).</summary>
internal static class ChangelogText
{
    public const string ResourceName = "RHI.Desktop.CHANGELOG.md";

    public static string Load()
    {
        using var stream = typeof(ChangelogText).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The changelog as display blocks: headings (<c>#</c>, <c>##</c>) and paragraphs/list items, with markdown markers
    /// (emphasis, inline code, link targets, admonition tags) taken out so it reads as plain text.
    /// </summary>
    public static IReadOnlyList<(int Level, string Text)> Blocks(string markdown)
    {
        var blocks = new List<(int, string)>();
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count > 0) blocks.Add((0, Plain(string.Join(" ", paragraph))));
            paragraph.Clear();
        }
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { Flush(); continue; }
            if (trimmed.StartsWith('#'))
            {
                Flush();
                var level = trimmed.TakeWhile(c => c == '#').Count();
                blocks.Add((level, Plain(trimmed[level..].Trim())));
                continue;
            }
            if (trimmed.StartsWith("> [!", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith('>')) trimmed = trimmed.TrimStart('>').TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                paragraph.Add("• " + trimmed[2..]);
                continue;
            }
            paragraph.Add(trimmed);
        }
        Flush();
        return blocks;
    }

    private static string Plain(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
    }
}

public sealed partial class MainWindow
{
    /// <summary>Puts <paramref name="text"/> on the clipboard; false when the session has none.</summary>
    internal async Task<bool> CopyTextToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>
    /// Copy game report (Refs #192, upstream <c>GameReportEncoder.ShowAndCopyAsync</c>): asks the user to correct wrong
    /// values in Edit game first (upstream's gate, as one line), takes an optional note, then saves the report and puts
    /// it on the clipboard.
    /// </summary>
    internal async Task ShowGameReportDialogAsync(DetectedGame game)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var note = new TextBox
        {
            PlaceholderText = T("Shell.ReportNotePlaceholder"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 80, MaxHeight = 160, Name = "GameReportNote"
        };
        var copy = new Button { Content = T("Shell.ReportCopy"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(copy);
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("Shell.ReportGate"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = T("Shell.ReportIntro"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        panel.Children.Add(note);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = TF("Shell.ReportTitle", game.DisplayName()),
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MinWidth = 360,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        cancel.Click += (_, _) => dialog.Close();
        copy.Click += (_, _) => RunUiSafe(async () =>
        {
            copy.IsEnabled = cancel.IsEnabled = false;
            try
            {
                vm.StatusText = await vm.Support.CopyReportAsync(game, note.Text, CopyTextToClipboardAsync);
                dialog.Close();
            }
            finally { copy.IsEnabled = cancel.IsEnabled = true; }
        });
        await ShowFittedDialogAsync(dialog);
    }

    /// <summary>
    /// Help (Refs #192, upstream's FAQ / Quick Start page and About page): getting started, questions, credits and
    /// licences, and What's new. Donate stays upstream's authors only (it is not here).
    /// </summary>
    internal async Task ShowHelpAsync()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        void Heading(string key) => panel.Children.Add(new TextBlock
        {
            Text = T(key), FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 8, 0, 0)
        });
        void Text(string text, double opacity = 0.86) => panel.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity });

        Heading("Win.RHILinuxQuickStart");
        Text(T("Win.N1UseRefreshDetectSteamHeroic"));

        Heading("Shell.HelpQuestions");
        foreach (var (question, answer) in Faq)
        {
            panel.Children.Add(new TextBlock { Text = T(question), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            Text(T(answer), 0.76);
        }

        Heading("Shell.HelpCredits");
        Text(T("Shell.CreditsIntro"), 0.72);
        foreach (var credit in Credits)
        {
            var line = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
            line.Children.Add(new TextBlock
            {
                Text = credit.Licence is null ? $"{credit.Name} — {credit.Author}" : $"{credit.Name} — {credit.Author} · {credit.Licence}",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            line.Children.Add(new TextBlock { Text = T(credit.DescriptionKey), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
            var link = new Button
            {
                Content = new MiddleEllipsisText { FullText = credit.Url, WrapNonPath = false }, Padding = new Thickness(8, 3),
                HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 520
            };
            link.Click += (_, _) => OpenProjectLink(credit.Url);
            line.Children.Add(link);
            panel.Children.Add(line);
        }

        var whatsNew = new Button { Content = T("Shell.WhatsNew"), Padding = new Thickness(14, 7), Name = "HelpWhatsNew" };
        var close = new Button { Content = T("Win.Close"), Padding = new Thickness(14, 7) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        footer.Children.Add(whatsNew);
        footer.Children.Add(close);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        root.Children.Add(footer);
        Grid.SetRow(footer, 1);
        var dialog = new Window
        {
            Title = T("Shell.HelpTitle"), Width = 720, Height = 760, MinWidth = 420, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root
        };
        close.Click += (_, _) => dialog.Close();
        whatsNew.Click += (_, _) => RunUiSafe(ShowChangelogAsync);
        await ShowFittedDialogAsync(dialog);
    }

    /// <summary>What's new: the fork's own CHANGELOG.md (upstream shows its patch notes from the status-bar version).</summary>
    internal async Task ShowChangelogAsync()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(18) };
        foreach (var (level, text) in ChangelogText.Blocks(ChangelogText.Load()))
            panel.Children.Add(new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = level > 0 ? FontWeight.SemiBold : FontWeight.Normal,
                FontSize = level switch { 1 => 20, 2 => 16, 3 => 14, _ => 12 },
                Foreground = level is 1 or 2 ? Brushes.LightSkyBlue : null,
                Margin = new Thickness(0, level is 1 or 2 ? 10 : 0, 0, 0),
                Opacity = level > 0 ? 1 : 0.86
            });
        var close = new Button { Content = T("Win.Close"), Padding = new Thickness(14, 7), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        root.Children.Add(close);
        Grid.SetRow(close, 1);
        var dialog = new Window
        {
            Title = TF("Shell.ChangelogTitle", RhiProductInfo.Version), Width = 760, Height = 760, MinWidth = 420, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root
        };
        close.Click += (_, _) => dialog.Close();
        await ShowFittedDialogAsync(dialog);
    }

    private void ProductVersion_Tapped(object? sender, TappedEventArgs e) => RunUiSafe(ShowChangelogAsync);

    private static readonly (string Question, string Answer)[] Faq =
    [
        ("Shell.FaqQ1", "Shell.FaqA1"), ("Shell.FaqQ2", "Shell.FaqA2"), ("Shell.FaqQ3", "Shell.FaqA3"),
        ("Shell.FaqQ4", "Shell.FaqA4"), ("Shell.FaqQ5", "Shell.FaqA5")
    ];

    private sealed record Credit(string Name, string Author, string? Licence, string DescriptionKey, string Url);

    // Upstream's credits that apply on Linux (names, authors and licences as upstream lists them), plus the Linux port this
    // build started from and the UI toolkit. Not a donation list: Donate stays upstream's authors only.
    private static readonly Credit[] Credits =
    [
        new("RHI", "RankFTW", "GPL-3.0", "Shell.CreditRhi", "https://github.com/RankFTW"),
        new("RHI Linux", "wizardkof", "GPL-3.0", "Shell.CreditRhiLinux", RhiProductInfo.RepositoryUrl),
        new("RenoDX", "clshortfuse & contributors", "MIT", "Shell.CreditRenoDx", "https://github.com/clshortfuse/renodx"),
        new("ReShade", "Crosire", "BSD 3-Clause", "Shell.CreditReShade", "https://reshade.me"),
        new("Luma Framework", "Pumbo (Filoppi)", "Source-available", "Shell.CreditLuma", "https://github.com/Filoppi/Luma-Framework"),
        new("Display Commander", "pmnox", "Source-available", "Shell.CreditDisplayCommander", "https://github.com/pmnoxx/display-commander"),
        new("RE Framework", "praydog", "MIT", "Shell.CreditReFramework", "https://github.com/praydog/REFramework-nightly"),
        new("OptiScaler", "OptiScaler contributors", "Source-available", "Shell.CreditOptiScaler", "https://github.com/optiscaler/OptiScaler"),
        new("Universal UE DOF Fix", "Lazorr", null, "Shell.CreditDofFix", "https://github.com/RankFTW/rhi-repo/releases"),
        new("Avalonia", "AvaloniaUI", "MIT", "Shell.CreditAvalonia", "https://github.com/AvaloniaUI/Avalonia"),
        new("CommunityToolkit.Mvvm", "Microsoft", "MIT", "Shell.CreditMvvm", "https://github.com/CommunityToolkit/dotnet")
    ];
}
