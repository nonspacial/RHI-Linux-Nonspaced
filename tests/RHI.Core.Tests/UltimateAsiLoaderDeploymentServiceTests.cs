using RHI.Core.Deployment;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed partial class UltimateAsiLoaderDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-ual-tests-" + Guid.NewGuid().ToString("N"));

    public UltimateAsiLoaderDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SupportedNames_AreBitnessFiltered()
    {
        using var x64 = CreateGame("x64", 0x8664);
        using var x86 = CreateGame("x86", 0x014c);

        Assert.Contains("binkw64.dll", UltimateAsiLoaderDeploymentService.SupportedNamesFor(x64.Game));
        Assert.DoesNotContain("binkw32.dll", UltimateAsiLoaderDeploymentService.SupportedNamesFor(x64.Game));
        Assert.Contains("binkw32.dll", UltimateAsiLoaderDeploymentService.SupportedNamesFor(x86.Game));
        Assert.DoesNotContain("binkw64.dll", UltimateAsiLoaderDeploymentService.SupportedNamesFor(x86.Game));
    }

    [Fact]
    public void ParseReleaseJson_SelectsArchitectureAssetAndDigest()
    {
        var json = """
        {
          "tag_name": "v9.7.4",
          "assets": [
            {
              "name": "Ultimate-ASI-Loader.zip",
              "browser_download_url": "https://github.com/rhi-tests/x86.zip",
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            },
            {
              "name": "Ultimate-ASI-Loader_x64.zip",
              "browser_download_url": "https://github.com/rhi-tests/x64.zip",
              "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }
          ]
        }
        """;

        var release = UltimateAsiLoaderDeploymentService.ParseReleaseJson(json, PeArchitecture.X64);

        Assert.Equal("9.7.4", release.Version);
        Assert.Equal("Ultimate-ASI-Loader_x64.zip", release.AssetName);
        Assert.Equal("https://github.com/rhi-tests/x64.zip", release.AssetUrl);
        Assert.Equal(new string('b', 64), release.AssetSha256);
    }

    [Fact]
    public async Task InstallAndRemove_NewProxy_AppliesAndRestoresPerGameWineOverride()
    {
        using var fixture = CreateGame("newproxy", 0x8664);
        var zip = CreateUalZip(0x8664, marker: 0x31);
        var handler = CreateReleaseHandler(zip, "9.7.4");
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.True(install.Success, install.Message);
        var target = Path.Combine(fixture.Bin, "version.dll");
        Assert.True(File.Exists(target));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        var record = records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader);
        Assert.NotNull(record);
        Assert.Equal("9.7.4", record!.Version);
        Assert.Equal("version.dll", record.Metadata["UAL.ProxyName"]);
        DownloadRecordAssert.Recorded(record, DownloadRecordAssert.Sha256Of(target), VerifiedDownload.PublishedDigest);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    [Fact]
    public async Task InstallAndRemove_ExistingForeignProxy_UsesHookedChainingAndRestoresExactBytes()
    {
        using var fixture = CreateGame("chain", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);

        var zip = CreateUalZip(0x8664, marker: 0x42);
        using var http = new HttpClient(CreateReleaseHandler(zip, "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(install.Success, install.Message);
        var hooked = Path.Combine(fixture.Bin, "winmmHooked.dll");
        Assert.True(File.Exists(hooked));
        Assert.Equal(original, File.ReadAllBytes(hooked));
        Assert.False(original.SequenceEqual(File.ReadAllBytes(target)));

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(hooked));
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
    }

    [Fact]
    public async Task Install_ExistingHookedBackup_IsProtected()
    {
        using var fixture = CreateGame("hookedexists", 0x8664);
        File.WriteAllText(Path.Combine(fixture.Bin, "version.dll"), "original");
        File.WriteAllText(Path.Combine(fixture.Bin, "versionHooked.dll"), "existing-hook");

        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x12), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Equal("existing-hook", File.ReadAllText(Path.Combine(fixture.Bin, "versionHooked.dll")));
    }

    [Fact]
    public async Task Install_DoesNotChainOverAnotherRhiManagedProxy()
    {
        using var fixture = CreateGame("rhiowned", 0x8664);
        var target = Path.Combine(fixture.Bin, "dxgi.dll");
        File.WriteAllText(target, "managed-reshade");

        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = "deadbeef" }]
        });

        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x22), "9.7.4"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var result = await service.InstallAsync(fixture.Game, "dxgi.dll");

        Assert.False(result.Success);
        Assert.Contains("RHI-managed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("managed-reshade", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "dxgiHooked.dll")));
    }

    [Fact]
    public async Task Download_DigestMismatch_BlocksBeforeFileOrRegistryMutation()
    {
        using var fixture = CreateGame("digest", 0x8664);
        var zip = CreateUalZip(0x8664, 0x51);
        using var http = new HttpClient(CreateReleaseHandler(zip, "9.7.4", forcedDigest: new string('0', 64)));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains(RHI.Core.Deployment.AtomicFile.ShaMismatchPrefix, result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
    }

    [Fact]
    public async Task Remove_ModifiedManagedProxy_BlocksBeforeTeardown()
    {
        using var fixture = CreateGame("modified", 0x8664);
        var originalPath = Path.Combine(fixture.Bin, "version.dll");
        File.WriteAllText(originalPath, "original-third-party");

        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x62), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));
        var install = await service.InstallAsync(fixture.Game, "version.dll");
        Assert.True(install.Success, install.Message);

        File.AppendAllText(originalPath, "modified-after-install");
        var result = await service.RemoveAsync(fixture.Game);

        Assert.False(result.Success);
        Assert.Contains("modified outside RHI", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "versionHooked.dll")));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    [Fact]
    public async Task Update_ReplacesOnlyManagedProxyAndKeepsHookedOriginal()
    {
        using var fixture = CreateGame("update", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var original = Encoding.ASCII.GetBytes("original-winmm");
        File.WriteAllBytes(target, original);

        var zip1 = CreateUalZip(0x8664, 0x70);
        var zip2 = CreateUalZip(0x8664, 0x71);
        var handler = new SequenceReleaseHandler(
            CreateReleaseJson(zip1, "9.7.4", "https://github.com/rhi-tests/ual-1.zip"), zip1,
            CreateReleaseJson(zip2, "9.7.5", "https://github.com/rhi-tests/ual-2.zip"), zip2);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");
        Assert.True(install.Success, install.Message);
        var firstBytes = File.ReadAllBytes(target);

        handler.AdvanceRelease();
        var update = await service.UpdateAsync(fixture.Game);

        Assert.True(update.Success, update.Message);
        Assert.False(firstBytes.SequenceEqual(File.ReadAllBytes(target)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Bin, "winmmHooked.dll")));
        Assert.Equal("9.7.5", records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!.Version);
        DownloadRecordAssert.Recorded(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!,
            DownloadRecordAssert.Sha256Of(target), VerifiedDownload.PublishedDigest);
    }

    [Fact]
    public async Task CheckLatest_ReportsUpdateWithoutMutatingGame()
    {
        using var fixture = CreateGame("check", 0x8664);
        var zip = CreateUalZip(0x8664, 0x79);
        using var http = new HttpClient(CreateReleaseHandler(zip, "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var status = await service.CheckLatestAsync(fixture.Game);

        Assert.True(status.Success);
        Assert.Equal("9.7.4", status.LatestVersion);
        Assert.False(status.HasUpdate);
        Assert.Empty(Directory.GetFiles(fixture.Bin, "*.dll"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        using var fixture = CreateGame("cancel-install", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, marker: 0x33), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(fixture.Game, "version.dll", ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
    }

    // w2/vmsplit re-review, minor 2: the removal closes the Cancel gate before its first change, so a Cancel pressed
    // while it writes is refused and its real outcome is reported (it used to be accepted and read "Operation cancelled.").
    [Fact]
    public async Task Remove_EntersItsWritePhaseBeforeTheFirstChange_AndRunsToTheEnd()
    {
        using var fixture = CreateGame("cancel-remove", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, marker: 0x36), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(fixture.Game, ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    [Fact]
    public async Task Update_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        using var fixture = CreateGame("cancel-update", 0x8664);
        var zip1 = CreateUalZip(0x8664, 0x74);
        var zip2 = CreateUalZip(0x8664, 0x75);
        var handler = new SequenceReleaseHandler(
            CreateReleaseJson(zip1, "9.7.4", "https://github.com/rhi-tests/ual-1.zip"), zip1,
            CreateReleaseJson(zip2, "9.7.5", "https://github.com/rhi-tests/ual-2.zip"), zip2);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        handler.AdvanceRelease();

        var run = await CancelAtWritePhase.RunAsync(ct => service.UpdateAsync(fixture.Game, ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Equal("9.7.5", records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!.Version);
    }

    private GameFixture CreateGame(string name, ushort machine)
    {
        var dir = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(dir, "game");
        var prefix = Path.Combine(dir, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        WritePe(exe, machine, 64 * 1024, 0x11);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");

        return new GameFixture(dir, bin, prefix, new DetectedGame
        {
            Name = "UAL Test " + name,
            InstallPath = dir,
            Source = "Test",
            Runner = "Wine",
            PrefixPath = prefix,
            ExecutablePath = exe,
            BinaryDirectory = bin,
            GraphicsApi = GraphicsApi.DirectX12
        });
    }

    private static HttpMessageHandler CreateReleaseHandler(byte[] zip, string version, string? forcedDigest = null)
    {
        var assetUrl = "https://github.com/rhi-tests/ual.zip";
        var json = CreateReleaseJson(zip, version, assetUrl, forcedDigest);
        return new MapHandler(new Dictionary<string, HttpResponseMessage>
        {
            [UltimateAsiLoaderDeploymentService.GitHubApiUrl] = JsonResponse(json),
            [assetUrl] = BytesResponse(zip)
        });
    }

    private static string CreateReleaseJson(byte[] zip, string version, string assetUrl, string? forcedDigest = null)
    {
        var digest = forcedDigest ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        return $$"""
        {
          "tag_name": "v{{version}}",
          "assets": [
            {
              "name": "Ultimate-ASI-Loader_x64.zip",
              "browser_download_url": "{{assetUrl}}",
              "digest": "sha256:{{digest}}"
            },
            {
              "name": "Ultimate-ASI-Loader.zip",
              "browser_download_url": "{{assetUrl}}-x86",
              "digest": "sha256:{{digest}}"
            }
          ]
        }
        """;
    }

    private static byte[] CreateUalZip(ushort machine, byte marker)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("dinput8.dll", CompressionLevel.Fastest);
            using var stream = entry.Open();
            var pe = CreatePe(machine, 64 * 1024, marker);
            stream.Write(pe);
        }
        return ms.ToArray();
    }

    private static byte[] CreatePe(ushort machine, int size, byte marker)
    {
        var bytes = Enumerable.Repeat(marker, size).ToArray();
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        bytes[0x82] = 0;
        bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static void WritePe(string path, ushort machine, int size, byte marker)
        => File.WriteAllBytes(path, CreatePe(machine, size, marker));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BytesResponse(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record GameFixture(string Root, string Bin, string Prefix, DetectedGame Game) : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses;

        public MapHandler(Dictionary<string, HttpResponseMessage> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.ToString();
            if (!_responses.TryGetValue(key, out var template))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var clone = new HttpResponseMessage(template.StatusCode);
            foreach (var header in template.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (template.Content is StringContent)
            {
                var text = template.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                clone.Content = new StringContent(text, Encoding.UTF8, "application/json");
            }
            else
            {
                var bytes = template.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
                clone.Content = new ByteArrayContent(bytes);
            }
            return Task.FromResult(clone);
        }
    }

    private sealed class SequenceReleaseHandler : HttpMessageHandler
    {
        private readonly string _release1;
        private readonly byte[] _zip1;
        private readonly string _release2;
        private readonly byte[] _zip2;
        private int _phase;

        public SequenceReleaseHandler(string release1, byte[] zip1, string release2, byte[] zip2)
        {
            _release1 = release1;
            _zip1 = zip1;
            _release2 = release2;
            _zip2 = zip2;
        }

        public void AdvanceRelease() => _phase = 1;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == UltimateAsiLoaderDeploymentService.GitHubApiUrl)
                return Task.FromResult(JsonResponse(_phase == 0 ? _release1 : _release2));
            if (url == "https://github.com/rhi-tests/ual-1.zip")
                return Task.FromResult(BytesResponse(_zip1));
            if (url == "https://github.com/rhi-tests/ual-2.zip")
                return Task.FromResult(BytesResponse(_zip2));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
