using System.Net;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #26/#42: the one cached-payload path (<see cref="TrustedDownload.EnsureVerifiedAsync"/>) and the per-source host
/// policies. A published digest is verified (a mismatch leaves nothing); a missing one is accepted (upstream verifies
/// none), stamped with its SHA-256 and flagged in rhi.log.
/// </summary>
public sealed class TrustedDownloadIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-download-integrity-" + Guid.NewGuid().ToString("N"));

    public TrustedDownloadIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private const string Url = "https://github.com/owner/repo/releases/download/v1/payload.bin";

    [Fact]
    public async Task PublishedDigest_IsVerifiedAndRecorded()
    {
        var body = Bytes(0x11);
        var handler = new Handler(_ => body);
        var entry = Path.Combine(_root, "a", "payload.bin");

        var result = await TrustedDownload.EnsureVerifiedAsync(new HttpClient(handler), Url, entry, DownloadSources.OptiScaler, "1", Hex(body).ToLowerInvariant());

        Assert.True(result.DigestVerified);
        Assert.Equal(VerifiedDownload.PublishedDigest, result.Integrity);
        Assert.Equal(Hex(body), result.Sha256);
        Assert.True(CacheStamp.IsValid(entry, Url, "1", Hex(body)));
    }

    [Fact]
    public async Task PublishedDigestMismatch_IsRefusedAndLeavesNothing()
    {
        var handler = new Handler(_ => Bytes(0x22));
        var entry = Path.Combine(_root, "b", "payload.bin");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => TrustedDownload.EnsureVerifiedAsync(
            new HttpClient(handler), Url, entry, DownloadSources.OptiScaler, "1", new string('a', 64)));

        Assert.StartsWith(AtomicFile.ShaMismatchPrefix, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(entry));
        Assert.False(File.Exists(CacheStamp.StampPath(entry)));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(entry)!));
    }

    [Fact]
    public async Task MissingDigest_FailOpen_IsAcceptedStampedAndFlaggedInTheLog()
    {
        var body = Bytes(0x44);
        var handler = new Handler(_ => body);
        var entry = Path.Combine(_root, "d", "payload.bin");
        var log = new AppLog(Path.Combine(_root, "rhi.log"));

        VerifiedDownload result;
        using (AppLog.UseInThisFlow(log))
            result = await TrustedDownload.EnsureVerifiedAsync(new HttpClient(handler), Url, entry, DownloadSources.OptiScaler, "1");

        Assert.False(result.DigestVerified);
        Assert.Equal(VerifiedDownload.RecordedOnly, result.Integrity);
        Assert.True(CacheStamp.IsValid(entry, Url, "1"));
        var text = File.ReadAllText(log.FilePath);
        Assert.Contains(TrustedDownload.UnpublishedDigestLogArea, text, StringComparison.Ordinal);
        Assert.Contains(Hex(body), text, StringComparison.Ordinal);
        Assert.Contains(Url, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StampedEntry_IsReusedWithoutRequest_AndForeignBytesAreReplaced()
    {
        var body = Bytes(0x55);
        var handler = new Handler(_ => body);
        var entry = Path.Combine(_root, "e", "payload.bin");
        var http = new HttpClient(handler);

        await TrustedDownload.EnsureVerifiedAsync(http, Url, entry, DownloadSources.OptiScaler, "1", Hex(body));
        await TrustedDownload.EnsureVerifiedAsync(http, Url, entry, DownloadSources.OptiScaler, "1", Hex(body));
        Assert.Equal(1, handler.Requests);

        var foreign = Bytes(0x66);
        File.WriteAllBytes(entry, foreign);
        var again = await TrustedDownload.EnsureVerifiedAsync(http, Url, entry, DownloadSources.OptiScaler, "1", Hex(body));
        Assert.Equal(2, handler.Requests);
        Assert.Equal(body, File.ReadAllBytes(again.Path));
    }

    [Fact]
    public async Task ValidationFailure_PlacesNothingAndStampsNothing()
    {
        var handler = new Handler(_ => Bytes(0x77));
        var entry = Path.Combine(_root, "f", "payload.bin");

        await Assert.ThrowsAsync<InvalidDataException>(() => TrustedDownload.EnsureVerifiedAsync(
            new HttpClient(handler), Url, entry, DownloadSources.OptiScaler, "1",
            validate: (_, _) => throw new InvalidDataException("not a PE file")));

        Assert.False(File.Exists(entry));
        Assert.False(File.Exists(CacheStamp.StampPath(entry)));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(entry)!));
    }

    [Fact]
    public async Task GetString_RefusesARedirectOffThePolicy_AndAnOversizedBody()
    {
        var redirect = new Handler(_ => "[]"u8.ToArray(), new Uri("https://evil.example/api.json"));
        await Assert.ThrowsAsync<UntrustedDownloadException>(() =>
            TrustedDownload.GetStringAsync(new HttpClient(redirect), "https://api.github.com/repos/a/b/releases", DownloadSources.GitHubApi));

        var huge = new Handler(_ => new byte[(int)TrustedDownload.MetadataMaxBytes + 1]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TrustedDownload.GetStringAsync(new HttpClient(huge), "https://api.github.com/repos/a/b/releases", DownloadSources.GitHubApi));
        Assert.StartsWith(TrustedDownload.SizeLimitPrefix, error.Message, StringComparison.Ordinal);
        // 16 MiB literal: a lifted cap must not move with the constant.
        Assert.Equal(16L * 1024 * 1024, TrustedDownload.MetadataMaxBytes);
    }

    [Theory]
    [InlineData("https://productionresultssa5.blob.core.windows.net/actions-results/x.zip", true)]
    [InlineData("https://productionresultssa123.blob.core.windows.net/a.zip", true)]
    [InlineData("https://nightly.link/crosire/reshade/workflows/build/main/ReShade%20(64-bit).zip", true)]
    [InlineData("https://evil.blob.core.windows.net/a.zip", false)]
    [InlineData("https://productionresultssa5.blob.core.windows.net.evil.example/a.zip", false)]
    [InlineData("https://xproductionresultssa5.blob.core.windows.net/a.zip", false)]
    [InlineData("https://productionresultssa5.blob.core.windows.net:8443/a.zip", false)]
    [InlineData("http://productionresultssa5.blob.core.windows.net/a.zip", false)]
    [InlineData("https://github.com/crosire/reshade/releases/download/v1/a.zip", false)]
    public void ReShadeNightlyPolicy_AdmitsOnlyNightlyLinkAndNumberedActionsStorage(string url, bool allowed)
        => Assert.Equal(allowed, DownloadSources.ReShadeNightly.IsTrusted(url));

    public static IEnumerable<object[]> PolicyPins()
    {
        // A host only a wider policy allows, per source: the source must refuse it with its own refusal text.
        yield return [DownloadSources.ReShadeSetup, "https://github.com/crosire/reshade/releases/download/v6/ReShade_Setup_6.5.1_Addon.exe"];
        yield return [DownloadSources.GitHubApi, "https://github.com/owner/repo/releases"];
        yield return [DownloadSources.GitHubAddonRelease, "https://pmnoxx.github.io/display_commander.addon64"];
        yield return [DownloadSources.OptiScaler, "https://raw.githubusercontent.com/optiscaler/OptiScaler/main/OptiScaler.7z"];
        yield return [DownloadSources.Luma, "https://codeload.github.com/Filoppi/Luma-Framework/zip/refs/heads/main"];
        yield return [DownloadSources.REFramework, "https://nightly.link/praydog/REFramework/workflows/dev-release/master/REFramework.zip"];
        yield return [DownloadSources.UltimateAsiLoader, "https://raw.githubusercontent.com/ThirteenAG/Ultimate-ASI-Loader/master/dinput8.dll"];
        yield return [DownloadSources.RhiRepository, "https://renodx.com/renodx-dlss.addon64"];
        yield return [DownloadSources.UniversalMfg, "https://raw.githubusercontent.com/dashdogy/RTX40MFG-Unlock/main/RTXMFG.dll"];
        yield return [DownloadSources.Dlssg2030, "https://github.com/sdli1995/dlssg_for_sm86/raw/main/version.dll"];
        yield return [DownloadSources.DgVoodoo, "https://dege.freeweb.hu/dgVoodoo2/bin/dgVoodoo2_87.zip"];
        yield return [DownloadSources.Dlss5Feeder, "https://gitlab.com/jlrouzies-fr/DLSS5-Feeder/-/archive/main.zip"];
        yield return [DownloadSources.ShaderPacks, "https://example.github.io/shaders.zip"];
    }

    [Theory]
    [MemberData(nameof(PolicyPins))]
    public void EverySource_RefusesAHostOnlyAWiderPolicyAllows_WithItsOwnRefusal(TrustedDownloadPolicy policy, string url)
    {
        var error = Assert.Throws<UntrustedDownloadException>(() => TrustedDownload.Validate(url, policy));
        Assert.Equal(policy.RefusalPrefix + url, error.Message);
    }

    [Fact]
    public void SourcePolicies_HaveDistinctRefusalTexts()
    {
        var prefixes = DownloadSources.All.Select(policy => policy.RefusalPrefix).ToArray();
        Assert.Equal(prefixes.Length, prefixes.Distinct(StringComparer.Ordinal).Count());
    }

    private static byte[] Bytes(byte marker)
    {
        var bytes = new byte[4096];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(marker + i);
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private sealed class Handler(Func<HttpRequestMessage, byte[]> body, Uri? redirectTo = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var bytes = body(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.RequestMessage = redirectTo is null ? request : new HttpRequestMessage(request.Method, redirectTo);
            return Task.FromResult(response);
        }
    }
}
