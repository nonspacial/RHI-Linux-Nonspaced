using System.Net;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Persistence;

namespace RHI.Core.Tests;

/// <summary>#163: what Settings → GitHub token says, in both languages, and that it never shows the token.</summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class GitHubTokenPresenterTests : IDisposable
{
    private const string Token = "ghp_PresenterTestOnly0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-gh-presenter-" + Guid.NewGuid().ToString("N"));

    public GitHubTokenPresenterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private GitHubTokenPresenter Build(HttpStatusCode answer)
        => new(new GitHubTokenSettingsService(new HttpClient(new Answer(answer)), new SecretFileStore(Path.Combine(_root, "github_api.txt"))));

    [Fact]
    public async Task Save_Accepted_SaysSavedWithTheLimit_ShowsCheckAndClear_NeverTheToken()
    {
        var presenter = Build(HttpStatusCode.OK);
        Assert.False(presenter.HasSavedToken);
        Assert.Equal("No token saved; RHI uses anonymous access (60 lookups an hour).", presenter.StatusText);

        Assert.True(await presenter.SaveAsync(Token));

        Assert.True(presenter.HasSavedToken);
        Assert.Equal("GitHub accepted the token; saved. 4999 of 5000 lookups left this hour.", presenter.StatusText);
        Assert.DoesNotContain(Token, presenter.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_Refused_InPortuguese_SaysNothingWasSaved()
    {
        UiLocalizationService.Apply("pt-BR");
        var presenter = Build(HttpStatusCode.Unauthorized);

        Assert.False(await presenter.SaveAsync(Token));

        Assert.False(presenter.HasSavedToken);
        Assert.Equal("O GitHub recusou este token (revogado, expirado ou digitado errado); nada foi salvo.", presenter.StatusText);
    }

    [Fact]
    public async Task Clear_SaysAnonymousAccess_AndHidesCheckAndClear()
    {
        var presenter = Build(HttpStatusCode.OK);
        await presenter.SaveAsync(Token);

        presenter.Clear();

        Assert.False(presenter.HasSavedToken);
        Assert.Equal("Token cleared; RHI uses anonymous access.", presenter.StatusText);
    }

    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent("{\"resources\":{\"core\":{\"limit\":5000,\"remaining\":4999}}}")
            });
    }
}
