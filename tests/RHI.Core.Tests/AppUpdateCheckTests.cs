using System.Net;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #100: RHI-Linux checks its own releases only and only tells the user (owner decision: never installs). Owner decision
/// 2026-10-27: no other repository is ever asked, not even when the project has published nothing.
/// </summary>
public sealed class AppUpdateCheckServiceTests
{
    private const string Project = "rhi-tests/project";

    private static string Release(string tag, bool prerelease = false, bool draft = false, string? html = null)
        => $$"""{"tag_name":"{{tag}}","name":"","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"{{html ?? $"https://github.com/{Project}/releases/tag/{tag}"}}","assets":[{"name":"RHI-Linux-{{tag}}-x86_64.AppImage","browser_download_url":"https://github.com/{{Project}}/releases/download/{{tag}}/RHI-Linux-{{tag}}-x86_64.AppImage"}]}""";

    private static (AppUpdateCheckService Service, Releases Http) Build(string current, Func<string, HttpResponseMessage> byRepo)
    {
        var http = new Releases(byRepo);
        return (new AppUpdateCheckService(new HttpClient(http), current, Project), http);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task TheShippedCheck_AsksOnlyThisProjectsRepository()
    {
        Assert.Equal("nonspacial/RHI-Linux-Nonspaced", AppUpdateCheckService.Repository);
        var http = new Releases(_ => Json("[]"));

        var check = await new AppUpdateCheckService(new HttpClient(http)).CheckAsync();

        Assert.Equal(AppUpdateCheckResult.NoReleases, check.Result);
        Assert.Equal([AppUpdateCheckService.Repository], http.Repositories);
    }

    [Fact]
    public async Task NothingPublished_NoReleases_AndNoOtherRepositoryIsAsked()
    {
        var (service, http) = Build("2.8.7", _ => Json("[]"));

        var check = await service.CheckAsync();

        Assert.Equal(AppUpdateCheckResult.NoReleases, check.Result);
        Assert.Null(check.Latest);
        Assert.Equal([Project], http.Repositories);
    }

    [Fact]
    public async Task RepositoryMissing404_NoReleases()
    {
        var (service, http) = Build("2.8.7", _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(AppUpdateCheckResult.NoReleases, (await service.CheckAsync()).Result);
        Assert.Equal([Project], http.Repositories);
    }

    [Fact]
    public async Task OnlyPrereleasesPublished_AStableBuildSeesNoReleases()
    {
        var (service, http) = Build("2.8.7", _ => Json($"[{Release("v2.8.8-beta.1", prerelease: true)}]"));

        Assert.Equal(AppUpdateCheckResult.NoReleases, (await service.CheckAsync()).Result);
        Assert.Equal([Project], http.Repositories);
    }

    [Fact]
    public async Task ANewerRelease_IsReportedWithItsPage()
    {
        var (service, http) = Build("0.9.23.4-beta.1", _ => Json($"[{Release("v0.9.23.4-beta.2", prerelease: true)},{Release("v0.9.23.3")}]"));

        var check = await service.CheckAsync();

        Assert.Equal(AppUpdateCheckResult.UpdateAvailable, check.Result);
        Assert.Equal("0.9.23.4-beta.2", check.Latest!.Version);
        Assert.Equal($"https://github.com/{Project}/releases/tag/v0.9.23.4-beta.2", check.Latest.PageUrl);
        Assert.Equal([Project], http.Repositories);
    }

    [Fact]
    public async Task NeverDownloadsAnything_OnlyTheReleaseListIsRead()
    {
        var (service, http) = Build("2.8.7", _ => Json($"[{Release("v2.8.8")}]"));

        await service.CheckAsync();

        Assert.All(http.Uris, uri => Assert.Equal("https://api.github.com/repos/rhi-tests/project/releases?per_page=20", uri.AbsoluteUri));
    }

    [Fact]
    public async Task Offline_CouldNotCheck()
    {
        var (service, http) = Build("2.8.7", _ => throw new HttpRequestException("offline test"));

        var check = await service.CheckAsync();

        Assert.Equal(AppUpdateCheckResult.CouldNotCheck, check.Result);
        Assert.Equal([Project], http.Repositories);
    }

    [Fact]
    public async Task StableBuild_IgnoresPrereleases_PrereleaseBuildCountsThem_DraftsNeverCount()
    {
        var list = $"[{Release("v1.1.0-beta.1", prerelease: true)},{Release("v1.2.0", draft: true)},{Release("v1.0.1")}]";
        var (stable, _) = Build("1.0.0", _ => Json(list));
        var (beta, _) = Build("1.0.0-beta.3", _ => Json(list));

        Assert.Equal("1.0.1", (await stable.CheckAsync()).Latest!.Version);
        Assert.Equal("1.1.0-beta.1", (await beta.CheckAsync()).Latest!.Version);
    }

    // The first release, 2.8.7 (level with upstream RHI 2.8.7): releases of this project alone follow as 2.8.7.1, the
    // next upstream port as 2.8.8.
    [Fact]
    public async Task Release2_8_7_SeesItsOwnReleaseAsUpToDate_AndLaterReleasesAsNewer()
    {
        var (same, _) = Build("2.8.7", _ => Json($"[{Release("v2.8.7")}]"));
        var upToDate = await same.CheckAsync();
        Assert.Equal(AppUpdateCheckResult.UpToDate, upToDate.Result);
        Assert.Equal("2.8.7", upToDate.Latest!.Version);

        foreach (var next in new[] { "2.8.7.1", "2.8.8" })
        {
            var (service, _) = Build("2.8.7", _ => Json($"[{Release("v" + next)},{Release("v2.8.7")}]"));
            var check = await service.CheckAsync();
            Assert.Equal(AppUpdateCheckResult.UpdateAvailable, check.Result);
            Assert.Equal(next, check.Latest!.Version);
        }
    }

    [Fact]
    public async Task EarlierBuilds_SeeRelease2_8_7AsNewer()
    {
        var (service, _) = Build("0.9.23.4-beta.1", _ => Json($"[{Release("v2.8.7")}]"));

        var check = await service.CheckAsync();

        Assert.Equal(AppUpdateCheckResult.UpdateAvailable, check.Result);
        Assert.Equal("2.8.7", check.Latest!.Version);
    }

    [Fact]
    public void TheRunningBuildsVersion_IsComparable()
        => Assert.NotNull(AppVersion.TryParse(RhiProductInfo.Version)); // otherwise every release would read as newer

    [Fact]
    public async Task ReleasePageOffGitHub_IsReplacedByTheTagPageOnGitHub()
    {
        var (service, _) = Build("0.1.0", _ => Json($"[{Release("v0.2.0", html: "https://evil.example/releases/v0.2.0")}]"));

        Assert.Equal($"https://github.com/{Project}/releases/tag/v0.2.0", (await service.CheckAsync()).Latest!.PageUrl);
    }

    [Theory]
    [InlineData("0.9.23.4-beta.1", "0.9.23.4-beta.2")]
    [InlineData("0.9.23.4-beta.2", "0.9.23.4-beta.10")]
    [InlineData("0.9.23.4-beta.1", "0.9.23.4")]
    [InlineData("0.9.23.4", "0.9.24-beta.1")]
    [InlineData("0.9.23", "0.9.23.1")]
    [InlineData("v0.9.23.4-alpha", "v0.9.23.4-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.1")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("0.9.25.0-beta.1", "2.8.7")]
    [InlineData("2.8.7", "2.8.7.1")]
    [InlineData("2.8.7.1", "2.8.8")]
    public void Versions_OrderAsSemVer(string older, string newer)
    {
        var a = AppVersion.TryParse(older);
        var b = AppVersion.TryParse(newer);
        Assert.True(AppVersion.Comparer.Compare(a, b) < 0, $"{older} < {newer}");
        Assert.True(AppVersion.Comparer.Compare(b, a) > 0, $"{newer} > {older}");
    }

    [Fact]
    public void Versions_ReadFromTags()
    {
        Assert.Equal("0.9.23.4-beta.1", AppVersion.Display("v0.9.23.4-beta.1"));
        Assert.Equal(0, AppVersion.Comparer.Compare(AppVersion.TryParse("RHI-Linux v0.9.23.4-beta.1"), AppVersion.TryParse("0.9.23.4-beta.1")));
        Assert.Null(AppVersion.TryParse("nightly"));
    }

    internal sealed class Releases(Func<string, HttpResponseMessage> byRepo) : HttpMessageHandler
    {
        public List<string> Repositories { get; } = [];
        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var repo = $"{segments[1]}/{segments[2]}";
            Repositories.Add(repo);
            var response = byRepo(repo);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}

/// <summary>#100 in Settings and at start-up: the notice, the start-up choice and the release page link.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class AppUpdatePresenterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-app-update-" + Guid.NewGuid().ToString("N"));

    public AppUpdatePresenterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (AppUpdatePresenter Presenter, AppUpdateCheckServiceTests.Releases Http, List<string> Opened) Build(string listJson)
    {
        var http = new AppUpdateCheckServiceTests.Releases(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson) });
        var opened = new List<string>();
        var presenter = new AppUpdatePresenter(new AppUpdateCheckService(new HttpClient(http), "0.9.23.4-beta.1", "rhi-tests/fork"),
            new AccountsPreferenceStore(Path.Combine(_root, "accounts.json")), url => { opened.Add(url); return (true, null); });
        return (presenter, http, opened);
    }

    private const string Newer = """[{"tag_name":"v0.9.24","prerelease":false,"draft":false,"html_url":"https://github.com/rhi-tests/fork/releases/tag/v0.9.24"}]""";

    [Fact]
    public async Task StartUp_NewerRelease_NotifiesOnceInTheStatusLine_AndOffersTheReleasePage()
    {
        var (presenter, _, opened) = Build(Newer);
        var notices = new List<string>();

        await presenter.NotifyAtStartupAsync(Task.CompletedTask, notices.Add);

        Assert.Equal(["RHI-Linux 0.9.24 is available (this is 0.9.23.4-beta.1) — see Settings → Accounts & updates."], notices);
        Assert.Equal("Version 0.9.24 is available (this is 0.9.23.4-beta.1).", presenter.StatusText);
        presenter.OpenReleasePage();
        Assert.Equal(["https://github.com/rhi-tests/fork/releases/tag/v0.9.24"], opened);
    }

    [Fact]
    public async Task StartUp_CheckTurnedOff_AsksNothing_AndTheChoiceIsKept()
    {
        var (presenter, http, _) = Build(Newer);
        presenter.SetCheckAtStartup(false);
        var notices = new List<string>();

        await presenter.NotifyAtStartupAsync(Task.CompletedTask, notices.Add);

        Assert.Empty(http.Uris);
        Assert.Empty(notices);
        Assert.False(new AccountsPreferenceStore(Path.Combine(_root, "accounts.json")).Load().CheckAppUpdatesAtStartup);
    }

    [Fact]
    public async Task StartUp_UpToDate_StaysQuiet_InPortugueseTheSettingsLineSaysSo()
    {
        UiLocalizationService.Apply("pt-BR");
        var (presenter, _, _) = Build("""[{"tag_name":"v0.9.23.4-beta.1","prerelease":true,"draft":false}]""");
        var notices = new List<string>();

        await presenter.NotifyAtStartupAsync(Task.CompletedTask, notices.Add);

        Assert.Empty(notices);
        Assert.Equal("Você tem a versão mais nova (0.9.23.4-beta.1).", presenter.StatusText);
        Assert.Null(presenter.ReleasePageUrl);
    }
}
