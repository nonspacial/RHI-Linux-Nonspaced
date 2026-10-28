using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #245 (upstream 2.8.8 <c>20d3297</c>): a Luma mod the Luma wiki lists only on Nexus Mods (Mass Effect: Andromeda today)
/// is checked on Nexus with the connected account's key, never against a same-named GitHub build; its update downloads
/// nothing (Update All: could not be checked, never failed) and the row's "Update on Nexus" opens the Nexus page.
/// </summary>
public sealed class LumaNexusOnlyTests : IDisposable
{
    // Saved from https://github.com/Filoppi/Luma-Framework/wiki (2026-10-27), badge images removed: a Nexus-only row and
    // a GitHub mod that also links Nexus (download first), plus the same GitHub mod with the Nexus link written first.
    internal const string WikiSnippet = """
        <table>
        <tr>
        <th>Name</th>
        <th>Author</th>
        <th>Download Link</th>
        <th>Status</th>
        <th>Special Notes</th>
        <th>Features</th>
        </tr>
        <tr>
        <td>BioShock Remastered</td>
        <td>Pumbo</td>
        <td>
        <a href="https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-BioShock_Series-x32.zip"></a><br><a href="https://www.nexusmods.com/bioshock/mods/94" rel="nofollow"></a>
        </td>
        <td>✅</td>
        <td></td>
        <td></td>
        </tr>
        <tr>
        <td>Mass Effect: Andromeda</td>
        <td>DristoforColumb</td>
        <td><a href="https://www.nexusmods.com/masseffectandromeda/mods/1351" rel="nofollow"></a></td>
        <td>✅</td>
        <td></td>
        <td><a href="https://github.com/Filoppi/Luma-Framework/wiki#mass-effect-andromeda">📌</a></td>
        </tr>
        <tr>
        <td>Prey</td>
        <td>Pumbo</td>
        <td><a href="https://www.nexusmods.com/prey2017/mods/149" rel="nofollow"></a> · <a href="https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Prey.zip"></a></td>
        <td>✅</td>
        <td></td>
        <td></td>
        </tr>
        </table>
        """;

    internal const string WikiUrl = "https://github.com/rhi-tests/luma-wiki";
    internal const string AndromedaPage = "https://www.nexusmods.com/masseffectandromeda/mods/1351";
    internal const string AndromedaFiles = "https://api.nexusmods.com/v1/games/masseffectandromeda/mods/1351/files.json";
    internal const string AndromedaGitHubAsset = "https://github.com/Filoppi/Luma-Framework/releases/download/latest-700/Luma-Mass_Effect_Andromeda.zip";
    internal static readonly DateTime InstalledAt = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-nexus-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _requests = new();

    public LumaNexusOnlyTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ── Catalogue ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Wiki_NexusOnlyRow_IsItsNexusPage_WithNoDownload()
    {
        var andromeda = LumaCatalogService.ParseCompletedMods(WikiSnippet).Single(entry => entry.Name == "Mass Effect: Andromeda");

        Assert.Null(andromeda.DownloadUrl);
        Assert.Equal(AndromedaPage, andromeda.NexusUrl);
        Assert.True(andromeda.IsNexusOnly);
    }

    [Theory]
    [InlineData("BioShock Remastered", "https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-BioShock_Series-x32.zip")] // download link first
    [InlineData("Prey", "https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Prey.zip")] // Nexus link first
    public void Wiki_GitHubModThatAlsoLinksNexus_KeepsItsGitHubDownload(string name, string download)
    {
        var entry = LumaCatalogService.ParseCompletedMods(WikiSnippet).Single(item => item.Name == name);

        Assert.Equal(download, entry.DownloadUrl);
        Assert.NotNull(entry.NexusUrl);
        Assert.False(entry.IsNexusOnly);
    }

