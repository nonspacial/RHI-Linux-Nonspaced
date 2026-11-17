using System.Globalization;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Achievements;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>The strip under Launch: progress, the latest unlock and the recent ones (upstream's hero strip).</summary>
public sealed record AchievementStrip(string Progress, int Percent, string? Latest, IReadOnlyList<Achievement> Recent);

/// <summary>One row of the side panel's list.</summary>
public sealed record AchievementRow(Achievement Source, string Title, string Description, string? When, string? Rarity, bool Masked)
{
    public bool Unlocked => Source.Unlocked;
}

/// <summary>
/// w14/layouts-b (#307, upstream 2.9.2/2.9.3 Spotlight achievements): Settings › Achievements › Steam (off by default) and
/// Show hidden; the strip under Launch (progress, latest, recent unlocks) and the side panel's list (unlock dates,
/// rarity, search, hidden ones masked until Show hidden). Steam games only: Xbox achievements are not applicable on Linux
/// (#313). Saved at once, like the layout fields.
/// </summary>
public sealed class AchievementsPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>How many recent unlocks the strip shows (upstream).</summary>
    public const int RecentCount = 5;

    private readonly Func<LinuxAppSettings> _settings;
    private readonly Func<Action<LinuxAppSettings>, string?> _save;

    public AchievementsPresenter(Func<LinuxAppSettings> settings, Func<Action<LinuxAppSettings>, string?> save, SteamAchievementService? service)
    {
        _settings = settings;
        _save = save;
        Service = service;
    }

    /// <summary>Steam's files reader; null in a build without it (nothing shows).</summary>
    public SteamAchievementService? Service { get; }

    /// <summary>Raised after the Steam or Show hidden switch changed.</summary>
    public event Action? Changed;

    public bool SteamOn => _settings().SteamAchievements;
    public bool ShowHidden => _settings().AchievementsShowHidden;

    public string? SetSteamOn(bool on) => Save(s => s.SteamAchievements = on, SteamOn == on);
    public string? SetShowHidden(bool on) => Save(s => s.AchievementsShowHidden = on, ShowHidden == on);

    private string? Save(Action<LinuxAppSettings> change, bool unchanged)
    {
        if (unchanged) return null;
        if (_save(change) is { } refused) return refused;
        change(_settings());
        Changed?.Invoke();
        return null;
    }

    /// <summary>The Steam AppID whose achievements a game shows: a Steam game's, when Settings › Achievements › Steam is on.</summary>
    public int? AppIdFor(DetectedGame? game)
        => SteamOn && Service is not null && game?.SteamAppId is > 0 and var id && string.Equals(game.Source, "Steam", StringComparison.OrdinalIgnoreCase) ? id : null;

    /// <summary>The strip: "12 of 20 · 60%", the latest unlock with its date, the newest unlocks.</summary>
    public static AchievementStrip Strip(AchievementSet set, DateTime nowUtc)
    {
        var latest = set.Latest;
        return new AchievementStrip(
            TF("Ach.Progress", set.Unlocked, set.Total, set.PercentDone),
            set.PercentDone,
            latest is null ? null : latest.UnlockedUtc is { } when ? TF("Ach.LatestWhen", latest.Name, When(when, nowUtc)) : TF("Ach.Latest", latest.Name),
            set.Items.Where(a => a.Unlocked).Take(RecentCount).ToList());
    }

    /// <summary>The list: unlocked first (newest first), then locked; filtered by <paramref name="query"/> (masked rows match only "hidden").</summary>
    public static IReadOnlyList<AchievementRow> Rows(AchievementSet set, bool showHidden, string? query, DateTime nowUtc)
    {
        var q = query?.Trim() ?? "";
        var rows = new List<AchievementRow>();
        foreach (var a in set.Items)
        {
            var masked = a.IsMasked(showHidden);
            var title = masked ? T("Ach.HiddenTitle") : a.Name;
            var desc = masked ? T("Ach.HiddenDesc") : a.Description;
            if (q.Length > 0 && !title.Contains(q, StringComparison.CurrentCultureIgnoreCase) && !desc.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
            var when = a.Unlocked ? a.UnlockedUtc is { } t ? TF("Ach.UnlockedOn", When(t, nowUtc)) : T("Ach.Unlocked") : null;
            rows.Add(new AchievementRow(a, title, desc, when, a.Percent is { } p ? RarityText(p) : null, masked));
        }
        return rows;
    }

    /// <summary>"12.3% of players" (one decimal under 10%, as Steam does).</summary>
    public static string RarityText(double percent)
        => TF("Ach.Rarity", percent < 10 ? percent.ToString("0.0", CultureInfo.CurrentCulture) : Math.Round(percent).ToString(CultureInfo.CurrentCulture));

    /// <summary>A date as the list shows it: today, yesterday, N days ago (under a week), else the local date.</summary>
    public static string When(DateTime utc, DateTime nowUtc)
    {
        var day = utc.ToLocalTime().Date;
        var today = nowUtc.ToLocalTime().Date;
        var days = (today - day).Days;
        return days switch
        {
            <= 0 => T("Ach.Today"),
            1 => T("Ach.Yesterday"),
            < 7 => TF("Ach.DaysAgo", days),
            _ => day.ToString("d MMM yyyy", UiLocalizationService.CurrentLanguage.StartsWith("pt", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("pt-BR") : CultureInfo.GetCultureInfo("en-GB")),
        };
    }
}
