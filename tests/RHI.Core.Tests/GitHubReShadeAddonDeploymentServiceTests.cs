using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class GitHubReShadeAddonDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-addon-tests-" + Guid.NewGuid().ToString("N"));

    public GitHubReShadeAddonDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ResolveLatest_UsesArchitectureSpecificAssetAndBodyVersion()
    {
        var asset = MakePePayload(600 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant();
        var json = $$"""
        {
          "tag_name": "latest",
          "name": "Latest",
          "body": "**Version in binaries**: 0.15.9.3468",
          "assets": [
            { "name": "zzz_display_commander.addon32", "browser_download_url": "https://github.com/rhi-tests/dc32", "digest": "sha256:{{digest}}" },
            { "name": "zzz_display_commander.addon64", "browser_download_url": "https://github.com/rhi-tests/dc64", "digest": "sha256:{{digest}}" }
          ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var service = NewDisplayCommander(http, store);

        var release = await service.ResolveLatestAsync(PeArchitecture.X64);

        Assert.Equal("0.15.9.3468", release.Version);
        Assert.Equal("zzz_display_commander.addon64", release.AssetName);
        Assert.Equal("https://github.com/rhi-tests/dc64", release.AssetUrl);
        Assert.Equal(digest, release.Sha256);
    }

    [Fact]
    public async Task Install_UsesReShadeAddonPath_AndRemoveRollsBack()
    {
        var game = CreateGame();
        var addonDir = Path.Combine(game.BinaryDirectory!, "reshade-addons");
        Directory.CreateDirectory(addonDir);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "reshade.ini"), "[ADDON]\nAddonPath=.\\reshade-addons\n");
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest);

        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("api.github"))
                return JsonResponse(json);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var service = NewReLimiter(http, store);

        var install = await service.InstallAsync(game);
        var target = Path.Combine(addonDir, "relimiter.addon64");
        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(target));
        var config = Path.Combine(addonDir, "relimiter.ini");
        Assert.True(File.Exists(config));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "relimiter.ini")));
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReLimiter));

        var remove = await service.RemoveAsync(game);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(config));
        Assert.Null(store.Find(game, ManagedComponentKind.ReLimiter));
    }

    [Fact]
    public async Task Install_ProtectsExternalTarget()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "relimiter.addon64"), MakePePayload(150 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("network should not be used")));
        var service = NewReLimiter(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("External ReLimiter", result.Message);
    }


    [Fact]
    public async Task Install_ProtectsLegacySameFamilyAddon()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var legacy = Path.Combine(game.BinaryDirectory!, "ultra_limiter.addon64");
        File.WriteAllBytes(legacy, MakePePayload(150 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("network should not be used")));
        var service = NewReLimiter(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("External ReLimiter", result.Message);
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public async Task Install_BlocksMutuallyExclusiveManagedAddon()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.DisplayCommander,
            BinaryDirectory = game.BinaryDirectory!
        });
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("network should not be used")));
        var service = NewReLimiter(http, store);

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("Display Commander", result.Message);
    }

    [Fact]
    public async Task Remove_ProtectsManagedFileModifiedOutsideRhi()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var target = Path.Combine(game.BinaryDirectory!, "relimiter.addon64");
        var original = MakePePayload(150 * 1024);
        File.WriteAllBytes(target, original);
        var hash = Convert.ToHexString(SHA256.HashData(original));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReLimiter,
            BinaryDirectory = game.BinaryDirectory!,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = hash }]
        });
        File.WriteAllBytes(target, MakePePayload(151 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("network should not be used")));
        var service = NewReLimiter(http, store);

        var result = await service.RemoveAsync(game);

        Assert.False(result.Success);
        Assert.True(File.Exists(target));
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReLimiter));
    }

    [Fact]
    public async Task Install_ReLimiter_CreatesDefaultIniBesideAddon()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "relimiter-ini-records.json"));
        var service = NewReLimiter(http, store);

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        var ini = Path.Combine(game.BinaryDirectory!, "relimiter.ini");
        Assert.True(File.Exists(ini));
        Assert.Equal("[UltraLimiter]\n", File.ReadAllText(ini));
        var record = store.Find(game, ManagedComponentKind.ReLimiter);
        Assert.NotNull(record);
        Assert.Equal("true", record!.Metadata["Auxiliary.ConfigCreatedByRhi"]);
        Assert.Equal(ini, record.Metadata["Auxiliary.ConfigPath"]);
    }

    [Fact]
    public async Task Install_DisplayCommander_CreatesDefaultIniBesideAddon()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(600 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v0.15.9.3468", "zzz_display_commander.addon64", "https://github.com/rhi-tests/dc", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "dc-ini-records.json"));
        var service = NewDisplayCommander(http, store);

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        var ini = Path.Combine(game.BinaryDirectory!, "DisplayCommander.ini");
        Assert.True(File.Exists(ini));
        Assert.Equal("[DisplayCommander]\n", File.ReadAllText(ini));
        Assert.Equal("true", store.Find(game, ManagedComponentKind.DisplayCommander)!.Metadata["Auxiliary.ConfigCreatedByRhi"]);
    }

    [Fact]
    public async Task Install_ReLimiter_PreservesExistingIniByteForByte()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var ini = Path.Combine(game.BinaryDirectory!, "relimiter.ini");
        var custom = "[UltraLimiter]\nFPSLimit=117\nUserToken=keep-me\n";
        File.WriteAllText(ini, custom);
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "relimiter-existing-ini-records.json"));
        var service = NewReLimiter(http, store);

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(custom, File.ReadAllText(ini));
        Assert.Equal("false", store.Find(game, ManagedComponentKind.ReLimiter)!.Metadata["Auxiliary.ConfigCreatedByRhi"]);
    }

    [Fact]
    public async Task Install_DisplayCommander_PreservesCaseInsensitiveExistingIni()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var existing = Path.Combine(game.BinaryDirectory!, "displaycommander.INI");
        var custom = "[DisplayCommander]\nRefreshRate=120\n";
        File.WriteAllText(existing, custom);
        var payload = MakePePayload(600 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v0.15.9.3468", "zzz_display_commander.addon64", "https://github.com/rhi-tests/dc", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "dc-existing-ini-records.json"));
        var service = NewDisplayCommander(http, store);

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(custom, File.ReadAllText(existing));
        Assert.Single(Directory.EnumerateFiles(game.BinaryDirectory!, "*", SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).Equals("DisplayCommander.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(existing, store.Find(game, ManagedComponentKind.DisplayCommander)!.Metadata["Auxiliary.ConfigPath"]);
    }

    [Fact]
    public async Task Remove_PreservesRhiInitializedAuxiliaryConfig()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "relimiter-remove-ini-records.json"));
        var service = NewReLimiter(http, store);
        Assert.True((await service.InstallAsync(game)).Success);
        var ini = Path.Combine(game.BinaryDirectory!, "relimiter.ini");

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.True(File.Exists(ini));
        Assert.Equal("[UltraLimiter]\n", File.ReadAllText(ini));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "relimiter.addon64")));
    }

    [Fact]
    public async Task Reinstall_ReLimiter_DoesNotOverwriteUserEditedInitializedIni()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "relimiter-reinstall-ini-records.json"));
        var service = NewReLimiter(http, store);
        Assert.True((await service.InstallAsync(game)).Success);
        var ini = Path.Combine(game.BinaryDirectory!, "relimiter.ini");
        var custom = "[UltraLimiter]\nFPSLimit=141\nPreset=UserOwned\n";
        File.WriteAllText(ini, custom);

        var reinstall = await service.InstallAsync(game);

        Assert.True(reinstall.Success, reinstall.Message);
        Assert.Equal(custom, File.ReadAllText(ini));
        Assert.Equal("false", store.Find(game, ManagedComponentKind.ReLimiter)!.Metadata["Auxiliary.ConfigCreatedByRhi"]);
    }

    [Fact]
    public void ComponentProbe_DetectsDisplayCommanderUnderscoreFilename()
    {
        var game = new DetectedGame { Name = "x", InstallPath = _root, Source = "Steam", Runner = "Proton" };
        var path = Path.Combine(_root, "zzz_display_commander.addon64");
        File.WriteAllText(path, "x");

        var components = ComponentProbe.Probe(game, [path]);

        Assert.Equal(ComponentAvailability.Installed,
            components.Single(c => c.Kind == ManagedComponentKind.DisplayCommander).Availability);
    }

    [Theory]
    [InlineData("relimiter.ini")]
    [InlineData("ReLimiter.log")]
    [InlineData("relimiter.json")]
    public void ComponentProbe_DoesNotTreatReLimiterSupportFilesAsInstalledAddon(string fileName)
    {
        var game = new DetectedGame { Name = "x", InstallPath = _root, Source = "Steam", Runner = "Proton" };
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "x");

        var components = ComponentProbe.Probe(game, [path]);

        Assert.Equal(ComponentAvailability.Available,
            components.Single(c => c.Kind == ManagedComponentKind.ReLimiter).Availability);
    }

    [Theory]
    [InlineData("DisplayCommander.ini")]
    [InlineData("display_commander.log")]
    [InlineData("displaycommander.json")]
    public void ComponentProbe_DoesNotTreatDisplayCommanderSupportFilesAsInstalledAddon(string fileName)
    {
        var game = new DetectedGame { Name = "x", InstallPath = _root, Source = "Steam", Runner = "Proton" };
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "x");

        var components = ComponentProbe.Probe(game, [path]);

        Assert.Equal(ComponentAvailability.Available,
            components.Single(c => c.Kind == ManagedComponentKind.DisplayCommander).Availability);
    }

    [Fact]
    public async Task ResolveLatest_DynamicFilenamePattern_SelectsMatchingAsset()
    {
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v1.4.2", "relimiter-v1.4.2-win64.addon64", "https://github.com/rhi-tests/dynamic", digest);
        using var http = new HttpClient(new FakeHandler(_ => JsonResponse(json)));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "dynamic-records.json"));
        var service = NewDynamicReLimiter(http, store);

        var release = await service.ResolveLatestAsync(PeArchitecture.X64);

        Assert.Equal("1.4.2", release.Version);
        Assert.Equal("relimiter-v1.4.2-win64.addon64", release.AssetName);
        Assert.Equal("https://github.com/rhi-tests/dynamic", release.AssetUrl);
    }

    [Fact]
    public async Task ResolveLatest_ReleaseArray_SkipsReleaseWithoutMatchingAsset()
    {
        var json = """
        [
          {
            "tag_name": "v2.0.0",
            "draft": false,
            "assets": [
              { "name": "source.zip", "browser_download_url": "https://github.com/rhi-tests/source" }
            ]
          },
          {
            "tag_name": "v1.9.0",
            "draft": false,
            "assets": [
              { "name": "relimiter-v1.9.0-win64.addon64", "browser_download_url": "https://github.com/rhi-tests/addon" }
            ]
          }
        ]
        """;
        using var http = new HttpClient(new FakeHandler(_ => JsonResponse(json)));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "array-records.json"));
        var service = NewDynamicReLimiter(http, store, "https://api.github.com/repos/example/releases?per_page=100");

        var release = await service.ResolveLatestAsync(PeArchitecture.X64);

        Assert.Equal("1.9.0", release.Version);
        Assert.Equal("relimiter-v1.9.0-win64.addon64", release.AssetName);
    }

    [Fact]
    public async Task Install_DynamicAsset_UsesStableDeploymentFilename()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v1.4.2", "relimiter-v1.4.2-win64.addon64", "https://github.com/rhi-tests/dynamic", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "dynamic-install-records.json"));
        var service = NewDynamicReLimiter(http, store);

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        var stableTarget = Path.Combine(game.BinaryDirectory!, "relimiter.addon64");
        Assert.True(File.Exists(stableTarget));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "relimiter-v1.4.2-win64.addon64")));
        var record = store.Find(game, ManagedComponentKind.ReLimiter);
        Assert.NotNull(record);
        Assert.Equal("relimiter-v1.4.2-win64.addon64", record!.Metadata["GitHub.AssetName"]);
        Assert.Equal("relimiter.addon64", record.Metadata["GitHub.DeployFileName"]);
    }

    [Fact]
    public async Task Install_DynamicZipAsset_ExtractsMatchingArchitecturePayload()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var addon64 = MakePePayload(96 * 1024);
        var addon32 = MakePePayload(80 * 1024);
        var zip = MakeZipPayload(
            ("DLSS5-Feeder/addon64/dlss5-feed.addon64", addon64),
            ("DLSS5-Feeder/addon32/dlss5-feed.addon32", addon32));
        var digest = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var json = ReleaseJson("v0.13.1-beta.1", "DLSS5-Feeder-0.13.1-beta.1.zip", "https://github.com/rhi-tests/feeder.zip", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "feeder-records.json"));
        var service = new GitHubReShadeAddonDeploymentService(http, store, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.Dlss5Feeder,
            "DLSS5 Feeder",
            "https://api.github.com/repos/feeder/releases/latest",
            null,
            null,
            64 * 1024,
            AssetNamePattern64: @"^DLSS5-Feeder-.*\.zip$",
            AssetNamePattern32: @"^DLSS5-Feeder-.*\.zip$",
            DeployFileName64: "dlss5-feed.addon64",
            DeployFileName32: "dlss5-feed.addon32"),
            Path.Combine(_root, "feeder-cache"));

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        var target = Path.Combine(game.BinaryDirectory!, "dlss5-feed.addon64");
        Assert.True(File.Exists(target));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(addon64)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dlss5-feed.addon32")));
        var record = store.Find(game, ManagedComponentKind.Dlss5Feeder);
        Assert.NotNull(record);
        Assert.Equal("DLSS5-Feeder-0.13.1-beta.1.zip", record!.Metadata["GitHub.AssetName"]);
        Assert.Equal("dlss5-feed.addon64", record.Metadata["GitHub.DeployFileName"]);
    }

    [Theory]
    [InlineData("renodx-mfgunlock.addon64", ManagedComponentKind.MfgAdaUnlock)]
    [InlineData("dlss5-feed.addon64", ManagedComponentKind.Dlss5Feeder)]
    [InlineData("dlss5-bridge.addon64", ManagedComponentKind.Dlss5Dx11Bridge)]
    public void ComponentProbe_DetectsRhi259PickerAddon(string fileName, ManagedComponentKind kind)
    {
        var game = new DetectedGame { Name = "x", InstallPath = _root, Source = "Steam", Runner = "Proton" };
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "x");

        var components = ComponentProbe.Probe(game, [path]);

        Assert.Equal(ComponentAvailability.Installed, components.Single(c => c.Kind == kind).Availability);
    }

    [Theory]
    [InlineData("dlss5-feed.cfg", ManagedComponentKind.Dlss5Feeder)]
    [InlineData("dlss5-feed.log", ManagedComponentKind.Dlss5Feeder)]
    [InlineData("dlss5-bridge.cfg", ManagedComponentKind.Dlss5Dx11Bridge)]
    [InlineData("mfgunlock.log", ManagedComponentKind.MfgAdaUnlock)]
    public void ComponentProbe_DoesNotTreatPickerAddonSupportFileAsAddon(string fileName, ManagedComponentKind kind)
    {
        var game = new DetectedGame { Name = "x", InstallPath = _root, Source = "Steam", Runner = "Proton" };
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "x");

        var components = ComponentProbe.Probe(game, [path]);

        Assert.Equal(ComponentAvailability.Available, components.Single(c => c.Kind == kind).Availability);
    }

    // ---- #142: the add-on cache is only reused when RHI stamped the file at download time ----

    [Fact]
    public async Task Install_ForeignFileInCache_IsNotDeployed_AndIsReplacedByDownload()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var genuine = MakePePayload(150 * 1024);
        var foreign = MakeForeignPePayload(160 * 1024);
        var cached = Path.Combine(CacheDir, "relimiter", "3.3.5-relimiter.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllBytes(cached, foreign);
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", Sha(genuine).ToLowerInvariant());
        var downloads = 0;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)) return JsonResponse(json);
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(genuine) };
        }));
        var service = NewReLimiter(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "foreign-records.json")));

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, downloads);
        Assert.Equal(Sha(genuine), Sha(File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "relimiter.addon64"))));
        Assert.Equal(Sha(genuine), Sha(File.ReadAllBytes(cached)));
        Assert.True(CacheStamp.IsValid(cached, "https://github.com/rhi-tests/relimiter", "3.3.5"));
    }

    [Fact]
    public async Task Install_ForeignFileInCache_DownloadFails_NothingIsDeployed()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var foreign = MakeForeignPePayload(160 * 1024);
        var cached = Path.Combine(CacheDir, "relimiter", "3.3.5-relimiter.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllBytes(cached, foreign);
        // No published digest, so nothing but the stamp could vouch for the cached file.
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest: null);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "foreign-offline-records.json"));
        var service = NewReLimiter(http, store);

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "relimiter.addon64")));
        Assert.Null(store.Find(game, ManagedComponentKind.ReLimiter));
    }

    [Fact]
    public async Task Install_ForgedStampOverForeignFile_IsNotTrusted()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var genuine = MakePePayload(150 * 1024);
        var cached = Path.Combine(CacheDir, "relimiter", "3.3.5-relimiter.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        // A stamp for the genuine bytes, then the file swapped for a foreign one of the same size.
        File.WriteAllBytes(cached, genuine);
        CacheStamp.Write(cached, "https://github.com/rhi-tests/relimiter", "3.3.5");
        File.WriteAllBytes(cached, MakeForeignPePayload(genuine.Length));
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest: null);
        var downloads = 0;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)) return JsonResponse(json);
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(genuine) };
        }));
        var service = NewReLimiter(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "forged-records.json")));

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, downloads);
        Assert.Equal(Sha(genuine), Sha(File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "relimiter.addon64"))));
    }

    [Fact]
    public async Task Install_StampedCachedFile_IsReusedWithoutDownloading()
    {
        var genuine = MakePePayload(150 * 1024);
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", Sha(genuine).ToLowerInvariant());
        var downloads = 0;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)) return JsonResponse(json);
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(genuine) };
        }));

        var first = CreateGame();
        WriteFakeReShade(Path.Combine(first.BinaryDirectory!, "dxgi.dll"));
        var service = NewReLimiter(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "reuse-records.json")));
        Assert.True((await service.InstallAsync(first)).Success);
        Assert.Equal(1, downloads);
        var cached = Path.Combine(CacheDir, "relimiter", "3.3.5-relimiter.addon64");
        Assert.True(File.Exists(CacheStamp.StampPath(cached)));

        var second = CreateGame();
        WriteFakeReShade(Path.Combine(second.BinaryDirectory!, "dxgi.dll"));
        var result = await service.InstallAsync(second);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, downloads);
        Assert.Equal(Sha(genuine), Sha(File.ReadAllBytes(Path.Combine(second.BinaryDirectory!, "relimiter.addon64"))));
    }

    [Fact]
    public async Task Install_StampedCacheForOtherRelease_IsNotReused()
    {
        var oldPayload = MakePePayload(150 * 1024);
        var newPayload = MakeForeignPePayload(150 * 1024);
        var url = "https://github.com/rhi-tests/relimiter";
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", url, Sha(oldPayload).ToLowerInvariant());
        var current = oldPayload;
        var downloads = 0;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)) return JsonResponse(json);
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(current) };
        }));
        var service = NewReLimiter(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "republish-records.json")));
        var first = CreateGame();
        WriteFakeReShade(Path.Combine(first.BinaryDirectory!, "dxgi.dll"));
        Assert.True((await service.InstallAsync(first)).Success);

        // Same tag and file name re-published with a new digest: the stamped copy is stale.
        current = newPayload;
        json = ReleaseJson("v3.3.5", "relimiter.addon64", url, Sha(newPayload).ToLowerInvariant());
        var second = CreateGame();
        WriteFakeReShade(Path.Combine(second.BinaryDirectory!, "dxgi.dll"));
        var result = await service.InstallAsync(second);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, downloads);
        Assert.Equal(Sha(newPayload), Sha(File.ReadAllBytes(Path.Combine(second.BinaryDirectory!, "relimiter.addon64"))));
    }

    [Fact]
    public async Task Install_ZipAsset_StampsExtractedPayload_AndRejectsForeignExtractedFile()
    {
        var addon64 = MakePePayload(96 * 1024);
        var zip = MakeZipPayload(("DLSS5-Feeder/addon64/dlss5-feed.addon64", addon64));
        var json = ReleaseJson("v0.13.1", "DLSS5-Feeder-0.13.1.zip", "https://github.com/rhi-tests/feeder.zip", Sha(zip).ToLowerInvariant());
        var downloads = 0;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)) return JsonResponse(json);
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        }));
        var cacheDir = Path.Combine(_root, "feeder-stamp-cache");
        var extracted = Path.Combine(cacheDir, "0.13.1-dlss5-feed.addon64");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(extracted, MakeForeignPePayload(96 * 1024));
        var service = new GitHubReShadeAddonDeploymentService(http,
            new LinuxDeploymentRecordStore(Path.Combine(_root, "feeder-stamp-records.json")),
            new GitHubReShadeAddonDefinition(
                ManagedComponentKind.Dlss5Feeder, "DLSS5 Feeder", "https://api.github.com/repos/feeder/releases/latest",
                null, null, 64 * 1024,
                AssetNamePattern64: @"^DLSS5-Feeder-.*\.zip$", AssetNamePattern32: @"^DLSS5-Feeder-.*\.zip$",
                DeployFileName64: "dlss5-feed.addon64", DeployFileName32: "dlss5-feed.addon32"),
            cacheDir);

        var first = CreateGame();
        WriteFakeReShade(Path.Combine(first.BinaryDirectory!, "dxgi.dll"));
        Assert.True((await service.InstallAsync(first)).Success);
        Assert.Equal(1, downloads);
        Assert.Equal(Sha(addon64), Sha(File.ReadAllBytes(Path.Combine(first.BinaryDirectory!, "dlss5-feed.addon64"))));

        var second = CreateGame();
        WriteFakeReShade(Path.Combine(second.BinaryDirectory!, "dxgi.dll"));
        Assert.True((await service.InstallAsync(second)).Success);
        Assert.Equal(1, downloads);
        Assert.Equal(Sha(addon64), Sha(File.ReadAllBytes(Path.Combine(second.BinaryDirectory!, "dlss5-feed.addon64"))));
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static byte[] MakeForeignPePayload(int size)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var i = 2; i < bytes.Length; i++) bytes[i] = (byte)(i * 17 + 3);
        return bytes;
    }

    private static byte[] MakeZipPayload(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return memory.ToArray();
    }

    private DetectedGame CreateGame()
    {
        var bin = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "game.exe");
        WritePeExecutable(exe, 0x8664);
        return new DetectedGame
        {
            Name = "Test Game",
            InstallPath = bin,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            Source = "Steam",
            Runner = "Proton",
            PrefixPath = Path.Combine(bin, "pfx")
        };
    }

    // Every service gets an explicit per-test cache: the default (LinuxPaths.CacheHome) is the real
    // user cache in production, and the add-on cache is now reused when stamped (#142).
    private string CacheDir => Path.Combine(_root, "addon-cache");

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    private (GitHubReShadeAddonDeploymentService Service, LinuxDeploymentRecordStore Store, DetectedGame Game, HttpClient Http) CancelFixture(string name)
    {
        var game = CreateGame();
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "reshade-addons"));
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "reshade.ini"), "[ADDON]\nAddonPath=.\\reshade-addons\n");
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest);
        var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri.Contains("api.github")
            ? JsonResponse(json)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, name + "-records.json"));
        return (NewReLimiter(http, store), store, game, http);
    }

    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (service, store, game, http) = CancelFixture("cancel-install");
        using var _ = http;

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, ct), [game.InstallPath]);

        // A late cancel used to throw after the copy and leave the add-on in the game folder with no record.
        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReLimiter));
    }

    [Fact]
    public async Task Remove_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        var (service, store, game, http) = CancelFixture("cancel-remove");
        using var _ = http;
        Assert.True((await service.InstallAsync(game)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(game, ct), [game.InstallPath]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.ReLimiter));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "reshade-addons", "relimiter.addon64")));
    }

    private GitHubReShadeAddonDeploymentService NewReLimiter(HttpClient http, LinuxDeploymentRecordStore store)
        => new(http, store, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.ReLimiter,
            "ReLimiter",
            "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest",
            "relimiter.addon64",
            "relimiter.addon32",
            100 * 1024,
            ManagedComponentKind.DisplayCommander),
            Path.Combine(CacheDir, "relimiter"));

    private GitHubReShadeAddonDeploymentService NewDynamicReLimiter(
        HttpClient http,
        LinuxDeploymentRecordStore store,
        string releaseApiUrl = "https://api.github.com/repos/example/releases/latest")
        => new(http, store, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.ReLimiter,
            "ReLimiter",
            releaseApiUrl,
            null,
            null,
            100 * 1024,
            ManagedComponentKind.DisplayCommander,
            AssetNamePattern64: @"^relimiter-.*-win64\.addon64$",
            AssetNamePattern32: @"^relimiter-.*-win32\.addon32$",
            DeployFileName64: "relimiter.addon64",
            DeployFileName32: "relimiter.addon32"),
            Path.Combine(CacheDir, "relimiter"));

    private GitHubReShadeAddonDeploymentService NewDisplayCommander(HttpClient http, LinuxDeploymentRecordStore store)
        => new(http, store, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.DisplayCommander,
            "Display Commander",
            "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest",
            "zzz_display_commander.addon64",
            "zzz_display_commander.addon32",
            500 * 1024,
            ManagedComponentKind.ReLimiter,
            @"Version in binaries\*\*:\s*([0-9.]+)"),
            Path.Combine(CacheDir, "displaycommander"));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ReleaseJson(string tag, string assetName, string url, string? digest)
        => digest is null
            ? JsonSerializer.Serialize(new
            {
                tag_name = tag,
                name = tag,
                body = "",
                assets = new[] { new { name = assetName, browser_download_url = url } }
            })
            : JsonSerializer.Serialize(new
            {
                tag_name = tag,
                name = tag,
                body = "",
                assets = new[] { new { name = assetName, browser_download_url = url, digest = "sha256:" + digest } }
            });

    private static byte[] MakePePayload(int size)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
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
