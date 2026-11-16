using RHI.Core.Services;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #270 (upstream 2.9.2 Release setting): a game pinned to a release of Stable, DLSS NR or F5 installs that release, is
/// reported current by the update check and is never moved by Update / Update All.
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    private sealed class ReleaseProvider(Func<OptiScalerVariant, string, OptiScalerPackage> release, OptiScalerPackage latest) : IOptiScalerPackageProvider
    {
        public List<(OptiScalerVariant Variant, string Tag)> Pinned { get; } = [];
        public int LatestCalls { get; private set; }

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => GetLatestAsync(OptiScalerVariant.Stable, cancellationToken);

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
        {
            LatestCalls++;
            return Task.FromResult(latest with { Variant = variant });
        }

        public Task<OptiScalerPackage> GetReleaseAsync(OptiScalerVariant variant, string tag, CancellationToken cancellationToken = default)
        {
            Pinned.Add((variant, tag));
            return Task.FromResult(release(variant, tag) with { Variant = variant });
        }
    }

    [Theory]
    [InlineData("Stable", "v0.9.3", "0.9.3")]
    [InlineData("F5", "v0.1.29", "0.1.29")]
    public async Task InstallPinnedRelease_InstallsThatRelease_IsCurrent_AndUpdateLeavesIt(OptiScalerVariant variant, string tag, string version)
    {
        var (game, store) = CreateGame("release-pin-" + variant, GraphicsApi.DirectX12);
        var old = (variant == OptiScalerVariant.F5 ? CreateF5Package("release-pin-old-" + variant) : CreatePackage("release-pin-old-" + variant)) with { Version = version };
        var newest = (variant == OptiScalerVariant.F5 ? CreateF5Package("release-pin-new-" + variant) : CreatePackage("release-pin-new-" + variant)) with { Version = "9.9.9" };
        var provider = new ReleaseProvider((_, _) => old, newest);
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider,
            variant == OptiScalerVariant.F5 ? NrRuntime("release-pin-" + variant) : null);

        var installed = await service.InstallAsync(game, variant, nightlyBuild: null, proxyName: null, releaseTag: tag);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal([(variant, tag)], provider.Pinned);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(version, record.Version);
        Assert.Equal(tag, OptiScalerDeploymentService.GetPinnedRelease(record));

        var calls = provider.LatestCalls;
        var check = await service.CheckUpdateAsync(game);
        Assert.False(check.UpdateAvailable, check.Message);
        Assert.False(check.CouldNotCheck);
        Assert.Equal(OptiScalerDeploymentService.ReleasePinnedCurrentPrefix + tag, check.Message);
        var update = await service.UpdateAsync(game);
        Assert.True(update.Success, update.Message);
        Assert.Equal(OptiScalerDeploymentService.ReleasePinnedNotUpdatedPrefix + tag, update.Message);
        Assert.Equal(calls, provider.LatestCalls);
        Assert.Equal(version, store.Find(game, ManagedComponentKind.OptiScaler)!.Version);
    }

    [Fact]
    public async Task InstallPinnedRelease_RefusesNightlyAnUnsafeTagAndBothPinsAtOnce()
    {
        var (game, store) = CreateGame("release-pin-refused", GraphicsApi.DirectX12);
        var provider = new ReleaseProvider((_, _) => CreatePackage("release-pin-refused-old"), CreatePackage("release-pin-refused-new"));
        var service = NewService(store, provider);

        var nightly = await service.InstallAsync(game, OptiScalerVariant.Nightly, nightlyBuild: null, proxyName: null, releaseTag: "v1.0");
        var unsafeTag = await service.InstallAsync(game, OptiScalerVariant.Stable, nightlyBuild: null, proxyName: null, releaseTag: "../v1");

        Assert.Equal(OptiScalerDeploymentService.ReleasePinWrongChannelMessage, nightly.Message);
        Assert.StartsWith(OptiScalerDeploymentService.ReleaseTagInvalidPrefix, unsafeTag.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Pinned);
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }
}
