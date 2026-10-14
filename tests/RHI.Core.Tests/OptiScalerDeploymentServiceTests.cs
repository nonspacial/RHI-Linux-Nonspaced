using RHI.Core.Deployment;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed partial class OptiScalerDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optiscaler-deploy-tests-" + Guid.NewGuid().ToString("N"));

    public OptiScalerDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task InstallAndRemove_Dx12_DeploysRuntimeAllowsIniEditsAndRestoresRegistry()
    {
        var (game, store) = CreateGame("basic", GraphicsApi.DirectX12);
        var package = CreatePackage("basic-package") with { Download = new VerifiedDownload("/cache/OptiScaler.7z", new string('C', 64), PublishedSha256: null) };
        var service = NewService(store, package);

        var installed = await service.InstallAsync(game);
        Assert.True(installed.Success, installed.Message);

        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        var nested = Path.Combine(game.BinaryDirectory!, "OptiScaler", "libxess.dll");
        Assert.True(File.Exists(dxgi));
        Assert.True(File.Exists(ini));
        Assert.True(File.Exists(nested));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "setup_linux.sh")));
        Assert.False(Directory.Exists(Path.Combine(game.BinaryDirectory!, "Licenses")));

        var registryPath = Path.Combine(game.PrefixPath!, "user.reg");
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(registryPath), "dxgi", "Game.exe"));
        var record = store.Find(game, ManagedComponentKind.OptiScaler);
        Assert.NotNull(record);
        Assert.Equal("9.9.9", record!.Version);
        Assert.Contains(record.Files, file => Path.GetFileName(file.Path).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) && file.AllowUserChanges);
        DownloadRecordAssert.Recorded(record, new string('C', 64), VerifiedDownload.RecordedOnly);

        // OptiScaler.ini is deliberately user-editable and must not make uninstall unsafe.
        await File.AppendAllTextAsync(ini, "\n[User]\nCustomSetting=true\n");

        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(dxgi));
        Assert.False(File.Exists(ini));
        Assert.False(File.Exists(nested));
        Assert.False(Directory.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler")));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));

        var restoredRegistry = File.ReadAllText(registryPath);
        Assert.Null(WineDllOverrideService.TryReadOverride(restoredRegistry, "dxgi", "Game.exe"));
        Assert.DoesNotContain(@"[Software\\Wine\\AppDefaults\\Game.exe\\DllOverrides]", restoredRegistry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_RecordsTheNewPackagesDownload()
    {
        // Review w2-trust minor 6: the record names the archive the files now come from, not the first install's.
        var (game, store) = CreateGame("update-download", GraphicsApi.DirectX12);
        var first = CreatePackage("update-download-1") with { Download = new VerifiedDownload("/cache/a.7z", new string('A', 64), PublishedSha256: null) };
        var second = CreatePackage("update-download-2") with
        {
            Version = "9.9.10",
            Download = new VerifiedDownload("/cache/b.7z", new string('B', 64), PublishedSha256: new string('B', 64))
        };
        File.AppendAllText(Path.Combine(second.RootDirectory, "OptiScaler", "libxess.dll"), " v2");
        var provider = new SwitchingPackageProvider(first);
        var service = NewService(store, provider);
        Assert.True((await service.InstallAsync(game)).Success);

        provider.Package = second;
        var updated = await service.UpdateAsync(game);

        Assert.True(updated.Success, updated.Message);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("9.9.10", record.Version);
        DownloadRecordAssert.Recorded(record, new string('B', 64), VerifiedDownload.PublishedDigest);
    }

    [Fact]
    public async Task Install_NightlyPersistsVariantAndRequestsNightlyPackage()
    {
        var (game, store) = CreateGame("nightly", GraphicsApi.DirectX12);
        var package = CreatePackage("nightly-package") with { Variant = OptiScalerVariant.Nightly, Version = "20260920" };
        var provider = new FakePackageProvider(package);
        var service = NewService(store, provider);

        var installed = await service.InstallAsync(game, OptiScalerVariant.Nightly);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(OptiScalerVariant.Nightly, provider.LastVariant);
        var record = store.Find(game, ManagedComponentKind.OptiScaler);
        Assert.NotNull(record);
        Assert.Equal("Nightly", record!.Metadata.GetValueOrDefault(OptiScalerDeploymentService.VariantMetadataKey));
    }

    [Fact]
    public async Task Install_RejectsX86BeforeRequestingPackage()
    {
        var (game, store) = CreateGame("x86", GraphicsApi.DirectX11, machine: 0x014c);
        var provider = new FakePackageProvider(CreatePackage("x86-package"));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("64-bit", result.Message);
        Assert.Equal(0, provider.CallCount);
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }


    // w2 wine review, minor 2 (N7): refused while Wine runs in the prefix, before the package is requested.
    [Fact]
    public async Task Install_WhileWineRunsInThePrefix_IsRefusedBeforeRequestingPackage()
    {
        var (game, store) = CreateGame("wine-running", GraphicsApi.DirectX12);
        var provider = new FakePackageProvider(CreatePackage("wine-running-package"));
        var userReg = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store,
            new WineDllOverrideService(new WineRegistryEditor(_ => true)), provider);

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + game.PrefixPath, result.Message);
        Assert.Equal(0, provider.CallCount);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal(userReg, File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Install_DlssNrBlocksWhileFeederOwnsNeuralRuntimeBeforeRequestingPackage()
    {
        var (game, store) = CreateGame("feeder-nr-block", GraphicsApi.DirectX12);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.Dlss5Feeder,
            BinaryDirectory = game.BinaryDirectory!,
            Files = []
        });
        var provider = new FakePackageProvider(CreatePackage("feeder-nr-block-package"));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game, OptiScalerVariant.DlssNr);

        Assert.False(result.Success);
        Assert.Contains("Feeder", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nvngx_dlssnr.dll", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, provider.CallCount);
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Install_BlocksWhileShortFuseManagedBeforeRequestingPackage()
    {
        var (game, store) = CreateGame("shortfuse-block", GraphicsApi.DirectX12);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = game.BinaryDirectory!,
            Files = []
        });
        var provider = new FakePackageProvider(CreatePackage("shortfuse-block-package"));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("ShortFuse", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, provider.CallCount);
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Install_ProtectsUnknownForeignDxgiWithoutChangingRegistry()
    {
        var (game, store) = CreateGame("foreign", GraphicsApi.DirectX11);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        File.WriteAllText(dxgi, "MZ foreign proxy owned by somebody else");
        var registryPath = Path.Combine(game.PrefixPath!, "user.reg");
        var before = File.ReadAllText(registryPath);
        var service = NewService(store, CreatePackage("foreign-package"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("Foreign DLL protection", result.Message);
        Assert.Equal("MZ foreign proxy owned by somebody else", File.ReadAllText(dxgi));
        Assert.Equal(before, File.ReadAllText(registryPath));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Install_MigratesManagedReShadeToReShade64AndTransfersOverrideOwnership()
    {
        var (game, store) = CreateGame("migrate", GraphicsApi.DirectX12);
        var originalOverride = SeedManagedReShade(game, store);
        var service = NewService(store, CreatePackage("migrate-package"));

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var reshade64 = Path.Combine(game.BinaryDirectory!, "ReShade64.dll");
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(dxgi));
        Assert.True(BinarySignatureInspector.LooksLikeReShade(reshade64));
        Assert.Contains("LoadReshade=true", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")), StringComparison.OrdinalIgnoreCase);

        var reShadeRecord = store.Find(game, ManagedComponentKind.ReShade);
        var optiRecord = store.Find(game, ManagedComponentKind.OptiScaler);
        Assert.NotNull(reShadeRecord);
        Assert.NotNull(optiRecord);
        Assert.Null(reShadeRecord!.WineOverride);
        Assert.NotNull(optiRecord!.WineOverride);
        Assert.Equal(originalOverride.DllName, optiRecord.WineOverride!.DllName);
        Assert.Single(reShadeRecord.Files);
        Assert.Equal(Path.GetFullPath(reshade64), Path.GetFullPath(reShadeRecord.Files[0].Path));
    }

    [Fact]
    public async Task RemoveOptiScalerFirst_MovesManagedReShadeBackToDxgiAndKeepsOverride()
    {
        var (game, store) = CreateGame("remove-opti-first", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var service = NewService(store, CreatePackage("remove-opti-first-package"));
        Assert.True((await service.InstallAsync(game)).Success);

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        Assert.True(BinarySignatureInspector.LooksLikeReShade(dxgi));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
        var reShadeRecord = store.Find(game, ManagedComponentKind.ReShade);
        Assert.NotNull(reShadeRecord);
        Assert.NotNull(reShadeRecord!.WineOverride);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")), "dxgi", "Game.exe"));
    }

    // up-135 re-review nit 4: a retry after a removal that already deleted OptiScaler's dxgi.dll (and saved that
    // progress) still moves ReShade64.dll back to dxgi.dll, with its record following the file.
    [Fact]
    public async Task RemoveOptiScaler_RetryAfterTheProxyWasAlreadyRemoved_MovesReShadeBack()
    {
        var (game, store) = CreateGame("remove-retry-coexist", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var service = NewService(store, CreatePackage("remove-retry-coexist-package"));
        Assert.True((await service.InstallAsync(game)).Success);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var reshade64 = Path.Combine(game.BinaryDirectory!, "ReShade64.dll");
        var reshadeHash = Sha256(reshade64);
        // The state a partly failed removal leaves: dxgi.dll deleted and dropped from the saved record.
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(1, record.Files.RemoveAll(file => Path.GetFullPath(file.Path) == Path.GetFullPath(dxgi)));
        store.Upsert(record);
        File.Delete(dxgi);

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        Assert.True(BinarySignatureInspector.LooksLikeReShade(dxgi));
        Assert.False(File.Exists(reshade64));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
        var reShadeFile = Assert.Single(store.Find(game, ManagedComponentKind.ReShade)!.Files);
        Assert.Equal(Path.GetFullPath(dxgi), Path.GetFullPath(reShadeFile.Path));
        Assert.Equal(reshadeHash, reShadeFile.Sha256);
        Assert.Equal(Sha256(dxgi), reShadeFile.Sha256);
    }

    [Fact]
    public async Task RemoveReShadeFirst_LeavesOptiScalerThenOptiRemovalCleansRegistry()
    {
        var (game, store) = CreateGame("remove-reshade-first", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var optiService = NewService(store, CreatePackage("remove-reshade-first-package"));
        Assert.True((await optiService.InstallAsync(game)).Success);

        using var http = new HttpClient(new NeverUsedHttpHandler());
        var reshadeService = new ReShadeDeploymentService(
            http,
            new TransactionalFileDeploymentService(),
            store,
            new WineDllOverrideService(),
            new NeverUsedReShadeExtractor(),
            Path.Combine(_root, "unused-reshade-cache"),
            new ReShadeShaderPackService(http, Path.Combine(_root, "unused-reshade-cache", "shader-packs"),
                customRoot: Path.Combine(_root, "custom-shaders")));

        var removedReShade = await reshadeService.RemoveAsync(game);
        Assert.True(removedReShade.Success, removedReShade.Message);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Contains("LoadReshade=false", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")), StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));
        Assert.NotNull(store.Find(game, ManagedComponentKind.OptiScaler));

        var removedOpti = await optiService.RemoveAsync(game);
        Assert.True(removedOpti.Success, removedOpti.Message);
        var registry = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        Assert.Null(WineDllOverrideService.TryReadOverride(registry, "dxgi", "Game.exe"));
        Assert.DoesNotContain(@"[Software\\Wine\\AppDefaults\\Game.exe\\DllOverrides]", registry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAndRemove_PreservesPreexistingNestedRuntimeFile()
    {
        var (game, store) = CreateGame("preserve-runtime", GraphicsApi.DirectX12);
        var package = CreatePackage("preserve-runtime-package");
        var nestedDir = Path.Combine(game.BinaryDirectory!, "OptiScaler");
        Directory.CreateDirectory(nestedDir);
        var runtime = Path.Combine(nestedDir, "libxess.dll");
        File.WriteAllText(runtime, "game-owned original runtime");
        var service = NewService(store, package);

        var installed = await service.InstallAsync(game);
        Assert.True(installed.Success, installed.Message);
        Assert.Equal("MZ package xess runtime", File.ReadAllText(runtime));
        Assert.True(File.Exists(runtime + TransactionalFileDeploymentService.BackupExtension));

        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("game-owned original runtime", File.ReadAllText(runtime));
        Assert.False(File.Exists(runtime + TransactionalFileDeploymentService.BackupExtension));
        Assert.True(Directory.Exists(nestedDir));
    }

    [Fact]
    public void BuildDeploymentPlan_SkipsSetupMarkerAndLicenseTrees()
    {
        var package = CreatePackage("plan-package");
        var bin = Path.Combine(_root, "plan-bin");
        Directory.CreateDirectory(bin);
        var requested = new List<(string Source, string Target)>();

        OptiScalerDeploymentService.BuildDeploymentPlan(package.RootDirectory, bin, Path.Combine(bin, "dxgi.dll"), requested);

        var targets = requested.Select(item => Path.GetRelativePath(bin, item.Target)).ToArray();
        Assert.Contains("dxgi.dll", targets);
        Assert.Contains("OptiScaler.ini", targets);
        Assert.Contains(Path.Combine("OptiScaler", "libxess.dll"), targets);
        Assert.DoesNotContain(targets, path => path.Contains("setup_linux", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targets, path => path.Contains("EXTRACT ALL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targets, path => path.Split(Path.DirectorySeparatorChar).Any(segment => segment.Equals("Licenses", StringComparison.OrdinalIgnoreCase)));
    }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (game, store) = CreateGame("cancel-install", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var service = NewService(store, CreatePackage("cancel-install-package"));

        var run = await CancelAtWritePhase.RunAsync(ct => service.InstallAsync(game, ct), [game.InstallPath, game.PrefixPath!]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(store.Find(game, ManagedComponentKind.OptiScaler));
        Assert.True(BinarySignatureInspector.LooksLikeReShade(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
    }

    [Fact]
    public async Task Update_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var (game, store) = CreateGame("cancel-update", GraphicsApi.DirectX12);
        var service = NewService(store, CreatePackage("cancel-update-package"));
        Assert.True((await service.InstallAsync(game)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.UpdateAsync(game, ct), [game.InstallPath, game.PrefixPath!]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Remove_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        var (game, store) = CreateGame("cancel-remove", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var service = NewService(store, CreatePackage("cancel-remove-package"));
        Assert.True((await service.InstallAsync(game)).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(game, ct), [game.InstallPath, game.PrefixPath!]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
        Assert.True(BinarySignatureInspector.LooksLikeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
    }

    private (DetectedGame Game, LinuxDeploymentRecordStore Store) CreateGame(string name, GraphicsApi api, ushort machine = 0x8664)
    {
        var gameRoot = Path.Combine(_root, name, "game");
        var bin = Path.Combine(gameRoot, "bin");
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, machine);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "Game",
            InstallPath = gameRoot,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Runner = "Proton",
            GraphicsApi = api,
            SteamAppId = 4242
        };
        return (game, new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json")));
    }

    private OptiScalerPackage CreatePackage(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        WriteSignedTextFile(Path.Combine(root, "OptiScaler.dll"), "optiscaler/OptiScaler official fake package");
        File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n");
        File.WriteAllText(Path.Combine(root, "setup_linux.sh"), "ignored setup");
        File.WriteAllText(Path.Combine(root, "setup_windows.bat"), "ignored setup");
        File.WriteAllText(Path.Combine(root, "!! EXTRACT ALL FILES TO GAME FOLDER !!"), "marker");
        var licenses = Path.Combine(root, "Licenses");
        Directory.CreateDirectory(licenses);
        File.WriteAllText(Path.Combine(licenses, "license.txt"), "ignored license");
        var nested = Path.Combine(root, "OptiScaler");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "libxess.dll"), "MZ package xess runtime");
        var nestedLicenses = Path.Combine(nested, "Licenses");
        Directory.CreateDirectory(nestedLicenses);
        File.WriteAllText(Path.Combine(nestedLicenses, "nested-license.txt"), "ignored nested license");
        var agility = Path.Combine(nested, "D3D12_OptiScaler");
        Directory.CreateDirectory(agility);
        File.WriteAllText(Path.Combine(agility, "D3D12Core.dll"), "MZ package agility runtime");
        return new OptiScalerPackage("9.9.9", "https://github.com/optiscaler/OptiScaler/releases/fake", root);
    }

    private WineOverrideRecord SeedManagedReShade(DetectedGame game, LinuxDeploymentRecordStore store)
    {
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        WriteSignedTextFile(dxgi, "ReShade crosire reshade.me");
        var wine = new WineDllOverrideService();
        var overrideRecord = wine.EnsureNativeBuiltin(game.PrefixPath!, "dxgi.dll", Path.GetFileName(game.ExecutablePath));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!,
            Version = "9.9.9",
            WineOverride = overrideRecord,
            Files =
            [
                new LinuxDeployedFile
                {
                    Path = dxgi,
                    ExistedBeforeInstall = false,
                    Sha256 = Sha256(dxgi)
                }
            ]
        });
        return overrideRecord;
    }

    private OptiScalerDeploymentService NewService(LinuxDeploymentRecordStore store, OptiScalerPackage package)
        => NewService(store, new FakePackageProvider(package));

    private static OptiScalerDeploymentService NewService(LinuxDeploymentRecordStore store, IOptiScalerPackageProvider provider)
        => new(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider);

    private static void WriteSignedTextFile(string path, string signature)
    {
        var bytes = new byte[Math.Max(4096, signature.Length + 128)];
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class SwitchingPackageProvider(OptiScalerPackage package) : IOptiScalerPackageProvider
    {
        public OptiScalerPackage Package { get; set; } = package;

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => GetLatestAsync(OptiScalerVariant.Stable, cancellationToken);

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
            => Task.FromResult(Package with { Variant = variant });
    }

    private sealed class FakePackageProvider(OptiScalerPackage package) : IOptiScalerPackageProvider
    {
        public int CallCount { get; private set; }
        public OptiScalerVariant? LastVariant { get; private set; }

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => GetLatestAsync(OptiScalerVariant.Stable, cancellationToken);

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastVariant = variant;
            return Task.FromResult(package with { Variant = variant });
        }
    }

    private sealed class NeverUsedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private sealed class NeverUsedReShadeExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Extractor should not be called during removal tests.");
    }
}
