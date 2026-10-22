using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#229: a ShortFuse install with no NR pin places "Latest" made for the card, never a DLL for another generation.</summary>
public sealed partial class ShortFuseDeploymentServiceTests
{
    private const string NrFor50Url = "https://github.com/example/rhi/releases/download/1/nr-50xx.zip";

    /// <summary>The full fixture's manifest with a newer 50xx-only NR entry listed first (the live manifest's shape).</summary>
    private sealed class Nr50xxFirst(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == NrFor50Url)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CreateZip(("nvngx_dlssnr.dll", CreatePe(0x8664, 80 * 1024, 0x7A)))) };
            var response = await base.SendAsync(request, cancellationToken);
            if (url != DlssManagerService.ManifestUrl) return response;
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            json = json.Replace("\"dlssnr\":[{\"version\":\"9.0\"", $"\"dlssnr\":[{{\"version\":\"9.1 (50xx)\",\"url\":\"{NrFor50Url}\"}},{{\"version\":\"9.0 (20/30/40/50)\"", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
        }
    }

    [Theory]
    [InlineData(RtxGeneration.Rtx40, 0x74)]
    [InlineData(RtxGeneration.Rtx50, 0x7A)]
    [InlineData(RtxGeneration.Unknown, 0x7A)]
    public async Task NrGpu_InstallWithoutPin_PlacesLatestForThisCard(RtxGeneration card, int marker)
    {
        using var fixture = CreateGame("nrgpu-" + card);
        var ctx = CreateContext(fixture, fullNetwork: true, wrapHandler: inner => new Nr50xxFirst(inner));
        ctx.ShortFuse.Dlss.CardGeneration = () => card;
        AddManagedReShade(ctx.Records, fixture);

        var install = await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: false);

        Assert.True(install.Success, install.Message);
        Assert.Equal(CreatePe(0x8664, 80 * 1024, (byte)marker), File.ReadAllBytes(Path.Combine(fixture.Bin, "nvngx_dlssnr.dll")));
    }
}

/// <summary>#230: Update All moves a "Latest" ShortFuse NR install onto the DLL made for the card; a pin stays.</summary>
public sealed partial class ShortFuseDeploymentServiceTests
{
    private const string Nr50 = "9.1 (50xx)";
    private const string NrAll = "9.0 (20/30/40/50)";

    private static RHI.Platform.Linux.Deployment.LinuxManagedComponentService NrManaged(Context ctx, string root, RtxGeneration card,
        out RHI.Platform.Linux.Paths.NeuralAddonVersionPreferenceStore pins, out RHI.Platform.Linux.Deployment.ComponentUpdateLogStore log)
    {
        pins = new RHI.Platform.Linux.Paths.NeuralAddonVersionPreferenceStore(Path.Combine(root, "pins.json"));
        log = new RHI.Platform.Linux.Deployment.ComponentUpdateLogStore(Path.Combine(root, "update-log.json"));
        var managed = new RHI.Platform.Linux.Deployment.LinuxManagedComponentService(ctx.Records, reshade: null!, addonManager: null!, renodx: null!,
            renodxUpgrade: null!, optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!,
            shortFuse: ctx.ShortFuse, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!, neuralVersionPins: pins, updateLog: log);
        managed.NvidiaGpu = () => NrGpuTestKit.Facts(card);
        return managed;
    }

    /// <summary>ShortFuse installed while RHI took the first NR entry (9.1 (50xx)); the record from before #230 (no NR version).</summary>
    private async Task<(GameFixture Fixture, Context Ctx)> LegacyShortFuseOn50xxAsync(string name)
    {
        var fixture = CreateGame(name);
        var ctx = CreateContext(fixture, fullNetwork: true, wrapHandler: inner => new Nr50xxFirst(inner));
        AddManagedReShade(ctx.Records, fixture);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: false)).Success);
        var record = ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse)!;
        Assert.Equal(Nr50, record.Metadata[RHI.Platform.Linux.Deployment.NeuralRenderingDllSwap.MetaDllVersion]);
        record.Metadata.Remove(RHI.Platform.Linux.Deployment.NeuralRenderingDllSwap.MetaDllVersion);
        ctx.Records.Upsert(record);
        return (fixture, ctx);
    }

    [Fact]
    public async Task NrMove_LatestInstallOnAnRtx40_IsMovedByUpdateAll_WithAHistoryRow()
    {
        var (fixture, ctx) = await LegacyShortFuseOn50xxAsync("nrmove");
        using var disposeFixture = fixture;
        var managed = NrManaged(ctx, fixture.Root, RtxGeneration.Rtx40, out _, out var log);
        var addon = Path.Combine(RenoDxDeploymentService.ResolveAddonDeployPath(fixture.Bin), ShortFuseDeploymentService.AddonFileName);
        var addonBefore = File.ReadAllBytes(addon);

        var all = await new RHI.Platform.Linux.Deployment.LinuxUpdateAllService(ctx.Records, managed, new NrGpuTestKit.NeverRunning())
            .RunAsync([fixture.Game], _ => [], RHI.Platform.Linux.Deployment.LinuxUpdateRunMode.Manual);

        var item = Assert.Single(all.Items, item => item.Component == ManagedComponentKind.ShortFuse); // (the fixture's ReShade is not under test)
        Assert.True(item is { Success: true, Changed: true }, item.Message);
        Assert.Equal(CreatePe(0x8664, 80 * 1024, 0x74), File.ReadAllBytes(Path.Combine(fixture.Bin, "nvngx_dlssnr.dll")));
        Assert.Equal(addonBefore, File.ReadAllBytes(addon));
        Assert.Equal(NrAll, ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse)!.Metadata[RHI.Platform.Linux.Deployment.NeuralRenderingDllSwap.MetaDllVersion]);
        var entry = Assert.Single(log.GetAll(), entry => entry.Component == ManagedComponentKind.ShortFuse);
        Assert.Equal((ManagedComponentKind.ShortFuse, (string?)null, NrAll), (entry.Component, entry.FromVersion, entry.ToVersion));
    }

    [Theory]
    [InlineData(RtxGeneration.Rtx40, true)]   // hand-picked for another card: kept
    [InlineData(RtxGeneration.Unknown, false)] // unknown card: nothing moves because of the card
    public async Task NrMove_APinOrAnUnknownCard_LeavesTheDll(RtxGeneration card, bool pinned)
    {
        var (fixture, ctx) = await LegacyShortFuseOn50xxAsync("nrkeep-" + card);
        using var disposeFixture = fixture;
        var managed = NrManaged(ctx, fixture.Root, card, out var pins, out var log);
        if (pinned) pins.SetNeuralRenderingDllVersion(fixture.Game, Nr50);

        var check = await managed.CheckManagedUpdateAsync(fixture.Game, ManagedComponentKind.ShortFuse, []);
        await managed.UpdateManagedAsync(fixture.Game, ManagedComponentKind.ShortFuse, []);

        Assert.False(check.UpdateAvailable, check.Message);
        Assert.Equal(CreatePe(0x8664, 80 * 1024, 0x7A), File.ReadAllBytes(Path.Combine(fixture.Bin, "nvngx_dlssnr.dll")));
        Assert.DoesNotContain(log.GetAll(), entry => entry.ComponentName.Contains("nvngx_dlssnr.dll", StringComparison.Ordinal));
    }
}
