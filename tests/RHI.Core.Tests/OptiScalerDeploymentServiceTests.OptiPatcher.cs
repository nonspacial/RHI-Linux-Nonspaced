using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #98 (upstream 2.8.7 install step 7 + EnsureOptiPatcherStagingAsync): OptiPatcher goes with every OptiScaler install
// as plugins/OptiPatcher.asi with LoadAsiPlugins on; its download failing never fails OptiScaler; it is part of the
// OptiScaler record (removed with it, refreshed by its update, counted as an update when the rolling build moved on).
public sealed partial class OptiScalerDeploymentServiceTests
{
    private sealed class FakeOptiPatcher(string root) : IOptiPatcherProvider
    {
        public string Version { get; set; } = "c3f758a";
        public bool Fail { get; set; }
        /// <summary>Release lookups (metadata).</summary>
        public int Calls { get; private set; }
        /// <summary>Downloads of the .asi.</summary>
        public int Fetches { get; private set; }

        public Task<OptiPatcherRelease> ResolveLatestAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("GitHub is down (test)");
            return Task.FromResult(new OptiPatcherRelease(Version, "https://github.com/optiscaler/OptiPatcher/releases/download/rolling/OptiPatcher.asi", null));
        }

        public Task<OptiPatcherBuild> FetchAsync(OptiPatcherRelease release, CancellationToken cancellationToken = default)
        {
            Fetches++;
            var path = Path.Combine(root, release.Version, OptiPatcherProvider.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllBytes(path, OptiPatcherProviderTests.Pe64((byte)release.Version[0]));
            return Task.FromResult(new OptiPatcherBuild(release.Version, release.SourceUrl,
                new VerifiedDownload(path, FileHash.Sha256Hex(path), PublishedSha256: FileHash.Sha256Hex(path))));
        }
    }

    private OptiScalerDeploymentService NewServiceWithOptiPatcher(LinuxDeploymentRecordStore store, IOptiScalerPackageProvider provider, IOptiPatcherProvider optiPatcher)
        => new(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider, optiPatcher: optiPatcher);

    [Fact]
    public async Task Install_DeploysOptiPatcherToPlugins_TurnsLoadAsiPluginsOn_AndRemovalTakesItAway()
    {
        var (game, store) = CreateGame("optipatcher", GraphicsApi.DirectX12);
        var patcher = new FakeOptiPatcher(Path.Combine(_root, "optipatcher-cache"));
        var service = NewServiceWithOptiPatcher(store, new FakePackageProvider(CreatePackage("optipatcher-package")), patcher);
        var asi = Path.Combine(game.BinaryDirectory!, "plugins", "OptiPatcher.asi");

        var installed = await service.InstallAsync(game);

        Assert.True(installed.Success, installed.Message);
        Assert.EndsWith(OptiScalerDeploymentService.OptiPatcherDeployedPrefix + "c3f758a", installed.Message);
        Assert.True(File.Exists(asi));
        Assert.Contains("LoadAsiPlugins=true", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("c3f758a", record.Metadata[OptiScalerDeploymentService.OptiPatcherVersionMetadataKey]);
        Assert.Contains(record.Files, file => Path.GetFullPath(file.Path) == Path.GetFullPath(asi) && file.Sha256 == FileHash.Sha256Hex(asi));
        DownloadRecordAssert.Recorded(record, FileHash.Sha256Hex(asi), VerifiedDownload.PublishedDigest, OptiScalerDeploymentService.OptiPatcherDownloadPart);

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.False(File.Exists(asi));
        Assert.False(Directory.Exists(Path.Combine(game.BinaryDirectory!, "plugins")));
    }

    [Fact]
    public async Task Install_OptiPatcherUnavailable_InstallsOptiScalerAlone_AndSaysSo()
    {
        var (game, store) = CreateGame("optipatcher-down", GraphicsApi.DirectX12);
        var patcher = new FakeOptiPatcher(Path.Combine(_root, "optipatcher-down-cache")) { Fail = true };
        var service = NewServiceWithOptiPatcher(store, new FakePackageProvider(CreatePackage("optipatcher-down-package")), patcher);

        var installed = await service.InstallAsync(game);

        Assert.True(installed.Success, installed.Message);
        Assert.Contains(OptiScalerDeploymentService.OptiPatcherSkippedPrefix + "GitHub is down (test)", installed.Message);
        Assert.False(Directory.Exists(Path.Combine(game.BinaryDirectory!, "plugins")));
        Assert.DoesNotContain("LoadAsiPlugins", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
        Assert.False(store.Find(game, ManagedComponentKind.OptiScaler)!.Metadata.ContainsKey(OptiScalerDeploymentService.OptiPatcherVersionMetadataKey));
    }

    [Fact]
    public async Task Install_AForeignOptiPatcherInPlugins_IsLeftAsItIs()
    {
        var (game, store) = CreateGame("optipatcher-foreign", GraphicsApi.DirectX12);
        var asi = Path.Combine(game.BinaryDirectory!, "plugins", "OptiPatcher.asi");
        Directory.CreateDirectory(Path.GetDirectoryName(asi)!);
        File.WriteAllText(asi, "the user's own OptiPatcher");
        var service = NewServiceWithOptiPatcher(store, new FakePackageProvider(CreatePackage("optipatcher-foreign-package")),
            new FakeOptiPatcher(Path.Combine(_root, "optipatcher-foreign-cache")));

        var installed = await service.InstallAsync(game);

        Assert.True(installed.Success, installed.Message);
        Assert.Contains(OptiScalerDeploymentService.OptiPatcherSkippedPrefix + OptiScalerDeploymentService.OptiPatcherForeignMessage, installed.Message);
        Assert.Equal("the user's own OptiPatcher", File.ReadAllText(asi));
        Assert.DoesNotContain(store.Find(game, ManagedComponentKind.OptiScaler)!.Files, file => Path.GetFullPath(file.Path) == Path.GetFullPath(asi));
        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.Equal("the user's own OptiPatcher", File.ReadAllText(asi));
    }

    [Fact]
    public async Task UpdateCheck_ANewOptiPatcherBuild_IsAnUpdate_AndTheUpdateRefreshesIt()
    {
        var (game, store) = CreateGame("optipatcher-update", GraphicsApi.DirectX12);
        var patcher = new FakeOptiPatcher(Path.Combine(_root, "optipatcher-update-cache"));
        var service = NewServiceWithOptiPatcher(store, new FakePackageProvider(CreatePackage("optipatcher-update-package")), patcher);
        Assert.True((await service.InstallAsync(game)).Success);
        var asi = Path.Combine(game.BinaryDirectory!, "plugins", "OptiPatcher.asi");

        var current = await service.CheckUpdateAsync(game);
        Assert.False(current.UpdateAvailable, current.Message);

        patcher.Version = "4662cb0";
        var fetchesBefore = patcher.Fetches;
        var check = await service.CheckUpdateAsync(game);
        Assert.True(check.UpdateAvailable);
        Assert.Equal(OptiScalerDeploymentService.OptiPatcherUpdatePrefix + "c3f758a → 4662cb0", check.Message);
        // The check reads the build only; the new .asi is downloaded by the update the user approves (review minor 2).
        Assert.Equal(fetchesBefore, patcher.Fetches);
        Assert.False(Directory.Exists(Path.Combine(_root, "optipatcher-update-cache", "4662cb0")));

        var updated = await service.UpdateAsync(game);
        Assert.True(updated.Success, updated.Message);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_root, "optipatcher-update-cache", "4662cb0", "OptiPatcher.asi")), File.ReadAllBytes(asi));
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("4662cb0", record.Metadata[OptiScalerDeploymentService.OptiPatcherVersionMetadataKey]);
        Assert.Equal(FileHash.Sha256Hex(asi), record.Files.Single(file => Path.GetFullPath(file.Path) == Path.GetFullPath(asi)).Sha256);

