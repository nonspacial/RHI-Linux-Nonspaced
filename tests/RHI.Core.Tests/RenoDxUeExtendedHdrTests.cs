using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #218: a UE-Extended RenoDX gets upstream's settings — the <c>[renodx]</c> native-HDR keys in ReShade.ini (missing ones
/// only; <c>Set_Path</c> from the UE-Extended database's <c>Method</c>, else Unreal Engine 4 = SDR path; the database's
/// upgrade key forced) and, where the database says <c>Method: ini</c> (else the manifest's <c>hdr</c>, else every game but
/// UE4), Engine.ini's HDR keys with the ⚙ HDR switch — owned per key on RenoDX's record, inside its journaled operation:
/// removal, Off and switching UE-Extended off take out only RHI's keys.
/// </summary>
public sealed partial class RenoDxUeExtendedHdrTests : IDisposable
{
    private const string ReShadeIni = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n";
    private const string EngineIniBefore = "[SystemSettings]\nUserKeep=42\n";
    private static readonly string[] HdrLines =
        ["r.AllowHDR=1", "r.HDR.EnableHDROutput=1", "r.HDR.Display.OutputDevice=3", "r.HDR.Display.ColorGamut=2", "r.HDR.UI.CompositeMode=1"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-uee-hdr-" + Guid.NewGuid().ToString("N"));

    public RenoDxUeExtendedHdrTests() => Directory.CreateDirectory(_root);

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
    public async Task UeExtended_Ue5_NoDatabaseEntry_WritesTheHdrAndNativeKeys_RemovalPutsBothFilesBackByteForByte()
    {
        var fx = Fixture("ue5");

        var installed = await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on");

        Assert.True(installed.Success, installed.Message);
        var engine = File.ReadAllText(fx.EngineIni);
        Assert.All(HdrLines, line => Assert.Contains(line, engine));
        Assert.Contains("r.LUT.UpdateEveryFrame=1", engine); // #217's line, its own group
        var section = RenoDxIniOverrideManager.ReadRenoDxSection(fx.Bin)!;
        Assert.Equal("0", section["Set_Path"]); // UE5: the HDR path
        Assert.Equal("2", section["SettingsMode"]);
        Assert.Equal("", section["Upgrade_UseSCRGB"]);
        Assert.True(fx.Service.OffersEngineIniHdr(fx.Game));
        Assert.True(fx.Service.EngineIniHdrIsOn(fx.Game));

        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(EngineIniBefore, File.ReadAllText(fx.EngineIni));
        Assert.Equal(ReShadeIni, File.ReadAllText(Path.Combine(fx.Bin, "ReShade.ini")));
    }

    public static TheoryData<string, string?, bool, string> Rules => new()
    {
        // engine, database Method (null: no entry), HDR keys written, Set_Path
        { "Unreal Engine 4", null, false, "1" },            // UE4 without an entry: SDR path, no Engine.ini HDR
        { "Unreal Engine 4", "ini", true, "0" },            // the entry wins over the engine rule
        { "Unreal Engine 5", "native", false, "0" },        // native HDR: no Engine.ini keys
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task TheDatabaseEntryDecides_ElseTheEngine(string engine, string? method, bool hdrKeys, string setPath)
    {
        var fx = Fixture("rule-" + engine.Replace(' ', '-') + "-" + method, engine: engine,
            database: method is null ? "[]" : $$"""[{ "Name": "Uee Game", "Status": "Done", "Method": "{{method}}" }]""");

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);

        Assert.Equal(hdrKeys, File.ReadAllText(fx.EngineIni).Contains("r.HDR.EnableHDROutput=1"));
        Assert.Equal(hdrKeys, fx.Service.OffersEngineIniHdr(fx.Game)); // the ⚙ row shows only where the keys apply
        Assert.Equal(setPath, RenoDxIniOverrideManager.ReadRenoDxSection(fx.Bin)!["Set_Path"]);
    }

    [Fact]
    public async Task UpgradeMethod_ForcesItsKey_OverAValueThere_AndLeavesTheUsersOtherKeys()
    {
        var fx = Fixture("upgrade", database: """[{ "Name": "Uee Game", "Method": "upgrade", "Upgrades": "`B8G8R8A8_TYPELESS` `Output Size`" }]""");
        File.WriteAllText(Path.Combine(fx.Bin, "ReShade.ini"), ReShadeIni + "\n[renodx]\nUpgrade_B8G8R8A8_TYPELESS=0\nForceBorderless=0\n");

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);

        var section = RenoDxIniOverrideManager.ReadRenoDxSection(fx.Bin)!;
        Assert.Equal("1", section["Upgrade_B8G8R8A8_TYPELESS"]); // upstream's one forced key
        Assert.Equal("0", section["ForceBorderless"]);           // a value already there is the user's
        Assert.Equal("1", section["Set_Path"]);                  // "upgrade" = the SDR path

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        section = RenoDxIniOverrideManager.ReadRenoDxSection(fx.Bin)!;
        Assert.Equal("0", section["Upgrade_B8G8R8A8_TYPELESS"]); // the value before RHI's write is back
        Assert.Equal("0", section["ForceBorderless"]);
        Assert.False(section.ContainsKey("Set_Path"));
    }