    [Fact]
    public void Merge_NeverFillsANexusOnlyRow_FromTheSameNamedGitHubBuild()
    {
        var wiki = LumaCatalogService.ParseCompletedMods(WikiSnippet)
            .Append(new LumaCatalogEntry("Linkless Game", "x", null, "✅", null, null)).ToArray();
        var releases = LumaCatalogService.ParseReleaseMods(ReleaseList("Luma-Mass_Effect_Andromeda.zip", "Luma-Linkless_Game.zip"));

        var merged = LumaCatalogService.MergeReleaseMods(wiki, releases);

        var andromeda = merged.Single(entry => entry.Name == "Mass Effect: Andromeda");
        Assert.Null(andromeda.DownloadUrl);
        Assert.True(andromeda.IsNexusOnly);
        Assert.Single(merged, entry => LumaCatalogService.NormalizeName(entry.Name) == LumaCatalogService.NormalizeName("Mass Effect Andromeda"));
        // A wiki row with no link at all still takes the release asset (#94, unchanged).
        Assert.EndsWith("Luma-Linkless_Game.zip", merged.Single(entry => entry.Name == "Linkless Game").DownloadUrl);
    }

    [Fact]
    public void NexusOnlyMod_IsNotOfferedForInstall_AndSaysWhereToGetIt()
    {
        var game = Game("Mass Effect: Andromeda");
        var andromeda = LumaCatalogService.ParseCompletedMods(WikiSnippet).Single(entry => entry.IsNexusOnly);

        Assert.False(LumaFrameworkDeploymentService.IsNamedGameEligible(game, andromeda));
        Assert.Equal(LumaFrameworkDeploymentService.NexusOnlyInstallMessage, LumaFrameworkDeploymentService.NamedEligibilityReason(game, andromeda));
    }

    // ── Update check ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_NewerFileOnNexus_IsAnUpdate_NeverTheGitHubBuild()
    {
        var (service, game, records) = Build(key: "key", files: Files((1, "MAIN", InstalledAt.AddDays(19), "1.2")));
        var before = records.Find(game, ManagedComponentKind.LumaFramework)!.Version;

        var check = await service.CheckUpdateAsync(game);

        Assert.True(check.UpdateAvailable);
        Assert.False(check.CouldNotCheck);
        Assert.Equal(LumaFrameworkDeploymentService.NexusNewerPrefix + "Luma MEA 1.2", check.Message);
        Assert.Equal("1.2", check.LatestVersion);
        Assert.Contains(AndromedaFiles, _requests);
        Assert.DoesNotContain(_requests, url => url.Contains("Mass_Effect_Andromeda", StringComparison.Ordinal)
                                                || url.Contains("/releases/latest", StringComparison.Ordinal));
        Assert.Equal(before, records.Find(game, ManagedComponentKind.LumaFramework)!.Version);
    }

    [Fact]
    public async Task Check_NoNewerMainFileOnNexus_IsCurrent()
    {
        // Older main file; newer optional/old-version files never count.
        var (service, game, _) = Build(key: "key", files: Files(
            (1, "MAIN", InstalledAt.AddDays(-3), "1.1"), (2, "OPTIONAL", InstalledAt.AddDays(5), "x"), (3, "OLD_VERSION", InstalledAt.AddDays(6), "y")));

        var check = await service.CheckUpdateAsync(game);

        Assert.False(check.UpdateAvailable);
        Assert.False(check.CouldNotCheck);
        Assert.Equal(LumaFrameworkDeploymentService.NexusCurrentMessage, check.Message);
    }

