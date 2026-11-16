using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>What a Help menu entry does (the window carries each out with the handler it already had).</summary>
public enum HelpAction
{
    QuickStart, Guide, Discord, RenoDxWiki, LumaWiki, RhiLinuxGitHub, RhiGitHub, ReLimiterGitHub,
    PatchNotes, Motd, UpdateLog, About, Donate,
}

/// <summary>One Help menu entry; <see cref="Url"/> is set for the links.</summary>
public sealed record HelpMenuItem(HelpAction Action, string TextKey, bool SeparatorBefore, string? Url = null)
{
    public string Text => UiLocalizationService.Text(TextKey);
}

/// <summary>
/// #303 (upstream 2.9.0 top bar, every layout): "Checked … ago" beside Refresh, Update All with the number of games that
/// have updates, and the Help menu (Quick Start, Guide, Discord; the wikis and GitHub pages; Patch notes, Message of the
/// day, Update log, About and Donate). The Linux count is the games RHI has seen an update for this session (each game's
/// own analysis, or the last Update All): RHI Linux checks a game's updates when it is opened or updated, so the button
/// never claims "All up to date" for games it has not checked.
/// </summary>
public sealed partial class TopBarPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<DateTimeOffset?> _lastCheck;
    private readonly Func<IReadOnlyCollection<string>> _unattendedKeys;
    private readonly HashSet<string> _withUpdates = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="now">The clock.</param>
    /// <param name="lastCheck">When RHI last checked for updates (the status bar's "Last checked").</param>
    /// <param name="unattendedKeys">The library keys of the games Update All covers (hidden games left out, #291).</param>
    public TopBarPresenter(Func<DateTimeOffset> now, Func<DateTimeOffset?> lastCheck, Func<IReadOnlyCollection<string>> unattendedKeys)
    {
        _now = now;
        _lastCheck = lastCheck;
        _unattendedKeys = unattendedKeys;
    }

    /// <summary>"Checked 18m ago" (upstream's top bar), empty before the first check.</summary>
    public string CheckedText => _lastCheck() is { } last ? TF("Top.Checked", Ago(_now() - last)) : "";

    /// <summary>Upstream's short age: "just now", "18m ago", "2h 5m ago", "3d ago".</summary>
    public static string Ago(TimeSpan age)
    {
        if (age < TimeSpan.Zero || age.TotalMinutes < 1) return T("Top.JustNow");
        if (age.TotalMinutes < 60) return TF("Top.MinutesAgo", (int)age.TotalMinutes);
        if (age.TotalHours < 24) return TF("Top.HoursAgo", (int)age.TotalHours, age.Minutes);
        return TF("Top.DaysAgo", (int)age.TotalDays);
    }

    /// <summary>The games with an update RHI knows of, among those Update All covers.</summary>
    public int UpdateCount
    {
        get
        {
            var covered = _unattendedKeys();
            return _withUpdates.Count(key => covered.Contains(key));
        }
    }

    public bool HasUpdateCount => UpdateCount > 0;

    public string UpdateCountText => UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Update All's tooltip: how many games it will update, or what it does.</summary>
    public string UpdateAllTip => UpdateCount > 0 ? TF("Top.UpdateAllCountTip", UpdateCount) : T("Top.UpdateAllTip");

    /// <summary>A game's analysis says whether any of its components has an update.</summary>
    public void NoteGame(string key, bool hasUpdate)
    {
        var changed = hasUpdate ? _withUpdates.Add(key) : _withUpdates.Remove(key);
        if (changed) NotifyCount();
    }

    /// <summary>Update All ran: what it updated is current; games it could not update are found again when opened.</summary>
    public void UpdateAllRan()
    {
        if (_withUpdates.Count == 0) return;
        _withUpdates.Clear();
        NotifyCount();
    }

    /// <summary>The library changed (games hidden, shown or removed): the count follows.</summary>
    public void LibraryChanged() => NotifyCount();

    /// <summary>The clock moved on: "Checked … ago" follows.</summary>
    public void Tick() => OnPropertyChanged(nameof(CheckedText));

    private void NotifyCount()
    {
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasUpdateCount));
        OnPropertyChanged(nameof(UpdateCountText));
        OnPropertyChanged(nameof(UpdateAllTip));
    }

    /// <summary>The Help menu, in upstream's order and groups.</summary>
    public static IReadOnlyList<HelpMenuItem> HelpMenu { get; } =
    [
        new(HelpAction.QuickStart, "Help.QuickStart", false),
        new(HelpAction.Guide, "Help.Guide", false, RHI.Core.RhiProductInfo.RepositoryUrl + "#readme"),
        new(HelpAction.Discord, "Help.Discord", false, "https://discordapp.com/channels/1296187754979528747/1475173660686815374"),
        new(HelpAction.RenoDxWiki, "Help.RenoDxWiki", true, "https://github.com/clshortfuse/renodx/wiki/Mods"),
        new(HelpAction.LumaWiki, "Help.LumaWiki", false, "https://github.com/Filoppi/Luma-Framework/wiki"),
        new(HelpAction.RhiLinuxGitHub, "Help.RhiLinuxGitHub", false, RHI.Core.RhiProductInfo.RepositoryUrl),
        new(HelpAction.RhiGitHub, "Help.RhiGitHub", false, "https://github.com/RankFTW/RHI"),
        new(HelpAction.ReLimiterGitHub, "Help.ReLimiterGitHub", false, "https://github.com/RankFTW/ReLimiter"),
        new(HelpAction.PatchNotes, "Help.PatchNotes", true),
        new(HelpAction.Motd, "Help.Motd", false),
        new(HelpAction.UpdateLog, "Help.UpdateLog", false),
        new(HelpAction.About, "Help.About", false),
        new(HelpAction.Donate, "Help.Donate", false),
    ];
}
