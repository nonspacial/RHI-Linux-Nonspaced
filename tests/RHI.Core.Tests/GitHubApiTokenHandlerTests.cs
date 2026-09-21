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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(Uri Uri, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
