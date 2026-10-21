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

    // Review minor 4 (R2, before extraction): the archive lists its d3d9.dll over the 64 MiB cap: refused before 7-Zip
    // extracts anything; nothing is cached, placed or recorded. Exactly 64 MiB is accepted.
    [Theory]
    [InlineData(64L * 1024 * 1024 + 1, false)]
    [InlineData(64L * 1024 * 1024, true)]
    public async Task DxvkHdr_ListedD3d9OverTheCap_IsRefusedBeforeExtraction(long size, bool accepted)
    {
        var rig = Rig("dxvk-cap-listed-" + accepted);
        rig.Builds.Grow64(size);
        var before = CancelAtWritePhase.Snapshot(rig.Bin);

        var result = await rig.Dxvk.InstallAsync(rig.Game);

        if (accepted)
        {
            Assert.True(result.Success, result.Message);
            Assert.Equal(size, new FileInfo(rig.D3d9).Length);
            return;
        }
        Assert.False(result.Success);
        Assert.Equal(DxvkHdrDeploymentService.DownloadFailedPrefix + DxvkHdrDeploymentService.D3d9TooLargePrefix + "normal/x64/d3d9.dll", result.Message);
        Assert.Equal(0, rig.Builds.Extractions);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin);
        Assert.Null(rig.Dxvk.GetRecord(rig.Game));
    }

    // Review minor 4 (R2, after extraction): the listing claims a small d3d9.dll but 7-Zip writes one over the cap (a
    // header that lies): refused once written, never cached or placed.
    [Fact]
    public async Task DxvkHdr_ExtractedD3d9OverTheCap_IsRefusedAndNeverCached()
    {
        var rig = Rig("dxvk-cap-extracted");
        rig.Builds.Grow64(64L * 1024 * 1024 + 1);
        rig.Builds.Declare64(8192);
        var before = CancelAtWritePhase.Snapshot(rig.Bin);

        var result = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.False(result.Success);
        Assert.Equal(DxvkHdrDeploymentService.DownloadFailedPrefix + DxvkHdrDeploymentService.D3d9TooLargePrefix + "normal/x64/d3d9.dll", result.Message);
        Assert.Equal(1, rig.Builds.Extractions);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin);
        Assert.False(File.Exists(CachedD3d9(rig, DxvkVersion)));
        Assert.Null(rig.Dxvk.GetRecord(rig.Game));
    }

    private string CachedD3d9(DxvkRig rig, string version, string arch = "x64")
        => Path.Combine(_root, Path.GetFileName(Path.GetDirectoryName(rig.Ledger))! + "-cache", "dxvk-hdr", version, arch, "d3d9.dll");

    // Review minor 4 (R4): Update never replaces a d3d9.dll changed outside RHI (it keeps the marker and the size): refused
    // before any download, file and record as they were.
    [Fact]
    public async Task DxvkHdr_Update_RefusesAD3d9ChangedOutsideRhi_BeforeAnyDownload()
    {
        var rig = Rig("dxvk-update-changed");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        File.WriteAllBytes(rig.D3d9, DxvkHdrDll(0x8664, "user"));
        var changed = File.ReadAllBytes(rig.D3d9);
        rig.Handler.Tag = "v10.0-HDR-mod-v1.0.0";
        rig.Handler.Archive = [.. rig.Handler.Archive, 42];
        rig.Builds.Set("b");
        var requests = (rig.Handler.ApiRequests, rig.Handler.ArchiveRequests);

        var updated = await rig.Dxvk.UpdateAsync(rig.Game);

        Assert.False(updated.Success);
        Assert.Equal(DxvkHdrDeploymentService.ModifiedFilePrefix + rig.D3d9, updated.Message);
        Assert.Equal(changed, File.ReadAllBytes(rig.D3d9));
        Assert.Equal(DxvkVersion, rig.Dxvk.GetRecord(rig.Game)!.Version);
        Assert.Equal(requests, (rig.Handler.ApiRequests, rig.Handler.ArchiveRequests));
    }

    // Review minor 4 (R5): the extracted d3d9.dll is reused only while bound to the archive it came from. A foreign file
    // in its place (header, size and marker kept) is extracted again, never placed.
    [Fact]
    public async Task DxvkHdr_ATamperedCachedBuild_IsExtractedAgain_NeverPlaced()
    {
        var rig = Rig("dxvk-cache-tie");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        Assert.True((await rig.Dxvk.RemoveAsync(rig.Game)).Success);
        var cached = CachedD3d9(rig, DxvkVersion);
        var foreign = DxvkHdrDll(0x8664, "x");
        Assert.Equal(new FileInfo(cached).Length, foreign.Length);
        File.WriteAllBytes(cached, foreign);

        var again = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.True(again.Success, again.Message);
        Assert.Equal(rig.Builds.Bytes(false), File.ReadAllBytes(rig.D3d9));
        Assert.Equal(2, rig.Builds.Extractions);
    }

    // Review nit 6: a release that ships the same d3d9.dll under a newer tag: Update says it is current and records the
    // tag, so the check stops offering it.
    [Fact]
    public async Task DxvkHdr_SameD3d9UnderANewTag_UpdateRecordsTheTag_AndTheCheckStopsOffering()
    {
        var rig = Rig("dxvk-same-d3d9");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        var d3d9 = File.ReadAllBytes(rig.D3d9);
        rig.Handler.Tag = "v10.0-HDR-mod-v1.0.0";
        Assert.True((await rig.Dxvk.CheckUpdateAsync(rig.Game)).UpdateAvailable);

        var updated = await rig.Dxvk.UpdateAsync(rig.Game);
        var check = await rig.Dxvk.CheckUpdateAsync(rig.Game);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(DxvkHdrDeploymentService.AlreadyCurrentPrefix + "10.0-HDR-mod-v1.0.0", updated.Message);
        Assert.Equal(d3d9, File.ReadAllBytes(rig.D3d9));
        Assert.Equal("10.0-HDR-mod-v1.0.0", rig.Dxvk.GetRecord(rig.Game)!.Version);
        Assert.False(check.UpdateAvailable, check.Message);
    }

    // The listing parser against what the host tools really print for the release (7-Zip 26.04 `l -slt`, bsdtar 3.8
    // `-tvf` on dxvk_v3.1-HDR-mod-v0.3.4.7z): the archive's own block (its Path and Physical Size) is never taken for
    // the member, and a member the archive does not hold is null.
    [Theory]
    [InlineData(ExternalArchiveToolKind.SevenZip, "normal/x32/d3d9.dll", 7884814L)]
    [InlineData(ExternalArchiveToolKind.BsdTar, "normal/x32/d3d9.dll", 7884814L)]
    [InlineData(ExternalArchiveToolKind.SevenZip, "normal/x64/d3d9.dll", null)]
    [InlineData(ExternalArchiveToolKind.BsdTar, "normal/x64/d3d9.dll", null)]
    public async Task DxvkHdr_ListedSize_ReadsWhatTheHostToolsPrint(ExternalArchiveToolKind kind, string member, long? expected)
    {
        const string sevenZip = """

            7-Zip 26.04 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-10-05
             64-bit locale=en_GB.UTF-8 Threads:36 OPEN_MAX:1048576, ASM

            Scanning the drive for archives:
            1 file, 6198716 bytes (6054 KiB)

            Listing archive: dxvk_v3.1-HDR-mod-v0.3.4.7z

            --
            Path = dxvk_v3.1-HDR-mod-v0.3.4.7z
            Type = 7z
            Physical Size = 6198716
            Headers Size = 666
            Method = LZMA2:48m LZMA:20 BCJ2
            Solid = +
            Blocks = 2

            ----------
            Path = normal/x32/d3d9.dll
            Size = 7884814
            Packed Size = 
            Modified = 2026-09-07 11:45:22.2239379
            Attributes = A -rwxr-xr-x
            CRC = 66C70630
            Encrypted = -
            Method = BCJ2 LZMA2:48m LZMA:20:lc0:lp2 LZMA:20:lc0:lp2
            Block = 1

            """;
        const string bsdtar = "-rwxr-xr-x  0 0      0     7884814 Sep  7 11:45 normal/x32/d3d9.dll\n";
        var folder = Path.Combine(_root, "listing-" + kind + "-" + Path.GetFileName(Path.GetDirectoryName(member)));
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "output.txt");
        File.WriteAllText(output, kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar);
        var tool = Path.Combine(folder, "tool");
        File.WriteAllText(tool, $"#!/bin/sh\ncat '{output}'\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var archive = Path.Combine(folder, "dxvk_v3.1-HDR-mod-v0.3.4.7z");
        File.WriteAllBytes(archive, [0]);

        var size = await DxvkHdrDeploymentService.ListedSizeAsync(new ExternalArchiveTool(tool, kind), archive, member, CancellationToken.None);

        Assert.Equal(expected, size);
    }
}
