using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #200 DX9 HDR, correction round (reviews/w7-review-dxvk-hdr.md): the reviewer's probes adopted as tests, and one test
// per finding, each shown red without its fix.
public sealed partial class ReShadeChannelTests
{
    /// <summary>The DX9 HDR row as the app builds it: the probe over the game's files, then the service's decoration.</summary>
    private static ManagedComponent DecoratedDxvkRow(DxvkRig rig)
    {
        var files = Directory.EnumerateFiles(rig.Game.InstallPath!, "*", SearchOption.AllDirectories).ToArray();
        var components = ComponentProbe.Probe(rig.Game, files);
        var row = components.Single(component => component.Kind == ManagedComponentKind.DxvkHdr);
        rig.Dxvk.Decorate(rig.Game, components, rig.Dxvk.GetRecord(rig.Game));
        return row;
    }

    // Review major 1 (probe P3): the user deleted RHI's d3d9.dll. The row must still offer Remove (Installed, action
    // allowed), and Remove must take dxvk.conf, the per-exe override and the record away and put a moved ReShade back.
    [Fact]
    public async Task Review_DxvkHdr_D3d9DeletedByUser_RowStillOffersRemoval()
    {
        var rig = Rig("rv-d3d9-gone");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        File.Delete(rig.D3d9);
        var row = new ManagedComponent { Kind = ManagedComponentKind.DxvkHdr, DisplayName = "DX9 HDR", Availability = ComponentAvailability.Available };
        rig.Dxvk.Decorate(rig.Game, [row], rig.Dxvk.GetRecord(rig.Game));
        var install = await rig.Dxvk.InstallAsync(rig.Game);
        Assert.True(row.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable || install.Success,
            $"row={row.Availability} actionAllowed={row.ActionAllowed}; Install answers: {install.Message}");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DxvkHdr_D3d9DeletedByUser_RowOffersRemove_AndRemovalCleansTheRest(bool withReShade, bool noLongerDx9)
    {
        var rig = Rig($"dxvk-gone-{withReShade}-{noLongerDx9}");
        if (withReShade) await InstallDllReShadeAsync(rig);
        var proxy = withReShade ? File.ReadAllBytes(rig.D3d9) : null;
        var user = File.ReadAllBytes(rig.UserReg);
        var system = File.ReadAllBytes(rig.SystemReg);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        File.Delete(rig.D3d9);
        if (noLongerDx9) rig.Game.UserGraphicsApiOverride = GraphicsApi.DirectX11;

        var row = DecoratedDxvkRow(rig);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.True(row.ManagedByRhi);
        Assert.True(row.ActionAllowed, row.Note);
        Assert.Equal(DxvkHdrDeploymentService.MissingD3d9Note, row.Note);

        var removed = await rig.Dxvk.RemoveAsync(rig.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Null(rig.Dxvk.GetRecord(rig.Game));
        Assert.False(File.Exists(rig.Conf));
        Assert.Equal(user, File.ReadAllBytes(rig.UserReg));
        Assert.Equal(system, File.ReadAllBytes(rig.SystemReg));
        if (withReShade)
        {
            Assert.Equal(proxy, File.ReadAllBytes(rig.D3d9));
            Assert.False(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
            Assert.False(File.Exists(Path.Combine(rig.Bin, "vulkan-1.dll")));
        }
        else Assert.False(File.Exists(rig.D3d9));
        // Gone with the record: the row is no longer shown on a game where DX9 HDR cannot apply.
        var after = DecoratedDxvkRow(rig);
        Assert.Equal(noLongerDx9 ? ComponentAvailability.Unsupported : ComponentAvailability.Available, after.Availability);
    }

    // Reviewer probes adopted (reviews/w7-review-dxvk-hdr-probes/ReviewDxvkProbes.cs).
    // Review minor 3 (probe P1; pins the install's own BeginWrites, mutant M8): a Cancel honoured late
    // (CancelAtWritePhase) with NO forced rollback: the install must run to completion.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_DxvkHdr_LateCancel_RunsToCompletion(bool withReShade)
    {
        var rig = Rig("rv-latecancel-" + withReShade);
        if (withReShade) await InstallDllReShadeAsync(rig);
        var outcome = await CancelAtWritePhase.RunAsync(token => rig.Dxvk.InstallAsync(rig.Game, token), [rig.Bin, rig.Game.PrefixPath! + "/drive_c"]);
        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(rig.Builds.Bytes(false), File.ReadAllBytes(rig.D3d9));
        Assert.NotNull(rig.Dxvk.GetRecord(rig.Game));
    }

    // P2: journaled cancel-after-first-write (forced rollback), retry, then the next start's recovery keeps the retry.
    [Fact]
    public async Task Review_DxvkHdr_CancelRollback_Retry_Restart_KeepsTheRetry()
    {
        var rig = Rig("rv-cancel-retry", journaled: true);
        await InstallDllReShadeAsync(rig);
        var userReShade = File.ReadAllBytes(rig.UserReg);
        var systemReShade = File.ReadAllBytes(rig.SystemReg);
        var outcome = await CancelAtWritePhase.RunAsync(token => rig.Dxvk.InstallAsync(rig.Game, token), [rig.Bin, rig.Game.PrefixPath! + "/drive_c"],
            atWritePhase: () => rig.Store.SaveFaultForTesting = () => new IOException("disk full"));
        rig.Store.SaveFaultForTesting = null;
        Assert.False(outcome.Result.Success);
        CancelAtWritePhase.AssertUnchanged(outcome.Before, rig.Bin);
        Assert.Equal(userReShade, File.ReadAllBytes(rig.UserReg));
        Assert.Equal(systemReShade, File.ReadAllBytes(rig.SystemReg));

        var retried = await rig.Dxvk.InstallAsync(rig.Game);
        Assert.True(retried.Success, retried.Message);
        var files = CancelAtWritePhase.Snapshot(rig.Bin, LayerFolder(rig.Game));
        var user = File.ReadAllBytes(rig.UserReg);
        var system = File.ReadAllBytes(rig.SystemReg);
        var ledger = File.ReadAllBytes(rig.Ledger);

        await SimulatedDeath.RecoverAsync(rig.JournalDirectory!, rig.Ledger); // the next start

        CancelAtWritePhase.AssertUnchanged(files, rig.Bin, LayerFolder(rig.Game));
        Assert.Equal(user, File.ReadAllBytes(rig.UserReg));
        Assert.Equal(system, File.ReadAllBytes(rig.SystemReg));
        Assert.Equal(ledger, File.ReadAllBytes(rig.Ledger));
    }

    // P2b: crash at the install's commit write, restart (recovery), retry with fresh services, restart again.
    [Fact]
    public async Task Review_DxvkHdr_CrashAtCommit_Recover_Retry_RecoverAgain_KeepsTheRetry()
    {
        var rig = Rig("rv-crash-retry", journaled: true);
        await InstallDllReShadeAsync(rig);
        var withReShade = CancelAtWritePhase.Snapshot(rig.Bin);
        using (var death = new SimulatedDeath(rig.Journal!))
        {
            rig.Store.SaveFaultForTesting = () => death.Die();
            var (died, _) = await death.RunUntilDeathAsync(() => rig.Dxvk.InstallAsync(rig.Game));
            Assert.True(died);
            await SimulatedDeath.RecoverAsync(rig.JournalDirectory!, rig.Ledger);
            CancelAtWritePhase.AssertUnchanged(withReShade, rig.Bin);

            // Retry through fresh services (the dead thread still holds the old store's lock).
            var store = new LinuxDeploymentRecordStore(rig.Ledger);
            var (journal, overrides) = SimulatedDeath.NewJournal(rig.JournalDirectory!, store);
            var http = new HttpClient(new DxvkHdrHandler(new LunarGHandler()));
            var cache = Path.Combine(_root, "rv-crash-retry-cache2");
            var reshade = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), store, overrides,
                new FakeExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
                journal: journal);
            var dxvk = new DxvkHdrDeploymentService(http, store, overrides, reshade, journal, Path.Combine(cache, "dxvk-hdr"));
            var retried = await dxvk.InstallAsync(rig.Game);
            Assert.True(retried.Success, retried.Message);
            var files = CancelAtWritePhase.Snapshot(rig.Bin, LayerFolder(rig.Game));
            var user = File.ReadAllBytes(rig.UserReg);
            var system = File.ReadAllBytes(rig.SystemReg);
            var ledger = File.ReadAllBytes(rig.Ledger);

            await SimulatedDeath.RecoverAsync(rig.JournalDirectory!, rig.Ledger);

            CancelAtWritePhase.AssertUnchanged(files, rig.Bin, LayerFolder(rig.Game));
            Assert.Equal(user, File.ReadAllBytes(rig.UserReg));
            Assert.Equal(system, File.ReadAllBytes(rig.SystemReg));
            Assert.Equal(ledger, File.ReadAllBytes(rig.Ledger));
        }
        rig.Store.SaveFaultForTesting = null;
    }

    // P5: ReShade's own update/reinstall on the moved layer (no DX9 HDR view): stays the layer, d3d9.dll untouched, removal still moves back.
    [Fact]
    public async Task Review_DxvkHdr_ReShadeUpdateOnMovedLayer_StaysLayer()
    {
        var rig = Rig("rv-reshade-update");
        await InstallDllReShadeAsync(rig);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        var d3d9 = File.ReadAllBytes(rig.D3d9);
        var updated = await rig.ReShade.InstallAsync(rig.Game, new ReShadeInstallOptions());
        Assert.True(updated.Success, updated.Message);
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
        Assert.Equal(d3d9, File.ReadAllBytes(rig.D3d9));
        var removed = await rig.Dxvk.RemoveAsync(rig.Game);
        Assert.True(removed.Success, removed.Message);
        var back = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        Assert.False(ReShadeVulkanLayer.IsLayerRecord(back));
        Assert.All(back.Files, file => Assert.True(File.Exists(file.Path), file.Path));
        Assert.False(File.Exists(Path.Combine(rig.Bin, "vulkan-1.dll")));
    }

    // P7: a foreign D3D9.DLL / DXVK.CONF in another spelling is refused and left alone.
    [Theory]
    [InlineData("D3D9.DLL")]
    [InlineData("DXVK.CONF")]
    public async Task Review_DxvkHdr_ForeignOtherSpelling_Refused(string name)
    {
        var rig = Rig("rv-upper-" + name);
        var path = Path.Combine(rig.Bin, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        var before = CancelAtWritePhase.Snapshot(rig.Bin, rig.Game.PrefixPath!);
        var result = await rig.Dxvk.InstallAsync(rig.Game);
        Assert.False(result.Success, result.Message);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin, rig.Game.PrefixPath!);
    }

    // P8: a per-exe section the user already has (another override) comes back byte for byte.
    [Fact]
    public async Task Review_DxvkHdr_ExistingPerExeSection_ByteForByte()
    {
        var rig = Rig("rv-section");
        var user = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        user.EnsureNativeBuiltin(rig.Game.PrefixPath!, "dinput8", "Game.exe");
        var before = File.ReadAllBytes(rig.UserReg);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        Assert.Equal("native,builtin", rig.Override("d3d9"));
        Assert.True((await rig.Dxvk.RemoveAsync(rig.Game)).Success);
        Assert.Equal(before, File.ReadAllBytes(rig.UserReg));
    }

    // P9: crash right after d3d9.dll is placed (mid-operation, not a ledger write), with ReShade moving: recovery puts all back.
    [Fact]
    public async Task Review_DxvkHdr_CrashMidInstall_WithReShade_RecoversByteForByte()
    {
        var rig = Rig("rv-crash-mid", journaled: true);
        await InstallDllReShadeAsync(rig);
        var before = CancelAtWritePhase.Snapshot(rig.Bin);
        var user = File.ReadAllBytes(rig.UserReg);
        var system = File.ReadAllBytes(rig.SystemReg);
        using (var death = new SimulatedDeath(rig.Journal!))
        {
            rig.Dxvk.AfterD3d9PlacedForTesting = () => throw death.Die();
            var (died, _) = await death.RunUntilDeathAsync(() => rig.Dxvk.InstallAsync(rig.Game));
            Assert.True(died);
            await SimulatedDeath.RecoverAsync(rig.JournalDirectory!, rig.Ledger);
            CancelAtWritePhase.AssertUnchanged(before, rig.Bin);
            Assert.Equal(user, File.ReadAllBytes(rig.UserReg));
            Assert.Equal(system, File.ReadAllBytes(rig.SystemReg));
            Assert.False(Directory.Exists(LayerFolder(rig.Game)), "layer folder left");
            var fresh = new LinuxDeploymentRecordStore(rig.Ledger);
            Assert.Null(fresh.Find(rig.Game, ManagedComponentKind.DxvkHdr));
            Assert.False(ReShadeVulkanLayer.IsLayerRecord(fresh.Find(rig.Game, ManagedComponentKind.ReShade)));
        }
    }
}
