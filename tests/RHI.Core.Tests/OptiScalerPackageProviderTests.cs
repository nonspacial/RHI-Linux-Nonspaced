using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class OptiScalerPackageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optiscaler-package-tests-" + Guid.NewGuid().ToString("N"));

    public OptiScalerPackageProviderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GetLatestAsync_ValidatesReleaseAssetAndReturnsExtractedPackage()
    {
        var archiveBytes = CreateArchiveBytes();
        var digest = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var assetUrl = "https://downloads.example/Optiscaler_9.9.9.7z";
        using var http = new HttpClient(new ReleaseHandler(archiveBytes, digest, assetUrl));
        var extractor = new FakeExtractor();
        var provider = new OptiScalerPackageProvider(http, extractor, Path.Combine(_root, "cache"));

        var package = await provider.GetLatestAsync();

        Assert.Equal("9.9.9", package.Version);
        Assert.Equal(assetUrl, package.SourceUrl);
        Assert.True(File.Exists(Path.Combine(package.RootDirectory, "OptiScaler.dll")));
        Assert.True(File.Exists(Path.Combine(package.RootDirectory, "OptiScaler.ini")));
        Assert.Equal(1, extractor.CallCount);

        // A second call should trust the validated cache and extracted package.
        var packageAgain = await provider.GetLatestAsync();
        Assert.Equal(package.RootDirectory, packageAgain.RootDirectory);
        Assert.Equal(1, extractor.CallCount);
    }

    [Theory]
    [InlineData(OptiScalerVariant.Nightly, OptiScalerPackageProvider.NightlyReleasesApi, "nightly-20260920", "OptiScaler-nightly.7z", "20260920")]
    [InlineData(OptiScalerVariant.DlssNr, OptiScalerPackageProvider.DlssNrReleasesApi, "v0.7.7", "OptiScaler-DLSSNR.zip", "0.7.7")]
    public async Task GetLatestAsync_VariantUsesUpstreamChannelAndPreservesVariant(
        OptiScalerVariant variant, string apiUrl, string tag, string assetName, string expectedVersion)
    {
        var archiveBytes = CreateArchiveBytes();
        var digest = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var assetUrl = $"https://downloads.example/{assetName}";
        using var http = new HttpClient(new VariantReleaseHandler(archiveBytes, digest, apiUrl, tag, assetName, assetUrl));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "variant-" + variant));

        var package = await provider.GetLatestAsync(variant);

        Assert.Equal(variant, package.Variant);
        Assert.Equal(expectedVersion, package.Version);
        Assert.Equal(assetUrl, package.SourceUrl);
    }

    [Fact]
    public async Task GetLatestAsync_RejectsArchiveWithMismatchedSha256BeforeExtraction()
    {
        var archiveBytes = CreateArchiveBytes();
        var wrongDigest = new string('0', 64);
        var assetUrl = "https://downloads.example/Optiscaler_bad.7z";
        using var http = new HttpClient(new ReleaseHandler(archiveBytes, wrongDigest, assetUrl));
        var extractor = new FakeExtractor();
        var provider = new OptiScalerPackageProvider(http, extractor, Path.Combine(_root, "bad-cache"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.GetLatestAsync());

        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, extractor.CallCount);
    }

    [Fact]
    public async Task GetLatestAsync_ExtractedTreeFromOtherArchiveBytes_IsReExtracted()
    {
        // #142 review: the extracted tree's stamp is bound to the archive's SHA-256, not just URL + tag,
        // so it cannot outlive the archive it came from even if nothing invalidated it on replacement.
        var archiveBytes = CreateArchiveBytes();
        var digest = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var assetUrl = "https://downloads.example/Optiscaler_9.9.9.7z";
        using var http = new HttpClient(new ReleaseHandler(archiveBytes, digest, assetUrl));
        var extractor = new FakeExtractor();
        var cache = Path.Combine(_root, "bound-cache");
        var provider = new OptiScalerPackageProvider(http, extractor, cache);

        await provider.GetLatestAsync();
        Assert.Equal(1, extractor.CallCount);
        var versionDir = Path.Combine(cache, "v9.9.9");
        var extractStamp = File.ReadAllText(RHI.Core.Services.CacheStamp.StampPath(Path.Combine(versionDir, "extract")));
        Assert.Contains(Convert.ToHexString(SHA256.HashData(archiveBytes)), extractStamp, StringComparison.OrdinalIgnoreCase);

        // Same URL/tag/digest stamp on the archive, but a stamp whose recorded archive hash differs
        // (as if the archive were replaced by other bytes without touching extract/).
        var archive = Path.Combine(versionDir, "Optiscaler_9.9.9.7z");
        var stampPath = RHI.Core.Services.CacheStamp.StampPath(Path.Combine(versionDir, "extract"));
        var genuineHash = Convert.ToHexString(SHA256.HashData(archiveBytes));
        File.WriteAllText(stampPath, extractStamp.Replace(genuineHash, new string('A', 64), StringComparison.OrdinalIgnoreCase));

        await provider.GetLatestAsync();
        Assert.Equal(2, extractor.CallCount);
        Assert.True(File.Exists(archive));
    }

    private static byte[] CreateArchiveBytes()
    {
        var bytes = new byte[1_200_000];
        new Random(12345).NextBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class ReleaseHandler(byte[] archiveBytes, string digest, string assetUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == OptiScalerPackageProvider.LatestReleaseApi)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    tag_name = "v9.9.9",
                    assets = new[]
                    {
                        new
                        {
                            name = "Optiscaler_9.9.9.7z",
                            browser_download_url = assetUrl,
                            size = archiveBytes.LongLength,
                            digest = "sha256:" + digest
                        }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            if (request.RequestUri?.AbsoluteUri == assetUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class VariantReleaseHandler(
        byte[] archiveBytes, string digest, string apiUrl, string tag, string assetName, string assetUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == apiUrl)
            {
                var release = new
                {
                    tag_name = tag,
                    draft = false,
                    prerelease = variantPrerelease(tag),
                    assets = new[]
                    {
                        new { name = assetName, browser_download_url = assetUrl, size = archiveBytes.LongLength, digest = "sha256:" + digest }
                    }
                };
                var payload = apiUrl == OptiScalerPackageProvider.LatestReleaseApi
                    ? JsonSerializer.Serialize(release)
                    : JsonSerializer.Serialize(new[] { release });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }
            if (request.RequestUri?.AbsoluteUri == assetUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static bool variantPrerelease(string value)
            => value.Contains("nightly", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeExtractor : IOptiScalerArchiveExtractor
    {
        public int CallCount { get; private set; }

        public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var root = Path.Combine(outputDirectory, "package");
            Directory.CreateDirectory(root);
            var dll = new byte[300_000];
            dll[0] = (byte)'M';
            dll[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("OptiScaler official package").CopyTo(dll, 512);
            File.WriteAllBytes(Path.Combine(root, "OptiScaler.dll"), dll);
            File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n");
            File.WriteAllBytes(Path.Combine(root, "nvngx.dll_dlssnr.dll"), dll);
            return Task.CompletedTask;
        }
    }
}
