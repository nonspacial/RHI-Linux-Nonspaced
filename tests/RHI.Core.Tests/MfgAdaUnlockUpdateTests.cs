using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #237 (upstream v2.8.8 Beta abf18c7, AddonPackService.RedeployToTrackedGames): MFG Ada Unlock is an Update All family
/// through the app's wiring (the manifest add-on pack over the generic GitHub add-on service), and an update never
/// re-creates an add-on file removed outside RHI — for every caller of that service (ReLimiter, Display Commander, the
/// DLSS5 DX11 Bridge's version switch). Crash tests over MFG Ada's update: Cancel at the write phase, death after the file
/// write, and the commit-point agreement at every ledger write.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MfgAdaUnlockUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-mfg-ada-tests-" + Guid.NewGuid().ToString("N"));

    public MfgAdaUnlockUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string ManifestUrl = "https://raw.githubusercontent.com/rhi-tests/rhi/main/manifest.json";
    private const string ListUrl = "https://api.github.com/repos/rhi-tests/mfg-ada/releases?per_page=1";
    private const string AddonName = "renodx-mfgunlock.addon64";

    /// <summary>The manifest (its add-on packs point at the fake list) and a switchable release list.</summary>
    private sealed class Release : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);
        public string Active { get; set; } = "";
        public int ListRequests { get; private set; }

        public Release Add(string version, byte marker)
        {
            _payloads[version] = Payload(marker);
            Active = version;
            return this;
        }

        public Release WithActive(string version)
        {
            Active = version;
            return this;
        }

        public static byte[] Payload(byte marker)
        {
            var bytes = Enumerable.Repeat(marker, 160 * 1024).ToArray();
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            return bytes;
        }

        private static string AssetUrl(string version) => $"https://github.com/rhi-tests/mfg-ada/{version}/{AddonName}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ManifestUrl)
                return Task.FromResult(Json($$"""
                    {"version":2,"addonPacks":{
                      "mfgunlock":{"packageName":"MFG Ada Unlock","releaseApiUrl":"{{ListUrl}}","repositoryUrl":"https://github.com/rhi-tests/mfg-ada"},
                      "dlss5-dx11-bridge":{"packageName":"DLSS5 DX11 Bridge","releaseApiUrl":"{{ListUrl}}","repositoryUrl":"https://github.com/rhi-tests/mfg-ada"} } }
                    """));
            if (url == ListUrl)
            {
                ListRequests++;
                var digest = Convert.ToHexString(SHA256.HashData(_payloads[Active])).ToLowerInvariant();
                return Task.FromResult(Json($$"""
                    [{"tag_name":"v{{Active}}","draft":false,"prerelease":false,"assets":[
                      {"name":"{{AddonName}}","browser_download_url":"{{AssetUrl(Active)}}","digest":"sha256:{{digest}}"}]}]
                    """));
            }
            foreach (var (version, payload) in _payloads)
                if (url == AssetUrl(version))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Running : IGameRunningProbe
    {
        public bool Value { get; set; }
        public bool IsRunning(DetectedGame game) => Value;
    }

    /// <summary>One game with ReShade on disk, MFG Ada installed by RHI at <paramref name="installed"/>, the app's wiring.</summary>
    private sealed class Fixture
    {
        public required string Root { get; init; }
        public required string Bin { get; init; }
        public required DetectedGame Game { get; init; }
        public required LinuxDeploymentRecordStore Records { get; init; }
        public required string RecordsPath { get; init; }
        public required DeploymentJournal Journal { get; init; }
        public required string JournalDirectory { get; init; }
        public required Release Release { get; init; }
        public required GateWatchingHandler Gate { get; init; }
        public required UpstreamRhiAddonDeploymentService Service { get; init; }
        public required LinuxManagedComponentService Managed { get; init; }
        public required ComponentUpdateLogStore History { get; init; }
        public Running Running { get; } = new();
        public string Addon => Path.Combine(Bin, AddonName);
        public LinuxUpdateAllService UpdateAll => new(Records, Managed, Running);
        public LinuxDeploymentRecord? Record => Records.Find(Game, ManagedComponentKind.MfgAdaUnlock);
    }

    private static UpstreamRhiAddonDeploymentService AddonService(ManagedComponentKind kind, string sectionId, string name, HttpClient http,
        LinuxDeploymentRecordStore records, string root, DeploymentJournal? journal)
        => new(http, records, new UpstreamRhiManifestService(http, ManifestUrl, null, Path.Combine(root, "manifest-cache.json")),
            new UpstreamRhiAddonDeploymentOptions(kind, sectionId, name, ListUrl, 128 * 1024,
                ExactAssetName64: AddonName, DeployFileName64: AddonName, X64Only: true),
            Path.Combine(root, "addon-cache"), journal);

    private static async Task<Fixture> InstalledAsync(string root, Release release, ManagedComponentKind kind = ManagedComponentKind.MfgAdaUnlock)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "Game.exe"), CacheForeignFileRejectionTests.Pe(0x8664, 4096, 0x11));
        var reshade = Release.Payload(0x20);
        Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(reshade, 1024);
        File.WriteAllBytes(Path.Combine(bin, "dxgi.dll"), reshade);
        var game = new DetectedGame
        {
            Name = "MFG Ada Game", Source = "Manual", Runner = "Manual Windows", InstallPath = bin, BinaryDirectory = bin,
            ExecutablePath = Path.Combine(bin, "Game.exe"), GraphicsApi = GraphicsApi.DirectX12
        };
        var recordsPath = Path.Combine(root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(recordsPath);
        var journalDirectory = Path.Combine(root, "journal");
        var journal = SimulatedDeath.NewJournal(journalDirectory, records).Journal;
        var gate = new GateWatchingHandler(release);
        var http = new HttpClient(gate);
        var service = AddonService(kind, kind == ManagedComponentKind.MfgAdaUnlock ? "mfgunlock" : "dlss5-dx11-bridge",
            kind == ManagedComponentKind.MfgAdaUnlock ? "MFG Ada Unlock" : "DLSS5 DX11 Bridge", http, records, root, journal);
        var history = new ComponentUpdateLogStore(Path.Combine(root, "update-log.json"));
        var managed = new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: service, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: service, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            updateLog: history);
        var install = await service.InstallAsync(game);
        Assert.True(install.Success, install.Message);
        return new Fixture
        {
            Root = root, Bin = bin, Game = game, Records = records, RecordsPath = recordsPath, Journal = journal,
            JournalDirectory = journalDirectory, Release = release, Gate = gate, Service = service, Managed = managed, History = history
        };
    }

    private Task<Fixture> InstalledAsync(string name, ManagedComponentKind kind = ManagedComponentKind.MfgAdaUnlock)
        => InstalledAsync(Path.Combine(_root, name), new Release().Add("0.5", 0x31).Add("0.6", 0x32).WithActive("0.5"), kind);

    // Proved first on 8f15e44: MFG Ada with a newer release "does not take part" in Update All (no check/update arm).
    [Fact]
    public async Task UpdateAll_NewerRelease_UpdatesMfgAda_InBothModes_AndTheSummaryCountsIt()
    {
        var f = await InstalledAsync("update");
        f.Release.Active = "0.6";

        var background = await f.UpdateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Background);
        var manual = await f.UpdateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal((1, 0, 0, 0), (background.ChangedCount, background.CurrentCount, background.FailedCount, background.UncheckedCount));
        Assert.Equal((0, 1, 0, 0), (manual.ChangedCount, manual.CurrentCount, manual.FailedCount, manual.UncheckedCount));
        Assert.Equal(Release.Payload(0x32), File.ReadAllBytes(f.Addon));
        Assert.Equal("0.6", f.Record!.Version);
        var entry = Assert.Single(f.History.GetAll());
        Assert.Equal((ManagedComponentKind.MfgAdaUnlock, "0.5", "0.6", true), (entry.Component, entry.FromVersion, entry.ToVersion, entry.Success));
        Assert.Equal("Update All: 1 updated · 0 current · 0 deferred · 0 failed.", UiLocalizationService.UpdateAllSummaryFor("en-US", background));
        Assert.Equal("Atualizar tudo: 1 atualizado(s) · 0 em dia · 0 adiado(s) · 0 com falha.", UiLocalizationService.UpdateAllSummaryFor("pt-BR", background));
        Assert.Equal("Atualizar tudo: 0 atualizado(s) · 1 em dia · 0 adiado(s) · 0 com falha.", UiLocalizationService.UpdateAllSummaryFor("pt-BR", manual));
    }

    [Fact]
    public async Task UpdateAll_GameRunning_IsDeferred_ThenCaughtUpAtTheNextPass()
    {
        var f = await InstalledAsync("running");
        f.Release.Active = "0.6";
        f.Running.Value = true;
        var updateAll = f.UpdateAll;

        var first = await updateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Background);
        f.Running.Value = false;
        var second = await updateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Equal((0, 1), (first.ChangedCount, first.DeferredCount));
        Assert.Equal((1, 0, 0), (second.ChangedCount, second.DeferredCount, second.FailedCount));
        Assert.Equal(Release.Payload(0x32), File.ReadAllBytes(f.Addon));
    }

    // Upstream's "removed copies reinstalled" (abf18c7): a copy the user deleted is never put back. The check says why
    // before any lookup (could not be checked, never failed), and the update refuses it.
    [Fact]
    public async Task RemovedAddon_IsNeverRecreated_CheckSaysWhy_BeforeAnyLookup()
    {
        var f = await InstalledAsync("removed");
        File.Delete(f.Addon);
        f.Release.Active = "0.6";
        var lookups = f.Release.ListRequests;

        var check = await f.Managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []);
        var results = new[]
        {
            await f.UpdateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual),
            await f.UpdateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Background)
        };
        var update = await f.Managed.UpdateManagedAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + f.Addon, check.Message);
        Assert.All(results, result => Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount)));
        Assert.Equal(lookups, f.Release.ListRequests);
        Assert.False(update.Success);
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + f.Addon, update.Message);
        Assert.False(File.Exists(f.Addon));
        Assert.Equal("0.5", f.Record!.Version);
    }

    [Fact]
    public async Task ChangedAddon_IsCouldNotCheck_WhenAnUpdateIsPlanned_AndIsNeverReplaced()
    {
        var f = await InstalledAsync("changed");
        var changed = Release.Payload(0x33);
        File.WriteAllBytes(f.Addon, changed);

        var current = await f.Managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []);
        f.Release.Active = "0.6";
        var planned = await f.Managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []);
        var result = await f.UpdateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Equal((false, false), (current.UpdateAvailable, current.CouldNotCheck)); // no swap planned: never hashed
        Assert.Equal((false, true), (planned.UpdateAvailable, planned.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + f.Addon, planned.Message);
        Assert.Equal((0, 0, 1), (result.ChangedCount, result.FailedCount, result.UncheckedCount));
        Assert.Equal(changed, File.ReadAllBytes(f.Addon));
    }

    // #170: an add-on turned off for ReShade's No Addons build is held, never updated.
    [Fact]
    public async Task AddonTurnedOffForNoAddons_IsHeld_NotUpdated()
    {
        var f = await InstalledAsync("off");
        var off = f.Addon + ReShadeAddonSwitch.OffSuffix;
        File.Move(f.Addon, off);
        var record = f.Record!;
        record.Files.Single().Path = off;
        f.Records.Upsert(record);
        f.Release.Active = "0.6";

        var result = await f.UpdateAll.RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.FailedCount, result.UncheckedCount, result.HeldCount));
        Assert.Equal(LinuxManagedComponentService.ReShadeNoAddonsName, Assert.Single(result.Items).HeldBy);
        Assert.Equal(Release.Payload(0x31), File.ReadAllBytes(off));
    }

    // A game held by its own earlier undo gets the hold's restart text, not "modified outside RHI" about its half-undone file.
    [PermissionDenialFact]
    public async Task OnAGameHeldByItsOwnUndo_TheUpdateGetsTheRestartText()
    {
        var f = await InstalledAsync("held");
        f.Release.Active = "0.6";
        var (opened, refusal) = await DeploymentScope.BeginAsync(f.Journal, "earlier MFG Ada change", f.Game, ManagedComponentKind.MfgAdaUnlock, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(f.Addon, Release.Payload(0x34));
            f.Records.SaveFaultForTesting = () =>
            {
                PermissionDenial.MakeReadOnly(f.Bin);
                return new IOException("Simulated write failure: deployments.json");
            };
            try { Assert.NotNull(Record.Exception(() => f.Records.Upsert(scope.Stamp(f.Record!)))); }
            finally { f.Records.SaveFaultForTesting = null; }
            Assert.False((await scope.RollbackAsync()).Success);
        }
        try
        {
            var update = await f.Managed.UpdateManagedAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []);

            Assert.False(update.Success);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, update.Message);
            Assert.Equal(Release.Payload(0x34), File.ReadAllBytes(f.Addon));
        }
        finally
        {
            PermissionDenial.MakeWritable(f.Bin);
        }
    }

    // The never-re-create rule lives in the generic add-on update, so the DLSS5 DX11 Bridge's update (and its version switch,
    // the same method with a pin) keeps it too; with its file there, the update works as before.
    [Fact]
    public async Task BridgeUpdate_OverARemovedAddon_IsRefused_AndWorksWithItsFile()
    {
        var removed = await InstalledAsync("bridge-removed", ManagedComponentKind.Dlss5Dx11Bridge);
        File.Delete(removed.Addon);
        var present = await InstalledAsync("bridge-present", ManagedComponentKind.Dlss5Dx11Bridge);

        removed.Release.Active = "0.6";
        present.Release.Active = "0.6";
        var refused = await removed.Service.UpdateAsync(removed.Game);
        var switched = await present.Service.UpdateAsync(present.Game);

        Assert.False(refused.Success);
        Assert.StartsWith(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix, refused.Message);
        Assert.False(File.Exists(removed.Addon));
        Assert.True(switched.Success, switched.Message);
        Assert.Equal(Release.Payload(0x32), File.ReadAllBytes(present.Addon));
    }

    // ReLimiter and Display Commander use the same update: a removed add-on is refused, a present one still updates.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReLimiterUpdate_RefusesARemovedAddon_AndStillUpdatesAPresentOne(bool removeIt)
    {
        var root = Path.Combine(_root, "relimiter-" + removeIt);
        var f = await InstalledAsync(root, new Release().Add("0.5", 0x31).Add("0.6", 0x32).WithActive("0.5"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(root, "relimiter.json"));
        var relimiter = new GitHubReShadeAddonDeploymentService(new HttpClient(f.Release), records, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.ReLimiter, "ReLimiter", ListUrl, AddonName, null, 128 * 1024), Path.Combine(root, "relimiter-cache"));
        File.Delete(f.Addon); // the MFG Ada copy goes; ReLimiter (same asset name in this fake) installs its own
        Assert.True((await relimiter.InstallAsync(f.Game)).Success);
        if (removeIt) File.Delete(f.Addon);
        f.Release.Active = "0.6";

        var update = await relimiter.UpdateAsync(f.Game);

        Assert.Equal(!removeIt, update.Success);
        Assert.Equal(!removeIt, File.Exists(f.Addon));
        if (!removeIt) Assert.Equal(Release.Payload(0x32), File.ReadAllBytes(f.Addon));
    }

    // ---- crash tests (plan §7) --------------------------------------------------------------------------------------

    // Cancel pressed as the update enters its write phase is honoured only before it: the update runs to the end, and no
    // download happens after the gate; with the record save failing there, everything is put back as it was.
    [Trait("Category", "CommitPoint")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_CancelAtTheWritePhase_RunsToTheEnd_OrRollsBack_WithNoDownloadAfterTheGate(bool forceRollback)
    {
        var f = await InstalledAsync("cancel-" + forceRollback);
        f.Release.Active = "0.6";
        var before = File.ReadAllBytes(f.Addon);

        var run = await CancelAtWritePhase.RunAsync(
            ct => f.Managed.UpdateManagedAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, [], ct), [f.Bin],
            atWritePhase: () =>
            {
                f.Gate.CloseGate();
                if (forceRollback) f.Records.SaveFaultForTesting = () => new IOException("disk full");
            });
        f.Records.SaveFaultForTesting = null;

        Assert.Empty(f.Gate.LateRequests);
        Assert.Equal(!forceRollback, run.Result.Success);
        if (forceRollback)
        {
            CancelAtWritePhase.AssertUnchanged(run.Before, f.Bin);
            Assert.Equal(before, File.ReadAllBytes(f.Addon));
            Assert.Equal("0.5", f.Record!.Version);
        }
        else
        {
            Assert.Equal(Release.Payload(0x32), File.ReadAllBytes(f.Addon));
            Assert.Equal("0.6", f.Record!.Version);
        }
    }

    // Death right after the new add-on was written (at the record save): the next start puts the previous add-on back, and
    // the record and the file agree.
    [Trait("Category", "CommitPoint")]
    [Fact]
    public async Task Update_DeathAfterTheFileWrite_NextStartLeavesRecordAndFileAgreeing()
    {
        var f = await InstalledAsync("death");
        f.Release.Active = "0.6";
        using (var death = new SimulatedDeath(f.Journal))
        {
            f.Records.SaveFaultForTesting = death.Die;
            var (died, _) = await death.RunUntilDeathAsync(() => f.Managed.UpdateManagedAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []));
            Assert.True(died);
            Assert.Equal(Release.Payload(0x32), File.ReadAllBytes(f.Addon)); // written before the record

            var report = await SimulatedDeath.RecoverAsync(f.JournalDirectory, f.RecordsPath);

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            var record = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.MfgAdaUnlock)!;
            Assert.Equal("0.5", record.Version);
            Assert.Equal(Release.Payload(0x31), File.ReadAllBytes(f.Addon));
            Assert.Equal(FileHash.Sha256Hex(f.Addon), record.Files.Single().Sha256, ignoreCase: true);
        }
    }

    [Trait("Category", "CommitPoint")]
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Update_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        async Task<CommitPointCase> Make(string name)
        {
            var f = await InstalledAsync(Path.Combine(_root, $"agree-{shape}-{name}"), new Release().Add("0.5", 0x31).Add("0.6", 0x32).WithActive("0.5"));
            f.Release.Active = "0.6";
            return new CommitPointCase(f.Root, [f.Bin], f.Records, f.RecordsPath, f.Journal, f.JournalDirectory,
                () => f.Managed.UpdateManagedAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []));
        }

        var results = await CommitPointAgreement.AssertEveryWriteAsync(Make, shape);

        Assert.True(results[^1].Success); // the commit write is the last
    }

    // ---- Correction round 1 (review w12-review-mfg.md) -----------------------------------------------------------------

    // F1: on a game held by its own failed undo, Update All (both modes) reads the hold's restart text for MFG Ada (could not
    // be checked), never "changed outside RHI" about RHI's own half-undone add-on; nothing is looked up or written.
    [PermissionDenialFact]
    public async Task HeldGame_UpdateAllItem_IsTheHoldsRestartText_InBothModes()
    {
        var f = await InstalledAsync("correction-held");
        f.Release.Active = "0.6";
        var halfUndone = Release.Payload(0x34);
        using var hold = await FailedUndoHold.ArmAsync(f.Journal, f.Records, f.Game, ManagedComponentKind.MfgAdaUnlock, f.Addon, halfUndone);
        var lookups = f.Release.ListRequests;

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await f.UpdateAll.RunAsync([f.Game], _ => [], mode);
            Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, Assert.Single(result.Items).Message);
        }
        Assert.Equal(lookups, f.Release.ListRequests);
        Assert.Equal(halfUndone, File.ReadAllBytes(f.Addon));
    }

    /// <summary>A network that answers nothing (offline, a cold start without the manifest cache); counts what was asked.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new HttpRequestException("offline test");
        }
    }

    // F6: the add-on removed outside RHI is told before any network call, the manifest included, so offline (a cold start
    // with no manifest cache) the check still reads the removed line, not "update check failed".
    [Fact]
    public async Task RemovedAddon_Offline_ReadsTheRemovedLine_BeforeTheManifest()
    {
        var f = await InstalledAsync("correction-offline");
        File.Delete(f.Addon);
        var offline = new Offline();
        var cold = AddonService(ManagedComponentKind.MfgAdaUnlock, "mfgunlock", "MFG Ada Unlock", new HttpClient(offline), f.Records,
            Directory.CreateDirectory(Path.Combine(f.Root, "cold")).FullName, f.Journal);

        var check = await cold.CheckUpdateAsync(f.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + f.Addon, check.Message);
        Assert.Equal(0, offline.Requests);
    }

    // F6 + F1: the same wrapper tells a held game the hold's restart text before the manifest.
    [PermissionDenialFact]
    public async Task HeldGame_Offline_ReadsTheHoldsRestartText_BeforeTheManifest()
    {
        var f = await InstalledAsync("correction-held-offline");
        using var hold = await FailedUndoHold.ArmAsync(f.Journal, f.Records, f.Game, ManagedComponentKind.MfgAdaUnlock, f.Addon, Release.Payload(0x35));
        var offline = new Offline();
        var cold = AddonService(ManagedComponentKind.MfgAdaUnlock, "mfgunlock", "MFG Ada Unlock", new HttpClient(offline), f.Records,
            Directory.CreateDirectory(Path.Combine(f.Root, "cold")).FullName, f.Journal);

        var check = await cold.CheckUpdateAsync(f.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, check.Message);
        Assert.Equal(0, offline.Requests);
    }

    // w12 follow-up (re-review mfg N4): a direct Update (the row's own, offline on a cold start) tells a held game the hold's
    // restart text and a removed add-on the removed line before the manifest is asked, as the check does.
    [PermissionDenialFact]
    public async Task HeldGame_Offline_DirectUpdate_ReadsTheHoldsRestartText_BeforeTheManifest()
    {
        var f = await InstalledAsync("followup-held-update-offline");
        var halfUndone = Release.Payload(0x37);
        using var hold = await FailedUndoHold.ArmAsync(f.Journal, f.Records, f.Game, ManagedComponentKind.MfgAdaUnlock, f.Addon, halfUndone);
        var offline = new Offline();
        var cold = AddonService(ManagedComponentKind.MfgAdaUnlock, "mfgunlock", "MFG Ada Unlock", new HttpClient(offline), f.Records,
            Directory.CreateDirectory(Path.Combine(f.Root, "cold")).FullName, f.Journal);

        var update = await cold.UpdateAsync(f.Game);

        Assert.False(update.Success);
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, update.Message);
        Assert.Equal(0, offline.Requests);
        Assert.Equal(halfUndone, File.ReadAllBytes(f.Addon));
    }

    [Fact]
    public async Task RemovedAddon_Offline_DirectUpdate_ReadsTheRemovedLine_BeforeTheManifest()
    {
        var f = await InstalledAsync("followup-removed-update-offline");
        File.Delete(f.Addon);
        var offline = new Offline();
        var cold = AddonService(ManagedComponentKind.MfgAdaUnlock, "mfgunlock", "MFG Ada Unlock", new HttpClient(offline), f.Records,
            Directory.CreateDirectory(Path.Combine(f.Root, "cold")).FullName, f.Journal);

        var update = await cold.UpdateAsync(f.Game);

        Assert.False(update.Success);
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + f.Addon, update.Message);
        Assert.Equal(0, offline.Requests);
        Assert.False(File.Exists(f.Addon)); // never put back
    }

    // F1: the generic GitHub add-on check (ReLimiter, Display Commander) asks the hold first too, before it reads the file.
    [PermissionDenialFact]
    public async Task ReLimiter_HeldGame_CheckIsTheHoldsRestartText_BeforeAnyLookup()
    {
        var root = Path.Combine(_root, "correction-relimiter-held");
        var f = await InstalledAsync(root, new Release().Add("0.5", 0x31).Add("0.6", 0x32).WithActive("0.5"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(root, "relimiter.json"));
        var journal = SimulatedDeath.NewJournal(Path.Combine(root, "relimiter-journal"), records).Journal;
        var relimiter = new GitHubReShadeAddonDeploymentService(new HttpClient(f.Release), records, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.ReLimiter, "ReLimiter", ListUrl, AddonName, null, 128 * 1024), Path.Combine(root, "relimiter-cache"), journal);
        File.Delete(f.Addon); // the MFG Ada copy goes; ReLimiter (same asset name in this fake) installs its own
        Assert.True((await relimiter.InstallAsync(f.Game)).Success);
        f.Release.Active = "0.6";
        using var hold = await FailedUndoHold.ArmAsync(journal, records, f.Game, ManagedComponentKind.ReLimiter, f.Addon, Release.Payload(0x36));
        var lookups = f.Release.ListRequests;

        var check = await relimiter.CheckUpdateAsync(f.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, check.Message);
        Assert.Equal(lookups, f.Release.ListRequests);
    }
}
