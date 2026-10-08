using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class REFrameworkDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reframework-tests-" + Guid.NewGuid().ToString("N"));

    public REFrameworkDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Eligibility_RequiresReEngineAndRejectsX86()
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        Assert.True(REFrameworkDeploymentService.IsGameEligible(game));

        game.EngineHint = "Unreal Engine 5.4";
        Assert.False(REFrameworkDeploymentService.IsGameEligible(game));

        game.EngineHint = "RE Engine";
        game.ForcedArchitecture = PeArchitecture.X86;
        Assert.False(REFrameworkDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public async Task ResolveLatest_UsesMonolithicZipAndPublishedDigest()
    {
        var zip = MakeFrameworkZip();
        var digest = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            Assert.Contains("REFramework-nightly/releases", req.RequestUri!.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            return JsonResponse(ReleaseJson("nightly-01302-deadbeef", "https://github.com/rhi-tests/ref.zip", digest));
        }));
        var service = NewService(http, "resolve-records.json");

        var release = await service.ResolveLatestAsync();

        Assert.Equal(REFrameworkDeploymentService.MonolithicZipName, release.AssetName);
        Assert.Equal("nightly-01302-deadbeef", release.Version);
        Assert.Equal(digest, release.Sha256);
    }

    [Fact]
    public async Task Install_ProtectsExternalDinput8BeforeNetwork()
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "DINPUT8.DLL"), MakePePayload(0x8664, 300 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var service = NewService(http, "external-records.json");

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("external", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "DINPUT8.DLL")));
    }

    [Fact]
    public async Task InstallAndRemove_DeploysDinput8TracksHashAndRestoresOverride()
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        var zip = MakeFrameworkZip();
        var digest = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("REFramework-nightly/releases", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ReleaseJson("nightly-01302-deadbeef", "https://github.com/rhi-tests/ref.zip", digest));
            if (url == "https://github.com/rhi-tests/ref.zip")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "lifecycle-records.json"));
        var overrides = new WineDllOverrideService();
        var service = new REFrameworkDeploymentService(http, records, overrides, Path.Combine(_root, "cache-lifecycle"));

        var install = await service.InstallAsync(game);
        var target = Path.Combine(game.BinaryDirectory!, REFrameworkDeploymentService.DllFileName);

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(target));
        var record = records.Find(game, ManagedComponentKind.REFramework);
        Assert.NotNull(record);
        Assert.Equal("01302", record!.Version);
        Assert.False(string.IsNullOrWhiteSpace(record.Files.Single().Sha256));
        var regAfterInstall = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(regAfterInstall, "dinput8", Path.GetFileName(game.ExecutablePath)));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.Null(records.Find(game, ManagedComponentKind.REFramework));
        var regAfterRemove = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Null(WineDllOverrideService.TryReadOverride(regAfterRemove, "dinput8", Path.GetFileName(game.ExecutablePath)));
    }

    [Fact]
    public async Task Remove_BlocksModifiedOwnedDinput8AndKeepsOverride()
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        var target = Path.Combine(game.BinaryDirectory!, REFrameworkDeploymentService.DllFileName);
        var original = MakePePayload(0x8664, 300 * 1024);
        File.WriteAllBytes(target, original);
        var originalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        var overrides = new WineDllOverrideService();
        var overrideRecord = overrides.EnsureNativeBuiltin(game.PrefixPath!, "dinput8", Path.GetFileName(game.ExecutablePath));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "modified-records.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.REFramework,
            BinaryDirectory = game.BinaryDirectory!,
            WineOverride = overrideRecord,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = originalHash }]
        });
        File.WriteAllBytes(target, MakePePayload(0x8664, 301 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var service = new REFrameworkDeploymentService(http, records, overrides, Path.Combine(_root, "cache-modified"));

        var result = await service.RemoveAsync(game);

        Assert.False(result.Success);
        Assert.Contains("modified", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(target));
        var reg = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(reg, "dinput8", Path.GetFileName(game.ExecutablePath)));
    }

    // w2 wine review, minor 2 (N10): refused while Wine runs in the prefix, before the release is looked up.
    [Fact]
    public async Task Install_WhileWineRunsInThePrefix_IsRefusedBeforeNetwork()
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        var requests = 0;
        using var http = new HttpClient(new FakeHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Network must not be used.");
        }));
        var userReg = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "wine-running-records.json"));
        var service = new REFrameworkDeploymentService(http, records, new WineDllOverrideService(new WineRegistryEditor(_ => true)),
            Path.Combine(_root, "cache-wine-running"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + game.PrefixPath, result.Message);
        Assert.Equal(0, requests);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, REFrameworkDeploymentService.DllFileName)));
        Assert.Equal(userReg, File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")));
    }

    // w2 wine review, minor 2 (N5): when dinput8.dll cannot be deleted after the override was restored, RHI's value
    // goes back so the game keeps loading the framework the record still owns.
    [Fact]
    public async Task Remove_FailingToDeleteDinput8_PutsRhisOverrideBack()
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        var zip = MakeFrameworkZip();
        var digest = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("REFramework-nightly/releases", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ReleaseJson("nightly-01303-deadbeef", "https://github.com/rhi-tests/ref.zip", digest));
            if (url == "https://github.com/rhi-tests/ref.zip")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "delete-fails-records.json"));
        var service = new REFrameworkDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(_root, "cache-delete-fails"));
        Assert.True((await service.InstallAsync(game)).Success);
        var target = Path.Combine(game.BinaryDirectory!, REFrameworkDeploymentService.DllFileName);
        var mode = File.GetUnixFileMode(game.BinaryDirectory!);

        ComponentOperationResult remove;
        File.SetUnixFileMode(game.BinaryDirectory!, UnixFileMode.UserRead | UnixFileMode.UserExecute); // the delete fails
        try
        {
            remove = await service.RemoveAsync(game);
        }
        finally
        {
            File.SetUnixFileMode(game.BinaryDirectory!, mode);
        }

        Assert.False(remove.Success);
        Assert.True(File.Exists(target));
        var reg = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(reg, "dinput8", Path.GetFileName(game.ExecutablePath)));
        Assert.NotNull(records.Find(game, ManagedComponentKind.REFramework));
    }

    [Fact]
    public void ComponentProbe_ExternalRefNeedsRuntimeFootprintNotBareDinput8()
    {
        var game = CreateGame();
        var dinput = Path.Combine(game.BinaryDirectory!, "dinput8.dll");
        File.WriteAllBytes(dinput, MakePePayload(0x8664, 300 * 1024));
        var bare = ComponentProbe.Probe(game, [dinput]);
        Assert.Equal(ComponentAvailability.Available, bare.Single(c => c.Kind == ManagedComponentKind.REFramework).Availability);

        var refDir = Path.Combine(game.BinaryDirectory!, "reframework");
        Directory.CreateDirectory(refDir);
        var config = Path.Combine(refDir, "config.txt");
        File.WriteAllText(config, "test");
        var withFootprint = ComponentProbe.Probe(game, [dinput, config]);
        Assert.Equal(ComponentAvailability.Installed, withFootprint.Single(c => c.Kind == ManagedComponentKind.REFramework).Availability);
    }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    private (REFrameworkDeploymentService Service, LinuxDeploymentRecordStore Records, DetectedGame Game, HttpClient Http) CancelFixture(string name)
    {
        var game = CreateGame();
        game.EngineHint = "RE Engine";
        var zip = MakeFrameworkZip();
        var digest = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("REFramework-nightly/releases", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ReleaseJson("nightly-01302-deadbeef", "https://github.com/rhi-tests/ref.zip", digest));
            if (url == "https://github.com/rhi-tests/ref.zip")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, name + "-records.json"));
        return (new REFrameworkDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(_root, "cache-" + name)), records, game, http);
    }

    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (service, records, game, http) = CancelFixture("cancel-install");
        using var _ = http;

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, ct), [game.InstallPath]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(records.Find(game, ManagedComponentKind.REFramework));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")), "dinput8", Path.GetFileName(game.ExecutablePath)));
    }

    [Fact]
    public async Task Update_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (service, records, game, http) = CancelFixture("cancel-update");
        using var _ = http;
        Assert.True((await service.InstallAsync(game)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.UpdateAsync(game, ct), [game.InstallPath]);

        Assert.True(run.Result.Success, run.Result.Message);
        var record = records.Find(game, ManagedComponentKind.REFramework)!;
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.rhi-update-backup-*"));
        Assert.Equal(record.Files.Single().Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(record.Files.Single().Path))), ignoreCase: true);
    }

    private REFrameworkDeploymentService NewService(HttpClient http, string recordsName)
        => new(http,
            new LinuxDeploymentRecordStore(Path.Combine(_root, recordsName)),
            new WineDllOverrideService(),
            Path.Combine(_root, "cache-" + recordsName));

    private DetectedGame CreateGame()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "game.exe");
        File.WriteAllBytes(exe, MakePePayload(0x8664, 4096));
        var prefix = Path.Combine(dir, "pfx");
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n");
        return new DetectedGame
        {
            Name = "Street Fighter™ 6",
            InstallPath = dir,
            BinaryDirectory = dir,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Source = "Steam",
            Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX12
        };
    }

    private static string ReleaseJson(string tag, string url, string digest)
        => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            assets = new[]
            {
                new
                {
                    name = REFrameworkDeploymentService.MonolithicZipName,
                    browser_download_url = url,
                    digest = "sha256:" + digest
                }
            }
        });

    private static byte[] MakeFrameworkZip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var dll = zip.CreateEntry(REFrameworkDeploymentService.DllFileName, CompressionLevel.NoCompression);
            var payload = MakePePayload(0x8664, 300 * 1024);
            using (var stream = dll.Open())
                stream.Write(payload, 0, payload.Length);

            // ZipArchiveMode.Create allows only one entry stream to be open at a time.
            var readme = zip.CreateEntry("README.txt");
            using (var text = new StreamWriter(readme.Open(), Encoding.UTF8))
                text.Write("REFramework test package");
        }
        return output.ToArray();
    }

    private static byte[] MakePePayload(ushort machine, int size)
    {
        var bytes = new byte[Math.Max(size, 512)];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        for (var i = 0x100; i < bytes.Length; i++) bytes[i] = (byte)(i * 17 + 5);
        return bytes;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
