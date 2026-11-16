using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #277: HSF installed, updated and removed in a game folder — the real 0.9.8.8 layout (entry list pinned), GitHub's
/// digest checked, the D3D12 route set before the first launch, the player's HSF settings carried over on update,
/// iMMERSE Launchpad placed through its step and kept first in the preset, nothing of the user's overwritten or deleted.
/// </summary>
public sealed class HsfDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-hsf-" + Guid.NewGuid().ToString("N"));

    public HsfDeploymentServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Install_PlacesThePackage_SetsTheDx12RouteBeforeTheFirstLaunch_AndRemoveTakesOnlyRhisFilesAndHsfsLogs()
    {
        var fx = Fixture("install", GraphicsApi.DirectX12);
        var install = await fx.Service.InstallAsync(fx.Game);

        Assert.True(install.Success, install.Message);
        Assert.StartsWith(HsfDeploymentService.InstalledPrefix + "0.9.8.8.", install.Message);
        Assert.Contains(HsfDeploymentService.RouteSetNote, install.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "hsf.addon64")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "hsf.addon32"))); // the game's bitness only
        Assert.True(File.Exists(Path.Combine(fx.Bin, "HSF_streamline", "sl.interposer.dll")));
        Assert.True(File.Exists(Path.Combine(fx.Bin, "HSF_streamline", "LICENSE.txt")));
        Assert.True(File.Exists(Path.Combine(fx.Bin, "HSF_streamline", "licenses", "Streamline-license.txt")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "LICENSE.txt"))); // never beside the game's own files
        Assert.True(File.Exists(Path.Combine(fx.Bin, "reshade-shaders", "Shaders", "HSF.fx")));
        // w14/nr-2: Launchpad in the shared tree GITC Uplift uses, found through ReShade.ini's search paths.
        Assert.True(File.Exists(Path.Combine(ImmerseLaunchpadService.TreeRoot(fx.Bin), "Shaders", "MartysMods_LAUNCHPAD.fx")));
        Assert.Contains(ImmerseLaunchpadService.EffectSearchPath, File.ReadAllText(Path.Combine(fx.Bin, "ReShade.ini")));
        var ini = File.ReadAllText(Path.Combine(fx.Bin, "HSF.ini"));
        Assert.Contains("SlBringIn=2", ini); // no Streamline of the game's own: HSF's
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.Hsf)!;
        Assert.Equal("0.9.8.8", record.Version);
        Assert.Equal(VerifiedDownload.PublishedDigest, record.Metadata[VerifiedDownload.MetadataIntegrityKey]);
        Assert.Equal((true, "0.9.8.8", true, HsfRoute.BringIn), (fx.Service.Read(fx.Game).Installed, fx.Service.Read(fx.Game).Version,
            fx.Service.Read(fx.Game).LaunchpadOn, fx.Service.Read(fx.Game).Route));

        // HSF runs: its logs beside the add-on and in its folder, camera profiles, a mods folder of the player's.
        File.WriteAllText(Path.Combine(fx.Bin, "HSF_sl_probe.log"), "log");
        File.WriteAllText(Path.Combine(fx.Bin, "HSF_streamline", "helper.log"), "log");
        Directory.CreateDirectory(Path.Combine(fx.Bin, "HSF_profiles"));
        File.WriteAllText(Path.Combine(fx.Bin, "HSF_profiles", "cam.ini"), "mine");
        File.AppendAllText(Path.Combine(fx.Bin, "HSF.ini"), "Multiplier=4\r\n");

        var remove = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Contains(HsfDeploymentService.KeptFilesPrefix, remove.Message);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(fx.Bin, "HSF_profiles", "cam.ini")));
        Assert.False(Directory.Exists(Path.Combine(fx.Bin, "HSF_streamline")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "HSF.ini")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "HSF_sl_probe.log")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "reshade-shaders", "Shaders", "HSF.fx")));
        Assert.False(Directory.Exists(ImmerseLaunchpadService.TreeRoot(fx.Bin)));
        Assert.Equal(fx.IniBefore, File.ReadAllBytes(Path.Combine(fx.Bin, "ReShade.ini"))); // Launchpad's search paths gone with the last user
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.Hsf));
    }

    [Fact]
    public async Task Install_GameWithItsOwnStreamline_TakesTheGamesRoute_And32BitGetsItsAddonAndNoRoute()
    {
        var fx = Fixture("own-sl", GraphicsApi.DirectX12);
        Directory.CreateDirectory(Path.Combine(fx.Bin, "Streamline"));
        File.WriteAllText(Path.Combine(fx.Bin, "Streamline", "sl.interposer.dll"), "game");
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        Assert.Contains("InGameDlssg=2", File.ReadAllText(Path.Combine(fx.Bin, "HSF.ini")));

        var x86 = Fixture("x86", GraphicsApi.DirectX12, machine: 0x014c);
        var install = await x86.Service.InstallAsync(x86.Game);
        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(Path.Combine(x86.Bin, "hsf.addon32")));
        Assert.DoesNotContain("InGameDlssg", File.ReadAllText(Path.Combine(x86.Bin, "HSF.ini")));
        Assert.DoesNotContain("SlBringIn", File.ReadAllText(Path.Combine(x86.Bin, "HSF.ini")));
    }

    public static TheoryData<string> Refusals => new() { "no-reshade", "manual", "vulkan-32", "digest", "user-fx" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Install_Refused_ChangesNothing(string scenario)
    {
        var fx = Fixture("refused-" + scenario, scenario == "vulkan-32" ? GraphicsApi.Vulkan : GraphicsApi.DirectX11,
            machine: scenario == "vulkan-32" ? (ushort)0x014c : (ushort)0x8664, withReShade: scenario != "no-reshade", badDigest: scenario == "digest");
        var expected = scenario switch
        {
            "no-reshade" => HsfDeploymentService.NeedsReShadeMessage,
            "manual" => HsfDeploymentService.ManualInstallPrefix,
            "vulkan-32" => HsfDeploymentService.VulkanNeeds64Message,
            "digest" => HsfDeploymentService.DownloadFailedPrefix,
            _ => HsfDeploymentService.ForeignFilePrefix
        };
        if (scenario == "manual") File.WriteAllText(Path.Combine(fx.Bin, "HSF.addon64"), "by hand");
        if (scenario == "user-fx")
        {
            Directory.CreateDirectory(Path.Combine(fx.Bin, "reshade-shaders", "Shaders"));
            File.WriteAllText(Path.Combine(fx.Bin, "reshade-shaders", "Shaders", "HSF.fx"), "mine");
        }
        var before = CancelAtWritePhase.Snapshot([fx.Bin]);

        var result = await fx.Service.InstallAsync(fx.Game);

        Assert.False(result.Success);
        Assert.StartsWith(expected, result.Message);
        CancelAtWritePhase.AssertUnchanged(before, [fx.Bin]);
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.Hsf));
    }

    [Fact]
    public async Task Update_BringsTheCleanIniWithThePlayersSettings_AndReplacesOnlyRhisFiles()
    {
        var fx = Fixture("update", GraphicsApi.DirectX11);
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        File.AppendAllText(Path.Combine(fx.Bin, "HSF.ini"), "Multiplier=4\r\nDx9ExRun=1\r\n");
        Assert.False((await fx.Service.CheckUpdateAsync(fx.Game)).UpdateAvailable);

        fx.Server.Publish("0.9.9", helperBytes: "helper 0.9.9");
        var check = await fx.Service.CheckUpdateAsync(fx.Game);
        Assert.True(check.UpdateAvailable, check.Message);
        var update = await fx.Service.UpdateAsync(fx.Game);

        Assert.True(update.Success, update.Message);
        Assert.Equal("helper 0.9.9", File.ReadAllText(Path.Combine(fx.Bin, "HSF_streamline", "hsf-dlssg-helper64.exe")));
        var ini = File.ReadAllText(Path.Combine(fx.Bin, "HSF.ini"));
        Assert.Contains("Multiplier=4", ini);
        Assert.DoesNotContain("Dx9ExRun", ini);
        Assert.Equal("0.9.9", fx.Records.Find(fx.Game, ManagedComponentKind.Hsf)!.Version);
        Assert.True(File.Exists(Path.Combine(ImmerseLaunchpadService.TreeRoot(fx.Bin), "Shaders", "MartysMods_LAUNCHPAD.fx"))); // kept
    }

    [Fact]
    public async Task Update_AFileChangedSinceRhiInstalledIt_RefusesBeforeAnyChange()
    {
        var fx = Fixture("update-changed", GraphicsApi.DirectX11);
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        File.WriteAllText(Path.Combine(fx.Bin, "hsf.addon64"), "patched by the player");
        fx.Server.Publish("0.9.9", helperBytes: "x");
        var before = CancelAtWritePhase.Snapshot([fx.Bin]);

        var update = await fx.Service.UpdateAsync(fx.Game);

        Assert.False(update.Success);
        Assert.StartsWith(HsfDeploymentService.ChangedPrefix, update.Message);
        CancelAtWritePhase.AssertUnchanged(before, [fx.Bin]);
    }

    [Fact]
    public async Task Gear_LaunchpadOffAndOn_RouteChoice_AndLaunchpadKeptFirstInThePreset()
    {
        var fx = Fixture("gear", GraphicsApi.DirectX12);
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        var launchpad = Path.Combine(ImmerseLaunchpadService.TreeRoot(fx.Bin), "Shaders", "MartysMods_LAUNCHPAD.fx");

        Assert.True((await fx.Service.SetLaunchpadAsync(fx.Game, false)).Success);
        Assert.False(File.Exists(launchpad));
        Assert.False(fx.Service.Read(fx.Game).LaunchpadOn);
        Assert.True((await fx.Service.SetLaunchpadAsync(fx.Game, true)).Success);
        Assert.True(File.Exists(launchpad));

        Assert.True((await fx.Service.SetRouteAsync(fx.Game, HsfRoute.GameStreamline)).Success);
        Assert.Equal(HsfRoute.GameStreamline, fx.Service.Read(fx.Game).Route);
        Assert.DoesNotContain("SlBringIn", File.ReadAllText(Path.Combine(fx.Bin, "HSF.ini")));
        Assert.True((await fx.Service.SetRouteAsync(fx.Game, HsfRoute.None)).Success);
        Assert.True(fx.Service.Read(fx.Game).RouteOptedOut);

        var preset = Path.Combine(fx.Bin, "ReShadePreset.ini");
        File.WriteAllText(preset, "Techniques=HSF_Motion@HSF.fx," + HsfPackage.LaunchpadToken + "\r\n");
        Assert.True((await fx.Service.KeepLaunchpadFirstAsync(fx.Game)).Success);
        Assert.Equal("Techniques=" + HsfPackage.LaunchpadToken + ",HSF_Motion@HSF.fx\r\n", File.ReadAllText(preset));
    }

    [Fact]
    public async Task Launchpad_AUsersOwnImmerseFile_IsLeftAndNeverRecorded()
    {
        var fx = Fixture("user-immerse", GraphicsApi.DirectX11);
        var users = Path.Combine(fx.Bin, "reshade-shaders", "Shaders", "iMMERSE", "MartysMods_LAUNCHPAD.fx");
        Directory.CreateDirectory(Path.GetDirectoryName(users)!);
        File.WriteAllText(users, "the player's iMMERSE");

        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);

        Assert.Equal("the player's iMMERSE", File.ReadAllText(users));
    }

    // ── w14/nr-2: one iMMERSE Launchpad for HSF and GITC Uplift ──────────────────────────────────────────────────────

    private static LinuxDeploymentRecord UpliftListing(Fx fx)
        => new()
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fx.Game), GameName = fx.Game.Name, Component = ManagedComponentKind.GitcUplift,
            BinaryDirectory = fx.Bin, Version = "2.0.0",
            Files = Directory.EnumerateFiles(ImmerseLaunchpadService.TreeRoot(fx.Bin), "*", SearchOption.AllDirectories)
                .Select(p => new LinuxDeployedFile { Path = p, Sha256 = FileHash.Sha256Hex(p) }).ToList()
        };

    [Fact]
    public async Task SharedLaunchpad_HsfRemoval_LeavesTheFilesAndSearchPathsGitcUpliftStillLists()
    {
        var fx = Fixture("shared", GraphicsApi.DirectX12);
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        fx.Records.Upsert(UpliftListing(fx)); // GITC Uplift installed after HSF shares the same files (ImmerseLaunchpadService.PlaceAsync)
        var launchpad = Path.Combine(ImmerseLaunchpadService.TreeRoot(fx.Bin), "Shaders", "MartysMods_LAUNCHPAD.fx");

        var remove = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.True(File.Exists(launchpad));
        var ini = File.ReadAllText(Path.Combine(fx.Bin, "ReShade.ini"));
        Assert.Contains(ImmerseLaunchpadService.EffectSearchPath, ini);
        Assert.Contains(ImmerseLaunchpadService.TextureSearchPath, ini);
    }

    [Fact]
    public async Task SharedLaunchpad_HsfAfterGitcUplift_ListsTheSameFiles_NeverPlacesASecondCopy()
    {
        var fx = Fixture("shared-after", GraphicsApi.DirectX12);
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        var upliftRecord = UpliftListing(fx);
        fx.Records.Upsert(upliftRecord);
        Assert.True((await fx.Service.SetLaunchpadAsync(fx.Game, false)).Success); // HSF off: Uplift keeps them
        Assert.All(upliftRecord.Files, f => Assert.True(File.Exists(f.Path), f.Path));

        Assert.True((await fx.Service.SetLaunchpadAsync(fx.Game, true)).Success);

        var hsf = fx.Records.Find(fx.Game, ManagedComponentKind.Hsf)!;
        Assert.Equal(upliftRecord.Files.Select(f => f.Path).Order(), hsf.Files.Where(f => ImmerseLaunchpadService.IsLaunchpadFile(f.Path)).Select(f => f.Path).Order());
        Assert.Single(Directory.EnumerateFiles(fx.Bin, "MartysMods_LAUNCHPAD.fx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OlderHsfRecord_ItsOwnLaunchpadCopies_MoveIntoTheSharedTree()
    {
        // An HSF install from before w14/nr-2: Launchpad under reshade-shaders/Shaders/iMMERSE and Textures/iMMERSE, listed in
        // HSF.LaunchpadFiles, no search paths. Shaped here from a current install.
        var fx = Fixture("older", GraphicsApi.DirectX12);
        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        var record = LinuxDeploymentRecordStore.Clone(fx.Records.Find(fx.Game, ManagedComponentKind.Hsf)!);
        var older = new List<string>();
        foreach (var file in record.Files.Where(f => ImmerseLaunchpadService.IsLaunchpadFile(f.Path)))
        {
            var relative = Path.GetRelativePath(ImmerseLaunchpadService.TreeRoot(fx.Bin), file.Path);
            var top = relative.Split(Path.DirectorySeparatorChar)[0];
            var target = Path.Combine(fx.Bin, "reshade-shaders", top, "iMMERSE", relative[(top.Length + 1)..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file.Path, target);
            file.Path = target;
            older.Add(target);
        }
        Directory.Delete(ImmerseLaunchpadService.TreeRoot(fx.Bin), recursive: true);
        record.Metadata[HsfDeploymentService.MetaLaunchpadFiles] = string.Join("\n", older);
        fx.Records.Upsert(record);
        File.WriteAllBytes(Path.Combine(fx.Bin, "ReShade.ini"), fx.IniBefore);

        Assert.Equal(HsfDeploymentService.LaunchpadMigration.Moved, await fx.Service.MigrateLaunchpadAsync(fx.Game));

        Assert.All(older, path => Assert.False(File.Exists(path), path));
        Assert.False(Directory.Exists(Path.Combine(fx.Bin, "reshade-shaders", "Shaders", "iMMERSE")));
        var migrated = fx.Records.Find(fx.Game, ManagedComponentKind.Hsf)!;
        Assert.False(migrated.Metadata.ContainsKey(HsfDeploymentService.MetaLaunchpadFiles));
        Assert.Equal(3, migrated.Files.Count(f => ImmerseLaunchpadService.IsLaunchpadFile(f.Path) && File.Exists(f.Path)));
        Assert.Contains(ImmerseLaunchpadService.EffectSearchPath, File.ReadAllText(Path.Combine(fx.Bin, "ReShade.ini")));
        Assert.Equal(HsfDeploymentService.LaunchpadMigration.NotNeeded, await fx.Service.MigrateLaunchpadAsync(fx.Game));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(fx.IniBefore, File.ReadAllBytes(Path.Combine(fx.Bin, "ReShade.ini")));
        Assert.False(Directory.Exists(ImmerseLaunchpadService.TreeRoot(fx.Bin)));
    }

    [Fact]
    public async Task OlderHsfRecord_ACopyChangedSinceRhiPlacedIt_StaysWhereItIs()
    {
        var fx = Fixture("older-changed", GraphicsApi.DirectX12);
        Assert.True((await fx.Service.InstallAsync(fx.Game, launchpadOn: false)).Success);
        var record = LinuxDeploymentRecordStore.Clone(fx.Records.Find(fx.Game, ManagedComponentKind.Hsf)!);
        var olderFx = Path.Combine(fx.Bin, "reshade-shaders", "Shaders", "iMMERSE", "MartysMods_LAUNCHPAD.fx");
        Directory.CreateDirectory(Path.GetDirectoryName(olderFx)!);
        File.WriteAllText(olderFx, "launchpad");
        record.Files.Add(new LinuxDeployedFile { Path = olderFx, Sha256 = FileHash.Sha256Hex(olderFx) });
        record.Metadata[HsfDeploymentService.MetaLaunchpadFiles] = olderFx;
        record.Metadata[HsfDeploymentService.MetaLaunchpad] = "on";
        fx.Records.Upsert(record);
        File.WriteAllText(olderFx, "edited by the player");

        Assert.Equal(HsfDeploymentService.LaunchpadMigration.Kept, await fx.Service.MigrateLaunchpadAsync(fx.Game));

        Assert.Equal("edited by the player", File.ReadAllText(olderFx));
        Assert.False(Directory.Exists(ImmerseLaunchpadService.TreeRoot(fx.Bin)));
    }

    [Fact]
    public void VulkanWarning_NamesADirectXReShadeInTheGameFolder()
    {
        var fx = Fixture("vk", GraphicsApi.Vulkan, withReShade: false);
        Assert.Null(HsfDeploymentService.VulkanDxgiWarning(fx.Game));
        var dxgi = Path.Combine(fx.Bin, "dxgi.dll");
        PeArchitectureDetectorTests.WritePe(dxgi, 0x8664, 4096);
        File.AppendAllText(dxgi, "ReShade crosire reshade.me");
        Assert.StartsWith(HsfDeploymentService.VulkanDxgiWarningPrefix, HsfDeploymentService.VulkanDxgiWarning(fx.Game));
    }

    // Upstream: installing HSF offers Universal MFG Unlocker on RTX 20/30/40 (the card is detected), never on RTX 50 or an
    // unknown card, and not once it is installed. The row shows the anti-cheat warning before an install.
    [Theory]
    [InlineData(RHI.Core.Services.RtxGeneration.Rtx20, true)]
    [InlineData(RHI.Core.Services.RtxGeneration.Rtx30, true)]
    [InlineData(RHI.Core.Services.RtxGeneration.Rtx40, true)]
    [InlineData(RHI.Core.Services.RtxGeneration.Rtx50, false)]
    [InlineData(RHI.Core.Services.RtxGeneration.Unknown, false)]
    public async Task Row_OffersUniversalMfgOnRtx20To40Only(RHI.Core.Services.RtxGeneration card, bool offered)
    {
        var fx = Fixture("offer-" + card, GraphicsApi.DirectX12);
        var managed = new LinuxManagedComponentService(fx.Records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!)
        { Hsf = fx.Service, GpuGeneration = () => card };
        var presenter = new RHI.Desktop.ViewModels.HsfPresenter(managed);

        var before = presenter.StateFor(fx.Game, idle: true);
        Assert.True(before.Visible);
        Assert.False(before.Installed);
        Assert.NotNull(before.Warning); // the anti-cheat warning

        Assert.True((await fx.Service.InstallAsync(fx.Game)).Success);
        var after = presenter.StateFor(fx.Game, idle: true);
        Assert.True(after.Installed);
        Assert.Equal(offered, after.OffersUniversalMfg);

        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fx.Game), GameName = fx.Game.Name, Component = ManagedComponentKind.Rtx40MfgStandalone,
            BinaryDirectory = fx.Bin, Version = "1", Files = [new LinuxDeployedFile { Path = Path.Combine(fx.Bin, "version.dll") }]
        });
        Assert.False(presenter.StateFor(fx.Game, idle: true).OffersUniversalMfg);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Fx(DetectedGame Game, string Bin, LinuxDeploymentRecordStore Records, HsfDeploymentService Service, HsfServer Server, byte[] IniBefore);

    private Fx Fixture(string name, GraphicsApi api, ushort machine = 0x8664, bool withReShade = true, bool badDigest = false)
    {
        var bin = Path.Combine(_root, name, "game");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, machine);
        var game = new DetectedGame
        {
            Name = "HSF " + name, InstallPath = Path.Combine(_root, name), BinaryDirectory = bin, ExecutablePath = exe,
            Runner = "Proton", GraphicsApi = api, Source = "Test", UserGraphicsApiOverride = api
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, name + "-records.json"));
        // The ReShade.ini RHI's ReShade install writes (Launchpad's search paths are added to it, #274/#277).
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\n");
        if (withReShade)
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
                BinaryDirectory = bin, Version = "6.6.2", Files = [new LinuxDeployedFile { Path = Path.Combine(bin, "dxgi.dll") }]
            });
        var server = new HsfServer(badDigest);
        server.Publish("0.9.8.8", helperBytes: "helper 0.9.8.8");
        var http = new HttpClient(server);
        var service = new HsfDeploymentService(http, records, null, new ImmerseLaunchpadService(http, new GitcUpliftDeploymentServiceTests.FakeRunning()),
            Path.Combine(_root, name + "-cache"));
        return new Fx(game, bin, records, service, server, File.ReadAllBytes(Path.Combine(bin, "ReShade.ini")));
    }

    /// <summary>GitHub's release list and the zip, built with the real 0.9.8.8 entry list (stand-in bytes).</summary>
    internal sealed class HsfServer(bool badDigest) : HttpMessageHandler
    {
        private string _json = "[]";
        private byte[] _zip = [];

        public void Publish(string version, string helperBytes)
        {
            var entries = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hsf", "HSF-0.9.8.8-entries.txt")).Where(l => l.Length > 0);
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var entry in entries)
                {
                    var text = entry switch
                    {
                        "HSF.ini" => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hsf", "HSF-0.9.8.8-HSF.ini")),
                        "HSF_streamline/hsf-dlssg-helper64.exe" => helperBytes,
                        _ => entry + " " + version
                    };
                    using var stream = zip.CreateEntry(entry).Open();
                    stream.Write(Encoding.UTF8.GetBytes(text));
                }
            _zip = buffer.ToArray();
            var digest = badDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(_zip)).ToLowerInvariant();
            _json = $$"""[{"tag_name":"v{{version}}","draft":false,"assets":[{"name":"HSF-{{version}}.zip","digest":"sha256:{{digest}}","browser_download_url":"https://github.com/Capt-Hunter/HSF/releases/download/v{{version}}/HSF-{{version}}.zip"}]}]""";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var response = url == ImmerseLaunchpadService.ArchiveUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImmerseArchive) }
                : url == HsfPackage.ReleasesApiUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json, Encoding.UTF8, "application/json") }
                : url.StartsWith("https://github.com/Capt-Hunter/HSF/releases/download/", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_zip) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>The iMMERSE branch archive's Launchpad files (stand-in bytes; layout as martymcmodding/iMMERSE main).</summary>
    internal static readonly byte[] ImmerseArchive = BuildImmerseArchive();

    private static byte[] BuildImmerseArchive()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in new[]
                     {
                         ("iMMERSE-main/Shaders/MartysMods_LAUNCHPAD.fx", "#include \".\\MartysMods\\mmx_global.fxh\"\ntechnique MartysMods_Launchpad { }\n"),
                         ("iMMERSE-main/Shaders/MartysMods/mmx_global.fxh", "// global\n"),
                         ("iMMERSE-main/Shaders/MartysMods_MXAO.fx", "// not Launchpad\n"),
                         ("iMMERSE-main/Textures/iMMERSE_bluenoise_opt.png", "png"),
                     })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        return buffer.ToArray();
    }
}
