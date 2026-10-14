using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #98 (upstream 2.8.7 <c>EnsureOptiPatcherStagingAsync</c>): OptiPatcher comes from the <c>rolling</c> release of
/// optiscaler/OptiPatcher; the build is the release notes' "Build commit"; the .asi is cached per build through the
/// stamped cache, GitHub's digest checked, and must be a 64-bit Windows DLL.
/// </summary>
public sealed class OptiPatcherProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optipatcher-" + Guid.NewGuid().ToString("N"));

    public OptiPatcherProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RealRelease() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "optipatcher-rolling.json"));

    [Fact]
    public void ParseRelease_TheRealRollingRelease_GivesTheBuildCommitTheAssetAndItsDigest()
    {
        var (version, url, digest) = OptiPatcherProvider.ParseRelease(RealRelease());

        Assert.Equal("c3f758abcd2491a950f384efbed579152145bdca", version);
        Assert.Equal("https://github.com/optiscaler/OptiPatcher/releases/download/rolling/OptiPatcher.asi", url);
        Assert.Equal("sha256:226e8db4cc7a30d4988ae200ac6cbc98afeff1e2e69bd07c6fbe2282fb545648", digest);
    }

    [Fact]
    public void ParseRelease_WithoutABuildCommit_UsesTheAssetDigest_AndWithoutTheAssetRefuses()
    {
        var noCommit = Release(body: "Rolling", digest: "sha256:ABCDEF0123456789" + new string('0', 48));
        Assert.Equal("sha256-abcdef012345", OptiPatcherProvider.ParseRelease(noCommit).Version);

        var noAsset = JsonSerializer.Serialize(new { tag_name = "rolling", body = "**Build commit:** c3f758a", assets = Array.Empty<object>() });
        Assert.Equal(OptiPatcherProvider.ReleaseIncompleteMessage, Assert.Throws<InvalidDataException>(() => OptiPatcherProvider.ParseRelease(noAsset)).Message);
    }

    [Fact]
    public async Task GetLatest_DownloadsOncePerBuild_ChecksTheDigest_AndRefusesANonWindowsFile()
    {
        var asi = Pe64(0x41);
        var build = "c3f758a";
        var digest = "sha256:" + FileHash.Sha256HexOf(asi).ToLowerInvariant();
        var downloads = 0;
        var handler = new Fake(request => request.RequestUri!.Host == "api.github.com"
            ? Json(Release($"**Build commit:** {build}", digest))
            : Bytes(asi, ref downloads));
        using var http = new HttpClient(handler);
        var provider = new OptiPatcherProvider(http, Path.Combine(_root, "cache"));

        var first = await provider.GetLatestAsync();
        var again = await provider.GetLatestAsync();

        Assert.Equal(build, first.Version);
        Assert.Equal(asi, File.ReadAllBytes(first.Download.Path));
        Assert.True(first.Download.DigestVerified);
        Assert.Equal(1, downloads);
        Assert.Equal(first.Download.Path, again.Download.Path);

        build = "4662cb0";
        asi = Pe64(0x42);
        digest = "sha256:" + FileHash.Sha256HexOf(asi).ToLowerInvariant();
        var next = await provider.GetLatestAsync();
        Assert.Equal("4662cb0", next.Version);
        Assert.Equal(2, downloads);

        build = "1a31f0d";
        digest = "sha256:" + new string('0', 64);
        await Assert.ThrowsAnyAsync<Exception>(() => provider.GetLatestAsync()); // published digest mismatch

        build = "8e5fa04";
        asi = Encoding.ASCII.GetBytes("<html>not a dll</html>");
        digest = "sha256:" + FileHash.Sha256HexOf(asi).ToLowerInvariant();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => provider.GetLatestAsync());
        Assert.Contains(OptiPatcherProvider.NotWindowsDllMessage, refused.ToString());
    }

    [Fact]
    public async Task GetLatest_AnAssetLinkOffGitHub_IsRefusedWithOptiPatchersOwnPolicy()
    {
        var json = Release("**Build commit:** c3f758a", null).Replace("https://github.com/optiscaler", "https://evil.example.org/optiscaler");
        using var http = new HttpClient(new Fake(request => request.RequestUri!.Host == "api.github.com"
            ? Json(json)
            : throw new InvalidOperationException("nothing off GitHub may be fetched")));
        var provider = new OptiPatcherProvider(http, Path.Combine(_root, "cache-evil"));

        var refused = await Assert.ThrowsAsync<UntrustedDownloadException>(() => provider.GetLatestAsync());

        Assert.StartsWith(DownloadSources.OptiPatcher.RefusalPrefix, refused.Message);
        // Pinned with a host only a wider policy allows (#98's policy is GitHub releases only).
        Assert.False(DownloadSources.OptiPatcher.IsTrusted("https://raw.githubusercontent.com/optiscaler/OptiPatcher/main/OptiPatcher.asi"));
        Assert.True(DownloadSources.OptiPatcher.IsTrusted("https://github.com/optiscaler/OptiPatcher/releases/download/rolling/OptiPatcher.asi"));
        Assert.Equal(256L * 1024 * 1024, DownloadSources.OptiPatcher.MaxBytes);
    }

    internal static string Release(string body, string? digest)
        => JsonSerializer.Serialize(new
        {
            tag_name = "rolling",
            body,
            assets = new[]
            {
                new { name = "OptiPatcher.asi", browser_download_url = "https://github.com/optiscaler/OptiPatcher/releases/download/rolling/OptiPatcher.asi", digest }
            }
        });

    internal static byte[] Pe64(byte marker)
    {
        var path = Path.Combine(Path.GetTempPath(), "rhi-optipatcher-pe-" + Guid.NewGuid().ToString("N"));
        try
        {
            PeArchitectureDetectorTests.WritePe(path, 0x8664, 64 * 1024);
            var bytes = File.ReadAllBytes(path);
            bytes[^1] = marker;
            return bytes;
        }
        finally { File.Delete(path); }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes, ref int count)
    {
        count++;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