    [Fact]
    public async Task ManifestHdrFalse_NoHdrKeys_AndNoHdrRow()
    {
        var fx = Fixture("manifest-off", change: game => game.UeExtendedHdrByManifest = false);

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);

        Assert.DoesNotContain("r.HDR.EnableHDROutput", File.ReadAllText(fx.EngineIni));
        Assert.False(fx.Service.OffersEngineIniHdr(fx.Game));
    }

    [Fact]
    public async Task GenericUnrealAddon_GetsNoUeExtendedSettings()
    {
        var fx = Fixture("generic");

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);

        Assert.EndsWith("renodx-unrealengine.addon64", fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.SourceUrl);
        Assert.DoesNotContain("r.HDR", File.ReadAllText(fx.EngineIni));
        Assert.Null(RenoDxIniOverrideManager.ReadRenoDxSection(fx.Bin));
        Assert.False(fx.Service.OffersEngineIniHdr(fx.Game));
    }

    [Fact]
    public async Task AnHdrKeyAlreadyThere_IsLeftAlone_AndNotOwned()
    {
        var fx = Fixture("present");
        File.WriteAllText(fx.EngineIni, "[SystemSettings]\nr.AllowHDR=0\n");

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);
        Assert.Contains("r.AllowHDR=0", File.ReadAllText(fx.EngineIni));
        Assert.DoesNotContain("r.AllowHDR=1", File.ReadAllText(fx.EngineIni));
        Assert.DoesNotContain(RenoDxEngineIni.Read(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX))!.Entries, entry => entry.Key == "r.AllowHDR");

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal("[SystemSettings]\nr.AllowHDR=0\n", File.ReadAllText(fx.EngineIni));
    }

    [Fact]
    public async Task GearHdrOff_TakesOnlyTheHdrKeysOut_UpdatesFollowIt_On_PutsThemBack()
    {
        var fx = Fixture("gear");
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);

        var off = await fx.Service.SetEngineIniHdrAsync(fx.Game, on: false);
        Assert.True(off.Success, off.Message);
        Assert.DoesNotContain("r.HDR", File.ReadAllText(fx.EngineIni));
        Assert.Contains("r.LUT.UpdateEveryFrame=1", File.ReadAllText(fx.EngineIni)); // the LUT line is its own setting
        Assert.False(fx.Service.EngineIniHdrIsOn(fx.Game));
        Assert.True((await fx.Service.UpdateAsync(fx.Game, fx.Files)).Success);
        Assert.DoesNotContain("r.HDR", File.ReadAllText(fx.EngineIni)); // the update follows the user's Off

        var on = await fx.Service.SetEngineIniHdrAsync(fx.Game, on: true);
        Assert.True(on.Success, on.Message);
        Assert.All(HdrLines, line => Assert.Contains(line, File.ReadAllText(fx.EngineIni)));
    }

    [Fact]
    public async Task SwitchingUeExtendedOff_TakesItsKeysOut_TheLutLineStays()
    {
        var fx = Fixture("switch-off");
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);

        var updated = await fx.Service.UpdateAsync(fx.Game, fx.Files, ueExtendedChoice: "off");

        Assert.True(updated.Success, updated.Message);
        Assert.EndsWith("renodx-unrealengine.addon64", fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.SourceUrl);
        Assert.Equal(ReShadeIni, File.ReadAllText(Path.Combine(fx.Bin, "ReShade.ini")));
        Assert.DoesNotContain("r.HDR", File.ReadAllText(fx.EngineIni));
        Assert.Contains("r.LUT.UpdateEveryFrame=1", File.ReadAllText(fx.EngineIni));
        Assert.False(fx.Service.OffersEngineIniHdr(fx.Game));
    }

    // A dropped add-on replacing RHI's UE-Extended: no HDR row any more (it is not UE-Extended), and the HDR keys RHI added
    // stay owned, so removing RenoDX still takes them out.
    [Fact]
    public async Task ADroppedAddonReplacingUeExtended_HidesTheHdrRow_RemovalStillTakesTheKeysOut()
    {
        var fx = Fixture("drop");
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);
        var dropped = Path.Combine(fx.Root, "Downloads", "renodx-ueegame.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(dropped)!);
        PeArchitectureDetectorTests.WritePe(dropped, 0x8664, 96 * 1024);

        Assert.True((await fx.Service.InstallLocalAddonAsync(fx.Game, dropped)).Success);
        Assert.False(fx.Service.OffersEngineIniHdr(fx.Game));
        Assert.Contains("r.HDR.EnableHDROutput=1", File.ReadAllText(fx.EngineIni));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(EngineIniBefore, File.ReadAllText(fx.EngineIni));
        Assert.Equal(ReShadeIni, File.ReadAllText(Path.Combine(fx.Bin, "ReShade.ini")));
    }

    // Correction round, review 1: the project's config folder is not there yet (the game was never launched): UE-Extended
    // installs without its Engine.ini keys, creates no folder, and neither ⚙ row is offered.
    [Fact]
    public async Task UeExtended_ConfigFolderNotThereYet_InstallsWithoutTheEngineIniKeys_AndHidesBothRows()
    {
        var fx = Fixture("no-config-folder");
        var project = Path.Combine(fx.Root, "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "UeeProject");
        Directory.Delete(project, recursive: true);

        var installed = await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on");

        Assert.True(installed.Success, installed.Message);
        Assert.False(Directory.Exists(project));
        Assert.False(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX)!.Metadata.ContainsKey(RenoDxEngineIni.StateMetadataKey));
        Assert.False(fx.Service.OffersEngineIniHdr(fx.Game));
        Assert.False(fx.Service.OffersEngineIniLut(fx.Game));
    }

    // C7 (ii): the HDR row hides on an Engine.ini RHI cannot read, unless RHI owns HDR keys there (Off can still be asked for).
    [Fact]
    public async Task HdrRow_UnreadableEngineIni_IsHidden_UnlessRhiOwnsTheHdrKeys()
    {
        var fx = Fixture("hdr-row-unreadable");
        File.WriteAllText(fx.EngineIni, EngineIniBefore.Replace("UserKeep=42\n", "UserKeep=42\n" + string.Join("\n", HdrLines) + "\n")); // the user's own
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);
        Assert.True(fx.Service.OffersEngineIniHdr(fx.Game));
        File.SetUnixFileMode(fx.EngineIni, UnixFileMode.None);

        Assert.False(fx.Service.OffersEngineIniHdr(fx.Game));

        var owned = Fixture("hdr-row-unreadable-owned");
        Assert.True((await owned.Service.InstallAsync(owned.Game, owned.Files, ueExtendedChoice: "on")).Success);
        File.SetUnixFileMode(owned.EngineIni, UnixFileMode.None);
        Assert.True(owned.Service.OffersEngineIniHdr(owned.Game));
    }

    // Review 3: an empty [SystemSettings] the user had, CRLF endings and no final newline all come back byte for byte.
    [Fact]
    public async Task HdrKeys_IntoAnEmptySectionTheUserHad_RemovalAndOff_GiveTheFileBackByteForByte()
    {
        var fx = Fixture("bytes");
        var before = Encoding.UTF8.GetBytes("[SystemSettings]\r\n\r\n[Other]\r\nA=1");
        File.WriteAllBytes(fx.EngineIni, before);

        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);
        Assert.Contains("[SystemSettings]\r\nr.AllowHDR=1\r\n", File.ReadAllText(fx.EngineIni));
        Assert.True((await fx.Service.SetEngineIniHdrAsync(fx.Game, on: false)).Success);
        Assert.True((await fx.Service.SetEngineIniLutAsync(fx.Game, on: false)).Success);
        Assert.Equal(before, File.ReadAllBytes(fx.EngineIni));
        Assert.True((await fx.Service.SetEngineIniHdrAsync(fx.Game, on: true)).Success);

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);

        Assert.Equal(before, File.ReadAllBytes(fx.EngineIni));
    }

    public static TheoryData<string, string> CommitPoints() => new()
    {
        { "ledger-blocked", "install" }, { "crash", "install" }, { "ledger-blocked", "gear-off" }, { "crash", "gear-off" }
    };

    [Theory]
    [MemberData(nameof(CommitPoints))]
    public async Task FailsOrDies_AtTheCommitPoint_BothFilesAreBack(string failure, string operation)
    {
        var fx = Fixture("commit-" + failure + "-" + operation);
        if (operation == "gear-off") Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")).Success);
        var dirs = new[] { Path.Combine(fx.Root, "Game"), Path.Combine(fx.Root, "pfx") }; // not RHI's download cache
        var before = CancelAtWritePhase.Snapshot(dirs);
        var engineMode = File.GetUnixFileMode(fx.EngineIni);
        var recordBefore = RenoDxJournalTests.Describe(fx.Records.Find(fx.Game, ManagedComponentKind.RenoDX));
        Func<Task<ComponentOperationResult>> run = operation == "install"
            ? () => fx.Service.InstallAsync(fx.Game, fx.Files, ueExtendedChoice: "on")
            : () => fx.Service.SetEngineIniHdrAsync(fx.Game, on: false);

        await RenoDxJournalTests.FailAsync(fx.Journal, failure, 1, JournalRecoveryOutcome.RolledBack, run, () =>
        {
            CancelAtWritePhase.AssertUnchanged(before, dirs);
            Assert.Equal(engineMode, File.GetUnixFileMode(fx.EngineIni));
            Assert.Equal(recordBefore, RenoDxJournalTests.Describe(new LinuxDeploymentRecordStore(fx.Journal.LedgerPath).Find(fx.Game, ManagedComponentKind.RenoDX)));
        });
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record UeeFixture(DetectedGame Game, string Root, string Bin, string EngineIni, RenoDxJournalTests.JournalParts Journal,
        LinuxDeploymentRecordStore Records, RenoDxDeploymentService Service, string[] Files);

    private UeeFixture Fixture(string name, string engine = "Unreal Engine 5", string database = "[]", Action<DetectedGame>? change = null)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "UeeProject", "Binaries", "Win64")).FullName;
        var prefix = Path.Combine(root, "pfx");
        var engineIni = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "UeeProject", "Saved", "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(engineIni)!);
        File.WriteAllText(engineIni, EngineIniBefore);
        var exe = Path.Combine(bin, "UeeProject-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), ReShadeIni);
        var game = new DetectedGame
        {
            Name = "Uee Game", InstallPath = Path.Combine(root, "Game"), BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", Source = "Steam", SteamAppId = 2052, EngineHint = engine, EngineIniProjectOverride = "UeeProject"
        };
        change?.Invoke(game);
        var journal = RenoDxJournalTests.JournalParts.Create(Path.Combine(root, "state"));
        var http = new HttpClient(new Handler(database));
        var service = new RenoDxDeploymentService(http, journal.Records, new RenoDxCatalogService(http), Path.Combine(root, "state", "cache"),
            journal: journal.Journal)
        {
            UnrealDatabase = new RenoDxUnrealDatabaseService(http, Path.Combine(root, "state", "RenoDXdb-unreal.json"))
        };
        return new UeeFixture(game, root, bin, engineIni, journal, journal.Records, service, [exe, Path.Combine(bin, "ReShade64.dll")]);
    }

    private sealed class Handler(string database) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri;
            if (url == RenoDxUnrealDatabaseService.LiveUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(database, Encoding.UTF8, "application/json") });
            if (url == RenoDxCatalogService.WikiUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<table><tr><th>Name</th><th>Links</th><th>Status</th></tr></table>") });
            if (url == RenoDxDeploymentService.DefaultUeExtendedUrl || url == RenoDxCatalogService.GenericUnrealUrl)
            {
                var bytes = new byte[120 * 1024];
                bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                Encoding.ASCII.GetBytes(url).CopyTo(bytes, 256); // each add-on its own bytes
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
