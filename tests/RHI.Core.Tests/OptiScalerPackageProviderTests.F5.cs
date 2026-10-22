using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #157: the F5 DLSSNR package — asset choice (the release also carries an \"Optional-Vendor-Neutral-DLSS5-Forwarder.zip\"
/// companion, upstream needed two fixes), its own download policy, the required forwarder, and the backslash entry
/// names of its Windows-made zip, which 7-Zip on Linux extracts as flat names (checked on the real 0.1.30 zip).
/// </summary>
public sealed partial class OptiScalerPackageProviderTests
{
    private static string F5Release(params (string Name, string Url, long Size, string? Digest)[] assets)
        => JsonSerializer.Serialize(new
        {
            tag_name = "v0.1.30",
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url, size = a.Size, digest = a.Digest is null ? null : "sha256:" + a.Digest })
        });

    /// <summary>Extracts like 7-Zip does on Linux: Windows names keep their backslashes in one flat file name.</summary>
    private sealed class FlatBackslashExtractor(params string[] names) : IOptiScalerArchiveExtractor
    {
        public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            foreach (var name in names)
            {
                var bytes = new byte[300_000];
                bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                // #231: a real PE header (the F5 forwarder must be a Windows DLL, not just start with MZ).
                BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
                "PE\0\0"u8.ToArray().CopyTo(bytes, 0x80);
                BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
                Encoding.ASCII.GetBytes("optiscaler/OptiScaler " + name).CopyTo(bytes, 512);
                File.WriteAllBytes(Path.Combine(outputDirectory, name), name.EndsWith(".ini", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes("[Plugins]\n") : bytes);
            }
            return Task.CompletedTask;
        }
    }

    private static readonly string[] F5Entries =
        ["OptiScaler.dll", "OptiScaler.ini", "nvngx.dll_dlssnr.dll", @"OptiScaler\libxess.dll", @"OptiScaler\D3D12_OptiScaler\D3D12Core.dll", @"docs\NR-COMPATIBILITY.md"];

    [Fact]
    public async Task GetLatestF5_TakesTheOptiScalerZip_NotTheOptionalCompanion_AndRebuildsItsFolders()
    {
        var archive = CreateArchiveBytes();
        var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var companion = new byte[2_000_000];
        using var http = new HttpClient(new MapHandler(new()
        {
            // The companion is listed first and is larger: neither may win.
            [OptiScalerPackageProvider.F5ReleasesApi] = F5Release(
                ("Optional-Vendor-Neutral-DLSS5-Forwarder.zip", "https://github.com/rhi-tests/companion.zip", companion.LongLength, null),
                // A companion that does say OptiScaler (upstream's first fix, StartsWith("OptiScaler"), still took such a zip).
                ("OptiScaler-Vendor-Neutral-NR-Forwarder.zip", "https://github.com/rhi-tests/companion.zip", companion.LongLength, null),
                ("OptiScaler-DLSSNR-F5-0.1.30.zip", "https://github.com/rhi-tests/f5.zip", archive.LongLength, digest)),
            ["https://github.com/rhi-tests/f5.zip"] = archive,
            ["https://github.com/rhi-tests/companion.zip"] = companion
        }));
        var provider = new OptiScalerPackageProvider(http, new FlatBackslashExtractor(F5Entries), Path.Combine(_root, "f5"));

        var package = await provider.GetLatestAsync(OptiScalerVariant.F5);

        Assert.Equal(("0.1.30", OptiScalerVariant.F5), (package.Version, package.Variant));
        Assert.Equal("https://github.com/rhi-tests/f5.zip", package.SourceUrl);
        Assert.True(File.Exists(Path.Combine(package.RootDirectory, "OptiScaler", "libxess.dll")));
        Assert.True(File.Exists(Path.Combine(package.RootDirectory, "OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll")));
        Assert.Empty(Directory.EnumerateFiles(package.RootDirectory, "*\\*"));
    }

    [Fact]
    public async Task GetLatestF5_WithoutTheForwarder_IsRefused()
    {
        var archive = CreateArchiveBytes();
        using var http = new HttpClient(new MapHandler(new()
        {
            [OptiScalerPackageProvider.F5ReleasesApi] = F5Release(("OptiScaler-DLSSNR-F5-0.1.30.zip", "https://github.com/rhi-tests/f5.zip", archive.LongLength, null)),
            ["https://github.com/rhi-tests/f5.zip"] = archive
        }));
        var provider = new OptiScalerPackageProvider(http, new FlatBackslashExtractor("OptiScaler.dll", "OptiScaler.ini"), Path.Combine(_root, "f5-noforwarder"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetLatestAsync(OptiScalerVariant.F5));
        Assert.Contains("nvngx.dll_dlssnr.dll", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Pinned with a host only a wider policy allows: F5 downloads go through their own GitHub-only policy.</summary>
    [Fact]
    public async Task GetLatestF5_FromAHostOutsideGitHubReleases_IsRefusedByTheF5Policy()
    {
        using var http = new HttpClient(new MapHandler(new()
        {
            [OptiScalerPackageProvider.F5ReleasesApi] = F5Release(("OptiScaler-DLSSNR-F5-0.1.30.zip", "https://janblade.github.io/f5.zip", 2_000_000, null))
        }));
        var provider = new OptiScalerPackageProvider(http, new FlatBackslashExtractor(F5Entries), Path.Combine(_root, "f5-host"));

        var error = await Assert.ThrowsAsync<UntrustedDownloadException>(() => provider.GetLatestAsync(OptiScalerVariant.F5));
        Assert.StartsWith(DownloadSources.OptiScalerF5.RefusalPrefix, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("Download refused: OptiScaler F5 DLSSNR links", DownloadSources.OptiScalerF5.RefusalPrefix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"..\evil.dll")]
    [InlineData(@"OptiScaler\..\..\evil.dll")]
    [InlineData(@"OptiScaler\\x.dll")]
    public void BackslashEntryLeavingOrBendingThePath_RefusesTheArchive(string name)
    {
        var dir = Path.Combine(_root, "escape-" + Math.Abs(name.GetHashCode()));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "x");

        var error = Assert.Throws<InvalidOperationException>(() => OptiScalerPackageProvider.NormalizeBackslashEntries(dir));
        Assert.StartsWith(OptiScalerPackageProvider.ArchivePathRefusedPrefix, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "evil.dll")));
    }

    [Fact]
    public void BackslashEntryCollidingWithAnother_RefusesTheArchive()
    {
        var dir = Path.Combine(_root, "collide");
        Directory.CreateDirectory(Path.Combine(dir, "OptiScaler"));
        File.WriteAllText(Path.Combine(dir, "OptiScaler", "libxess.dll"), "a");
        File.WriteAllText(Path.Combine(dir, @"OptiScaler\libxess.dll"), "b");

        Assert.Throws<InvalidOperationException>(() => OptiScalerPackageProvider.NormalizeBackslashEntries(dir));
        Assert.Equal("a", File.ReadAllText(Path.Combine(dir, "OptiScaler", "libxess.dll")));
    }
}
