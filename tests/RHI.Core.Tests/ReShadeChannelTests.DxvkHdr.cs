using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #200 DX9 HDR: DXVK HDR (EndlesslyFlowering) as d3d9.dll + dxvk.conf beside a DirectX 9 game with a per-exe
// d3d9=native,builtin override, journaled and removable byte for byte; an RHI ReShade at d3d9.dll moves to its per-game
// Vulkan layer in the same operation and back on removal (reviews/w7-dxvk-hdr-wine.md). The release archive is a .7z:
// the host 7-Zip is replaced by a script that hands out the fixture's d3d9.dll per bitness.
public sealed partial class ReShadeChannelTests
{
    private const string DxvkTag = "v9.9-HDR-mod-v0.9.9";
    private const string DxvkVersion = "9.9-HDR-mod-v0.9.9"; // the release resolver drops the tag's leading "v"

    private (DetectedGame Game, LinuxDeploymentRecordStore Store) CreateDx9Game(string name, bool is32 = false)
    {
        var (game, store) = CreateVulkanGame(name, is32);
        game.GraphicsApi = GraphicsApi.DirectX9;
        game.GraphicsApis = [GraphicsApi.DirectX9];
        return (game, store);
    }

    /// <summary>A fake DXVK HDR d3d9.dll: a PE of <paramref name="machine"/> carrying the build's marker and a variant tag.</summary>
    internal static byte[] DxvkHdrDll(ushort machine, string variant = "a")
    {
        var bytes = new byte[8192];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        Encoding.ASCII.GetBytes(ComponentProbe.DxvkHdrMarker + "\0dxvk-hdr-" + variant).CopyTo(bytes, 4096);
        return bytes;
    }

