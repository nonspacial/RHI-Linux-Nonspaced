using System.Security.Cryptography;
using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ReShadeDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-deploy-tests-" + Guid.NewGuid().ToString("N"));
    public ReShadeDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task InstallAndRemove_Dx12_DeploysDxgiAndRestoresWineOverride()
    {
        var gameRoot = Path.Combine(_root, "game");
        var bin = Path.Combine(gameRoot, "bin");
        var prefix = Path.Combine(_root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "Game", InstallPath = gameRoot, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12, SteamAppId = 42
        };
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var service = NewService(store, Path.Combine(_root, "cache"));

        var installed = await service.InstallAsync(game);
        Assert.True(installed.Success, installed.Message);
        var target = Path.Combine(bin, "dxgi.dll");
        Assert.True(File.Exists(target));
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "dxgi", "Game.exe"));
        var reshadeLog = Path.Combine(bin, "reshade.log");
        File.WriteAllText(reshadeLog, "user/runtime diagnostic log");

        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(reshadeLog));
        Assert.Equal("user/runtime diagnostic log", File.ReadAllText(reshadeLog));
        var restoredRegistry = File.ReadAllText(Path.Combine(prefix, "user.reg"));
        Assert.Null(WineDllOverrideService.TryReadOverride(restoredRegistry, "dxgi", "Game.exe"));
        Assert.DoesNotContain(@"[Software\\Wine\\AppDefaults\\Game.exe\\DllOverrides]", restoredRegistry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_WhileShortFuseOwnsAsi_PreservesAsiLifecycleWithoutBogusBackupOrDxgiOverride()
    {
        var gameRoot = Path.Combine(_root, "shortfuse-update-game");
        var bin = Path.Combine(gameRoot, "bin");
        var prefix = Path.Combine(_root, "shortfuse-update-pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\Wine\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "ShortFuse Update", InstallPath = gameRoot, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12, SteamAppId = 77
        };
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "shortfuse-update-records.json"));
        var service = NewService(store, Path.Combine(_root, "shortfuse-update-cache"));

        var first = await service.InstallAsync(game);
        Assert.True(first.Success, first.Message);
        var directPath = Path.Combine(bin, "dxgi.dll");
        var asiPath = Path.Combine(bin, ShortFuseDeploymentService.ReShadeAsiName);
        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        var payload = record.Files.Single(f => Path.GetFileName(f.Path).Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase));
        var overrides = new WineDllOverrideService();
        overrides.Restore(prefix, record.WineOverride!);
        File.Move(directPath, asiPath);
        payload.Path = asiPath;
        record.Metadata["ReShade.ShortFuseOriginalPayloadPath"] = directPath;
        record.Metadata["ShortFuse.ReShadeOverrideSuspended"] = "1";
        store.Upsert(record);

        var updated = await service.InstallAsync(game);

        Assert.True(updated.Success, updated.Message);
        Assert.True(File.Exists(asiPath));
        Assert.False(File.Exists(asiPath + TransactionalFileDeploymentService.BackupExtension));
        Assert.False(File.Exists(directPath));
        var updatedRecord = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(asiPath, updatedRecord.Files.Single(f => Path.GetFileName(f.Path).Equals(ShortFuseDeploymentService.ReShadeAsiName, StringComparison.OrdinalIgnoreCase)).Path);
        Assert.Equal(directPath, updatedRecord.Metadata["ReShade.ShortFuseOriginalPayloadPath"]);
        var registry = File.ReadAllText(Path.Combine(prefix, "user.reg"));
        Assert.Null(WineDllOverrideService.TryReadOverride(registry, "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task Install_ProtectsUnknownForeignProxy()
    {
        var bin = Path.Combine(_root, "foreign");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "dxgi.dll"), "MZ foreign dxgi");
        var game = new DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Wine", GraphicsApi = GraphicsApi.DirectX11 };
        var service = NewService(new LinuxDeploymentRecordStore(Path.Combine(_root, "foreign-records.json")), Path.Combine(_root, "foreign-cache"));

        var result = await service.InstallAsync(game);
        Assert.False(result.Success);
        Assert.Contains("Foreign DLL protection", result.Message);
        Assert.Equal("MZ foreign dxgi", File.ReadAllText(Path.Combine(bin, "dxgi.dll")));
    }

    [Fact]
    public async Task Install_ProtectsExternalReShadeAtOptiScalerCoexistenceTarget()
    {
        var bin = Path.Combine(_root, "optiscaler-external-rs");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "dxgi.dll"), "MZ OptiScaler proxy");
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire external");
        var game = new DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 };
        var service = NewService(new LinuxDeploymentRecordStore(Path.Combine(_root, "external-rs-records.json")), Path.Combine(_root, "external-rs-cache"));

        var result = await service.InstallAsync(game);
        Assert.False(result.Success);
        Assert.Contains("external setup is protected", result.Message.ToLowerInvariant());
        Assert.Equal("MZ ReShade crosire external", File.ReadAllText(Path.Combine(bin, "ReShade64.dll")));
    }

    [Fact]
    public async Task Install_UsesReShade64CoexistenceWhenRhiManagedOptiScalerOwnsDxgi()
    {
        var bin = Path.Combine(_root, "optiscaler");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var dxgi = Path.Combine(bin, "dxgi.dll");
        File.WriteAllText(dxgi, "MZ OptiScaler proxy");
        var ini = Path.Combine(bin, "OptiScaler.ini");
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=auto\n");
        var game = new DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 };
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "os-records.json"));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler, BinaryDirectory = bin,
            Files =
            [
                new LinuxDeployedFile { Path = dxgi, ExistedBeforeInstall = false, Sha256 = Sha256(dxgi) },
                new LinuxDeployedFile { Path = ini, ExistedBeforeInstall = false, AllowUserChanges = true, Sha256 = Sha256(ini) }
            ]
        });
        var service = NewService(store, Path.Combine(_root, "os-cache"));

        var result = await service.InstallAsync(game);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(bin, "ReShade64.dll")));
        Assert.Equal("MZ OptiScaler proxy", File.ReadAllText(dxgi));
        Assert.Contains("LoadReshade=true", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_LockedOptiScalerIni_NeverBlocksRemoval_AndStaysLocked()
    {
        // Review w2-trust major 2: OptiScaler.ini read-only may refuse an install, never a removal.
        var (game, store, ini) = CoexistingOptiScaler("optiscaler-locked-remove");
        var service = NewService(store, Path.Combine(_root, "os-locked-cache"));
        var install = await service.InstallAsync(game);
        Assert.True(install.Success, install.Message);
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(ini, locked);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Contains("LoadReshade=false", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(locked, File.GetUnixFileMode(ini));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
    }

    [Fact]
    public async Task Install_LockedOptiScalerIni_IsRefused_AndTheFileLeftAlone()
    {
        var (game, store, ini) = CoexistingOptiScaler("optiscaler-locked-install");
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(ini, locked);
        var service = NewService(store, Path.Combine(_root, "os-locked-install-cache"));

        var install = await service.InstallAsync(game);

        Assert.False(install.Success);
        Assert.Contains(AtomicFile.ReadOnlyRefusalPrefix + ini, install.Message, StringComparison.Ordinal);
        Assert.Equal("[Plugins]\nLoadReshade=auto\n", File.ReadAllText(ini));
        Assert.Equal(locked, File.GetUnixFileMode(ini));
    }

    /// <summary>An RHI-managed OptiScaler owning dxgi.dll (ReShade then installs as ReShade64.dll beside it).</summary>
    private (DetectedGame Game, LinuxDeploymentRecordStore Store, string Ini) CoexistingOptiScaler(string folder)
    {
        var bin = Path.Combine(_root, folder);
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var dxgi = Path.Combine(bin, "dxgi.dll");
        File.WriteAllText(dxgi, "MZ OptiScaler proxy");
        var ini = Path.Combine(bin, "OptiScaler.ini");
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=auto\n");
        var game = new DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 };
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, folder + "-records.json"));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler, BinaryDirectory = bin,
            Files =
            [
                new LinuxDeployedFile { Path = dxgi, ExistedBeforeInstall = false, Sha256 = Sha256(dxgi) },
                new LinuxDeployedFile { Path = ini, ExistedBeforeInstall = false, AllowUserChanges = true, Sha256 = Sha256(ini) }
            ]
        });
        return (game, store, ini);
    }

    [Fact]
    public async Task Install_BlocksCoexistenceWhenManagedOptiScalerProxyHashChanged()
    {
        var bin = Path.Combine(_root, "optiscaler-modified");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var dxgi = Path.Combine(bin, "dxgi.dll");
        File.WriteAllText(dxgi, "MZ OptiScaler original proxy");
        var ini = Path.Combine(bin, "OptiScaler.ini");
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=auto\n");
        var game = new DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 };
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "os-modified-records.json"));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler, BinaryDirectory = bin,
            Files =
            [
                new LinuxDeployedFile { Path = dxgi, ExistedBeforeInstall = false, Sha256 = Sha256(dxgi) },
                new LinuxDeployedFile { Path = ini, ExistedBeforeInstall = false, AllowUserChanges = true, Sha256 = Sha256(ini) }
            ]
        });
        File.AppendAllText(dxgi, " modified-outside-rhi");
        var service = NewService(store, Path.Combine(_root, "os-modified-cache"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("modified outside RHI", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(bin, "ReShade64.dll")));
        Assert.Contains("modified-outside-rhi", File.ReadAllText(dxgi));
    }

    [Fact]
    public async Task Install_ProtectsExternalOptiScalerWithoutRhiRecord()
    {
        var bin = Path.Combine(_root, "optiscaler-external");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "dxgi.dll"), "MZ OptiScaler proxy");
        File.WriteAllText(Path.Combine(bin, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n");
        var game = new DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 };
        var service = NewService(new LinuxDeploymentRecordStore(Path.Combine(_root, "os-external-records.json")), Path.Combine(_root, "os-external-cache"));

        var result = await service.InstallAsync(game);
        Assert.False(result.Success);
        Assert.Contains("not managed by RHI Linux", result.Message);
        Assert.False(File.Exists(Path.Combine(bin, "ReShade64.dll")));
    }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; installs and removals never stop half-way.
    private (DetectedGame Game, string Bin, string Prefix) CancelGame(string name, bool prefixExists = true)
    {
        var gameRoot = Path.Combine(_root, name);
        var bin = Path.Combine(gameRoot, "bin");
        var prefix = Path.Combine(_root, name + "-pfx");
        Directory.CreateDirectory(bin);
        if (prefixExists)
        {
            Directory.CreateDirectory(prefix);
            File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        }
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        return (new DetectedGame
        {
            Name = name, InstallPath = gameRoot, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12
        }, bin, prefix);
    }

    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (game, bin, prefix) = CancelGame("cancel-install");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "cancel-install.json"));
        var service = NewService(store, Path.Combine(_root, "cancel-install-cache"));

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, ct), [bin, prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_ThenAFailure_LeavesNoProxyAndNoOriginal()
    {
        // The prefix is missing, so the install fails right after placing dxgi.dll: the fresh-file rollback must run
        // with its own token, not the cancelled one.
        var (game, bin, _) = CancelGame("cancel-rollback", prefixExists: false);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "cancel-rollback.json"));
        var service = NewService(store, Path.Combine(_root, "cancel-rollback-cache"));

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, ct), [bin]);

        Assert.False(run.Result.Success);
        CancelAtWritePhase.AssertUnchanged(run.Before, bin);
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task Remove_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        var (game, bin, prefix) = CancelGame("cancel-remove");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "cancel-remove.json"));
        var service = NewService(store, Path.Combine(_root, "cancel-remove-cache"));
        Assert.True((await service.InstallAsync(game)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(game, ct), [bin, prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
        Assert.False(File.Exists(Path.Combine(bin, "dxgi.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task Remove_CancelledBeforeAnyChange_ChangesNothing_AndARetrySucceeds()
    {
        var (game, bin, prefix) = CancelGame("cancel-retry");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "cancel-retry.json"));
        var service = NewService(store, Path.Combine(_root, "cancel-retry-cache"));
        Assert.True((await service.InstallAsync(game)).Success);
        var before = CancelAtWritePhase.Snapshot(bin, prefix);

        using (var cancel = new RHI.Core.Deployment.OperationCancellation())
        {
            Assert.True(cancel.TryCancel());
            var cancelled = await service.RemoveAsync(game, cancel.Token);
            Assert.False(cancelled.Success);
        }
        CancelAtWritePhase.AssertUnchanged(before, bin, prefix);
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));

        var retry = await service.RemoveAsync(game);
        Assert.True(retry.Success, retry.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
        Assert.False(File.Exists(Path.Combine(bin, "dxgi.dll")));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private ReShadeDeploymentService NewService(LinuxDeploymentRecordStore store, string cache)
    {
        var http = new HttpClient(new FakeHandler());
        return new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.RequestUri?.Host, "reshade.me", StringComparison.OrdinalIgnoreCase) && request.RequestUri?.AbsolutePath == "/")
            {
                const string html = "<a href=\"/downloads/ReShade_Setup_9.9.9_Addon.exe\">Download</a>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            var bytes = new byte[600_000];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class FakeExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            WriteDll(Path.Combine(outputDirectory, "ReShade64.dll"));
            WriteDll(Path.Combine(outputDirectory, "ReShade32.dll"));
            return Task.CompletedTask;
        }

        private static void WriteDll(string path)
        {
            var bytes = new byte[1_100_000];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("ReShade crosire reshade.me").CopyTo(bytes, 512);
            File.WriteAllBytes(path, bytes);
        }
    }
}
