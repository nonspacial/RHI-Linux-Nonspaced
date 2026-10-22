using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// Wave 11 integration, where Update All's Neural Rendering move (#230) meets the other lanes: a stalled NR download
/// reads as the stall and changes nothing (#233), and an OptiScaler Nightly on the same game is never part of the move
/// (#232: a Nightly owns no NR DLL).
/// </summary>
public sealed partial class Dlss5FeederLifecycleAuditTests
{
    // Integration item 4 (download-idle × nr-gpu): the BL3 case on an RTX 40 card, whose move needs the 310.8.2 DLL; its
    // download stops sending data. Update All gives up on that game at the idle limit with the stall line (never a Cancel,
    // never "current"), and the DLL, the record and the saved pins are left as they were; no move is logged.
    [Theory]
    [InlineData("en-US", LinuxUpdateRunMode.Manual)]
    [InlineData("pt-BR", LinuxUpdateRunMode.Background)]
    public async Task W11Int_NrMove_WhoseDllDownloadStalls_SaysSo_AndLeavesTheDllTheRecordAndThePins(string language, LinuxUpdateRunMode mode)
    {
        TrustedDownload.IdleLimitForTesting = TimeSpan.FromMilliseconds(300);
        using var f = await LegacyLatestOn50xxAsync(journaled: true);
        var managed = Managed(f, RtxGeneration.Rtx40, out var pins, out var log);
        var stalled = UrlOf(ForAll);
        f.Answer = url => url == stalled ? StalledHttp.Stalled() : null;
        var dllBefore = File.ReadAllBytes(NrPath(f));
        var recordBefore = JsonSerializer.Serialize(f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder));
        var pinsPath = Path.Combine(f.Root, "pins.json");
        var pinsBefore = File.Exists(pinsPath) ? File.ReadAllText(pinsPath) : null;
        Assert.True((await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, [])).UpdateAvailable);

        var all = await new LinuxUpdateAllService(f.Records, managed, new NeverRunning()).RunAsync([f.Game], _ => [], mode)
            .WaitAsync(StalledHttp.HangGuard);

        Assert.Contains(stalled, f.Requests);
        var item = Assert.Single(all.Items, i => i.Component == ManagedComponentKind.Dlss5Feeder);
        Assert.False(item.Success);
        Assert.Equal((0, 1), (all.ChangedCount, all.FailedCount));
        Assert.StartsWith(LinuxManagedComponentService.NrStageFailedPrefix + DownloadStalledException.MessagePrefix, item.Message, StringComparison.Ordinal);
        Assert.Equal(dllBefore, File.ReadAllBytes(NrPath(f)));
        Assert.Equal(recordBefore, JsonSerializer.Serialize(new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.Dlss5Feeder)));
        Assert.Equal(pinsBefore, File.Exists(pinsPath) ? File.ReadAllText(pinsPath) : null);
        Assert.Null(pins.GetNeuralRenderingDllVersion(f.Game));
        Assert.DoesNotContain(log.GetAll(), entry => entry.Success); // a failed attempt may be logged as failed, never as a move
        var line = UiLocalizationService.PresentFor(language, item.Message);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
        Assert.DoesNotContain(language == "pt-BR" ? "cancelad" : "cancel", line, StringComparison.OrdinalIgnoreCase);
        if (language == "pt-BR")
        {
            Assert.DoesNotContain("The download stopped", line, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be downloaded", line, StringComparison.Ordinal);
        }
    }

    // Integration item 4 (nr-gpu × nightly-switch): a pinned OptiScaler Nightly in the same game folder owns no NR DLL, so
    // the move plans nothing for it and moves only the Feeder's DLL; OptiScaler's record and files stay byte for byte.
    [Fact]
    public async Task W11Int_NrMove_BesideAPinnedOptiScalerNightly_MovesOnlyTheOwnersDll()
    {
        using var f = await LegacyLatestOn50xxAsync();
        var proxy = Path.Combine(f.Bin, "winmm.dll");
        var xess = Path.Combine(f.Bin, "OptiScaler", "libxess.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(xess)!);
        File.WriteAllBytes(proxy, FeederFixture.Pe(96 * 1024));
        File.WriteAllText(xess, "MZ xess 20261004");
        f.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(f.Game), GameName = f.Game.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = f.Bin, Version = "20261004",
            Metadata =
            {
                [OptiScalerDeploymentService.VariantMetadataKey] = nameof(OptiScalerVariant.Nightly),
                [OptiScalerDeploymentService.NightlyBuildMetadataKey] = "20261004",
                [OptiScalerDeploymentService.ProxyNameMetadataKey] = "winmm.dll"
            },
            Files =
            [
                new LinuxDeployedFile { Path = proxy, Sha256 = FileHash.Sha256Hex(proxy) },
                new LinuxDeployedFile { Path = xess, Sha256 = FileHash.Sha256Hex(xess) }
            ]
        });
        var optiRecord = f.Records.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("20261004", OptiScalerDeploymentService.GetPinnedNightlyBuild(optiRecord));
        Assert.False(NeuralRenderingRuntimeOwnership.ListsRuntime(optiRecord));
        var optiBefore = JsonSerializer.Serialize(optiRecord);
        var proxyBefore = File.ReadAllBytes(proxy);
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);

        Assert.Equal(LinuxManagedComponentService.NrMoveKind.NotApplicable,
            (await managed.PlanNeuralRenderingMoveAsync(f.Game, ManagedComponentKind.OptiScaler, CancellationToken.None)).Kind);
        Assert.Equal(LinuxManagedComponentService.NrMoveKind.Move,
            (await managed.PlanNeuralRenderingMoveAsync(f.Game, ManagedComponentKind.Dlss5Feeder, CancellationToken.None)).Kind);
        var updated = await managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(NrPath(f)));
        Assert.Equal(optiBefore, JsonSerializer.Serialize(new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.OptiScaler)));
        Assert.Equal(proxyBefore, File.ReadAllBytes(proxy));
        Assert.Equal("MZ xess 20261004", File.ReadAllText(xess));
    }
}