    /// <summary>The fixture's DXVK HDR builds and a fake 7-Zip that hands them out (7z e &lt;archive&gt; &lt;member&gt; -o&lt;out&gt; -y).</summary>
    private sealed class DxvkHdrBuilds
    {
        public DxvkHdrBuilds(string root)
        {
            Folder = Path.Combine(root, "dxvk-builds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
            Set("a");
            Tool = Path.Combine(Folder, "fake-7z");
            File.WriteAllText(Tool, $$"""
                #!/bin/sh
                out="${4#-o}"
                case "$3" in
                  normal/x64/d3d9.dll) cp "{{Folder}}/x64.dll" "$out/d3d9.dll" ;;
                  normal/x32/d3d9.dll) cp "{{Folder}}/x32.dll" "$out/d3d9.dll" ;;
                  *) exit 2 ;;
                esac
                """);
            File.SetUnixFileMode(Tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ExternalArchiveRunner.FindToolForTesting.Value = () => new ExternalArchiveTool(Tool, ExternalArchiveToolKind.SevenZip);
        }

        public string Folder { get; }
        public string Tool { get; }
        public byte[] Bytes(bool is32) => File.ReadAllBytes(Path.Combine(Folder, is32 ? "x32.dll" : "x64.dll"));

        public void Set(string variant)
        {
            File.WriteAllBytes(Path.Combine(Folder, "x64.dll"), DxvkHdrDll(0x8664, variant));
            File.WriteAllBytes(Path.Combine(Folder, "x32.dll"), DxvkHdrDll(0x014c, variant));
        }
    }

    /// <summary>GitHub for EndlesslyFlowering/dxvk: the latest release (a gplasync decoy first, then the .7z) and its archive.</summary>
    private sealed class DxvkHdrHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public string Tag { get; set; } = DxvkTag;
        public byte[] Archive { get; set; } = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
        public bool Unreachable { get; set; }
        public string? AssetUrlOverride { get; set; }
        public int ApiRequests { get; private set; }
        public int ArchiveRequests { get; private set; }
        public string AssetUrl => AssetUrlOverride ?? $"https://github.com/EndlesslyFlowering/dxvk/releases/download/{Tag}/dxvk_{Tag}.7z";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/EndlesslyFlowering/dxvk/releases/latest")
            {
                ApiRequests++;
                if (Unreachable) throw new HttpRequestException("Connection refused");
                var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Archive)).ToLowerInvariant();
                var json = $$"""
                    {"tag_name":"{{Tag}}","assets":[
                      {"name":"dxvk_{{Tag}}-gplasync.7z","browser_download_url":"https://github.com/EndlesslyFlowering/dxvk/releases/download/{{Tag}}/dxvk_{{Tag}}-gplasync.7z","digest":"sha256:00"},
                      {"name":"dxvk_{{Tag}}.7z","browser_download_url":"{{AssetUrl}}","digest":"{{digest}}"}]}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/EndlesslyFlowering/dxvk/releases/download/", StringComparison.Ordinal))
            {
                ArchiveRequests++;
                if (Unreachable) throw new HttpRequestException("Connection refused");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record DxvkRig(DetectedGame Game, LinuxDeploymentRecordStore Store, DxvkHdrBuilds Builds, DxvkHdrHandler Handler,
        ReShadeDeploymentService ReShade, DxvkHdrDeploymentService Dxvk, string Ledger, string? JournalDirectory, DeploymentJournal? Journal)
    {
        public string Bin => Game.BinaryDirectory!;
        public string D3d9 => Path.Combine(Bin, "d3d9.dll");
        public string Conf => Path.Combine(Bin, "dxvk.conf");
        public string UserReg => Path.Combine(Game.PrefixPath!, "user.reg");
        public string SystemReg => Path.Combine(Game.PrefixPath!, "system.reg");
        public string? Override(string dll) => WineDllOverrideService.TryReadOverride(File.ReadAllText(UserReg), dll, "Game.exe");
    }

    private DxvkRig Rig(string name, bool is32 = false, bool journaled = false, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
    {
        var (game, store) = CreateDx9Game(name, is32);
        var ledger = Path.Combine(_root, name, "records.json");
        var builds = new DxvkHdrBuilds(_root);
        var handler = new DxvkHdrHandler(new LunarGHandler());
        var cache = Path.Combine(_root, name + "-cache");
        string? journalDir = null;
        DeploymentJournal? journal = null;
        WineDllOverrideService overrides;
        if (journaled)
        {
            journalDir = Path.Combine(_root, name, "journal");
            (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        }
        else overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var http = new HttpClient(wrap?.Invoke(handler) ?? handler);
        var reshade = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), store, overrides,
            new FakeExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);
        var dxvk = new DxvkHdrDeploymentService(http, store, overrides, reshade, journal, Path.Combine(cache, "dxvk-hdr"));
        return new DxvkRig(game, store, builds, handler, reshade, dxvk, ledger, journalDir, journal);
    }

    private static async Task InstallDllReShadeAsync(DxvkRig rig)
    {
        var result = await rig.ReShade.InstallAsync(rig.Game, new ReShadeInstallOptions());
        Assert.True(result.Success, result.Message);
        Assert.Equal(rig.D3d9, ProxyPayloadLocator.ReShadePayload(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade))!.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DxvkHdr_Install_PlacesTheBuildAndSafestPresetWithTheExeOverride_AndRemoveGivesItAllBack(bool is32)
    {
        var rig = Rig(is32 ? "dxvk-32" : "dxvk-64", is32);
        var before = CancelAtWritePhase.Snapshot(rig.Bin);
        var userBefore = File.ReadAllBytes(rig.UserReg);

        var installed = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(DxvkHdrDeploymentService.InstalledPrefix + DxvkVersion, installed.Message);
        Assert.Equal(rig.Builds.Bytes(is32), File.ReadAllBytes(rig.D3d9));
        Assert.Equal(DxvkHdrPresets.ConfFor(0), File.ReadAllText(rig.Conf));
        Assert.Contains("d3d9.upgradeSwapChainColorSpaceTo         = scRGB", File.ReadAllText(rig.Conf), StringComparison.Ordinal);
        Assert.DoesNotContain("enableAsync", File.ReadAllText(rig.Conf), StringComparison.Ordinal);
        Assert.Equal("native,builtin", rig.Override("d3d9"));
        var record = rig.Dxvk.GetRecord(rig.Game)!;
        Assert.Equal(DxvkVersion, record.Version);
        Assert.Equal(VerifiedDownload.PublishedDigest, record.Metadata[VerifiedDownload.MetadataIntegrityKey]);
        Assert.Equal("0", record.Metadata[DxvkHdrDeploymentService.MetaPreset]);
        Assert.Equal("d3d9", record.WineOverride!.DllName);
        Assert.Equal(1, rig.Handler.ArchiveRequests);

        var removed = await rig.Dxvk.RemoveAsync(rig.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(DxvkHdrDeploymentService.RemovedMessage, removed.Message);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin);
        Assert.Equal(userBefore, File.ReadAllBytes(rig.UserReg));
        Assert.Null(rig.Dxvk.GetRecord(rig.Game));
    }

    public static TheoryData<string> DxvkRefusals() => new()
    {
        "foreign-d3d9", "plain-dxvk-d3d9", "held-by-component", "foreign-conf", "not-dx9", "xbox", "native", "no-prefix", "unknown-bitness"
    };

    // Each refusal comes before any download or change: the game folder and the hive stay byte for byte.
    [Theory]
    [MemberData(nameof(DxvkRefusals))]
    public async Task DxvkHdr_Install_Refusal_ChangesNothingAndDownloadsNothing(string refusal)
    {
        var rig = Rig("dxvk-refuse-" + refusal);
        var game = rig.Game;
        string expected;
        switch (refusal)
        {
            case "foreign-d3d9":
                File.WriteAllBytes(rig.D3d9, [1, 2, 3]);
                expected = DxvkHdrDeploymentService.ForeignD3d9Prefix + rig.D3d9;
                break;
            case "plain-dxvk-d3d9":
                PeArchitectureDetectorTests.WritePe(rig.D3d9, 0x8664, 8192);
                expected = DxvkHdrDeploymentService.ForeignD3d9Prefix + rig.D3d9;
                break;
            case "held-by-component":
                File.WriteAllBytes(rig.D3d9, [4, 5, 6]);
                rig.Store.Upsert(new LinuxDeploymentRecord
                {
                    GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.LumaFramework,
                    BinaryDirectory = rig.Bin, Files = [new LinuxDeployedFile { Path = rig.D3d9, Sha256 = FileHash.Sha256Hex(rig.D3d9) }]
                });
                expected = DxvkHdrDeploymentService.HeldByComponentPrefix + "Luma Framework";
                break;
            case "foreign-conf":
                File.WriteAllText(rig.Conf, "d3d9.maxFrameRate = 60\n");
                expected = DxvkHdrDeploymentService.ForeignConfPrefix + rig.Conf;
                break;
            case "not-dx9":
                game.GraphicsApi = GraphicsApi.DirectX11;
                expected = DxvkHdrDeploymentService.NotOfferedMessage;
                break;
            case "xbox":
                game.XboxPackage = new XboxPackageInfo("9TEST", "installer", null, null);
                expected = DxvkHdrDeploymentService.NotOfferedMessage;
                break;
            case "native":
                game.Runner = "Native Linux";
                expected = DxvkHdrDeploymentService.NotOfferedMessage;
                break;
            case "no-prefix":
                File.Delete(rig.UserReg);
                expected = DxvkHdrDeploymentService.PrefixNotStartedMessage;
                break;
            default:
                File.WriteAllBytes(game.ExecutablePath!, [0x4D, 0x5A, 0, 0]);
                expected = DxvkHdrDeploymentService.BitnessUnknownMessage;
                break;
        }
        var before = CancelAtWritePhase.Snapshot(rig.Bin, game.PrefixPath!);

        var result = await rig.Dxvk.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin, game.PrefixPath!);
        Assert.Equal((0, 0), (rig.Handler.ApiRequests, rig.Handler.ArchiveRequests));
        Assert.Null(rig.Dxvk.GetRecord(game));
    }

    [Fact]
    public async Task DxvkHdr_Remove_KeepsADxvkConfTheUserChanged_AndSaysSo()
    {
        var rig = Rig("dxvk-keep-conf");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        File.AppendAllText(rig.Conf, "d3d9.maxFrameRate = 60\n");
        var edited = File.ReadAllBytes(rig.Conf);

        var removed = await rig.Dxvk.RemoveAsync(rig.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(DxvkHdrDeploymentService.RemovedMessage + DxvkHdrDeploymentService.KeptConfPrefix + rig.Conf, removed.Message);
        Assert.Equal(edited, File.ReadAllBytes(rig.Conf));
        Assert.False(File.Exists(rig.D3d9));
    }

    [Fact]
    public async Task DxvkHdr_Remove_RefusesAD3d9ChangedOutsideRhi_AndChangesNothing()
    {
        var rig = Rig("dxvk-changed-d3d9");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        File.WriteAllBytes(rig.D3d9, DxvkHdrDll(0x8664, "user"));
        var before = CancelAtWritePhase.Snapshot(rig.Bin, rig.Game.PrefixPath!);

        var removed = await rig.Dxvk.RemoveAsync(rig.Game);

        Assert.False(removed.Success);
        Assert.Equal(DxvkHdrDeploymentService.ModifiedFilePrefix + rig.D3d9, removed.Message);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin, rig.Game.PrefixPath!);
        Assert.NotNull(rig.Dxvk.GetRecord(rig.Game));
    }

    [Fact]
    public async Task DxvkHdr_Update_SwapsOnlyTheD3d9_AndTheCheckSaysSo()
    {
        var rig = Rig("dxvk-update");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        var conf = File.ReadAllBytes(rig.Conf);
        var user = File.ReadAllBytes(rig.UserReg);
        Assert.False((await rig.Dxvk.CheckUpdateAsync(rig.Game)).UpdateAvailable);

        rig.Handler.Tag = "v10.0-HDR-mod-v1.0.0";
        rig.Handler.Archive = [.. rig.Handler.Archive, 42];
        rig.Builds.Set("b");
        var check = await rig.Dxvk.CheckUpdateAsync(rig.Game);
        var updated = await rig.Dxvk.UpdateAsync(rig.Game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal((DxvkVersion, "10.0-HDR-mod-v1.0.0"), (check.InstalledVersion, check.LatestVersion));
        Assert.True(updated.Success, updated.Message);
        Assert.Equal(DxvkHdrDeploymentService.UpdatedPrefix + "10.0-HDR-mod-v1.0.0", updated.Message);
        Assert.Equal(rig.Builds.Bytes(false), File.ReadAllBytes(rig.D3d9));
        Assert.Equal(conf, File.ReadAllBytes(rig.Conf));
        Assert.Equal(user, File.ReadAllBytes(rig.UserReg));
        Assert.Equal("10.0-HDR-mod-v1.0.0", rig.Dxvk.GetRecord(rig.Game)!.Version);
    }

    [Fact]
    public async Task DxvkHdr_CheckUpdate_Offline_IsCouldNotCheck()
    {
        var rig = Rig("dxvk-offline");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        rig.Handler.Unreachable = true;

        var check = await rig.Dxvk.CheckUpdateAsync(rig.Game);

        Assert.True(check.CouldNotCheck, check.Message);
        Assert.False(check.UpdateAvailable);
    }

    [Fact]
    public async Task DxvkHdr_Preset_RewritesTheConf_AndNeverReplacesOneTheUserChanged()
    {
        var rig = Rig("dxvk-preset");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);

        var set = await rig.Dxvk.SetPresetAsync(rig.Game, 5);

        Assert.True(set.Success, set.Message);
        Assert.Equal(DxvkHdrPresets.ConfFor(5), File.ReadAllText(rig.Conf));
        Assert.Contains("upgrade_RGBA8_UNORM_renderTargetTo   = rgba16_sfloat", File.ReadAllText(rig.Conf), StringComparison.Ordinal);
        Assert.Equal(5, rig.Dxvk.PresetOf(rig.Game));

        File.AppendAllText(rig.Conf, "# mine\n");
        var edited = File.ReadAllBytes(rig.Conf);
        var refused = await rig.Dxvk.SetPresetAsync(rig.Game, 0);

        Assert.False(refused.Success);
        Assert.Equal(DxvkHdrDeploymentService.ConfChangedPrefix + rig.Conf, refused.Message);
        Assert.Equal(edited, File.ReadAllBytes(rig.Conf));
        Assert.Equal(5, rig.Dxvk.PresetOf(rig.Game));
    }

    // The archive's d3d9.dll must be DXVK HDR for the game's bitness: a plain DXVK (no HDR build marker) or the other
    // bitness is refused before anything changes, and nothing is cached as if it were the build.
    [Theory]
    [InlineData("plain-dxvk")]
    [InlineData("other-bitness")]
    public async Task DxvkHdr_ArchiveWithoutTheGamesHdrBuild_IsRefusedBeforeAnyChange(string what)
    {
        var rig = Rig("dxvk-notbuild-" + what);
        if (what == "plain-dxvk") PeArchitectureDetectorTests.WritePe(Path.Combine(rig.Builds.Folder, "x64.dll"), 0x8664, 8192);
        else File.WriteAllBytes(Path.Combine(rig.Builds.Folder, "x64.dll"), DxvkHdrDll(0x014c));
        var before = CancelAtWritePhase.Snapshot(rig.Bin, rig.Game.PrefixPath!);

        var result = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.False(result.Success);
        Assert.Equal(DxvkHdrDeploymentService.DownloadFailedPrefix + DxvkHdrDeploymentService.NotDxvkHdrPrefix + "normal/x64/d3d9.dll", result.Message);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin, rig.Game.PrefixPath!);

        rig.Builds.Set("a"); // the release fixed: the next install takes it (the refused copy was never cached)
        var retried = await rig.Dxvk.InstallAsync(rig.Game);
        Assert.True(retried.Success, retried.Message);
        Assert.Equal(rig.Builds.Bytes(false), File.ReadAllBytes(rig.D3d9));
    }

    [Fact]
    public async Task DxvkHdr_UntrustedAssetLink_IsNeverDownloaded()
    {
        var rig = Rig("dxvk-untrusted");
        rig.Handler.AssetUrlOverride = "https://github.com/someone-else/dxvk/releases/download/v1/dxvk_v1.7z";

        var result = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.False(result.Success);
        Assert.Equal(DxvkHdrDeploymentService.DownloadFailedPrefix + DxvkHdrDeploymentService.UntrustedAssetPrefix + rig.Handler.AssetUrl, result.Message);
        Assert.Equal(0, rig.Handler.ArchiveRequests);
        Assert.False(File.Exists(rig.D3d9));
    }

    // Coordinator decision (#200): an RHI ReShade at d3d9.dll moves to its per-game Vulkan layer inside DX9 HDR's install
    // (one ledger write for both records) and back on removal, which leaves the game folder and both hives as they were
    // with ReShade alone.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DxvkHdr_WithDllReShade_MovesReShadeToItsVulkanLayer_AndRemovalPutsItBackByteForByte(bool is32)
    {
        var rig = Rig(is32 ? "dxvk-move-32" : "dxvk-move-64", is32);
        await InstallDllReShadeAsync(rig);
        var withReShade = CancelAtWritePhase.Snapshot(rig.Bin);
        var userBefore = File.ReadAllBytes(rig.UserReg);
        var systemBefore = File.ReadAllBytes(rig.SystemReg);
        var proxy = File.ReadAllBytes(rig.D3d9);
        var proxyRecord = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        Assert.True(rig.Dxvk.WillMoveReShade(rig.Game));

        var installed = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(DxvkHdrDeploymentService.InstalledPrefix + DxvkVersion + DxvkHdrDeploymentService.ReShadeMovedNote, installed.Message);
        Assert.Equal(rig.Builds.Bytes(is32), File.ReadAllBytes(rig.D3d9));
        var layer = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(layer));
        var folder = LayerFolder(rig.Game);
        Assert.Equal(proxy, File.ReadAllBytes(Path.Combine(folder, ReShadeVulkanLayer.LayerDllName(is32))));
        Assert.True(File.Exists(Path.Combine(folder, ReShadeVulkanLayer.ManifestName(is32))));
        Assert.Equal(LunarGHandler.Loader(is32), File.ReadAllBytes(Path.Combine(rig.Bin, "vulkan-1.dll")));
        Assert.Equal("native,builtin", rig.Override("vulkan-1"));
        Assert.Equal("native,builtin", rig.Override("d3d9"));
        Assert.Contains(is32 ? "Wow6432Node\\\\Khronos" : "[Software\\\\Khronos\\\\Vulkan\\\\ImplicitLayers]", File.ReadAllText(rig.SystemReg), StringComparison.Ordinal);
        Assert.Single(layer.RegistryValues);
        Assert.All(layer.Files, file => Assert.True(File.Exists(file.Path), file.Path));
        var dxvk = rig.Dxvk.GetRecord(rig.Game)!;
        Assert.Equal("1", dxvk.Metadata[DxvkHdrDeploymentService.MetaOverrideFromReShade]);
        Assert.Equal(proxyRecord.WineOverride!.WrittenValue, dxvk.WineOverride!.WrittenValue);
        Assert.Equal("vulkan-1", layer.WineOverride!.DllName);

        var removed = await rig.Dxvk.RemoveAsync(rig.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(DxvkHdrDeploymentService.RemovedMessage + DxvkHdrDeploymentService.ReShadeBackNote, removed.Message);
        CancelAtWritePhase.AssertUnchanged(withReShade, rig.Bin);
        Assert.Equal(userBefore, File.ReadAllBytes(rig.UserReg));
        Assert.Equal(systemBefore, File.ReadAllBytes(rig.SystemReg));
        Assert.False(Directory.Exists(Path.Combine(rig.Game.PrefixPath!, "drive_c")));
        var back = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        Assert.False(ReShadeVulkanLayer.IsLayerRecord(back));
        Assert.Equal(proxyRecord.Files.Select(f => (f.Path, f.Sha256, f.AllowUserChanges)).Order(),
            back.Files.Select(f => (f.Path, f.Sha256, f.AllowUserChanges)).Order());
        Assert.Equal("d3d9", back.WineOverride!.DllName);
        Assert.Empty(back.RegistryValues);
        Assert.Empty(back.DirectoriesCreatedByRhi);
        Assert.DoesNotContain(back.Metadata.Keys, key => key.Contains("Vulkan", StringComparison.Ordinal) || key.Contains("DxvkHdr", StringComparison.Ordinal));

        // ReShade is an ordinary DLL ReShade again: its own removal gives the hive back as it was before everything.
        Assert.True((await rig.ReShade.RemoveAsync(rig.Game)).Success);
        Assert.False(File.Exists(rig.D3d9));
    }

    // Installing ReShade on a game that already has DX9 HDR takes the per-game Vulkan layer (the d3d9.dll name is DXVK
    // HDR's); removing DX9 HDR then gives ReShade the d3d9.dll name with the d3d9 override.
    [Fact]
    public async Task DxvkHdr_ReShadeInstalledLater_TakesTheVulkanLayer_AndRemovalPutsItOnD3d9()
    {
        var rig = Rig("dxvk-later");
        var userBefore = File.ReadAllBytes(rig.UserReg);
        var systemBefore = File.ReadAllBytes(rig.SystemReg);
        Assert.Same(rig.Game, rig.ReShade.ViewForDxvkHdr(rig.Game)); // no DX9 HDR: ReShade's ordinary path
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);

        var view = rig.ReShade.ViewForDxvkHdr(rig.Game);
        var reshade = await rig.ReShade.InstallAsync(view, new ReShadeInstallOptions { VulkanLayer = ReShadeVulkanLayer.IsVulkanTarget(view) });

        Assert.True(reshade.Success, reshade.Message);
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
        Assert.Equal(rig.Builds.Bytes(false), File.ReadAllBytes(rig.D3d9));
        var layerDll = File.ReadAllBytes(Path.Combine(LayerFolder(rig.Game), "ReShade64.dll"));

        var removed = await rig.Dxvk.RemoveAsync(rig.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(layerDll, File.ReadAllBytes(rig.D3d9));
        var back = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        Assert.False(ReShadeVulkanLayer.IsLayerRecord(back));
        Assert.Equal(rig.D3d9, ProxyPayloadLocator.ReShadePayload(back)!.Path);
        Assert.Equal("native,builtin", rig.Override("d3d9"));
        Assert.Null(rig.Override("vulkan-1"));
        Assert.Equal(systemBefore, File.ReadAllBytes(rig.SystemReg));

        Assert.True((await rig.ReShade.RemoveAsync(rig.Game)).Success);
        Assert.Equal(userBefore, File.ReadAllBytes(rig.UserReg));
    }

    public static TheoryData<string> DxvkMoveRefusals() => new() { "modified-proxy", "other-name", "shortfuse" };

    [Theory]
    [MemberData(nameof(DxvkMoveRefusals))]
    public async Task DxvkHdr_ReShadeItCannotMove_IsRefusedBeforeAnyChange(string refusal)
    {
        var rig = Rig("dxvk-nomove-" + refusal);
        string expected;
        if (refusal == "other-name")
        {
            rig.Game.GraphicsApi = GraphicsApi.DirectX11;
            Assert.True((await rig.ReShade.InstallAsync(rig.Game, new ReShadeInstallOptions())).Success);
            rig.Game.GraphicsApi = GraphicsApi.DirectX9;
            expected = ReShadeDeploymentService.DxvkHdrReShadeNamePrefix + "dxgi.dll";
        }
        else
        {
            await InstallDllReShadeAsync(rig);
            if (refusal == "modified-proxy")
            {
                var bytes = File.ReadAllBytes(rig.D3d9);
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(rig.D3d9, bytes);
                expected = ReShadeDeploymentService.DxvkHdrReShadeModifiedPrefix + rig.D3d9;
            }
            else
            {
                var record = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
                record.Metadata[ShortFuseDeploymentService.MetaReShadeOriginalPath] = rig.D3d9;
                rig.Store.Upsert(record);
                expected = ReShadeDeploymentService.DxvkHdrReShadeShortFuseMessage;
            }
        }
        var before = CancelAtWritePhase.Snapshot(rig.Bin, rig.Game.PrefixPath!);
        var requests = (rig.Handler.ApiRequests, rig.Handler.ArchiveRequests);

        var result = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin, rig.Game.PrefixPath!);
        Assert.Equal(requests, (rig.Handler.ApiRequests, rig.Handler.ArchiveRequests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DxvkHdr_InstallWithReShade_LedgerWriteFails_RollsBothBack(bool journaled)
    {
        var rig = Rig(journaled ? "dxvk-blocked-j" : "dxvk-blocked", journaled: journaled);
        await InstallDllReShadeAsync(rig);
        var before = CancelAtWritePhase.Snapshot(rig.Bin);
        var userBefore = File.ReadAllBytes(rig.UserReg);
        var systemBefore = File.ReadAllBytes(rig.SystemReg);
        var ledgerBefore = File.ReadAllBytes(rig.Ledger);

        ComponentOperationResult result;
        using (LedgerWriteBlocker.Block(rig.Store, rig.Ledger)) result = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.False(result.Success);
        Assert.StartsWith(DxvkHdrDeploymentService.FailedRolledBackPrefix, result.Message, StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin);
        Assert.Equal(userBefore, File.ReadAllBytes(rig.UserReg));
        Assert.Equal(systemBefore, File.ReadAllBytes(rig.SystemReg));
        Assert.Equal(ledgerBefore, File.ReadAllBytes(rig.Ledger));
        Assert.False(Directory.Exists(LayerFolder(rig.Game)));
    }

    [Fact]
    public async Task DxvkHdr_RemoveWithReShade_CommitWriteFails_RollsBothBack()
    {
        var rig = Rig("dxvk-remove-blocked", journaled: true);
        await InstallDllReShadeAsync(rig);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        var before = CancelAtWritePhase.Snapshot(rig.Bin, LayerFolder(rig.Game));
        var systemBefore = File.ReadAllBytes(rig.SystemReg);
        var saves = 0;
        rig.Store.SaveFaultForTesting = () => ++saves == 2 ? new IOException("disk full") : null; // 1 = the removal's stamp

        var result = await rig.Dxvk.RemoveAsync(rig.Game);
        rig.Store.SaveFaultForTesting = null;

        Assert.False(result.Success);
        Assert.StartsWith(DxvkHdrDeploymentService.RemovalFailedPrefix, result.Message, StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(before, rig.Bin, LayerFolder(rig.Game));
        // The restored Wine values are put back (a re-applied key carries a new time stamp, so not byte for byte).
        Assert.Equal(("native,builtin", "native,builtin"), (rig.Override("d3d9"), rig.Override("vulkan-1")));
        Assert.Equal(systemBefore.Length, File.ReadAllBytes(rig.SystemReg).Length);
        Assert.Contains("ReShade64.json\"=dword:00000000", File.ReadAllText(rig.SystemReg), StringComparison.Ordinal);
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
        Assert.NotNull(rig.Dxvk.GetRecord(rig.Game));
    }

    // Every ledger write of the install is a crash point: the next start leaves records that describe the files — either
    // DX9 HDR in with ReShade on its layer, or neither, with ReShade back at d3d9.dll.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DxvkHdr_InstallWithReShade_CrashAtAnyLedgerWrite_LeavesRecordsThatDescribeTheFiles(int crashAtSave)
    {
        var rig = Rig("dxvk-crash-install-" + crashAtSave, journaled: true);
        await InstallDllReShadeAsync(rig);
        var before = CancelAtWritePhase.Snapshot(rig.Bin);
        var userBefore = File.ReadAllBytes(rig.UserReg);
        var systemBefore = File.ReadAllBytes(rig.SystemReg);
        bool died;
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(rig.Journal!))
        {
            var saves = 0;
            rig.Store.SaveFaultForTesting = () => ++saves == crashAtSave ? death.Die() : null;
            (died, run) = await death.RunUntilDeathAsync(() => rig.Dxvk.InstallAsync(rig.Game));
            if (died)
            {
                await SimulatedDeath.RecoverAsync(rig.JournalDirectory!, rig.Ledger);
                var fresh = new LinuxDeploymentRecordStore(rig.Ledger);
                var reShade = fresh.Find(rig.Game, ManagedComponentKind.ReShade)!;
                Assert.All(reShade.Files, file => Assert.True(File.Exists(file.Path), file.Path));
                if (fresh.Find(rig.Game, ManagedComponentKind.DxvkHdr) is { } dxvk)
                {
                    Assert.True(ReShadeVulkanLayer.IsLayerRecord(reShade));
                    Assert.All(dxvk.Files, file => Assert.True(File.Exists(file.Path), file.Path));
                }
                else
                {
                    Assert.False(ReShadeVulkanLayer.IsLayerRecord(reShade));
                    CancelAtWritePhase.AssertUnchanged(before, rig.Bin);
                    Assert.Equal(userBefore, File.ReadAllBytes(rig.UserReg));
                    Assert.Equal(systemBefore, File.ReadAllBytes(rig.SystemReg));
                }
            }
        }
        var result = await run;
        rig.Store.SaveFaultForTesting = null;
        if (!died) Assert.True(result.Success, result.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DxvkHdr_RemoveWithReShade_CrashAtAnyLedgerWrite_LeavesRecordsThatDescribeTheFiles(int crashAtSave)
    {
        var rig = Rig("dxvk-crash-remove-" + crashAtSave, journaled: true);
        await InstallDllReShadeAsync(rig);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        var installed = CancelAtWritePhase.Snapshot(rig.Bin);
        var stillInstalled = false;
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(rig.Journal!))
        {
            var saves = 0;
            rig.Store.SaveFaultForTesting = () => ++saves == crashAtSave ? death.Die() : null; // 1 = the removal's stamp
            (var died, run) = await death.RunUntilDeathAsync(() => rig.Dxvk.RemoveAsync(rig.Game));
            if (died)
            {
                await SimulatedDeath.RecoverAsync(rig.JournalDirectory!, rig.Ledger);
                var fresh = new LinuxDeploymentRecordStore(rig.Ledger);
                var reShade = fresh.Find(rig.Game, ManagedComponentKind.ReShade)!;
                Assert.All(reShade.Files, file => Assert.True(File.Exists(file.Path), file.Path));
                stillInstalled = fresh.Find(rig.Game, ManagedComponentKind.DxvkHdr) is not null;
                Assert.Equal(stillInstalled, ReShadeVulkanLayer.IsLayerRecord(reShade));
                if (stillInstalled) CancelAtWritePhase.AssertUnchanged(installed, rig.Bin);
            }
        }
        await run;
        rig.Store.SaveFaultForTesting = null;
        if (stillInstalled)
        {
            var again = await rig.Dxvk.RemoveAsync(rig.Game);
            Assert.True(again.Success, again.Message);
        }
    }

    // Cancel pressed as the install's first write starts is honoured only as a full rollback (forced here by a failing
    // commit write), and nothing is downloaded after the gate (DXVK HDR and the Khronos loader come first).
    [Fact]
    public async Task DxvkHdr_CancelAtTheFirstWrite_RollsBackWholly_AndDownloadsNothingAfterTheGate()
    {
        GateWatchingHandler? watch = null;
        var rig = Rig("dxvk-cancel", wrap: inner => watch = new GateWatchingHandler(inner));
        await InstallDllReShadeAsync(rig);
        var userBefore = File.ReadAllBytes(rig.UserReg);

        var outcome = await CancelAtWritePhase.RunAsync(token => rig.Dxvk.InstallAsync(rig.Game, token), [rig.Bin, rig.Game.PrefixPath! + "/drive_c"],
            atWritePhase: () =>
            {
                watch!.CloseGate();
                rig.Store.SaveFaultForTesting = () => new IOException("disk full");
            });
        rig.Store.SaveFaultForTesting = null;

        Assert.False(outcome.Result.Success);
        CancelAtWritePhase.AssertUnchanged(outcome.Before, rig.Bin);
        Assert.Equal(userBefore, File.ReadAllBytes(rig.UserReg));
        Assert.Empty(watch!.LateRequests);
        Assert.False(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
    }

    [Fact]
    public void DxvkHdr_Presets_AreUpstreamsSixTiers_SafestFirst()
    {
        Assert.Equal(new[] { "Safest", "2nd Safest", "Slightly Unsafe", "Unsafer", "Even Unsafer", "Experimental" }, DxvkHdrPresets.All.Select(p => p.Name));
        Assert.Equal(DxvkHdrPresets.All[0], DxvkHdrPresets.For(99));
        Assert.All(DxvkHdrPresets.All, preset => Assert.Contains("d3d9.upgradeSwapChainColorSpaceTo         = scRGB", preset.Content, StringComparison.Ordinal));
        Assert.DoesNotContain("enableBackBufferUpgrade", DxvkHdrPresets.For(0).Content, StringComparison.Ordinal);
        Assert.DoesNotContain("renderTarget", DxvkHdrPresets.For(2).Content, StringComparison.Ordinal);
        Assert.Contains("upgrade_RGBA8_UNORM_renderTargetTo   = rgba16_unorm", DxvkHdrPresets.For(3).Content, StringComparison.Ordinal);
    }
}
