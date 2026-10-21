using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #168 (DEP-S5): the Addon Manager's state is a ledger record per game, journaled like every other deployment, and
/// <c>addon-manager.json</c> is kept as the copy older builds read; a copy an older build changed, or a ledger an older
/// build saved without the record kind it does not know, is imported again.
/// </summary>
public sealed partial class AddonManagerLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-addon-ledger-" + Guid.NewGuid().ToString("N"));

    public AddonManagerLedgerTests() => Directory.CreateDirectory(_root);

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);
    private string LedgerPath => P("deployments.json");
    private string CopyPath => P("state", "addon-manager.json");
    private string JournalDir => P("journal");

    private sealed record Setup(ReShadeAddonManagerService Service, LinuxDeploymentRecordStore Records, DetectedGame Game, DeploymentJournal? Journal)
    {
        public string Addon(string name) => Path.Combine(Game.BinaryDirectory!, name + ".addon64");
    }

    private Setup Fixture(bool journaled = false, LinuxDeploymentRecordStore? records = null)
    {
        var game = P("game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "ReShade by crosire");
        PeArchitectureDetectorTests.WritePe(Path.Combine(game, "Game.exe"), 0x8664, 64 * 1024);
        var custom = P("custom");
        Directory.CreateDirectory(custom);
        byte marker = 1;
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
        {
            var path = Path.Combine(custom, name + ".addon64");
            if (!File.Exists(path)) File.WriteAllBytes(path, CacheForeignFileRejectionTests.Pe(0x8664, 4096, marker));
            marker++;
        }
        Directory.CreateDirectory(P("state"));
        var http = new HttpClient(new RouteHandler(req => req.RequestUri?.AbsoluteUri == ReShadeAddonManagerService.AddonsIniUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[01]\nPackageName=Generic\n", Encoding.UTF8) }
            : req.RequestUri?.AbsoluteUri == "https://manifest.test/manifest.json"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":2}", Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, P("manifest-cache.json"));
        records ??= new LinuxDeploymentRecordStore(LedgerPath);
        var journal = journaled ? SimulatedDeath.NewJournal(JournalDir, records).Journal : null;
        var service = new ReShadeAddonManagerService(http, manifest, P("addon-cache"), custom, CopyPath, records, journal);
        return new(service, records, new DetectedGame
        {
            Name = "Addon Game", Source = "Steam", SteamAppId = 4242, InstallPath = game, BinaryDirectory = game,
            ExecutablePath = Path.Combine(game, "Game.exe"), Runner = "Proton"
        }, journal);
    }

    private string Key(DetectedGame game) => LinuxDeploymentRecordStore.GameKey(game);

    // The shape every older build reads and writes (Schema 1).
    private sealed class OlderCopy
    {
        public int Schema { get; set; }
        public Dictionary<string, OlderGame> Games { get; set; } = [];
    }

    private sealed class OlderGame
    {
        public List<string> SelectedIds { get; set; } = [];
        public List<OlderFile> Files { get; set; } = [];
    }

    private sealed class OlderFile
    {
        public string AddonId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    // What an older build's save does: its own shape only (the stamp is gone).
    private void OlderBuildWritesCopy(string key, IReadOnlyList<string> ids, params string[] files)
        => File.WriteAllText(CopyPath, JsonSerializer.Serialize(new OlderCopy
        {
            Schema = 1,
            Games = new() { [key] = new OlderGame { SelectedIds = ids.ToList(), Files = files.Select(f => new OlderFile { AddonId = "custom-" + Path.GetFileNameWithoutExtension(f), Path = f, Sha256 = FileHash.Sha256Hex(f) }).ToList() } }
        }, new JsonSerializerOptions { WriteIndented = true }));

    // What an older build's ledger save does: records of a kind it does not know are read as Unknown and dropped.
    private void OlderBuildSavesLedger()
    {
        var array = JsonNode.Parse(File.ReadAllText(LedgerPath))!.AsArray();
        foreach (var node in array.Where(n => (string?)n!["Component"] == nameof(ManagedComponentKind.ReShadeAddonManager)).ToArray())
            array.Remove(node);
        File.WriteAllText(LedgerPath, array.ToJsonString());
    }

    [Fact]
    public async Task Apply_SavesTheSelectionInTheLedger_AndAStampedCopyInTheShapeOlderBuildsRead()
    {
        var s = Fixture();

        var apply = await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha", "custom-Beta"]);

        Assert.True(apply.Success, apply.Message);
        var record = Assert.IsType<LinuxDeploymentRecord>(s.Records.Find(s.Game, ManagedComponentKind.ReShadeAddonManager));
        Assert.Equal([s.Addon("Alpha"), s.Addon("Beta")], record.Files.Select(f => f.Path));
        Assert.All(record.Files, f => Assert.Equal(FileHash.Sha256Hex(f.Path), f.Sha256, ignoreCase: true));
        Assert.Equal(["custom-Alpha", "custom-Beta"], ReShadeAddonManagerService.SelectedIdsOf(record));
        Assert.Equal(s.Game.BinaryDirectory, record.BinaryDirectory);
        Assert.Equal(s.Game.Name, record.GameName);

        var older = JsonSerializer.Deserialize<OlderCopy>(File.ReadAllText(CopyPath))!;
        Assert.Equal(1, older.Schema);
        var game = older.Games[Key(s.Game)];
        Assert.Equal(["custom-Alpha", "custom-Beta"], game.SelectedIds);
        Assert.Equal(["custom-Alpha", "custom-Beta"], game.Files.Select(f => f.AddonId));
        Assert.Equal([s.Addon("Alpha"), s.Addon("Beta")], game.Files.Select(f => f.Path));
        Assert.Equal(record.Files.Select(f => f.Sha256), game.Files.Select(f => f.Sha256));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(CopyPath))!["LedgerCopySha256"]);
        Assert.Equal(["custom-Alpha", "custom-Beta"], (await s.Service.GetSnapshotAsync(s.Game)).SelectedIds);
    }

    [Fact]
    public async Task ACopyAnOlderBuildChanged_IsImported_AndItsAddonsAreThenOwned()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        // The older build swapped Alpha for Beta and saved its list (no stamp).
        File.Delete(s.Addon("Alpha"));
        File.Copy(P("custom", "Beta.addon64"), s.Addon("Beta"));
        OlderBuildWritesCopy(Key(s.Game), ["custom-Beta"], s.Addon("Beta"));

        Assert.Equal(["custom-Beta"], (await s.Service.GetSnapshotAsync(s.Game)).SelectedIds);

        var record = s.Records.Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!;
        Assert.Equal([s.Addon("Beta")], record.Files.Select(f => f.Path));
        Assert.Equal(s.Game.BinaryDirectory, record.BinaryDirectory); // kept from the record the ledger still had
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(CopyPath))!["LedgerCopySha256"]);
        var clear = await s.Service.ApplySelectionAsync(s.Game, []);
        Assert.True(clear.Success, clear.Message);
        Assert.False(File.Exists(s.Addon("Beta")));
        Assert.Null(s.Records.Find(s.Game, ManagedComponentKind.ReShadeAddonManager));
        Assert.Empty(JsonSerializer.Deserialize<OlderCopy>(File.ReadAllText(CopyPath))!.Games);
    }

    [Fact]
    public async Task ALedgerAnOlderBuildSaved_WithoutTheManagersRecords_GetsThemBackFromTheCopy()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        s.Records.Upsert(new LinuxDeploymentRecord // a record the older build knows, kept by its save
        {
            GameKey = Key(s.Game), GameName = s.Game.Name, Component = ManagedComponentKind.ReShade, BinaryDirectory = s.Game.BinaryDirectory!,
        });
        OlderBuildSavesLedger();
        Assert.Null(new LinuxDeploymentRecordStore(LedgerPath).Find(s.Game, ManagedComponentKind.ReShadeAddonManager));

        Assert.Equal([s.Addon("Alpha")], s.Service.OwnedAddonFiles(s.Game));

        var record = new LinuxDeploymentRecordStore(LedgerPath).Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!;
        Assert.Equal([s.Addon("Alpha")], record.Files.Select(f => f.Path));
        Assert.Equal(["custom-Alpha"], ReShadeAddonManagerService.SelectedIdsOf(record));
        Assert.Equal(s.Game.Name, record.GameName); // from the game's other record (Repair after move adopts by name)
    }

    [Fact]
    public async Task ACopyEditedByHand_WithTheStampLeftIn_IsImported()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        var json = JsonNode.Parse(File.ReadAllText(CopyPath))!;
        json["Games"]![Key(s.Game)]!["SelectedIds"] = new JsonArray("custom-Alpha", "custom-Gamma");
        File.WriteAllText(CopyPath, json.ToJsonString());

        Assert.Equal(["custom-Alpha", "custom-Gamma"], (await s.Service.GetSnapshotAsync(s.Game)).SelectedIds);
    }

    [Fact]
    public async Task AnImportedAddonTurnedOffForNoAddons_IsRecordedUnderItsOffName()
    {
        var s = Fixture();
        File.Copy(P("custom", "Alpha.addon64"), s.Addon("Alpha"));
        OlderBuildWritesCopy(Key(s.Game), ["custom-Alpha"], s.Addon("Alpha"));
        File.Move(s.Addon("Alpha"), s.Addon("Alpha") + ReShadeAddonSwitch.OffSuffix);

        Assert.Equal([s.Addon("Alpha")], s.Service.OwnedAddonFiles(s.Game));

        Assert.Equal([s.Addon("Alpha") + ReShadeAddonSwitch.OffSuffix],
            s.Records.Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!.Files.Select(f => f.Path));
        var clear = await s.Service.ApplySelectionAsync(s.Game, []);
        Assert.True(clear.Success, clear.Message);
        Assert.False(File.Exists(s.Addon("Alpha") + ReShadeAddonSwitch.OffSuffix));
    }

    // Repair after move re-points the copy (the record moves with the ledger): a copy this build stamped stays stamped, one
    // an older build wrote stays unstamped so it is still imported (else another game's ledger record would make it lose).
    [Fact]
    public async Task RepairAfterMove_KeepsTheCopysStampState()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        var moved = P("moved", "game");
        var move = new RHI.Platform.Linux.Settings.GameStateMove(s.Game, [(s.Game.InstallPath, moved)], []);
        Directory.CreateDirectory(P("moved"));
        Directory.Move(s.Game.InstallPath, moved);

        var stamped = JsonNode.Parse(s.Service.StageMove(move)!.After)!;
        Assert.Equal(Path.Combine(moved, "Alpha.addon64"), (string?)stamped["Games"]![Key(s.Game)]!["Files"]![0]!["Path"]);
        Assert.NotNull((string?)stamped["LedgerCopySha256"]);

        OlderBuildWritesCopy(Key(s.Game), ["custom-Alpha"], Path.Combine(moved, "Alpha.addon64"));
        var json = JsonNode.Parse(File.ReadAllText(CopyPath))!;
        json["Games"]![Key(s.Game)]!["Files"]![0]!["Path"] = s.Addon("Alpha");
        File.WriteAllText(CopyPath, json.ToJsonString());
        var unstamped = JsonNode.Parse(s.Service.StageMove(move)!.After)!;
        Assert.Equal(Path.Combine(moved, "Alpha.addon64"), (string?)unstamped["Games"]![Key(s.Game)]!["Files"]![0]!["Path"]);
        Assert.Null((string?)unstamped["LedgerCopySha256"]);
    }

    [Fact]
    public async Task AStampedCopyThatNoLongerMatchesTheLedger_IsRewrittenFromIt()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        // Another writer of the ledger (Repair after move, a key migration) changed the record; the ledger wins.
        var record = s.Records.Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!;
        var renamed = Path.Combine(s.Game.BinaryDirectory!, "Renamed.addon64");
        File.Move(s.Addon("Alpha"), renamed);
        record.Files[0].Path = renamed;
        s.Records.Upsert(record);

        Assert.Equal([renamed], s.Service.OwnedAddonFiles(s.Game));

        var copy = JsonSerializer.Deserialize<OlderCopy>(File.ReadAllText(CopyPath))!;
        Assert.Equal([renamed], copy.Games[Key(s.Game)].Files.Select(f => f.Path));
        Assert.Equal([renamed], s.Records.Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task AnUnreadableCopy_RefusesTheApply_AndTheOwnedListIsUnknown()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        var ledger = File.ReadAllBytes(LedgerPath);
        File.WriteAllText(CopyPath, "{ not json");

        var apply = await s.Service.ApplySelectionAsync(s.Game, ["custom-Beta"]);

        Assert.False(apply.Success);
        Assert.Null(s.Service.OwnedAddonFiles(s.Game));
        Assert.Null(s.Service.ReconcileForReShade(s.Game)); // ReShade's No Addons switch then leaves them alone and says so
        Assert.Equal("{ not json", File.ReadAllText(CopyPath));
        Assert.Equal(ledger, File.ReadAllBytes(LedgerPath));
        Assert.True(File.Exists(s.Addon("Alpha")));
        Assert.False(File.Exists(s.Addon("Beta")));
    }

    [Fact]
    public async Task AnUnreadableLedger_RefusesBeforeAnyDownloadOrChange()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        File.WriteAllText(LedgerPath, "[ broken");
        var copy = File.ReadAllBytes(CopyPath);

        var apply = await s.Service.ApplySelectionAsync(s.Game, ["custom-Beta"]);

        Assert.False(apply.Success);
        Assert.StartsWith(ReShadeAddonManagerService.ReconcileFailedPrefix, apply.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(s.Addon("Alpha")));
        Assert.False(File.Exists(s.Addon("Beta")));
        Assert.Equal(copy, File.ReadAllBytes(CopyPath));
        Assert.Null(s.Service.OwnedAddonFiles(s.Game));
    }

    [Fact]
    public async Task TheRecordSaveFailing_PutsTheAddonsAndTheCopyBack()
    {
        var s = Fixture();
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        var copy = File.ReadAllBytes(CopyPath);
        var alpha = File.ReadAllBytes(s.Addon("Alpha"));
        s.Records.SaveFaultForTesting = () => new IOException("disk full");

        var apply = await s.Service.ApplySelectionAsync(s.Game, ["custom-Beta"]);

        s.Records.SaveFaultForTesting = null;
        Assert.False(apply.Success);
        Assert.Equal(alpha, File.ReadAllBytes(s.Addon("Alpha")));
        Assert.False(File.Exists(s.Addon("Beta")));
        Assert.Equal(copy, File.ReadAllBytes(CopyPath));
        Assert.Equal([s.Addon("Alpha")], s.Records.Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!.Files.Select(f => f.Path));
    }

    // ---- Crash tests (in-process, simulated death at each commit point) -----------------------------------------

    [Fact]
    public async Task Change_CrashAtTheRecordSave_NextStartPutsTheOldAddonsAndTheCopyBack()
    {
        var s = Fixture(journaled: true);
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        var copy = File.ReadAllBytes(CopyPath);
        var alpha = File.ReadAllBytes(s.Addon("Alpha"));
        using var death = new SimulatedDeath(s.Journal!);
        s.Records.SaveFaultForTesting = death.Die; // the first ledger write of the change is its commit point

        var (died, _) = await death.RunUntilDeathAsync(() => s.Service.ApplySelectionAsync(s.Game, ["custom-Beta"]));
        Assert.True(died);
        Assert.True(File.Exists(s.Addon("Beta")));
        Assert.False(File.Exists(s.Addon("Alpha")));
        Assert.NotEqual(copy, File.ReadAllBytes(CopyPath)); // the copy was written inside the operation

        var report = await SimulatedDeath.RecoverAsync(JournalDir, LedgerPath);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(alpha, File.ReadAllBytes(s.Addon("Alpha")));
        Assert.False(File.Exists(s.Addon("Beta")));
        Assert.Equal(copy, File.ReadAllBytes(CopyPath));
        var after = new LinuxDeploymentRecordStore(LedgerPath);
        Assert.Equal([s.Addon("Alpha")], after.Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Change_CrashRightAfterTheRecordSave_IsKeptWithItsCopy()
    {
        var s = Fixture(journaled: true);
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        using var death = new SimulatedDeath(s.Journal!);
        s.Records.SaveWrittenFaultForTesting = death.Die; // the record landed, then the process died

        var (died, _) = await death.RunUntilDeathAsync(() => s.Service.ApplySelectionAsync(s.Game, ["custom-Beta"]));
        Assert.True(died);

        var report = await SimulatedDeath.RecoverAsync(JournalDir, LedgerPath);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.True(File.Exists(s.Addon("Beta")));
        Assert.False(File.Exists(s.Addon("Alpha")));
        var after = new LinuxDeploymentRecordStore(LedgerPath);
        var record = after.Find(s.Game, ManagedComponentKind.ReShadeAddonManager)!;
        Assert.Equal([s.Addon("Beta")], record.Files.Select(f => f.Path));
        Assert.Equal([s.Addon("Beta")], JsonSerializer.Deserialize<OlderCopy>(File.ReadAllText(CopyPath))!.Games[Key(s.Game)].Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Clear_CrashBeforeTheRecordIsGone_NextStartPutsTheAddonsBack_AndTheyStayOwned()
    {
        var s = Fixture(journaled: true);
        Assert.True((await s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"])).Success);
        var copy = File.ReadAllBytes(CopyPath);
        var alpha = File.ReadAllBytes(s.Addon("Alpha"));
        using var death = new SimulatedDeath(s.Journal!);
        var saves = 0;
        s.Records.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null; // 1 = the removal's stamp, 2 = Remove

        var (died, _) = await death.RunUntilDeathAsync(() => s.Service.ApplySelectionAsync(s.Game, []));
        Assert.True(died);
        Assert.False(File.Exists(s.Addon("Alpha")));

        var report = await SimulatedDeath.RecoverAsync(JournalDir, LedgerPath);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(alpha, File.ReadAllBytes(s.Addon("Alpha")));
        Assert.Equal(copy, File.ReadAllBytes(CopyPath));
        // The next start: a fresh service over the recovered ledger still owns the add-on and can clear it.
        var next = Fixture(records: new LinuxDeploymentRecordStore(LedgerPath));
        Assert.Equal([s.Addon("Alpha")], next.Service.OwnedAddonFiles(next.Game));
        var clear = await next.Service.ApplySelectionAsync(next.Game, []);
        Assert.True(clear.Success, clear.Message);
        Assert.False(File.Exists(s.Addon("Alpha")));
    }

    [Fact]
    public async Task FirstApply_CrashAtTheRecordSave_LeavesNoAddonAndNoCopy()
    {
        var s = Fixture(journaled: true);
        using var death = new SimulatedDeath(s.Journal!);
        s.Records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => s.Service.ApplySelectionAsync(s.Game, ["custom-Alpha"]));
        Assert.True(died);
        Assert.True(File.Exists(CopyPath));

        var report = await SimulatedDeath.RecoverAsync(JournalDir, LedgerPath);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(s.Addon("Alpha")));
        Assert.False(File.Exists(CopyPath)); // an older build finds no selection either
        Assert.Null(new LinuxDeploymentRecordStore(LedgerPath).Find(s.Game, ManagedComponentKind.ReShadeAddonManager));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}
