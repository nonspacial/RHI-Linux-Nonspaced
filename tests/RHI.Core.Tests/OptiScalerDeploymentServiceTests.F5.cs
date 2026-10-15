using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #157 (upstream 2.8.7 d4aac04): OptiScaler F5 DLSSNR is treated like DLSS NR — it deploys its bundled NR forwarder and
/// the manifest's nvngx_dlssnr.dll, owns that runtime under the one-owner rule, and gets the Nightly-style FG rows.
/// Package layout copied from the real OptiScaler-DLSSNR-F5-0.1.30.zip (helper DLLs under OptiScaler/, docs/, redist/,
/// LICENSE, .md/.ps1/.txt beside OptiScaler.dll).
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    private OptiScalerPackage CreateF5Package(string name)
    {
        var package = CreatePackage(name) with { Version = "0.1.30", Variant = OptiScalerVariant.F5 };
        var root = package.RootDirectory;
        File.WriteAllBytes(Path.Combine(root, "nvngx.dll_dlssnr.dll"), [0x4d, 0x5a, 0x0f, 0x05]);
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "NR-COMPATIBILITY.md"), "docs");
        Directory.CreateDirectory(Path.Combine(root, "redist", "streamline"));
        File.WriteAllText(Path.Combine(root, "redist", "streamline", "manifest.json"), "{}");
        foreach (var file in new[] { "LICENSE", "README.md", "INSTALL-DLSSNR.md", "SHA256SUMS.txt", "get_streamline.ps1" })
            File.WriteAllText(Path.Combine(root, file), file);
        Directory.CreateDirectory(Path.Combine(root, "OptiScaler", "LUTs"));
        File.WriteAllText(Path.Combine(root, "OptiScaler", "LUTs", "identity_33.cube"), "lut");
        return package;
    }

    private static byte[] NrZip()
    {
        var pe = new byte[128 * 1024];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(pe, 0x80);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("nvngx_dlssnr.dll").Open();
            entry.Write(pe);
        }
        return stream.ToArray();
    }

    private DlssManagerService NrRuntime(string name)
    {
        var nr = NrZip();
        var manifest = Encoding.UTF8.GetBytes("{\"dlss\":[],\"dlssnr\":[{\"version\":\"310.8.2\",\"url\":\"https://github.com/rhi-tests/nr.zip\"}]}");
        var http = new HttpClient(new BytesHandler(new() { [DlssManagerService.ManifestUrl] = manifest, ["https://github.com/rhi-tests/nr.zip"] = nr }));
        return new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(_root, name, "dlss-cache"));
    }

    [Fact]
    public async Task InstallF5_DeploysForwarderAndManagedNrRuntime_SkipsPackageDocs_AndRemovesCleanly()
    {
        var (game, store) = CreateGame("f5", GraphicsApi.DirectX12);
        var provider = new FakePackageProvider(CreateF5Package("f5-package"));
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider, NrRuntime("f5"));
        var bin = game.BinaryDirectory!;

        var installed = await service.InstallAsync(game, OptiScalerVariant.F5);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(OptiScalerVariant.F5, provider.LastVariant);
        foreach (var deployed in new[] { "dxgi.dll", "OptiScaler.ini", "nvngx.dll_dlssnr.dll", "nvngx_dlssnr.dll", Path.Combine("OptiScaler", "libxess.dll"), Path.Combine("OptiScaler", "LUTs", "identity_33.cube") })
            Assert.True(File.Exists(Path.Combine(bin, deployed)), deployed);
        foreach (var skipped in new[] { "docs", "redist" })
            Assert.False(Directory.Exists(Path.Combine(bin, skipped)), skipped);
        foreach (var skipped in new[] { "LICENSE", "README.md", "INSTALL-DLSSNR.md", "SHA256SUMS.txt", "get_streamline.ps1" })
            Assert.False(File.Exists(Path.Combine(bin, skipped)), skipped);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(OptiScalerVariant.F5, OptiScalerDeploymentService.GetInstalledVariant(record));
        Assert.Equal("310.8.2", record.Metadata[OptiScalerDeploymentService.DlssNrRuntimeVersionMetadataKey]);
        Assert.Contains("F5 DLSSNR", installed.Message, StringComparison.Ordinal);

        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssnr.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx.dll_dlssnr.dll")));
    }

    [Fact]
    public async Task InstallF5_NeedsTheNrRuntimeService_LikeDlssNr()
    {
        var (game, store) = CreateGame("f5-no-dlss", GraphicsApi.DirectX12);
        var service = NewService(store, new FakePackageProvider(CreateF5Package("f5-no-dlss-package")));

        var result = await service.InstallAsync(game, OptiScalerVariant.F5);

        Assert.False(result.Success);
        Assert.Contains("nvngx_dlssnr.dll", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
    }

    [Theory]
    [InlineData(OptiScalerVariant.F5)]
    [InlineData(OptiScalerVariant.DlssNr)]
    public async Task Install_NrVariant_IsRefusedWhileFeederOwnsTheRuntime_BeforeRequestingPackage(OptiScalerVariant variant)
    {
        var (game, store) = CreateGame("feeder-owns-" + variant, GraphicsApi.DirectX12);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.Dlss5Feeder,
            BinaryDirectory = game.BinaryDirectory!, Files = []
        });
        var provider = new FakePackageProvider(CreateF5Package("feeder-owns-package-" + variant));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game, variant);

        Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("DLSS5 Feeder"), result.Message);
        Assert.Equal(0, provider.CallCount);
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    // w4/encore review minor 2: RTX Encore with its Neural Rendering file owns the runtime (#158); OptiScaler's NR variants
    // are refused at their own entry point, before the package is requested. Without that file RTX Encore owns nothing.
    [Theory]
    [InlineData(OptiScalerVariant.F5, true)]
    [InlineData(OptiScalerVariant.DlssNr, true)]
    [InlineData(OptiScalerVariant.F5, false)]
    public async Task Install_NrVariant_IsRefusedOnlyWhileRtxEncoreOwnsTheRuntime(OptiScalerVariant variant, bool encoreListsRuntime)
    {
        var (game, store) = CreateGame($"encore-owns-{variant}-{encoreListsRuntime}", GraphicsApi.DirectX12);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RtxEncore,
            BinaryDirectory = game.BinaryDirectory!,
            Files = encoreListsRuntime
                ? [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "version.dll"), Sha256 = "00" },
                   new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll"), Sha256 = "00" }]
                : [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "version.dll"), Sha256 = "00" }]
        });
        var provider = new FakePackageProvider(CreateF5Package($"encore-owns-package-{variant}-{encoreListsRuntime}"));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game, variant);

        if (encoreListsRuntime)
        {
            Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("RTX Encore"), result.Message);
            Assert.Equal(0, provider.CallCount);
            Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
        }
        else
        {
            Assert.NotEqual(NeuralRenderingRuntimeOwnership.RefusalFor("RTX Encore"), result.Message);
            Assert.Equal(1, provider.CallCount);
        }
    }

    [Theory]
    [InlineData(OptiScalerVariant.Stable)]
    [InlineData(OptiScalerVariant.Nightly)]
    public async Task Install_StableOrNightly_StaysAvailableBesideFeeder(OptiScalerVariant variant)
    {
        var (game, store) = CreateGame("feeder-beside-" + variant, GraphicsApi.DirectX12);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.Dlss5Feeder,
            BinaryDirectory = game.BinaryDirectory!, Files = []
        });
        var service = NewService(store, new FakePackageProvider(CreatePackage("feeder-beside-package-" + variant)));

        Assert.True((await service.InstallAsync(game, variant)).Success);
    }

    [Fact]
    public void F5_GetsTheNightlyStyleFgStack()
    {
        var record = new LinuxDeploymentRecord
        {
            Component = ManagedComponentKind.OptiScaler,
            Metadata = new(StringComparer.OrdinalIgnoreCase) { [OptiScalerDeploymentService.VariantMetadataKey] = "F5" }
        };
        Assert.True(OptiScalerDeploymentService.SupportsManagedFgStack(record));
        Assert.Equal("F5 DLSSNR", OptiScalerDeploymentService.VariantLabel(OptiScalerVariant.F5));
    }

    private sealed class BytesHandler(Dictionary<string, byte[]> map) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(map.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
