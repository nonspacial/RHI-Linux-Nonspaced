using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #238 (upstream v2.8.8 Beta a1a4e48: ASI Loader updates reach games): Ultimate ASI Loader is an Update All family. Its check
// reads the game's own architecture (x86 and x64 each from their own asset), never plans a swap over a proxy removed or
// changed outside RHI, and could not check when the lookup fails. Upstream's "64-bit re-downloaded at every launch, 32-bit
// never updated" never existed here (per-architecture stamped cache): pinned below.
public sealed partial class UltimateAsiLoaderDeploymentServiceTests
{
    /// <summary>A UAL release whose x64 and x86 zips are both served, counting the downloads of each.</summary>
    private sealed class UalRelease : HttpMessageHandler
    {
        public string Version { get; set; } = "9.7.4";
        public Dictionary<string, (byte[] X64, byte[] X86)> Zips { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);
        public int ApiRequests { get; private set; }
        public bool Offline { get; set; }

        public UalRelease Add(string version, byte marker)
        {
            Zips[version] = (CreateUalZip(0x8664, marker), CreateUalZip(0x014C, (byte)(marker + 1)));
            Version = version;
            return this;
        }

        private static string Url(string version, string arch) => $"https://github.com/rhi-tests/ual/{version}/{arch}.zip";
        private static string Digest(byte[] zip) => Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (Offline) throw new HttpRequestException("offline test");
            if (url == UltimateAsiLoaderDeploymentService.GitHubApiUrl)
            {
                ApiRequests++;
                var (x64, x86) = Zips[Version];
                return Task.FromResult(JsonResponse($$"""
                    {"tag_name":"v{{Version}}","assets":[
                      {"name":"Ultimate-ASI-Loader_x64.zip","browser_download_url":"{{Url(Version, "x64")}}","digest":"sha256:{{Digest(x64)}}"},
                      {"name":"Ultimate-ASI-Loader.zip","browser_download_url":"{{Url(Version, "x86")}}","digest":"sha256:{{Digest(x86)}}"}]}
                    """));
            }
            foreach (var (version, (x64, x86)) in Zips)
                foreach (var (arch, zip) in new[] { ("x64", x64), ("x86", x86) })
                    if (url == Url(version, arch))
                    {
                        Downloads[url] = Downloads.GetValueOrDefault(url) + 1;
                        return Task.FromResult(BytesResponse(zip));
                    }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public int DownloadsOf(string version, string arch) => Downloads.GetValueOrDefault(Url(version, arch));
    }