    [Fact]
    public async Task Check_WithoutANexusAccount_CouldNotCheck_PointingAtConnectNexusAccount_AndNexusIsNotAsked()
    {
        var (service, game, _) = Build(key: null, files: Files((1, "MAIN", InstalledAt.AddDays(19), "1.2")));

        var check = await service.CheckUpdateAsync(game);

        Assert.True(check.CouldNotCheck);
        Assert.False(check.UpdateAvailable);
        Assert.Equal(NexusModUpdateCheck.NoKeyMessage, check.Message);
        Assert.Contains("Connect Nexus account", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_requests, url => url.Contains("nexusmods.com", StringComparison.Ordinal));
        Assert.Contains("Conectar conta do Nexus", UiLocalizationService.PresentFor("pt-BR", check.Message), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Check_NexusError_CouldNotCheck_NeverAnUpdate(HttpStatusCode status)
    {
        var (service, game, _) = Build(key: "key", files: null, nexusStatus: status);

        var check = await service.CheckUpdateAsync(game);

        Assert.True(check.CouldNotCheck);
        Assert.False(check.UpdateAvailable);
        Assert.StartsWith(NexusModUpdateCheck.FailedPrefix, check.Message, StringComparison.Ordinal);
    }

    // Review F3: a refused key (401/403) is a blocker the user fixes in RHI: could-not-check, naming the control.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Check_RefusedKey_CouldNotCheck_NamingAccountsAndUpdates(HttpStatusCode status)
    {
        var (service, game, _) = Build(key: "revoked", files: null, nexusStatus: status);

        var check = await service.CheckUpdateAsync(game);

        Assert.True(check.CouldNotCheck);
        Assert.False(check.UpdateAvailable);
        Assert.Contains("Settings → Accounts & updates → Disconnect, then Connect Nexus account", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("revoked", check.Message, StringComparison.Ordinal);
        Assert.Contains("Configurações → Contas e atualizações → Desconectar e depois Conectar conta do Nexus",
            UiLocalizationService.PresentFor("pt-BR", check.Message), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_GitHubModThatAlsoLinksNexus_IsStillCheckedOnGitHub_AndNexusIsNotAsked()
    {
        var (service, game, _) = Build(key: "key", files: Files((1, "MAIN", InstalledAt.AddDays(19), "1.2")), gameName: "BioShock Remastered",
            x86: true);

        var check = await service.CheckUpdateAsync(game);

        Assert.False(check.CouldNotCheck, check.Message);
        Assert.True(check.UpdateAvailable, check.Message); // the record holds an older GitHub build
        Assert.Contains("latest-701", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_requests, url => url.Contains("nexusmods.com", StringComparison.Ordinal));
    }

    // ── Update ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_NexusOnly_DownloadsNothing_ChangesNothing_AndNamesThePage()
    {
        var (service, game, records) = Build(key: "key", files: Files((1, "MAIN", InstalledAt.AddDays(19), "1.2")));
        var before = JsonSerializer.Serialize(records.Find(game, ManagedComponentKind.LumaFramework));

        var result = await service.UpdateAsync(game);

        Assert.False(result.Success);
        Assert.True(result.SourceUnavailable);
        Assert.Equal(LumaFrameworkDeploymentService.NexusOnlyUpdatePrefix + AndromedaPage, result.Message);
        Assert.DoesNotContain(_requests, url => url.Contains("/releases/", StringComparison.Ordinal) && !url.EndsWith("per_page=5", StringComparison.Ordinal));
        Assert.Equal(before, JsonSerializer.Serialize(records.Find(game, ManagedComponentKind.LumaFramework)));
    }

    [Fact]
    public async Task UpdateAll_NexusOnlyWithANewerFile_CountsAsCouldNotBeChecked_NeverFailed_NeverDownloaded()
    {
        var (service, game, records) = Build(key: "key", files: Files((1, "MAIN", InstalledAt.AddDays(19), "1.2")));
        var updateAll = new LinuxUpdateAllService(records, new LumaOnlyBackend(service), new NotRunning());

        var result = await updateAll.RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

        var item = Assert.Single(result.Items);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(1, result.UncheckedCount);
        Assert.StartsWith(LumaFrameworkDeploymentService.NexusOnlyUpdatePrefix, item.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_requests, url => url.Contains("Mass_Effect_Andromeda", StringComparison.Ordinal));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    internal static string Files(params (int Id, string Category, DateTime Uploaded, string Version)[] files)
        => JsonSerializer.Serialize(new
        {
            files = files.Select(file => new
            {
                file_id = file.Id, name = "Luma MEA", version = file.Version, category_name = file.Category, size_kb = 1000,
                file_name = $"luma-{file.Id}.zip", uploaded_timestamp = new DateTimeOffset(file.Uploaded).ToUnixTimeSeconds()
            })
        });

    internal static string ReleaseList(params string[] assets) => JsonSerializer.Serialize(new[]
    {
        new
        {
            tag_name = "latest-701", draft = false,
            assets = assets.Select(name => new { name, browser_download_url = "https://github.com/Filoppi/Luma-Framework/releases/download/latest-701/" + name }).ToArray()
        }
    });

    private (LumaFrameworkDeploymentService Service, DetectedGame Game, LinuxDeploymentRecordStore Records) Build(string? key, string? files,
        HttpStatusCode nexusStatus = HttpStatusCode.OK, string gameName = "Mass Effect: Andromeda", bool x86 = false)
    {
        var http = new HttpClient(new Fake(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            _requests.Enqueue(url);
            if (url == WikiUrl) return Answer(WikiSnippet, "text/html");
            if (url == LumaCatalogService.ReleasesListApiUrl)
                return Answer(ReleaseList("Luma-Mass_Effect_Andromeda.zip", "Luma-BioShock_Series-x32.zip"), "application/json");
            if (url == "https://api.github.com/repos/Filoppi/Luma-Framework/releases/latest")
                return Answer(JsonSerializer.Serialize(new
                {
                    tag_name = "latest-701", draft = false,
                    assets = new[] { new { name = "Luma-BioShock_Series-x32.zip", browser_download_url = "https://github.com/Filoppi/Luma-Framework/releases/download/latest-701/Luma-BioShock_Series-x32.zip" } }
                }), "application/json");
            if (url == AndromedaFiles)
                return nexusStatus == HttpStatusCode.OK ? Answer(files!, "application/json") : new HttpResponseMessage(nexusStatus);
            throw new HttpRequestException("unexpected request in a test: " + url);
        }));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json"));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache"), new LumaCatalogService(http, wikiUrl: WikiUrl))
        {
            NexusUpdates = new NexusModUpdateCheck(new NexusApiClient(http, (_, _) => Task.CompletedTask), () => key)
        };
        var game = Game(gameName, x86);
        records.Upsert(InstalledRecord(game, gameName == "Mass Effect: Andromeda" ? AndromedaGitHubAsset
            : "https://github.com/Filoppi/Luma-Framework/releases/download/latest-700/Luma-BioShock_Series-x32.zip",
            gameName == "Mass Effect: Andromeda" ? "Luma-Mass_Effect_Andromeda.zip" : "Luma-BioShock_Series-x32.zip"));
        return (service, game, records);
    }

    internal static LinuxDeploymentRecord InstalledRecord(DetectedGame game, string sourceUrl, string package, string? file = null) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.LumaFramework,
        BinaryDirectory = game.BinaryDirectory!, SourceUrl = sourceUrl, Version = "latest-700", InstalledAtUtc = InstalledAt,
        Metadata = new(StringComparer.OrdinalIgnoreCase) { ["LumaFramework.Package"] = package, ["LumaFramework.Generic"] = "0" },
        Files = file is null ? [] : [new LinuxDeployedFile { Path = file, Sha256 = FileHash.Sha256Hex(file) }]
    };

    private DetectedGame Game(string name, bool x86 = false)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "game.exe");
        PeArchitectureDetectorTests.WritePe(exe, x86 ? (ushort)0x14c : (ushort)0x8664, 4096);
        return new DetectedGame
        {
            Name = name, InstallPath = dir, BinaryDirectory = dir, ExecutablePath = exe, PrefixPath = Path.Combine(dir, "pfx"),
            Source = "Steam", Runner = "Proton", GraphicsApi = GraphicsApi.DirectX11
        };
    }

