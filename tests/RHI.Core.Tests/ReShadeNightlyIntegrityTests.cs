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

    [Theory]
    [InlineData("pull_request", "main", "evil/reshade")]    // a fork's pull request from its own branch named main
    [InlineData("pull_request", "main", "crosire/reshade")] // a pull request run, even from the repository itself
    [InlineData("push", "main", "evil/reshade")]            // built from another head repository
    [InlineData("push", "feature", "crosire/reshade")]      // another branch
    public async Task RunNotBuiltByCrosireMainPush_IsSkipped_EvenWhenListedFirst(string triggerEvent, string headBranch, string headRepository)
    {
        // Review w2-trust blocker 1: GitHub's branch filter matches head_branch, so a fork's pull request named
        // main passed it and its artifact (digest = that same untrusted zip) would have been injected.
        var site = new FakeSite();
        var good = NightlyZip(0x61);
        var forged = NightlyZip(0x62);
        site.Runs("push",
            FakeSite.Run(9999, triggerEvent, headBranch, headRepository, "2026-09-25T00:00:00Z"),
            FakeSite.Run(7000, "push", "main", "crosire/reshade", "2026-09-24T00:00:00Z"));
        site.Runs("schedule");
        site.Artifacts(9999, forged, digestOf: forged);
        site.Artifacts(7000, good, digestOf: good);
        var svc = Service(site);

        var staged = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);

        Assert.Equal(ReShadeDeploymentService.NightlyRunUrl(7000, false), staged.SourceUrl);
        Assert.StartsWith("nightly-run:7000|", staged.SourceIdentity, StringComparison.Ordinal);
        Assert.Equal(0, site.Gets(ReShadeDeploymentService.NightlyRunUrl(9999, false)));
        Assert.Equal(0, site.Gets(ReShadeDeploymentService.NightlyArtifactsApiUrl(9999)));
        // The query itself asks for push runs only (literal, so a dropped filter cannot move with the constant).
        Assert.Equal(1, site.Gets("https://api.github.com/repos/crosire/reshade/actions/workflows/build.yml/runs?branch=main&event=push&status=success&per_page=10"));
    }

    [Fact]
    public async Task OnlyForeignRuns_FallBackToTheBranchLink_NotTheForeignRun()
    {
        var site = new FakeSite();
        var forged = NightlyZip(0x63);
        site.Runs("push", FakeSite.Run(9998, "pull_request", "main", "evil/reshade", "2026-09-25T00:00:00Z"));
        site.Runs("schedule");
        site.Artifacts(9998, forged, digestOf: forged);
        site.Bodies[ReShadeDeploymentService.Nightly64Url] = NightlyZip(0x64);
        var svc = Service(site);

        var staged = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);

        Assert.Equal(ReShadeDeploymentService.Nightly64Url, staged.SourceUrl);
        Assert.Equal(VerifiedDownload.RecordedOnly, staged.Download!.Integrity);
        Assert.Equal(0, site.Gets(ReShadeDeploymentService.NightlyRunUrl(9998, false)));
    }

    [Fact]
    public async Task NewerScheduledRun_WinsOverAnOlderPushRun_AsNightlyLinkChooses()
    {
        var site = new FakeSite();
        var pushed = NightlyZip(0x65);
        var scheduled = NightlyZip(0x66);
        site.Runs("push", FakeSite.Run(8000, "push", "main", "crosire/reshade", "2026-09-01T10:00:00Z"));
        site.Runs("schedule", FakeSite.Run(8001, "schedule", "main", "crosire/reshade", "2026-09-02T10:00:00Z"));
        site.Artifacts(8000, pushed, digestOf: pushed);
        site.Artifacts(8001, scheduled, digestOf: scheduled);
        var svc = Service(site);

        var staged = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);

        Assert.Equal(ReShadeDeploymentService.NightlyRunUrl(8001, false), staged.SourceUrl);

        // A newer push beats the scheduled run again.
        site.Runs("push", FakeSite.Run(8002, "push", "main", "crosire/reshade", "2026-09-03T10:00:00Z"));
        site.Artifacts(8002, pushed, digestOf: pushed);
        var later = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);
        Assert.Equal(ReShadeDeploymentService.NightlyRunUrl(8002, false), later.SourceUrl);
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
            Runs("push", Run(runId, "push", "main", ReShadeDeploymentService.NightlyRepository, "2026-09-24T11:31:39Z"));
            Runs("schedule");
            Artifacts(runId, zip, digestOf);
        }

        public void Runs(string triggerEvent, params string[] runs)
            => Bodies[ReShadeDeploymentService.NightlyRunsApiUrl(triggerEvent)] = Encoding.UTF8.GetBytes($"{{\"workflow_runs\":[{string.Join(',', runs)}]}}");

        public void Artifacts(long runId, byte[] zip, byte[] digestOf)
        {
            Bodies[ReShadeDeploymentService.NightlyArtifactsApiUrl(runId)] = Encoding.UTF8.GetBytes(
                $"{{\"artifacts\":[{{\"name\":\"ReShade (32-bit)\",\"digest\":\"sha256:{new string('0', 64)}\"}}," +
                $"{{\"name\":\"ReShade (64-bit)\",\"expired\":false,\"digest\":\"sha256:{Hex(digestOf).ToLowerInvariant()}\"}}]}}");
            Bodies[ReShadeDeploymentService.NightlyRunUrl(runId, false)] = zip;
        }

        /// <summary>One run as GitHub's workflow-runs API lists it (the fields the lookup reads).</summary>
        public static string Run(long id, string triggerEvent, string headBranch, string headRepository, string updatedAt)
            => $"{{\"id\":{id},\"event\":\"{triggerEvent}\",\"head_branch\":\"{headBranch}\",\"status\":\"completed\",\"conclusion\":\"success\"," +
               $"\"updated_at\":\"{updatedAt}\",\"head_repository\":{{\"full_name\":\"{headRepository}\"}}}}";

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
