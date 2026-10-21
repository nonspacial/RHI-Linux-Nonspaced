using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #206 (follow-up to #203): "Apply to all games" (and the per-game Apply) and a preset's install/removal change
/// ReShade.ini; where ReShade is RHI's each is one journaled operation on ReShade's record that records the write with
/// the #203 rule (the new hash only while the file still held RHI's last write). So ReShade's removal still deletes a
/// ReShade.ini RHI created and nobody changed since, keeps one the user changed, and takes RHI's search paths out of a
/// file that was there before (the Apply's settings stay: the user asked for them, as with Deploy ReShade.ini). A crash at
/// the commit point puts the files and the record back as before the Apply; one after it keeps both.
/// </summary>
public sealed class ReShadeIniWriteRecordTests : IDisposable
{
    private const string Template = "[GENERAL]\nEffectSearchPaths=.\\rhi-test\\**\nTextureSearchPaths=.\\rhi-test-tex\\**\n";
    private const string Preset = "[GENERAL]\nTechniques=Bloom@Bloom.fx\n";
    private static readonly LinuxAppSettings Settings = new() { ReShadeVariableListUseTabs = false, ScreenshotHotkey = "45,0,0,0" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-ini-write-record-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _shared = [];

    public ReShadeIniWriteRecordTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        foreach (var shared in _shared)
            try { Directory.Delete(Path.GetDirectoryName(shared)!, recursive: true); } catch { }
    }

