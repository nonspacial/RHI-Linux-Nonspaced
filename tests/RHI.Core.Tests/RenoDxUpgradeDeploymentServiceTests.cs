using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed partial class RenoDxUpgradeDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-upgrade-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxUpgradeDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ComponentProbe_UpgradeDoesNotMasqueradeAsPrimaryRenoDx()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var upgrade = Path.Combine(game.BinaryDirectory!, RenoDxUpgradeDeploymentService.AddonFileName64);
        File.WriteAllBytes(upgrade, CreatePe(0x8664, 96 * 1024, 0x31));

        var components = ComponentProbe.Probe(game, [game.ExecutablePath!, Path.Combine(game.BinaryDirectory!, "ReShade64.dll"), upgrade]);

        Assert.Equal(ComponentAvailability.Installed, components.Single(c => c.Kind == ManagedComponentKind.RenoDXUpgrade).Availability);
        Assert.Equal(ComponentAvailability.Available, components.Single(c => c.Kind == ManagedComponentKind.RenoDX).Availability);
    }

    [Fact]
    public async Task Install_X64_UsesManifest64UrlAndReshadeAddonPath()
    {
        var game = CreateGame(0x8664, withReShade: true);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=addons\n");
        var payload = CreatePe(0x8664, 96 * 1024, 0x41);
        var requested = new List<string>();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            requested.Add(req.RequestUri!.AbsoluteUri);
            if (req.RequestUri.AbsoluteUri.EndsWith("manifest.json", StringComparison.Ordinal))
                return JsonResponse(ManifestJson());
            if (req.RequestUri.AbsoluteUri == "https://github.com/OopyDoopy/renodx/releases/download/snapshot/renodx-upgrade.addon64")
                return BytesResponse(payload);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var store = NewStore();
        var service = NewService(http, store);

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        var target = Path.Combine(game.BinaryDirectory!, "addons", RenoDxUpgradeDeploymentService.AddonFileName64);
        Assert.True(File.Exists(target));
        Assert.Contains("https://github.com/OopyDoopy/renodx/releases/download/snapshot/renodx-upgrade.addon64", requested);
        var record = store.Find(game, ManagedComponentKind.RenoDXUpgrade);
        Assert.NotNull(record);
        Assert.Equal(target, record!.Files.Single().Path);
        Assert.Equal("snapshot", record.Version);
        var indexed = GameFileIndexer.Build(game.InstallPath, 8);
        var probed = ComponentProbe.Probe(game, indexed);
        Assert.Equal(ComponentAvailability.Installed, probed.Single(c => c.Kind == ManagedComponentKind.RenoDXUpgrade).Availability);
        Assert.Equal(ComponentAvailability.Available, probed.Single(c => c.Kind == ManagedComponentKind.RenoDX).Availability);
    }

    [Fact]
    public async Task Install_ForcedX86_UsesManifest32Url()
    {
        var game = CreateGame(0x8664, withReShade: true);
        game.ForcedArchitecture = PeArchitecture.X86;
        var payload = CreatePe(0x014c, 96 * 1024, 0x51);
        string? download = null;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.EndsWith("manifest.json", StringComparison.Ordinal))
                return JsonResponse(ManifestJson());
            download = req.RequestUri.AbsoluteUri;
            return BytesResponse(payload);
        }));
        var service = NewService(http, NewStore());

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal("https://github.com/OopyDoopy/renodx/releases/download/snapshot/renodx-upgrade.addon32", download);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, RenoDxUpgradeDeploymentService.AddonFileName32)));
    }

    [Fact]
    public async Task Install_BlocksExternalUpgradeBeforeNetworkDownload()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var external = Path.Combine(game.BinaryDirectory!, RenoDxUpgradeDeploymentService.AddonFileName64);
        File.WriteAllBytes(external, CreatePe(0x8664, 96 * 1024, 0x61));
        var downloadCalled = false;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.EndsWith("manifest.json", StringComparison.Ordinal))
                return JsonResponse(ManifestJson());
            downloadCalled = true;
            return BytesResponse(CreatePe(0x8664, 96 * 1024, 0x62));
        }));
        var service = NewService(http, NewStore());

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("External RenoDX Upgrade", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(downloadCalled);
        Assert.Equal((byte)0x61, File.ReadAllBytes(external)[2048]);
    }

    [Fact]
    public async Task Install_BlocksWhenReShadeIsMissing()
    {
        var game = CreateGame(0x8664, withReShade: false);
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("network should not be used")));
        var service = NewService(http, NewStore());

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("ReShade", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_RejectsPayloadWithWrongArchitecture()
    {
        var game = CreateGame(0x8664, withReShade: true);
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.EndsWith("manifest.json", StringComparison.Ordinal))
                return JsonResponse(ManifestJson());
            return BytesResponse(CreatePe(0x014c, 96 * 1024, 0x71));
        }));
        var service = NewService(http, NewStore());

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("expected 64-bit", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, RenoDxUpgradeDeploymentService.AddonFileName64)));
    }

    [Fact]
    public async Task Install_RespectsManifestDisabledFlag()
    {
        var game = CreateGame(0x8664, withReShade: true);
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.EndsWith("manifest.json", StringComparison.Ordinal))
                return JsonResponse(ManifestJson(disabled: true));
            throw new InvalidOperationException("payload must not be downloaded when disabled");
        }));
        var service = NewService(http, NewStore());

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_BlocksWhenManagedPayloadWasModified()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var store = NewStore();
        using var http = NewInstallHttp(CreatePe(0x8664, 96 * 1024, 0x81));
        var service = NewService(http, store);
        var install = await service.InstallAsync(game);
        Assert.True(install.Success, install.Message);
        var target = store.Find(game, ManagedComponentKind.RenoDXUpgrade)!.Files.Single().Path;
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 4096;
            stream.WriteByte(0xFF);
        }

        var remove = await service.RemoveAsync(game);

        Assert.False(remove.Success);
        Assert.True(File.Exists(target));
        Assert.NotNull(store.Find(game, ManagedComponentKind.RenoDXUpgrade));
    }

    [Fact]
    public async Task Remove_DeletesOnlyOwnedUnmodifiedPayload()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var store = NewStore();
        using var http = NewInstallHttp(CreatePe(0x8664, 96 * 1024, 0x91));
        var service = NewService(http, store);
        var install = await service.InstallAsync(game);
        Assert.True(install.Success, install.Message);
        var target = store.Find(game, ManagedComponentKind.RenoDXUpgrade)!.Files.Single().Path;
        var unrelated = Path.Combine(game.BinaryDirectory!, "user.addon64");
        File.WriteAllText(unrelated, "user file");

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(unrelated));
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDXUpgrade));
    }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var store = NewStore();
        using var http = NewInstallHttp(CreatePe(0x8664, 96 * 1024, 0x82));
        var service = NewService(http, store);

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, ct), [game.InstallPath]);

        // A late cancel used to throw after the copy and leave the add-on in the game folder with no record.
        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(store.Find(game, ManagedComponentKind.RenoDXUpgrade));
    }

    [Fact]
    public async Task Remove_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var store = NewStore();
        using var http = NewInstallHttp(CreatePe(0x8664, 96 * 1024, 0x83));
        var service = NewService(http, store);
        Assert.True((await service.InstallAsync(game)).Success);
        var target = store.Find(game, ManagedComponentKind.RenoDXUpgrade)!.Files.Single().Path;

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(game, ct), [game.InstallPath]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDXUpgrade));
        Assert.False(File.Exists(target));
    }

    private RenoDxUpgradeDeploymentService NewService(HttpClient http, LinuxDeploymentRecordStore store)
        => new(http, store, new UpstreamRhiManifestService(http, "https://example.test/manifest.json"), Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N")));

    private LinuxDeploymentRecordStore NewStore()
        => new(Path.Combine(_root, "records-" + Guid.NewGuid().ToString("N") + ".json"));

    private HttpClient NewInstallHttp(byte[] payload)
        => new(new FakeHandler(req => req.RequestUri!.AbsoluteUri.EndsWith("manifest.json", StringComparison.Ordinal)
            ? JsonResponse(ManifestJson())
            : BytesResponse(payload)));

    private DetectedGame CreateGame(ushort machine, bool withReShade)
    {
        var bin = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "game.exe");
        File.WriteAllBytes(exe, CreatePe(machine, 4096, 0x11));
        if (withReShade)
        {
            var reshade = Path.Combine(bin, machine == 0x014c ? "ReShade32.dll" : "ReShade64.dll");
            var bytes = CreatePe(machine, 2 * 1024 * 1024, 0x22);
            Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(bytes, 1024);
            File.WriteAllBytes(reshade, bytes);
        }
        return new DetectedGame
        {
            Name = "Upgrade Test",
            InstallPath = bin,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            Source = "Steam",
            Runner = "Proton",
            PrefixPath = Path.Combine(bin, "pfx")
        };
    }

    private static string ManifestJson(bool disabled = false) => $$"""
    {
      "version": 2,
      "addonPacks": {
        "renodx-upgrade": {
          "packageName": "RenoDX Upgrade",
          "description": "ITM/resource upgrades",
          "downloadUrl32": "https://github.com/OopyDoopy/renodx/releases/download/snapshot/renodx-upgrade.addon32",
          "downloadUrl64": "https://github.com/OopyDoopy/renodx/releases/download/snapshot/renodx-upgrade.addon64",
          "repositoryUrl": "https://github.com/OopyDoopy/renodx",
          "disabled": {{disabled.ToString().ToLowerInvariant()}}
        }
      }
    }
    """;

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static byte[] CreatePe(ushort machine, int size, byte marker)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        for (var i = 0x200; i < bytes.Length; i++) bytes[i] = marker;
        return bytes;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
