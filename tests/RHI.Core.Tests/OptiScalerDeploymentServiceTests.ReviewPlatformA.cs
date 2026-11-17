using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review (platform A): the one saved pin slot never hands a release tag to a Nightly install (M4); a version
/// switch fetches the new version and the exact installed one before it removes anything, and a failed switch puts that
/// exact version back (m1).
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    private const string StableReleaseUrl = "https://github.com/optiscaler/OptiScaler/releases/download/v0.9.2/OptiScaler_v0.9.2.7z";

    /// <summary>Every channel: the newest per channel, any pinned Nightly build or release; <see cref="Unreachable"/> fails every request.</summary>
    private sealed class PinsProvider(Func<OptiScalerVariant, OptiScalerPackage> latest, Func<OptiScalerVariant, string, OptiScalerPackage> release) : IOptiScalerPackageProvider
    {
        public List<string> Calls { get; } = [];
        public bool Unreachable { get; set; }
        public Func<OptiScalerVariant, OptiScalerPackage> Latest { get; set; } = latest;

        private Task<OptiScalerPackage> Serve(string call, Func<OptiScalerPackage> package)
        {
            Calls.Add(call);
            return Unreachable
                ? Task.FromException<OptiScalerPackage>(new HttpRequestException("api.github.com is unreachable"))
                : Task.FromResult(package());
        }

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default) => GetLatestAsync(OptiScalerVariant.Stable, cancellationToken);

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
            => Serve("latest:" + variant, () => Latest(variant) with { Variant = variant });

        public Task<OptiScalerPackage> GetNightlyBuildAsync(string build, CancellationToken cancellationToken = default)
            => Serve("build:" + build, () => Latest(OptiScalerVariant.Nightly) with { Variant = OptiScalerVariant.Nightly, Version = build });

        public Task<OptiScalerPackage> GetReleaseAsync(OptiScalerVariant variant, string tag, CancellationToken cancellationToken = default)
            => Serve($"release:{variant}:{tag}", () => release(variant, tag) with { Variant = variant });
    }

    private sealed record PinsFixture(DetectedGame Game, LinuxDeploymentRecordStore Store, PinsProvider Provider, LinuxManagedComponentService Managed);

    private PinsFixture NewPinsFixture(string name)
    {
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        game.InstallPath = Path.GetDirectoryName(game.BinaryDirectory)!;
        var stable092 = CreatePackage(name + "-stable-092") with { Version = "0.9.2", SourceUrl = StableReleaseUrl };
        var stableNewest = CreatePackage(name + "-stable-099") with { Version = "0.9.9", SourceUrl = "https://github.com/optiscaler/OptiScaler/releases/download/v0.9.9/OptiScaler_v0.9.9.7z" };
        var nightly = CreatePackage(name + "-nightly") with { Version = "20261013" };
        File.AppendAllText(Path.Combine(nightly.RootDirectory, "OptiScaler", "libxess.dll"), " nightly");
        var provider = new PinsProvider(
            variant => variant == OptiScalerVariant.Nightly ? nightly : stableNewest,
            (_, tag) => tag == "v0.9.2" ? stable092 : throw new InvalidOperationException("no release " + tag));
        var service = NewService(store, provider);
        service.FreshIniBackupDirectory = Path.Combine(_root, name, "ini-backups");
        var managed = new LinuxManagedComponentService(store, null!, null!, null!, null!, service, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            neuralVersionPins: new NeuralAddonVersionPreferenceStore(Path.Combine(_root, name, "pins.json")),
            updateLog: new ComponentUpdateLogStore(Path.Combine(_root, name, "log.json")));
        return new PinsFixture(game, store, provider, managed);
    }

    // M4: the slot holds "Stable+v0.9.2"; a Nightly install takes the newest Nightly, never the tag.
    [Fact]
    public async Task NightlyInstall_WithAStableReleasePinSaved_TakesTheNewestNightly()
    {
        var f = NewPinsFixture("m4-install");
        f.Managed.SetOptiScalerReleasePin(f.Game, OptiScalerVariant.Stable, "v0.9.2");

        var installed = await f.Managed.InstallOptiScalerAsync(f.Game, OptiScalerVariant.Nightly);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(["latest:Nightly"], f.Provider.Calls);
        var record = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(OptiScalerVariant.Nightly, OptiScalerDeploymentService.GetInstalledVariant(record));
        Assert.Null(OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.Equal("v0.9.2", f.Managed.GetOptiScalerReleasePin(f.Game, OptiScalerVariant.Stable)); // Stable's pin is kept for Stable
    }

    // M4, the Nightly preset's path: a game on Stable pinned to v0.9.2 is switched to Nightly.
    [Fact]
    public async Task SwitchToNightly_FromAStablePinnedRelease_InstallsTheNewestNightly()
    {
        var f = NewPinsFixture("m4-switch");
        f.Managed.SetOptiScalerReleasePin(f.Game, OptiScalerVariant.Stable, "v0.9.2");
        Assert.True((await f.Managed.InstallOptiScalerAsync(f.Game, OptiScalerVariant.Stable)).Success);
        Assert.Equal("v0.9.2", OptiScalerDeploymentService.GetPinnedRelease(f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!));

        var switched = await f.Managed.SwitchOptiScalerVersionAsync(f.Game, OptiScalerVariant.Nightly);

        Assert.True(switched.Success, switched.Message);
        Assert.Contains("latest:Nightly", f.Provider.Calls);
        Assert.Equal(OptiScalerVariant.Nightly, f.Managed.GetInstalledOptiScalerVariant(f.Game));
        Assert.Contains("installed from the official GitHub release as dxgi.dll", switched.Message, StringComparison.Ordinal); // nit: the install's line stays
    }

    // m1: GitHub unreachable — the switch stops before removing anything.
    [Fact]
    public async Task SwitchVersion_WithGitHubUnreachable_RemovesNothing()
    {
        var f = NewPinsFixture("m1-offline");
        Assert.True((await f.Managed.InstallOptiScalerAsync(f.Game, OptiScalerVariant.Stable)).Success);
        var before = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        var dxgi = Path.Combine(f.Game.BinaryDirectory!, "dxgi.dll");
        var bytes = File.ReadAllBytes(Path.Combine(f.Game.BinaryDirectory!, "OptiScaler", "libxess.dll"));
        f.Provider.Unreachable = true;

        var switched = await f.Managed.SwitchOptiScalerVersionAsync(f.Game, OptiScalerVariant.Nightly);

        Assert.False(switched.Success);
        Assert.StartsWith(LinuxManagedComponentService.SwitchNotStartedPrefix, switched.Message, StringComparison.Ordinal);
        Assert.Contains("api.github.com is unreachable", switched.Message, StringComparison.Ordinal);
        var after = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal((before.Version, before.InstalledAtUtc), (after.Version, after.InstalledAtUtc));
        Assert.True(File.Exists(dxgi));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Game.BinaryDirectory!, "OptiScaler", "libxess.dll")));
    }

    // m1: DLSS5 Feeder owns nvngx_dlssnr.dll — a switch to DLSS NR is refused before anything is removed or downloaded.
    [Fact]
    public async Task SwitchToDlssNr_WhileDlss5FeederOwnsTheNrDll_IsRefusedBeforeRemoving()
    {
        var f = NewPinsFixture("m1-feeder");
        Assert.True((await f.Managed.InstallOptiScalerAsync(f.Game, OptiScalerVariant.Stable)).Success);
        f.Store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(f.Game), GameName = f.Game.Name, Component = ManagedComponentKind.Dlss5Feeder,
            BinaryDirectory = f.Game.BinaryDirectory!, Version = "1.0"
        });
        f.Provider.Calls.Clear();

        var switched = await f.Managed.SwitchOptiScalerVersionAsync(f.Game, OptiScalerVariant.DlssNr);

        Assert.False(switched.Success);
        Assert.Equal(LinuxManagedComponentService.SwitchNotStartedPrefix + NeuralRenderingRuntimeOwnership.RefusalFor("DLSS5 Feeder"), switched.Message);
        Assert.Empty(f.Provider.Calls);
        Assert.Equal(OptiScalerVariant.Stable, f.Managed.GetInstalledOptiScalerVariant(f.Game));
    }

    // m1: the new version fails to install after the removal; the previous one comes back as its exact release (0.9.2,
    // still Latest), not the newest Stable (0.9.9).
    [Fact]
    public async Task SwitchVersion_WhenTheNewVersionFailsToInstall_PutsTheExactPreviousReleaseBack()
    {
        var f = NewPinsFixture("m1-rollback");
        var newest = f.Provider.Latest;
        f.Provider.Latest = variant => variant == OptiScalerVariant.Stable ? newest(OptiScalerVariant.Stable) with { Version = "0.9.2", SourceUrl = StableReleaseUrl } : newest(variant);
        Assert.True((await f.Managed.InstallOptiScalerAsync(f.Game, OptiScalerVariant.Stable)).Success);
        f.Provider.Latest = newest; // GitHub's Latest Stable is now 0.9.9
        // The Nightly package carries a file whose name already has a .original beside it in the game: its install fails.
        var nightlyRoot = newest(OptiScalerVariant.Nightly).RootDirectory;
        File.WriteAllText(Path.Combine(nightlyRoot, "collide.dll"), "nightly");
        File.WriteAllText(Path.Combine(f.Game.BinaryDirectory!, "collide.dll"), "the game's");
        File.WriteAllText(Path.Combine(f.Game.BinaryDirectory!, "collide.dll" + TransactionalFileDeploymentService.BackupExtension), "someone's backup");
        f.Provider.Calls.Clear();

        var switched = await f.Managed.SwitchOptiScalerVersionAsync(f.Game, OptiScalerVariant.Nightly);

        Assert.False(switched.Success);
        Assert.StartsWith(LinuxManagedComponentService.SwitchInstallFailedPrefix, switched.Message, StringComparison.Ordinal);
        Assert.EndsWith(LinuxManagedComponentService.SwitchRestoredNote, switched.Message, StringComparison.Ordinal);
        Assert.Equal(["latest:Nightly", "release:Stable:v0.9.2"], f.Provider.Calls); // both fetched before the removal, once
        var record = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(OptiScalerVariant.Stable, OptiScalerDeploymentService.GetInstalledVariant(record));
        Assert.Equal("0.9.2", record.Version);
        Assert.Null(OptiScalerDeploymentService.GetPinnedRelease(record)); // it was Latest and stays Latest
    }

    // m1: a record whose release RHI cannot name again is not switched (it could not come back).
    [Fact]
    public async Task SwitchVersion_WhenTheInstalledReleaseCannotBeNamed_RemovesNothing()
    {
        var f = NewPinsFixture("m1-unknown");
        var newest = f.Provider.Latest;
        f.Provider.Latest = variant => variant == OptiScalerVariant.Stable ? newest(variant) with { SourceUrl = "https://example.invalid/OptiScaler.7z" } : newest(variant);
        Assert.True((await f.Managed.InstallOptiScalerAsync(f.Game, OptiScalerVariant.Stable)).Success);

        var switched = await f.Managed.SwitchOptiScalerVersionAsync(f.Game, OptiScalerVariant.Nightly);

        Assert.Equal(LinuxManagedComponentService.SwitchPreviousUnavailablePrefix + OptiScalerDeploymentService.ExactVersionUnknownMessage, switched.Message);
        Assert.Equal(OptiScalerVariant.Stable, f.Managed.GetInstalledOptiScalerVariant(f.Game));
    }

    [Theory]
    [InlineData("https://github.com/optiscaler/OptiScaler/releases/download/v0.9.2/OptiScaler_v0.9.2.7z", "v0.9.2")]
    [InlineData("https://github.com/someone/else/releases/download/v0.9.2/OptiScaler.7z", null)]
    [InlineData("https://github.com/optiscaler/OptiScaler/releases/download/../x", null)]
    [InlineData("https://github.com/optiscaler/OptiScaler/releases/fake", null)]
    public void ReleaseTagOf_ReadsTheTagOnlyFromTheVersionsOwnRepository(string url, string? tag)
        => Assert.Equal(tag, OptiScalerDeploymentService.ReleaseTagOf(new LinuxDeploymentRecord { SourceUrl = url }, OptiScalerVariant.Stable));
}