    // ── Apply to all games → ReShade removal ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RhiCreatedIni_ApplyToAll_ThenReShadeRemoval_DeletesIt()
    {
        var fx = await InstalledAsync("created");
        Assert.True(fx.IniCreatedByRhi);

        Assert.Equal(new IniBatchResult(1, 0, 0), await fx.Ini.ApplyReShadeSettingsToAllAsync([fx.Game], Settings));
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(fx.IniPath));
        AssertRecordDescribesTheIni(fx);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(fx.IniPath));
    }

    [Fact]
    public async Task RhiCreatedIni_PerGameApply_ThenReShadeRemoval_DeletesIt()
    {
        // Settings' "Apply to the selected game" goes through the same method.
        var fx = await InstalledAsync("per-game");

        Assert.True((await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings)).Success);
        AssertRecordDescribesTheIni(fx);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(fx.IniPath));
    }

    [Fact]
    public async Task GamesOwnIni_ApplyToAll_ThenReShadeRemoval_TakesBackRhisPaths_AndKeepsTheAppliedSettings()
    {
        const string original = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n\n[OVERLAY]\nKeepMe=1\n";
        var fx = await InstalledAsync("own", existingIni: original);
        Assert.False(fx.IniCreatedByRhi);
        Assert.True((await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings)).Success);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);

        // Byte for byte: the file from before RHI, plus only the settings the user applied (RHI's search paths gone).
        var expected = IniLines.FromBytes(Encoding.UTF8.GetBytes(original));
        ReShadeIniManager.ApplySettings(expected, ReShadeGlobalSettingsApplier.SettingsFor(Settings, fx.Game));
        Assert.Equal(string.Join("\n", expected) + "\n", File.ReadAllText(fx.IniPath));
    }

    [Fact]
    public async Task RhiCreatedIni_ChangedByTheUserAfterTheApply_IsKeptByReShadeRemoval()
    {
        var fx = await InstalledAsync("changed-after");
        Assert.True((await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings)).Success);
        File.AppendAllText(fx.IniPath, "\n[MINE]\nKeepMe=1\n");

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);

        var text = File.ReadAllText(fx.IniPath);
        Assert.Contains("KeepMe=1", text);
        Assert.Contains("VariableListUseTabs=0", text);
    }

    [Fact]
    public async Task RhiCreatedIni_ChangedByTheUserBeforeTheApply_StaysTheUsers_AndRemovalKeepsIt()
    {
        // The file held the user's change when the Apply wrote it: the record keeps RHI's earlier hash (the file is no
        // longer RHI's alone), so removal keeps it with the user's line.
        var fx = await InstalledAsync("changed-before");
        var installedHash = IniEntry(fx)!.Sha256;
        File.AppendAllText(fx.IniPath, "\n[MINE]\nKeepMe=1\n");

        Assert.True((await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings)).Success);
        Assert.Equal(installedHash, IniEntry(fx)!.Sha256);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.Contains("KeepMe=1", File.ReadAllText(fx.IniPath));
    }

    [Fact]
    public async Task LinkedIni_ApplyToAll_ThenReShadeRemoval_KeepsTheLink_AndTakesOnlyRhisPathsOutOfTheSharedFile()
    {
        var fx = await InstalledAsync("linked", linked: true);
        Assert.Contains(@".\rhi-test\**", File.ReadAllText(fx.Shared!));
        Assert.True((await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings)).Success);
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(fx.Shared!));
        Assert.Equal(fx.Shared, new FileInfo(fx.IniPath).LinkTarget);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);

        var expected = IniLines.FromBytes(Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.SharedBefore));
        ReShadeIniManager.ApplySettings(expected, ReShadeGlobalSettingsApplier.SettingsFor(Settings, fx.Game));
        Assert.Equal(fx.Shared, new FileInfo(fx.IniPath).LinkTarget); // never deleted: a link is the user's
        Assert.Equal(string.Join("\n", expected) + "\n", File.ReadAllText(fx.Shared!));
    }

    [Theory]
    [MemberData(nameof(LinkedFailures))]
    public async Task LinkedIni_Apply_FailsOrDies_TheLinkStaysALink_AndTheSharedFileIsBackByteForByte(string failure)
    {
        var fx = await InstalledAsync("linked-" + failure, linked: true);
        var afterInstall = File.ReadAllBytes(fx.Shared!);

        await ReShadeLinkedIniJournalTests.FailAsync(fx.Records, fx.Journal, fx.JournalDir, fx.LedgerPath, failure, saveThatFails: 1,
            async () =>
            {
                try { return await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings); }
                catch (IOException ex) { return ComponentOperationResult.Fail(ex.Message); } // a failure after the first write throws
            },
            () => ReShadeLinkedIniJournalTests.AssertLinked(fx.Bin, fx.Shared!, afterInstall));
    }

    public static TheoryData<string> LinkedFailures => ReShadeLinkedIniJournalTests.Failures;

    [Fact]
    public async Task GamesOwnIni_DeletedByTheUser_ApplyCreatesIt_AndReShadeRemovalDeletesIt()
    {
        // The game's own ReShade.ini (not RHI's) is gone; the Apply creates a new one, which is RHI's.
        var fx = await InstalledAsync("own-deleted", existingIni: "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n");
        Assert.False(fx.IniCreatedByRhi);
        File.Delete(fx.IniPath);

        Assert.True((await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings)).Success);
        Assert.True(File.Exists(fx.IniPath));

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(fx.IniPath));
    }

    [Fact]
    public async Task ManageIniOff_Apply_IsJournaled_ButRecordsNoIni()
    {
        // ReShade installed with "Manage ReShade.ini" off: the record names no ReShade.ini, so nothing is recorded and the
        // game's own file stays the user's (removal leaves it); the write is still journaled on ReShade's record.
        var fx = await InstalledAsync("manage-off", existingIni: "[OVERLAY]\n", manageIni: false);
        Assert.Null(IniEntry(fx));

        Assert.True((await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings)).Success);
        Assert.Null(IniEntry(fx));
        Assert.Empty(Directory.GetFiles(fx.JournalDir));

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(fx.IniPath));
    }

    // ── presets → ReShade removal ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RhiCreatedIni_PresetInstalled_ThenReShadeRemoval_DeletesIt()
    {
        var fx = await InstalledAsync("preset");
        Assert.True((await fx.Presets.InstallAsync(fx.Game, fx.PresetSource)).Success);
        Assert.Contains(@"PresetPath=.\Mine.ini", File.ReadAllText(fx.IniPath));
        AssertRecordDescribesTheIni(fx);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);

        Assert.False(File.Exists(fx.IniPath));
        Assert.True(File.Exists(Path.Combine(fx.Bin, "Mine.ini"))); // removing ReShade leaves the user's presets (#180)
    }

    [Fact]
    public async Task RhiCreatedIni_PresetInstalledAndRemoved_ThenReShadeRemoval_DeletesIt()
    {
        var fx = await InstalledAsync("preset-removed");
        Assert.True((await fx.Presets.InstallAsync(fx.Game, fx.PresetSource)).Success);
        Assert.True((await fx.Presets.RemoveAsync(fx.Game, "Mine.ini")).Success);
        AssertRecordDescribesTheIni(fx);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(fx.IniPath));
    }

    [Fact]
    public async Task RhiCreatedIni_PresetInstalled_ThenReShadeUpdated_KeepsTheSelection_AndRemovalStillDeletesIt()
    {
        // Recording the PresetPath write makes the update regenerate RHI's unchanged file from the template: the
        // regenerated file gets RHI's preset selection back, and stays RHI's.
        var fx = await InstalledAsync("preset-update");
        Assert.True((await fx.Presets.InstallAsync(fx.Game, fx.PresetSource)).Success);

        var updated = await fx.ReShade.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true });

        Assert.True(updated.Success, updated.Message);
        Assert.Contains(@"PresetPath=.\Mine.ini", File.ReadAllText(fx.IniPath));
        AssertRecordDescribesTheIni(fx);
        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(fx.IniPath));
    }

    [Fact]
    public async Task RhiCreatedIni_PresetInstalledThenRemoved_ThenReShadeUpdated_SelectsNoPreset()
    {
        var fx = await InstalledAsync("preset-update-removed");
        Assert.True((await fx.Presets.InstallAsync(fx.Game, fx.PresetSource)).Success);
        Assert.True((await fx.Presets.RemoveAsync(fx.Game, "Mine.ini")).Success);

        Assert.True((await fx.ReShade.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);

        Assert.DoesNotContain("PresetPath", File.ReadAllText(fx.IniPath));
    }

    [Fact]
    public void Apply_PresetSelection_GoesOnlyIntoANewFile()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "apply-preset")).FullName;
        var defaults = new ReShadeIniDefaults { Template = Encoding.UTF8.GetBytes(Template), PresetFileName = "Mine.ini" };

        ReShadeIniManager.Apply(dir, [], [], defaults);
        Assert.Contains(@"PresetPath=.\Mine.ini", File.ReadAllText(Path.Combine(dir, "ReShade.ini")));

        File.WriteAllText(Path.Combine(dir, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n"); // the user's own file, no preset
        ReShadeIniManager.Apply(dir, [], [], defaults);
        Assert.DoesNotContain("PresetPath", File.ReadAllText(Path.Combine(dir, "ReShade.ini")));

        File.Delete(Path.Combine(dir, "ReShade.ini"));
        ReShadeIniManager.Apply(dir, [], [], new ReShadeIniDefaults { PresetFileName = "Mine.ini" }); // no template: RHI's baseline
        Assert.Contains(@"PresetPath=.\Mine.ini", File.ReadAllText(Path.Combine(dir, "ReShade.ini")));
    }

    [Fact]
    public void App_WiresThePresetSelectionIntoReShadesIniDefaults()
    {
        // The production graph (AppComposition, the factory App calls): ReShade's install defaults carry the preset RHI
        // selected, read from ReShade's record in the app's ledger.
        var ledger = new LinuxDeploymentRecordStore(Path.Combine(_root, "app-records.json"));
        var game = new DetectedGame { Name = "Wired", InstallPath = _root, BinaryDirectory = _root, ExecutablePath = Path.Combine(_root, "g.exe"), Runner = "Proton", SteamAppId = 4999 };
        ledger.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade, BinaryDirectory = _root,
            Metadata = new(StringComparer.OrdinalIgnoreCase)
            {
                [ReShadePresetService.MetadataKey] = JsonSerializer.Serialize(new[] { new InstalledReShadePreset("Mine.ini", "00") }),
                [ReShadePresetService.SelectedMetadataKey] = "Mine.ini"
            }
        });
        using var http = new HttpClient(new Offline());
        var wine = new WineDllOverrideService();
        var app = RHI.Desktop.AppComposition.Create(http, ledger, wine, LinuxDeploymentRecovery.CreateJournal(wine));

        var reshade = Assert.Single(RepairStoreWiringTests.Reachable(app).OfType<ReShadeDeploymentService>());

        Assert.Equal("Mine.ini", reshade.IniDefaults!(game, false)!.PresetFileName);
    }

    [Fact]
    public async Task RhiCreatedIni_ChangedByTheUser_ThenPresetInstalled_RemovalKeepsTheUsersLine()
    {
        var fx = await InstalledAsync("preset-changed");
        File.AppendAllText(fx.IniPath, "\n[MINE]\nKeepMe=1\n");
        Assert.True((await fx.Presets.InstallAsync(fx.Game, fx.PresetSource)).Success);

        Assert.True((await fx.ReShade.RemoveAsync(fx.Game)).Success);
        Assert.Contains("KeepMe=1", File.ReadAllText(fx.IniPath));
    }

    // ── crash and cancel ─────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, int> CrashPoints => new()
    {
        // Each game of the batch is one operation with one ledger write (its commit point): saves 1 and 2.
        { "dies-before-the-write", 1 }, { "dies-before-the-write", 2 }, { "write-lands-then-dies", 1 }, { "write-lands-then-dies", 2 }
    };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task ApplyToAll_CrashAtEveryLedgerWrite_RecoveryLeavesRecordsThatDescribeTheFiles(string shape, int save)
    {
        var a = await InstalledAsync("crash-a-" + shape + save);
        var b = await InstalledAsync("crash-b-" + shape + save, shareWith: a);
        var games = new[] { a, b };
        foreach (var fx in games) File.WriteAllText(Path.Combine(fx.Bin, "ReShade2.ini"), "[OVERLAY]\n"); // a numbered sibling
        var before = games.Select(fx => (Files: CancelAtWritePhase.Snapshot(fx.Bin), Record: RecordJson(fx))).ToArray();

        var saves = 0;
        using (var death = new SimulatedDeath(a.Journal))
        {
            if (shape == "dies-before-the-write") a.Records.SaveFaultForTesting = () => ++saves == save ? death.Die() : null;
            else a.Records.SaveWrittenFaultForTesting = () => ++saves == save ? death.Die() : null;
            var (died, _) = await death.RunUntilDeathAsync(() => a.Ini.ApplyReShadeSettingsToAllAsync([a.Game, b.Game], Settings));
            Assert.True(died);

            var report = await SimulatedDeath.RecoverAsync(a.JournalDir, a.LedgerPath);
            var landed = shape == "write-lands-then-dies";
            Assert.Equal(landed ? JournalRecoveryOutcome.AlreadyCommitted : JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            var fresh = new LinuxDeploymentRecordStore(a.LedgerPath);
            for (var i = 0; i < games.Length; i++)
            {
                var fx = games[i];
                // Every game: the record names the bytes that are in the file (RHI's last write either way).
                var entry = fresh.Find(fx.Game, ManagedComponentKind.ReShade)!.Files.Single(f => Path.GetFileName(f.Path) == "ReShade.ini");
                Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(fx.IniPath), entry.Sha256!), $"game {i + 1}: record and file disagree");
                var applied = i + 1 < save || (i + 1 == save && landed);
                if (applied)
                {
                    Assert.Contains("VariableListUseTabs=0", File.ReadAllText(fx.IniPath));
                    Assert.Contains("VariableListUseTabs=0", File.ReadAllText(Path.Combine(fx.Bin, "ReShade2.ini")));
                }
                else
                {
                    CancelAtWritePhase.AssertUnchanged(before[i].Files, fx.Bin); // main and numbered file as before the Apply
                    var record = JsonSerializer.Serialize(Comparable(fresh.Find(fx.Game, ManagedComponentKind.ReShade)!));
                    Assert.Equal(before[i].Record, record);
                }
            }
        }
    }

    [Fact]
    public async Task Apply_CancelAtTheWritePhase_RunsToTheEnd_AndIsRecorded()
    {
        // Cancel is honoured only before the first write: once the write phase began the game's apply completes.
        var fx = await InstalledAsync("cancel-complete");

        var outcome = await CancelAtWritePhase.RunAsync(token => fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings, token), [fx.Bin]);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(fx.IniPath));
        AssertRecordDescribesTheIni(fx);
    }

    [Fact]
    public async Task Apply_CancelAtTheWritePhase_ThenTheSaveFails_RollsBackWithAnUncancelledToken()
    {
        var fx = await InstalledAsync("cancel-rollback");
        File.WriteAllText(Path.Combine(fx.Bin, "ReShade2.ini"), "[OVERLAY]\n");
        var record = RecordJson(fx);

        var outcome = await CancelAtWritePhase.RunAsync(async token =>
        {
            try { return await fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings, token); }
            catch (IOException ex) { return ComponentOperationResult.Fail(ex.Message); }
        }, [fx.Bin], atWritePhase: () => fx.Records.SaveFaultForTesting = () => new IOException("disk full (test)"));
        fx.Records.SaveFaultForTesting = null;

        Assert.False(outcome.Result.Success);
        CancelAtWritePhase.AssertUnchanged(outcome.Before, fx.Bin);
        Assert.Equal(record, RecordJson(fx));
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
    }

    [Fact]
    public async Task ApplyToAll_CancelAfterTheFirstGamesFirstWrite_ThatGameIsCommitted_TheNextIsNeverStarted()
    {
        var a = await InstalledAsync("batch-cancel-a");
        var b = await InstalledAsync("batch-cancel-b", shareWith: a);
        var bBefore = CancelAtWritePhase.Snapshot(b.Bin);
        var bRecord = RecordJson(b);
        using var cts = new CancellationTokenSource();
        OperationCancellation.WritePhaseObserverForTesting = _ => cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.Ini.ApplyReShadeSettingsToAllAsync([a.Game, b.Game], Settings, cts.Token));
        }
        finally { OperationCancellation.WritePhaseObserverForTesting = null; }

        Assert.Contains("VariableListUseTabs=0", File.ReadAllText(a.IniPath));
        AssertRecordDescribesTheIni(a);
        Assert.Empty(Directory.GetFiles(a.JournalDir));
        CancelAtWritePhase.AssertUnchanged(bBefore, b.Bin);
        Assert.Equal(bRecord, RecordJson(b));
    }

    [Fact]
    public async Task ApplyToAll_AGameWhoseJournalCannotBeWritten_IsRefused_AndTheBatchGoesOn()
    {
        var a = await InstalledAsync("journal-refused-a");
        var b = await InstalledAsync("journal-refused-b", shareWith: a);
        var aBefore = File.ReadAllBytes(a.IniPath);
        // A file where the journal folder should be: BeginAsync cannot write its entry for any game, so both are refused.
        var blocked = Path.Combine(_root, "journal-refused-blocked");
        File.WriteAllText(blocked, "x");
        var service = new UserIniService(new UserIniTemplates(Path.Combine(_root, "inis")), a.Records, _ => null, new DeploymentJournal(blocked));

        var result = await service.ApplyReShadeSettingsToAllAsync([a.Game, b.Game], Settings);

        Assert.Equal(new IniBatchResult(0, 2, 0), result);
        Assert.Equal(aBefore, File.ReadAllBytes(a.IniPath));
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertRecordDescribesTheIni(Installed fx)
        => Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(fx.IniPath), IniEntry(fx)!.Sha256!), "ReShade's record names the bytes RHI wrote last");

    private static LinuxDeployedFile? IniEntry(Installed fx)
        => new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.ReShade)!.Files
            .FirstOrDefault(f => Path.GetFileName(f.Path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase));

    private static string RecordJson(Installed fx)
        => JsonSerializer.Serialize(Comparable(new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.ReShade)!));

    // The record without its save time and operation stamp (a rolled-back op's stamp never lands; recovery may re-save).
    private static LinuxDeploymentRecord Comparable(LinuxDeploymentRecord record)
    {
        var copy = LinuxDeploymentRecordStore.Clone(record);
        copy.SavedAtUtc = default;
        copy.Metadata.Remove(LinuxDeploymentRecovery.OperationIdMetadataKey);
        return copy;
    }

    private sealed record Installed(DetectedGame Game, string Bin, string IniPath, string? Shared, string LedgerPath, string JournalDir,
        LinuxDeploymentRecordStore Records, DeploymentJournal Journal, ReShadeDeploymentService ReShade, UserIniService Ini,
        ReShadePresetService Presets, string PresetSource, bool IniCreatedByRhi);

    /// <summary>A game with RHI's ReShade installed ("Manage ReShade.ini" on, a template) through the real service.</summary>
    private async Task<Installed> InstalledAsync(string name, string? existingIni = null, bool linked = false, bool manageIni = true, Installed? shareWith = null)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "bin")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        string? shared = null;
        if (linked)
        {
            shared = ReShadeLinkedIniJournalTests.LinkSharedIni(bin);
            _shared.Add(shared);
        }
        else if (existingIni is not null) File.WriteAllText(Path.Combine(bin, "ReShade.ini"), existingIni);
        var game = new DetectedGame
        {
            Name = "Record " + name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12, Source = "Test", SteamAppId = 5000 + Math.Abs(name.GetHashCode() % 4000)
        };
        var ledger = shareWith?.LedgerPath ?? Path.Combine(Directory.CreateDirectory(Path.Combine(root, "state")).FullName, "records.json");
        var records = shareWith?.Records ?? new LinuxDeploymentRecordStore(ledger);
        var journalDir = shareWith?.JournalDir ?? Path.Combine(root, "journal");
        var journal = shareWith?.Journal ?? SimulatedDeath.NewJournal(journalDir, records).Journal;
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var http = new HttpClient(new XnaGacReShadeTests.VersionHandler());
        var cache = Path.Combine(root, "cache");
        var reshade = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), records, overrides,
            new StubExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal, dllNames: new RHI.Platform.Linux.Paths.DllNamePreferenceStore(Path.Combine(root, "dll-names.json")))
        {
            // As the app wires it (AppComposition): a regenerated ReShade.ini keeps RHI's preset selection.
            IniDefaults = ReShadePresetService.KeepingSelection((_, _) => new ReShadeIniDefaults { Template = Encoding.UTF8.GetBytes(Template) }, records)
        };
        var installed = await reshade.InstallAsync(game, new ReShadeInstallOptions { ManageIni = manageIni });
        Assert.True(installed.Success, installed.Message);
        var preset = Path.Combine(root, "Mine.ini");
        File.WriteAllText(preset, Preset);
        var created = records.Find(game, ManagedComponentKind.ReShade)!.Metadata.GetValueOrDefault("ReShade.IniCreatedByRhi") == bool.TrueString;
        return new Installed(game, bin, Path.Combine(bin, "ReShade.ini"), shared, ledger, journalDir, records, journal, reshade,
            new UserIniService(new UserIniTemplates(Path.Combine(root, "inis")), records, _ => null, journal),
            new ReShadePresetService(records, journal, _ => null), preset, created);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline (test)");
    }

    private sealed class StubExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            Write(Path.Combine(outputDirectory, "ReShade64.dll"), 0x8664);
            Write(Path.Combine(outputDirectory, "ReShade32.dll"), 0x014c);
            return Task.CompletedTask;
        }

        private static void Write(string path, ushort machine)
        {
            PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = 512;
            stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me"));
        }
    }
}
