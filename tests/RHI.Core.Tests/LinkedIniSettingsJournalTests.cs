using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #213: the per-game settings that edit a linked ReShade.ini (presets, Luma ⚙ "HDR on first boot") or a linked
/// Engine.ini (Luma ⚙ TAA) journal the file a write through the link replaces — the shared file — never the link. A
/// rollback (failed ledger write, or crash recovery at the next start) keeps the link a link and puts the shared file back
/// byte for byte, so no other game linked to it keeps a line no record lists. Same shape as
/// <see cref="ReShadeLinkedIniJournalTests"/> (#207, ReShade's own install/update/removal).
/// </summary>
public sealed class LinkedIniSettingsJournalTests : IDisposable
{
    private const string Preset = "[GENERAL]\nTechniques=Bloom@Bloom.fx\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-linked-settings-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _shared = [];

    public LinkedIniSettingsJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        // Only this class's own GUID folders in the shared (test-isolated) ReShade config folder.
        foreach (var shared in _shared)
            try { Directory.Delete(Path.GetDirectoryName(shared)!, recursive: true); } catch { }
    }

    public static TheoryData<string> Failures => ReShadeLinkedIniJournalTests.Failures;

    // ── presets ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task PresetInstall_FailsOrDies_TheLinkStaysALink_AndTheSharedFileIsBackByteForByte(string failure)
    {
        var fx = Fixture("preset-install-" + failure);
        var service = new ReShadePresetService(fx.Records, fx.Journal, _ => null);

        await FailAsync(fx, failure, () => service.InstallAsync(fx.Game, fx.Preset), Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.SharedBefore),
            also: () =>
            {
                Assert.False(File.Exists(Path.Combine(fx.Bin, "Mine.ini")));
                Assert.Empty(ReShadePresetService.Read(new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.ReShade)));
            });
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task PresetRemoval_FailsOrDies_TheLinkStaysALink_AndTheSharedFileKeepsThePresetPath(string failure)
    {
        var fx = Fixture("preset-remove-" + failure);
        var service = new ReShadePresetService(fx.Records, fx.Journal, _ => null);
        var installed = await service.InstallAsync(fx.Game, fx.Preset);
        Assert.True(installed.Success, installed.Message);
        var afterInstall = File.ReadAllBytes(fx.Shared);
        Assert.Contains(@"PresetPath=.\Mine.ini", Encoding.UTF8.GetString(afterInstall)); // RHI's line went into the shared file

        await FailAsync(fx, failure, () => service.RemoveAsync(fx.Game, "Mine.ini"), afterInstall,
            also: () =>
            {
                Assert.Equal(Preset, File.ReadAllText(Path.Combine(fx.Bin, "Mine.ini"))); // the copy is back too
                Assert.Equal("Mine.ini", Assert.Single(ReShadePresetService.Read(new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.ReShade))).FileName);
            });
    }

    [Fact]
    public async Task Presets_WriteThroughTheLink_IntoTheSharedFile_AndRemovalTakesTheirLineBack()
    {
        var fx = Fixture("preset-ok");
        var service = new ReShadePresetService(fx.Records, fx.Journal, _ => null);

        var installed = await service.InstallAsync(fx.Game, fx.Preset);
        Assert.True(installed.Success, installed.Message);
        Assert.Equal(fx.Shared, new FileInfo(Path.Combine(fx.Bin, "ReShade.ini")).LinkTarget);
        Assert.Contains(@"PresetPath=.\Mine.ini", File.ReadAllText(fx.Shared));

        var removed = await service.RemoveAsync(fx.Game, "Mine.ini");
        Assert.True(removed.Success, removed.Message);
        ReShadeLinkedIniJournalTests.AssertLinked(fx.Bin, fx.Shared, Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.SharedBefore));
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
    }

    [Fact]
    public async Task PresetInstall_ReShadeIniLinkedOutsideTheAllowedFolders_IsRefusedBeforeAnyWrite()
    {
        var fx = Fixture("preset-outside", link: false);
        var ini = Path.Combine(fx.Bin, "ReShade.ini");
        var outside = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "preset-outside", "Documents")).FullName, "ReShade.ini");
        File.WriteAllText(outside, ReShadeLinkedIniJournalTests.SharedBefore);
        File.CreateSymbolicLink(ini, outside);
        var before = CancelAtWritePhase.Snapshot(fx.Bin);
        var recordBefore = JsonSerializer.Serialize(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade));

        var result = await new ReShadePresetService(fx.Records, fx.Journal, _ => null).InstallAsync(fx.Game, fx.Preset);

        Assert.False(result.Success);
        Assert.StartsWith(RenoDxIniOverrideManager.LinkRefusalPrefix, result.Message, StringComparison.Ordinal);
        Assert.Equal(outside, new FileInfo(ini).LinkTarget);
        Assert.Equal(ReShadeLinkedIniJournalTests.SharedBefore, File.ReadAllText(outside));
        CancelAtWritePhase.AssertUnchanged(before, fx.Bin);
        Assert.False(Directory.Exists(fx.JournalDir), "refused before an operation began");
        Assert.Equal(recordBefore, JsonSerializer.Serialize(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)));
    }

    [Fact]
    public async Task PresetRemoval_ReShadeIniLinkedOutsideTheAllowedFoldersSince_RemovesThePreset_AndLeavesTheLinkAlone()
    {
        // The user re-linked ReShade.ini to a folder RHI never writes through after the install: the preset still comes
        // out (a removal is never blocked by the user's file), and the file behind that link stays exactly as it is.
        var fx = Fixture("preset-remove-outside", link: false);
        var service = new ReShadePresetService(fx.Records, fx.Journal, _ => null);
        Assert.True((await service.InstallAsync(fx.Game, fx.Preset)).Success);
        var ini = Path.Combine(fx.Bin, "ReShade.ini");
        var outside = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "preset-remove-outside", "Documents")).FullName, "ReShade.ini");
        File.Copy(ini, outside);
        File.Delete(ini);
        File.CreateSymbolicLink(ini, outside);
        var outsideBefore = File.ReadAllBytes(outside);

        var removed = await service.RemoveAsync(fx.Game, "Mine.ini");

        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "Mine.ini")));
        Assert.Empty(service.Installed(fx.Game));
        Assert.Equal(outside, new FileInfo(ini).LinkTarget);
        Assert.Equal(outsideBefore, File.ReadAllBytes(outside));
    }

    // ── Luma ⚙ ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task LumaHdr_FailsOrDies_TheLinkStaysALink_AndTheSharedFileIsBackByteForByte(string failure)
    {
        var fx = Fixture("luma-hdr-" + failure, luma: true);
        var service = new LumaSettingsService(fx.Records, fx.Journal, _ => null);

        await FailAsync(fx, failure, () => service.SetHdrAsync(fx.Game, LumaHdrMode.Off), Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.SharedBefore));
    }

    [Fact]
    public async Task LumaHdr_WritesThroughTheLink_IntoTheSharedFile()
    {
        var fx = Fixture("luma-hdr-ok", luma: true);

        var result = await new LumaSettingsService(fx.Records, fx.Journal, _ => null).SetHdrAsync(fx.Game, LumaHdrMode.Off);

        Assert.True(result.Success, result.Message);
        Assert.Equal(fx.Shared, new FileInfo(Path.Combine(fx.Bin, "ReShade.ini")).LinkTarget);
        Assert.Contains("EnableHDR=0", File.ReadAllText(fx.Shared));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task LumaTaa_LinkedEngineIni_FailsOrDies_TheLinkStaysALink_AndItsTargetIsBackByteForByte(string failure)
    {
        var fx = Fixture("luma-taa-" + failure, luma: true);
        var (engine, target) = LinkedEngineIni(fx);
        var targetBefore = File.ReadAllBytes(target);
        var service = new LumaSettingsService(fx.Records, fx.Journal, _ => null);

        await RunFailingAsync(fx, failure, () => service.SetTaaAsync(fx.Game, on: true), () =>
        {
            Assert.Equal(target, new FileInfo(engine).LinkTarget); // still the link, to the same file
            Assert.Equal(targetBefore, File.ReadAllBytes(target));
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(engine)!), file => Path.GetFileName(file).StartsWith("Engine.ini.", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task LumaTaa_LinkedEngineIni_WritesThroughTheLink()
    {
        var fx = Fixture("luma-taa-ok", luma: true);
        var (engine, target) = LinkedEngineIni(fx);

        var result = await new LumaSettingsService(fx.Records, fx.Journal, _ => null).SetTaaAsync(fx.Game, on: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal(target, new FileInfo(engine).LinkTarget);
        Assert.Contains("r.DefaultFeature.AntiAliasing=2", File.ReadAllText(target));
    }

    // A UE project's Engine.ini in the prefix, linked to the same project's other platform folder (allowed by
    // LumaEngineIniOverrideManager.LinkPolicy: a file named Engine.ini inside the same UE project folder). Luma's record
    // already names it (an earlier install or ⚙ change).
    private static (string Engine, string Target) LinkedEngineIni(LinkedFixture fx)
    {
        var project = Path.Combine(fx.Game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", "LinkedGame");
        var engine = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "Saved", "Config", "Windows")).FullName, "Engine.ini");
        var target = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "Saved", "Config", "WinGDK")).FullName, "Engine.ini");
        File.WriteAllText(target, "[SystemSettings]\nr.Mine=1\n");
        File.CreateSymbolicLink(engine, target);
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.LumaFramework)!;
        record.Metadata[LumaEngineIniOverrideManager.MetadataKey] = JsonSerializer.Serialize(new LumaEngineIniOverrideState(engine, false, []));
        fx.Records.Upsert(record);
        return (engine, target);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    private static Task FailAsync(LinkedFixture fx, string failure, Func<Task<ComponentOperationResult>> run, byte[] expected, Action? also = null)
        => RunFailingAsync(fx, failure, run, () =>
        {
            ReShadeLinkedIniJournalTests.AssertLinked(fx.Bin, fx.Shared, expected);
            also?.Invoke();
        });

    // The operation's one ledger save (its commit point) fails or dies; the assertions run on the rolled-back state.
    private static Task RunFailingAsync(LinkedFixture fx, string failure, Func<Task<ComponentOperationResult>> run, Action assertRolledBack)
        => ReShadeLinkedIniJournalTests.FailAsync(fx.Records, fx.Journal, fx.JournalDir, fx.LedgerPath, failure, saveThatFails: 1, run, assertRolledBack);

    private sealed record LinkedFixture(DetectedGame Game, string Bin, string Shared, string Preset, string JournalDir, string LedgerPath,
        LinuxDeploymentRecordStore Records, DeploymentJournal Journal);

    private LinkedFixture Fixture(string name, bool link = true, bool luma = false)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "Binaries", "Win64")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
        var shared = ReShadeLinkedIniJournalTests.LinkSharedIni(bin, link);
        _shared.Add(shared);
        var game = new DetectedGame
        {
            Name = "Linked " + name, InstallPath = Path.Combine(root, "Game"), BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game-Win64-Shipping.exe"),
            PrefixPath = prefix, Runner = "Proton", EngineHint = "Unreal Engine 4", Source = "Test", SteamAppId = 4313
        };
        var ledger = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "state")).FullName, "records.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(root, "journal");
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, records);
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, BinaryDirectory = bin,
            Component = luma ? ManagedComponentKind.LumaFramework : ManagedComponentKind.ReShade,
            Files = [new LinuxDeployedFile { Path = Path.Combine(bin, luma ? "Luma.addon64" : "dxgi.dll"), Sha256 = "00" }],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["Test.Kept"] = "1" }
        });
        var preset = Path.Combine(root, "Mine.ini");
        File.WriteAllText(preset, Preset);
        return new LinkedFixture(game, bin, shared, preset, journalDir, ledger, records, journal);
    }
}
