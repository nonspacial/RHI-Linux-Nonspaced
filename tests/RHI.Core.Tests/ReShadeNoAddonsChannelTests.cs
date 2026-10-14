using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #91: upstream's "No Addons" ReShade channel — the reshade.me build without add-on support (<c>ReShade_Setup_X.exe</c>,
/// upstream <c>NormalReShadeUpdateService</c>), kept apart from the add-on build in the cache and the update check, and
/// never combined with ReShade add-ons.
/// </summary>
public sealed class ReShadeNoAddonsChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-noaddons-" + Guid.NewGuid().ToString("N"));

    public ReShadeNoAddonsChannelTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NoAddons_InstallsTheBuildWithoutAddonSupport_AndRecordsTheChannel()
    {
        var (game, store) = CreateGame("install");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        var service = NewService(store, handler);

        var result = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.Contains(handler.Requests, u => u.EndsWith("/downloads/ReShade_Setup_6.8.0.exe", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, u => u.Contains("_Addon", StringComparison.Ordinal));
        Assert.Contains("NO-ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.Equal(ReShadeChannel.NoAddons, service.GetInstalledOptions(game).Channel);
        Assert.True(ReShadeDeploymentService.LacksAddonSupport(store.Find(game, ManagedComponentKind.ReShade)));
        Assert.Equal("6.8.0", store.Find(game, ManagedComponentKind.ReShade)!.Version);
    }

    [Fact]
    public async Task AddonChannel_StillInstallsTheAddonBuild()
    {
        var (game, store) = CreateGame("addon");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        var service = NewService(store, handler);

        var result = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.Contains("ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.DoesNotContain("NO-ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.False(ReShadeDeploymentService.LacksAddonSupport(store.Find(game, ManagedComponentKind.ReShade)));
    }

    // The update check compares with the No Addons release, not the add-on one (they can differ on reshade.me).
    [Fact]
    public async Task NoAddons_UpdateCheck_FollowsTheNoAddonsRelease()
    {
        var (game, store) = CreateGame("update");
        var handler = new Handler { AddonVersion = "6.7.0", NoAddonsVersion = "6.7.0" };
        var service = NewService(store, handler);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false })).Success);

        handler.AddonVersion = "6.9.0";
        var current = await service.CheckUpdateAsync(game);
        Assert.False(current.UpdateAvailable, current.Message);
        Assert.Equal(ReShadeDeploymentService.NoAddonsCurrentPrefix + "6.7.0.", current.Message);

        handler.NoAddonsVersion = "6.8.0";
        var newer = await service.CheckUpdateAsync(game);
        Assert.True(newer.UpdateAvailable, newer.Message);
        Assert.Equal("6.8.0", newer.LatestVersion);

        var update = await service.UpdateAsync(game);
        Assert.True(update.Success, update.Message);
        Assert.Equal("6.8.0", store.Find(game, ManagedComponentKind.ReShade)!.Version);
        Assert.Equal(ReShadeChannel.NoAddons, service.GetInstalledOptions(game).Channel);
    }

    // During a reshade.me outage only a cached No Addons copy may stand in, never the add-on build (and the reverse).
    [Fact]
    public async Task NoAddons_Outage_NeverFallsBackToTheAddonBuild()
    {
        var (seedGame, seedStore) = CreateGame("seed");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        Assert.True((await NewService(seedStore, handler).InstallAsync(seedGame, new ReShadeInstallOptions { ManageIni = false })).Success);

        var (game, store) = CreateGame("outage");
        handler.HomeUnreachable = true;
        var result = await NewService(store, handler).InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        Assert.False(result.Success, result.Message);
        Assert.Contains("No cached ReShade copy", result.Message);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
    }

    // Owner decision (#91): as upstream, a game switches to No Addons with add-ons still installed; they stay in place and
    // simply stop loading (a warning is tracked as #170).
    [Fact]
    public async Task NoAddons_WithRhiAddonsInstalled_SwitchesLikeUpstream_AndLeavesTheAddonsInPlace()
    {
        var (game, store) = CreateGame("switch");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        var service = NewService(store, handler);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var addon = Path.Combine(game.BinaryDirectory!, "renodx-game.addon64");
        File.WriteAllText(addon, "MZ RenoDX");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX,
            BinaryDirectory = game.BinaryDirectory!, Version = "mod", Files = [new LinuxDeployedFile { Path = addon }]
        });

        var result = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.Contains("NO-ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.Equal(ReShadeChannel.NoAddons, service.GetInstalledOptions(game).Channel);
        Assert.Equal("MZ RenoDX", File.ReadAllText(addon));
        Assert.NotNull(store.Find(game, ManagedComponentKind.RenoDX));
    }

    [Fact]
    public async Task AddonInstall_OverNoAddonsReShade_IsRefusedAndNotOffered()
    {
        var (game, store) = CreateGame("addon-refused");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Version = "6.8.0",
            Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "dxgi.dll") }],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["ReShade.Channel"] = nameof(ReShadeChannel.NoAddons) }
        });
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), "MZ ReShade");
        var managed = Managed(store, game.InstallPath);

        var install = await managed.InstallAsync(game, ManagedComponentKind.RenoDX, Array.Empty<string>());
        Assert.False(install.Success);
        Assert.StartsWith(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix, install.Message);

        var selection = await managed.ApplyReShadeAddonSelectionAsync(game, ["some-addon"]);
        Assert.False(selection.Success);
        Assert.StartsWith(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix, selection.Message);

        var rows = Rows();
        managed.Decorate(game, rows);
        Assert.False(rows[0].ActionAllowed);
        Assert.Equal(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + "ReLimiter", rows[0].Note);

        // Back on an add-on build, the add-on is offered again with no such note.
        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        record.Metadata["ReShade.Channel"] = nameof(ReShadeChannel.Stable);
        store.Upsert(record);
        rows = Rows();
        managed.Decorate(game, rows);
        Assert.DoesNotContain(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix, rows[0].Note ?? "");

        static List<ManagedComponent> Rows() =>
            [new ManagedComponent { Kind = ManagedComponentKind.ReLimiter, DisplayName = "ReLimiter", Availability = ComponentAvailability.Available, ActionAllowed = true }];
    }

    [Fact]
    public void ChannelOptions_OfferNoAddons_InBothLanguages_AndEveryOptionIsAChannel()
    {
        Assert.Equal("NoAddons", OptionLists.ReShadeChannel.Value("No Addons"));
        Assert.Equal("NoAddons", OptionLists.ReShadeChannel.Value("Sem add-ons"));
        foreach (var value in OptionLists.ReShadeChannel.Values)
            Assert.True(Enum.TryParse<ReShadeChannel>(value, true, out _), value);
        foreach (var channel in Enum.GetValues<ReShadeChannel>())
            Assert.Contains(channel.ToString(), OptionLists.ReShadeChannel.Values);
    }

    // A real (offline) Addon Manager, so a missing guard shows as a different answer, not as a null reference.
    private static LinuxManagedComponentService Managed(LinuxDeploymentRecordStore records, string root)
    {
        var http = new HttpClient(new Handler { HomeUnreachable = true });
        var addonManager = new ReShadeAddonManagerService(http, new UpstreamRhiManifestService(http), Path.Combine(root, "addon-cache"),
            Path.Combine(root, "custom-addons"), Path.Combine(root, "addon-manager.json"));
        return Managed(records, addonManager);
    }

    private static LinuxManagedComponentService Managed(LinuxDeploymentRecordStore records, ReShadeAddonManagerService addonManager)
        => new(records, reshade: null!, addonManager: addonManager, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);

    private (DetectedGame Game, LinuxDeploymentRecordStore Store) CreateGame(string name)
    {
        var bin = Path.Combine(_root, name, "game", "bin");
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "Game " + name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12
        };
        return (game, new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json")));
    }

    // One cache for every service in a test, as in the app: the outage test needs the add-on build seeded there.
    private ReShadeDeploymentService NewService(LinuxDeploymentRecordStore store, Handler handler)
    {
        var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "cache");
        return new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(),
            new MarkingExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string AddonVersion { get; set; } = "6.8.0";
        public string NoAddonsVersion { get; set; } = "6.8.0";
        public bool HomeUnreachable { get; set; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri.AbsoluteUri);
            if (uri.Host == "reshade.me" && uri.AbsolutePath == "/")
            {
                if (HomeUnreachable) throw new HttpRequestException("Connection refused");
                // reshade.me lists both builds, the add-on one first.
                var html = $"<a href=\"/downloads/ReShade_Setup_{AddonVersion}_Addon.exe\">Add-on</a> <a href=\"/downloads/ReShade_Setup_{NoAddonsVersion}.exe\">Standard</a>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            if (HomeUnreachable) throw new HttpRequestException("Connection refused");
            var setup = new byte[600_000];
            setup[0] = (byte)'M';
            setup[1] = (byte)'Z';
            Encoding.ASCII.GetBytes(uri.AbsolutePath.Contains("_Addon", StringComparison.Ordinal) ? "ADDON-BUILD" : "NO-ADDON-BUILD").CopyTo(setup, 1024);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup) });
        }
    }

    // Writes DLLs that carry the build marker of the setup they were extracted from.
    private sealed class MarkingExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var marker = File.ReadAllText(setupExe, Encoding.Latin1).Contains("NO-ADDON-BUILD", StringComparison.Ordinal) ? "NO-ADDON-BUILD" : "ADDON-BUILD";
            Directory.CreateDirectory(outputDirectory);
            Write(Path.Combine(outputDirectory, "ReShade64.dll"), 0x8664, marker);
            Write(Path.Combine(outputDirectory, "ReShade32.dll"), 0x014c, marker);
            return Task.CompletedTask;
        }

        private static void Write(string path, ushort machine, string marker)
        {
            PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = 512;
            stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me " + marker));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
