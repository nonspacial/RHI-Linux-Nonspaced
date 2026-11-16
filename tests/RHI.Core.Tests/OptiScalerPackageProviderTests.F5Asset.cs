using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #260: the F5 release renamed its package zip (<c>OptiScaler-DLSSNR-F5-*.zip</c> up to v0.1.36, <c>v0.1.37-Optiscaler-F5.zip</c>
/// since); RHI takes either shape, never a companion zip, and refuses a release with two packages instead of guessing
/// (upstream 2.9.1 <c>1f8a32e</c>). Asset lists are the real ones of janblade/OptiScaler-F5-DLSSNR-Multipass.
/// </summary>
public sealed partial class OptiScalerPackageProviderTests
{
    private static string F5Fixture()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "optiscaler-f5-release-v0.1.38.json"));

    public static TheoryData<string, string[]> RealF5Releases => new()
    {
        // v0.1.38, /releases/latest on 2026-11-16 (the fixture).
        { "v0.1.38-Optiscaler-F5.zip", ["Optional-Vendor-Neutral-DLSS5-Forwarder.zip", "OptiScaler-DLSSNR-Troubleshooting.md", "v0.1.38-Optiscaler-F5.zip"] },
        // v0.1.37.2: an F5Low companion that names F5 but is optional.
        { "v0.1.37.2-Optiscaler-F5.zip", ["Optional-F5Low-Update-1.1.0.184.zip", "Optional-Vendor-Neutral-DLSS5-Forwarder.zip", "OptiScaler-DLSSNR-Troubleshooting.md", "v0.1.37.2-Optiscaler-F5.zip"] },
        // v0.1.36 and v0.1.30: the name shape RHI took until now.
        { "OptiScaler-DLSSNR-F5-v0.1.36-with-dlss-fg.zip", ["Optional-F5Low-Update-1.1.0.119.zip", "Optional-Vendor-Neutral-DLSS5-Forwarder.zip", "OptiScaler-DLSSNR-F5-v0.1.36-with-dlss-fg.zip", "OptiScaler-DLSSNR-Troubleshooting.md"] },
        { "OptiScaler-DLSSNR-F5-0.1.30.zip", ["Optional-Vendor-Neutral-DLSS5-Forwarder.zip", "OptiScaler-DLSSNR-F5-0.1.30.zip"] },
    };

    [Theory]
    [MemberData(nameof(RealF5Releases))]
    public void F5Asset_IsThePackageZip_InEitherNameShape(string expected, string[] assets)
        => Assert.Equal(expected, OptiScalerPackageProvider.SelectF5AssetName(assets, "v0"));

    [Fact]
    public void F5Asset_ReleaseWithTwoPackages_IsRefusedNotGuessed()
    {
        // v0.1.28 really published a test build beside the package.
        string[] assets = ["Optional-Vendor-Neutral-DLSS5-Forwarder.zip", "OptiScaler-DLSSNR-F5-0.1.28.zip", "OptiScaler-DLSSNR-F5-test-0.1.28-dxvk-reframework-e0ed6c1f.zip"];

        var error = Assert.Throws<InvalidOperationException>(() => OptiScalerPackageProvider.SelectF5AssetName(assets, "v0.1.28"));
        Assert.StartsWith(OptiScalerPackageProvider.F5AmbiguousAssetPrefix, error.Message, StringComparison.Ordinal);
        Assert.Contains("OptiScaler-DLSSNR-F5-0.1.28.zip", error.Message, StringComparison.Ordinal);
        Assert.Contains("OptiScaler-DLSSNR-F5-test-0.1.28-dxvk-reframework-e0ed6c1f.zip", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Optional-Vendor-Neutral-DLSS5-Forwarder.zip")]
    [InlineData("OptiScaler-Vendor-Neutral-NR-Forwarder.zip")] // says OptiScaler, but no F5 package
    [InlineData("Optional-OptiScaler-F5-Extras.zip")]
    [InlineData("OptiScaler-F5-Forwarder.zip")]
    [InlineData("v0.1.38-Optiscaler-F5.7z")]
    [InlineData("v0.1.38-Optiscaler-F50.zip")] // F5 must be a word of its own
    [InlineData("v0.1.38-Optiscaler-F5-debug.zip")]
    public void F5Asset_CompanionsAndOtherFiles_AreNeverThePackage(string companion)
    {
        var error = Assert.Throws<InvalidOperationException>(() => OptiScalerPackageProvider.SelectF5AssetName([companion, "OptiScaler-DLSSNR-Troubleshooting.md"], "v0.1.38"));
        Assert.StartsWith(OptiScalerPackageProvider.NoRuntimeAssetPrefix, error.Message, StringComparison.Ordinal);
        Assert.Contains("v0.1.38", error.Message, StringComparison.Ordinal);
    }

    // w13 final review m4: both #260 refusals read in PT-BR (fixed text first, the release's names last).
    [Fact]
    public void F5Asset_Refusals_HavePtBrText()
    {
        var ambiguous = Assert.Throws<InvalidOperationException>(() =>
            OptiScalerPackageProvider.SelectF5AssetName(["OptiScaler-DLSSNR-F5-0.1.28.zip", "OptiScaler-DLSSNR-F5-test-0.1.28.zip"], "v0.1.28"));
        var none = Assert.Throws<InvalidOperationException>(() => OptiScalerPackageProvider.SelectF5AssetName(["notes.md"], "v0.1.38"));

        Assert.Equal("A versão do OptiScaler F5 DLSSNR publica mais de um pacote, então o RHI não escolheu nenhum: OptiScaler-DLSSNR-F5-0.1.28.zip, OptiScaler-DLSSNR-F5-test-0.1.28.zip",
            RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", ambiguous.Message));
        Assert.Equal("Esta versão do OptiScaler não publica nenhum pacote que o RHI Linux possa instalar: OptiScaler F5 v0.1.38 (.zip)",
            RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", none.Message));
    }

    [Fact]
    public async Task GetLatestF5_FromTodaysRelease_DownloadsTheRenamedZip()
    {
        var archive = CreateArchiveBytes();
        var release = JsonNode.Parse(F5Fixture())!;
        const string packageUrl = "https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/releases/download/v0.1.38/v0.1.38-Optiscaler-F5.zip";
        var package = release["assets"]!.AsArray().Single(asset => (string?)asset!["browser_download_url"] == packageUrl)!;
        package["size"] = archive.LongLength; // the real zip is 146 MB; the test serves a small archive under its name
        package["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        using var http = new HttpClient(new MapHandler(new()
        {
            [OptiScalerPackageProvider.F5ReleasesApi] = release.ToJsonString(),
            [packageUrl] = archive
        }));
        var provider = new OptiScalerPackageProvider(http, new FlatBackslashExtractor(F5Entries), Path.Combine(_root, "f5-renamed"));

        var result = await provider.GetLatestAsync(OptiScalerVariant.F5);

        Assert.Equal(("0.1.38", OptiScalerVariant.F5, packageUrl), (result.Version, result.Variant, result.SourceUrl));
        Assert.True(File.Exists(Path.Combine(result.RootDirectory, "nvngx.dll_dlssnr.dll")));
    }
}
