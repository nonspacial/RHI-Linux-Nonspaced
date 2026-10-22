using System.Reflection;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using static RHI.Core.Tests.DroppedArchiveExtractorTests;

namespace RHI.Core.Tests;

// Wave-10 integration: the archive install (#216) hands its add-on to the journaled RenoDX install unchanged, so the
// commit-point rules of that install reach the archive's line: a save that landed keeps the change and says so (#204),
// and a game held because an earlier undo could not be finished (#220) is refused with the hold's own text, which says to
// restart RHI (#221 wording rule). RHI's copy of the archive is gone in both cases; nothing more is installed.
public sealed partial class WaveTenIntegrationTests
{
    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    private static List<string> Lines(ViewModelFixture fx)
    {
        var lines = new List<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lock (lines) lines.Add(fx.Vm.StatusText); };
        return lines;
    }

    private static async Task<RHI.Desktop.ViewModels.DroppedRenoDxOffer> ArchiveOfferAsync(ViewModelFixture fx, string zip)
        => (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!.AddOns.Single();

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task AnArchiveInstall_WhoseSaveLanded_KeepsTheAddon_AndItsLineSaysTheSaveReportedAnError(string language)
    {
        var (fx, game) = await StartAsync(language);
        using var _ = fx;
        var addon = AddonBytes();
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "Foo Game HDR.zip"), ("readme.txt", "r"u8.ToArray()), ("renodx-foogame.addon64", addon));
        var offer = await ArchiveOfferAsync(fx, zip);
        var lines = Lines(fx);

        LandedLedgerWrite.Arm(fx.Records, LandedShape.AfterCache, at: 1);
        bool installed;
        try
        {
            installed = await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game);
        }
        finally
        {
            LandedLedgerWrite.Disarm(fx.Records);
        }

        Assert.True(installed, string.Join(" | ", lines));
        var note = UiLocalizationService.PresentFor(language, DeploymentScope.Landed("RenoDX").Message);
        Assert.Contains(lines, line => line.StartsWith(note, StringComparison.Ordinal));
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), string.Join(" | ", lines), StringComparison.Ordinal);
        Assert.Equal(addon, File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "renodx-foogame.addon64")));
        Assert.NotNull(new LinuxDeploymentRecordStore(Path.Combine(fx.DataDir, "deployments.json")).Find(game, ManagedComponentKind.RenoDX));
        Assert.True(File.Exists(zip));
        Assert.True(ScratchIsEmpty(fx));
    }

    /// <summary>
    /// An earlier RenoDX change to <paramref name="game"/> on the app's own RenoDX journal whose commit write fails (not
    /// landed) while the folder it changed turns read-only: its undo cannot finish, so the game is held (#220). The
    /// folder is beside the game's binaries, so the add-on folder itself stays as it was.
    /// </summary>
    private static async Task HoldAsync(ViewModelFixture fx, DetectedGame game, string folder, string payloadName = "earlier.ini", bool existedBefore = true)
    {
        var renodx = typeof(LinuxManagedComponentService).GetField("_renodx", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fx.Managed)!;
        var journal = (DeploymentJournal)typeof(RenoDxDeploymentService).GetField("_journal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renodx)!;
        var payload = Path.Combine(folder, payloadName);
        if (existedBefore) File.WriteAllText(payload, "before");
        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "earlier RenoDX change", game, ManagedComponentKind.RenoDX, CancellationToken.None);
        Assert.Null(refusal);
        await using var scope = opened!;
        await scope.PlaceBytesAsync(payload, "after"u8.ToArray());
        fx.Records.SaveFaultForTesting = () =>
        {
            PermissionDenial.MakeReadOnly(folder);
            return new IOException("Simulated write failure: deployments.json");
        };
        try
        {
            var failed = Record.Exception(() => fx.Records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX,
                BinaryDirectory = game.BinaryDirectory!, Files = [new LinuxDeployedFile { Path = payload, Sha256 = FileHash.Sha256HexOf("after"u8.ToArray()) }]
            })));
            Assert.True(failed is IOException or InvalidOperationException, failed?.ToString());
        }
        finally
        {
            fx.Records.SaveFaultForTesting = null;
        }
        var rollback = await scope.RollbackAsync();
        Assert.False(rollback.Success); // the undo could not finish: the game is held
        Assert.Equal("after", File.ReadAllText(payload));
    }

    [PermissionDenialTheory]
    [MemberData(nameof(Languages))]
    public async Task AnArchiveInstall_OnAGameHeldByAnUndoThatFailed_IsRefusedWithTheRestartText_AndNothingIsInstalled(string language)
    {
        var (fx, game) = await StartAsync(language);
        using var _ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "Foo Game HDR.zip"), ("renodx-foogame.addon64", AddonBytes()));
        var side = Directory.CreateDirectory(Path.Combine(game.InstallPath, "side")).FullName;
        try
        {
            await HoldAsync(fx, game, side);
            var offer = await ArchiveOfferAsync(fx, zip);
            var lines = Lines(fx);

            Assert.False(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game));

            var refusal = UiLocalizationService.PresentFor(language, DeploymentJournal.EarlierUndoFailedMessage);
            var line = lines.FirstOrDefault(l => l.Contains(refusal, StringComparison.Ordinal));
            Assert.True(line is not null, string.Join(" | ", lines));
            Assert.Contains(language == "pt-BR" ? "Reinicie o RHI para concluir" : "Restart RHI to finish it", line, StringComparison.Ordinal);
            Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-foogame.addon64"))); // nothing installed
            Assert.Null(new LinuxDeploymentRecordStore(Path.Combine(fx.DataDir, "deployments.json")).Find(game, ManagedComponentKind.RenoDX));
            Assert.True(File.Exists(zip)); // a dropped archive is never removed
            Assert.True(ScratchIsEmpty(fx));
        }
        finally
        {
            fx.Records.SaveFaultForTesting = null;
            PermissionDenial.MakeWritable(side);
        }
    }

    // w10 follow-up D15: the game is held because the undo of its OWN earlier RenoDX add-on could not finish (the add-on
    // it placed is still there: its folder turned read-only). The archive install gets the hold's restart text — not
    // "Foreign RenoDX protection", which took RHI's own half-undone add-on for someone else's.
    [PermissionDenialTheory]
    [MemberData(nameof(Languages))]
    public async Task AnArchiveInstall_OnAGameHeldByItsOwnRenoDxUndo_GetsTheRestartText_NotTheForeignFileRefusal(string language)
    {
        var (fx, game) = await StartAsync(language);
        using var _ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "Foo Game HDR.zip"), ("renodx-foogame.addon64", AddonBytes()));
        var offer = await ArchiveOfferAsync(fx, zip);
        var earlier = Path.Combine(game.BinaryDirectory!, "renodx-foogame-earlier.addon64");
        try
        {
            await HoldAsync(fx, game, game.BinaryDirectory!, Path.GetFileName(earlier), existedBefore: false);
            Assert.True(File.Exists(earlier)); // the earlier change's own add-on, its undo not finished
            var lines = Lines(fx);

            Assert.False(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game));

            var all = string.Join(" | ", lines);
            var refusal = UiLocalizationService.PresentFor(language, DeploymentJournal.EarlierUndoFailedMessage);
            Assert.True(lines.Any(l => l.Contains(refusal, StringComparison.Ordinal)), all);
            Assert.DoesNotContain(UiLocalizationService.PresentFor(language,
                "Foreign RenoDX protection: another RenoDX add-on already exists at " + earlier + ". RHI Linux will not overwrite or stack it automatically."), all, StringComparison.Ordinal);
            Assert.DoesNotContain("Foreign RenoDX", all, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-foogame.addon64"))); // nothing installed
            Assert.True(ScratchIsEmpty(fx));
        }
        finally
        {
            fx.Records.SaveFaultForTesting = null;
            PermissionDenial.MakeWritable(game.BinaryDirectory!);
        }
    }
}
