using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ReShadeChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-channel-tests-" + Guid.NewGuid().ToString("N"));

    public ReShadeChannelTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CustomChannel_InstallsMatchingArchitectureAndPersistsProfile()
    {
        var (game, store) = CreateGame("custom");
        var custom = Path.Combine(_root, "custom", "MyReShade.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        PeArchitectureDetectorTests.WritePe(custom, 0x8664, 1_100_000);
        StampReShadeSignature(custom);
        var service = NewService(store, new MutableHandler(), Path.Combine(_root, "custom-cache"));
        var options = new ReShadeInstallOptions
        {
            Channel = ReShadeChannel.Custom,
            CustomDllPath = custom,
            ManageIni = false
        };

        var installed = await service.InstallAsync(game, options);

        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        var persisted = service.GetInstalledOptions(game);
        Assert.Equal(ReShadeChannel.Custom, persisted.Channel);
        Assert.Equal(Path.GetFullPath(custom), persisted.CustomDllPath);
        Assert.False(persisted.ManageIni);
    }

    [Fact]
    public async Task CustomChannel_RejectsArbitraryPeDllWithoutReShadeSignature()
    {
        var (game, store) = CreateGame("custom-arbitrary");
        var custom = Path.Combine(_root, "custom-arbitrary", "Unknown.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        PeArchitectureDetectorTests.WritePe(custom, 0x8664, 1_100_000);
        var service = NewService(store, new MutableHandler(), Path.Combine(_root, "arbitrary-cache"));

        var result = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            Channel = ReShadeChannel.Custom,
            CustomDllPath = custom
        });

        Assert.False(result.Success);
        Assert.Contains("recognizable ReShade signature", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
    }

    [Fact]
    public async Task CustomChannel_BlocksArchitectureMismatch()
    {
        var (game, store) = CreateGame("custom-mismatch");
        var custom = Path.Combine(_root, "custom-mismatch", "ReShade32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        PeArchitectureDetectorTests.WritePe(custom, 0x014c, 1_100_000);
        StampReShadeSignature(custom);
        var service = NewService(store, new MutableHandler(), Path.Combine(_root, "mismatch-cache"));

        var result = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            Channel = ReShadeChannel.Custom,
            CustomDllPath = custom
        });

        Assert.False(result.Success);
        Assert.Contains("architecture mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
    }

    [Fact]
    public async Task LegacyChannel_InvalidVersionFailsBeforeDownload()
    {
        var (game, store) = CreateGame("legacy-invalid");
        var handler = new MutableHandler();
        var service = NewService(store, handler, Path.Combine(_root, "legacy-invalid-cache"));

        var result = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            Channel = ReShadeChannel.Legacy,
            LegacyVersion = "../../bad"
        });

        Assert.False(result.Success);
        Assert.Contains("explicit numeric", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task StableChannel_CheckUpdateDetectsNewVersion()
    {
        var (game, store) = CreateGame("stable-update");
        var handler = new MutableHandler { StableVersion = "9.9.9" };
        var service = NewService(store, handler, Path.Combine(_root, "stable-update-cache"));
        var installed = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(installed.Success, installed.Message);

        handler.StableVersion = "10.0.0";
        var status = await service.CheckUpdateAsync(game);

        Assert.True(status.IsInstalled);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("9.9.9", status.InstalledVersion);
        Assert.Equal("10.0.0", status.LatestVersion);
    }

    [Fact]
    public async Task CustomChannel_AfterSwitchFromStable_ReportsNoFalseUpdate()
    {
        // Upstream 2.7.9: a game moved to Custom kept flagging Stable updates. Here the channel
        // lives on the record and is rewritten by the apply that switches it.
        var (game, store) = CreateGame("custom-switch");
        var handler = new MutableHandler { StableVersion = "9.9.9" };
        var service = NewService(store, handler, Path.Combine(_root, "custom-switch-cache"));
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var custom = Path.Combine(_root, "custom-switch", "MyReShade.dll");
        PeArchitectureDetectorTests.WritePe(custom, 0x8664, 1_100_000);
        StampReShadeSignature(custom);
        var switched = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            Channel = ReShadeChannel.Custom,
            CustomDllPath = custom,
            ManageIni = false
        });
        Assert.True(switched.Success, switched.Message);

        handler.StableVersion = "10.0.0";
        var requestsBefore = handler.RequestCount;
        var status = await service.CheckUpdateAsync(game);

        Assert.True(status.IsInstalled);
        Assert.False(status.UpdateAvailable, status.Message);
        Assert.Equal(ReShadeChannel.Custom, status.Channel);
        Assert.Equal(requestsBefore, handler.RequestCount);
    }

    [Fact]
    public async Task ShaderSelection_IsPerGameWithNoGlobalFallback()
    {
        // Upstream 2.7.9 fixed a global shader "Off" overriding per-game picks. RHI-Linux has no
        // global shader mode: each game's selection lives on its own record and deploys as-is.
        var (game, store) = CreateGame("per-game-shaders");
        var compiler = Path.Combine(_root, "per-game-native", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(compiler, 0x8664);
        var cache = Path.Combine(_root, "per-game-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var service = NewService(store, new MutableHandler(), cache);

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            ManageIni = false,
            D3DCompiler47Path = compiler,
            ShaderPackIds = ["CrosireMaster"]
        });

        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "reshade-shaders", "RHI", "CrosireMaster", "Shaders", "Test.fx")));
        Assert.Equal(["CrosireMaster"], service.GetInstalledOptions(game).ShaderPackIds);
        var other = CreateGame("per-game-other").Game;
        other.SteamAppId = 4343;
        Assert.Empty(service.GetInstalledOptions(other).ShaderPackIds);
    }

    [Fact]
    public async Task NightlyChannel_StagesArchitectureSpecificArtifact()
    {
        var handler = new MutableHandler { NightlyPayload = CreateNightlyZip("ReShade64.dll") };
        var service = NewService(
            new LinuxDeploymentRecordStore(Path.Combine(_root, "nightly-records.json")),
            handler,
            Path.Combine(_root, "nightly-cache"));

        var staged = await service.EnsureNightlyStagedAsync(PeArchitecture.X64);

        Assert.Equal(ReShadeChannel.Nightly, staged.Channel);
        Assert.Equal("Nightly", staged.Version);
        Assert.True(File.Exists(staged.DllPath));
        Assert.True(new FileInfo(staged.DllPath).Length > 1_000_000);
        Assert.Contains("zip-size:", staged.SourceIdentity);
    }

    [Fact]
    public async Task ManagedUpdate_RhiCreatedProxyDoesNotCreateBogusOriginalBackup()
    {
        var (game, store) = CreateGame("standalone-update");
        var handler = new MutableHandler { StableVersion = "9.9.9" };
        var service = NewService(store, handler, Path.Combine(_root, "standalone-update-cache"));
        var first = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(first.Success, first.Message);
        var target = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        Assert.False(File.Exists(target + TransactionalFileDeploymentService.BackupExtension));

        handler.StableVersion = "10.0.0";
        var second = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(second.Success, second.Message);
        Assert.False(File.Exists(target + TransactionalFileDeploymentService.BackupExtension));
        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + TransactionalFileDeploymentService.BackupExtension));
    }

    [Fact]
    public async Task ManagedUpdate_BlocksDllModifiedOutsideRhi()
    {
        var (game, store) = CreateGame("modified-update");
        var handler = new MutableHandler { StableVersion = "9.9.9" };
        var service = NewService(store, handler, Path.Combine(_root, "modified-update-cache"));
        var first = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(first.Success, first.Message);
        var target = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 700;
            stream.WriteByte(0x5A);
        }

        handler.StableVersion = "10.0.0";
        var update = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.False(update.Success);
        Assert.Contains("modified outside RHI", update.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("9.9.9", store.Find(game, ManagedComponentKind.ReShade)?.Version);
        Assert.False(File.Exists(target + TransactionalFileDeploymentService.BackupExtension));
    }

    [Fact]
    public async Task ManagedOptiScaler_ReShadeUpdateKeepsReShade64Target()
    {
        var (game, store) = CreateGame("coexist-update");
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        WriteSignedTextFile(dxgi, "OptiScaler proxy");
        var optiIni = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        File.WriteAllText(optiIni, "[Plugins]\nLoadReshade=auto\n");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = game.BinaryDirectory!,
            Files =
            [
                new LinuxDeployedFile { Path = dxgi, Sha256 = Sha256(dxgi) },
                new LinuxDeployedFile { Path = optiIni, Sha256 = Sha256(optiIni), AllowUserChanges = true }
            ]
        });
        var handler = new MutableHandler { StableVersion = "9.9.9" };
        var service = NewService(store, handler, Path.Combine(_root, "coexist-update-cache"));

        var first = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(first.Success, first.Message);
        var coexistPath = Path.Combine(game.BinaryDirectory!, "ReShade64.dll");
        Assert.True(File.Exists(coexistPath));
        var dxgiBefore = Sha256(dxgi);

        handler.StableVersion = "10.0.0";
        var second = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(second.Success, second.Message);
        Assert.True(File.Exists(coexistPath));
        Assert.Equal(dxgiBefore, Sha256(dxgi));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll.original")));
        Assert.False(File.Exists(coexistPath + TransactionalFileDeploymentService.BackupExtension));
        var record = store.Find(game, ManagedComponentKind.ReShade);
        Assert.Equal("10.0.0", record?.Version);
        Assert.Contains(record!.Files, f => Path.GetFullPath(f.Path) == Path.GetFullPath(coexistPath));
    }

    [Fact]
    public async Task ShaderPacks_WithNativeCompilerInPrefix_SetsAndRestoresPerGameOverride()
    {
        var (game, store) = CreateGame("shader-prefix-compiler");
        var compiler = Path.Combine(game.PrefixPath!, "drive_c", "windows", "system32", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(compiler, 0x8664);
        var cache = Path.Combine(_root, "shader-prefix-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var service = NewService(store, new MutableHandler(), cache);

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            ManageIni = false,
            ShaderPackIds = ["CrosireMaster"]
        });

        Assert.True(installed.Success, installed.Message);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "D3DCompiler_47.dll")));
        var registry = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(registry, "d3dcompiler_47", "Game.exe"));
        var record = store.Find(game, ManagedComponentKind.ReShade);
        Assert.NotNull(record);
        Assert.Contains(record!.AdditionalWineOverrides, item => item.DllName.Equals("d3dcompiler_47", StringComparison.OrdinalIgnoreCase));

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        Assert.True(File.Exists(compiler));
        registry = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Null(WineDllOverrideService.TryReadOverride(registry, "d3dcompiler_47", "Game.exe"));
        Assert.Null(WineDllOverrideService.TryReadOverride(registry, "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task ShaderPacks_MissingNativeCompiler_BlocksBeforeReShadeDownload()
    {
        var (game, store) = CreateGame("shader-missing-compiler");
        var handler = new MutableHandler();
        var service = NewService(store, handler, Path.Combine(_root, "shader-missing-cache"));

        var result = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            ManageIni = false,
            ShaderPackIds = ["CrosireMaster"]
        });

        Assert.False(result.Success);
        Assert.Contains("native Microsoft D3DCompiler_47.dll", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protontricks 4242 d3dcompiler_47", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ShaderPacks_SelectedMicrosoftCompiler_IsManagedAppLocalAndRemoved()
    {
        var (game, store) = CreateGame("shader-selected-compiler");
        var source = Path.Combine(_root, "native", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(source, 0x8664);
        var cache = Path.Combine(_root, "shader-selected-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var service = NewService(store, new MutableHandler(), cache);

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            ManageIni = false,
            D3DCompiler47Path = source,
            ShaderPackIds = ["CrosireMaster"]
        });

        Assert.True(installed.Success, installed.Message);
        var target = Path.Combine(game.BinaryDirectory!, "D3DCompiler_47.dll");
        Assert.True(File.Exists(target));
        var record = store.Find(game, ManagedComponentKind.ReShade);
        Assert.Contains(record!.Files, file => Path.GetFileName(file.Path).Equals("D3DCompiler_47.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(source, record.Metadata["ReShade.D3DCompiler47Source"]);
        var registry = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(registry, "d3dcompiler_47", "Game.exe"));

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(target));
        registry = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Null(WineDllOverrideService.TryReadOverride(registry, "d3dcompiler_47", "Game.exe"));
    }

    [Fact]
    public async Task SelectedMicrosoftCompiler_WithoutShaderPacks_IsAppliedAndPersisted()
    {
        var (game, store) = CreateGame("selected-compiler-no-packs");
        var source = Path.Combine(_root, "native-no-packs", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(source, 0x8664);
        var service = NewService(store, new MutableHandler(), Path.Combine(_root, "selected-compiler-no-packs-cache"));

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            ManageIni = false,
            D3DCompiler47Path = source
        });

        Assert.True(installed.Success, installed.Message);
        var profile = service.GetInstalledOptions(game);
        Assert.Equal(source, profile.D3DCompiler47Path);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "D3DCompiler_47.dll")));
        var registry = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(registry, "d3dcompiler_47", "Game.exe"));
    }

    [Fact]
    public async Task ShaderPacks_ExternalAppLocalMicrosoftCompiler_IsPreserved()
    {
        var (game, store) = CreateGame("shader-external-local-compiler");
        // winetricks/protontricks and many manual Linux setups use the lowercase filename.
        var target = Path.Combine(game.BinaryDirectory!, "d3dcompiler_47.dll");
        WriteNativeD3DCompiler(target, 0x8664);
        var originalHash = Sha256(target);
        var cache = Path.Combine(_root, "shader-external-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var service = NewService(store, new MutableHandler(), cache);

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions
        {
            ManageIni = false,
            ShaderPackIds = ["CrosireMaster"]
        });

        Assert.True(installed.Success, installed.Message);
        var record = store.Find(game, ManagedComponentKind.ReShade);
        Assert.DoesNotContain(record!.Files, file => Path.GetFileName(file.Path).Equals("D3DCompiler_47.dll", StringComparison.OrdinalIgnoreCase));

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        Assert.True(File.Exists(target));
        Assert.Equal(originalHash, Sha256(target));
    }

    [Fact]
    public async Task ManagedD3DCompilerModifiedOutsideRhi_BlocksProfileUpdate()
    {
        var (game, store) = CreateGame("shader-modified-compiler");
        var source = Path.Combine(_root, "native-modified", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(source, 0x8664);
        var cache = Path.Combine(_root, "shader-modified-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var service = NewService(store, new MutableHandler(), cache);
        var options = new ReShadeInstallOptions
        {
            ManageIni = false,
            D3DCompiler47Path = source,
            ShaderPackIds = ["CrosireMaster"]
        };
        var first = await service.InstallAsync(game, options);
        Assert.True(first.Success, first.Message);
        var target = Path.Combine(game.BinaryDirectory!, "D3DCompiler_47.dll");
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 900;
            stream.WriteByte(0x5A);
        }

        var second = await service.InstallAsync(game, options);

        Assert.False(second.Success);
        Assert.Contains("modified outside RHI", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public void NativeD3DCompilerProbe_RejectsWineLikePeWithoutMicrosoftCompilerStrings()
    {
        var path = Path.Combine(_root, "wine-like", "d3dcompiler_47.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PeArchitectureDetectorTests.WritePe(path, 0x8664, 1_100_000);

        Assert.False(ReShadeDeploymentService.LooksLikeNativeD3DCompiler(path, PeArchitecture.X64));

        WriteNativeD3DCompiler(path, 0x8664);
        Assert.True(ReShadeDeploymentService.LooksLikeNativeD3DCompiler(path, PeArchitecture.X64));
    }

    [Fact]
    public void LegacyRecordWithoutMetadata_DefaultsToStableProfile()
    {
        var (game, store) = CreateGame("old-record");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!,
            Version = "9.9.9",
            Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "dxgi.dll") }]
        });
        var service = NewService(store, new MutableHandler(), Path.Combine(_root, "old-record-cache"));

        var options = service.GetInstalledOptions(game);

        Assert.Equal(ReShadeChannel.Stable, options.Channel);
        Assert.Empty(options.ShaderPackIds);
    }

    private (DetectedGame Game, LinuxDeploymentRecordStore Store) CreateGame(string name)
    {
        var gameRoot = Path.Combine(_root, name, "game");
        var bin = Path.Combine(gameRoot, "bin");
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "Game",
            InstallPath = gameRoot,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX12,
            SteamAppId = 4242
        };
        return (game, new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json")));
    }

    private ReShadeDeploymentService NewService(LinuxDeploymentRecordStore store, MutableHandler handler, string cache)
    {
        var http = new HttpClient(handler);
        return new ReShadeDeploymentService(
            http,
            new TransactionalFileDeploymentService(),
            store,
            new WineDllOverrideService(),
            new FakeExtractor(),
            cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")));
    }

    private static void SeedShaderCache(string cache, string packId)
    {
        var pack = Path.Combine(cache, "shader-packs", packId);
        var shaders = Path.Combine(pack, "extracted", "Shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllText(Path.Combine(shaders, "Test.fx"), "float4 PS() : SV_Target { return 1; }");
        File.WriteAllText(Path.Combine(pack, ".complete"), "test");

        // ReShadeShaderPackService treats the cache as valid only when the
        // source identity matches the accepted catalog entry. Keep this fixture
        // aligned with the real CrosireMaster DirectUrl authority so profile-only
        // update tests remain offline and exercise D3DCompiler ownership instead
        // of accidentally falling through to the network handler.
        var sourceIdentity = packId.Equals("CrosireMaster", StringComparison.OrdinalIgnoreCase)
            ? "https://github.com/crosire/reshade-shaders/archive/refs/heads/master.zip"
            : throw new InvalidOperationException($"SeedShaderCache has no source identity fixture for '{packId}'.");
        File.WriteAllText(Path.Combine(pack, ".source"), sourceIdentity);
        // A cache entry RHI itself extracted also carries its download stamp (#142).
        CacheStamp.Write(Path.Combine(pack, "extracted"), sourceIdentity, sourceIdentity);
    }

    private static void WriteNativeD3DCompiler(string path, ushort machine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = 512;
        stream.Write(Encoding.ASCII.GetBytes("Microsoft Corporation Microsoft (R) HLSL Shader Compiler 10.1 D3DCompiler_47"));
    }

    private static byte[] CreateNightlyZip(string dllName)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(dllName, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            var bytes = new byte[1_100_000];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            stream.Write(bytes);
        }
        return memory.ToArray();
    }


    private static void StampReShadeSignature(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = 512;
        stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me"));
    }

    private static void WriteSignedTextFile(string path, string signature)
    {
        var bytes = new byte[Math.Max(4096, signature.Length + 256)];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 128);
        File.WriteAllBytes(path, bytes);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class MutableHandler : HttpMessageHandler
    {
        public string StableVersion { get; set; } = "9.9.9";
        public byte[]? NightlyPayload { get; set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var uri = request.RequestUri!;
            if (uri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/")
            {
                var html = $"<a href=\"/downloads/ReShade_Setup_{StableVersion}_Addon.exe\">Download</a>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            if (uri.Host.Equals("nightly.link", StringComparison.OrdinalIgnoreCase))
            {
                var payload = NightlyPayload ?? CreateNightlyZip(uri.AbsoluteUri.Contains("32-bit", StringComparison.OrdinalIgnoreCase) ? "ReShade32.dll" : "ReShade64.dll");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
                response.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(response);
            }

            var setup = new byte[600_000];
            setup[0] = (byte)'M';
            setup[1] = (byte)'Z';
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup) });
        }
    }

    private sealed class FakeExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            WriteDll(Path.Combine(outputDirectory, "ReShade64.dll"), 0x8664);
            WriteDll(Path.Combine(outputDirectory, "ReShade32.dll"), 0x014c);
            return Task.CompletedTask;
        }

        private static void WriteDll(string path, ushort machine)
        {
            PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = 512;
            stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me"));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
