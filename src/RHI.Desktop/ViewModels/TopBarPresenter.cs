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
/// have updates ("Update All (N)", else "All up to date"), and the Help menu (Quick Start, Guide, Discord; the wikis and
/// GitHub pages; Patch notes, Message of the day, Update log, About and Donate). w14/layouts-c: the count comes from the
/// library-wide check-only pass (<see cref="NoteCheckPass"/>, with the background update cadence) and each game's own
/// analysis; "All up to date" shows only once every game Update All covers has been checked and none has an update.
/// </summary>
public sealed partial class TopBarPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<DateTimeOffset?> _lastCheck;
    private readonly Func<IReadOnlyCollection<string>> _unattendedKeys;
    private readonly HashSet<string> _withUpdates = new(StringComparer.OrdinalIgnoreCase);
    // w14/layouts-c: the games whose updates are known (checked by the pass or their own analysis since the last Update All).
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private int _notChecked;

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

    /// <summary>
    /// Upstream's "All up to date": every game Update All covers has been checked and none has an update. Never while a game is
    /// unknown (added since, or not reached because GitHub's request limit was reached).
    /// </summary>
    public bool AllUpToDate => UpdateCount == 0 && _unattendedKeys().All(_checked.Contains);

    /// <summary>The button's words: "All up to date", else "Update All" (the count shows beside it).</summary>
    public string UpdateAllText => AllUpToDate ? T("Top.AllUpToDate") : T("Ui.UpdateAll");

    /// <summary>Update All's tooltip: how many games it will update, that all are up to date, or what it does; and when the
    /// last check was cut short by GitHub's request limit, how many games it did not reach.</summary>
    public string UpdateAllTip
    {
        get
        {
            var tip = UpdateCount > 0 ? TF("Top.UpdateAllFoundTip", UpdateCount) : AllUpToDate ? T("Top.AllUpToDateTip") : T("Top.UpdateAllTip");
            return _notChecked > 0 ? tip + "\n\n" + TF("Top.CheckCutShort", _notChecked) : tip;
        }
    }

    /// <summary>Whether a game has an update RHI knows of (the game list's mark).</summary>
    public bool HasUpdate(string key) => _withUpdates.Contains(key);

    /// <summary>Raised when the games with updates changed (the game list's marks follow).</summary>
    public event Action? UpdatesChanged;

    /// <summary>A game's analysis says whether any of its components has an update.</summary>
    public void NoteGame(string key, bool hasUpdate)
    {
        var changed = hasUpdate ? _withUpdates.Add(key) : _withUpdates.Remove(key);
        changed |= _checked.Add(key);
        if (changed) NotifyCount();
    }

    /// <summary>
    /// w14/layouts-c: the library-wide check-only pass's findings (library key → has an update) and how many games it did not
    /// reach (GitHub's request limit). A game it could not check keeps what was known.
    /// </summary>
    public void NoteCheckPass(IReadOnlyDictionary<string, bool> games, int notChecked)
    {
        foreach (var (key, hasUpdate) in games)
        {
            if (hasUpdate) _withUpdates.Add(key);
            else _withUpdates.Remove(key);
            _checked.Add(key);
        }
        _notChecked = Math.Max(0, notChecked);
        NotifyCount();
    }

    /// <summary>Update All ran: nothing is known until the games are checked again (the shell asks for a check pass).</summary>
    public void UpdateAllRan()
    {
        if (_withUpdates.Count == 0 && _checked.Count == 0) return;
        _withUpdates.Clear();
        _checked.Clear();
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
        OnPropertyChanged(nameof(AllUpToDate));
        OnPropertyChanged(nameof(UpdateAllText));
        UpdatesChanged?.Invoke();
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
