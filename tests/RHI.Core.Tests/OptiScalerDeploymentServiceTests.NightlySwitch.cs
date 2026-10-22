using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #232 (w11/nightly-switch; upstream RHI 2.8.7, wizardkof v0.9.25.0-beta.1 "Apply selected build now"): an installed
/// OptiScaler Nightly changes its build in place through OptiScaler's update transaction. The record's pin, the files and
/// the saved choice always end on the same build — after a switch, a refusal, a failed download, a failed save, a Cancel
/// at the write phase and a crash — and Update All still leaves a pinned Nightly alone.
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    private sealed record SwitchFixture(
        DetectedGame Game, LinuxDeploymentRecordStore Store, NightlyProvider Provider, OptiScalerDeploymentService Service,
        NeuralAddonVersionPreferenceStore Choices, string ChoicePath, Journaled? Journal);

    private const string Installed = "20261004";
    private const string Chosen = "20261012";

    private SwitchFixture NewSwitch(string name, bool journaled = false, string gameName = "Game")
    {
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        game.Name = gameName;
        var latest = CreatePackage(name + "-latest") with { Version = "20261013" };
        File.AppendAllText(Path.Combine(latest.RootDirectory, "OptiScaler", "libxess.dll"), " latest");
        var pinned = CreatePackage(name + "-pinned");
        var provider = new NightlyProvider(latest, pinned);
        var j = journaled ? NewJournaled(name, store) : null;
        var service = j is null ? NewService(store, provider) : NewJournaledService(store, provider, j);
        var choicePath = Path.Combine(_root, name, "cfg", "pins.json");
        return new SwitchFixture(game, store, provider, service, new NeuralAddonVersionPreferenceStore(choicePath), choicePath, j);
    }

    // Installs the fixture's Nightly on `build` (null = Latest) under `proxyName`, with the saved choice on it as an
    // install from the ⚙ dialog leaves it, then the user picks `choice` in the dropdown.
    private static async Task InstallThenChooseAsync(SwitchFixture f, string? build, string? choice, string proxyName = "dxgi.dll")
    {
        f.Choices.Set(f.Game, ManagedComponentKind.OptiScaler, build);
        var installed = await f.Service.InstallAsync(f.Game, OptiScalerVariant.Nightly, build, proxyName);
        Assert.True(installed.Success, installed.Message);
        f.Choices.Set(f.Game, ManagedComponentKind.OptiScaler, choice);
        f.Provider.Calls.Clear();
    }

    private static string? Choice(SwitchFixture f)
        => new NeuralAddonVersionPreferenceStore(f.ChoicePath).Get(f.Game, ManagedComponentKind.OptiScaler);

    private static string Xess(SwitchFixture f) => Path.Combine(f.Game.BinaryDirectory!, "OptiScaler", "libxess.dll");

    [Fact]
    public async Task Switch_PinnedToPinned_ReplacesTheFiles_KeepsTheIniTheNameAndTheOverride_AndRecordsBuildAndChoice()
    {
        var f = NewSwitch("nsw-p2p");
        await InstallThenChooseAsync(f, Installed, Chosen, proxyName: "winmm.dll");
        var bin = f.Game.BinaryDirectory!;
        File.AppendAllText(Path.Combine(bin, "OptiScaler.ini"), "\n[User]\nMine=1\n");
        var ini = File.ReadAllBytes(Path.Combine(bin, "OptiScaler.ini"));
        var hive = File.ReadAllText(Path.Combine(f.Game.PrefixPath!, "user.reg"));
        // The chosen build's package differs from the installed one.
        var chosenPackage = CreatePackage("nsw-p2p-chosen");
        File.AppendAllText(Path.Combine(chosenPackage.RootDirectory, "OptiScaler", "libxess.dll"), " 1012");
        f.Provider.Pinned = chosenPackage;

        var result = await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices);

        Assert.True(result.Success, result.Message);
        Assert.Equal(OptiScalerDeploymentService.NightlySwitchedPrefix + Chosen, result.Message);
        Assert.Equal(["build:" + Chosen], f.Provider.Calls);
        var record = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(Chosen, OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.Equal(Chosen, record.Version);
        Assert.Equal("winmm.dll", OptiScalerDeploymentService.GetProxyName(record));
        Assert.Equal(OptiScalerVariant.Nightly, OptiScalerDeploymentService.GetInstalledVariant(record));
        Assert.Equal(File.ReadAllBytes(Path.Combine(chosenPackage.RootDirectory, "OptiScaler", "libxess.dll")), File.ReadAllBytes(Xess(f)));
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(bin, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "dxgi.dll")));
        Assert.Equal(ini, File.ReadAllBytes(Path.Combine(bin, "OptiScaler.ini"))); // the user's INI is never overwritten
        Assert.Equal(hive, File.ReadAllText(Path.Combine(f.Game.PrefixPath!, "user.reg")));
        Assert.Equal(Chosen, Choice(f));
        foreach (var file in record.Files.Where(file => !file.AllowUserChanges))
            Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(file.Path), file.Sha256), file.Path);

        // Update All leaves the newly pinned build alone (#156, unchanged).
        var update = await f.Service.UpdateAsync(f.Game);
        Assert.StartsWith(OptiScalerDeploymentService.NightlyPinnedNotUpdatedPrefix, update.Message, StringComparison.Ordinal);
        Assert.Equal(["build:" + Chosen], f.Provider.Calls);
    }

    [Fact]
    public async Task Switch_PinnedToLatest_DropsThePin_SoUpdateAllFollowsTheNewestBuildAgain()
    {
        var f = NewSwitch("nsw-p2l");
        await InstallThenChooseAsync(f, Installed, choice: null);

        var result = await f.Service.SwitchNightlyBuildAsync(f.Game, null, f.Choices);

        Assert.True(result.Success, result.Message);
        Assert.Equal(OptiScalerDeploymentService.NightlySwitchedToLatestPrefix + "20261013", result.Message);
        Assert.Equal(["latest:Nightly"], f.Provider.Calls);
        var record = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Null(OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.False(record.Metadata.ContainsKey("OptiScaler.NightlyBuild"));
        Assert.Equal("20261013", record.Version);
        Assert.Null(Choice(f));
        Assert.Equal(File.ReadAllBytes(Path.Combine(f.Provider.Latest.RootDirectory, "OptiScaler", "libxess.dll")), File.ReadAllBytes(Xess(f)));
        record.Version = "20261001";
        f.Store.Upsert(record);
        Assert.True((await f.Service.CheckUpdateAsync(f.Game)).UpdateAvailable);
    }

    [Fact]
    public async Task Switch_LatestToPinned_AddsThePin_AndUpdateAllThenLeavesItAlone()
    {
        var f = NewSwitch("nsw-l2p");
        await InstallThenChooseAsync(f, build: null, Chosen);
        Assert.Null(OptiScalerDeploymentService.GetPinnedNightlyBuild(f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!));

        var result = await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["build:" + Chosen], f.Provider.Calls);
        var record = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(Chosen, OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.Equal(Chosen, Choice(f));
        var check = await f.Service.CheckUpdateAsync(f.Game);
        Assert.False(check.UpdateAvailable);
        Assert.StartsWith(OptiScalerDeploymentService.NightlyPinnedCurrentPrefix, check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switch_WithAFileChangedOutsideRhi_IsRefusedBeforeAnyDownload_AndNothingChanges()
    {
        var f = NewSwitch("nsw-modified");
        await InstallThenChooseAsync(f, Installed, Chosen);
        File.AppendAllText(Xess(f), " user edit");
        var before = CancelAtWritePhase.Snapshot(f.Game.InstallPath, f.Game.PrefixPath!);
        var ledger = File.ReadAllText(Path.Combine(_root, "nsw-modified", "records.json"));

        var result = await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices);

        Assert.False(result.Success);
        Assert.StartsWith(OptiScalerDeploymentService.NightlySwitchFailedPrefix + "Managed OptiScaler file was modified outside RHI", result.Message, StringComparison.Ordinal);
        Assert.Empty(f.Provider.Calls);
        CancelAtWritePhase.AssertUnchanged(before, f.Game.InstallPath, f.Game.PrefixPath!);
        Assert.Equal(ledger, File.ReadAllText(Path.Combine(_root, "nsw-modified", "records.json")));
        Assert.Equal(Installed, Choice(f)); // back on the installed build (#232)
    }

    [Fact]
    public async Task Switch_WhenTheBuildCannotBeDownloaded_LeavesEverything_AndPutsTheChoiceBack()
    {
        var f = NewSwitch("nsw-offline");
        await InstallThenChooseAsync(f, Installed, Chosen);
        var before = CancelAtWritePhase.Snapshot(f.Game.InstallPath, f.Game.PrefixPath!);
        f.Provider.Failure = new HttpRequestException("offline");

        var result = await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices);

        Assert.False(result.Success);
        Assert.Equal(OptiScalerDeploymentService.NightlySwitchFailedPrefix + OptiScalerDeploymentService.NightlyBuildUnavailablePrefix + "offline", result.Message);
        CancelAtWritePhase.AssertUnchanged(before, f.Game.InstallPath, f.Game.PrefixPath!);
        Assert.Equal(Installed, OptiScalerDeploymentService.GetPinnedNightlyBuild(f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!));
        Assert.Equal(Installed, Choice(f));
    }

    [Fact]
    public async Task Switch_WhenTheRecordCannotBeSaved_RollsBack_FilesRecordAndChoiceStayOnTheInstalledBuild()
    {
        var f = NewSwitch("nsw-save-fails");
        await InstallThenChooseAsync(f, Installed, Chosen);
        var before = CancelAtWritePhase.Snapshot(f.Game.InstallPath, f.Game.PrefixPath!);

        ComponentOperationResult result;
        using (LedgerWriteBlocker.Block(f.Store, "records.json"))
            result = await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices);

        Assert.False(result.Success);
        Assert.StartsWith(OptiScalerDeploymentService.NightlySwitchFailedPrefix + "OptiScaler update rolled back: ", result.Message, StringComparison.Ordinal);
        Assert.Equal(["build:" + Chosen], f.Provider.Calls);
        CancelAtWritePhase.AssertUnchanged(before, f.Game.InstallPath, f.Game.PrefixPath!);
        Assert.Equal(Installed, OptiScalerDeploymentService.GetPinnedNightlyBuild(new LinuxDeploymentRecordStore(Path.Combine(_root, "nsw-save-fails", "records.json")).Find(f.Game, ManagedComponentKind.OptiScaler)!));
        Assert.Equal(Installed, Choice(f));
    }

    [Fact]
    public async Task Switch_ControlFrameGenerationBuild_IsRefused_BeforeAnyChange_ButTheSamePinElsewhereSwitches()
    {
        var control = NewSwitch("nsw-control", gameName: ControlUePostInstallService.GameName);
        await InstallThenChooseAsync(control, ControlUeFrameGenerationService.RequiredNightlyBuild, Chosen, ControlUeFrameGenerationService.OptiScalerProxyName);
        var before = CancelAtWritePhase.Snapshot(control.Game.InstallPath, control.Game.PrefixPath!);

        var refused = await control.Service.SwitchNightlyBuildAsync(control.Game, Chosen, control.Choices);

        Assert.False(refused.Success);
        Assert.Equal(OptiScalerDeploymentService.NightlySwitchControlUePrefix + ControlUeFrameGenerationService.RequiredNightlyBuild, refused.Message);
        Assert.Empty(control.Provider.Calls);
        CancelAtWritePhase.AssertUnchanged(before, control.Game.InstallPath, control.Game.PrefixPath!);
        Assert.Equal(Chosen, Choice(control)); // a refusal before anything starts changes nothing, the choice included
        Assert.True(OptiScalerDeploymentService.HoldsControlUeFrameGenerationBuild(control.Game, control.Store.Find(control.Game, ManagedComponentKind.OptiScaler)!));

        // The same build and name on another game is an ordinary pin.
        var other = NewSwitch("nsw-control-other");
        await InstallThenChooseAsync(other, ControlUeFrameGenerationService.RequiredNightlyBuild, Chosen, ControlUeFrameGenerationService.OptiScalerProxyName);
        Assert.True((await other.Service.SwitchNightlyBuildAsync(other.Game, Chosen, other.Choices)).Success);
    }

    [Fact]
    public async Task Switch_RefusesAnotherChannel_ABadBuild_AndNoInstall_WithNothingAsked()
    {
        var f = NewSwitch("nsw-refusals");
        var none = await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices);
        Assert.Equal(OptiScalerDeploymentService.NightlySwitchNotNightlyMessage, none.Message);
        Assert.True((await f.Service.InstallAsync(f.Game, OptiScalerVariant.Stable)).Success);
        f.Provider.Calls.Clear();

        var stable = await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices);
        var bad = await f.Service.SwitchNightlyBuildAsync(f.Game, "../x", f.Choices);

        Assert.False(stable.Success);
        Assert.Equal(OptiScalerDeploymentService.NightlySwitchNotNightlyMessage, stable.Message);
        Assert.False(bad.Success);
        Assert.Empty(f.Provider.Calls);
        Assert.Null(Choice(f));
    }

    [Fact]
    public async Task Switch_ToTheInstalledBuild_ChangesNothing()
    {
        var f = NewSwitch("nsw-same");
        await InstallThenChooseAsync(f, Installed, Installed);
        var before = CancelAtWritePhase.Snapshot(f.Game.InstallPath, f.Game.PrefixPath!);

        var result = await f.Service.SwitchNightlyBuildAsync(f.Game, Installed, f.Choices);

        Assert.True(result.Success, result.Message);
        Assert.Equal(OptiScalerDeploymentService.NightlySwitchSameBuildPrefix + Installed, result.Message);
        Assert.Empty(f.Provider.Calls);
        CancelAtWritePhase.AssertUnchanged(before, f.Game.InstallPath, f.Game.PrefixPath!);
    }

    // Update All (UpdateAsync) never follows the saved choice: a Latest Nightly with another build chosen updates to the
    // newest build and stays on Latest; the choice waits for Apply.
    [Fact]
    public async Task UpdateAll_OnLatestNightly_WithAnotherBuildChosen_UpdatesToTheNewest_AndLeavesTheChoice()
    {
        var f = NewSwitch("nsw-updateall");
        await InstallThenChooseAsync(f, build: null, Chosen);
        var record = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        record.Version = "20261001";
        f.Store.Upsert(record);
        var managed = new LinuxManagedComponentService(f.Store, null!, null!, null!, null!, f.Service, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            neuralVersionPins: f.Choices, updateLog: new ComponentUpdateLogStore(Path.Combine(_root, "nsw-updateall", "log.json")));
        f.Game.InstallPath = Path.GetDirectoryName(f.Game.BinaryDirectory)!;

        var run = await new LinuxUpdateAllService(f.Store, managed, new NotRunning()).RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);

        var item = Assert.Single(run.Items);
        Assert.True(item.Success && item.Changed, item.Message);
        Assert.DoesNotContain("build:" + Chosen, f.Provider.Calls);
        var after = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Null(OptiScalerDeploymentService.GetPinnedNightlyBuild(after));
        Assert.Equal("20261013", after.Version);
        Assert.Equal(Chosen, Choice(f));
    }

    // The managed entry point (the ⚙ dialog's Apply): switches to the SAVED choice, guarded, and logs the update.
    [Fact]
    public async Task ManagedApply_SwitchesToTheSavedChoice_AndRecordsItInTheUpdateHistory()
    {
        var f = NewSwitch("nsw-managed");
        await InstallThenChooseAsync(f, Installed, Chosen);
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "nsw-managed", "log.json"));
        var managed = new LinuxManagedComponentService(f.Store, null!, null!, null!, null!, f.Service, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            neuralVersionPins: f.Choices, updateLog: log);
        f.Game.InstallPath = Path.GetDirectoryName(f.Game.BinaryDirectory)!;
        Assert.Equal(OptiScalerNightlySwitchState.Switchable, managed.GetOptiScalerNightlySwitchState(f.Game));
        Assert.Equal(Installed, managed.GetInstalledOptiScalerNightlyBuild(f.Game));
        Assert.Equal(Chosen, managed.GetOptiScalerNightlyChoice(f.Game));

        // Guarded like any change to the game's files: no confirmed patch target, nothing asked or changed.
        f.Game.GameRootNotAllowed = true;
        var refused = await managed.ApplyOptiScalerNightlyBuildAsync(f.Game);
        f.Game.GameRootNotAllowed = false;
        Assert.False(refused.Success);
        Assert.Empty(f.Provider.Calls);
        Assert.Equal(Installed, managed.GetInstalledOptiScalerNightlyBuild(f.Game));

        var result = await managed.ApplyOptiScalerNightlyBuildAsync(f.Game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["build:" + Chosen], f.Provider.Calls);
        Assert.Equal(Chosen, managed.GetInstalledOptiScalerNightlyBuild(f.Game));
        var entry = Assert.Single(log.GetAll(), e => e.Component == ManagedComponentKind.OptiScaler && e.Action == ComponentUpdateAction.Update);
        Assert.Equal(Installed, entry.FromVersion);
        Assert.Equal(Chosen, entry.ToVersion);
    }

    // ── Crash, Cancel and commit-point agreement (#204 shapes) over the switch ────────────────────────────────────────

    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Switch_CrashAtTheCommitWrite_NextStartPutsTheBuildRecordAndChoiceBack()
    {
        var f = NewSwitch("nsw-crash", journaled: true);
        await InstallThenChooseAsync(f, Installed, Chosen);
        var xessBefore = File.ReadAllBytes(Xess(f));
        var chosenPackage = CreatePackage("nsw-crash-chosen");
        File.AppendAllText(Path.Combine(chosenPackage.RootDirectory, "OptiScaler", "libxess.dll"), " 1012");
        f.Provider.Pinned = chosenPackage;
        using (var death = new SimulatedDeath(f.Journal!.Journal))
        {
            f.Store.SaveFaultForTesting = death.Die;

            var (died, _) = await death.RunUntilDeathAsync(() => f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices));
            Assert.True(died);
            Assert.NotEqual(xessBefore, File.ReadAllBytes(Xess(f)));
            Assert.Equal(Chosen, Choice(f)); // written before the commit point

            var report = await SimulatedDeath.RecoverAsync(f.Journal.Dir, f.Journal.Ledger);

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            Assert.Equal(xessBefore, File.ReadAllBytes(Xess(f)));
            var record = new LinuxDeploymentRecordStore(f.Journal.Ledger).Find(f.Game, ManagedComponentKind.OptiScaler)!;
            Assert.Equal(Installed, OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
            Assert.Equal(Installed, Choice(f));
            foreach (var file in record.Files.Where(file => !file.AllowUserChanges))
                Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(file.Path), file.Sha256), file.Path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CommitPoint")]
    public async Task Switch_CancelAtTheWritePhase_RunsToTheEndOrRollsBack_AndDownloadsNothingAfterTheGate(bool forceRollback)
    {
        var name = "nsw-cancel-" + forceRollback;
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        var choicePath = Path.Combine(_root, name, "cfg", "pins.json");
        var choices = new NeuralAddonVersionPreferenceStore(choicePath);
        var watch = new GateWatchingHandler(new NightlyHttp());
        using var http = new HttpClient(watch);
        var provider = new OptiScalerPackageProvider(http, new NightlyExtractor(), Path.Combine(_root, name, "cache"));
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider);
        choices.Set(game, ManagedComponentKind.OptiScaler, Installed);
        var install = await service.InstallAsync(game, OptiScalerVariant.Nightly, Installed);
        Assert.True(install.Success, install.Message);
        choices.Set(game, ManagedComponentKind.OptiScaler, Chosen);
        var watched = new[] { game.InstallPath, game.PrefixPath! };

        var run = await CancelAtWritePhase.RunAsync(ct => service.SwitchNightlyBuildAsync(game, Chosen, choices, ct), watched, () =>
        {
            watch.CloseGate();
            if (forceRollback) store.SaveFaultForTesting = () => new IOException("Simulated write failure");
        });
        store.SaveFaultForTesting = null;

        Assert.Empty(watch.LateRequests);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        if (forceRollback)
        {
            Assert.False(run.Result.Success);
            CancelAtWritePhase.AssertUnchanged(run.Before, watched);
            Assert.Equal(Installed, OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
            Assert.Equal(Installed, choices.Get(game, ManagedComponentKind.OptiScaler));
        }
        else
        {
            // Cancel pressed after the first change is not honoured: the switch finishes.
            Assert.True(run.Result.Success, run.Result.Message);
            Assert.Equal(Chosen, OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
            Assert.Equal(Chosen, choices.Get(game, ManagedComponentKind.OptiScaler));
            Assert.Equal(NightlyHttp.DllFor(Chosen), File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        }
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Switch_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite_ChoiceIncluded(LandedShape shape)
    {
        var results = await CommitPointAgreement.AssertEveryWriteAsync(async name =>
        {
            var f = NewSwitch($"nsw-cp-{shape}-{name}", journaled: true);
            var chosenPackage = CreatePackage($"nsw-cp-{shape}-{name}-chosen");
            File.AppendAllText(Path.Combine(chosenPackage.RootDirectory, "OptiScaler", "libxess.dll"), " 1012");
            await InstallThenChooseAsync(f, Installed, Chosen);
            f.Provider.Pinned = chosenPackage;
            var root = Path.Combine(_root, $"nsw-cp-{shape}-{name}");
            return new CommitPointCase(root, [Path.Combine(root, "game"), Path.Combine(root, "pfx"), Path.Combine(root, "cfg")],
                f.Store, f.Journal!.Ledger, f.Journal.Journal, f.Journal.Dir,
                () => f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices),
                Extra: () => "pin=" + OptiScalerDeploymentService.GetPinnedNightlyBuild(
                    new LinuxDeploymentRecordStore(f.Journal.Ledger).Find(f.Game, ManagedComponentKind.OptiScaler)!) + " choice=" + Choice(f));
        }, shape);

        Assert.True(results[^1].Success);
        Assert.StartsWith(DeploymentScope.LandedChangePrefix, results[^1].Message, StringComparison.Ordinal);
    }

    // ── Fakes: a nightly release per build served over (fake) GitHub, its archive "extracted" to a package ───────────

    private sealed class NightlyHttp : HttpMessageHandler
    {
        // At least 1 MB (the provider's plausible-size check); the build in its last eight bytes.
        public static byte[] ArchiveFor(string build)
        {
            var bytes = new byte[1_100_000];
            new Random(int.Parse(build)).NextBytes(bytes);
            Encoding.ASCII.GetBytes(build).CopyTo(bytes, bytes.Length - 8);
            return bytes;
        }

        public static byte[] DllFor(string build)
        {
            // Sized and marked like the provider tests' package (its PE/signature sanity check), the build after the mark.
            var dll = new byte[300_000];
            dll[0] = (byte)'M';
            dll[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("optiscaler/OptiScaler official package " + build).CopyTo(dll, 512);
            return dll;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            foreach (var build in new[] { Installed, Chosen })
            {
                var asset = $"https://github.com/rhi-tests/nightly-{build}.7z";
                if (url == OptiScalerPackageProvider.NightlyTagApiUrl(build))
                {
                    var archive = ArchiveFor(build);
                    var json = JsonSerializer.Serialize(new
                    {
                        tag_name = "nightly-" + build, draft = false, prerelease = true,
                        assets = new[] { new { name = $"OptiScaler_v10.0.0-pre1_{build}.7z", browser_download_url = asset, size = archive.LongLength,
                            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant() } }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
                }
                if (url == asset)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArchiveFor(build)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NightlyExtractor : IOptiScalerArchiveExtractor
    {
        public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var build = Encoding.ASCII.GetString(File.ReadAllBytes(archivePath)[^8..]);
            var root = Path.Combine(outputDirectory, "package");
            Directory.CreateDirectory(Path.Combine(root, "OptiScaler"));
            File.WriteAllBytes(Path.Combine(root, "OptiScaler.dll"), NightlyHttp.DllFor(build));
            File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n");
            File.WriteAllText(Path.Combine(root, "OptiScaler", "libxess.dll"), "MZ xess " + build);
            return Task.CompletedTask;
        }
    }
}