    internal static HttpResponseMessage Answer(string body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType)
    };

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    // Update All over the Luma service alone (the managed service adds guards this test does not need).
    private sealed class LumaOnlyBackend(LumaFrameworkDeploymentService luma) : ILinuxManagedUpdateBackend
    {
        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles,
            CancellationToken cancellationToken = default) => luma.CheckUpdateAsync(game, cancellationToken);

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles,
            CancellationToken cancellationToken = default) => luma.UpdateAsync(game, cancellationToken);
    }
}

/// <summary>#245: the one Nexus "newer file" check (#198 reuses it): baseline, categories, cache, failures, page rule.</summary>
public sealed class NexusModUpdateCheckTests
{
    private const string Page = LumaNexusOnlyTests.AndromedaPage;
    private static readonly DateTimeOffset Installed = new(LumaNexusOnlyTests.InstalledAt);

    [Theory]
    [InlineData("https://www.nexusmods.com/masseffectandromeda/mods/1351", true)]
    [InlineData("https://nexusmods.com/masseffectandromeda/mods/1351?tab=files", true)]
    [InlineData("http://www.nexusmods.com/masseffectandromeda/mods/1351", false)]
    [InlineData("https://www.nexusmods.com.example.org/masseffectandromeda/mods/1351", false)]
    [InlineData("https://github.com/masseffectandromeda/mods/1351", false)]
    [InlineData("https://www.nexusmods.com/masseffectandromeda/1351", false)]
    [InlineData("https://www.nexusmods.com/masseffectandromeda/images/1351", false)]
    [InlineData("https://www.nexusmods.com/masseffectandromeda/mods/x", false)]
    public void ParsePage_OnlyANexusModPage(string url, bool accepted)
        => Assert.Equal(accepted, NexusModUpdateCheck.ParsePage(url) is not null);

