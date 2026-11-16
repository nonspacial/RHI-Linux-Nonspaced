using System.Net;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #266 (upstream 2.9.4: "Fixed RHI saying it's up to date when a newer version is out, after GitHub's hourly request limit
/// was reached"). The GitHub API cache hands an earlier answer back when GitHub refuses (#35/#244); the app update check
/// must never read that stand-in as "up to date". It says it could not check, and names the hourly limit when GitHub said
/// so. An answer GitHub confirmed less than a minute ago (the cache's ordinary reuse) still counts.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class AppUpdateRateLimitTests : IDisposable
{
    private const string Repository = "rhi-tests/rhi";
    private const string V1 = "[{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://github.com/rhi-tests/rhi/releases/tag/v1.0.0\"}]";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-app-rl-{Guid.NewGuid():N}");
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public AppUpdateRateLimitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private GitHubConditionalCacheHandler Handler(HttpMessageHandler inner)
        => new(inner, () => _now, new GitHubApiCacheStore(Path.Combine(_root, "cache", GitHubApiCacheStore.FileName)),
            persistDelay: TimeSpan.FromHours(1));

    private async Task SaveAnAnswerFromAnEarlierLaunch()
    {
        using var first = new HttpClient(Handler(new GitHubApiCacheTests.GitHubFake(V1, "\"etag-1\"")));
        Assert.Equal(AppUpdateCheckResult.UpToDate, (await new AppUpdateCheckService(first, "1.0.0", Repository).CheckAsync()).Result);
    }

    [Fact]
    public async Task SavedAnswer_InsideGitHubsRateLimitWindow_IsNeverUpToDate_ButRateLimited()
    {
        await SaveAnAnswerFromAnEarlierLaunch();
        _now += TimeSpan.FromDays(2); // 1.1.0 may be out by now
        var github = new GitHubApiCacheTests.GitHubFake(V1, "\"etag-1\"") { Refuse = HttpStatusCode.Forbidden, QuotaResetAt = _now.AddMinutes(40) };
        using var http = new HttpClient(Handler(github));

        var first = await new AppUpdateCheckService(http, "1.0.0", Repository).CheckAsync();
        _now += TimeSpan.FromMinutes(5); // the second check is answered by the cache without asking GitHub
        var second = await new AppUpdateCheckService(http, "1.0.0", Repository).CheckAsync();

        Assert.Equal(AppUpdateCheckResult.RateLimited, first.Result);
        Assert.Equal(AppUpdateCheckResult.RateLimited, second.Result);
        Assert.Null(first.Latest);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(403)]
    public async Task AnswerConfirmedThisSession_AndThenAGitHubRefusal_CouldNotCheck(int status)
    {
        var github = new GitHubApiCacheTests.GitHubFake(V1, "\"etag-1\"");
        using var http = new HttpClient(Handler(github));
        Assert.Equal(AppUpdateCheckResult.UpToDate, (await new AppUpdateCheckService(http, "1.0.0", Repository).CheckAsync()).Result);
        _now += TimeSpan.FromHours(4);
        github.Refuse = (HttpStatusCode)status;

        var check = await new AppUpdateCheckService(http, "1.0.0", Repository).CheckAsync();

        Assert.Equal(AppUpdateCheckResult.CouldNotCheck, check.Result);
    }

    [Fact]
    public async Task RateLimitedWithNothingCached_SaysRateLimited()
    {
        var github = new GitHubApiCacheTests.GitHubFake(V1, "\"etag-1\"") { Refuse = HttpStatusCode.Forbidden, QuotaResetAt = _now.AddMinutes(40) };
        using var http = new HttpClient(Handler(github));

        var check = await new AppUpdateCheckService(http, "1.0.0", Repository).CheckAsync();

        Assert.Equal(AppUpdateCheckResult.RateLimited, check.Result);
    }

    [Fact]
    public async Task AnswerGitHubConfirmedSecondsAgo_StillCounts()
    {
        var github = new GitHubApiCacheTests.GitHubFake(V1, "\"etag-1\"");
        using var http = new HttpClient(Handler(github));
        await new AppUpdateCheckService(http, "1.0.0", Repository).CheckAsync();
        _now += TimeSpan.FromSeconds(20);

        var check = await new AppUpdateCheckService(http, "1.0.0", Repository).CheckAsync();

        Assert.Equal(AppUpdateCheckResult.UpToDate, check.Result);
        Assert.Single(github.Requests); // reused without asking, as before
    }

    [Theory]
    [InlineData("en-US", "GitHub's hourly request limit is reached, so RHI could not check for updates; this is 1.0.0. A GitHub token (Settings → Accounts & updates) raises the limit.")]
    [InlineData("pt-BR", "O limite de pedidos por hora do GitHub foi atingido, então o RHI não pôde procurar atualizações; esta é a 1.0.0. Um token do GitHub (Configurações → Contas e atualizações) aumenta o limite.")]
    public async Task Presenter_RateLimited_SaysSo_AndOffersNoReleasePage(string language, string expected)
    {
        UiLocalizationService.Apply(language);
        var github = new GitHubApiCacheTests.GitHubFake(V1, "\"etag-1\"") { Refuse = HttpStatusCode.Forbidden, QuotaResetAt = _now.AddMinutes(40) };
        using var http = new HttpClient(Handler(github));
        var presenter = new AppUpdatePresenter(new AppUpdateCheckService(http, "1.0.0", Repository),
            new AccountsPreferenceStore(Path.Combine(_root, "accounts.json")), _ => (true, null));

        var notice = await presenter.CheckAsync();

        Assert.Null(notice);
        Assert.Equal(expected, presenter.StatusText);
        Assert.Null(presenter.ReleasePageUrl);
    }
}
