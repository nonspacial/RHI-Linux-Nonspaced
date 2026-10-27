using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

// #236 (upstream v2.8.8 Beta bb9b2fd, Rtx40MfgService.ReleaseDllNames): the publisher renamed the project to MFG Unlocker
// (dashdogy/MFG-Unlocker; the old API URL answers 301). v1.5.0 and newer ship MFGUnlocker-v<ver>.zip holding one
// MFGUnlocker.dll plus a SHA256SUMS.txt naming the zip and the DLL; v1.4.2 and older ship RTXMFG-v<ver>.zip with RTXMFG.dll.
// Every release since v1.5.0 used to be refused: Install failed, the check could not check, v1.4.x installs never updated.
public sealed partial class Rtx40MfgStandaloneDeploymentServiceTests
{
    private const string LegacyReleasesApiUrl = "https://api.github.com/repos/dashdogy/RTX40MFG-Unlock/releases/latest";

    private static byte[] MfgUnlockerZip(byte marker) => CreateZip(("MFGUnlocker.dll", CreatePe(0x8664, 512 * 1024, marker)));

    private static Rtx40Handler V142ThenV170(byte oldMarker, byte newMarker)
    {
        var handler = Rtx40Handler.WithVersions(
            ("1.4.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, oldMarker)))),
            ("1.7.0", MfgUnlockerZip(newMarker)));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        handler.ActiveVersion = "1.4.2";
        return handler;
    }

    [Fact]
    public void ReleasesApi_IsTheRenamedRepository()
    {
        Assert.Equal("https://api.github.com/repos/dashdogy/MFG-Unlocker/releases/latest", Rtx40MfgStandaloneDeploymentService.ReleasesApiUrl);
        Assert.Equal("https://github.com/dashdogy/MFG-Unlocker", Rtx40MfgStandaloneDeploymentService.RepositoryUrl);
        Assert.Equal(["MFGUnlocker.dll", "RTXMFG.dll"], Rtx40MfgStandaloneDeploymentService.ReleaseDllNames);
    }

    [Fact]
    public async Task Install_V170ShapedRelease_DeploysMfgUnlockerDllUnderTheChosenName()
    {
        using var fixture = CreateGame("mfgunlocker-install", 0x8664, GraphicsApi.DirectX12);
        var dll = CreatePe(0x8664, 512 * 1024, 0x71);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", CreateZip(("MFGUnlocker.dll", dll))));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "dinput8.dll");

        Assert.True(result.Success, result.Message);
        Assert.Equal(dll, File.ReadAllBytes(Path.Combine(fixture.Bin, "dinput8.dll")));
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        Assert.Equal("1.7.0", record.Version);
        Assert.Equal("MFGUnlocker-v1.7.0.zip", record.Metadata["RTX40MFG.AssetName"]);
        Assert.Equal("https://github.com/dashdogy/MFG-Unlocker", record.Metadata["RTX40MFG.Upstream"]);
        Assert.Equal("native,builtin", ProxyOverride(fixture, "dinput8"));
    }

    [Fact]
    public async Task CheckManaged_V142RecordAndV170Release_ReadsTheUpdate()
    {
        using var fixture = CreateGame("mfgunlocker-check", 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x72, 0x73);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        handler.ActiveVersion = "1.7.0";

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.False(check.CouldNotCheck, check.Message);
        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal(("1.4.2", "1.7.0"), (check.InstalledVersion, check.LatestVersion));
    }

    [Fact]
    public async Task Update_V142ToV170_WritesMfgUnlockerUnderTheRecordsProxyName()
    {
        using var fixture = CreateGame("mfgunlocker-update", 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x74, 0x75);
        using var http = new HttpClient(handler);
        var (service, records, _, journalDir, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "dinput8.dll")).Success);
        handler.ActiveVersion = "1.7.0";

        var result = await service.UpdateAsync(fixture.Game);

