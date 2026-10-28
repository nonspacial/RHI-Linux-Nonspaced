using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Detection.Xbox;
using RHI.Platform.Linux.Hdr;

namespace RHI.Core.Tests;

/// <summary>
/// #251 correction F4: the Microsoft Store HDR flag is read from the same product record RHI reads the card's name from —
/// <c>catalogue/products/&lt;StoreId&gt;.json</c> in the installer-app instance's state folder named by its breadcrumb —
/// never from beside a job list the breadcrumb puts elsewhere, and only when the record names the same product.
/// Synthetic breadcrumbs and records only; nothing is started and the app that installed the games is never asked.
/// </summary>
public sealed class HdrStoreRecordTests : IDisposable
{
    private const string Id = "9NBLGGH4R315";
    private readonly DetectionTestTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task TheStoreFlag_IsReadFromTheInstancesStateFolder_NotBesideAJobListKeptElsewhere()
    {
        var state = _tree.Dir("a", "state");
        var jobsDir = _tree.Dir("b");
        Product(state, Id, hdr: true);
        Product(jobsDir, Id, hdr: false); // a record beside the job list: not the one RHI reads
        var game = Detect(state, Path.Combine(jobsDir, "jobs.json"));

        var evidence = await Service(game.Name).ResolveAsync(game); // PCGW says false

        Assert.Equal(new HdrSupportEvidence(HdrSupportAnswer.Yes, HdrSupportSource.MicrosoftStore), evidence);
    }

    [Fact]
    public async Task ARecordBesideTheJobList_IsNeverRead()
    {
        var state = _tree.Dir("a", "state");
        var jobsDir = _tree.Dir("b");
        Product(jobsDir, Id, hdr: true); // only beside the job list: RHI never reads that folder
        var game = Detect(state, Path.Combine(jobsDir, "jobs.json"));

        // PCGW has no answer, so a Store record read from the wrong folder would decide it.
        Assert.Equal(HdrSupportAnswer.Unknown, (await Service(game.Name, "unknown").ResolveAsync(game)).Answer);
    }

    [Fact]
    public async Task ARecordNamingAnotherProduct_DoesNotCount()
    {
        var state = _tree.Dir("a", "state");
        Product(state, Id, hdr: true, productId: "9OTHERPRODUCT");
        var game = Detect(state, null);

        Assert.Equal(HdrSupportAnswer.Unknown, (await Service(game.Name, "unknown").ResolveAsync(game)).Answer);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static void Product(string dir, string id, bool hdr, string? productId = null)
    {
        var products = Directory.CreateDirectory(Path.Combine(dir, "catalogue", "products")).FullName;
        var attributes = hdr ? """[{"Name":"CapabilityHDR","ApplicablePlatforms":["Desktop","Xbox"]}]""" : """[{"Name":"XblLocalCoop","ApplicablePlatforms":null}]""";
        File.WriteAllText(Path.Combine(products, id + ".json"),
            "{\"ProductId\":\"" + (productId ?? id) + "\",\"LocalizedProperties\":[{\"Language\":\"en-us\",\"ProductTitle\":\"Forza Quest\"}],"
            + "\"Properties\":{\"Attributes\":" + attributes + "}}");
    }

    /// <summary>One installer-app title found through a schema-1 breadcrumb naming <paramref name="state"/> and the job list.</summary>
    private DetectedGame Detect(string state, string? jobs)
    {
        var payloads = _tree.Dir("payloads");
        var dir = Directory.CreateDirectory(Path.Combine(payloads, Id)).FullName;
        File.WriteAllText(Path.Combine(dir, XboxInstallerApp.OwnedMarker), JsonSerializer.Serialize(new { storeId = Id, packageTitle = "Forza Quest" }));
        File.WriteAllText(Path.Combine(dir, "MicrosoftGame.config"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Game configVersion=\"1\"><Identity Name=\"Pub.Game\" Version=\"1.0.0.0\" />" +
            "<ShellVisuals DefaultDisplayName=\"Forza Quest\" /><ExecutableList><Executable Name=\"Game.exe\" Id=\"Game\" /></ExecutableList>" +
            $"<StoreId>{Id}</StoreId></Game>");
        File.WriteAllBytes(Path.Combine(dir, "Game.exe"), [0x13, 0x37, .. Enumerable.Repeat((byte)0xA5, 4096)]);
        var crumbs = _tree.Dir("data", XboxInstallerApp.FolderName, "instances");
        _tree.File(JsonSerializer.Serialize(new
        {
            schema = 1, app = XboxInstallerApp.ProductTag, version = "0.1.0", updatedAt = "2026-10-14T12:57:59.289Z", stateDir = state,
            settingsFile = Path.Combine(state, "settings.json"), jobsFile = jobs ?? Path.Combine(state, "jobs.json"),
            payloadRoots = new[] { payloads }, environmentRoot = (string?)null, runtimeRoot = (string?)null, backupRoot = "/b",
            launch = new { argv = Array.Empty<string>(), env = new Dictionary<string, string>() },
            entryPoints = new { play = new[] { "--play", "{storeId}" }, show = new[] { "--show", "{storeId}" } }
        }), "data", XboxInstallerApp.FolderName, "instances", "355353fff774f03b.json");
        var games = XboxGameDetector.DetectIn(new XboxDiscoveryInputs(_tree.Dir("home"), [], [], crumbs, new OwningAppLocator([], [])), CancellationToken.None);
        return Assert.Single(games);
    }

    /// <summary>PCGamingWiki answers <paramref name="hdr"/> for the game (so only the Store flag can say Yes).</summary>
    private HdrSupportService Service(string name, string hdr = "false")
    {
        var wiki = new HdrSupportServiceTests.FakeWiki { Search = { [name] = name.Replace(' ', '_') }, Pages = { [name] = HdrSupportServiceTests.Wikitext(hdr) } };
        var cache = _tree.Dir("cache-" + Guid.NewGuid().ToString("N"));
        return new HdrSupportService(new PcGamingWikiGraphicsApiService(new HttpClient(wiki), Path.Combine(cache, "pcgw-api.json"), minRequestGap: TimeSpan.Zero),
            cachePath: Path.Combine(cache, "pcgw_hdr_cache.json"), overridesJson: """{"games":{}}""");
    }
}
