using System.Net;
using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #249 item 11 (upstream 2.8.8 <c>a1a4e48</c>, <c>1b51cb2</c>): upstream re-downloaded the 64-bit Ultimate ASI Loader on
/// every launch and never updated 32-bit games. Here the cache is per architecture and stamped per release asset: a
/// stamped x64 DLL is reused for the next x64 game, and an x86 game installs and updates from the x86 asset of the
/// release, never from the x64 one. Pinned through the public install/update calls and the asset requests.
/// </summary>
public sealed partial class UltimateAsiLoaderDeploymentServiceTests
{
    [Fact]
    public async Task Pin288_X64CacheIsReusedWhenStamped_X86InstallsAndUpdatesFromItsOwnAsset()
    {
        using var x64First = CreateGame("pin288-x64-a", 0x8664);
        using var x64Second = CreateGame("pin288-x64-b", 0x8664);
        using var x86 = CreateGame("pin288-x86", 0x014c);
        var site = new Pin288UalSite();
        site.Publish("9.7.4", CreateUalZip(0x8664, 0x64), CreateUalZip(0x014c, 0x32));
        using var http = new HttpClient(site);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "pin288-deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(_root, "pin288-cache"));

        Assert.True((await service.InstallAsync(x64First.Game, "winmm.dll")).Success);
        var x86Install = await service.InstallAsync(x86.Game, "winmm.dll");
        var x64Again = await service.InstallAsync(x64Second.Game, "winmm.dll");

        Assert.True(x86Install.Success, x86Install.Message);
        Assert.True(x64Again.Success, x64Again.Message);
        Assert.Equal(1, site.Gets("9.7.4", "x64")); // the stamped x64 copy served the second x64 game
        Assert.Equal(1, site.Gets("9.7.4", "x86"));
        Assert.Equal(PeArchitecture.X86, PeArchitectureDetector.Detect(Path.Combine(x86.Bin, "winmm.dll")));
        Assert.Equal(PeArchitecture.X64, PeArchitectureDetector.Detect(Path.Combine(x64Second.Bin, "winmm.dll")));

        site.Publish("9.7.5", CreateUalZip(0x8664, 0x65), CreateUalZip(0x014c, 0x33));
        var check = await service.CheckLatestAsync(x86.Game);
        var update = await service.UpdateAsync(x86.Game);

        Assert.True(check.HasUpdate);
        Assert.True(update.Success, update.Message);
        Assert.Equal(1, site.Gets("9.7.5", "x86"));
        Assert.Equal(0, site.Gets("9.7.5", "x64"));
        var deployed = File.ReadAllBytes(Path.Combine(x86.Bin, "winmm.dll"));
        Assert.Equal(PeArchitecture.X86, PeArchitectureDetector.Detect(Path.Combine(x86.Bin, "winmm.dll")));
        Assert.Equal(0x33, deployed[^1]);
        Assert.Equal("9.7.5", records.Find(x86.Game, ManagedComponentKind.UltimateAsiLoader)!.Version);
        Assert.Equal("9.7.4", records.Find(x64First.Game, ManagedComponentKind.UltimateAsiLoader)!.Version);
    }

    /// <summary>GitHub's latest-release answer with one x64 and one x86 asset per version (own digests); counts asset GETs.</summary>
    private sealed class Pin288UalSite : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _gets = new(StringComparer.Ordinal);
        private string _release = "";

        private static string Url(string version, string arch) => $"https://github.com/rhi-tests/ual/releases/download/v{version}/{arch}.zip";

        public void Publish(string version, byte[] x64, byte[] x86)
        {
            _assets[Url(version, "x64")] = x64;
            _assets[Url(version, "x86")] = x86;
            static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            _release = $$"""
            {
              "tag_name": "v{{version}}",
              "assets": [
                { "name": "Ultimate-ASI-Loader_x64.zip", "browser_download_url": "{{Url(version, "x64")}}", "digest": "sha256:{{Digest(x64)}}" },
                { "name": "Ultimate-ASI-Loader.zip", "browser_download_url": "{{Url(version, "x86")}}", "digest": "sha256:{{Digest(x86)}}" }
              ]
            }
            """;
        }

        public int Gets(string version, string arch) => _gets.GetValueOrDefault(Url(version, arch));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == UltimateAsiLoaderDeploymentService.GitHubApiUrl) return Task.FromResult(JsonResponse(_release));
            if (!_assets.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            _gets[url] = _gets.GetValueOrDefault(url) + 1;
            return Task.FromResult(BytesResponse(bytes));
        }
    }
}
