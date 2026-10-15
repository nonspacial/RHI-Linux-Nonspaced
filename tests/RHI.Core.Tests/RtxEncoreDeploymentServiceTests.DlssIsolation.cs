using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w4/encore review finding 1: RTX Encore's pinned <c>nvngx_dlssnr.dll</c> (and the game's own runtime it kept as
/// <c>.original</c>) is never a DLSS panel, Quick Apply, Batch Deploy or Restore target, so RHI itself never breaks the
/// one-owner rule and RTX Encore's removal still finds its file unchanged. The game's other NR file stays a fair target.
/// </summary>
public sealed partial class RtxEncoreDeploymentServiceTests
{
    private const string OtherNrVersion = "310.8.3 (50xx)";

    [Fact]
    public async Task DlssPanelBatchAndRestore_NeverTouchRtxEncoresRuntime_NorTheOriginalItKept()
    {
        var encoreNr = Path.Combine(_bin, "nvngx_dlssnr.dll");
        var kept = encoreNr + ".original";
        File.WriteAllText(encoreNr, "the game's own runtime");
        var engine = Path.Combine(_root, "game", "Engine");
        Directory.CreateDirectory(engine);
        var gameNr = Path.Combine(engine, "nvngx_dlssnr.dll");
        var gameNrBytes = Pe(0x50);
        File.WriteAllBytes(gameNr, gameNrBytes);
        var handler = new EncoreHandler();
        _names.SetRtxEncoreNeuralRendering(_game, true);
        var encore = Service(handler);
        var stale = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-panel"))
            .Detect(_game, null, null); // the panel's detection, cached before RTX Encore owned the runtime
        Assert.Contains(encoreNr, stale.DlssnrPaths);
        Assert.True((await encore.InstallAsync(_game)).Success);
        Assert.Equal("the game's own runtime", File.ReadAllText(kept));

        var dlss = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-panel"));
        await dlss.FetchManifestAsync();
        var defaults = new LinuxDlssDefaultsService(dlss, _records, new NeverRunning());
        void AssertEncoreUntouched()
        {
            Assert.Equal(NrAccepted, File.ReadAllBytes(encoreNr));
            Assert.Equal("the game's own runtime", File.ReadAllText(kept));
            Assert.False(File.Exists(kept + ".original"));
        }

        // Targets: a fresh probe, and the panel's cached one at action time.
        Assert.Equal(new[] { gameNr }, defaults.DetectTargets(_game).DlssnrPaths);
        Assert.True(defaults.TryPanelTargets(_game, stale, "NR", out var kind, out var paths));
        Assert.Equal(new[] { gameNr }, paths);

        // Per-game panel swap and Restore.
        Assert.True((await dlss.SwapAsync(kind, paths, OtherNrVersion)).Success);
        Assert.Equal(NrOther, File.ReadAllBytes(gameNr));
        AssertEncoreUntouched();
        Assert.True((await dlss.RestoreAsync(kind, paths)).Success);
        Assert.Equal(gameNrBytes, File.ReadAllBytes(gameNr));
        AssertEncoreUntouched();

        // Quick Apply with a default NR version, then Restore All.
        var quick = await defaults.ApplyDefaultsAsync(_game, new LinuxDlssVersionDefaults(NeuralRendering: OtherNrVersion));
        Assert.Equal(0, quick.FailedCount);
        Assert.Equal(NrOther, File.ReadAllBytes(gameNr));
        AssertEncoreUntouched();
        var restored = await defaults.RestoreAllAsync(_game);
        Assert.Equal(0, restored.FailedCount);
        Assert.Equal(gameNrBytes, File.ReadAllBytes(gameNr));
        AssertEncoreUntouched();

        // Batch Deploy, then Batch Restore.
        Assert.Equal(0, (await defaults.ApplyBatchAsync([_game], new LinuxDlssVersionDefaults(NeuralRendering: OtherNrVersion))).FailedCount);
        Assert.Equal(NrOther, File.ReadAllBytes(gameNr));
        AssertEncoreUntouched();
        Assert.Equal(0, (await defaults.RestoreBatchAsync([_game])).FailedCount);
        Assert.Equal(gameNrBytes, File.ReadAllBytes(gameNr));
        AssertEncoreUntouched();

        // RTX Encore's record still matches its file: removal succeeds and puts the game's own runtime back.
        var removed = await encore.RemoveAsync(_game);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("the game's own runtime", File.ReadAllText(encoreNr));
        Assert.False(File.Exists(kept));
    }

    [Fact]
    public async Task WithoutItsNeuralRenderingFile_RtxEncoreOwnsNothing_AndTheGamesRuntimeStaysATarget()
    {
        var gameNr = Path.Combine(_bin, "nvngx_dlssnr.dll");
        File.WriteAllBytes(gameNr, Pe(0x51));
        Assert.True((await Service(new EncoreHandler()).InstallAsync(_game)).Success);
        var defaults = new LinuxDlssDefaultsService(
            new DlssManagerService(new HttpClient(new EncoreHandler()), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-panel")),
            _records, new NeverRunning());

        Assert.Equal(new[] { gameNr }, defaults.DetectTargets(_game).DlssnrPaths);
        Assert.Empty(NeuralRenderingRuntimeOwnership.OwnerRecords(_records, _game));
    }

    [Theory]
    [InlineData(ManagedComponentKind.ShortFuse)]
    [InlineData(ManagedComponentKind.Dlss5Feeder)]
    [InlineData(ManagedComponentKind.RtxEncore)]
    public void EveryRuntimeOwnersFiles_LeaveTheDirectTargets(ManagedComponentKind owner)
    {
        var ownedNr = Path.Combine(_bin, "nvngx_dlssnr.dll");
        File.WriteAllBytes(ownedNr, Pe(0x52));
        _records.Upsert(Record(owner, ownedNr));
        var defaults = new LinuxDlssDefaultsService(
            new DlssManagerService(new HttpClient(new EncoreHandler()), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-panel")),
            _records, new NeverRunning());

        Assert.Empty(defaults.DetectTargets(_game).DlssnrPaths);
        Assert.Equal(owner, NeuralRenderingRuntimeOwnership.OwnerRecords(_records, _game).Single().Component);
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }
}
