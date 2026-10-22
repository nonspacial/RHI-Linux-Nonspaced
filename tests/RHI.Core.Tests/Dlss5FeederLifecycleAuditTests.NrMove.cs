using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #230 (owner decision 2026-10-22, item 2): Update All moves a "Latest" DLSS5 Feeder Full NR install onto the DLL made
/// for the card — the Borderlands 3 case: an RTX 4090 whose Feeder holds <c>310.8.3 (50xx)</c> from before #229, its
/// record without <c>NeuralRendering.DllVersion</c>. A hand-picked version stays (with a row note), an unknown card moves
/// nothing, an installed DLL RHI cannot tell is "could not check" (never current, never a blind swap), the NR Cost Scaler
/// holds the move. Crash and cancel tests over the move (journaled as in the app).
/// </summary>
public sealed partial class Dlss5FeederLifecycleAuditTests
{
    private static LinuxManagedComponentService Managed(FeederFixture f, RtxGeneration card, out NeuralAddonVersionPreferenceStore pins,
        out ComponentUpdateLogStore log)
    {
        pins = new NeuralAddonVersionPreferenceStore(Path.Combine(f.Root, "pins.json"));
        log = new ComponentUpdateLogStore(Path.Combine(f.Root, "update-log.json"));
        var managed = new LinuxManagedComponentService(f.Records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: null!, dlss5Feeder: f.Service, dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!, shortFuse: null!,
            dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!, neuralVersionPins: pins, updateLog: log);
        managed.NvidiaGpu = () => Facts(card);
        return managed;
    }

    /// <summary>
    /// Borderlands 3's state: Feeder Full installed while RHI took the manifest's first NR entry (an unknown card does that
    /// today), and a record from before #230 (no NR version in its metadata). The cache keeps the stamped 310.8.3 entry.
    /// </summary>
    private static async Task<FeederFixture> LegacyLatestOn50xxAsync(bool journaled = false)
    {
        var f = new FeederFixture(journaled: journaled, liveNr: true);
        f.Dlss.CardGeneration = () => RtxGeneration.Unknown;
        Assert.True((await f.Install(Dlss5FeederConsumer.RenoDxDlss5)).Success);
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.Equal(For50, record.Metadata[NeuralRenderingDllSwap.MetaDllVersion]); // #230 records it from now on
        record.Metadata.Remove(NeuralRenderingDllSwap.MetaDllVersion);
        f.Records.Upsert(record);
        Assert.Equal(Dll(For50), File.ReadAllBytes(NrPath(f)));
        return f;
    }

    private static string NrPath(FeederFixture f) => Path.Combine(f.Bin, "nvngx_dlssnr.dll");

    [Fact]
    public async Task NrMove_Borderlands3Case_LegacyRecordIdentifiedByItsHash_IsMovedTo310_8_2_WithAHistoryRow()
    {
        using var f = await LegacyLatestOn50xxAsync();
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out var log);

        Assert.Equal(new InstalledNrVersion(For50, InstalledNrVersionSource.CacheStamp),
            managed.InstalledNeuralRenderingVersion(f.Game, f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!));

        var check = await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Contains(LinuxManagedComponentService.NrMovePrefix + $"{For50} → {ForAll}", check.Message, StringComparison.Ordinal);
        var addonBefore = File.ReadAllBytes(Path.Combine(f.Bin, "dlss5-feed.addon64"));

