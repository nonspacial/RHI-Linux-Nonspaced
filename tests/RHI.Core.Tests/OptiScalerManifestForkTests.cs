using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #271: the recommended forks come from the manifest's <c>osForks</c> (upstream 2.9.4 format, pinned at manifest commit
/// 6b21102); the built-in four are the fallback before a manifest is read and when it lists none.
/// </summary>
public sealed class OptiScalerManifestForkTests
{
    private const string ManifestUrl = "https://example.test/manifest.json";

    private static string ManifestFixture()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rhi-manifest-osForks-6b21102.json"));

    [Fact]
    public async Task Manifest_OsForks_AreReadAsUpstreamWrites_Them()
    {
        using var http = new HttpClient(new OneDocument(ManifestFixture()));
        var service = new UpstreamRhiManifestService(http, ManifestUrl, cachePath: null);

        var forks = await service.FetchOsForksAsync();

        Assert.Equal(["venom", "nr-presr-mfg", "feeder-nr", "opennr"], forks.Select(fork => fork.Id));
        // Upstream's 2.9.4 list is what RHI builds in, so a manifest that cannot be read changes nothing.
        Assert.Equal(OptiScalerForks.BuiltIn, forks);
    }

    [Fact]
    public void Registry_StartsWithTheBuiltInFour_ByLabel_AsUpstreamListsThem()
    {
        var registry = new OptiScalerForkRegistry();
        Assert.Equal(["Feeder NR", "NR PreSR MFG", "OpenNR", "Venom"], registry.All.Select(fork => fork.Label));
    }

    [Fact]
    public void ManifestForks_ReplaceTheBuiltIns_ADroppedOrDisabledForkStaysKnownToItsGames()
    {
        var registry = new OptiScalerForkRegistry();
        using var flow = OptiScalerForks.UseInThisFlow(registry);
        var changed = 0;
        registry.Changed += () => changed++;

        OptiScalerForks.SetFromManifest(OptiScalerForks.Parse(Entries(
            ("venom", new UpstreamManifestOsFork { Label = "Venom", Repo = "LorenzoKy/Optiscaler-Venom", Disabled = true }),
            ("new-fork", new UpstreamManifestOsFork { Label = "New Fork", Repo = "someone/OptiScaler-New", NeedsNrDll = true, IncludePrereleases = false }))));

        Assert.Equal(1, changed);
        Assert.Equal(["New Fork"], OptiScalerForks.All.Select(fork => fork.Label));
        Assert.Equal([.. OptiScalerVariant.BuiltIns, OptiScalerVariant.Fork("new-fork")], OptiScalerVersions.All());
        // A new fork is a version at once: NR runtime, OptiScaler 10 rows, its repository, its prerelease rule.
        Assert.True(OptiScalerVersions.NeedsNrDll(OptiScalerVariant.Fork("new-fork")));
        Assert.True(OptiScalerVersions.IsOs10(OptiScalerVariant.Fork("new-fork")));
        Assert.Equal("someone/OptiScaler-New", OptiScalerPackageProvider.ReleaseRepository(OptiScalerVariant.Fork("new-fork")));
        // Venom is disabled and the others are dropped: off the lists, but a game that has one still updates and removes it.
        Assert.Equal("Venom", OptiScalerVersions.Label(OptiScalerVariant.Fork("venom")));
        Assert.Equal("ShyVortex/OptiScaler-NR-PreSR-MFG", OptiScalerPackageProvider.ReleaseRepository(OptiScalerVariant.Fork("nr-presr-mfg")));
        Assert.True(OptiScalerVersions.IsKnown(OptiScalerVariant.Fork("feeder-nr")));
        // A manifest without the key changes nothing (upstream SetDefinitions(null)).
        OptiScalerForks.SetFromManifest([]);
        Assert.Equal(["New Fork"], OptiScalerForks.All.Select(fork => fork.Label));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Parse_SkipsWhatUpstreamSkips()
    {
        var forks = OptiScalerForks.Parse(Entries(
            ("Bad Id", new UpstreamManifestOsFork { Repo = "a/b" }),
            ("no-repo", new UpstreamManifestOsFork { Label = "No repo" }),
            ("bad-repo", new UpstreamManifestOsFork { Repo = "https://github.com/a/b" }),
            ("dot-repo", new UpstreamManifestOsFork { Repo = "a/.." }),
            ("null-entry", null),
            ("ok", new UpstreamManifestOsFork { Repo = "owner/Repo.Name_1" })));

        var ok = Assert.Single(forks);
        Assert.Equal(("ok", "ok", "owner/Repo.Name_1"), (ok.Id, ok.Label, ok.Repository)); // a missing label shows the id
    }

    [Theory]
    [InlineData("OptiScaler-Venom.7z", @"^OptiScaler-Venom", true)]
    [InlineData("Optional-Venom.7z", @"^OptiScaler-Venom", false)]
    [InlineData("OptiScaler-Venom.txt", @"^OptiScaler-Venom", false)] // the pattern never lets a non-archive through
    [InlineData("Optional-Forwarder.zip", @"Forwarder", true)] // the pattern replaces the score, as upstream
    [InlineData("OptiScaler.zip", @"(", true)] // an invalid pattern falls back to the score
    public void AssetPattern_ReplacesTheScore(string asset, string pattern, bool usable)
    {
        var fork = new OptiScalerFork("x", "X", "o/r", false, "", pattern);
        Assert.Equal(usable, OptiScalerForks.IsPackageAsset(fork, asset));
    }

    private static IEnumerable<KeyValuePair<string, UpstreamManifestOsFork?>> Entries(params (string Id, UpstreamManifestOsFork? Fork)[] entries)
        => entries.Select(entry => new KeyValuePair<string, UpstreamManifestOsFork?>(entry.Id, entry.Fork));

    private sealed class OneDocument(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
