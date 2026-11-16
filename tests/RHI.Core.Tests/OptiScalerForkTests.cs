using System.Security.Cryptography;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #271 (upstream 2.9.2/2.9.3 OsForkService, OsForkTests): the recommended OptiScaler forks — their releases (the
/// repositories' own lists as served on 2026-11-16), the asset upstream's ScoreAsset picks, downloads only from the fork's
/// own GitHub releases, and the OptiScaler 10 / Neural Rendering behaviour each fork gets.
/// </summary>
public sealed partial class OptiScalerPackageProviderTests
{
    [Theory]
    // Newest by publish date, as upstream: Venom's nightly of 2026-11-15 came out after v0.4.1.
    [InlineData(OptiScalerVariant.Venom, "releases-lorenzoky-optiscaler-venom-2026-11-16.json", "opti-venom-v0.4.1-nightly-20261115")]
    [InlineData(OptiScalerVariant.NrPreSrMfg, "releases-shyvortex-optiscaler-nr-presr-mfg-2026-11-16.json", "v0.9.90")]
    [InlineData(OptiScalerVariant.FeederNr, "releases-jlrouzies-fr-optiscaler-dlssnr-presr-multipass-2026-11-16.json", "v0.8.93")]
    [InlineData(OptiScalerVariant.OpenNr, "releases-matheusferreiras-opennr-optiscaler-2026-11-16.json", "v0.6.9-opennr")]
    public async Task Fork_ListsItsOwnReleases_NewestFirst(OptiScalerVariant variant, string fixture, string newest)
    {
        var fork = OptiScalerForks.Get(variant)!;
        Assert.Equal($"https://api.github.com/repos/{fork.Repository}/releases", OptiScalerPackageProvider.ReleaseListApiUrl(variant));
        using var http = new HttpClient(new MapHandler(new() { [OptiScalerPackageProvider.ReleaseListApiUrl(variant)] = Fixture(fixture) }));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "fork-list-" + variant));

        var releases = await provider.ListReleasesAsync(variant);

        Assert.Equal(newest, releases[0].Tag);
        // Venom publishes prereleases (its nightlies); upstream lists them for forks.
        if (variant == OptiScalerVariant.Venom) Assert.Contains(releases, release => release.Tag.Contains("nightly", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("OptiScaler-Venom_nightly-20261107.7z", true)]
    [InlineData("v0.1.37.2-Optiscaler-F5.zip", true)]
    [InlineData("OptiScaler-NR-v0.9.86.zip", true)]
    [InlineData("Optional-Vendor-Neutral-DLSS5-Forwarder.zip", false)]
    [InlineData("OptiScaler-Venom_nightly-20261107.7z.sha256", false)]
    [InlineData("OptiScaler-DLSSNR-Troubleshooting.md", false)]
    [InlineData("Source code.zip", false)]
    [InlineData("OptiScaler-NR-v0.8.93-SHA256SUMS.txt", false)]
    public void ScoreAsset_KeepsBuildsAndSkipsCompanions(string name, bool usable)
        => Assert.Equal(usable, OptiScalerForks.ScoreAsset(name) > 0);

    [Fact]
    public async Task Fork_Latest_TakesTheNewestRelease_ByItsTag_AndItsBuild_NotTheChecksum()
    {
        var archive = CreateArchiveBytes();
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var asset = "https://github.com/LorenzoKy/Optiscaler-Venom/releases/download/opti-venom-v0.4.1/OptiScaler-Venom_v0.4.1.7z";
        string Release(string url) => JsonSerializer.Serialize(new
        {
            tag_name = "opti-venom-v0.4.1", draft = false, prerelease = false, published_at = "2026-11-15T00:00:00Z",
            assets = new object[]
            {
                new { name = "OptiScaler-Venom_v0.4.1.7z.sha256", browser_download_url = url + ".sha256", size = 64L, digest = (string?)null },
                new { name = "OptiScaler-Venom_v0.4.1.7z", browser_download_url = url, size = archive.LongLength, digest = (string?)digest }
            }
        });
        var map = new Dictionary<string, object>
        {
            [OptiScalerPackageProvider.ReleaseListApiUrl(OptiScalerVariant.Venom)] = "[" + Release(asset) + "]",
            [OptiScalerPackageProvider.ReleaseTagApiUrl(OptiScalerVariant.Venom, "opti-venom-v0.4.1")] = Release(asset),
            [asset] = archive
        };
        using var http = new HttpClient(new MapHandler(map));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "fork-latest"));

        var package = await provider.GetLatestAsync(OptiScalerVariant.Venom);

        Assert.Equal((OptiScalerVariant.Venom, asset), (package.Variant, package.SourceUrl));

        // A release whose asset sits anywhere but the fork's own GitHub releases is refused.
        var elsewhere = "https://github.com/someone-else/OptiScaler/releases/download/x/OptiScaler-Venom_v0.4.1.7z";
        map[OptiScalerPackageProvider.ReleaseTagApiUrl(OptiScalerVariant.Venom, "opti-venom-v0.4.1")] = Release(elsewhere);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "fork-elsewhere"))
            .GetReleaseAsync(OptiScalerVariant.Venom, "opti-venom-v0.4.1"));
        Assert.StartsWith(OptiScalerPackageProvider.ForkAssetElsewherePrefix, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forks_AreOptiScaler10Builds_TheNrOnesOwnTheNrRuntime_AndTheyShareTheNightlyTemplate()
    {
        Assert.All(OptiScalerForks.All, fork => Assert.True(OptiScalerForks.IsOs10(fork.Variant)));
        Assert.False(OptiScalerForks.IsOs10(OptiScalerVariant.Stable));
        Assert.True(NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(OptiScalerVariant.NrPreSrMfg));
        Assert.True(NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(OptiScalerVariant.FeederNr));
        Assert.False(NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(OptiScalerVariant.Venom));
        Assert.False(NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(OptiScalerVariant.OpenNr)); // ships its own runtime
        var choice = new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true);
        Assert.All(OptiScalerForks.All, fork => Assert.Equal(OptiScalerIniTemplates.FileName(OptiScalerVariant.Nightly, choice), OptiScalerIniTemplates.FileName(fork.Variant, choice)));
        Assert.Equal(OptiScalerIniTemplates.Shipped(OptiScalerVariant.Nightly, choice), OptiScalerIniTemplates.Shipped(OptiScalerVariant.Venom, choice));
    }
}

