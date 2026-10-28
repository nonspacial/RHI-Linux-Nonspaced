using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12 integration A1 (#243 meets #236–#238): the Update All summary names each could-not-check item that has a LOCAL
/// reason (game + component + reason, in the UI language), as it names a failure. The MFG families' local reasons — a game
/// held by its own failed undo, a file removed or changed outside RHI, a record without its file's hash or proxy name, a
/// game whose bitness changed — are carried on the check result itself (<see cref="ManagedUpdateCheck.LocalReason"/>) and
/// copied into the item by <see cref="LinuxUpdateAllService.RunAsync"/>; a release that could not be reached stays a count.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MfgLocalReasonUpdateAllTests
{
    // ---- through the app's real services, one test per family, EN + PT-BR ---------------------------------------------

    public static TheoryData<ManagedComponentKind, string> FamiliesAndLanguages()
    {
        var data = new TheoryData<ManagedComponentKind, string>();
        foreach (var kind in new[] { ManagedComponentKind.MfgAdaUnlock, ManagedComponentKind.Rtx40MfgStandalone,
                     ManagedComponentKind.Dlssg2030Unlock, ManagedComponentKind.UltimateAsiLoader })
        foreach (var language in new[] { "en-US", "pt-BR" })
            data.Add(kind, language);
        return data;
    }

    [Theory]
    [MemberData(nameof(FamiliesAndLanguages))]
    public async Task FileRemovedOutsideRhi_IsNamedInTheSummary_WithTheGameAndTheReason_InBothModes(ManagedComponentKind kind, string language)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = MfgUpdateAllTests.StreamlineGame(fx.Root, "Local " + kind);
        var missing = SeedRecord(fx, game, kind, fileOnDisk: false);

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new MfgUpdateAllTests.SwitchableRunning()).RunAsync([game], _ => [], mode);

            var item = Assert.Single(result.Items);
            Assert.Equal((true, true), (item.Unchecked, item.LocalReason));
            Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + missing, item.Message);
            Assert.Same(item, Assert.Single(result.LocalUncheckedItems));
            var summary = UiLocalizationService.UpdateAllSummaryFor(language, result);
            var reason = UiLocalizationService.PresentFor(language, item.Message).TrimEnd().TrimEnd('.');
            var named = string.Format(UiLocalizationService.TextFor(language, "Ua.UpdateAllNotChecked"), game.Name,
                LinuxManagedComponentService.DisplayDependencyName(kind), reason);
            Assert.EndsWith(named, summary, StringComparison.Ordinal);
            if (language == "pt-BR")
            {
                Assert.StartsWith(" Não verificado: " + game.Name, named, StringComparison.Ordinal);
                Assert.DoesNotContain("Not updated", summary, StringComparison.Ordinal);
                Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), summary, StringComparison.Ordinal);
            }
        }
        Assert.False(File.Exists(missing)); // never put back
    }

    // ---- every local outcome the MFG families report carries the flag --------------------------------------------------

    [Theory]
    [InlineData(ManagedComponentKind.Rtx40MfgStandalone)]
    [InlineData(ManagedComponentKind.Dlssg2030Unlock)]
    [InlineData(ManagedComponentKind.UltimateAsiLoader)]
    public async Task RecordWithoutItsProxyName_IsALocalReason(ManagedComponentKind kind)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = MfgUpdateAllTests.StreamlineGame(fx.Root, "No proxy " + kind);
        SeedRecord(fx, game, kind, fileOnDisk: true, proxyName: false);

        var check = await fx.Managed.CheckManagedUpdateAsync(game, kind, []);

        Assert.Equal((true, false, true, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck, check.LocalReason));
    }

    // w12 follow-up (re-review mfg N2): the three families say the same thing, with its way out, in the check and in the
    // update (a direct Update and Update All alike), EN + PT-BR. w12 final review M1: the way out is followed, not only read
    // (the review's probe): Remove through the app's own service succeeds, the file and the record are gone.
    [Theory]
    [InlineData(ManagedComponentKind.Rtx40MfgStandalone)]
    [InlineData(ManagedComponentKind.Dlssg2030Unlock)]
    [InlineData(ManagedComponentKind.UltimateAsiLoader)]
    public async Task RecordWithoutItsProxyName_OneLine_WithItsWayOut_InTheCheckAndTheUpdate_AndTheWayOutWorks(ManagedComponentKind kind)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = MfgUpdateAllTests.StreamlineGame(fx.Root, "One line " + kind);
        var path = Path.Combine(game.BinaryDirectory!, kind == ManagedComponentKind.Rtx40MfgStandalone ? "dinput8.dll" : "winmm.dll");
        File.WriteAllBytes(path, MfgUpdateAllTests.UniversalMfgRelease.Pe(0x42));
        var record = Record(game, kind, path);
        record.Files[0].Sha256 = FileHash.Sha256Hex(path);
        if (kind == ManagedComponentKind.UltimateAsiLoader) record.Metadata["UAL.Architecture"] = nameof(PeArchitecture.X64);
        fx.Records.Upsert(record);

        var check = await fx.Managed.CheckManagedUpdateAsync(game, kind, []);
        var update = await fx.Managed.UpdateManagedAsync(game, kind, []);

        Assert.Equal(ManagedUpdateFileGuard.MissingProxyNameMessage, check.Message);
        Assert.False(update.Success);
        Assert.Equal(ManagedUpdateFileGuard.MissingProxyNameMessage, update.Message);
        Assert.EndsWith("Remove it, then Install it again.", check.Message, StringComparison.Ordinal);
        var pt = UiLocalizationService.PresentFor("pt-BR", check.Message);
        Assert.EndsWith("Remova-o e instale-o de novo.", pt, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy DLL name", pt, StringComparison.Ordinal);

        var remove = kind == ManagedComponentKind.UltimateAsiLoader
            ? await fx.Managed.RemoveUltimateAsiLoaderAsync(game)
            : await fx.Managed.RemoveAsync(game, kind);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(path));
        Assert.Null(fx.Records.Find(game, kind));
    }

    // w12 follow-up (merge review nit 2): the GitHub add-ons' and RenoDX's refusals that come from this machine (no game exe,
    // an architecture RHI cannot read, a RenoDX installed from a local file) are named in the Update All line, EN + PT-BR.
    [Theory]
    [InlineData(ManagedComponentKind.ReLimiter, "no-exe")]
    [InlineData(ManagedComponentKind.ReLimiter, "unknown-arch")]
    [InlineData(ManagedComponentKind.RenoDX, "no-exe")]
    [InlineData(ManagedComponentKind.RenoDX, "unknown-arch")]
    [InlineData(ManagedComponentKind.RenoDX, "local-package")]
    public async Task AddonAndRenoDx_LocalRefusals_AreNamedInTheSummary(ManagedComponentKind kind, string shape)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = MfgUpdateAllTests.StreamlineGame(fx.Root, $"Local {kind} {shape}");
        var file = Path.Combine(game.BinaryDirectory!, kind == ManagedComponentKind.RenoDX ? "renodx-game.addon64" : "relimiter.addon64");
        File.WriteAllBytes(file, [0x4D, 0x5A, 1, 2]);
        var record = Record(game, kind, file);
        if (shape == "local-package") record.Metadata[RenoDxDeploymentService.LocalPackageMetadataKey] = "1";
        fx.Records.Upsert(record);
        if (shape == "no-exe") File.Delete(game.ExecutablePath!);
        if (shape == "unknown-arch") File.WriteAllText(game.ExecutablePath!, "not a Windows executable");

        var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new MfgUpdateAllTests.SwitchableRunning()).RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

        var item = Assert.Single(result.Items);
        Assert.Equal((true, true), (item.Unchecked, item.LocalReason));
        Assert.Contains($"Not checked: {game.Name} (", UiLocalizationService.UpdateAllSummaryFor("en-US", result), StringComparison.Ordinal);
        var pt = UiLocalizationService.UpdateAllSummaryFor("pt-BR", result);
        Assert.Contains($"Não verificado: {game.Name} (", pt, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt, StringComparison.Ordinal);
        Assert.DoesNotContain("update check skipped", pt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsiLoader_GameWhoseBitnessChanged_IsALocalReason_NamedInTheSummary()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = MfgUpdateAllTests.StreamlineGame(fx.Root, "Bitness changed");
        var record = Record(game, ManagedComponentKind.UltimateAsiLoader, Path.Combine(game.BinaryDirectory!, "winmm.dll"));
        record.Metadata["UAL.ProxyName"] = "winmm.dll";
        record.Metadata["UAL.Architecture"] = nameof(PeArchitecture.X86); // the game's exe now reads x64
        File.WriteAllBytes(record.Files[0].Path, MfgUpdateAllTests.UniversalMfgRelease.Pe(0x41));
        fx.Records.Upsert(record);

        var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new MfgUpdateAllTests.SwitchableRunning()).RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

        var item = Assert.Single(result.Items);
        Assert.StartsWith(UltimateAsiLoaderDeploymentService.ArchitectureChangedPrefix, item.Message, StringComparison.Ordinal);
        Assert.Equal((true, true), (item.Unchecked, item.LocalReason));
        Assert.Contains("Not checked: Bitness changed (", UiLocalizationService.UpdateAllSummaryFor("en-US", result), StringComparison.Ordinal);
        Assert.Contains("Não verificado: Bitness changed (", UiLocalizationService.UpdateAllSummaryFor("pt-BR", result), StringComparison.Ordinal);
    }

    [PermissionDenialFact]
    public async Task HeldGame_IsALocalReason()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-a1-held-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var game = MfgUpdateAllTests.StreamlineGame(root, "Held");
            var records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
            var file = Path.Combine(game.BinaryDirectory!, "renodx-mfgunlock.addon64");
            File.WriteAllBytes(file, [1, 2, 3]);
            records.Upsert(Record(game, ManagedComponentKind.MfgAdaUnlock, file));
            var (journal, _) = SimulatedDeath.NewJournal(Path.Combine(root, "journal"), records);
            using var hold = await FailedUndoHold.ArmAsync(journal, records, game, ManagedComponentKind.MfgAdaUnlock, file, [9, 9]);

            var held = await ManagedUpdateFileGuard.HeldAsync(journal, game, ManagedComponentKind.MfgAdaUnlock, "0.5");

            Assert.NotNull(held);
            Assert.Equal((true, true), (held!.CouldNotCheck, held.LocalReason));
        }
        finally
        {
            PermissionDenial.MakeWritable(Path.Combine(root, "games", "Held"));
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("no-hash")]
    [InlineData("changed")]
    public async Task GuardBeforePlanning_EveryRefusal_IsALocalReason_APlannedUpdateIsNot(string shape)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-a1-guard-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var file = Path.Combine(root, "proxy.dll");
            File.WriteAllBytes(file, [1, 2, 3]);
            var sha = FileHash.Sha256Hex(file);
            if (shape == "removed") File.Delete(file);
            if (shape == "changed") File.WriteAllBytes(file, [4, 5, 6]);
            var planned = new ManagedUpdateCheck(true, true, "Update available: 2.0", "1.0", "2.0");

            var refused = await ManagedUpdateFileGuard.BeforePlanningAsync(planned, [(file, shape == "no-hash" ? null : sha)], CancellationToken.None);
            var kept = await ManagedUpdateFileGuard.BeforePlanningAsync(planned with { UpdateAvailable = false }, [(file, null)], CancellationToken.None);

            Assert.Equal((false, true, true), (refused.UpdateAvailable, refused.CouldNotCheck, refused.LocalReason));
            Assert.False(kept.LocalReason);
            Assert.True(ManagedUpdateFileGuard.Removed([Path.Combine(root, "gone.dll")], "1.0")!.LocalReason);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- RunAsync copies the flag; a remote could-not-check stays a count ----------------------------------------------

    [Theory]
    [InlineData(true, LinuxUpdateRunMode.Manual, true)]
    [InlineData(true, LinuxUpdateRunMode.Background, true)]
    [InlineData(false, LinuxUpdateRunMode.Manual, false)]
    [InlineData(false, LinuxUpdateRunMode.Background, false)]
    public async Task RunAsync_NamesACheckThatCarriesALocalReason_EvenWhenItsFamilyIsNotCheckedFirst(bool local, LinuxUpdateRunMode mode, bool named)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-a1-run-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var game = MfgUpdateAllTests.StreamlineGame(root, "Run");
            var records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
            records.Upsert(Record(game, ManagedComponentKind.Dlssg2030Unlock, Path.Combine(game.BinaryDirectory!, "winmm.dll")));
            var answer = new ManagedUpdateCheck(true, false, "Some reason.", "1.0", CouldNotCheck: true, LocalReason: local);

            var result = await new LinuxUpdateAllService(records, new Backend(answer), new MfgUpdateAllTests.SwitchableRunning()).RunAsync([game], _ => [], mode);

            var item = Assert.Single(result.Items);
            Assert.True(item.Unchecked);
            Assert.Equal(named, item.LocalReason);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string SeedRecord(ViewModelFixture fx, DetectedGame game, ManagedComponentKind kind, bool fileOnDisk, bool proxyName = true)
    {
        var name = kind switch
        {
            ManagedComponentKind.MfgAdaUnlock => "renodx-mfgunlock.addon64",
            ManagedComponentKind.Rtx40MfgStandalone => "dinput8.dll",
            _ => "winmm.dll"
        };
        var path = Path.Combine(game.BinaryDirectory!, name);
        var record = Record(game, kind, path);
        if (proxyName)
        {
            if (kind == ManagedComponentKind.Rtx40MfgStandalone) record.Metadata["UniversalMFG.ProxyName"] = name;
            if (kind == ManagedComponentKind.Dlssg2030Unlock) record.Metadata["FG2030.ProxyName"] = name;
            if (kind == ManagedComponentKind.UltimateAsiLoader) record.Metadata["UAL.ProxyName"] = name;
        }
        else if (kind == ManagedComponentKind.Rtx40MfgStandalone) record.Files.Clear(); // no proxy name anywhere
        if (kind == ManagedComponentKind.UltimateAsiLoader) record.Metadata["UAL.Architecture"] = nameof(PeArchitecture.X64);
        if (fileOnDisk) File.WriteAllBytes(path, MfgUpdateAllTests.UniversalMfgRelease.Pe(0x42));
        fx.Records.Upsert(record);
        return path;
    }

    private static LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind, string file) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!,
        Version = "0.5", Files = [new LinuxDeployedFile { Path = file, Sha256 = new string('a', 64) }]
    };

    private sealed class Backend(ManagedUpdateCheck answer) : ILinuxManagedUpdateBackend
    {
        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => Task.FromResult(answer);

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => Task.FromResult(ComponentOperationResult.Fail("not expected"));
    }
}
