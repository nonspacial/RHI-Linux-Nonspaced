using RHI.Core.Models;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #156: a game pinned to a nightly build installs that build, records it, and stays on it — Update All sees it current,
/// never an update and never \"could not check\", and asks GitHub nothing for it.
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    private sealed class NightlyProvider(OptiScalerPackage latest, OptiScalerPackage pinned) : IOptiScalerPackageProvider
    {
        public List<string> Calls { get; } = [];
        public OptiScalerPackage Latest { get; set; } = latest;
        /// <summary>The package every pinned build is served from (#232 tests swap it to tell two builds apart).</summary>
        public OptiScalerPackage Pinned { get; set; } = pinned;
        /// <summary>Thrown by every request while set (a build that cannot be downloaded).</summary>
        public Exception? Failure { get; set; }

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => GetLatestAsync(OptiScalerVariant.Stable, cancellationToken);

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
        {
            Calls.Add("latest:" + variant);
            return Failure is { } failure ? Task.FromException<OptiScalerPackage>(failure) : Task.FromResult(Latest with { Variant = variant });
        }

        public Task<OptiScalerPackage> GetNightlyBuildAsync(string build, CancellationToken cancellationToken = default)
        {
            Calls.Add("build:" + build);
            return Failure is { } failure
                ? Task.FromException<OptiScalerPackage>(failure)
                : Task.FromResult(Pinned with { Variant = OptiScalerVariant.Nightly, Version = build });
        }

        public Task<IReadOnlyList<string>> ListNightlyBuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["20261012", "20261004"]);
    }

    private NightlyProvider NightlyPackages(string name)
        => new(CreatePackage(name + "-latest") with { Version = "20261012" }, CreatePackage(name + "-pinned"));

    [Fact]
    public async Task InstallPinnedNightly_InstallsThatBuild_AndUpdateAllLeavesItAlone()
    {
        var (game, store) = CreateGame("pinned", GraphicsApi.DirectX12);
        var provider = NightlyPackages("pinned");
        var service = NewService(store, provider);

        var installed = await service.InstallPinnedNightlyAsync(game, "20261004");
        Assert.True(installed.Success, installed.Message);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("20261004", record.Version);
        Assert.Equal("20261004", OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.Equal(["build:20261004"], provider.Calls);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var hash = await FileHash.Sha256HexAsync(dxgi);

        var check = await service.CheckUpdateAsync(game);
        var update = await service.UpdateAsync(game);

        Assert.True(check.Managed);
        Assert.False(check.UpdateAvailable);
        Assert.False(check.CouldNotCheck);
        Assert.StartsWith(OptiScalerDeploymentService.NightlyPinnedCurrentPrefix, check.Message, StringComparison.Ordinal);
        Assert.True(update.Success, update.Message);
        Assert.StartsWith(OptiScalerDeploymentService.NightlyPinnedNotUpdatedPrefix, update.Message, StringComparison.Ordinal);
        Assert.Equal(["build:20261004"], provider.Calls); // neither the check nor the update asked for another build
        Assert.Equal(hash, await FileHash.Sha256HexAsync(dxgi));
        Assert.Equal("20261004", store.Find(game, ManagedComponentKind.OptiScaler)!.Version);
    }

    [Fact]
    public async Task LatestNightly_StillAutoUpdates()
    {
        var (game, store) = CreateGame("latest-nightly", GraphicsApi.DirectX12);
        var provider = NightlyPackages("latest-nightly");
        var service = NewService(store, provider);
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Nightly, nightlyBuild: null)).Success);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Null(OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        record.Version = "20261001";
        store.Upsert(record);

        var check = await service.CheckUpdateAsync(game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal("20261012", check.LatestVersion);
    }

    [Theory]
    [InlineData(OptiScalerVariant.Stable, "20261004")]
    [InlineData(OptiScalerVariant.DlssNr, "20261004")]
    [InlineData(OptiScalerVariant.Nightly, "2026-10-04")]
    [InlineData(OptiScalerVariant.Nightly, "../x")]
    public async Task Install_RefusesAPinThatIsNotANightlyBuild_BeforeAnyChange(OptiScalerVariant variant, string build)
    {
        var (game, store) = CreateGame("bad-pin-" + variant + build.GetHashCode().ToString("x"), GraphicsApi.DirectX12);
        var provider = NightlyPackages("bad-pin-" + variant + build.GetHashCode().ToString("x"));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game, variant, build);

        Assert.False(result.Success);
        Assert.Empty(provider.Calls);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    /// <summary>The managed service installs Nightly with the build the user chose in the ⚙ dialog (saved per game).</summary>
    [Fact]
    public async Task ManagedInstall_TakesTheSavedNightlyPin_AndOnlyForNightly()
    {
        var (game, store) = CreateGame("saved-pin", GraphicsApi.DirectX12);
        var provider = NightlyPackages("saved-pin");
        var pins = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "saved-pin", "pins.json"));
        var managed = new LinuxManagedComponentService(store, null!, null!, null!, null!, NewService(store, provider), null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            neuralVersionPins: pins, updateLog: new ComponentUpdateLogStore(Path.Combine(_root, "saved-pin", "log.json")));
        game.InstallPath = Path.GetDirectoryName(game.BinaryDirectory)!;

        managed.SetOptiScalerNightlyBuild(game, "20261004");
        Assert.Equal("20261004", managed.GetOptiScalerNightlyBuild(game));
        var result = await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Nightly);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["build:20261004"], provider.Calls);
        Assert.Equal("20261004", managed.GetOptiScalerNightlyBuild(game)); // now read from the record
        Assert.Throws<ArgumentException>(() => managed.SetOptiScalerNightlyBuild(game, "latest"));

        // Update All (manual and background) reports the pinned game current, not updated, failed or unchecked.
        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var run = await new LinuxUpdateAllService(store, managed, new NotRunning()).RunAsync([game], _ => [], mode);
            var item = Assert.Single(run.Items);
            Assert.True(item.Success && !item.Changed && !item.Unchecked && !item.Deferred, item.Message);
            Assert.StartsWith(OptiScalerDeploymentService.NightlyPinnedCurrentPrefix, item.Message, StringComparison.Ordinal);
        }
        Assert.Equal(["build:20261004"], provider.Calls);
    }

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }
}