public sealed partial class OptiScalerDeploymentServiceTests
{
    // #271: an NR fork gets NVIDIA's nvngx_dlssnr.dll (no forwarder needed), the Nightly-family ⚙ rows and OptiPatcher in OptiScaler/plugins.
    [Fact]
    public async Task InstallNrFork_AddsTheNrRuntime_GetsTheStreamlineRows_AndIsLabelledByItsName()
    {
        var (game, store) = CreateGame("fork-nr", GraphicsApi.DirectX12);
        var provider = new FakePackageProvider(CreatePackage("fork-nr-package") with { Version = "0.9.90" });
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider, NrRuntime("fork-nr"));

        var installed = await service.InstallAsync(game, OptiScalerVariant.NrPreSrMfg);

        Assert.True(installed.Success, installed.Message);
        Assert.Contains("NR PreSR MFG", installed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll")));
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(OptiScalerVariant.NrPreSrMfg, OptiScalerDeploymentService.GetInstalledVariant(record));
        Assert.Equal("310.8.2", record.Metadata[OptiScalerDeploymentService.DlssNrRuntimeVersionMetadataKey]);
        Assert.Contains(service.GetAdvancedSettings(game).Settings, setting => setting.Id == OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId);
        Assert.Equal("OptiScaler NR PreSR MFG 0.9.90 is current.", (await service.CheckUpdateAsync(game)).Message);
    }
}