    [Fact]
    public async Task KnownFile_IsTheBaseline_NotTheInstallTime()
    {
        // Installed long after file 1 was uploaded; file 2 came later than file 1 but before the install.
        var (check, _) = Build(LumaNexusOnlyTests.Files((1, "MAIN", Installed.UtcDateTime.AddDays(-20), "1.0"), (2, "MAIN", Installed.UtcDateTime.AddDays(-10), "1.1")));

        Assert.Equal(NexusModUpdateOutcome.Newer, (await check.CheckAsync(Page, Installed, installedFileId: 1)).Outcome);
        Assert.Equal(NexusModUpdateOutcome.Current, (await check.CheckAsync(Page, Installed, installedFileId: null)).Outcome);
        Assert.Equal(NexusModUpdateOutcome.Current, (await check.CheckAsync(Page, Installed, installedFileId: 2)).Outcome);
    }

    [Fact]
    public async Task UpdateCategoryCounts_AndAFileWithoutADate_IsCouldNotCheck()
    {
        var (update, _) = Build(LumaNexusOnlyTests.Files((1, "UPDATE", Installed.UtcDateTime.AddDays(2), "1.3")));
        Assert.Equal(NexusModUpdateOutcome.Newer, (await update.CheckAsync(Page, Installed)).Outcome);

        var (undated, _) = Build("""{"files":[{"file_id":1,"name":"x","category_name":"MAIN","file_name":"x.zip"}]}""");
        var answer = await undated.CheckAsync(Page, Installed);
        Assert.Equal(NexusModUpdateOutcome.Failed, answer.Outcome);
        Assert.Equal(NexusModUpdateCheck.FailedPrefix + NexusModUpdateCheck.NoDatedFileMessage, answer.Message);
    }

    [Fact]
    public async Task OneFileListPerModForAnHour_AFailureIsNeverKept()
    {
        var time = new ManualTime(Installed.AddDays(30));
        var fail = true;
        var (check, requests) = Build(LumaNexusOnlyTests.Files((1, "MAIN", Installed.UtcDateTime.AddDays(2), "1.3")), time, () => fail);

        Assert.Equal(NexusModUpdateOutcome.Failed, (await check.CheckAsync(Page, Installed)).Outcome);
        fail = false;
        Assert.Equal(NexusModUpdateOutcome.Newer, (await check.CheckAsync(Page, Installed)).Outcome);
        Assert.Equal(NexusModUpdateOutcome.Newer, (await check.CheckAsync(Page, Installed)).Outcome);
        Assert.Equal(2, requests.Count);
        time.Now += TimeSpan.FromMinutes(61);
        await check.CheckAsync(Page, Installed);
        Assert.Equal(3, requests.Count);
    }

    // Review F4 (R5): a file uploaded the same second as the install is the installed one, not newer.
    [Fact]
    public async Task AFileUploadedTheSecondOfTheInstall_IsCurrent()
    {
        var (check, _) = Build(LumaNexusOnlyTests.Files((1, "MAIN", Installed.UtcDateTime, "1.0")));

        Assert.Equal(NexusModUpdateOutcome.Current, (await check.CheckAsync(Page, Installed)).Outcome);
        Assert.Equal(NexusModUpdateOutcome.Newer, (await check.CheckAsync(Page, Installed.AddSeconds(-1))).Outcome);
    }

    // Review F4 (R8): an empty key is no account; Nexus is not asked.
    [Fact]
    public async Task AnEmptyKey_IsNoKey_AndNexusIsNotAsked()
    {
        var (check, requests) = Build(LumaNexusOnlyTests.Files((1, "MAIN", Installed.UtcDateTime.AddDays(2), "1.3")), key: "");

        Assert.Equal(NexusModUpdateOutcome.NoKey, (await check.CheckAsync(Page, Installed)).Outcome);
        Assert.Empty(requests);
    }

