using System.Security.Cryptography;
using System.Text.Json;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #270 (upstream 2.9.2 OsReleaseService): the release list of Stable, DLSS NR and F5 for the ⚙ Release row and its
/// "Latest" line, one release by its tag for a pinned install, the 2.9.2 "always ask for a newer release" fix, and
/// "Remove old Nightly builds". The release lists are the repositories' own, as served on 2026-11-16.
/// </summary>
public sealed partial class OptiScalerPackageProviderTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("Stable", "releases-optiscaler-2026-11-16.json", "v0.9.4", "0.9.4")]
    [InlineData("DlssNr", "releases-optiscaler-dlssnr-presr-multipass-2026-11-16.json", "v0.8.10", "0.8.10")]
    [InlineData("F5", "releases-optiscaler-f5-dlssnr-multipass-2026-11-16.json", "v0.1.38", "0.1.38")]
    public async Task ListReleases_TheRealLists_NewestFirst_OnlyReleasesWithAPackage(OptiScalerVariant variant, string fixture, string newestTag, string newestVersion)
    {
        using var http = new HttpClient(new MapHandler(new() { [OptiScalerPackageProvider.ReleaseListApiUrl(variant)] = Fixture(fixture) }));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "releases-" + variant));

        var releases = await provider.ListReleasesAsync(variant);

        Assert.NotEmpty(releases);
        Assert.Equal((newestTag, newestVersion), (releases[0].Tag, releases[0].Version));
        Assert.Equal(releases.OrderByDescending(release => release.Published).Select(release => release.Tag), releases.Select(release => release.Tag));
        // DLSS NR's repository also publishes its display-filter tool: no OptiScaler package, never offered.
        Assert.DoesNotContain(releases, release => release.Tag.StartsWith("display-filter", StringComparison.Ordinal));
        if (variant == OptiScalerVariant.F5)
        {
            // Both F5 package names (#260) are listed; v0.1.28 published a second package and is left out (refused on install).
            Assert.Contains(releases, release => release.Tag == "v0.1.36");
            Assert.Contains(releases, release => release.Tag == "v0.1.37");
        }
    }

    [Fact]
    public async Task ListReleases_Stable_SkipsPrereleasesAndDrafts()
    {
        var archive = CreateArchiveBytes();
        string Release(string tag, bool prerelease, bool draft, string published) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease, published_at = published,
            assets = new[] { new { name = $"Optiscaler_{tag}.7z", browser_download_url = $"https://github.com/rhi-tests/{tag}.7z", size = archive.LongLength, digest = (string?)null } }
        });
        var list = "[" + string.Join(",", Release("v0.9.5-pre", true, false, "2026-11-10T00:00:00Z"), Release("v0.9.6", false, true, "2026-11-12T00:00:00Z"),
            Release("v0.9.4", false, false, "2026-07-18T00:00:00Z")) + "]";
        using var http = new HttpClient(new MapHandler(new() { [OptiScalerPackageProvider.ReleaseListApiUrl(OptiScalerVariant.Stable)] = list }));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "releases-stable-pre"));

        var releases = await provider.ListReleasesAsync(OptiScalerVariant.Stable);

        Assert.Equal(["v0.9.4"], releases.Select(release => release.Tag));
    }

    [Fact]
    public async Task GetRelease_AsksThatTag_NeverLatest_AndRefusesAnotherOrAnUnsafeTag()
    {
        var archive = CreateArchiveBytes();
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        string F5Release(string tag) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease = false,
            assets = new[] { new { name = $"{tag}-Optiscaler-F5.zip", browser_download_url = $"https://github.com/rhi-tests/{tag}.zip", size = archive.LongLength, digest } }
        });
        var handler = new MapHandler(new()
        {
            [OptiScalerPackageProvider.ReleaseTagApiUrl(OptiScalerVariant.F5, "v0.1.29")] = F5Release("v0.1.29"),
            ["https://github.com/rhi-tests/v0.1.29.zip"] = archive,
            [OptiScalerPackageProvider.ReleaseTagApiUrl(OptiScalerVariant.F5, "v0.1.30")] = F5Release("v0.1.31"),
        });
        using var http = new HttpClient(handler);
        var provider = new OptiScalerPackageProvider(http, new F5Extractor(forwarderIsPe: true, backend: true), Path.Combine(_root, "release-pin"));

        var package = await provider.GetReleaseAsync(OptiScalerVariant.F5, "v0.1.29");

        Assert.Equal(("0.1.29", OptiScalerVariant.F5), (package.Version, package.Variant));
        Assert.Equal(0, handler.Count(OptiScalerPackageProvider.F5ReleasesApi));
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetReleaseAsync(OptiScalerVariant.F5, "v0.1.30"));
        Assert.StartsWith(OptiScalerPackageProvider.ReleaseMismatchPrefix, mismatch.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetReleaseAsync(OptiScalerVariant.F5, "../v1"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetReleaseAsync(OptiScalerVariant.Nightly, "v1"));
    }

    // #270 (upstream 2.9.2): Nightly, DLSS NR and F5 installed an old downloaded copy when a newer release was out. RHI
    // asks GitHub for the newest release every time and keeps each release in its own cache folder, so a cached older
    // build is never taken for the latest.
    [Fact]
    public async Task GetLatest_WithAnOlderReleaseCached_AsksGitHubAndTakesTheNewerOne()
    {
        var older = CreateArchiveBytes();
        var newer = Enumerable.Reverse(CreateArchiveBytes()).ToArray();
        var map = new Dictionary<string, object>
        {
            [OptiScalerPackageProvider.NightlyReleasesApi] = "[" + NightlyRelease("nightly-20261004", "https://github.com/rhi-tests/n1004.7z", older) + "]",
            ["https://github.com/rhi-tests/n1004.7z"] = older,
            ["https://github.com/rhi-tests/n1012.7z"] = newer
        };
        using var http = new HttpClient(new MapHandler(map));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "latest-newer"));
        Assert.Equal("20261004", (await provider.GetLatestAsync(OptiScalerVariant.Nightly)).Version);

        map[OptiScalerPackageProvider.NightlyReleasesApi] = "[" + NightlyRelease("nightly-20261012", "https://github.com/rhi-tests/n1012.7z", newer) + "]";

        Assert.Equal("20261012", (await provider.GetLatestAsync(OptiScalerVariant.Nightly)).Version);
    }

    [Fact]
    public async Task RemoveOldNightlyBuilds_KeepsTheNewestAndThePinned_AndRunsAfterANightlyDownloadWhenOn()
    {
        var cache = Path.Combine(_root, "nightly-cleanup");
        foreach (var tag in new[] { "nightly-20260901", "nightly-20261004", "nightly-20261010" })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(cache, "nightly", tag)).FullName, "OptiScaler.7z"), new string('x', 1000));
        Directory.CreateDirectory(Path.Combine(cache, "nightly", "not-a-build"));
        var archive = CreateArchiveBytes();
        using var http = new HttpClient(new MapHandler(new()
        {
            [OptiScalerPackageProvider.NightlyReleasesApi] = "[" + NightlyRelease("nightly-20261012", "https://github.com/rhi-tests/n1012.7z", archive) + "]",
            ["https://github.com/rhi-tests/n1012.7z"] = archive
        }));
        IReadOnlyCollection<string>? keep = null; // off
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), cache) { NightlyCleanupKeep = () => keep };

        await provider.GetLatestAsync(OptiScalerVariant.Nightly);
        Assert.Equal(5, Directory.GetDirectories(Path.Combine(cache, "nightly")).Length);

        keep = ["20261004"];
        await provider.GetLatestAsync(OptiScalerVariant.Nightly);

        var left = Directory.GetDirectories(Path.Combine(cache, "nightly")).Select(dir => Path.GetFileName(dir)).Order().ToArray();
        Assert.Equal(["nightly-20261004", "nightly-20261012", "not-a-build"], left);
        Assert.Equal((0, 0L), provider.RemoveOldNightlyBuilds(["20261004"]));
    }
}