    private (UltimateAsiLoaderDeploymentService Service, LinuxDeploymentRecordStore Records) UalService(GameFixture fixture, HttpClient http, string? cache = null)
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        return (new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), cache ?? Path.Combine(fixture.Root, "cache")), records);
    }

    [Theory]
    [InlineData((ushort)0x8664)]
    [InlineData((ushort)0x014C)]
    public async Task CheckManaged_NewerRelease_IsAnUpdate_ForEachArchitecture_AndUpdateKeepsNameAndBitness(ushort machine)
    {
        using var fixture = CreateGame("check-" + machine, machine);
        using var release = new UalRelease().Add("9.7.4", 0x40);
        release.Add("9.7.5", 0x42);
        release.Version = "9.7.4";
        using var http = new HttpClient(release);
        var (service, records) = UalService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        release.Version = "9.7.5";

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);
        var after = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.False(check.CouldNotCheck, check.Message);
        Assert.Equal(("9.7.4", "9.7.5"), (check.InstalledVersion, check.LatestVersion));
        Assert.True(update.Success, update.Message);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        Assert.Equal(machine == 0x8664 ? PeArchitecture.X64 : PeArchitecture.X86,
            RHI.Core.Services.PeArchitectureDetector.Detect(target));
        Assert.Equal(CreatePe(machine, 64 * 1024, machine == 0x8664 ? (byte)0x42 : (byte)0x43), File.ReadAllBytes(target));
        Assert.Equal("9.7.5", records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!.Version);
        Assert.False(after.UpdateAvailable, after.Message);
        Assert.False(after.CouldNotCheck, after.Message);
        // Each architecture downloads its own asset, once per release.
        var arch = machine == 0x8664 ? "x64" : "x86";
        var other = machine == 0x8664 ? "x86" : "x64";
        Assert.Equal((1, 1, 0, 0), (release.DownloadsOf("9.7.4", arch), release.DownloadsOf("9.7.5", arch),
            release.DownloadsOf("9.7.4", other), release.DownloadsOf("9.7.5", other)));
    }

    // Upstream a1a4e48's bug (x64 re-downloaded at every launch, x86 never updated) never existed here: with a shared cache,
    // a second x64 game reuses the stamped x64 DLL (no download), and an x86 game is updated from the x86 asset.
    [Fact]
    public async Task SharedCache_ReusesTheStampedX64Dll_AndUpdatesX86FromItsOwnAsset()
    {
        using var x64a = CreateGame("x64a", 0x8664);
        using var x64b = CreateGame("x64b", 0x8664);
        using var x86 = CreateGame("x86", 0x014C);
        using var release = new UalRelease().Add("9.7.4", 0x50);
        release.Add("9.7.5", 0x52);
        release.Version = "9.7.4";
        using var http = new HttpClient(release);
        var cache = Path.Combine(x64a.Root, "shared-cache");
        var records = new LinuxDeploymentRecordStore(Path.Combine(x64a.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), cache);
        foreach (var game in new[] { x64a, x64b, x86 })
            Assert.True((await service.InstallAsync(game.Game, "version.dll")).Success);
        release.Version = "9.7.5";

        foreach (var game in new[] { x64a, x64b, x86 })
            Assert.True((await service.UpdateAsync(game.Game)).Success);

        Assert.Equal((1, 1), (release.DownloadsOf("9.7.4", "x64"), release.DownloadsOf("9.7.5", "x64")));
        Assert.Equal((1, 1), (release.DownloadsOf("9.7.4", "x86"), release.DownloadsOf("9.7.5", "x86")));
        Assert.Equal(CreatePe(0x014C, 64 * 1024, 0x53), File.ReadAllBytes(Path.Combine(x86.Bin, "version.dll")));
        Assert.All(new[] { x64a, x64b }, game => Assert.Equal(CreatePe(0x8664, 64 * 1024, 0x52), File.ReadAllBytes(Path.Combine(game.Bin, "version.dll"))));
    }

    [Fact]
    public async Task CheckManaged_ProxyRemovedOutsideRhi_IsCouldNotCheck_BeforeAnyLookup_AndUpdateNeverRecreatesIt()
    {
        using var fixture = CreateGame("removed", 0x8664);
        using var release = new UalRelease().Add("9.7.4", 0x60);
        release.Add("9.7.5", 0x62);
        release.Version = "9.7.4";
        using var http = new HttpClient(release);
        var (service, records) = UalService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        File.Delete(target);
        release.Version = "9.7.5";
        var lookups = release.ApiRequests;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + target, check.Message);
        Assert.Equal(lookups, release.ApiRequests);
        Assert.False(update.Success);
        Assert.False(File.Exists(target));
        Assert.Equal("9.7.4", records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!.Version);
    }

    [Fact]
    public async Task CheckManaged_ProxyChangedOutsideRhi_IsCouldNotCheck_OnlyWhenAnUpdateIsPlanned()
    {
        using var fixture = CreateGame("changed", 0x8664);
        using var release = new UalRelease().Add("9.7.4", 0x64);
        release.Add("9.7.5", 0x66);
        release.Version = "9.7.4";
        using var http = new HttpClient(release);
        var (service, _) = UalService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var replaced = CreatePe(0x8664, 64 * 1024, 0x68);
        File.WriteAllBytes(target, replaced);

        var current = await service.CheckManagedUpdateAsync(fixture.Game);
        release.Version = "9.7.5";
        var planned = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.Equal((false, false), (current.UpdateAvailable, current.CouldNotCheck));
        Assert.Equal((false, true), (planned.UpdateAvailable, planned.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + target, planned.Message);
        Assert.Equal(replaced, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task CheckManaged_LookupFails_IsCouldNotCheck_NeverCurrent()
    {
        using var fixture = CreateGame("offline", 0x8664);
        using var release = new UalRelease().Add("9.7.4", 0x6A);
        using var http = new HttpClient(release);
        var (service, _) = UalService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        release.Offline = true;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
    }
}
