using System.Net;
using System.Security.Cryptography;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #204, a single-record family with the app's journal (it reads the ledger again): the install's, the update's and the
// removal's commit write landing and then throwing finish the operation as committed, in both seam shapes, and crash
// recovery agrees at every ledger write.
public sealed partial class NrCostScalerTests
{
    private sealed record ScalerRig(NrCostScalerService Service, LinuxDeploymentRecordStore Records, DetectedGame Game, CommitPointCase Case);

    private ScalerRig Rig(string name, Func<NrCostScalerService, DetectedGame, Task<ComponentOperationResult>>? act = null)
    {
        var root = Path.Combine(_root, "commit-point", name);
        var bin = Path.Combine(root, "game", "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var game = new DetectedGame { Name = "Scaled", InstallPath = Path.Combine(root, "game"), ExecutablePath = exe, BinaryDirectory = bin, Source = "Manual" };
        var ledger = Path.Combine(root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        InstallOwnerInto(records, game);
        var journalDir = Path.Combine(root, "journal");
        var journal = SimulatedDeath.NewJournal(journalDir, records).Journal;
        var zip = Package();
        var http = new Fake(request => request.RequestUri!.AbsoluteUri == ApiUrl
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"tag_name":"v1.0.6","assets":[{"name":"DLSSNR-Cost-Scaler-v1.0.6.zip","browser_download_url":"{{AssetUrl}}","digest":"sha256:{{Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}}"}]}""")
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        var service = new NrCostScalerService(new HttpClient(http), records, journal, Path.Combine(root, "cache"));
        act ??= (s, g) => s.InstallAsync(g);
        return new ScalerRig(service, records, game,
            new CommitPointCase(root, [Path.Combine(root, "game")], records, ledger, journal, journalDir, () => act(service, game),
                // The owner's record keeps naming the runtime NR Cost Scaler moves to _real while it is installed (by design).
                Describes: record => record.Component == ManagedComponentKind.NrCostScaler));
    }

    private static void InstallOwnerInto(LinuxDeploymentRecordStore records, DetectedGame game)
    {
        var runtime = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll");
        File.WriteAllBytes(runtime, OwnerRuntime);
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = game.BinaryDirectory!, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = runtime, Sha256 = Convert.ToHexString(SHA256.HashData(OwnerRuntime)) }]
        });
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Install_CommitWriteLandsThenThrows_IsKept_AndRemovesLater(LandedShape shape)
    {
        var rig = Rig("install-" + shape);
        LandedLedgerWrite.Arm(rig.Records, shape, at: 1);

        var result = await rig.Service.InstallAsync(rig.Game);
        LandedLedgerWrite.Disarm(rig.Records);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentScope.LandedChangePrefix, result.Message, StringComparison.Ordinal);
        var record = new LinuxDeploymentRecordStore(rig.Case.RecordsPath).Find(rig.Game, ManagedComponentKind.NrCostScaler)!;
        Assert.All(record.Files.Where(f => !f.AllowUserChanges), f => Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(f.Path), f.Sha256!)));
        Assert.Empty(CommitPointAgreement.JournalEntries(rig.Case));
        Assert.True((await rig.Service.RemoveAsync(rig.Game)).Success);
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Path.Combine(rig.Game.BinaryDirectory!, "nvngx_dlssnr.dll")));
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Remove_CommitWriteLandsThenThrows_StaysRemoved_TheRuntimeIsBack(LandedShape shape)
    {
        var rig = Rig("remove-" + shape);
        Assert.True((await rig.Service.InstallAsync(rig.Game)).Success);
        LandedLedgerWrite.Arm(rig.Records, shape, at: 2); // 1 = the removal's stamp

        var result = await rig.Service.RemoveAsync(rig.Game);
        LandedLedgerWrite.Disarm(rig.Records);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentScope.LandedRemovalPrefix, result.Message, StringComparison.Ordinal);
        Assert.Null(new LinuxDeploymentRecordStore(rig.Case.RecordsPath).Find(rig.Game, ManagedComponentKind.NrCostScaler));
        Assert.Equal(OwnerRuntime, File.ReadAllBytes(Path.Combine(rig.Game.BinaryDirectory!, "nvngx_dlssnr.dll")));
        Assert.Empty(CommitPointAgreement.JournalEntries(rig.Case));
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task InstallAndRemoval_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        var install = await CommitPointAgreement.AssertEveryWriteAsync(name => Task.FromResult(Rig($"agree-install-{shape}-{name}").Case), shape);
        var removal = await CommitPointAgreement.AssertEveryWriteAsync(async name =>
        {
            var rig = Rig($"agree-remove-{shape}-{name}", (s, g) => s.RemoveAsync(g));
            Assert.True((await rig.Service.InstallAsync(rig.Game)).Success);
            return rig.Case;
        }, shape);

        Assert.True(install[^1].Success);
        Assert.Equal([false, true], removal.Select(r => r.Success));
    }
    // w8 commit-point review, finding 3: the ledger cannot be read after the failed commit write, so nothing was undone and
    // the next start decides; the message says so (it used to say the install "was rolled back").
    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Install_LedgerUnreadableAfterTheWrite_SaysNothingWasUndone()
    {
        var rig = Rig("undecided");
        LandedLedgerWrite.Set(rig.Records, LandedShape.InsideWrite, () =>
        {
            File.WriteAllText(rig.Case.RecordsPath, "{ not json");
            return new IOException(LandedLedgerWrite.FaultMessage);
        });

        var result = await rig.Service.InstallAsync(rig.Game);
        LandedLedgerWrite.Disarm(rig.Records);

        Assert.False(result.Success);
        Assert.EndsWith(" " + DeploymentOperation.UndecidedNote, result.Message, StringComparison.Ordinal);
        Assert.Single(CommitPointAgreement.JournalEntries(rig.Case));
    }
}
