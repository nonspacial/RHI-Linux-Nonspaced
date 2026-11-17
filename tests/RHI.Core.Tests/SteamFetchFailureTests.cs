using System.Net;
using System.Text;
using RHI.Platform.Linux.Achievements;
using RHI.Platform.Linux.Artwork;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review m17 and the Steam nits: only Steam's own "no art" (404, an empty store answer) hides a game's art for a
/// week; being offline or leaving Spotlight (a cancel) leaves no marker and is asked again. JSON of another shape and
/// binary KeyValues types 5 / 10 are read, not crashed on.
/// </summary>
public sealed class SteamFetchFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-steamfail-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return answer(request, cancellationToken);
        }
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Repeat((byte)0x42, 4096)];

    private static HttpResponseMessage Image(HttpRequestMessage request)
    {
        var content = new ByteArrayContent(Jpeg);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
    }

    private SteamArtworkService Art(HttpMessageHandler handler)
        => new(new HttpClient(handler), Path.Combine(_root, "cache"), Path.Combine(_root, "custom"), steamLibraryCache: (_, _) => null);

    private string Miss(int appId) => Path.Combine(_root, "cache", appId.ToString(System.Globalization.CultureInfo.InvariantCulture), "cover.jpg.miss");

    [Fact]
    public async Task Offline_LeavesNoMissMarker_AndTheNextLookAsksAgain()
    {
        var online = false;
        // Online, only the store's hashed path has the art: the second look must ask the store again.
        var handler = new Handler((request, _) => !online ? throw new HttpRequestException("Name or service not known")
            : request.RequestUri!.Host == "api.steampowered.com"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"response\":{\"store_items\":[{\"assets\":{\"asset_url_format\":\"steam/apps/20/${FILENAME}\",\"library_capsule\":\"cap.jpg\"}}]}}",
                        Encoding.UTF8, "application/json"),
                    RequestMessage = request
                })
                : Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/cap.jpg", StringComparison.Ordinal)
                    ? Image(request) : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }));
        var art = Art(handler);

        Assert.Null(await art.GetAsync(20, ArtworkKind.Cover, CancellationToken.None));
        Assert.False(File.Exists(Miss(20)));

        online = true;
        Assert.NotNull(await art.GetAsync(20, ArtworkKind.Cover, CancellationToken.None)); // the store lookup was not cached as "none" either
    }

    [Fact]
    public async Task ACancelledLookup_LeavesNoMissMarker()
    {
        using var leave = new CancellationTokenSource();
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.steampowered.com")
            {
                await leave.CancelAsync(); // Spotlight closed while the store was asked
                token.ThrowIfCancellationRequested();
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });

        Assert.Null(await Art(handler).GetAsync(21, ArtworkKind.Cover, leave.Token));
        Assert.False(File.Exists(Miss(21)));
    }

    [Fact]
    public async Task SteamsOwnNotThere_IsStillAMiss()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.steampowered.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"response\":{\"store_items\":[]}}", Encoding.UTF8, "application/json"), RequestMessage = request }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }));

        Assert.Null(await Art(handler).GetAsync(22, ArtworkKind.Cover, CancellationToken.None));
        Assert.True(File.Exists(Miss(22)));
    }

    [Theory]
    [InlineData("{\"response\":[]}")]
    [InlineData("{\"response\":{\"store_items\":[1]}}")]
    [InlineData("{\"response\":{\"store_items\":[{\"assets\":{\"asset_url_format\":7}}]}}")]
    [InlineData("[]")]
    public void StoreAssets_OfAnotherShape_AreNone(string json) => Assert.Null(SteamArtworkService.ParseAssets(json));

    [Theory]
    [InlineData("{\"achievementpercentages\":[]}")]
    [InlineData("{\"achievementpercentages\":{\"achievements\":[1]}}")]
    [InlineData("{\"achievementpercentages\":{\"achievements\":[{\"name\":5,\"percent\":1}]}}")]
    [InlineData("[]")]
    public void Rarity_OfAnotherShape_IsNone(string json) => Assert.Null(SteamAchievementService.ParseRarity(json));

    [Fact]
    public void BinaryKeyValues_ReadsWideStringsAndInt64()
    {
        var bytes = new List<byte> { 0 };
        bytes.AddRange("root\0"u8.ToArray());
        bytes.Add(5);
        bytes.AddRange("wide\0"u8.ToArray());
        bytes.AddRange(Encoding.Unicode.GetBytes("Olá\0"));
        bytes.Add(10);
        bytes.AddRange("big\0"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(-5_000_000_000L));
        bytes.Add(1);
        bytes.AddRange("after\0x\0"u8.ToArray());
        bytes.Add(8);
        bytes.Add(8);

        var root = (Dictionary<string, object>)SteamBinaryKv.Parse([.. bytes])["root"];

        Assert.Equal("Olá", root["wide"]);
        Assert.Equal(-5_000_000_000L, root["big"]);
        Assert.Equal("x", root["after"]);
    }
}
