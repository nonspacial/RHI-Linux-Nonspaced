using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class DofFixDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dof-fix-tests-" + Guid.NewGuid().ToString("N"));

    public DofFixDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Eligibility_ExplicitX86RejectsEvenManifestForce()
    {
        var game = CreateGame();
        game.ForcedArchitecture = PeArchitecture.X86;
        game.DofFixForcedByManifest = true;

        Assert.False(DofFixDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public void Eligibility_ManifestForceMirrorsUpstreamAndBypassesEngineAndSkip()
    {
        var game = CreateGame();
        game.EngineHint = null;
        game.DofFixForcedByManifest = true;
        game.DofFixSkippedByManifest = true;

        Assert.True(DofFixDeploymentService.IsGameEligible(game));
        Assert.Contains("force", DofFixDeploymentService.EligibilityReason(game), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligibility_Ue50Through56Accepted_Ue57AndSkipRejected()
    {
        var game = CreateGame();
        game.EngineHint = "Unreal Engine 5.0";
        Assert.True(DofFixDeploymentService.IsGameEligible(game));
        game.EngineHint = "Unreal Engine 5.6.2";
        Assert.True(DofFixDeploymentService.IsGameEligible(game));
        game.EngineHint = "Unreal Engine 5.7";
        Assert.False(DofFixDeploymentService.IsGameEligible(game));
        game.EngineHint = "Unreal Engine 5.5";
        game.DofFixSkippedByManifest = true;
        Assert.False(DofFixDeploymentService.IsGameEligible(game));
        game.DofFixSkippedByManifest = false;
        game.EngineHint = "Unreal Engine 4.27";
        Assert.False(DofFixDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public async Task ResolveLatest_SelectsHighestSemverAndAppliesManifestUrlTemplate()
    {
        var releases = """
        [
          {
            "tag_name": "ue-dof-fix-1.0.1",
            "assets": [ { "name": "renodx-universal_ue-dof-fix.addon64", "browser_download_url": "https://github.com/test/1.0.1" } ]
          },
          {
            "tag_name": "ue-dof-fix-1.0.3",
            "assets": [ { "name": "renodx-universal_ue-dof-fix.addon64", "browser_download_url": "https://github.com/test/1.0.3", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ]
          },
          {
            "tag_name": "ue-dof-fix-1.0.2",
            "assets": [ { "name": "renodx-universal_ue-dof-fix.addon64", "browser_download_url": "https://github.com/test/1.0.2" } ]
          }
        ]
        """;
        var manifestJson = """
        {
          "version": 2,
          "componentUrls": {
            "ueDofFix": "https://github.com/test/dof/{version}/renodx-universal_ue-dof-fix.addon64"
          }
        }
        """;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("rhi-repo/releases", StringComparison.OrdinalIgnoreCase)) return JsonResponse(releases);
            if (url == "https://manifest.test/manifest.json") return JsonResponse(manifestJson);
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var service = NewService(http, "resolve-records.json", "https://manifest.test/manifest.json");

        var release = await service.ResolveLatestReleaseAsync();

        Assert.Equal("1.0.3", release.Version);
        Assert.Equal("https://github.com/test/dof/1.0.3/renodx-universal_ue-dof-fix.addon64", release.AssetUrl);
        Assert.Null(release.Sha256); // Override payload is not assumed byte-identical to the GitHub asset.
    }

    [Fact]
    public async Task ResolveLatest_UsesPublishedGitHubDigestWithoutOverride()
    {
        var digest = new string('b', 64);
        var releases = $$"""
        [
          {
            "tag_name": "ue-dof-fix-1.2.0",
            "assets": [
              {
                "name": "renodx-universal_ue-dof-fix.addon64",
                "browser_download_url": "https://github.com/test/fix",
                "digest": "sha256:{{digest}}"
              }
            ]
          }
        ]
        """;
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("rhi-repo/releases", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(releases)
                : JsonResponse("{\"version\":2}")));
        var service = NewService(http, "digest-records.json");

        var release = await service.ResolveLatestReleaseAsync();

        Assert.Equal("1.2.0", release.Version);
        Assert.Equal("https://github.com/test/fix", release.AssetUrl);
        Assert.Equal(digest, release.Sha256);
    }

    [Fact]
    public async Task Install_ProtectsExternalExactAddonBeforeNetwork()
    {
        var game = CreateGame();
        game.DofFixForcedByManifest = true;
        var target = Path.Combine(game.BinaryDirectory!, "RENODX-UNIVERSAL_UE-DOF-FIX.ADDON64");
        File.WriteAllBytes(target, MakePePayload(256 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var service = NewService(http, "external-records.json");

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("external", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task InstallAndRemove_TracksHashAndDeletesOnlyOwnedAddon()
    {
        var game = CreateGame();
        game.DofFixForcedByManifest = true;
        var payload = MakePePayload(256 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var releases = ReleaseListJson("1.0.2", "https://github.com/test/fix", digest);
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("rhi-repo/releases", StringComparison.OrdinalIgnoreCase)) return JsonResponse(releases);
            if (url == "https://manifest.test/manifest.json") return JsonResponse("{\"version\":2}");
            if (url == "https://github.com/test/fix") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var recordsPath = Path.Combine(_root, "lifecycle-records.json");
        var records = new LinuxDeploymentRecordStore(recordsPath);
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        var service = new DofFixDeploymentService(http, records, manifest, Path.Combine(_root, "cache-lifecycle"));

        var install = await service.InstallAsync(game);
        var target = Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName);

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(target));
        var record = records.Find(game, ManagedComponentKind.DofFix);
        Assert.NotNull(record);
        Assert.Equal("1.0.2", record!.Version);
        Assert.Equal(digest, record.Files.Single().Sha256);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.Null(records.Find(game, ManagedComponentKind.DofFix));
    }

    [Fact]
    public async Task Remove_BlocksWhenOwnedAddonWasModified()
    {
        var game = CreateGame();
        var target = Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName);
        var original = MakePePayload(256 * 1024);
        File.WriteAllBytes(target, original);
        var originalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "modified-records.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.DofFix,
            BinaryDirectory = game.BinaryDirectory!,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = originalHash }]
        });
        var modified = MakePePayload(257 * 1024);
        File.WriteAllBytes(target, modified);
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var service = new DofFixDeploymentService(
            http,
            records,
            new UpstreamRhiManifestService(http, "https://manifest.invalid"),
            Path.Combine(_root, "cache-modified"));

        var result = await service.RemoveAsync(game);

        Assert.False(result.Success);
        Assert.Contains("modified", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(target));
        Assert.NotNull(records.Find(game, ManagedComponentKind.DofFix));
    }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    private (DofFixDeploymentService Service, LinuxDeploymentRecordStore Records, DetectedGame Game, HttpClient Http) CancelFixture(string name)
    {
        var game = CreateGame();
        game.DofFixForcedByManifest = true;
        var payload = MakePePayload(256 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var releases = ReleaseListJson("1.0.2", "https://github.com/test/fix", digest);
        var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("rhi-repo/releases", StringComparison.OrdinalIgnoreCase)) return JsonResponse(releases);
            if (url == "https://manifest.test/manifest.json") return JsonResponse("{\"version\":2}");
            if (url == "https://github.com/test/fix") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, name + "-records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        return (new DofFixDeploymentService(http, records, manifest, Path.Combine(_root, "cache-" + name)), records, game, http);
    }

    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (service, records, game, http) = CancelFixture("cancel-install");
        using var _ = http;

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, ct), [game.InstallPath]);

        // A late cancel used to throw after the copy and leave the add-on in the game folder with no record.
        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(records.Find(game, ManagedComponentKind.DofFix));
    }

    [Fact]
    public async Task Remove_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        var (service, records, game, http) = CancelFixture("cancel-remove");
        using var _ = http;
        Assert.True((await service.InstallAsync(game)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(game, ct), [game.InstallPath]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(records.Find(game, ManagedComponentKind.DofFix));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName)));
    }

    private DofFixDeploymentService NewService(HttpClient http, string recordsName, string manifestUrl = "https://manifest.test/manifest.json")
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, recordsName));
        var manifest = new UpstreamRhiManifestService(http, manifestUrl);
        return new DofFixDeploymentService(http, records, manifest, Path.Combine(_root, "cache-" + recordsName));
    }

    private DetectedGame CreateGame()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "game.exe");
        WritePeExecutable(exe, 0x8664);
        return new DetectedGame
        {
            Name = "Clair Obscur: Expedition 33",
            InstallPath = dir,
            BinaryDirectory = dir,
            ExecutablePath = exe,
            Source = "Steam",
            Runner = "Proton"
        };
    }

    private static string ReleaseListJson(string version, string url, string digest)
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = DofFixDeploymentService.TagPrefix + version,
                assets = new[]
                {
                    new
                    {
                        name = DofFixDeploymentService.AddonFileName,
                        browser_download_url = url,
                        digest = "sha256:" + digest
                    }
                }
            }
        });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static byte[] MakePePayload(int size)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        for (var i = 2; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);
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

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
