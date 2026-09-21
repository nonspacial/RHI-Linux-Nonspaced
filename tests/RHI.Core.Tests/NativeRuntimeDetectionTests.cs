using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class NativeRuntimeDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-native-tests-" + Guid.NewGuid().ToString("N"));

    public NativeRuntimeDetectionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void FindLikelyNativeExecutable_PrefersTerrariaNativeBinaryOverWindowsExe()
    {
        var windows = Path.Combine(_root, "Terraria.exe");
        File.WriteAllBytes(windows, Encoding.ASCII.GetBytes("MZ....d3d9.dll"));
        var native = Path.Combine(_root, "Terraria.bin.x86_64");
        WriteElf(native, "libvulkan.so");

        var index = GameFileIndexer.Build(_root);
        var found = DetectionHelpers.FindLikelyNativeExecutable(_root, index);

        Assert.Equal(native, found);
    }

    [Fact]
    public void FindLikelyExecutable_DoesNotSelectBusyboxHelper()
    {
        var helperDir = Path.Combine(_root, "LaunchUtils");
        Directory.CreateDirectory(helperDir);
        File.WriteAllText(Path.Combine(helperDir, "busybox64.exe"), "d3d12.dll d3d11.dll d3d9.dll vulkan-1.dll opengl32.dll");

        var found = DetectionHelpers.FindLikelyExecutable(_root, GameFileIndexer.Build(_root));

        Assert.Null(found);
    }

    [Fact]
    public void Detect_NativeFnaReportsVulkanAndOpenGlWithoutInventingPrimary()
    {
        var native = Path.Combine(_root, "Terraria.bin.x86_64");
        WriteElf(native, "no renderer import in launcher");
        File.WriteAllText(Path.Combine(_root, "FNA.dll"), "framework");
        var index = GameFileIndexer.Build(_root);

        var found = GraphicsApiDetector.Detect(_root, native, index);

        Assert.Equal(GraphicsApi.Unknown, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
        Assert.Contains(GraphicsApi.Vulkan, found.Candidates);
        Assert.Contains(GraphicsApi.OpenGL, found.Candidates);
        Assert.Contains("FNA", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_UnityPlayerLogUsesActualDirectX11Runtime()
    {
        var exe = Path.Combine(_root, "TheWeirdHunter.exe");
        File.WriteAllText(exe, "MZ dynamic graphics backend");
        File.WriteAllText(Path.Combine(_root, "UnityPlayer.dll"), "renderer loaded dynamically");
        var prefix = Path.Combine(_root, "prefix");
        var logDir = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "LocalLow", "UMO STUDIO", "The Weird Hunter");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "Player.log"), "GfxDevice: creating device client\nDirect3D:\nVersion: Direct3D 11.0 [level 11.1]\n");
        var index = GameFileIndexer.Build(_root);

        var found = GraphicsApiDetector.Detect(_root, exe, index, prefix);

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
        Assert.Contains("Player.log", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_SharedPrefixIgnoresUnrelatedUnityPlayerLog()
    {
        var gameRoot = Path.Combine(_root, "TargetGame");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "TargetGame.exe");
        File.WriteAllText(exe, "MZ dynamic graphics backend");
        File.WriteAllText(Path.Combine(gameRoot, "UnityPlayer.dll"), "renderer loaded dynamically");
        var prefix = Path.Combine(_root, "shared-prefix");
        var unrelated = Path.Combine(prefix, "drive_c", "users", "user", "AppData", "LocalLow", "Other Studio", "Other Game");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "Player.log"), "Version: Direct3D 12.0");
        var index = GameFileIndexer.Build(gameRoot);

        var found = GraphicsApiDetector.Detect(gameRoot, exe, index, prefix);

        Assert.NotEqual(GraphicsApi.DirectX12, found.Primary);
        Assert.DoesNotContain("Runtime renderer log", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_AmbiguousNearbyEngineDoesNotPromoteHighestApi()
    {
        var exe = Path.Combine(_root, "Game.exe");
        File.WriteAllText(exe, "MZ no renderer import");
        var engine = Path.Combine(_root, "engine.dll");
        File.WriteAllText(engine, "d3d12.dll d3d11.dll d3d9.dll vulkan-1.dll opengl32.dll");
        var index = GameFileIndexer.Build(_root);

        var found = GraphicsApiDetector.Detect(_root, exe, index);

        Assert.Equal(GraphicsApi.Unknown, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Low, found.Confidence);
        Assert.True(found.Candidates.Count >= 3);
        Assert.Contains("Ambiguous", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComponentProbe_MarksWindowsDllBackendsUnsupportedForNativeLinuxGame()
    {
        var native = Path.Combine(_root, "Game.x86_64");
        WriteElf(native, "libGL.so");
        var game = new DetectedGame
        {
            Name = "Native Game",
            InstallPath = _root,
            ExecutablePath = native,
            Runner = "Native Linux"
        };

        var components = ComponentProbe.Probe(game, GameFileIndexer.Build(_root));

        Assert.NotEmpty(components);
        Assert.All(components, c => Assert.Equal(ComponentAvailability.Unsupported, c.Availability));
    }


    [Fact]
    public async Task RuntimeProbe_ForcedX64ReselectsAmbiguousWindowsExecutable()
    {
        var x86 = Path.Combine(_root, "BaldursGate3.exe");
        var x64Dir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(x64Dir);
        var x64 = Path.Combine(x64Dir, "bg3_dx11.exe");
        PeArchitectureDetectorTests.WritePe(x86, 0x014c, 4096);
        PeArchitectureDetectorTests.WritePe(x64, 0x8664, 2048);

        const string manifestJson = """
        {
          "version": 2,
          "engineHintOverrides": {
            "Baldur's Gate 3": "Larian Engine"
          },
          "engineIniPathOverrides": {
            "Baldur's Gate 3": "BG3"
          },
          "sixtyFourBitGames": [ "Baldur's Gate 3" ]
        }
        """;
        using var http = new HttpClient(new StaticManifestHandler(manifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var probe = new LinuxGameRuntimeProbe(manifest);
        var game = new DetectedGame
        {
            Name = "Baldur's Gate 3",
            InstallPath = _root,
            ExecutablePath = x86,
            Runner = "Proton"
        };
        var index = GameFileIndexer.Build(_root);

        await probe.ProbeAsync(game, index);

        Assert.Equal(PeArchitecture.X64, game.ForcedArchitecture);
        Assert.Equal(x64, game.ExecutablePath);
        Assert.Equal(PeArchitecture.X64, PeArchitectureDetector.Detect(game.ExecutablePath));
        Assert.Equal("Larian Engine", game.EngineHint);
        Assert.Equal("BG3", game.EngineIniProjectOverride);
    }

    [Fact]
    public async Task RuntimeProbe_HeroicShortTitleUsesPathIdentityForDofFixForceRule()
    {
        var gameRoot = Path.Combine(_root, "Clair Obscur. Expedition 33 (2025)", "Expedition 33");
        var binaryDir = Path.Combine(gameRoot, "Sandfall", "Binaries", "Win64");
        Directory.CreateDirectory(binaryDir);
        var exe = Path.Combine(binaryDir, "SandFall-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 2048);

        const string manifestJson = """
        {
          "version": 2,
          "dofFixForceGames": [ "Clair Obscur: Expedition 33" ]
        }
        """;
        using var http = new HttpClient(new StaticManifestHandler(manifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var probe = new LinuxGameRuntimeProbe(manifest);
        var game = new DetectedGame
        {
            Name = "Expedition 33",
            InstallPath = gameRoot,
            BinaryDirectory = binaryDir,
            ExecutablePath = exe,
            Runner = "Heroic Wine"
        };

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameRoot));

        Assert.True(game.DofFixForcedByManifest);
        Assert.True(DofFixDeploymentService.IsGameEligible(game));
        Assert.Contains("force", DofFixDeploymentService.EligibilityReason(game), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeProbe_ReChunkSignatureDetectsReEngineOffline()
    {
        var gameRoot = Path.Combine(_root, "REEngineLocal");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 2048);
        File.WriteAllBytes(Path.Combine(gameRoot, "re_chunk_000.pak"), [1, 2, 3, 4]);
        var game = new DetectedGame
        {
            Name = "Local RE Engine Game",
            InstallPath = gameRoot,
            ExecutablePath = exe,
            Runner = "Proton"
        };
        var probe = new LinuxGameRuntimeProbe();

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameRoot));

        Assert.Equal("RE Engine", game.EngineHint);
        Assert.True(REFrameworkDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public async Task RuntimeProbe_EngineOverrideEnablesReFrameworkEligibility()
    {
        var gameRoot = Path.Combine(_root, "Street Fighter 6");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "StreetFighter6.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 2048);
        const string manifestJson = """
        {
          "version": 2,
          "engineOverrides": { "Street Fighter™ 6": "RE Engine" },
          "sixtyFourBitGames": [ "Street Fighter™ 6" ]
        }
        """;
        using var http = new HttpClient(new StaticManifestHandler(manifestJson));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var probe = new LinuxGameRuntimeProbe(manifest);
        var game = new DetectedGame
        {
            Name = "Street Fighter™ 6",
            InstallPath = gameRoot,
            ExecutablePath = exe,
            Runner = "Proton"
        };

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameRoot));

        Assert.Equal("RE Engine", game.EngineHint);
        Assert.Equal(PeArchitecture.X64, game.ForcedArchitecture);
        Assert.True(REFrameworkDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public async Task RuntimeProbe_ShippingExeUnderGameBinariesDetectsUnrealOffline()
    {
        var gameRoot = Path.Combine(_root, "ExpeditionStyle");
        var bin = Path.Combine(gameRoot, "Sandfall", "Binaries", "Win64");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "SandFall-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 2048);
        var game = new DetectedGame
        {
            Name = "Expedition Style",
            InstallPath = bin,
            ExecutablePath = exe,
            Runner = "Wine",
            GraphicsApi = GraphicsApi.DirectX12,
            UserGraphicsApiOverride = GraphicsApi.DirectX11
        };
        var probe = new LinuxGameRuntimeProbe();

        await probe.ProbeAsync(game, GameFileIndexer.Build(bin));

        Assert.Equal("Unreal Engine", game.EngineHint);
        Assert.True(LumaFrameworkDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public async Task RuntimeProbe_UnrealBuildVersionRefinesLocalEngineHint()
    {
        var gameRoot = Path.Combine(_root, "VersionedUnreal");
        var bin = Path.Combine(gameRoot, "Project", "Binaries", "Win64");
        var buildDir = Path.Combine(gameRoot, "Engine", "Build");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(buildDir);
        var exe = Path.Combine(bin, "Project-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 2048);
        File.WriteAllText(Path.Combine(buildDir, "Build.version"),
            "{\"MajorVersion\":5,\"MinorVersion\":4,\"PatchVersion\":3}");
        var game = new DetectedGame
        {
            Name = "Versioned Unreal",
            InstallPath = bin,
            ExecutablePath = exe,
            Runner = "Proton"
        };
        var probe = new LinuxGameRuntimeProbe();

        await probe.ProbeAsync(game, GameFileIndexer.Build(bin));

        Assert.Equal("Unreal Engine 5.4.3", game.EngineHint);
    }

    [Fact]
    public async Task RuntimeProbe_ManifestFailureFallsBackToLocalDetection()
    {
        var exe = Path.Combine(_root, "OfflineGame.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 2048);
        using var http = new HttpClient(new StatusManifestHandler(System.Net.HttpStatusCode.ServiceUnavailable));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/offline-manifest.json");
        var probe = new LinuxGameRuntimeProbe(manifest);
        var game = new DetectedGame
        {
            Name = "Offline Game",
            InstallPath = _root,
            ExecutablePath = exe,
            Runner = "Proton"
        };

        await probe.ProbeAsync(game, GameFileIndexer.Build(_root));

        Assert.Equal(exe, game.ExecutablePath);
        Assert.Null(game.ForcedArchitecture);
    }

    private static void WriteElf(string path, string payload)
    {
        using var stream = File.Create(path);
        stream.Write([0x7f, (byte)'E', (byte)'L', (byte)'F']);
        stream.Write(Encoding.ASCII.GetBytes(payload));
        if (OperatingSystem.IsLinux())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                         UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                         UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { }
        }
    }

    private sealed class StatusManifestHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class StaticManifestHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
