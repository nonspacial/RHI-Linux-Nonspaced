using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ControlUePostInstallTests : IDisposable
{
    private const string RendererIni =
        "{\r\n  \"m_bSkipIntroVideos\": false,\r\n  \"m_eHDRPreset\": 0,\r\n  \"m_fHDRMaxNitsOverride\": -1.0\r\n}\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-control-ue-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _sources;
    private string _srVersion = "310.4.0";

    public ControlUePostInstallTests()
    {
        _sources = Path.Combine(_root, "dlss-cache");
        Directory.CreateDirectory(_sources);
        File.WriteAllText(Path.Combine(_sources, "nvngx_dlss.dll"), "NEW-SR");
        File.WriteAllText(Path.Combine(_sources, "nvngx_dlssd.dll"), "NEW-RR");
    }

    [Fact]
    public async Task Install_UpgradesDlssDeploysRrAndSetsHdrPresetInGameFolder_RemoveRestoresBytes()
    {
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var iniBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(RendererIni)).ToArray();
        File.WriteAllBytes(Path.Combine(bin, "renderer.ini"), iniBytes);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");
        var prefixIni = Path.Combine(game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", "Remedy", "Control", "renderer.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(prefixIni)!);
        File.WriteAllText(prefixIni, RendererIni);

        var install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(install.Success, install.Message);
        Assert.Equal("NEW-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.Equal("NEW-RR", File.ReadAllText(Path.Combine(bin, "nvngx_dlssd.dll")));
        var patched = File.ReadAllBytes(Path.Combine(bin, "renderer.ini"));
        Assert.Equal(Encoding.UTF8.GetPreamble(), patched[..3]);
        Assert.Equal(RendererIni.Replace("\"m_eHDRPreset\": 0", "\"m_eHDRPreset\": 2"), Encoding.UTF8.GetString(patched[3..]));
        Assert.Equal(RendererIni, File.ReadAllText(prefixIni));
        Assert.Contains("driver-profile", install.Message);
        AssertNotGenericInPortuguese(install.Message);
        Assert.True(store.Find(game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(ControlUePostInstallService.MetadataKey));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Equal(iniBytes, File.ReadAllBytes(Path.Combine(bin, "renderer.ini")));
        Assert.False(File.Exists(Path.Combine(bin, "renodx-control-rr.addon64")));
        AssertNotGenericInPortuguese(remove.Message);
    }

    [Fact]
    public async Task Remove_AfterGameRewroteRendererIniAndUserReplacedDlss_KeepsTheirChanges()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        var rewritten = File.ReadAllText(Path.Combine(bin, "renderer.ini")).Replace("\"m_bSkipIntroVideos\": false", "\"m_bSkipIntroVideos\": true");
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), rewritten);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "USER-SR");

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(RendererIni.Replace("\"m_bSkipIntroVideos\": false", "\"m_bSkipIntroVideos\": true"),
            File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.Equal("USER-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        AssertNotGenericInPortuguese(remove.Message);
    }

    [Fact]
    public async Task Install_OverEarlierDlssSwap_RemoveReturnsTheSwappedDllAndKeepsOriginalBackup()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "SWAPPED-SR");
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll.original"), "TRUE-ORIGINAL");

        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        Assert.Equal("NEW-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("TRUE-ORIGINAL", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("SWAPPED-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("TRUE-ORIGINAL", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.Empty(Directory.EnumerateFiles(bin, "*" + ControlUePostInstallService.PreviousSnapshotSuffix));
    }

    [Fact]
    public async Task Update_ReappliesNewestDll_RemoveStillRestoresThePreRhiState()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        File.WriteAllText(Path.Combine(_sources, "nvngx_dlss.dll"), "NEWER-SR");
        _srVersion = "310.5.0";
        var update = await service.UpdateAsync(game, [game.ExecutablePath!]);

        Assert.True(update.Success, update.Message);
        Assert.Equal("NEWER-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.Contains("\"m_eHDRPreset\": 2", File.ReadAllText(Path.Combine(bin, "renderer.ini")));

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal(RendererIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
    }

    [Fact]
    public async Task Install_WithoutRendererIni_StillDeploysDllsAndSaysToLaunchFirst()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);

        var install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(install.Success, install.Message);
        Assert.Contains("renderer.ini not found", install.Message);
        Assert.Equal("NEW-SR", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "renderer.ini")));
    }

    [Fact]
    public async Task Install_OtherGameWithTheSameAddon_RunsNoControlSteps()
    {
        var (game, service, store) = Arrange("Other Remedy Game");
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");

        var install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(Path.Combine(bin, "renodx-control-rr.addon64")));
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Equal(RendererIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.False(store.Find(game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(ControlUePostInstallService.MetadataKey));
    }

    // #124 finding 1: the earlier copy is the DLL that was in place at install time; when the user changed the DLL
    // since, removal leaves both alone (the snapshot is not deleted) and says where the copy is.
    [Fact]
    public async Task Remove_AfterUserChangedASwappedDll_KeepsTheEarlierCopyAndSaysWhere()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var dll = Path.Combine(bin, "nvngx_dlss.dll");
        File.WriteAllText(dll, "SWAPPED-SR");
        File.WriteAllText(dll + ".original", "TRUE-ORIGINAL");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        var snapshot = dll + ControlUePostInstallService.PreviousSnapshotSuffix;
        Assert.Equal("SWAPPED-SR", File.ReadAllText(snapshot));

        File.WriteAllText(dll, "USER-SR");
        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("USER-SR", File.ReadAllText(dll));
        Assert.Equal("SWAPPED-SR", File.ReadAllText(snapshot));
        Assert.Equal("TRUE-ORIGINAL", File.ReadAllText(dll + ".original"));
        Assert.Contains(ControlUePostInstallService.SnapshotKeptPrefix + snapshot, remove.Message);
        AssertNotGenericInPortuguese(remove.Message);
    }

    // The earlier copy is deleted only when another file already holds exactly its bytes (here: the user put the
    // swapped DLL back by hand), so nothing is lost.
    [Fact]
    public async Task Remove_AfterUserPutTheSameBytesBack_DropsTheRedundantEarlierCopy()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var dll = Path.Combine(bin, "nvngx_dlss.dll");
        File.WriteAllText(dll, "SWAPPED-SR");
        File.WriteAllText(dll + ".original", "TRUE-ORIGINAL");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        File.WriteAllText(dll, "SWAPPED-SR");
        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("SWAPPED-SR", File.ReadAllText(dll));
        Assert.False(File.Exists(dll + ControlUePostInstallService.PreviousSnapshotSuffix));
        Assert.DoesNotContain(ControlUePostInstallService.SnapshotKeptPrefix, remove.Message);
    }

    // #124 finding 2: a DLSS version and an HDR preset the user chose after the install survive an update; the item
    // the user did not touch is still updated.
    [Fact]
    public async Task Update_AfterUserChangedDllAndPreset_KeepsTheirChoicesAndUpdatesTheRest()
    {
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "USER-SR");
        var userIni = RendererIni.Replace("\"m_eHDRPreset\": 0", "\"m_eHDRPreset\": 1");
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), userIni);
        File.WriteAllText(Path.Combine(_sources, "nvngx_dlss.dll"), "NEWER-SR");
        File.WriteAllText(Path.Combine(_sources, "nvngx_dlssd.dll"), "NEWER-RR");
        var update = await service.UpdateAsync(game, [game.ExecutablePath!]);

        Assert.True(update.Success, update.Message);
        Assert.Equal("USER-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal(userIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.Equal("NEWER-RR", File.ReadAllText(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Contains("nvngx_dlss.dll" + ControlUePostInstallService.UserChangeKeptNote, update.Message);
        Assert.Contains("renderer.ini" + ControlUePostInstallService.UserChangeKeptNote, update.Message);
        AssertNotGenericInPortuguese(update.Message);

        // The user's files are no longer RHI's: removal leaves them and only takes back what it still owns.
        var remove = await service.RemoveAsync(game);
        Assert.True(remove.Success, remove.Message);
        Assert.Equal("USER-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal(userIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDX));
    }

    // w3 review major 1 (#124): the user's DLSS DLL and HDR preset stay theirs across EVERY later update, not only the
    // one that found the change. The list is saved with the RenoDX record and dropped on removal.
    [Fact]
    public async Task Updates_TwoAndThreeInARow_NeverApplyTheStepToTheUsersDllOrPresetAgain()
    {
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var sr = Path.Combine(bin, "nvngx_dlss.dll");
        var ini = Path.Combine(bin, "renderer.ini");
        File.WriteAllText(ini, RendererIni);
        File.WriteAllText(sr, "OLD-SR");
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        File.WriteAllText(sr, "USER-SR");
        var userIni = RendererIni.Replace("\"m_eHDRPreset\": 0", "\"m_eHDRPreset\": 1");
        File.WriteAllText(ini, userIni);

        foreach (var (n, version) in new[] { (1, "310.5.0"), (2, "310.6.0"), (3, "310.7.0") })
        {
            File.WriteAllText(Path.Combine(_sources, "nvngx_dlss.dll"), $"SR-{n}");
            File.WriteAllText(Path.Combine(_sources, "nvngx_dlssd.dll"), $"RR-{n}");
            _srVersion = version;
            var update = await service.UpdateAsync(game, [game.ExecutablePath!]);

            Assert.True(update.Success, update.Message);
            Assert.Equal("USER-SR", File.ReadAllText(sr));
            Assert.Equal(userIni, File.ReadAllText(ini));
            Assert.Equal($"RR-{n}", File.ReadAllText(Path.Combine(bin, "nvngx_dlssd.dll")));
            Assert.DoesNotContain("m_eHDRPreset 1 → 2", update.Message);
            Assert.DoesNotContain($"nvngx_dlss.dll → {version}", update.Message);
            Assert.Contains("nvngx_dlss.dll" + ControlUePostInstallService.UserChangeKeptNote, update.Message);
            Assert.Contains("renderer.ini" + ControlUePostInstallService.UserChangeKeptNote, update.Message);
            Assert.False(File.Exists(sr + ControlUePostInstallService.PreviousSnapshotSuffix));
            var saved = ControlUePostInstallService.Deserialize(store.Find(game, ManagedComponentKind.RenoDX))!;
            Assert.Equal(new[] { Path.GetFullPath(sr), Path.GetFullPath(ini) }.Order(StringComparer.Ordinal), saved.UserKept);
            AssertNotGenericInPortuguese(update.Message);
        }

        var remove = await service.RemoveAsync(game);
        Assert.True(remove.Success, remove.Message);
        Assert.Equal("USER-SR", File.ReadAllText(sr));
        Assert.Equal(userIni, File.ReadAllText(ini));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDX));
    }

    // When the user changed every file the step touches, nothing stays owned but the list still rides on the record:
    // later updates leave all three alone.
    [Fact]
    public async Task Updates_AfterUserChangedEveryFile_KeepAllOfThemOnEachLaterUpdate()
    {
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var sr = Path.Combine(bin, "nvngx_dlss.dll");
        var rr = Path.Combine(bin, "nvngx_dlssd.dll");
        var ini = Path.Combine(bin, "renderer.ini");
        File.WriteAllText(ini, RendererIni);
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);

        File.WriteAllText(sr, "USER-SR");
        File.WriteAllText(rr, "USER-RR");
        var userIni = RendererIni.Replace("\"m_eHDRPreset\": 0", "\"m_eHDRPreset\": 3");
        File.WriteAllText(ini, userIni);

        for (var n = 1; n <= 3; n++)
        {
            File.WriteAllText(Path.Combine(_sources, "nvngx_dlss.dll"), $"SR-{n}");
            File.WriteAllText(Path.Combine(_sources, "nvngx_dlssd.dll"), $"RR-{n}");
            var update = await service.UpdateAsync(game, [game.ExecutablePath!]);

            Assert.True(update.Success, update.Message);
            Assert.Equal("USER-SR", File.ReadAllText(sr));
            Assert.Equal("USER-RR", File.ReadAllText(rr));
            Assert.Equal(userIni, File.ReadAllText(ini));
            var saved = ControlUePostInstallService.Deserialize(store.Find(game, ManagedComponentKind.RenoDX))!;
            Assert.Empty(saved.Dlls);
            Assert.Null(saved.RendererIni);
            Assert.Equal(3, saved.UserKept!.Count);
        }

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.Equal("USER-SR", File.ReadAllText(sr));
        Assert.Equal("USER-RR", File.ReadAllText(rr));
        Assert.Equal(userIni, File.ReadAllText(ini));
    }

    // A state written before the list existed reads as "nothing kept".
    [Fact]
    public void Deserialize_StateWithoutTheKeptList_ReadsAsNothingKept()
    {
        var record = new LinuxDeploymentRecord();
        record.Metadata[ControlUePostInstallService.MetadataKey] = "{\"Dlls\":[],\"RendererIni\":null}";
        var state = ControlUePostInstallService.Deserialize(record)!;
        Assert.True(state.IsEmpty);
        Assert.Null(state.UserKept);
    }

    // #124 finding 3 (tests for the data-safety claims): a user-changed HDR preset is left alone on removal.
    [Fact]
    public async Task Remove_AfterUserChangedThePreset_LeavesIt()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        var userIni = File.ReadAllText(Path.Combine(bin, "renderer.ini")).Replace("\"m_eHDRPreset\": 2", "\"m_eHDRPreset\": 1");
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), userIni);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(userIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.Contains("m_eHDRPreset was changed outside RHI", remove.Message);
    }

    // A restore that cannot finish keeps RenoDX installed and the unrestored items owned in the record; a retry ends it.
    [Fact]
    public async Task Remove_WhenARestoreFails_KeepsRenoDxAndTheRemainingItems_RetryFinishes()
    {
        if (Environment.UserName == "root") return; // directory modes do not stop root
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var iniBytes = Encoding.UTF8.GetBytes(RendererIni);
        File.WriteAllBytes(Path.Combine(bin, "renderer.ini"), iniBytes);
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        var before = ControlUePostInstallService.Deserialize(store.Find(game, ManagedComponentKind.RenoDX))!;

        ComponentOperationResult partial;
        File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { partial = await service.RemoveAsync(game); }
        finally { File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        Assert.False(partial.Success, partial.Message);
        Assert.StartsWith("Control UE restore incomplete; RenoDX was kept:", partial.Message);
        var kept = store.Find(game, ManagedComponentKind.RenoDX);
        Assert.NotNull(kept);
        var remaining = ControlUePostInstallService.Deserialize(kept)!;
        Assert.Contains(remaining.Dlls, d => d.Path.EndsWith("nvngx_dlssd.dll", StringComparison.Ordinal));
        Assert.Equal(before.Dlls.Count, remaining.Dlls.Count);
        Assert.True(File.Exists(Path.Combine(bin, "renodx-control-rr.addon64")));

        var retry = await service.RemoveAsync(game);

        Assert.True(retry.Success, retry.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDX));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.Equal(iniBytes, File.ReadAllBytes(Path.Combine(bin, "renderer.ini")));
        Assert.False(File.Exists(Path.Combine(bin, "renodx-control-rr.addon64")));
    }

    // When the record cannot be saved, nothing would own the Control UE changes: they are undone with the install.
    [Fact]
    public async Task Install_WhenTheRecordCannotBeSaved_UndoesTheControlSteps()
    {
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "renderer.ini"), RendererIni);
        File.WriteAllText(Path.Combine(bin, "nvngx_dlss.dll"), "OLD-SR");

        ComponentOperationResult install;
        using (LedgerWriteBlocker.Block(store, Path.Combine(_root, "records.json")))
            install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.False(install.Success, install.Message);
        Assert.Equal("OLD-SR", File.ReadAllText(Path.Combine(bin, "nvngx_dlss.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlss.dll.original")));
        Assert.False(File.Exists(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Equal(RendererIni, File.ReadAllText(Path.Combine(bin, "renderer.ini")));
        Assert.Null(store.Find(game, ManagedComponentKind.RenoDX));
    }

    // An earlier copy already beside the DLL (a crash, or one kept on purpose above) is never overwritten: that DLL
    // step is refused with a plain reason, the other steps still run.
    [Fact]
    public async Task Install_WithAnEarlierCopyInTheWay_LeavesThatDllAndTheCopy_RunsTheOtherSteps()
    {
        var (game, service, _) = Arrange(ControlUePostInstallService.GameName);
        var bin = game.BinaryDirectory!;
        var dll = Path.Combine(bin, "nvngx_dlss.dll");
        File.WriteAllText(dll, "SWAPPED-SR");
        File.WriteAllText(dll + ".original", "TRUE-ORIGINAL");
        var snapshot = dll + ControlUePostInstallService.PreviousSnapshotSuffix;
        File.WriteAllText(snapshot, "STALE");

        var install = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(install.Success, install.Message);
        Assert.Equal("SWAPPED-SR", File.ReadAllText(dll));
        Assert.Equal("STALE", File.ReadAllText(snapshot));
        Assert.Equal("TRUE-ORIGINAL", File.ReadAllText(dll + ".original"));
        Assert.Equal("NEW-RR", File.ReadAllText(Path.Combine(bin, "nvngx_dlssd.dll")));
        Assert.Contains(ControlUePostInstallService.SnapshotInTheWayPrefix + snapshot, install.Message);
        AssertNotGenericInPortuguese(install.Message);
    }

    // #136: FgSlHdr10 (Control UE frame generation's "Using HDR?") is owned on the RenoDX record: a RenoDX update keeps
    // owning it, and RenoDX removal puts reshade.ini back.
    [Fact]
    public async Task FrameGenerationHdrKey_StaysOwnedThroughARenoDxUpdate_AndRemovalPutsItBack()
    {
        var (game, service, store) = Arrange(ControlUePostInstallService.GameName);
        var ini = Path.Combine(game.BinaryDirectory!, "reshade.ini");
        const string original = "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\n";
        File.WriteAllText(ini, original);
        Assert.True((await service.InstallAsync(game, [game.ExecutablePath!])).Success);
        // SetHdrKey reads only the RenoDX record and reshade.ini (no OptiScaler needed).
        var frameGeneration = new ControlUeFrameGenerationService(null!, store);
        Assert.Equal(ControlUeFrameGenerationService.HdrSetPrefix + "1", frameGeneration.SetHdrKey(game, useHdr: true));
        Assert.Contains("[renodx-preset1]\nFgSlHdr10=1\n", File.ReadAllText(ini));

        _srVersion = "310.9.9"; // the update has something to do
        var updated = await service.UpdateAsync(game, [game.ExecutablePath!]);
        Assert.True(updated.Success, updated.Message);
        Assert.True(store.Find(game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(ControlUeFrameGenerationService.HdrMetadataKey));

        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal(original, File.ReadAllText(ini));
    }

    private (DetectedGame Game, RenoDxDeploymentService Service, LinuxDeploymentRecordStore Store) Arrange(string name)
    {
        var bin = Path.Combine(_root, "steamapps", "common", "Control");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Control_DX12.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var prefix = Path.Combine(_root, "compatdata", "870780", "pfx");
        Directory.CreateDirectory(prefix);
        var game = new DetectedGame
        {
            Name = name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", SteamAppId = 870780
        };
        var http = new HttpClient(new Handler());
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var controlUe = new ControlUePostInstallService(new TransactionalFileDeploymentService(), (kind, _) =>
            Task.FromResult<(string, string)?>(kind == DlssComponentKind.SuperResolution
                ? (Path.Combine(_sources, "nvngx_dlss.dll"), _srVersion)
                : (Path.Combine(_sources, "nvngx_dlssd.dll"), "310.4.0")));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "cache"), controlUe: controlUe);
        return (game, service, store);
    }

    private static void AssertNotGenericInPortuguese(string message)
        => Assert.True(UiLocalizationService.PresentFor("pt-BR", message) != UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), message);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == RenoDxCatalogService.WikiUrl)
            {
                const string html = "<table><tr><th>Name</th><th>Links</th><th>Status</th></tr>" +
                                    "<tr><td>Control Ultimate Edition</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-control-rr.addon64\">snapshot</a></td><td>✅</td></tr>" +
                                    "<tr><td>Other Remedy Game</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-control-rr.addon64\">snapshot</a></td><td>✅</td></tr></table>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
