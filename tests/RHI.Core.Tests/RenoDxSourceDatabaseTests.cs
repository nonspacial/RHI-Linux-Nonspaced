using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #257 (upstream 2.8.8 <c>698956d</c>): RenoDX's Source engine database (<c>RenoDXdb-source.json</c>, RankFTW/rhi-repo),
/// read as the Unity database is: live file with ETag revalidation, the last good copy, then the copy pinned at RHI 2.8.8;
/// its games join the RenoDX column of Available mods.
/// </summary>
public sealed class RenoDxSourceDatabaseTests : IDisposable
{
    /// <summary>The pinned copy (rhi-repo <c>9a9cf39</c>).</summary>
    internal const string PinnedFixture = """
        [
          {
            "Name": "Black Mesa",
            "Status": "WIP",
            "Bitness": "32",
            "Comments": null
          },
          {
            "Name": "Counter Strike: Source",
            "Status": "Done",
            "Bitness": "64",
            "Comments": "Some of the maps are LDR, so you won't see many highlights on them."
          },
          {
            "Name": "Day of Defeat: Source",
            "Status": "Done",
            "Bitness": "64",
            "Comments": null
          },
          {
            "Name": "Entropy: Zero",
            "Status": "Done",
            "Bitness": "32",
            "Comments": null
          },
          {
            "Name": "Entropy: Zero 2",
            "Status": "Done",
            "Bitness": "32",
            "Comments": null
          },
          {
            "Name": "Garry's Mod",
            "Status": "Done",
            "Bitness": "both",
            "Comments": "Installation instructions Install x32 version as usual, install Reshade for gmod.exe to get 32-bit Reshade and move it to /bin. \n Then install Reshade again for gmod_win64.exe to get 64-bit Reshade and move it to /bin/win64 folder. \n Install DXVK proxy in both folders (dxgi_dxvk.dll and reshade.ini). \n A single dxvk.conf should be in the root folder, x32 bit installation in /bin and x64 installation in /bin/win64."
          },
          {
            "Name": "Half Life 2",
            "Status": "Done",
            "Bitness": "32",
            "Comments": null
          },
          {
            "Name": "Left 4 Dead",
            "Status": "WIP",
            "Bitness": "32",
            "Comments": "Visual glitches, LDR maps."
          },
          {
            "Name": "Left 4 Dead 2",
            "Status": "Done",
            "Bitness": "32",
            "Comments": null
          },
          {
            "Name": "No More Room in Hell",
            "Status": "Done",
            "Bitness": "64",
            "Comments": null
          },
          {
            "Name": "Portal",
            "Status": "Done",
            "Bitness": "32",
            "Comments": null
          },
          {
            "Name": "Portal 2",
            "Status": "Done",
            "Bitness": "32",
            "Comments": null
          },
          {
            "Name": "Team Fortress 2",
            "Status": "Done",
            "Bitness": "64",
            "Comments": null
          }
        ]
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-source-db-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxSourceDatabaseTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Parse_ThePinnedCopy_ReadsEveryGameWithItsStatus()
    {
        var database = RenoDxSourceDatabase.Parse(Encoding.UTF8.GetBytes(PinnedFixture))!;

        Assert.Equal(13, database.Entries.Count);
        var byName = database.Entries.ToDictionary(entry => entry.Name);
        Assert.Equal("WIP", byName["Black Mesa"].Status);
        Assert.Equal("Done", byName["Half Life 2"].Status);
        Assert.Equal("Done", byName["Portal"].Status);
        Assert.Equal("Visual glitches, LDR maps.", byName["Left 4 Dead"].Comments);
        Assert.Null(RenoDxSourceDatabase.Parse("[]"u8));
        Assert.Null(RenoDxSourceDatabase.Parse("""[{ "Name": "  " }]"""u8));
    }

    [Fact]
    public void ThePinnedCopy_IsTheRhiRepoFileAtUpstream288_OnGitHubRaw()
    {
        Assert.Equal("https://raw.githubusercontent.com/RankFTW/rhi-repo/main/database/RenoDXdb-source.json", RenoDxSourceDatabaseService.LiveUrl);
        Assert.StartsWith("https://raw.githubusercontent.com/RankFTW/rhi-repo/9a9cf39e403039cbee843bd39cb69c3020bc4d08/", RenoDxSourceDatabaseService.PinnedUrl);
    }

    [Fact]
    public async Task Loader_RevalidatesWithTheETag_AndKeepsTheLastGoodCopy()
    {
        var cache = Path.Combine(_root, "source.json");
        var seenEtags = new List<string?>();
        var live = true;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(RenoDxSourceDatabaseService.LiveUrl, request.RequestUri!.AbsoluteUri);
            seenEtags.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.Tag);
            if (!live) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (request.Headers.IfNoneMatch.Count > 0) return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = Text("""[{ "Name": "Portal", "Status": "Done" }]""");
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        }));

        var first = await Service(http, cache).GetAsync();
        var second = await Service(http, cache).GetAsync();   // a new session: 304, the cached copy
        live = false;
        var offline = await Service(http, cache).GetAsync();  // live down: the last good copy

        Assert.Equal("Portal", Assert.Single(first!.Entries).Name);
        Assert.Equal("Portal", Assert.Single(second!.Entries).Name);
        Assert.Equal("Portal", Assert.Single(offline!.Entries).Name);
        Assert.Equal(new string?[] { null, "\"v1\"", "\"v1\"" }, seenEtags);
    }

    [Fact]
    public async Task Loader_UsesThePinnedCopyWhenLiveIsDown_AndRejectsNonGitHubSources()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri == RenoDxSourceDatabaseService.PinnedUrl
            ? Text(PinnedFixture)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var pinned = await new RenoDxSourceDatabaseService(http, Path.Combine(_root, "source.json"), freshness: TimeSpan.Zero).GetAsync();

        Assert.Equal(13, pinned?.Entries.Count);
        Assert.Throws<ArgumentException>(() => new RenoDxSourceDatabaseService(http, Path.Combine(_root, "x.json"), liveUrl: "http://raw.githubusercontent.com/x.json"));
        Assert.Throws<ArgumentException>(() => new RenoDxSourceDatabaseService(http, Path.Combine(_root, "x.json"), fallbackUrl: "https://example.test/x.json"));
    }

    [Fact]
    public async Task Loader_NothingReachable_IsNull()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await new RenoDxSourceDatabaseService(http, Path.Combine(_root, "none.json"), freshness: TimeSpan.Zero).GetAsync());
    }

    private static RenoDxSourceDatabaseService Service(HttpClient http, string cache)
        => new(http, cache, fallbackUrl: null, freshness: TimeSpan.Zero);

    internal static HttpResponseMessage Text(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
