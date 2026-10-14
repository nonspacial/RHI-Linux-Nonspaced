using System.Net;
using System.Net.Http.Headers;
using RHI.Core.Services;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Persistence;

namespace RHI.Core.Tests;

/// <summary>#163 (upstream 2.8.6 PAT field, 128bc14): paste / check / clear the GitHub token; the 0600 secret-file store.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class GitHubTokenSettingsTests : IDisposable
{
    private const string Token = "ghp_TestOnlyNotARealToken0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-gh-settings-" + Guid.NewGuid().ToString("N"));
    private string TokenPath => Path.Combine(_root, "github_api.txt");

    public GitHubTokenSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static HttpResponseMessage RateLimit(int limit, int remaining) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"resources\":{{\"core\":{{\"limit\":{limit},\"remaining\":{remaining}}}}},\"rate\":{{\"limit\":{limit}}}}}")
    };

    private (GitHubTokenSettingsService Service, Fake Http) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var fake = new Fake(respond);
        return (new GitHubTokenSettingsService(new HttpClient(fake), new SecretFileStore(TokenPath)), fake);
    }

    [Fact]
    public async Task Save_TokenGitHubAccepts_IsCheckedWithOneRateLimitCallThenWrittenPrivate()
    {
        var (service, http) = Build(_ => RateLimit(5000, 4987));

        var status = await service.SaveAsync("  " + Token + "\n");

        Assert.Equal(new GitHubTokenStatus(GitHubTokenCheck.Valid, 5000, 4987), status);
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.github.com/rate_limit", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal(Token, request.Authorization?.Parameter);
        Assert.Equal(Token, await File.ReadAllTextAsync(TokenPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenPath));
        Assert.True(service.HasSavedToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitHubTokenCheck.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, GitHubTokenCheck.CouldNotCheck)]
    [InlineData(HttpStatusCode.InternalServerError, GitHubTokenCheck.CouldNotCheck)]
    public async Task Save_TokenGitHubDoesNotAccept_KeepsTheSavedTokenUntouched(HttpStatusCode answer, GitHubTokenCheck expected)
    {
        await File.WriteAllTextAsync(TokenPath, "ghp_previous");
        File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var (service, _) = Build(_ => new HttpResponseMessage(answer));

        var status = await service.SaveAsync(Token);

        Assert.Equal(expected, status.Result);
        Assert.Equal("ghp_previous", await File.ReadAllTextAsync(TokenPath));
    }

    [Fact]
    public async Task Save_Offline_SavesNothingAndNeverLogsTheToken()
    {
        var log = new AppLog(Path.Combine(_root, "logs", "rhi.log"));
        var (service, _) = Build(_ => throw new HttpRequestException("offline test"));

        GitHubTokenStatus status;
        using (AppLog.UseInThisFlow(log))
            status = await service.SaveAsync(Token);

        Assert.Equal(GitHubTokenCheck.CouldNotCheck, status.Result);
        Assert.False(File.Exists(TokenPath));
        Assert.True(File.Exists(log.FilePath));
        Assert.DoesNotContain(Token, await File.ReadAllTextAsync(log.FilePath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ghp_two words")]
    [InlineData("ghp_tab\tinside")]
    public async Task Save_NotAToken_SendsNothingAndSavesNothing(string pasted)
    {
        var (service, http) = Build(_ => RateLimit(5000, 5000));

        var status = await service.SaveAsync(pasted);

        Assert.Equal(GitHubTokenCheck.Malformed, status.Result);
        Assert.Empty(http.Requests);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public async Task Save_TooLong_IsRefusedBeforeAnyRequest()
    {
        var (service, http) = Build(_ => RateLimit(5000, 5000));

        var status = await service.SaveAsync(new string('a', 513));

        Assert.Equal(GitHubTokenCheck.Malformed, status.Result);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Save_OverAWorldReadableFile_LeavesItPrivate()
    {
        await File.WriteAllTextAsync(TokenPath, "ghp_previous");
        File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var (service, _) = Build(_ => RateLimit(5000, 5000));

        await service.SaveAsync(Token);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenPath));
        Assert.Equal(Token, await File.ReadAllTextAsync(TokenPath));
    }

    [Fact]
    public async Task Save_ThroughASymlinkedTokenFile_ReplacesTheLinkedFileAndKeepsTheLink()
    {
        var real = Path.Combine(_root, "dotfiles", "github_api.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        await File.WriteAllTextAsync(real, "ghp_previous");
        File.CreateSymbolicLink(TokenPath, real);
        var (service, _) = Build(_ => RateLimit(5000, 5000));

        await service.SaveAsync(Token);

        Assert.NotNull(new FileInfo(TokenPath).LinkTarget);
        Assert.Equal(Token, await File.ReadAllTextAsync(real));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(real));
    }

    [Fact]
    public async Task Clear_DeletesTheFile_AndTheAppStopsSendingTheToken()
    {
        var (service, _) = Build(_ => RateLimit(5000, 5000));
        await service.SaveAsync(Token);
        var capture = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var app = new HttpClient(new GitHubApiTokenHandler(TokenPath, capture));
        await app.GetAsync("https://api.github.com/repos/rhi-tests/a/releases/latest");

        Assert.True(service.Clear());
        await app.GetAsync("https://api.github.com/repos/rhi-tests/a/releases/latest");

        Assert.False(File.Exists(TokenPath));
        Assert.False(service.HasSavedToken);
        Assert.Equal(Token, capture.Requests[0].Authorization?.Parameter); // the app picked up the saved token
        Assert.Null(capture.Requests[1].Authorization);
        Assert.False(service.Clear()); // nothing left to clear
    }

    [Fact]
    public async Task CheckSaved_SendsTheSavedToken_AndReportsNoTokenWhenNoneIsSaved()
    {
        var (service, http) = Build(_ => RateLimit(5000, 12));
        Assert.Equal(GitHubTokenCheck.NoToken, (await service.CheckSavedAsync()).Result);
        Assert.Empty(http.Requests);

        await File.WriteAllTextAsync(TokenPath, Token + "\n");
        File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var status = await service.CheckSavedAsync();

        Assert.Equal(new GitHubTokenStatus(GitHubTokenCheck.Valid, 5000, 12), status);
        Assert.Equal(Token, Assert.Single(http.Requests).Authorization?.Parameter);
    }

    [Fact]
    public async Task CheckSaved_FileOthersCanReadAndRhiCannotRestrict_IsNotSent()
    {
        await File.WriteAllTextAsync(TokenPath, Token);
        File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var fake = new Fake(_ => RateLimit(5000, 5000));
        var store = new SecretFileStore(TokenPath) { SetUnixFileMode = (_, _) => throw new UnauthorizedAccessException("not yours") };
        var service = new GitHubTokenSettingsService(new HttpClient(fake), store);

        var status = await service.CheckSavedAsync();

        Assert.Equal(GitHubTokenCheck.FileExposed, status.Result);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Check_RedirectOffApiGitHubCom_ProvesNothing()
    {
        var (service, _) = Build(request =>
        {
            var answer = RateLimit(5000, 5000);
            answer.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/rate_limit");
            return answer;
        });

        var status = await service.SaveAsync(Token);

        Assert.Equal(GitHubTokenCheck.CouldNotCheck, status.Result);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public void ReadCoreLimit_ReadsResourcesCore_ThenTheLegacyRateBlock()
    {
        Assert.Equal((5000, 4999), GitHubTokenSettingsService.ReadCoreLimit("{\"resources\":{\"core\":{\"limit\":5000,\"remaining\":4999}}}"));
        Assert.Equal((60, 1), GitHubTokenSettingsService.ReadCoreLimit("{\"rate\":{\"limit\":60,\"remaining\":1}}"));
        Assert.Equal((null, null), GitHubTokenSettingsService.ReadCoreLimit("not json"));
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((request.RequestUri!, request.Headers.Authorization));
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
