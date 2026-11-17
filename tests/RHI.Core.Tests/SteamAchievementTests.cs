using System.Net;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Achievements;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-b (#307): Steam achievements from Steam's own files on Linux (native and Flatpak) — the binary KeyValues
/// schema pinned from Steam (The Jackbox Party Pack 2, 331670), a stats file in Steam's layout, Steam's real global
/// percentages answer, registry.vdf / loginusers.vdf, the strip and the list (masking, search, dates, rarity), and the
/// Settings › Achievements switches (upstream's AchievementMaskTests and SteamAchievementTests, ported).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SteamAchievementTests : IDisposable
{
    private const int AppId = 331670;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-ach-{Guid.NewGuid():N}");

    public SteamAchievementTests() => UiLocalizationService.Apply("en-US");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "achievements", name);

    /// <summary>A Steam folder with the pinned schema and stats; registry.vdf naming the account when given.</summary>
    private (SteamLocalFiles Files, string Steam) SteamWith(string? activeUser = "12345678", string folder = "Steam")
    {
        var steam = Path.Combine(_root, folder);
        var stats = SteamLocalFiles.StatsDir(steam);
        Directory.CreateDirectory(stats);
        File.Copy(Fixture($"UserGameStatsSchema_{AppId}.bin"), Path.Combine(stats, $"UserGameStatsSchema_{AppId}.bin"));
        File.Copy(Fixture($"UserGameStats_12345678_{AppId}.bin"), Path.Combine(stats, $"UserGameStats_12345678_{AppId}.bin"));
        var registry = Path.Combine(_root, "registry.vdf");
        File.WriteAllText(registry, "\"Registry\"\n{\n\t\"HKCU\"\n\t{\n\t\t\"Software\"\n\t\t{\n\t\t\t\"Valve\"\n\t\t\t{\n\t\t\t\t\"Steam\"\n\t\t\t\t{\n"
            + "\t\t\t\t\t\"language\"\t\t\"english\"\n"
            + (activeUser is null ? "" : $"\t\t\t\t\t\"ActiveProcess\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"ActiveUser\"\t\t\"{activeUser}\"\n\t\t\t\t\t}}\n")
            + "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n");
        return (new SteamLocalFiles([Path.Combine(_root, "missing"), steam], [registry]), steam);
    }

    [Fact]
    public void BinaryKeyValues_ReadsSteamsSchema()
    {
        var schema = SteamBinaryKv.Parse(File.ReadAllBytes(Fixture($"UserGameStatsSchema_{AppId}.bin")));
        var bits = (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)schema["331670"])["stats"])["1"])["bits"];
        Assert.Equal(20, bits.Count);
        Assert.Throws<FormatException>(() => SteamBinaryKv.Parse([1, (byte)'k', 0, (byte)'x'])); // unterminated string
        Assert.Throws<FormatException>(() => SteamBinaryKv.Parse([9, (byte)'k', 0]));              // unknown type
        Assert.Throws<FormatException>(() => SteamBinaryKv.Parse(Enumerable.Repeat(new byte[] { 0, (byte)'a', 0 }, 40).SelectMany(b => b).ToArray()));
    }

    [Fact]
    public void Load_UnlockedNewestFirst_ThenLockedInOrder_FromTheSignedInAccount()
    {
        var (files, _) = SteamWith();
        var set = new SteamAchievementService(files, cacheRoot: Path.Combine(_root, "cache")).Load(AppId)!;
        Assert.Equal((20, 3, 15), (set.Total, set.Unlocked, set.PercentDone));
        Assert.Equal(["Word Spud: Spud Stud", "Drawful: Schmuck of the Draw", "Drawful: Goodfinger"], set.Items.Take(3).Select(a => a.Name));
        Assert.Equal(new DateTime(2023, 11, 14, 22, 16, 40, DateTimeKind.Utc), set.Items[0].UnlockedUtc);
        Assert.Equal("YDKJ 2015: Drain the Main Brain", set.Items[3].Name); // locked: the game's order
        Assert.Equal("Get all questions right in an episode", set.Items[3].Description);
        Assert.EndsWith(".jpg", set.Items[3].Icon);
        Assert.True(set.Items.Single(a => a.Name.StartsWith("Lie Swatter: \"What", StringComparison.Ordinal)).Hidden);
        Assert.Null(new SteamAchievementService(files).Load(1)); // no schema: no achievements
    }

    [Fact]
    public void Load_AnotherSignedInAccount_HasNothingUnlocked_UnknownAccountTakesTheNewestFile()
    {
        var (other, _) = SteamWith(activeUser: "999");
        Assert.Equal(0, new SteamAchievementService(other).Load(AppId)!.Unlocked);
        Directory.Delete(Path.Combine(_root, "Steam"), true);
        var (unknown, _) = SteamWith(activeUser: null);
        Assert.Equal(3, new SteamAchievementService(unknown).Load(AppId)!.Unlocked);
    }

    [Fact]
    public void FlatpakSteam_IsFound_AndLinkedFoldersCountOnce()
    {
        var (files, steam) = SteamWith(folder: Path.Combine(".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        var link = Path.Combine(_root, "steam-link");
        Directory.CreateSymbolicLink(link, steam);
        var both = new SteamLocalFiles([steam, link], []);
        Assert.Single(both.Roots());
        Assert.Equal(steam, files.RootWithSchema(AppId));
    }

    [Fact]
    public void AccountAndLanguage_FromRegistryVdf_ElseLoginUsers()
    {
        var (files, steam) = SteamWith(activeUser: "4242");
        Assert.Equal(4242u, files.AccountId(steam));
        Assert.Equal("english", files.Language());
        const string login = """
            "users"
            {
                "76561197960265829" { "AccountName" "a" "MostRecent" "0" }
                "76561197960266728" { "AccountName" "b" "MostRecent" "1" }
            }
            """;
        Assert.Equal(1000u, SteamLocalFiles.ParseLoginUsers(login));
        Assert.Equal(101u, SteamLocalFiles.ParseLoginUsers("\"users\" { \"76561197960265829\" { \"MostRecent\" \"0\" } }"));
        Assert.Null(SteamLocalFiles.ParseLoginUsers("\"users\" { }"));
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        File.WriteAllText(Path.Combine(steam, "config", "loginusers.vdf"), login);
        Assert.Equal(1000u, new SteamLocalFiles([steam], []).AccountId(steam));
    }

    [Fact]
    public void Rarity_FromSteamsRealAnswer_AddedByApiName()
    {
        var rarity = SteamAchievementService.ParseRarity(File.ReadAllText(Fixture($"global-percentages-{AppId}.json")))!;
        Assert.Equal(20, rarity.Count);
        Assert.Equal(47.7, rarity["Drawful: Schmuck of the Draw"], 3);
        Assert.Null(SteamAchievementService.ParseRarity("{}"));
        var (files, _) = SteamWith();
        var set = SteamAchievementService.WithRarity(new SteamAchievementService(files).Load(AppId)!, rarity);
        Assert.True(set.HasRarity);
        Assert.Equal(47.7, set.Items.Single(a => a.ApiName == "Drawful: Schmuck of the Draw").Percent!.Value, 3);
        Assert.Equal("https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v0002/?gameid=331670", SteamAchievementService.RarityUrl(AppId));
    }

    [Fact]
    public async Task Rarity_DownloadedOnceADay_KeylessThroughTheSteamWebApiPolicy()
    {
        var requests = new List<HttpRequestMessage>();
        var http = new HttpClient(new Handler(r =>
        {
            requests.Add(r);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Fixture($"global-percentages-{AppId}.json"))), RequestMessage = r };
        }));
        var now = DateTime.UtcNow;
        var service = new SteamAchievementService(new SteamLocalFiles([], []), http, Path.Combine(_root, "cache"), () => now);
        Assert.Equal(20, (await service.GetRarityAsync(AppId, CancellationToken.None))!.Count);
        Assert.Equal(20, (await service.GetRarityAsync(AppId, CancellationToken.None))!.Count);
        var request = Assert.Single(requests);
        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain("key=", request.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Icons_FromSteamsLibraryCache_BeforeAnyDownload()
    {
        var (files, steam) = SteamWith();
        var icon = Path.Combine(steam, "appcache", "librarycache", AppId.ToString(), "ffae831a9a7ff442a714526138e3684df2e753e0.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        File.WriteAllBytes(icon, [0xFF, 0xD8, 0xFF, 0xE0]);
        var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("no download expected")));
        var service = new SteamAchievementService(files, http, Path.Combine(_root, "cache"));
        Assert.Equal(icon, await service.GetIconAsync(AppId, "ffae831a9a7ff442a714526138e3684df2e753e0.jpg", CancellationToken.None));
        Assert.Null(await service.GetIconAsync(AppId, "../../etc/passwd.jpg", CancellationToken.None)); // only plain names
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    // ---- masking (upstream AchievementMaskTests) -------------------------------------------------------------------

    private static Achievement A(bool hidden, bool unlocked, string name = "Secret Boss") => new("API", name, "Beat the secret boss.", hidden, unlocked, null, null, null);

    [Fact]
    public void LockedHidden_MaskedUntilShowHidden_NoNameStaysMasked()
    {
        Assert.True(A(true, false).IsMasked(false));
        Assert.False(A(true, false).IsMasked(true));
        Assert.False(A(true, true).IsMasked(false));
        Assert.False(A(false, false).IsMasked(false));
        Assert.True(A(true, false, "").IsMasked(true));
    }

    // ---- the strip and the list --------------------------------------------------------------------------------------

    [Fact]
    public void StripAndList_UpstreamsWords_SearchSkipsMaskedDetails()
    {
        var (files, _) = SteamWith();
        var set = new SteamAchievementService(files).Load(AppId)!;
        var now = new DateTime(2023, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var strip = AchievementsPresenter.Strip(set, now);
        Assert.Equal("3 of 20 · 15%", strip.Progress);
        Assert.StartsWith("Latest: Word Spud: Spud Stud · ", strip.Latest);
        Assert.Equal(3, strip.Recent.Count);

        var rows = AchievementsPresenter.Rows(set, showHidden: false, query: null, now);
        Assert.Equal(20, rows.Count);
        var hidden = rows.Where(r => r.Masked).ToList();
        Assert.Equal(2, hidden.Count);
        Assert.All(hidden, r => Assert.Equal("Hidden achievement", r.Title));
        Assert.Empty(AchievementsPresenter.Rows(set, false, "Golden Nugget", now));                // a masked one's name is not searched
        Assert.Single(AchievementsPresenter.Rows(set, true, "Golden Nugget", now));                 // Show hidden: it is
        Assert.Equal(2, AchievementsPresenter.Rows(set, false, "drawful: s", now).Count);           // case-insensitive
        Assert.Equal("Unlocked 2 days ago", rows.First().When);
        Assert.Equal("4.2% of players", AchievementsPresenter.RarityText(4.2));
        Assert.Equal("48% of players", AchievementsPresenter.RarityText(47.7));
    }

    [Fact]
    public void Dates_TodayYesterdayDaysAgo_ThenTheDate()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("today", AchievementsPresenter.When(now, now));
        Assert.Equal("yesterday", AchievementsPresenter.When(now.AddDays(-1), now));
        Assert.Equal("3 days ago", AchievementsPresenter.When(now.AddDays(-3), now));
        Assert.Equal("1 Nov 2026", AchievementsPresenter.When(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), now));
        UiLocalizationService.Apply("pt-BR");
        Assert.Equal("ontem", AchievementsPresenter.When(now.AddDays(-1), now));
    }

    // ---- Settings › Achievements --------------------------------------------------------------------------------------

    [Fact]
    public void Settings_SteamIsOptIn_OnlySteamGamesShow_SwitchesAreSaved()
    {
        var store = new LinuxSettingsStore(Path.Combine(_root, "config", "settings.json"), Path.Combine(_root, "c"), Path.Combine(_root, "d"));
        var settings = new LinuxAppSettings();
        store.Save(settings);
        string? Save(Action<LinuxAppSettings> change) { var copy = store.Load(); change(copy); store.Save(copy); return null; }
        var (files, _) = SteamWith();
        var presenter = new AchievementsPresenter(() => settings, Save, new SteamAchievementService(files));
        var steam = new DetectedGame { Name = "Jackbox", Source = "Steam", SteamAppId = AppId };
        Assert.Null(presenter.AppIdFor(steam)); // off by default
        var changes = 0;
        presenter.Changed += () => changes++;
        Assert.Null(presenter.SetSteamOn(true));
        Assert.Null(presenter.SetShowHidden(true));
        Assert.True(store.Load().SteamAchievements && store.Load().AchievementsShowHidden);
        Assert.Equal(AppId, presenter.AppIdFor(steam));
        Assert.Null(presenter.AppIdFor(new DetectedGame { Name = "Heroic", Source = "Heroic", SteamAppId = AppId })); // Steam games only
        Assert.Null(presenter.AppIdFor(new DetectedGame { Name = "No id", Source = "Steam" }));
        Assert.Equal(2, changes);
        Assert.Null(new AchievementsPresenter(() => settings, Save, null).AppIdFor(steam)); // no reader: nothing
    }
}
