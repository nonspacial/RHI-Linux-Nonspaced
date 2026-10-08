using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #27 (DEP-08): the ReShade nightly is identified by its GitHub Actions run and checked against the SHA-256 digest
/// GitHub publishes for the artifact, never by the zip's size; the setup exe stays on reshade.me's host policy.
/// </summary>
public sealed class ReShadeNightlyIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nightly-integrity-" + Guid.NewGuid().ToString("N"));

    public ReShadeNightlyIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public async Task EqualSizeZipsWithDifferentContent_AreStagedAgain()
    {
        // Upstream branch link (no GitHub answer): the old size marker reused the first DLL for the second zip.
        var site = new FakeSite();
        site.Bodies[ReShadeDeploymentService.Nightly64Url] = NightlyZip(0x11);
        var svc = Service(site);

        var first = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);
        var firstBytes = File.ReadAllBytes(first.DllPath);
        site.Bodies[ReShadeDeploymentService.Nightly64Url] = NightlyZip(0x22);
        Assert.Equal(site.Bodies[ReShadeDeploymentService.Nightly64Url].Length, NightlyZip(0x11).Length);
        var second = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);

        Assert.NotEqual(firstBytes, File.ReadAllBytes(second.DllPath));
        Assert.NotEqual(first.SourceIdentity, second.SourceIdentity);
        Assert.StartsWith("sha256:", second.SourceIdentity, StringComparison.Ordinal);
        Assert.Equal(VerifiedDownload.RecordedOnly, second.Download!.Integrity);
    }

    [Fact]
    public async Task RunArtifact_IsCheckedAgainstGitHubsDigest_AndReusedForTheSameRun()
    {
        var site = new FakeSite();
        var zip = NightlyZip(0x33);
        site.Publish(4242, zip, digestOf: zip);
        var svc = Service(site);

        var staged = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);
        var again = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);

        Assert.Equal(ReShadeDeploymentService.NightlyRunUrl(4242, false), staged.SourceUrl);
        Assert.Equal($"nightly-run:4242|sha256:{Hex(zip).ToLowerInvariant()}", staged.SourceIdentity, ignoreCase: true);
        Assert.True(staged.Download!.DigestVerified);
        Assert.Equal(1, site.Gets(ReShadeDeploymentService.NightlyRunUrl(4242, false)));
        Assert.Equal(staged.DllPath, again.DllPath);
    }

    [Fact]
    public async Task RunArtifactWhoseBytesDoNotMatchTheDigest_IsRefused_AndNothingIsStaged()
    {
        var site = new FakeSite();
        site.Publish(4343, NightlyZip(0x44), digestOf: NightlyZip(0x45));
        var svc = Service(site);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => svc.EnsureNightlyStagedAsync(PeArchitecture.X64));

        Assert.StartsWith(AtomicFile.ShaMismatchPrefix, error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "cache"), "*.dll", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UpdateCheck_ComparesTheRun_NotTheSize()
    {
        var site = new FakeSite();
        var zip = NightlyZip(0x55);
        site.Publish(5000, zip, digestOf: zip);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        var svc = Service(site, records);
        var game = Game();
        var staged = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Version = "Nightly",
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["ReShade.Channel"] = "Nightly", ["ReShade.SourceIdentity"] = staged.SourceIdentity }
        });

        var current = await svc.CheckUpdateAsync(game);
        site.Publish(5001, NightlyZip(0x56), digestOf: NightlyZip(0x56)); // same size, newer run
        var newer = await svc.CheckUpdateAsync(game);
        site.ApiDown = true;
        var unknown = await svc.CheckUpdateAsync(game);

        Assert.False(current.UpdateAvailable, current.Message);
        Assert.True(newer.UpdateAvailable, newer.Message);
        Assert.True(unknown.CouldNotCheck);
        Assert.False(unknown.UpdateAvailable);
        Assert.Equal(ReShadeDeploymentService.NightlyLookupFailedMessage, unknown.Message);
    }

    [Fact]
    public async Task SetupDownloadRedirectedOffReShadeMe_IsRefused()
    {
        var site = new FakeSite { RedirectTo = new Uri("https://mirror.evil.example/ReShade_Setup_6.4.1_Addon.exe") };
        site.Bodies[ReShadeDeploymentService.StableSetupUrl("6.4.1")] = CacheForeignFileRejectionTests.Pe(0x8664, 600_000, 0x71);
        var svc = Service(site);

        var error = await Assert.ThrowsAsync<UntrustedDownloadException>(() => svc.EnsureLegacyStagedAsync("6.4.1", PeArchitecture.X64));

        Assert.StartsWith(DownloadSources.ReShadeSetup.RefusalPrefix, error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "cache"), "*.exe", SearchOption.AllDirectories));
    }

    private ReShadeDeploymentService Service(FakeSite site, LinuxDeploymentRecordStore? records = null)
    {
        var http = new HttpClient(site);
        return new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(),
            records ?? new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json")), new WineDllOverrideService(),
            new ReShadeArchiveExtractor(), Path.Combine(_root, "cache"),
            new ReShadeShaderPackService(http, Path.Combine(_root, "shader-packs"), null, Path.Combine(_root, "custom-shaders")));
    }

    private DetectedGame Game()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "Game", "bin")).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        return new DetectedGame { Name = "Game", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", Source = "Steam" };
    }

    private static byte[] NightlyZip(byte marker)
    {
        var dll = CacheForeignFileRejectionTests.Pe(0x8664, 1_100_000, marker);
        Encoding.ASCII.GetBytes("ReShade crosire reshade.me").CopyTo(dll, 512);
        return DownloadedArchiveEntryTests.Zip("ReShade64.dll", dll, 0);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>nightly.link + GitHub's Actions API: runs list, run artifacts (with digest), run-pinned zip.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _gets = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);
        public bool ApiDown { get; set; }
        public Uri? RedirectTo { get; set; }

        public int Gets(string url) => _gets.GetValueOrDefault(new Uri(url).AbsoluteUri);

        public void Publish(long runId, byte[] zip, byte[] digestOf)
        {
            Bodies[ReShadeDeploymentService.NightlyRunsApiUrl] = Encoding.UTF8.GetBytes($"{{\"workflow_runs\":[{{\"id\":{runId}}}]}}");
            Bodies[ReShadeDeploymentService.NightlyArtifactsApiUrl(runId)] = Encoding.UTF8.GetBytes(
                $"{{\"artifacts\":[{{\"name\":\"ReShade (32-bit)\",\"digest\":\"sha256:{new string('0', 64)}\"}}," +
                $"{{\"name\":\"ReShade (64-bit)\",\"expired\":false,\"digest\":\"sha256:{Hex(digestOf).ToLowerInvariant()}\"}}]}}");
            Bodies[ReShadeDeploymentService.NightlyRunUrl(runId, false)] = zip;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.AbsoluteUri;
            _gets[key] = _gets.GetValueOrDefault(key) + 1;
            if (ApiDown && request.RequestUri.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = Bodies.FirstOrDefault(pair => new Uri(pair.Key).AbsoluteUri == key).Value;
            if (body is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.RequestMessage = RedirectTo is null ? request : new HttpRequestMessage(request.Method, RedirectTo);
            return Task.FromResult(response);
        }
    }
}
