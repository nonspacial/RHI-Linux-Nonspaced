using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class FeederConflictRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-feeder-conflict-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(Dlss5FeederConsumer.DeepFriedChicken, "RenoDX-DLSS5.addon64")]
    [InlineData(Dlss5FeederConsumer.RenoDxDlss5, "Deep-Fried-Chicken.addon64")]
    public async Task Install_RejectsCaseVariantOpposingConsumerBeforeNetwork(
        Dlss5FeederConsumer consumer, string externalFileName)
        => await AssertExternalConsumerGuardAsync(consumer, externalFileName, "Consumidor neural conflitante");

    [Theory]
    [InlineData(Dlss5FeederConsumer.DeepFriedChicken, "Deep-Fried-Chicken.addon64")]
    [InlineData(Dlss5FeederConsumer.RenoDxDlss5, "RenoDX-DLSS5.addon64")]
    public async Task Install_RejectsCaseVariantProtectedConsumerBeforeNetwork(
        Dlss5FeederConsumer consumer, string externalFileName)
        => await AssertExternalConsumerGuardAsync(consumer, externalFileName, "External or pre-existing");

    private async Task AssertExternalConsumerGuardAsync(
        Dlss5FeederConsumer consumer, string externalFileName, string expectedMessage)
    {
        var binary = Path.Combine(_root, "game");
        var prefix = Path.Combine(_root, "prefix");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(binary, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 128 * 1024);
        WriteMarkedPe(Path.Combine(binary, "dxgi.dll"), 2 * 1024 * 1024, "ReShade reshade.me crosire");
        WriteMarkedPe(Path.Combine(binary, "d3dcompiler_47.dll"), 1_100_000, "Microsoft HLSL Shader Compiler D3DCompiler_47.dll");
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var ini = Path.Combine(binary, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=UserPreset.ini\n[ADDON]\nLoadFromDllMain=user-addon.addon64\n");
        var iniBefore = File.ReadAllBytes(ini);
        var registryBefore = File.ReadAllBytes(Path.Combine(prefix, "user.reg"));
        var externalPath = Path.Combine(binary, externalFileName);
        PeArchitectureDetectorTests.WritePe(externalPath, 0x8664, 70 * 1024);
        var externalBefore = File.ReadAllBytes(externalPath);
        var package = Path.Combine(_root, "consumer.zip");
        CreateDfcPackage(package);

        var game = new DetectedGame
        {
            Name = "Feeder case-sensitive filesystem guard",
            InstallPath = binary,
            BinaryDirectory = binary,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Source = "Test",
            Runner = "Wine",
            GraphicsApi = GraphicsApi.DirectX12
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        using var handler = new CountingNetworkHandler();
        using var http = new HttpClient(handler);
        var dlss = new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json");
        var legacy = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://release.invalid/latest",
            64 * 1024, DeployFileName64: "dlss5-feed.addon64"), Path.Combine(_root, "cache-legacy"));
        var feeder = new Dlss5FeederFullDeploymentService(
            http, records, dlss, legacy, Path.Combine(_root, "cache-feeder"), new WineDllOverrideService());

        var result = await feeder.InstallAsync(game, consumer, package, d3dCompiler47Path: null);

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(externalBefore, File.ReadAllBytes(externalPath));
        Assert.Equal(iniBefore, File.ReadAllBytes(ini));
        Assert.Equal(registryBefore, File.ReadAllBytes(Path.Combine(prefix, "user.reg")));
        Assert.Null(records.Find(game, ManagedComponentKind.Dlss5Feeder));
        Assert.False(File.Exists(Path.Combine(binary, "dlss5-feed.addon64")));
        Assert.False(File.Exists(Path.Combine(binary, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(binary, "nvngx_dlssnr.dll")));
    }

    private static void WriteMarkedPe(string path, int size, string marker)
    {
        PeArchitectureDetectorTests.WritePe(path, 0x8664, size);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = 4096;
        stream.Write(Encoding.ASCII.GetBytes(marker));
    }

    private static void CreateDfcPackage(string path)
    {
        var addon = new byte[70 * 1024];
        addon[0] = (byte)'M'; addon[1] = (byte)'Z';
        var bridge = new byte[4 * 1024];
        bridge[0] = (byte)'M'; bridge[1] = (byte)'Z';
        var sums = $"Deep Fried Chicken 1.6.8-test\n{Convert.ToHexString(SHA256.HashData(addon))}  deep-fried-chicken.addon64\n" +
                   $"{Convert.ToHexString(SHA256.HashData(bridge))}  deep-fried-chicken-nvngx.dll\n";
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "deep-fried-chicken.addon64", addon);
        WriteEntry(archive, "deep-fried-chicken-nvngx.dll", bridge);
        WriteEntry(archive, "deep-fried-chicken.cfg", Encoding.UTF8.GetBytes("arm=1\n"));
        WriteEntry(archive, "SHA256SUMS.txt", Encoding.UTF8.GetBytes(sums));
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        using var output = archive.CreateEntry(name).Open();
        output.Write(bytes);
    }

    private sealed class CountingNetworkHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