        var updated = await managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(NrPath(f)));
        var record = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.Equal(ForAll, record.Metadata[NeuralRenderingDllSwap.MetaDllVersion]);
        Assert.Equal(FileHash.Sha256Hex(NrPath(f)), ManagedFileSwap.FindFile(record, "nvngx_dlssnr.dll")!.Sha256, ignoreCase: true);
        Assert.Equal(addonBefore, File.ReadAllBytes(Path.Combine(f.Bin, "dlss5-feed.addon64"))); // the add-on was current
        var entry = Assert.Single(log.GetAll());
        Assert.Equal((ManagedComponentKind.Dlss5Feeder, ComponentUpdateAction.Update, (string?)null, ForAll, true),
            (entry.Component, entry.Action, entry.FromVersion, entry.ToVersion, entry.Success));
        Assert.Contains("nvngx_dlssnr.dll", entry.ComponentName, StringComparison.Ordinal);
        Assert.False((await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, [])).UpdateAvailable);
    }

    [Fact]
    public async Task NrMove_LegacyRecordWithoutAStamp_IsIdentifiedByItsPeVersion()
    {
        using var f = await LegacyLatestOn50xxAsync();
        foreach (var stamp in Directory.EnumerateFiles(f.Dlss.CacheFolder(DlssComponentKind.NeuralRendering), "*.rhi-stamp.json", SearchOption.AllDirectories))
            File.Delete(stamp);
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;

        Assert.Equal(new InstalledNrVersion(For50, InstalledNrVersionSource.PeVersion), managed.InstalledNeuralRenderingVersion(f.Game, record));
        Assert.True((await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, [])).UpdateAvailable);
        Assert.True((await managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, [])).Success);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(NrPath(f)));
    }

    [Fact]
    public async Task NrMove_ADllRhiCannotTell_IsCouldNotCheck_NeverCurrent_AndNeverSwappedBlind()
    {
        using var f = await LegacyLatestOn50xxAsync();
        foreach (var stamp in Directory.EnumerateFiles(f.Dlss.CacheFolder(DlssComponentKind.NeuralRendering), "*.rhi-stamp.json", SearchOption.AllDirectories))
            File.Delete(stamp);
        // A DLL whose version no published entry has, placed and recorded as RHI's (e.g. a build pulled from the manifest).
        var odd = PeVersionReaderTests.BuildVersionedPe("310,7,9,0", "310,7,9,0", size: 80_000);
        File.WriteAllBytes(NrPath(f), odd);
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        ManagedFileSwap.FindFile(record, "nvngx_dlssnr.dll")!.Sha256 = FileHash.Sha256HexOf(odd);
        f.Records.Upsert(record);
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);

        var check = await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(check.CouldNotCheck, check.Message);
        Assert.False(check.UpdateAvailable);
        Assert.Contains(LinuxManagedComponentService.NrUnknownMessage, check.Message, StringComparison.Ordinal);
        var updated = await managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);
        Assert.Equal(odd, File.ReadAllBytes(NrPath(f)));
        var all = await new LinuxUpdateAllService(f.Records, managed, new NeverRunning()).RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);
        Assert.Equal((0, 0, 1), (all.ChangedCount, all.CurrentCount, all.UncheckedCount));
    }

    [Fact]
    public void Identify_TwoPublishedEntriesOfOneBuild_IsAmbiguous()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-nr-identify-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var nr = Path.Combine(dir, "nvngx_dlssnr.dll");
            File.WriteAllBytes(nr, Dll(For50));
            var record = new LinuxDeploymentRecord
            {
                Component = ManagedComponentKind.ShortFuse, Files = [new LinuxDeployedFile { Path = nr, Sha256 = FileHash.Sha256Hex(nr) }]
            };

            var both = NeuralRenderingInstalledVersion.Identify(record, ["310.8.3 (50xx)", "310.8.3 (20/30/40/50)"], Path.Combine(dir, "no-cache"), [record]);
            var none = NeuralRenderingInstalledVersion.Identify(record, [ForAll, Old50], Path.Combine(dir, "no-cache"), [record]);
            record.Metadata[NeuralRenderingDllSwap.MetaDllVersion] = ForAll;
            var recorded = NeuralRenderingInstalledVersion.Identify(record, LiveVersions, Path.Combine(dir, "no-cache"), [record]);

            // Under the NR Cost Scaler the owner's DLL is the scaler's nvngx_dlssnr_real.dll; the proxy sits at the name.
            var proxy = PeVersionReaderTests.BuildVersionedPe("1,0,0,0", "1,0,0,0", size: 80_000);
            var real = Path.Combine(dir, "nvngx_dlssnr_real.dll");
            File.Copy(nr, real);
            File.WriteAllBytes(nr, proxy);
            var scaler = new LinuxDeploymentRecord
            {
                Component = ManagedComponentKind.NrCostScaler, Files = [new LinuxDeployedFile { Path = nr, Sha256 = FileHash.Sha256HexOf(proxy) }]
            };
            record.Metadata.Remove(NeuralRenderingDllSwap.MetaDllVersion);
            var scaled = NeuralRenderingInstalledVersion.Identify(record, LiveVersions, Path.Combine(dir, "no-cache"), [record, scaler]);
            File.WriteAllBytes(nr, Dll(For50));

            Assert.Equal(new InstalledNrVersion(For50, InstalledNrVersionSource.PeVersion), scaled);
            Assert.Equal(new InstalledNrVersion(null, InstalledNrVersionSource.Ambiguous), both);
            Assert.Equal(new InstalledNrVersion(null, InstalledNrVersionSource.Unknown), none);
            Assert.Equal(new InstalledNrVersion(ForAll, InstalledNrVersionSource.Metadata), recorded);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task NrMove_AHandPickedVersionForAnotherCard_IsNeverMoved_AndItsRowSaysSo()
    {
        using var f = await LegacyLatestOn50xxAsync();
        var managed = Managed(f, RtxGeneration.Rtx40, out var pins, out _);
        pins.SetNeuralRenderingDllVersion(f.Game, For50);

        var check = await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);
        await managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.False(check.UpdateAvailable, check.Message);
        Assert.False(check.CouldNotCheck, check.Message);
        Assert.Equal(Dll(For50), File.ReadAllBytes(NrPath(f)));
        var row = new ManagedComponent { Kind = ManagedComponentKind.Dlss5Feeder, DisplayName = "DLSS5 Feeder", Availability = ComponentAvailability.Installed,
            ManagedByRhi = true, Note = "Managed by RHI Linux · 0.13.1-beta.1." };
        typeof(LinuxManagedComponentService).GetMethod("DecorateNeuralRenderingRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(managed, [f.Game, new[] { row }]);
        Assert.Equal("Managed by RHI Linux · 0.13.1-beta.1. " + LinuxManagedComponentService.NrPinOtherGenerationPrefix + For50, row.Note);
    }

    [Fact]
    public async Task NrMove_AHandPickedVersionTheCardCanUse_HasNoNote()
    {
        using var f = await LegacyLatestOn50xxAsync();
        var managed = Managed(f, RtxGeneration.Rtx50, out var pins, out _);
        pins.SetNeuralRenderingDllVersion(f.Game, For50);
        var row = new ManagedComponent { Kind = ManagedComponentKind.Dlss5Feeder, DisplayName = "DLSS5 Feeder", Availability = ComponentAvailability.Installed,
            ManagedByRhi = true, Note = "Managed." };

        typeof(LinuxManagedComponentService).GetMethod("DecorateNeuralRenderingRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(managed, [f.Game, new[] { row }]);

        Assert.Equal("Managed.", row.Note);
    }

    [Theory]
    [InlineData(RtxGeneration.Unknown)]
    [InlineData(RtxGeneration.Rtx50)]
    public async Task NrMove_UnknownCard_OrADllMadeForTheCard_MovesNothing(RtxGeneration card)
    {
        using var f = await LegacyLatestOn50xxAsync();
        var managed = Managed(f, card, out _, out var log);

        var check = await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);
        var all = await new LinuxUpdateAllService(f.Records, managed, new NeverRunning()).RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.False(check.UpdateAvailable, check.Message);
        Assert.False(check.CouldNotCheck, check.Message);
        Assert.Equal((0, 1), (all.ChangedCount, all.CurrentCount));
        Assert.Equal(Dll(For50), File.ReadAllBytes(NrPath(f)));
        Assert.Empty(log.GetAll());
    }

    [Fact]
    public async Task NrMove_UnderTheNrCostScaler_IsHeld_NeverAFailure()
    {
        using var f = await LegacyLatestOn50xxAsync();
        f.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(f.Game), GameName = f.Game.Name, Component = ManagedComponentKind.NrCostScaler,
            BinaryDirectory = f.Bin, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = NrPath(f), Sha256 = FileHash.Sha256Hex(NrPath(f)) }]
        });
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);

        var check = await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);
        var all = await new LinuxUpdateAllService(f.Records, managed, new NeverRunning()).RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal(NrCostScalerService.DisplayName, check.HeldBy);
        Assert.False(check.UpdateAvailable);
        Assert.Equal((0, 0, 1), (all.ChangedCount, all.FailedCount, all.HeldCount));
        Assert.Equal(Dll(For50), File.ReadAllBytes(NrPath(f)));
    }

    [Fact]
    public async Task NrMove_WithAnAddonUpdateToo_SwapsBoth_AndDownloadsNothingAfterTheWriteGate()
    {
        using var f = await LegacyLatestOn50xxAsync();
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        record.Version = "0.12.0"; // an older add-on: the release (0.13.1-beta.1) is an update too
        f.Records.Upsert(record);
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);

        var outcome = await CancelAtWritePhase.RunAsync(ct => managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, [], ct),
            [f.Bin], atWritePhase: f.Watch.CloseGate);

        Assert.True(outcome.Result.Success, outcome.Result.Message); // Cancel at the write phase: runs to completion
        Assert.Empty(f.Watch.LateRequests);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(NrPath(f)));
        var after = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.Equal(("0.13.1-beta.1", ForAll), (after.Version, after.Metadata[NeuralRenderingDllSwap.MetaDllVersion]));
    }

    [Trait("Category", "CommitPoint")]
    [Fact]
    public async Task NrMove_CancelAtTheWritePhase_WithAFailedSave_RollsTheDllBack()
    {
        using var f = await LegacyLatestOn50xxAsync(journaled: true);
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);

        var outcome = await CancelAtWritePhase.RunAsync(ct => managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, [], ct),
            [f.Bin], atWritePhase: () => { f.Watch.CloseGate(); f.Records.SaveFaultForTesting = () => new IOException("disk full"); });
        f.Records.SaveFaultForTesting = null;

        Assert.False(outcome.Result.Success);
        Assert.Empty(f.Watch.LateRequests);
        CancelAtWritePhase.AssertUnchanged(outcome.Before, f.Bin);
        Assert.Equal(Dll(For50), File.ReadAllBytes(NrPath(f)));
    }

    [Trait("Category", "CommitPoint")]
    [Fact]
    public async Task NrMove_DeathAfterTheSwapsFileWrite_RecoversToARecordAndFileThatAgree()
    {
        using var f = await LegacyLatestOn50xxAsync(journaled: true);
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);
        using (var death = new SimulatedDeath(f.Journal!))
        {
            f.Records.SaveFaultForTesting = death.Die; // the swap's record save: the new DLL is already in place
            var (died, _) = await death.RunUntilDeathAsync(() => managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []));
            Assert.True(died);
            Assert.Equal(Dll(ForAll), File.ReadAllBytes(NrPath(f))); // died after the file write

            await SimulatedDeath.RecoverAsync(f.JournalDirectory, f.RecordsPath);

            var record = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
            Assert.Equal(ManagedFileSwap.FindFile(record, "nvngx_dlssnr.dll")!.Sha256, FileHash.Sha256Hex(NrPath(f)), ignoreCase: true);
            Assert.Equal(Dll(For50), File.ReadAllBytes(NrPath(f))); // not committed: the previous DLL is back
            Assert.False(record.Metadata.ContainsKey(NeuralRenderingDllSwap.MetaDllVersion));
        }
        f.Records.SaveFaultForTesting = null;
    }

    [Trait("Category", "CommitPoint")]
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task NrMove_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        var fixtures = new List<FeederFixture>();
        try
        {
            async Task<CommitPointCase> Move(string label)
            {
                var f = await LegacyLatestOn50xxAsync(journaled: true);
                fixtures.Add(f);
                var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);
                return FeederCase(f, () => managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []));
            }

            var results = await CommitPointAgreement.AssertEveryWriteAsync(Move, shape);

            Assert.True(results[^1].Success, results[^1].Message); // the swap's record save is its commit point
        }
        finally
        {
            foreach (var f in fixtures) f.Dispose();
        }
    }
}
