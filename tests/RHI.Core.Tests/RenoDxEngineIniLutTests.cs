using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #217: RenoDX writes <c>r.LUT.UpdateEveryFrame=1</c> under <c>[/Script/Engine.RendererSettings]</c> in an Unreal game's
/// Engine.ini at install, update and local drop (upstream <c>ApplyEngineIniLutSetting</c>), owned per key on RenoDX's
/// record: a key the file already has is the user's (never changed, never owned); removal and the ⚙ Off take out only
/// RHI's line, and put back the lock state the file had (upstream makes Engine.ini read-only once its key is in). Not for a
/// non-Unreal game, a game the manifest marks <c>lut: false</c>, or one with a custom Engine.ini file.
/// </summary>
public sealed class RenoDxEngineIniLutTests : IDisposable
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

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record LutFixture(DetectedGame Game, string Root, string EngineIni, RenoDxJournalTests.JournalParts Journal,
        LinuxDeploymentRecordStore Records, RenoDxDeploymentService Service, string[] Files)
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
        var http = new HttpClient(new Handler());
        var manifest = new UpstreamRhiManifestService(http, Handler.ManifestUrl);
        var service = new RenoDxDeploymentService(http, journal.Records, new RenoDxCatalogService(http, manifest), Path.Combine(root, "state", "cache"), manifest,
            journal: journal.Journal);
        return new LutFixture(game, root, engineIni, journal, journal.Records, service, [exe, Path.Combine(bin, "ReShade64.dll")]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public const string ManifestUrl = "https://github.com/rhi-tests/renodx-lut/raw/main/manifest.json";

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
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            if (url == UpstreamEngineIniFileManager.BaseUrl + "lut-custom.ini")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[SystemSettings]\nr.Custom=1\n") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
