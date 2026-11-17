using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review m2: Universal MFG Unlocker moves into OptiScaler's plugins folder after an OptiScaler install pinned
/// to a release too (F5 DLSSNR pinned to v0.1.29), not only after a Latest install.
/// </summary>
public sealed partial class Rtx40MfgStandaloneDeploymentServiceTests
{
    [Fact]
    public async Task PinnedReleaseOptiScalerInstall_MovesUniversalMfgIntoItsPluginsFolder()
    {
        using var fixture = CreateGame("pinned-plugin", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var unlocker = CreateUnlocker(fixture, http, records, xess: null);
        Assert.True((await unlocker.InstallAsync(fixture.Game, "winmm.dll")).Success);

        var package = F5Package(Path.Combine(fixture.Root, "f5-package"));
        var provider = new OneReleaseProvider(package);
        var optiscaler = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), records, new WineDllOverrideService(), provider, NrRuntime(fixture.Root));
        var managed = new LinuxManagedComponentService(
            records: records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: optiscaler,
            relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: unlocker, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!,
            shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            neuralVersionPins: new NeuralAddonVersionPreferenceStore(Path.Combine(fixture.Root, "pins.json")));
        managed.SetOptiScalerReleasePin(fixture.Game, OptiScalerVariant.F5, "v0.1.29");

        var installed = await managed.InstallOptiScalerAsync(fixture.Game, OptiScalerVariant.F5);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(["v0.1.29"], provider.Tags);
        Assert.EndsWith(Rtx40MfgStandaloneDeploymentService.MovedIntoOptiScalerMessage, installed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "OptiScaler", "plugins", "MFGUnlocker.asi")));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
    }

    private sealed class OneReleaseProvider(OptiScalerPackage package) : IOptiScalerPackageProvider
    {
        public List<string> Tags { get; } = [];

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a pinned install never asks for Latest");

        public Task<OptiScalerPackage> GetReleaseAsync(OptiScalerVariant variant, string tag, CancellationToken cancellationToken = default)
        {
            Tags.Add(tag);
            return Task.FromResult(package with { Variant = variant, Version = OptiScalerPackageProvider.NormalizeVersion(tag, variant) });
        }
    }

    private static OptiScalerPackage F5Package(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "OptiScaler"));
        var dll = new byte[4096];
        dll[0] = (byte)'M'; dll[1] = (byte)'Z';
        Encoding.ASCII.GetBytes("optiscaler/OptiScaler F5 fake package").CopyTo(dll, 128);
        File.WriteAllBytes(Path.Combine(root, "OptiScaler.dll"), dll);
        File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n");
        File.WriteAllText(Path.Combine(root, "OptiScaler", "libxess.dll"), "MZ xess");
        File.WriteAllBytes(Path.Combine(root, "nvngx.dll_dlssnr.dll"), [0x4d, 0x5a, 0x0f, 0x05]);
        return new OptiScalerPackage("0.1.29", "https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/releases/download/v0.1.29/F5.zip", root, OptiScalerVariant.F5);
    }

    private static DlssManagerService NrRuntime(string root)
    {
        var pe = CreatePe(0x8664, 128 * 1024, 0x33);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("nvngx_dlssnr.dll").Open();
            entry.Write(pe);
        }
        var manifest = Encoding.UTF8.GetBytes("{\"dlss\":[],\"dlssnr\":[{\"version\":\"310.8.2\",\"url\":\"https://github.com/rhi-tests/nr.zip\"}]}");
        var files = new Dictionary<string, byte[]> { [DlssManagerService.ManifestUrl] = manifest, ["https://github.com/rhi-tests/nr.zip"] = stream.ToArray() };
        return new DlssManagerService(new HttpClient(new MapHandler(files)), new TransactionalFileDeploymentService(), Path.Combine(root, "dlss-cache"));
    }

    private sealed class MapHandler(Dictionary<string, byte[]> map) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(map.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }
}