    // Review F3 at the check: 401/403 is "key refused" (named control), never a passing hiccup.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedKey_IsKeyRefused_NamingAccountsAndUpdates(HttpStatusCode status)
    {
        var (check, _) = Build("", status: () => status);

        var answer = await check.CheckAsync(Page, Installed);

        Assert.Equal("KeyRefused", answer.Outcome.ToString());
        Assert.Contains("Settings → Accounts & updates", answer.Message, StringComparison.Ordinal);
    }

    // Review F5: after a 429 (the client already retried once) nothing is asked for five minutes, for any mod (the limit is
    // the account's); meanwhile every check is could-not-check with that answer, a file list still fresh still answers.
    [Fact]
    public async Task AfterARateLimit_NexusIsLeftAloneForFiveMinutes()
    {
        var time = new ManualTime(Installed.AddDays(30));
        HttpStatusCode? status = null;
        var (check, requests) = Build(LumaNexusOnlyTests.Files((1, "MAIN", Installed.UtcDateTime.AddDays(2), "1.3")), time, status: () => status);
        Assert.Equal(NexusModUpdateOutcome.Newer, (await check.CheckAsync(Page, Installed)).Outcome); // kept for an hour
        status = HttpStatusCode.TooManyRequests;
        const string Other = "https://www.nexusmods.com/borderlands2/mods/700";

        var limited = await check.CheckAsync(Other, Installed);
        var asked = requests.Count;
        var meanwhile = await check.CheckAsync(Other, Installed);
        var kept = await check.CheckAsync(Page, Installed);
        time.Now += TimeSpan.FromMinutes(4);
        var stillMeanwhile = await check.CheckAsync(Other, Installed);

        Assert.Equal(NexusModUpdateOutcome.Failed, limited.Outcome);
        Assert.Equal((NexusModUpdateOutcome.Failed, limited.Message), (meanwhile.Outcome, meanwhile.Message));
        Assert.Equal(NexusModUpdateOutcome.Failed, stillMeanwhile.Outcome);
        Assert.Equal(NexusModUpdateOutcome.Newer, kept.Outcome);
        Assert.Equal(asked, requests.Count); // nothing asked during the back-off

        status = null;
        time.Now += TimeSpan.FromMinutes(1.5);
        Assert.Equal(NexusModUpdateOutcome.Newer, (await check.CheckAsync(Other, Installed)).Outcome);
        Assert.Equal(asked + 1, requests.Count);
    }

    // A failure that is not a rate limit is never kept: the next check asks again (correctness first: never a stale answer).
    [Fact]
    public async Task AnOrdinaryFailure_StartsNoBackOff()
    {
        HttpStatusCode? status = HttpStatusCode.ServiceUnavailable;
        var (check, requests) = Build(LumaNexusOnlyTests.Files((1, "MAIN", Installed.UtcDateTime.AddDays(2), "1.3")), status: () => status);

        Assert.Equal(NexusModUpdateOutcome.Failed, (await check.CheckAsync(Page, Installed)).Outcome);
        status = null;
        Assert.Equal(NexusModUpdateOutcome.Newer, (await check.CheckAsync(Page, Installed)).Outcome);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task NotANexusPage_IsNeverAsked()
    {
        var (check, requests) = Build(LumaNexusOnlyTests.Files((1, "MAIN", Installed.UtcDateTime.AddDays(2), "1.3")));

        var answer = await check.CheckAsync("http://www.nexusmods.com/masseffectandromeda/mods/1351", Installed);

        Assert.Equal(NexusModUpdateOutcome.Failed, answer.Outcome);
        Assert.Empty(requests);
    }

    private static (NexusModUpdateCheck Check, ConcurrentQueue<string> Requests) Build(string files, TimeProvider? time = null, Func<bool>? fail = null,
        Func<HttpStatusCode?>? status = null, string? key = "key")
    {
        var requests = new ConcurrentQueue<string>();
        var fake = new NexusApiClientTests.FakeNexus(request =>
        {
            requests.Enqueue(request.RequestUri!.AbsoluteUri);
            if (status?.Invoke() is { } answer) return new HttpResponseMessage(answer);
            return fail?.Invoke() == true ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : LumaNexusOnlyTests.Answer(files, "application/json");
        });
        return (new NexusModUpdateCheck(new NexusApiClient(new HttpClient(fake), (_, _) => Task.CompletedTask), () => key, time), requests);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
