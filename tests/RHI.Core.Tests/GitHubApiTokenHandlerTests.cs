using System.Net;
using System.Net.Http.Headers;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

public sealed class GitHubApiTokenHandlerTests
{
    [Fact]
    public async Task SendAsync_AddsBearerTokenOnlyToGitHubApiRequests()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-gh-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tokenPath = Path.Combine(root, "github_api.txt");
            await File.WriteAllTextAsync(tokenPath, "github_pat_test_token\n");
            var capture = new CaptureHandler();
            using var http = new HttpClient(new GitHubApiTokenHandler(tokenPath, capture));

            await http.GetAsync("https://api.github.com/repos/RankFTW/RHI/releases/latest");
            await http.GetAsync("https://www.pcgamingwiki.com/wiki/Test");

            Assert.Equal(2, capture.Requests.Count);
            Assert.Equal("Bearer", capture.Requests[0].Authorization?.Scheme);
            Assert.Equal("github_pat_test_token", capture.Requests[0].Authorization?.Parameter);
            Assert.Null(capture.Requests[1].Authorization);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SendAsync_IgnoresWhitespaceOnlyToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-gh-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tokenPath = Path.Combine(root, "github_api.txt");
            await File.WriteAllTextAsync(tokenPath, "   \n");
            var capture = new CaptureHandler();
            using var http = new HttpClient(new GitHubApiTokenHandler(tokenPath, capture));

            await http.GetAsync("https://api.github.com/repos/RankFTW/RHI/releases/latest");

            Assert.Single(capture.Requests);
            Assert.Null(capture.Requests[0].Authorization);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SendAsync_DoesNotOverrideCallerAuthorizationHeader()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-gh-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tokenPath = Path.Combine(root, "github_api.txt");
            await File.WriteAllTextAsync(tokenPath, "github_pat_test_token");
            var capture = new CaptureHandler();
            using var http = new HttpClient(new GitHubApiTokenHandler(tokenPath, capture));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/RankFTW/RHI/releases/latest");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");

            await http.SendAsync(request);

            Assert.Single(capture.Requests);
            Assert.Equal("caller-token", capture.Requests[0].Authorization?.Parameter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string ReleaseUrl = "https://api.github.com/repos/RankFTW/RHI/releases/latest";

    [Fact]
    public async Task SendAsync_RevokedToken_RetriesAnonymouslyAndStopsSendingIt()
    {
        using var dir = new TokenDir("revoked-token");
        var capture = new CaptureHandler(RejectToken("revoked-token"));
        var handler = new GitHubApiTokenHandler(dir.TokenPath, capture);
        var rejections = 0;
        handler.TokenRejected += (_, _) => Interlocked.Increment(ref rejections);
        using var http = new HttpClient(handler);

        using var first = await http.GetAsync(ReleaseUrl);
        using var second = await http.GetAsync(ReleaseUrl);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(["revoked-token", null, null], capture.Requests.Select(r => r.Authorization?.Parameter));
        Assert.Equal(1, rejections);
        Assert.Equal("revoked-token", File.ReadAllText(dir.TokenPath)); // the user's file is left alone
    }

    [Fact]
    public async Task SendAsync_RateLimit403_KeepsToken()
    {
        using var dir = new TokenDir("good-token");
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var handler = new GitHubApiTokenHandler(dir.TokenPath, capture);
        var rejections = 0;
        handler.TokenRejected += (_, _) => rejections++;
        using var http = new HttpClient(handler);

        using var first = await http.GetAsync(ReleaseUrl);
        using var second = await http.GetAsync(ReleaseUrl);

        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(["good-token", "good-token"], capture.Requests.Select(r => r.Authorization?.Parameter));
        Assert.Equal(0, rejections);
    }

    [Fact]
    public async Task SendAsync_RewrittenTokenFile_ReEnablesToken()
    {
        using var dir = new TokenDir("revoked-token");
        var capture = new CaptureHandler(RejectToken("revoked-token"));
        var handler = new GitHubApiTokenHandler(dir.TokenPath, capture);
        var rejections = 0;
        handler.TokenRejected += (_, _) => rejections++;
        using var http = new HttpClient(handler);
        using (await http.GetAsync(ReleaseUrl)) { }

        dir.Rewrite("fresh-token");
        using (await http.GetAsync(ReleaseUrl)) { }
        dir.Rewrite("revoked-token");
        using (await http.GetAsync(ReleaseUrl)) { }

        Assert.Equal(["revoked-token", null, "fresh-token", "revoked-token", null],
            capture.Requests.Select(r => r.Authorization?.Parameter));
        Assert.Equal(2, rejections); // once per rejected file version
    }

    [Fact]
    public async Task SendAsync_ConcurrentRejections_RaiseOneNotice()
    {
        using var dir = new TokenDir("revoked-token");
        var bothSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenRequests = 0;
        var capture = new CaptureHandler(async request =>
        {
            if (request.Headers.Authorization is null)
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (Interlocked.Increment(ref tokenRequests) == 2)
                bothSent.SetResult();
            await bothSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var handler = new GitHubApiTokenHandler(dir.TokenPath, capture);
        var rejections = 0;
        handler.TokenRejected += (_, _) => Interlocked.Increment(ref rejections);
        using var http = new HttpClient(handler);

        var responses = await Task.WhenAll(http.GetAsync(ReleaseUrl), http.GetAsync(ReleaseUrl));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, rejections);
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task SendAsync_RejectedNonIdempotentRequest_IsNotReplayed()
    {
        using var dir = new TokenDir("revoked-token");
        var capture = new CaptureHandler(RejectToken("revoked-token"));
        var handler = new GitHubApiTokenHandler(dir.TokenPath, capture);
        using var http = new HttpClient(handler);

        // No body: only the method may stop the replay.
        using var post = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, ReleaseUrl));
        using var get = await http.GetAsync(ReleaseUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(["revoked-token", null], capture.Requests.Select(r => r.Authorization?.Parameter));
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> RejectToken(string token) => request =>
        new HttpResponseMessage(request.Headers.Authorization?.Parameter == token ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        };

    private sealed class TokenDir : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-gh-token-" + Guid.NewGuid().ToString("N"));
        private DateTime _stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public TokenDir(string token)
        {
            Directory.CreateDirectory(_root);
            Rewrite(token);
        }

        public string TokenPath => Path.Combine(_root, "github_api.txt");

        // Explicit, increasing mtimes: coarse timestamp resolution must not hide a rewrite.
        public void Rewrite(string token)
        {
            File.WriteAllText(TokenPath, token);
            _stamp = _stamp.AddMinutes(1);
            File.SetLastWriteTimeUtc(TokenPath, _stamp);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public CaptureHandler()
            : this(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })
        {
        }

        public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        public CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<(Uri Uri, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add((request.RequestUri!, request.Headers.Authorization));
            return _respond(request);
        }
    }
}
