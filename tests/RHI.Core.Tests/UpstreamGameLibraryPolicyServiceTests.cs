using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class UpstreamGameLibraryPolicyServiceTests
{
    [Fact]
    public async Task ApplyAsync_FiltersExactAndPrefixBlacklist()
    {
        var json = """
        {
          "version": 3,
          "blacklist": ["3DMark"],
          "blacklistPrefixes": ["Battle.net."]
        }
        """;
        using var http = Http(json);
        var policy = new UpstreamGameLibraryPolicyService(
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"));
        using var root = new TempTree();
        var games = new[]
        {
            Game("3DMark", root.Dir("3dmark")),
            Game("Battle.net.17477", root.Dir("bnet")),
            Game("Clair Obscur: Expedition 33", root.Dir("exp33"))
        };

        var result = await policy.ApplyAsync(games);

        var remaining = Assert.Single(result);
        Assert.Equal("Clair Obscur: Expedition 33", remaining.Name);
    }

    [Fact]
    public async Task ApplyAsync_BlacklistNeverMatchesPathEvidence()
    {
        // "Windows" is a real upstream blacklist entry. A library mounted under a path containing
        // that word (dual-boot NTFS at /mnt/windows) must not make every unlisted game vanish.
        var json = """
        {
          "version": 3,
          "blacklist": ["Windows", "Blender"]
        }
        """;
        using var http = Http(json);
        var policy = new UpstreamGameLibraryPolicyService(
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"));
        using var root = new TempTree();
        var windowsLib = root.Dir(Path.Combine("Windows", "SteamLibrary", "steamapps", "common", "Some Game"));
        var blenderDir = root.Dir(Path.Combine("Blender", "Other Game"));
        var games = new[]
        {
            Game("Some Game", windowsLib),
            Game("Other Game", blenderDir),
            Game("Windows", root.Dir("win"))
        };

        var result = await policy.ApplyAsync(games);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, g => g.Name == "Some Game");
        Assert.Contains(result, g => g.Name == "Other Game");
        Assert.DoesNotContain(result, g => g.Name == "Windows");
    }

    [Fact]
    public async Task Manifest_ReadsPrefixedEngineOverridesKey()
    {
        var json = """
        {
          "version": 3,
          "_engineOverrides": { "CrossCode": "NW.js" }
        }
        """;
        using var http = Http(json);
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        var game = Game("CrossCode", Path.GetTempPath());

        var resolved = await manifest.ResolveGameOverrideAsync(game);

        Assert.NotNull(resolved);
        Assert.Equal("NW.js", resolved!.EngineOverride ?? resolved.EngineHint);
    }

    [Fact]
    public async Task ApplyAsync_SplitsBundleAndThenAppliesChildPolicies()
    {
        var json = """
        {
          "version": 3,
          "splitGames": {
            "Mass Effect Legendary Edition": [
              { "name": "Mass Effect 1 (Legendary Edition)", "subPath": "Game/ME1/Binaries/Win64" },
              { "name": "Mass Effect 2 (Legendary Edition)", "subPath": "Game/ME2/Binaries/Win64" }
            ]
          },
          "wikiNameOverrides": {
            "Mass Effect 1 (Legendary Edition)": "Mass Effect : Legendary Edition"
          }
        }
        """;
        using var http = Http(json);
        var policy = new UpstreamGameLibraryPolicyService(
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"));
        using var root = new TempTree();
        var bundle = root.Dir("Mass Effect Legendary Edition");
        Directory.CreateDirectory(Path.Combine(bundle, "Game", "ME1", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(bundle, "Game", "ME2", "Binaries", "Win64"));

        var result = await policy.ApplyAsync([Game("Mass Effect Legendary Edition", bundle)]);

        Assert.Equal(2, result.Count);
        var me1 = result.Single(x => x.Name.StartsWith("Mass Effect 1", StringComparison.Ordinal));
        Assert.Equal("Mass Effect : Legendary Edition", me1.WikiNameOverride);
        Assert.Equal(bundle, me1.ManifestInstallRoot);
        Assert.EndsWith(Path.Combine("Game", "ME1", "Binaries", "Win64"), me1.InstallPath);
    }

    [Fact]
    public async Task ApplyAsync_AppliesInstallPathLaunchAndCrossPlatformMetadata()
    {
        var json = """
        {
          "version": 4,
          "wikiNameOverrides": { "Cyberpunk 2077": "Cyberpunk 2077 Ultimate" },
          "installPathOverrides": { "Cyberpunk 2077": "bin\\x64" },
          "steamAppIdOverrides": { "Cyberpunk 2077": 1091500 },
          "launchExeOverrides": { "Cyberpunk 2077": "Cyberpunk2077.exe" },
          "wikiUnlinks": ["Cyberpunk 2077"],
          "dlssSkipGames": ["Cyberpunk 2077"],
          "ueExtendedCompatibility": { "Cyberpunk 2077": { "hdr": false, "lut": true } },
          "engineIniFiles": { "Cyberpunk 2077": "cyberpunk-hdr.ini" },
          "dllNameOverrides": { "Cyberpunk 2077": { "reshade": "winmm.dll", "dc": "version.dll" } },
          "optiScalerDllOverrides": { "Cyberpunk 2077": "dxgi.dll" },
          "pdUpscalerGames": { "Cyberpunk 2077": "RE4" },
          "nexusUrlOverrides": { "Cyberpunk 2077": "https://nexus.test/cp2077" },
          "pcgwUrlOverrides": { "Cyberpunk 2077": "https://pcgw.test/cp2077" },
          "uwFixUrlOverrides": { "Cyberpunk 2077": "https://uw.test/cp2077" },
          "ultraPlusUrlOverrides": { "Cyberpunk 2077": "https://ultra.test/cp2077" },
          "wikiStatusOverrides": { "Cyberpunk 2077": "✅" },
          "authorOverrides": { "Cyberpunk 2077": "Mod Author" },
          "installWarnings": { "Cyberpunk 2077": { "reshade": "Test warning" } },
          "gameNotes": { "Cyberpunk 2077": { "notes": "Manifest note", "notesUrl": "https://notes.test/cp2077", "notesUrlLabel": "Read" } },
          "forceExternalOnly": { "Cyberpunk 2077": { "url": "https://external.test/mod", "label": "Open Mod" } }
        }
        """;
        using var http = Http(json);
        var policy = new UpstreamGameLibraryPolicyService(
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"));
        using var root = new TempTree();
        var gameRoot = root.Dir("Cyberpunk 2077");
        var bin = Path.Combine(gameRoot, "bin", "x64");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Cyberpunk2077.exe");
        await File.WriteAllBytesAsync(exe, [0x4d, 0x5a]);
        var game = Game("Cyberpunk 2077", gameRoot);

        var applied = Assert.Single(await policy.ApplyAsync([game]));

        Assert.Equal(gameRoot, applied.ManifestInstallRoot);
        Assert.Equal(bin, applied.InstallPath);
        Assert.Equal(exe, applied.ExecutablePath);
        Assert.Equal(bin, applied.BinaryDirectory);
        Assert.Null(applied.SteamAppId);
        Assert.Equal(1091500, applied.ManifestSteamAppIdOverride);
        Assert.Equal("Cyberpunk 2077 Ultimate", applied.WikiNameOverride);
        Assert.True(applied.WikiUnlinkedByManifest);
        Assert.True(applied.DlssScanSkippedByManifest);
        Assert.True(applied.UeExtendedByManifest);
        Assert.True(applied.NativeHdrByManifest);
        Assert.Equal("winmm.dll", applied.ReShadeDllNameOverride);
        Assert.Equal("version.dll", applied.DisplayCommanderDllNameOverride);
        Assert.Equal("dxgi.dll", applied.OptiScalerDllNameOverride);
        Assert.Equal("RE4", applied.PdUpscalerArtifact);
        Assert.Equal("Open Mod", applied.ExternalModLabel);
        Assert.Equal("https://external.test/mod", applied.ExternalModUrl);
        Assert.Equal("Test warning", applied.ManifestInstallWarnings["RESHade"]);
        Assert.Equal("Manifest note", applied.ManifestGameNote);
        Assert.Equal("https://notes.test/cp2077", applied.ManifestGameNoteUrl);
        Assert.Equal("Read", applied.ManifestGameNoteUrlLabel);
        Assert.Equal(false, applied.UeExtendedHdrByManifest);
        Assert.Equal(true, applied.UeExtendedLutByManifest);
        Assert.Equal("cyberpunk-hdr.ini", applied.EngineIniFileOverride);
        Assert.Equal("✅", applied.WikiStatusOverride);
        Assert.Equal("Mod Author", applied.AuthorOverride);
    }

    [Fact]
    public async Task ApplyAsync_SteamAppIdOverrideDoesNotReclassifyNonSteamLauncher()
    {
        var json = """
        { "version": 2, "steamAppIdOverrides": { "Shared Game": 4242 } }
        """;
        using var http = Http(json);
        var policy = new UpstreamGameLibraryPolicyService(
            new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"));
        using var root = new TempTree();
        var heroic = Game("Shared Game", root.Dir("heroic"));
        heroic.Source = "Heroic";
        var steam = Game("Shared Game", root.Dir("steam"));
        steam.Source = "Steam";
        steam.SteamAppId = 1;

        var result = await policy.ApplyAsync([heroic, steam]);
        var heroicResult = result.Single(x => x.Source == "Heroic");
        var steamResult = result.Single(x => x.Source == "Steam");

        Assert.Null(heroicResult.SteamAppId);
        Assert.Equal(4242, heroicResult.ManifestSteamAppIdOverride);
        Assert.Equal(4242, steamResult.SteamAppId);
        Assert.Equal(4242, steamResult.ManifestSteamAppIdOverride);
    }

    [Fact]
    public async Task ApplyAsync_UsesAllManifestGameFieldsForCanonicalPathIdentity()
    {
        var json = """
        {
          "version": 5,
          "engineOverrides": { "Clair Obscur: Expedition 33": "Unreal" },
          "graphicsApiOverrides": { "Clair Obscur: Expedition 33": "DX12, VLK" }
        }
        """;
        using var http = Http(json);
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json");
        var policy = new UpstreamGameLibraryPolicyService(manifest);
        using var root = new TempTree();
        var install = root.Dir("Clair Obscur - Expedition 33");
        var game = Game("Expedition 33", install);

        var applied = Assert.Single(await policy.ApplyAsync([game]));
        Assert.Equal("Clair Obscur: Expedition 33", applied.CanonicalManifestName);

        var runtime = await manifest.ResolveGameOverrideAsync(applied);
        Assert.NotNull(runtime);
        Assert.Equal("Unreal", runtime!.EngineOverride);
        Assert.Equal(RHI.Core.Models.GraphicsApi.DirectX12, runtime.GraphicsApiOverride);
        Assert.Equal(new[] { RHI.Core.Models.GraphicsApi.DirectX12, RHI.Core.Models.GraphicsApi.Vulkan }, runtime.GraphicsApiOverrides);
    }

    [Fact]
    public async Task GameLibraryScanner_PolicyFailureIsFailOpen()
    {
        using var root = new TempTree();
        var game = Game("Local Game", root.Dir("game"));
        var scanner = new GameLibraryScanner([new StaticDetector(game)], [new ThrowingPolicy()]);

        var result = await scanner.ScanAsync();

        Assert.Single(result);
        Assert.Equal("Local Game", result[0].Name);
    }

    private static HttpClient Http(string json)
        => new(new JsonHandler(json));

    private static DetectedGame Game(string name, string path)
        => new() { Name = name, InstallPath = path, Source = "Test", Runner = "Wine" };

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StaticDetector(params DetectedGame[] games) : IGameDetector
    {
        public string Name => "test";
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedGame>>(games);
    }

    private sealed class ThrowingPolicy : IGameLibraryPolicy
    {
        public Task<IReadOnlyList<DetectedGame>> ApplyAsync(IReadOnlyList<DetectedGame> games, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("offline");
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rhi-policy-" + Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(Root);
        public string Dir(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
