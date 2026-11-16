using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>One version of the notes: its <c>##</c> heading, the short name the list shows, and its text (heading included).</summary>
public sealed record WhatsNewSection(string Title, string NavLabel, string Body);

/// <summary>One entry of What's New's version list: shown or not by the search, its hit count, selected or not.</summary>
public sealed partial class WhatsNewNavItem(int index, string label) : ObservableObject
{
    public int Index { get; } = index;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;
    /// <summary>How many lines of this version match the search ("" without a search or a hit).</summary>
    [ObservableProperty] private string _count = "";
}

/// <summary>
/// #309 (upstream 2.9.2 <c>ShowPatchNotesDialogAsync</c>): What's New shows the notes one version at a time, picked from a
/// list down the left; a search narrows the list to the versions that mention the text and shows every matching line,
/// under the <c>###</c> heading it sits under; an arrow hides the list and search to widen the notes; + and − change the
/// text size (10–20). RHI remembers both. Linux: the notes are this project's CHANGELOG.md, one entry per <c>##</c> section
/// (a wave or release), with the text above the first section shown over the newest one, as upstream's preamble.
/// </summary>
public sealed partial class WhatsNewPresenter : ObservableObject
{
    /// <summary>A results page stops adding lines after this many (upstream: 400).</summary>
    public const int MaxResultLines = 400;

    private readonly Action<bool, double> _save;

