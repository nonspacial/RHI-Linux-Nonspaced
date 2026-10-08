using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class UpstreamRhiAddonDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-upstream-addon-tests-" + Guid.NewGuid().ToString("N"));

    public UpstreamRhiAddonDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Install_ConsumesPinnedManifestReleaseApiUrl()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(160 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifestJson = """
        {
          "version": 2,
          "addonPacks": {
            "mfgunlock": {
              "packageName": "MFG Ada Unlock",
              "releaseApiUrl": "https://api.github.com/repos/mfg/releases/latest",
              "repositoryUrl": "https://github.com/rhi-tests/mfg"
            }
          }
        }
        """;
        var releaseJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = "0.6",
                draft = false,
                assets = new[]
                {
                    new { name = "renodx-mfgunlock.addon64", browser_download_url = "https://github.com/rhi-tests/mfg.addon64", digest = "sha256:" + digest }
                }
            }
        });
        var seenReleaseApi = false;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == "https://manifest.test/current.json") return JsonResponse(manifestJson);
            if (url == "https://api.github.com/repos/mfg/releases?per_page=1")
            {
                seenReleaseApi = true;
                return JsonResponse(releaseJson);
            }
            if (url == "https://github.com/rhi-tests/mfg.addon64")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/current.json");
        var service = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.MfgAdaUnlock,
            "mfgunlock",
            "MFG Ada Unlock",
            "https://api.github.com/repos/rhi-tests/releases/latest",
            128 * 1024,
            ExactAssetName64: "renodx-mfgunlock.addon64",
            DeployFileName64: "renodx-mfgunlock.addon64",
            X64Only: true),
            Path.Combine(_root, "cache"));

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.True(seenReleaseApi);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-mfgunlock.addon64")));
        Assert.Equal("0.6", records.Find(game, ManagedComponentKind.MfgAdaUnlock)!.Version);
    }

    [Fact]
    public async Task Install_RespectsManifestDisabledFlag()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var manifestJson = """
        {
          "version": 2,
          "addonPacks": {
            "dlss5-feed": {
              "packageName": "DLSS5 Feeder",
              "releaseApiUrl": "https://api.github.com/repos/feeder/releases/latest",
              "disabled": true
            }
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://manifest.test/current.json") return JsonResponse(manifestJson);
            throw new InvalidOperationException("Release API must not be contacted for disabled add-on.");
        }));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "disabled-records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/current.json");
        var service = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder,
            "dlss5-feed",
            "DLSS5 Feeder",
            "https://api.github.com/repos/rhi-tests/releases/latest",
            64 * 1024,
            AssetNamePattern64: @"^DLSS5-Feeder-.*\.zip$",
            DeployFileName64: "dlss5-feed.addon64"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task Install_X64OnlyRejectsX86BeforeNetwork()
    {
        var game = CreateGame(machine: 0x014c);
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "x86-records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid");
        var service = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Dx11Bridge,
            "dlss5-dx11-bridge",
            "DLSS5 DX11 Bridge",
            "https://api.github.com/repos/rhi-tests/releases/latest",
            128 * 1024,
            ExactAssetName64: "dlss5-bridge.addon64",
            DeployFileName64: "dlss5-bridge.addon64",
            X64Only: true));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("64-bit", result.Message);
    }


    [Fact]
    public async Task Install_Dx11BridgeBlocksWhileShortFuseManagedBeforeNetwork()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "shortfuse-records.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = game.BinaryDirectory!,
            Files = []
        });
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid");
        var service = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Dx11Bridge,
            "dlss5-dx11-bridge",
            "DLSS5 DX11 Bridge",
            "https://api.github.com/repos/rhi-tests/releases/latest",
            128 * 1024,
            ExactAssetName64: "dlss5-bridge.addon64",
            DeployFileName64: "dlss5-bridge.addon64",
            X64Only: true));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("ShortFuse", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(records.Find(game, ManagedComponentKind.Dlss5Dx11Bridge));
    }

    private DetectedGame CreateGame(ushort machine = 0x8664)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "game.exe");
        WritePeExecutable(exe, machine);
        return new DetectedGame
        {
            Name = "Test Game",
            InstallPath = dir,
            BinaryDirectory = dir,
            ExecutablePath = exe,
            Source = "Steam",
            Runner = "Proton",
            PrefixPath = Path.Combine(dir, "pfx")
        };
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static byte[] MakePePayload(int size)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        for (var i = 2; i < bytes.Length; i++) bytes[i] = (byte)(i * 17 + 11);
        return bytes;
    }

    private static void WritePeExecutable(string path, ushort machine)
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteFakeReShade(string path)
    {
        var bytes = MakePePayload(2 * 1024 * 1024);
        Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(bytes, 1024);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
