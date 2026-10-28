using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #249 items 3 and 10 (upstream 2.8.8 <c>e634e52</c>, <c>b8094e6</c>): an OptiScaler update keeps the variant the game
/// has (Stable, DLSS NR, F5 DLSSNR), asks the provider for that variant, reports and records the version it installed,
/// and writes one Update row in the update history. Upstream had to fix all of these; the rewrite never had the bugs, so
/// these tests pin today's behaviour.
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Theory]
    [InlineData(OptiScalerVariant.Stable)]
    [InlineData(OptiScalerVariant.DlssNr)]
    [InlineData(OptiScalerVariant.F5)]
    public async Task Pin288_Update_KeepsTheVariant_ReportsTheNewVersion_AndLogsOneHistoryRow(OptiScalerVariant variant)
    {
        var name = "pin288-update-" + variant;
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        var first = (variant == OptiScalerVariant.F5 ? CreateF5Package(name + "-1") : CreatePackage(name + "-1")) with { Version = "0.9.1" };
        var second = (variant == OptiScalerVariant.F5 ? CreateF5Package(name + "-2") : CreatePackage(name + "-2")) with { Version = "0.9.2" };
        File.AppendAllText(Path.Combine(second.RootDirectory, "OptiScaler", "libxess.dll"), " v2");
        var provider = new Pin288Provider(first);
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider, NrRuntime(name));
        Assert.True((await service.InstallAsync(game, variant)).Success);
        var log = new ComponentUpdateLogStore(Path.Combine(_root, name, "update-log.json"));
        var managed = new LinuxManagedComponentService(store, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: service, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            updateLog: log);
        provider.Package = second;
        provider.Asked.Clear();

        var check = await service.CheckUpdateAsync(game);
        var update = await managed.UpdateManagedAsync(game, ManagedComponentKind.OptiScaler, []);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.True(update.Success, update.Message); // the first attempt succeeds (upstream's F5 failed once first)
        Assert.All(provider.Asked, asked => Assert.Equal(variant, asked));
        Assert.NotEmpty(provider.Asked);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(variant, OptiScalerDeploymentService.GetInstalledVariant(record));
        Assert.Equal("0.9.2", record.Version);
        Assert.Contains($"OptiScaler {OptiScalerDeploymentService.VariantLabel(variant)} updated to 0.9.2", update.Message, StringComparison.Ordinal);
        Assert.False((await service.CheckUpdateAsync(game)).UpdateAvailable); // the version label is not left stale
        var row = Assert.Single(log.GetAll());
        Assert.Equal((ManagedComponentKind.OptiScaler, ComponentUpdateAction.Update, true, "0.9.1", "0.9.2"),
            (row.Component, row.Action, row.Success, row.FromVersion, row.ToVersion));
    }

    /// <summary>The latest package per variant, recording every variant it was asked for.</summary>
    private sealed class Pin288Provider(OptiScalerPackage package) : IOptiScalerPackageProvider
    {
        public OptiScalerPackage Package { get; set; } = package;
        public List<OptiScalerVariant> Asked { get; } = [];

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => GetLatestAsync(OptiScalerVariant.Stable, cancellationToken);

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
        {
            Asked.Add(variant);
            return Task.FromResult(Package with { Variant = variant });
        }
    }
}