    /// <param name="save">Saves the list's hidden state and the text size (the app's settings).</param>
    public WhatsNewPresenter(string markdown, bool navHidden, double fontSize, Action<bool, double> save)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        (Preamble, Sections) = Split(markdown ?? "");
        foreach (var (section, index) in Sections.Select((section, index) => (section, index)))
            NavItems.Add(new WhatsNewNavItem(index, section.NavLabel));
        _navHidden = navHidden;
        _fontSize = ClampFontSize(fontSize);
        if (Sections.Count > 0) ShowVersion(0);
        else Text = UiLocalizationService.Text("Wn.NoNotes");
    }

    public string Preamble { get; }
    public IReadOnlyList<WhatsNewSection> Sections { get; }
    public ObservableCollection<WhatsNewNavItem> NavItems { get; } = [];

    /// <summary>The list is worth showing (upstream shows only the notes when there is a single version).</summary>
    public bool HasNav => Sections.Count > 1;

    /// <summary>The version on show; -1 while a search's results are on show.</summary>
    [ObservableProperty] private int _selectedIndex = -1;

    /// <summary>The notes on show, as Markdown (the window turns it into text blocks).</summary>
    [ObservableProperty] private string _text = "";

    [ObservableProperty] private bool _navHidden;

    [ObservableProperty] private double _fontSize;

    public bool CanMakeBigger => FontSize < LinuxAppSettings.WhatsNewMaxFontSize;
    public bool CanMakeSmaller => FontSize > LinuxAppSettings.WhatsNewMinFontSize;

    /// <summary>The arrow's tooltip: what a click does now.</summary>
    public string NavToggleTip => UiLocalizationService.Text(NavHidden ? "Wn.ShowNavTip" : "Wn.HideNavTip");

    /// <summary>Shows one version (the text above the first version stays above the newest).</summary>
    public void ShowVersion(int index)
    {
        if (index < 0 || index >= Sections.Count) return;
        SelectedIndex = index;
        var body = Sections[index].Body;
        Text = index == 0 && Preamble.Length > 0 ? Preamble + "\n\n" + body : body;
    }

    /// <summary>
    /// Narrows the list to the versions that mention <paramref name="query"/> and shows every matching line; an empty search
    /// shows every version again and the one picked before (the newest when none was).
    /// </summary>
    public void ApplySearch(string? query)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0)
        {
            foreach (var item in NavItems)
            {
                item.IsVisible = true;
                item.Count = "";
            }
            ShowVersion(SelectedIndex >= 0 ? SelectedIndex : _lastPicked);
            return;
        }

        if (SelectedIndex >= 0) _lastPicked = SelectedIndex;
        var page = new StringBuilder();
        int versionsHit = 0, linesHit = 0;
        for (var i = 0; i < Sections.Count; i++)
        {
            var lines = MatchingLines(Sections[i].Body, query);
            var hits = lines.Count(line => line.StartsWith("- ", StringComparison.Ordinal));
            NavItems[i].IsVisible = lines.Count > 0;
            NavItems[i].Count = hits > 0 ? hits.ToString(CultureInfo.InvariantCulture) : "";
            if (lines.Count == 0) continue;
            versionsHit++;
            linesHit += hits;
            if (linesHit > MaxResultLines) continue; // keep the results page a sensible size
            page.Append("## ").Append(Sections[i].Title).Append("\n\n");
            foreach (var line in lines) page.Append(line).Append("\n\n");
        }
        SelectedIndex = -1;
        Text = versionsHit == 0
            ? UiLocalizationService.Format("Wn.NoMatch", query)
            : UiLocalizationService.Format("Wn.Matches", linesHit, versionsHit) + "\n\n" + page;
    }

    private int _lastPicked;

    /// <summary>Hides or shows the version list and search; remembered.</summary>
    public void ToggleNav()
    {
        NavHidden = !NavHidden;
        _save(NavHidden, FontSize);
    }

    public void MakeBigger() => SetFontSize(FontSize + 1);
    public void MakeSmaller() => SetFontSize(FontSize - 1);

    private void SetFontSize(double size)
    {
        size = ClampFontSize(size);
        if (Math.Abs(size - FontSize) < 0.01) return;
        FontSize = size;
        _save(NavHidden, FontSize);
    }

    partial void OnFontSizeChanged(double value)
    {
        OnPropertyChanged(nameof(CanMakeBigger));
        OnPropertyChanged(nameof(CanMakeSmaller));
    }

    partial void OnNavHiddenChanged(bool value) => OnPropertyChanged(nameof(NavToggleTip));

    partial void OnSelectedIndexChanged(int value)
    {
        foreach (var item in NavItems) item.IsSelected = item.Index == value;
    }

    private static double ClampFontSize(double size)
        => double.IsFinite(size)
            ? Math.Clamp(Math.Round(size), LinuxAppSettings.WhatsNewMinFontSize, LinuxAppSettings.WhatsNewMaxFontSize)
            : LinuxAppSettings.WhatsNewDefaultFontSize;

    /// <summary>
    /// Splits the notes at each <c>##</c> heading (never inside a fenced code block): the text before the first one, and one
    /// section per heading with its text.
    /// </summary>
    public static (string Preamble, IReadOnlyList<WhatsNewSection> Sections) Split(string markdown)
    {
        var preamble = new StringBuilder();
        var sections = new List<WhatsNewSection>();
        string? title = null;
        var body = new StringBuilder();
        var inFence = false;
        void Flush()
        {
            if (title is not null) sections.Add(new WhatsNewSection(title, NavLabel(title), body.ToString().TrimEnd()));
            body.Clear();
        }
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && raw.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                title = raw[3..].Trim();
            }
            (title is null ? preamble : body).Append(raw).Append('\n');
        }
        Flush();
        return (preamble.ToString().Trim(), sections);
    }

    /// <summary>The list's short name of a section: its heading up to " — " or ": " ("2.8.8", "Wave 13", "In progress").</summary>
    public static string NavLabel(string title)
    {
        var cut = title.Length;
        foreach (var separator in new[] { " — ", ": " })
        {
            var at = title.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && at < cut) cut = at;
        }
        return title[..cut].Trim();
    }

    /// <summary>
    /// The lines of one version that contain <paramref name="query"/> (any case), each as a "- " line, with the <c>###</c>
    /// heading they sit under (bold, once) before the first of them (upstream's MatchingLines).
    /// </summary>
    public static List<string> MatchingLines(string body, string query)
    {
        var result = new List<string>();
        string? heading = null;
        var headingShown = false;
        foreach (var raw in body.Split('\n'))
        {
            var trimmed = raw.TrimEnd('\r').TrimStart();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                heading = trimmed[4..].Trim();
                headingShown = false;
                continue;
            }
            if (trimmed.Length == 0 || !trimmed.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (heading is not null && !headingShown)
            {
                result.Add($"**{heading}**");
                headingShown = true;
            }
            result.Add("- " + trimmed.TrimStart('-', '*', ' '));
        }
        return result;
    }
}