        // GitHub unreachable for OptiPatcher alone: OptiScaler's own check still answers (never "could not check").
        patcher.Fail = true;
        var offline = await service.CheckUpdateAsync(game);
        Assert.False(offline.UpdateAvailable);
        Assert.False(offline.CouldNotCheck);
    }

    [Fact]
    public async Task UpdateCheck_WithTheRealProvider_ReadsTheReleaseOnly_NeverDownloadsTheNewAsi()
    {
        var (game, store) = CreateGame("optipatcher-check-real", GraphicsApi.DirectX12);
        var build = "c3f758a";
        var asi = OptiPatcherProviderTests.Pe64(0x41);
        var assetRequests = 0;
        using var http = new HttpClient(new OptiPatcherProviderTests.Fake(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
                return OptiPatcherProviderTests.Json(OptiPatcherProviderTests.Release($"**Build commit:** {build}",
                    "sha256:" + FileHash.Sha256HexOf(asi).ToLowerInvariant()));
            assetRequests++;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(asi) };
        }));
        var cache = Path.Combine(_root, "optipatcher-check-real-cache");
        var service = NewServiceWithOptiPatcher(store, new FakePackageProvider(CreatePackage("optipatcher-check-real-package")),
            new OptiPatcherProvider(http, cache));
        Assert.True((await service.InstallAsync(game)).Success);
        Assert.Equal(1, assetRequests);

        build = "4662cb0";
        asi = OptiPatcherProviderTests.Pe64(0x42);
        var check = await service.CheckUpdateAsync(game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal(1, assetRequests);
        Assert.False(Directory.Exists(Path.Combine(cache, "4662cb0")));
    }

    [Fact]
    public async Task Update_OfAnInstallWithoutOptiPatcher_DoesNotAddIt_AndAPinnedNightlyNeverAsksForIt()
    {
        var (game, store) = CreateGame("optipatcher-old", GraphicsApi.DirectX12);
        Assert.True((await NewService(store, CreatePackage("optipatcher-old-package")).InstallAsync(game)).Success);
        var patcher = new FakeOptiPatcher(Path.Combine(_root, "optipatcher-old-cache"));
        var service = NewServiceWithOptiPatcher(store, new FakePackageProvider(CreatePackage("optipatcher-old-package-2")), patcher);

        Assert.False((await service.CheckUpdateAsync(game)).UpdateAvailable);
        Assert.True((await service.UpdateAsync(game)).Success);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "plugins", "OptiPatcher.asi")));
        Assert.Equal(0, patcher.Calls);

        var (pinnedGame, pinnedStore) = CreateGame("optipatcher-pinned", GraphicsApi.DirectX12);
        var pinnedPatcher = new FakeOptiPatcher(Path.Combine(_root, "optipatcher-pinned-cache"));
        var pinned = NewServiceWithOptiPatcher(pinnedStore, NightlyPackages("optipatcher-pinned"), pinnedPatcher);
        Assert.True((await pinned.InstallPinnedNightlyAsync(pinnedGame, "20261004")).Success);
        Assert.Equal(1, pinnedPatcher.Calls);
        pinnedPatcher.Version = "4662cb0";
        var check = await pinned.CheckUpdateAsync(pinnedGame);
        Assert.False(check.UpdateAvailable);
        Assert.Equal(1, pinnedPatcher.Calls);
    }
}
