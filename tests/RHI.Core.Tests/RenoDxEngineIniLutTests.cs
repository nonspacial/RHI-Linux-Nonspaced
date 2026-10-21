using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #217: RenoDX writes <c>r.LUT.UpdateEveryFrame=1</c> under <c>[/Script/Engine.RendererSettings]</c> in an Unreal game's
/// Engine.ini at install, update and local drop (upstream <c>ApplyEngineIniLutSetting</c>), owned per key on RenoDX's
/// record: a key the file already has is the user's (never changed, never owned); removal and the ⚙ Off take out only
/// RHI's line, and put back the lock state the file had (upstream makes Engine.ini read-only once its key is in). Not for a
/// non-Unreal game, a game the manifest marks <c>lut: false</c>, or one with a custom Engine.ini file.
/// </summary>
public sealed partial class RenoDxEngineIniLutTests : IDisposable
{
    internal const string Before = "[SystemSettings]\nUserKeep=42\n";
    private const UnixFileMode Open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Locked = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const string Line = "r.LUT.UpdateEveryFrame=1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-lut-" + Guid.NewGuid().ToString("N"));

    public RenoDxEngineIniLutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Install_AddsTheKeyOwned_AndLocks_RemovalTakesOnlyRhisLineOut_AndUnlocks()
    {
        var fx = Fixture("install");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);

        var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(Before + "\n[/Script/Engine.RendererSettings]\n" + Line + "\n", File.ReadAllText(fx.EngineIni));
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni)); // as upstream: the game cannot rewrite it
        var owned = Assert.Single(RenoDxEngineIni.Read(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX))!.Entries);
        Assert.Equal(("lut", "r.LUT.UpdateEveryFrame", "1"), (owned.Group, owned.Key, owned.AppliedValue));

        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(Before, File.ReadAllText(fx.EngineIni));
        Assert.Equal(Open, File.GetUnixFileMode(fx.EngineIni));
    }

    [Fact]
    public async Task Install_KeyAlreadyThere_LeavesTheFileAsItIs_AndOwnsNothing()
    {
        var fx = Fixture("present");
        const string user = "[SystemSettings]\nr.LUT.UpdateEveryFrame=0\n"; // the user's own value, in another section (upstream: anywhere)
        File.WriteAllText(fx.EngineIni, user);
        File.SetUnixFileMode(fx.EngineIni, Open);

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);

        Assert.Equal(user, File.ReadAllText(fx.EngineIni));
        Assert.Equal(Open, File.GetUnixFileMode(fx.EngineIni));
        Assert.Null(RenoDxEngineIni.Read(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)));
        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(user, File.ReadAllText(fx.EngineIni));
    }

    [Fact]
    public async Task Install_UserLockedFile_IsWrittenAndStaysLocked_RemovalKeepsTheUsersLock()
    {
        var fx = Fixture("user-locked");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Locked);

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        Assert.Contains(Line, File.ReadAllText(fx.EngineIni)); // a lock never blocks the install
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(Before, File.ReadAllText(fx.EngineIni));
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni)); // the user's lock, not RHI's: it stays
    }

    [Fact]
    public async Task Install_NoEngineIniYet_CreatesIt_RemovalDeletesIt()
    {
        var fx = Fixture("absent");

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        Assert.Equal("[/Script/Engine.RendererSettings]\n" + Line + "\n", File.ReadAllText(fx.EngineIni));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.False(File.Exists(fx.EngineIni));
    }

    [Fact]
    public async Task UpdateAndDrop_KeepTheKeyOnce_AndADropOnAFreshGameWritesIt()
    {
        var fx = Fixture("update");
        File.WriteAllText(fx.EngineIni, Before);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        var afterInstall = File.ReadAllText(fx.EngineIni);

        Assert.True((await fx.Service.UpdateAsync(fx.Game, fx.Files)).Success);
        Assert.Equal(afterInstall, File.ReadAllText(fx.EngineIni));
        Assert.Single(RenoDxEngineIni.Read(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX))!.Entries);

        var dropFx = Fixture("drop");
        File.WriteAllText(dropFx.EngineIni, Before);
        var dropped = await dropFx.Service.InstallLocalAddonAsync(dropFx.Game, dropFx.Dropped());
        Assert.True(dropped.Success, dropped.Message);
        Assert.Contains(Line, File.ReadAllText(dropFx.EngineIni));
        Assert.Single(RenoDxEngineIni.Read(dropFx.Records.Find(dropFx.Game, ManagedComponentKind.RenoDX))!.Entries);
    }

    // Upstream's resolver also finds Documents/My Games/<game>/Saved/Config (Borderlands 3 keeps its Engine.ini there).
    [Fact]
    public async Task Install_EngineIniUnderMyGames_IsFound_AsUpstreamDoes()
    {
        var fx = Fixture("my-games", game => game.EngineIniProjectOverride = null);
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(fx.EngineIni)!)!)!)!, recursive: true);
        var myGames = Path.Combine(fx.Game.PrefixPath!, "drive_c", "users", "steamuser", "Documents", "My Games", "Lut Game", "Saved", "Config", "WindowsNoEditor", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(myGames)!);
        File.WriteAllText(myGames, Before);

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);

        Assert.Contains(Line, File.ReadAllText(myGames));
        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(Before, File.ReadAllText(myGames));
    }

    public static TheoryData<string> NotApplicable => new() { "unity", "manifest-lut-false", "custom-engine-file" };

    [Theory]
    [MemberData(nameof(NotApplicable))]
    public async Task NotWritten_ForANonUnrealGame_ManifestLutFalse_OrACustomEngineIniFile(string why)
    {
        var fx = Fixture(why, game => _ = why switch
        {
            "unity" => game.EngineHint = "Unity",
            "manifest-lut-false" => (game.UeExtendedLutByManifest = false).ToString(),
            _ => game.EngineIniFileOverride = "lut-custom.ini"
        });
        File.WriteAllText(fx.EngineIni, Before);

        var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);

        Assert.True(installed.Success, installed.Message);
        Assert.DoesNotContain(Line, File.ReadAllText(fx.EngineIni));
        Assert.False(fx.Service.OffersEngineIniLut(fx.Game)); // and the ⚙ row is not shown
    }

    [Fact]
    public async Task GearOff_TakesRhisLineOut_AndUpdatesFollowIt_GearOn_PutsItBack()
    {
        var fx = Fixture("gear");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        Assert.True(fx.Service.OffersEngineIniLut(fx.Game));
        Assert.True(fx.Service.EngineIniLutIsOn(fx.Game));

        var off = await fx.Service.SetEngineIniLutAsync(fx.Game, on: false);
        Assert.True(off.Success, off.Message);
        Assert.Equal(Before, File.ReadAllText(fx.EngineIni));
        Assert.Equal(Open, File.GetUnixFileMode(fx.EngineIni));
        Assert.False(fx.Service.EngineIniLutIsOn(fx.Game));
        Assert.True((await fx.Service.UpdateAsync(fx.Game, fx.Files)).Success);
        Assert.Equal(Before, File.ReadAllText(fx.EngineIni)); // the update follows the user's Off

        var on = await fx.Service.SetEngineIniLutAsync(fx.Game, on: true);
        Assert.True(on.Success, on.Message);
        Assert.Contains(Line, File.ReadAllText(fx.EngineIni));
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));
        Assert.True(fx.Service.EngineIniLutIsOn(fx.Game));
    }

    [Fact]
    public async Task GearOff_AUserValueChangedSinceRhisWrite_StaysAndIsNoLongerOwned()
    {
        var fx = Fixture("user-edit");
        File.WriteAllText(fx.EngineIni, Before);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        File.SetUnixFileMode(fx.EngineIni, Open);
        File.WriteAllText(fx.EngineIni, File.ReadAllText(fx.EngineIni).Replace(Line, "r.LUT.UpdateEveryFrame=0"));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);

        Assert.Contains("r.LUT.UpdateEveryFrame=0", File.ReadAllText(fx.EngineIni));
    }

    public static TheoryData<string, string> CommitPoints() => new()
    {
        { "ledger-blocked", "install" }, { "crash", "install" }, { "ledger-blocked", "gear-off" }, { "crash", "gear-off" },
        { "ledger-blocked", "drop" }, { "crash", "drop" }
    };

    [Theory]
    [MemberData(nameof(CommitPoints))]
    public async Task FailsOrDies_AtTheCommitPoint_EngineIniAndItsLockAreBack(string failure, string operation)
    {
        var fx = Fixture("commit-" + failure + "-" + operation);
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);
        if (operation == "gear-off") Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        var bytes = File.ReadAllBytes(fx.EngineIni);
        var mode = File.GetUnixFileMode(fx.EngineIni);
        var recordBefore = RenoDxJournalTests.Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));
        Func<Task<ComponentOperationResult>> run = operation switch
        {
            "install" => () => fx.Service.InstallAsync(fx.Game, fx.Files),
            "drop" => () => fx.Service.InstallLocalAddonAsync(fx.Game, fx.Dropped()),
            _ => () => fx.Service.SetEngineIniLutAsync(fx.Game, on: false)
        };

        await RenoDxJournalTests.FailAsync(fx.Journal, failure, 1, JournalRecoveryOutcome.RolledBack, run, () =>
        {
            Assert.Equal(bytes, File.ReadAllBytes(fx.EngineIni));
            Assert.Equal(mode, File.GetUnixFileMode(fx.EngineIni));
            Assert.Equal(recordBefore, RenoDxJournalTests.Describe(new LinuxDeploymentRecordStore(fx.Journal.LedgerPath).Find(fx.Game, ManagedComponentKind.RenoDX)));
        });
    }

    // ── correction round (w8 review) ───────────────────────────────────────────────────────────────────────────

    // Review 1: a manifest project override names a folder the game creates on its first launch. Before that launch the
    // install succeeds without the key, creates no folder, and the ⚙ row is hidden (upstream's Engine.ini step logs and
    // carries on; the fork creates no Engine.ini folder).
    [Fact]
    public async Task Install_ProjectOverride_ConfigFolderNotThereYet_InstallsWithoutTheKey_AndHidesTheRow()
    {
        var fx = Fixture("no-config-folder");
        var project = ProjectFolder(fx);
        Directory.Delete(project, recursive: true);

        var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);

        Assert.True(installed.Success, installed.Message);
        Assert.False(Directory.Exists(project));
        Assert.DoesNotContain(RenoDxDeploymentService.EngineIniKeysNotWrittenPrefix, installed.Message); // nothing went wrong
        Assert.False(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxEngineIni.StateMetadataKey));
        Assert.False(fx.Service.OffersEngineIniLut(fx.Game));
        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.False(Directory.Exists(project));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("update-all")]
    [InlineData("drop")]
    public async Task UpdateUpdateAllAndDrop_ProjectOverride_ConfigFolderNotThereYet_SucceedWithoutTheKey(string operation)
    {
        var fx = Fixture("no-config-folder-" + operation);
        var project = ProjectFolder(fx);
        Directory.Delete(project, recursive: true);
        if (operation != "drop") Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);

        if (operation == "update-all")
        {
            fx.Remote.Rebuild();
            var all = await new LinuxUpdateAllService(fx.Records, new Backend(fx.Service), new NeverRunning())
                .RunAsync([fx.Game], _ => fx.Files, LinuxUpdateRunMode.Manual);
            Assert.True(all.ChangedCount == 1 && all.FailedCount == 0, all.Summary);
        }
        else
        {
            var result = operation == "update"
                ? await fx.Service.UpdateAsync(fx.Game, fx.Files)
                : await fx.Service.InstallLocalAddonAsync(fx.Game, fx.Dropped());
            Assert.True(result.Success, result.Message);
        }

        Assert.False(Directory.Exists(project));
        Assert.False(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxEngineIni.StateMetadataKey));
        Assert.False(fx.Service.OffersEngineIniLut(fx.Game));
    }

    // Review 2: the key is optional. A link outside the prefix or an Engine.ini RHI cannot read never fails RenoDX's install,
    // update or drop: RenoDX goes in, the file is left as it is, and the result says plainly why the line is not there.
    [Theory]
    [InlineData("install")]
    [InlineData("drop")]
    public async Task SavedLinkedOutsideThePrefix_RenoDxStillInstalls_WithoutTheKey_AndSaysWhy(string operation)
    {
        var fx = Fixture("linked-saved-" + operation);
        var saved = Path.Combine(ProjectFolder(fx), "Saved");
        var outside = Path.Combine(fx.Root, "outside", "Saved");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        Directory.Move(saved, outside);
        Directory.CreateSymbolicLink(saved, outside);
        var shared = Path.Combine(outside, "Config", "Windows", "Engine.ini");
        File.WriteAllText(shared, Before);
        File.SetUnixFileMode(shared, Open);

        var result = operation == "install"
            ? await fx.Service.InstallAsync(fx.Game, fx.Files)
            : await fx.Service.InstallLocalAddonAsync(fx.Game, fx.Dropped());

        Assert.True(result.Success, result.Message);
        Assert.Contains(" · " + RenoDxDeploymentService.EngineIniKeysNotWrittenPrefix + LumaEngineIniOverrideManager.LinkRefusalPrefix, result.Message);
        Assert.Equal(Before, File.ReadAllText(shared));
        Assert.Equal(Open, File.GetUnixFileMode(shared));
        Assert.False(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxEngineIni.StateMetadataKey));
        Assert.DoesNotContain("not written", UiLocalizationService.PresentFor("pt-BR", result.Message)); // PT-BR says it too
    }

    [Fact]
    public async Task UnreadableEngineIni_InstallAndUpdate_StillSucceed_WithoutTheKey_AndSayWhy()
    {
        var fx = Fixture("unreadable");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, UnixFileMode.None);

        var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);
        var updated = await fx.Service.UpdateAsync(fx.Game, fx.Files);

        foreach (var result in new[] { installed, updated })
        {
            Assert.True(result.Success, result.Message);
            Assert.Contains(" · " + RenoDxDeploymentService.EngineIniKeysNotWrittenPrefix + "Access to the path", result.Message);
        }
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(fx.EngineIni));
        Assert.False(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxEngineIni.StateMetadataKey));
        File.SetUnixFileMode(fx.EngineIni, Open);
        Assert.Equal(Before, File.ReadAllText(fx.EngineIni));
    }

    // The write itself fails (a config folder RHI cannot write to) and nothing changed: RenoDX installs, says why, owns nothing.
    [Fact]
    public async Task ConfigFolderNotWritable_RenoDxStillInstalls_WithoutTheKey_AndSaysWhy()
    {
        var fx = Fixture("folder-read-only");
        var folder = Path.GetDirectoryName(fx.EngineIni)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);

            Assert.True(installed.Success, installed.Message);
            Assert.Contains(" · " + RenoDxDeploymentService.EngineIniKeysNotWrittenPrefix, installed.Message);
            Assert.False(File.Exists(fx.EngineIni));
            Assert.False(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxEngineIni.StateMetadataKey));
        }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    // Only a failure that changed nothing is skipped: here RHI's line already came out of the old Engine.ini (the game's
    // config moved) when the new one could not be written, so the whole update is undone and the old file keeps RHI's line.
    [Fact]
    public async Task EngineIniMoved_NewOneCannotBeWritten_AfterTheOldOneChanged_TheUpdateIsUndoneWhole()
    {
        var fx = Fixture("moved-then-failed");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        var oldBytes = File.ReadAllBytes(fx.EngineIni);
        var oldMode = File.GetUnixFileMode(fx.EngineIni);
        var recordBefore = RenoDxJournalTests.Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));
        var moved = Path.Combine(fx.Game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", "MovedProject", "Saved", "Config", "Windows");
        Directory.CreateDirectory(moved);
        fx.Game.EngineIniProjectOverride = "MovedProject";
        // The new folder turns read-only after the up-front check (w8 follow-up C5 refuses a read-only folder before the
        // operation starts): the write itself fails once the old file already changed.
        using var operation = new OperationCancellation();
        OperationCancellation.WritePhaseObserverForTesting = _ => File.SetUnixFileMode(moved, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var updated = await fx.Service.UpdateAsync(fx.Game, fx.Files, cancellationToken: operation.Token);
            OperationCancellation.WritePhaseObserverForTesting = null;

            Assert.False(updated.Success, updated.Message);
            Assert.Equal(oldBytes, File.ReadAllBytes(fx.EngineIni));
            Assert.Equal(oldMode, File.GetUnixFileMode(fx.EngineIni));
            Assert.Equal(recordBefore, RenoDxJournalTests.Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)));
        }
        finally
        {
            OperationCancellation.WritePhaseObserverForTesting = null;
            File.SetUnixFileMode(moved, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Review 3: removal and the ⚙ Off give the file back byte for byte — a header or blank line the user had stays, and the
    // file's line endings, encoding, byte order mark and missing final newline are kept.
    public static TheoryData<string> UserFiles => new()
    {
        "header-already-there", "crlf", "no-final-newline", "trailing-blank-lines", "utf8-bom", "utf16-crlf", "empty-header-last-crlf", "empty",
        "lone-cr-last", "lone-cr-only" // w8 follow-up C6
    };

    private static byte[] UserFile(string name) => name switch
    {
        "header-already-there" => Encoding.UTF8.GetBytes("[/Script/Engine.RendererSettings]\n\n[SystemSettings]\nUserKeep=42\n"),
        "crlf" => Encoding.UTF8.GetBytes("[SystemSettings]\r\nUserKeep=42\r\n"),
        "no-final-newline" => Encoding.UTF8.GetBytes("[SystemSettings]\nUserKeep=42"),
        "trailing-blank-lines" => Encoding.UTF8.GetBytes("[SystemSettings]\nUserKeep=42\n\n\n"),
        "utf8-bom" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("[SystemSettings]\nUserKeep=42\n")],
        "utf16-crlf" => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[SystemSettings]\r\nUserKeep=Ação\r\n")],
        "empty-header-last-crlf" => Encoding.UTF8.GetBytes("[SystemSettings]\r\nUserKeep=42\r\n[/Script/Engine.RendererSettings]\r\n"),
        "lone-cr-last" => Encoding.UTF8.GetBytes("[SystemSettings]\nUserKeep=42\r"),
        "lone-cr-only" => Encoding.UTF8.GetBytes("[A]\rX=1\r"),
        _ => []
    };

    [Theory]
    [MemberData(nameof(UserFiles))]
    public async Task RemovalAndGearOff_GiveTheFileBackByteForByte(string name)
    {
        var fx = Fixture("bytes-" + name);
        var before = UserFile(name);
        File.WriteAllBytes(fx.EngineIni, before);
        File.SetUnixFileMode(fx.EngineIni, Open);

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        Assert.Single(RenoDxEngineIni.Read(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX))!.Entries);
        Assert.True((await fx.Service.SetEngineIniLutAsync(fx.Game, on: false)).Success);
        Assert.Equal(before, File.ReadAllBytes(fx.EngineIni));
        Assert.True((await fx.Service.SetEngineIniLutAsync(fx.Game, on: true)).Success);
        Assert.True(fx.Service.EngineIniLutIsOn(fx.Game));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);

        Assert.Equal(before, File.ReadAllBytes(fx.EngineIni));
        Assert.Equal(Open, File.GetUnixFileMode(fx.EngineIni));
    }

    [Fact]
    public async Task CrlfFile_RhisLineTakesTheFilesOwnLineEnding()
    {
        var fx = Fixture("crlf-ending");
        File.WriteAllText(fx.EngineIni, "[SystemSettings]\r\nUserKeep=42");

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);

        Assert.Equal("[SystemSettings]\r\nUserKeep=42\r\n\r\n[/Script/Engine.RendererSettings]\r\n" + Line, File.ReadAllText(fx.EngineIni));
    }

    // Review 4: an Engine.ini RHI cannot read never throws from the ⚙ (it reads as Off) and hides the row, unless RHI owns the
    // key there (then Off can still be asked for).
    [Fact]
    public async Task GearRow_UnreadableEngineIni_ReadsAsOff_AndIsHidden_UnlessRhiOwnsTheKey()
    {
        var fx = Fixture("gear-unreadable");
        File.WriteAllText(fx.EngineIni, Before + "[/Script/Engine.RendererSettings]\n" + Line + "\n"); // the user's own line
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        Assert.True(fx.Service.EngineIniLutIsOn(fx.Game));
        File.SetUnixFileMode(fx.EngineIni, UnixFileMode.None);

        Assert.False(fx.Service.EngineIniLutIsOn(fx.Game));
        Assert.False(fx.Service.OffersEngineIniLut(fx.Game));

        var owned = Fixture("gear-unreadable-owned");
        File.WriteAllText(owned.EngineIni, Before);
        Assert.True((await owned.Service.InstallAsync(owned.Game, owned.Files)).Success);
        File.SetUnixFileMode(owned.EngineIni, UnixFileMode.None);
        Assert.True(owned.Service.OffersEngineIniLut(owned.Game));
        Assert.True(owned.Service.EngineIniLutIsOn(owned.Game));
    }

    // ── w8 follow-up C4, C5, C7 ────────────────────────────────────────────────────────────────────────────────

    // C4: an Engine.ini that stopped being text (a Latin-1 byte) after RHI's line went in: Off says what to do and changes
    // nothing; removal finishes RenoDX, leaves the file's bytes as they are, names RHI's line and the file, and takes RHI's lock off.
    [Fact]
    public async Task NonTextEngineIni_OffSaysWhatToDo_RemovalFinishes_LeavesTheLine_AndTakesRhisLockOff()
    {
        var fx = Fixture("not-text");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));
        File.SetUnixFileMode(fx.EngineIni, Open);
        using (var stream = new FileStream(fx.EngineIni, FileMode.Append)) stream.Write([(byte)';', 0xE9, (byte)'\n']);
        File.SetUnixFileMode(fx.EngineIni, Locked);
        var bytes = File.ReadAllBytes(fx.EngineIni);

        var off = await fx.Service.SetEngineIniLutAsync(fx.Game, on: false);

        Assert.False(off.Success, off.Message);
        Assert.Equal(RenoDxDeploymentService.EngineIniNotTextMessage + RenoDxDeploymentService.EngineIniNotTextOffHint + Line, off.Message);
        Assert.Equal(bytes, File.ReadAllBytes(fx.EngineIni));
        Assert.DoesNotContain("Save it as", UiLocalizationService.PresentFor("pt-BR", off.Message));

        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.EndsWith(" · " + RenoDxDeploymentService.EngineIniLinesLeftPrefix + fx.EngineIni + RenoDxDeploymentService.EngineIniLinesLeftLinesInfix
                        + Line + RenoDxDeploymentService.EngineIniLinesLeftReasonInfix + RenoDxDeploymentService.EngineIniNotTextMessage, removed.Message);
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));
        Assert.Equal(bytes, File.ReadAllBytes(fx.EngineIni));
        Assert.Equal(Open, File.GetUnixFileMode(fx.EngineIni)); // RHI's lock comes off: the game can save its settings again
        Assert.DoesNotContain("stay in Engine.ini", UiLocalizationService.PresentFor("pt-BR", removed.Message));
    }

    // C5: an existing Engine.ini in a config folder RHI cannot write to: install and update go on without the key and say
    // why (the journal's copy and every write need that folder), and the file is left as it was.
    [Fact]
    public async Task ExistingEngineIni_InAReadOnlyFolder_InstallAndUpdateGoOn_WithoutTheKey_AndSayWhy()
    {
        var fx = Fixture("existing-folder-read-only");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);
        var folder = Path.GetDirectoryName(fx.EngineIni)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var installed = await fx.Service.InstallAsync(fx.Game, fx.Files);
            var updated = await fx.Service.UpdateAsync(fx.Game, fx.Files);

            foreach (var result in new[] { installed, updated })
            {
                Assert.True(result.Success, result.Message);
                Assert.Contains(" · " + RenoDxDeploymentService.EngineIniKeysNotWrittenPrefix + RenoDxDeploymentService.EngineIniFolderNotWritablePrefix + folder,
                    result.Message);
                Assert.DoesNotContain("cannot write to the folder", UiLocalizationService.PresentFor("pt-BR", result.Message));
            }
            Assert.Equal(Before, File.ReadAllText(fx.EngineIni));
            Assert.Equal(Open, File.GetUnixFileMode(fx.EngineIni));
            Assert.Single(Directory.GetFileSystemEntries(folder));
            Assert.False(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxEngineIni.StateMetadataKey));
        }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    // C7 (i): the game's config moved and the OLD Engine.ini (where RHI's line is) cannot be read: the update goes on without
    // the key step, says why, and RHI's line stays owned in the old file.
    [Fact]
    public async Task EngineIniMoved_OldOneUnreadable_UpdateGoesOn_WithoutTheKeyStep_AndSaysWhy()
    {
        var fx = Fixture("moved-old-unreadable");
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        var moved = Path.Combine(fx.Game.PrefixPath!, "drive_c", "users", "steamuser", "AppData", "Local", "MovedProject", "Saved", "Config", "Windows");
        Directory.CreateDirectory(moved);
        fx.Game.EngineIniProjectOverride = "MovedProject";
        File.SetUnixFileMode(fx.EngineIni, UnixFileMode.None);

        var updated = await fx.Service.UpdateAsync(fx.Game, fx.Files);

        Assert.True(updated.Success, updated.Message);
        Assert.Contains(" · " + RenoDxDeploymentService.EngineIniKeysNotWrittenPrefix + "Access to the path", updated.Message);
        Assert.False(File.Exists(Path.Combine(moved, "Engine.ini")));
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(fx.EngineIni));
        var state = RenoDxEngineIni.Read(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX))!;
        Assert.Equal(fx.EngineIni, state.IniPath);
        Assert.Single(state.Entries);
    }

    // C7 (iii): a UTF-8 byte order mark before the section RHI writes to: the key goes into that section (no second one),
    // and the mark stays.
    [Fact]
    public async Task Utf8BomBeforeTheRendererSection_TheKeyGoesIntoIt_NoSecondSection()
    {
        var fx = Fixture("bom-renderer-first");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var before = bom.Concat(Encoding.UTF8.GetBytes("[/Script/Engine.RendererSettings]\nUserKeep=42\n")).ToArray();
        File.WriteAllBytes(fx.EngineIni, before);

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);

        Assert.Equal(bom.Concat(Encoding.UTF8.GetBytes("[/Script/Engine.RendererSettings]\nUserKeep=42\n" + Line + "\n")).ToArray(),
            File.ReadAllBytes(fx.EngineIni));
        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(before, File.ReadAllBytes(fx.EngineIni));
    }

    private static string ProjectFolder(LutFixture fx)
        => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(fx.EngineIni)!)!)!)!;

    private sealed class Backend(RenoDxDeploymentService service) : ILinuxManagedUpdateBackend
    {
        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => service.CheckUpdateAsync(game, indexedFiles, cancellationToken);

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => service.UpdateAsync(game, indexedFiles, cancellationToken);
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record LutFixture(DetectedGame Game, string Root, string EngineIni, RenoDxJournalTests.JournalParts Journal,
        LinuxDeploymentRecordStore Records, RenoDxDeploymentService Service, string[] Files, Handler Remote)
    {
        public string Dropped()
        {
            var path = Path.Combine(Root, "Downloads", "renodx-lutgame-dropped.addon64");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PeArchitectureDetectorTests.WritePe(path, 0x8664, 96 * 1024);
            return path;
        }
    }

    private LutFixture Fixture(string name, Action<DetectedGame>? change = null)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "LutProject", "Binaries", "Win64")).FullName;
        var prefix = Path.Combine(root, "pfx");
        var engineIni = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "LutProject", "Saved", "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(engineIni)!);
        var exe = Path.Combine(bin, "LutProject-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var game = new DetectedGame
        {
            Name = "Lut Game", InstallPath = Path.Combine(root, "Game"), BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", Source = "Steam", SteamAppId = 2051, EngineHint = "Unreal Engine 5", EngineIniProjectOverride = "LutProject"
        };
        change?.Invoke(game);
        var journal = RenoDxJournalTests.JournalParts.Create(Path.Combine(root, "state"));
        var remote = new Handler();
        var http = new HttpClient(remote);
        var manifest = new UpstreamRhiManifestService(http, Handler.ManifestUrl);
        var service = new RenoDxDeploymentService(http, journal.Records, new RenoDxCatalogService(http, manifest), Path.Combine(root, "state", "cache"), manifest,
            journal: journal.Journal);
        return new LutFixture(game, root, engineIni, journal, journal.Records, service, [exe, Path.Combine(bin, "ReShade64.dll")], remote);
    }

    internal sealed class Handler : HttpMessageHandler
    {
        public const string ManifestUrl = "https://github.com/rhi-tests/renodx-lut/raw/main/manifest.json";
        private int _build;

        /// <summary>A RenoDX snapshot rebuilt under the same URL (new bytes, new ETag): Update All finds an update.</summary>
        public void Rebuild() => _build++;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri;
            if (url == ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        { "version": 3, "snapshotOverrides": { "Lut Game": "https://clshortfuse.github.io/renodx/renodx-lutgame.addon64" } }
                        """, Encoding.UTF8, "application/json")
                });
            if (url == "https://clshortfuse.github.io/renodx/renodx-lutgame.addon64")
            {
                var bytes = new byte[120 * 1024];
                bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
                bytes[512] = (byte)_build;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"lut-{_build}\"");
                return Task.FromResult(response);
            }
            if (url == UpstreamEngineIniFileManager.BaseUrl + "lut-custom.ini")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[SystemSettings]\nr.Custom=1\n") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