        Assert.True(result.Success, result.Message);
        var target = Path.Combine(fixture.Bin, "dinput8.dll");
        Assert.Equal(CreatePe(0x8664, 512 * 1024, 0x75), File.ReadAllBytes(target));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "MFGUnlocker.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "RTXMFG.dll")));
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        Assert.Equal(("1.7.0", "dinput8.dll", "MFGUnlocker-v1.7.0.zip", "v1.7.0"),
            (record.Version, record.Metadata["UniversalMFG.ProxyName"], record.Metadata["RTX40MFG.AssetName"], record.Metadata["RTX40MFG.Tag"]));
        Assert.Equal(handler.AssetUrlOf("1.7.0"), record.SourceUrl);
        Assert.Equal("https://github.com/dashdogy/MFG-Unlocker", record.Metadata["RTX40MFG.Upstream"]);
        Assert.Equal(Sha256(target), record.Files.Single().Sha256, ignoreCase: true);
        DownloadRecordAssert.Recorded(record, Sha256(target), VerifiedDownload.PublishedDigest);
        Assert.Equal("native,builtin", ProxyOverride(fixture, "dinput8"));
        Assert.Empty(Directory.GetFiles(journalDir));
    }

    // Upstream's order: the newest naming first. A zip holding both (a transition build) deploys MFGUnlocker.dll.
    [Fact]
    public async Task Archive_HoldingBothNames_TakesMfgUnlocker()
    {
        using var fixture = CreateGame("mfgunlocker-both", 0x8664, GraphicsApi.DirectX12);
        var mfg = CreatePe(0x8664, 512 * 1024, 0x76);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x77)), ("MFGUnlocker.dll", mfg))));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(result.Success, result.Message);
        Assert.Equal(mfg, File.ReadAllBytes(Path.Combine(fixture.Bin, "winmm.dll")));
    }

    [Theory]
    [InlineData("two")]
    [InlineData("none")]
    public async Task Archive_WithoutExactlyOneDllOfTheChosenName_IsRefusedBeforeAnyChange(string shape)
    {
        using var fixture = CreateGame("mfgunlocker-" + shape, 0x8664, GraphicsApi.DirectX12);
        var pe = CreatePe(0x8664, 512 * 1024, 0x78);
        var zip = shape == "two"
            ? CreateZip(("a/MFGUnlocker.dll", pe), ("b/MFGUnlocker.dll", pe), ("RTXMFG.dll", pe))
            : CreateZip(("MFGUnlocker.asi", pe), ("README.md", [1, 2, 3]));
        using var handler = Rtx40Handler.WithVersions(("1.7.0", zip));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.EndsWith(shape == "two"
            ? Rtx40MfgStandaloneDeploymentService.SeveralDllsPrefix + "MFGUnlocker.dll"
            : Rtx40MfgStandaloneDeploymentService.NoDllMessage, result.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
        Assert.Null(ProxyOverride(fixture, "version"));
    }

    // A release listing both namings (a re-upload under the old name beside the new one) takes the newest naming.
    [Fact]
    public async Task Release_ListingBothNamings_TakesTheMfgUnlockerAsset()
    {
        using var fixture = CreateGame("mfgunlocker-assets", 0x8664, GraphicsApi.DirectX12);
        var mfg = CreatePe(0x8664, 512 * 1024, 0x79);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", CreateZip(("MFGUnlocker.dll", mfg))));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        handler.ExtraAssets.Add(("RTXMFG-v1.7.0.zip", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x7A)))));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var release = await service.ResolveLatestReleaseAsync(CancellationToken.None);
        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.Equal("MFGUnlocker-v1.7.0.zip", release.AssetName);
        Assert.True(result.Success, result.Message);
        Assert.Equal(mfg, File.ReadAllBytes(Path.Combine(fixture.Bin, "version.dll")));
    }

    [Fact]
    public async Task Release_ListingTwoMfgUnlockerZips_IsRefusedBeforeTheDownload()
    {
        using var fixture = CreateGame("mfgunlocker-twozips", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", MfgUnlockerZip(0x7B)));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        handler.ExtraAssets.Add(("MFGUnlocker-v1.7.0-debug.zip", MfgUnlockerZip(0x7C)));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.EndsWith(Rtx40MfgStandaloneDeploymentService.SeveralAssetsPrefix + "MFGUnlocker-*.zip", result.Message);
        Assert.Equal(1, handler.RequestCount); // the release JSON only
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    // The fail-closed rule is unchanged for the new naming: GitHub's digest and SHA256SUMS.txt's line for the zip.
    [Theory]
    [InlineData("digest", "SHA-256")]
    [InlineData("sums", "SHA256SUMS")]
    public async Task MfgUnlockerRelease_ChecksumMismatch_IsRefusedBeforeAnyChange(string which, string expected)
    {
        using var fixture = CreateGame("mfgunlocker-" + which, 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", MfgUnlockerZip(0x7D)));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        if (which == "digest") handler.DigestOverride = new string('0', 64);
        else handler.ChecksumOverride = new string('f', 64);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(result.Success);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    // The old repository URL still answers through GitHub's 301 to /repositories/<id>/…, through the app's token handler:
    // the release resolves on the redirected host and the token is only ever sent to api.github.com.
    [Fact]
    public async Task OldRepositoryUrl_RedirectStillResolves_ThroughTheTokenHandler()
    {
        using var fixture = CreateGame("mfgunlocker-redirect", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", MfgUnlockerZip(0x7E)));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        handler.RedirectFrom = LegacyReleasesApiUrl;
        var tokenPath = Path.Combine(fixture.Root, "github-token");
        File.WriteAllText(tokenPath, "rhi-test-token");
        File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var http = new HttpClient(new GitHubApiTokenHandler(tokenPath, handler));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new Rtx40MfgStandaloneDeploymentService(http, records, new WineDllOverrideService(),
            new RHI.Platform.Linux.Paths.Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"))
        { ReleasesApi = LegacyReleasesApiUrl };

        var release = await service.ResolveLatestReleaseAsync(CancellationToken.None);
        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.Equal(("1.7.0", "MFGUnlocker-v1.7.0.zip"), (release.Version, release.AssetName));
        Assert.True(install.Success, install.Message);
        Assert.Contains(handler.Seen, seen => seen.Url == LegacyReleasesApiUrl && seen.Authorized);
        Assert.All(handler.Seen.Where(seen => !seen.Url.StartsWith("https://api.github.com/", StringComparison.Ordinal)),
            seen => Assert.False(seen.Authorized, seen.Url));
    }

    // w11 lesson: a check that names the installed version from the record must see the record's file before it plans a
    // swap. A file removed outside RHI is never put back by an update, so it is not an update: "could not be checked",
    // with a line saying why (never an update the swap would refuse on every pass).
    [Fact]
    public async Task CheckManaged_FileRemovedOutsideRhi_IsCouldNotCheck_AndUpdateNeverRecreatesIt()
    {
        using var fixture = CreateGame("mfgunlocker-removed", 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x41, 0x42);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        File.Delete(target);
        handler.ActiveVersion = "1.7.0";

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.True(check.Managed);
        Assert.False(check.UpdateAvailable, check.Message);
        Assert.True(check.CouldNotCheck, check.Message);
        Assert.StartsWith(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix, check.Message);
        Assert.False(update.Success);
        Assert.False(File.Exists(target));
        Assert.Equal("1.4.2", records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!.Version);
    }

    [Fact]
    public async Task CheckManaged_FileChangedOutsideRhi_IsCouldNotCheck_AndNothingIsWritten()
    {
        using var fixture = CreateGame("mfgunlocker-changed", 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x43, 0x44);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var replaced = CreatePe(0x8664, 512 * 1024, 0x45);
        File.WriteAllBytes(target, replaced);
        handler.ActiveVersion = "1.7.0";

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.False(check.UpdateAvailable, check.Message);
        Assert.True(check.CouldNotCheck, check.Message);
        Assert.StartsWith(ManagedUpdateFileGuard.ChangedOutsideRhiPrefix, check.Message);
        Assert.Equal(replaced, File.ReadAllBytes(target));
    }

    // Hash only when an update is planned: a current install is never hashed (the file may be large), so a changed file
    // with no newer release reads as current.
    [Fact]
    public async Task CheckManaged_NoNewerRelease_IsCurrentWithoutLookingAtTheFile()
    {
        using var fixture = CreateGame("mfgunlocker-current", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", MfgUnlockerZip(0x46)));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        File.WriteAllBytes(Path.Combine(fixture.Bin, "winmm.dll"), CreatePe(0x8664, 512 * 1024, 0x47));

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.False(check.UpdateAvailable, check.Message);
        Assert.False(check.CouldNotCheck, check.Message);
    }
    // ---- crash tests over the v1.4.2 → v1.7.0 update (plan §7) ------------------------------------------------------

    [Trait("Category", "CommitPoint")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_V142ToV170_CancelAtTheWritePhase_RunsToTheEnd_OrRollsBack_WithNoDownloadAfterTheGate(bool forceRollback)
    {
        using var fixture = CreateGame("mfgunlocker-cancel-" + forceRollback, 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x48, 0x49);
        var gate = new GateWatchingHandler(handler);
        using var http = new HttpClient(gate);
        var (service, records, _, _, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "dinput8.dll")).Success);
        handler.ActiveVersion = "1.7.0";
        var target = Path.Combine(fixture.Bin, "dinput8.dll");

        var run = await CancelAtWritePhase.RunAsync(ct => service.UpdateAsync(fixture.Game, ct), [fixture.Bin, fixture.Prefix],
            atWritePhase: () =>
            {
                gate.CloseGate();
                if (forceRollback) records.SaveFaultForTesting = () => new IOException("disk full");
            });
        records.SaveFaultForTesting = null;

        Assert.Empty(gate.LateRequests);
        Assert.Equal(!forceRollback, run.Result.Success);
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        if (forceRollback)
        {
            CancelAtWritePhase.AssertUnchanged(run.Before, fixture.Bin, fixture.Prefix);
            Assert.Equal("1.4.2", record.Version);
        }
        else
        {
            Assert.Equal(CreatePe(0x8664, 512 * 1024, 0x49), File.ReadAllBytes(target));
            Assert.Equal("1.7.0", record.Version);
        }
        Assert.Equal(Sha256(target), record.Files.Single().Sha256, ignoreCase: true);
    }

    [Trait("Category", "CommitPoint")]
    [Fact]
    public async Task Update_V142ToV170_DeathAfterTheFileWrite_NextStartLeavesRecordAndFileAgreeing()
    {
        using var fixture = CreateGame("mfgunlocker-death", 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x4A, 0x4B);
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "dinput8.dll")).Success);
        handler.ActiveVersion = "1.7.0";
        var target = Path.Combine(fixture.Bin, "dinput8.dll");
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.UpdateAsync(fixture.Game));
        Assert.True(died);
        Assert.Equal(CreatePe(0x8664, 512 * 1024, 0x4B), File.ReadAllBytes(target)); // written before the record

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        var record = new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        Assert.Equal("1.4.2", record.Version);
        Assert.Equal(CreatePe(0x8664, 512 * 1024, 0x4A), File.ReadAllBytes(target));
        Assert.Equal(Sha256(target), record.Files.Single().Sha256, ignoreCase: true);
        Assert.Equal("native,builtin", ProxyOverride(fixture, "dinput8"));
    }

    [Trait("Category", "CommitPoint")]
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Update_V142ToV170_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        var handlers = new List<Rtx40Handler>();
        try
        {
            async Task<CommitPointCase> Make(string name)
            {
                var fixture = CreateGame($"mfgunlocker-agree-{shape}-{name}", 0x8664, GraphicsApi.DirectX12);
                var handler = V142ThenV170(0x4C, 0x4D);
                handlers.Add(handler);
                var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, new HttpClient(handler));
                Assert.True((await service.InstallAsync(fixture.Game, "dinput8.dll")).Success);
                handler.ActiveVersion = "1.7.0";
                return new CommitPointCase(fixture.Root, [fixture.Bin, fixture.Prefix], records, ledger, journal, journalDir,
                    () => service.UpdateAsync(fixture.Game));
            }

            var results = await CommitPointAgreement.AssertEveryWriteAsync(Make, shape);

            Assert.True(results[^1].Success); // the commit write is the last
        }
        finally
        {
            foreach (var handler in handlers) handler.Dispose();
        }
    }
}
