using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w8 integration: Luma's ⚙ HDR and TAA (one journaled operation each on Luma's record).
// #204: the commit write landing and then throwing is finished as committed in both seam shapes, and crash recovery agrees.
// reshade-ini review 4: TAA's Engine.ini goes through RenoDX's one Engine.ini tracker, so a rollback (in process or at the
// next start) keeps a user's read-only lock and takes away the folders the write created.
public sealed partial class LinkedIniSettingsJournalTests
{
    private const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static TheoryData<LandedShape, string> LandedLumaOperations() => LandedRow.ShapesTimes("hdr", "taa");

    private (LinkedFixture Fx, CommitPointCase Case) LumaRig(string name, string operation)
    {
        var fx = Fixture(name, luma: true);
        if (operation == "taa") PlainEngineIni(fx, "LumaGame");
        var service = new LumaSettingsService(fx.Records, fx.Journal, _ => null);
        Func<Task<ComponentOperationResult>> act = operation == "hdr"
            ? () => service.SetHdrAsync(fx.Game, LumaHdrMode.Off)
            : () => service.SetTaaAsync(fx.Game, on: true);
        var root = Path.GetDirectoryName(fx.Game.InstallPath)!;
        // Luma's record in this fixture names a stand-in add-on that is not on disk: not compared against files.
        return (fx, new CommitPointCase(root, [fx.Game.InstallPath, fx.Game.PrefixPath!], fx.Records, fx.LedgerPath, fx.Journal, fx.JournalDir, act,
            Describes: _ => false));
    }

    [Theory]
    [MemberData(nameof(LandedLumaOperations))]
    [Trait("Category", "CommitPoint")]
    public async Task Luma_CommitWriteLandsThenThrows_IsKept_AndCrashRecoveryAgrees(LandedShape shape, string operation)
    {
        var (fx, c) = LumaRig($"cp-luma-{operation}-{shape}", operation);

        await LandedRow.RunAsync(fx.Records, shape, at: 1, c.Act);

        var record = new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.LumaFramework)!;
        if (operation == "hdr")
        {
            Assert.Contains("EnableHDR=0", File.ReadAllText(fx.Shared));
            Assert.True(record.Metadata.ContainsKey(LumaIniOverrideManager.MetadataKey));
        }
        else Assert.Contains("r.DefaultFeature.AntiAliasing=2", File.ReadAllText(EngineIniOf(fx, "LumaGame")));
        Assert.Empty(CommitPointAgreement.JournalEntries(c));

        var results = await CommitPointAgreement.AssertEveryWriteAsync(name => Task.FromResult(LumaRig($"cp-luma-{operation}-{shape}-{name}", operation).Case), shape);
        Assert.True(results[^1].Success, results[^1].Message);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task LumaTaa_ReadOnlyEngineIni_FailsOrDies_TheBytesAndTheLockAreBack(string failure)
    {
        var fx = Fixture("luma-taa-locked-" + failure, luma: true);
        var engine = PlainEngineIni(fx, "LockedGame");
        File.SetUnixFileMode(engine, ReadOnly); // the user locked it so the game cannot rewrite it
        var before = File.ReadAllBytes(engine);
        var service = new LumaSettingsService(fx.Records, fx.Journal, _ => null);

        await RunFailingAsync(fx, failure, () => service.SetTaaAsync(fx.Game, on: true), () =>
        {
            Assert.Equal(before, File.ReadAllBytes(engine));
            Assert.Equal(ReadOnly, File.GetUnixFileMode(engine));
        });
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task LumaTaa_EngineIniFolderNotThereYet_FailsOrDies_NoCreatedFolderIsLeft(string failure)
    {
        var fx = Fixture("luma-taa-fresh-" + failure, luma: true);
        var project = Path.Combine(fx.Game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", "FreshGame");
        var engine = Path.Combine(project, "Saved", "Config", "Windows", "Engine.ini");
        NameEngineIni(fx, engine);
        var service = new LumaSettingsService(fx.Records, fx.Journal, _ => null);

        await RunFailingAsync(fx, failure, () => service.SetTaaAsync(fx.Game, on: true), () =>
        {
            Assert.False(File.Exists(engine));
            Assert.False(Directory.Exists(project), "the folders the write created are gone");
        });
    }

    // A plain (not linked) Engine.ini in the prefix's UE project folder, named by Luma's record (an earlier install).
    private static string PlainEngineIni(LinkedFixture fx, string project)
    {
        var engine = EngineIniOf(fx, project);
        Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        File.WriteAllText(engine, "[SystemSettings]\nr.Mine=1\n");
        NameEngineIni(fx, engine);
        return engine;
    }

    private static string EngineIniOf(LinkedFixture fx, string project)
        => Path.Combine(fx.Game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", project, "Saved", "Config", "Windows", "Engine.ini");

    private static void NameEngineIni(LinkedFixture fx, string engine)
    {
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.LumaFramework)!;
        record.Metadata[LumaEngineIniOverrideManager.MetadataKey] = JsonSerializer.Serialize(new LumaEngineIniOverrideState(engine, false, []));
        fx.Records.Upsert(record);
    }
}
