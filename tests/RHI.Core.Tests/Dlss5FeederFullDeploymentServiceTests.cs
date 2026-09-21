using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class Dlss5FeederFullDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-feeder-full-tests-" + Guid.NewGuid().ToString("N"));

    public Dlss5FeederFullDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ValidateCachedRuntime_RejectsZeroByteDllBeforeDeployment()
    {
        var path = Path.GetTempFileName();
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                Dlss5FeederFullDeploymentService.ValidateCachedRuntime(path, "nvngx_dlss.dll"));
            Assert.Contains("0-byte", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateCachedRuntime_AcceptsPlausibleCachedDllSize()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[128 * 1024]);
            Dlss5FeederFullDeploymentService.ValidateCachedRuntime(path, "nvngx_dlss.dll");
        }
        finally { File.Delete(path); }
    }


    [Fact]
    public void PrefixCompilerProbe_FindsLowercaseWinetricksFilenameOnLinux()
    {
        var prefix = Path.Combine(_root, "lowercase-prefix");
        var system32 = Path.Combine(prefix, "drive_c", "windows", "system32");
        Directory.CreateDirectory(system32);
        var compiler = Path.Combine(system32, "d3dcompiler_47.dll");
        WriteNativeCompiler(compiler, unicodeMarkersOnly: true);

        var found = ReShadeDeploymentService.FindNativeD3DCompilerInPrefix(prefix, PeArchitecture.X64);

        Assert.Equal(compiler, found);
    }

    [Fact]
    public void FeederCompilerPlan_AcceptsExistingAppLocalCompiler()
    {
        var binary = Path.Combine(_root, "app-local-game");
        var prefix = Path.Combine(_root, "app-local-prefix");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(prefix);
        var compiler = Path.Combine(binary, "d3dcompiler_47.dll");
        WriteNativeCompiler(compiler);
        var game = new DetectedGame
        {
            Name = "App Local",
            BinaryDirectory = binary,
            PrefixPath = prefix
        };

        var plan = Dlss5FeederFullDeploymentService.ResolveCompilerPlan(game, PeArchitecture.X64, selectedExternalPath: null);

        Assert.Equal(compiler, plan.UsablePath);
        Assert.Null(plan.DeploySourcePath);
        Assert.StartsWith("app-local:", plan.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void FeederCompilerPlan_UsesSelectedExternalCompilerWithoutReShadeApply()
    {
        var binary = Path.Combine(_root, "selected-external-game");
        var prefix = Path.Combine(_root, "selected-external-prefix");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(prefix);
        var source = Path.Combine(_root, "external-native", "D3DCompiler_47.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        WriteNativeCompiler(source);
        var game = new DetectedGame
        {
            Name = "Selected External",
            BinaryDirectory = binary,
            PrefixPath = prefix
        };

        var plan = Dlss5FeederFullDeploymentService.ResolveCompilerPlan(game, PeArchitecture.X64, source);

        Assert.Equal(Path.Combine(binary, "D3DCompiler_47.dll"), plan.UsablePath);
        Assert.Equal(source, plan.DeploySourcePath);
        Assert.StartsWith("selected:", plan.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPresetText_PutsMvProviderOnEffectSection()
    {
        var text = Dlss5FeederFullDeploymentService.BuildPresetText();

        Assert.Contains("[DLSS5_Feed.fx]", text);
        Assert.Contains("PreprocessorDefinitions=DLSS5_MV_PROVIDER=3", text);
        Assert.Contains("Lumenite_Kernel@lumenite_Kernel.fx", text);
        Assert.Contains("DLSS5_Feed@DLSS5_Feed.fx", text);
        Assert.DoesNotContain("[GENERAL]\r\nPreprocessorDefinitions=DLSS5_MV_PROVIDER=3", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresetPathMutation_RestoresPreexistingValue()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=.\\UserPreset.ini\nEffectSearchPaths=.\\user\\Shaders\\**\n");

        var mutation = Dlss5FeederFullDeploymentService.SetPresetPath(ini, @".\RHI-DLSS5-Feeder.ini");
        Assert.True(mutation.PreviousKeyExisted);
        Assert.Equal(@".\UserPreset.ini", mutation.PreviousValue);
        Assert.Contains(@"PresetPath=.\RHI-DLSS5-Feeder.ini", File.ReadAllText(ini));

        Dlss5FeederFullDeploymentService.RestorePresetPath(ini, mutation);
        var text = File.ReadAllText(ini);
        Assert.Contains(@"PresetPath=.\UserPreset.ini", text);
        Assert.Contains(@"EffectSearchPaths=.\user\Shaders\**", text);
    }

    [Fact]
    public void PresetPathMutation_RemovesOnlyKeyItCreated()
    {
        var ini = Path.Combine(_root, "ReShade-no-preset.ini");
        File.WriteAllText(ini, "[GENERAL]\nEffectSearchPaths=.\\user\\Shaders\\**\n");

        var mutation = Dlss5FeederFullDeploymentService.SetPresetPath(ini, @".\RHI-DLSS5-Feeder.ini");
        Assert.False(mutation.PreviousKeyExisted);

        Dlss5FeederFullDeploymentService.RestorePresetPath(ini, mutation);
        var text = File.ReadAllText(ini);
        Assert.DoesNotContain("PresetPath=", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"EffectSearchPaths=.\user\Shaders\**", text);
    }

    [Fact]
    public void PresetPathMutation_PreservesUserChoiceMadeAfterInstall()
    {
        var ini = Path.Combine(_root, "ReShade-user-change.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=.\\Old.ini\n");

        var mutation = Dlss5FeederFullDeploymentService.SetPresetPath(ini, @".\RHI-DLSS5-Feeder.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=.\\NewUserChoice.ini\n");

        Dlss5FeederFullDeploymentService.RestorePresetPath(ini, mutation);
        Assert.Contains(@"PresetPath=.\NewUserChoice.ini", File.ReadAllText(ini));
    }


    [Fact]
    public void FeederReleaseQueries_KeepArchiveAndDirectCompatibilityQueries()
    {
        var addon = Dlss5FeederFullDeploymentService.CreateFeederAddonReleaseQuery();
        var fx = Dlss5FeederFullDeploymentService.CreateFeederFxReleaseQuery();
        var archive = Dlss5FeederFullDeploymentService.CreateFeederArchiveReleaseQuery();

        Assert.Equal("dlss5-feed.addon64", addon.ExactAssetName);
        Assert.Equal("DLSS5_Feed.fx", fx.ExactAssetName);
        Assert.Null(addon.AssetNamePattern);
        Assert.Null(fx.AssetNamePattern);
        Assert.NotNull(archive.AssetNamePattern);
        Assert.Matches(archive.AssetNamePattern!, "DLSS5-Feeder-0.13.1-beta.1.zip");
    }

    [Fact]
    public void ValidateDownloadedPayload_AcceptsDirectAddonAndShader()
    {
        var addon = Path.Combine(_root, "dlss5-feed.addon64");
        var shader = Path.Combine(_root, "DLSS5_Feed.fx");
        var addonBytes = new byte[70 * 1024];
        addonBytes[0] = (byte)'M';
        addonBytes[1] = (byte)'Z';
        File.WriteAllBytes(addon, addonBytes);
        File.WriteAllText(shader, new string('x', 1024));

        Assert.Equal(addon, Dlss5FeederFullDeploymentService.ValidateDownloadedPayload(addon, "dlss5-feed.addon64", 64 * 1024, requirePe: true));
        Assert.Equal(shader, Dlss5FeederFullDeploymentService.ValidateDownloadedPayload(shader, "DLSS5_Feed.fx", 256, requirePe: false));
    }


    [Fact]
    public void ExtractReleasePayloadByName_HandlesWindowsBackslashEntriesOnLinux()
    {
        var zip = Path.Combine(_root, "feeder-windows-layout.zip");
        var destination = Path.Combine(_root, "DLSS5_Feed.fx");
        var payload = new string('x', 1024);

        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(@"shared\DLSS5_Feed.fx");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(payload);
        }

        var extracted = Dlss5FeederFullDeploymentService.ExtractReleasePayloadByName(
            zip, "DLSS5_Feed.fx", destination, minimumSize: 256, requirePe: false);

        Assert.Equal(destination, extracted);
        Assert.Equal(payload, File.ReadAllText(destination));
    }

    [Fact]
    public void FeederShaderRawUrls_PinFallbackToResolvedReleaseTag()
    {
        var urls = Dlss5FeederFullDeploymentService.BuildFeederShaderRawUrls("0.13.1-beta.1");

        Assert.Equal(2, urls.Count);
        Assert.Equal(
            "https://raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/v0.13.1-beta.1/shaders/DLSS5_Feed.fx",
            urls[0]);
        Assert.Equal(
            "https://raw.githubusercontent.com/jlrouzies-fr/DLSS5-Feeder/0.13.1-beta.1/shaders/DLSS5_Feed.fx",
            urls[1]);
    }


    [Fact]
    public async Task Install_BlocksWhileShortFuseManagedBeforeCompilerPackageOrNetwork()
    {
        var root = Path.Combine(_root, "shortfuse-feeder-block");
        var bin = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 128 * 1024);
        var reshade = Path.Combine(bin, "dxgi.dll");
        PeArchitectureDetectorTests.WritePe(reshade, 0x8664, 2 * 1024 * 1024);
        using (var stream = new FileStream(reshade, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 4096;
            stream.Write(System.Text.Encoding.ASCII.GetBytes("ReShade reshade.me crosire"));
        }
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var game = new DetectedGame
        {
            Name = "Feeder ShortFuse Guard",
            InstallPath = root,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Source = "Test",
            Runner = "Wine",
            GraphicsApi = GraphicsApi.DirectX12
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = bin,
            Files = []
        });

        using var http = new HttpClient();
        var files = new TransactionalFileDeploymentService();
        var dlss = new DlssManagerService(http, files, Path.Combine(root, "cache-dlss"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json");
        var legacy = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder,
            "dlss5-feed",
            "DLSS5 Feeder",
            "https://release.invalid/latest",
            64 * 1024,
            DeployFileName64: "dlss5-feed.addon64"), Path.Combine(root, "cache-legacy"));
        var feeder = new Dlss5FeederFullDeploymentService(
            http, records, dlss, legacy, Path.Combine(root, "cache-feeder"), new WineDllOverrideService());

        var result = await feeder.InstallAsync(game, deepFriedChickenPackagePath: null, d3dCompiler47Path: null);

        Assert.False(result.Success);
        Assert.Contains("ShortFuse", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(records.Find(game, ManagedComponentKind.Dlss5Feeder));
    }

    [Fact]
    public async Task Install_RenoDxConsumer_BlocksWhenOptiScalerDlssNrOwnsNeuralRuntimeBeforeCompilerOrNetwork()
    {
        var root = Path.Combine(_root, "optiscaler-dlssnr-conflict");
        var bin = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 128 * 1024);
        var game = new DetectedGame
        {
            Name = "Feeder OptiScaler NR Conflict",
            InstallPath = root,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Source = "Test",
            Runner = "Wine",
            GraphicsApi = GraphicsApi.DirectX11
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = bin,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OptiScalerDeploymentService.VariantMetadataKey] = OptiScalerVariant.DlssNr.ToString()
            },
            Files = []
        });

        using var http = new HttpClient();
        var files = new TransactionalFileDeploymentService();
        var dlss = new DlssManagerService(http, files, Path.Combine(root, "cache-dlss"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json");
        var legacy = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder,
            "dlss5-feed",
            "DLSS5 Feeder",
            "https://release.invalid/latest",
            64 * 1024,
            DeployFileName64: "dlss5-feed.addon64"), Path.Combine(root, "cache-legacy"));
        var feeder = new Dlss5FeederFullDeploymentService(
            http, records, dlss, legacy, Path.Combine(root, "cache-feeder"), new WineDllOverrideService());

        var result = await feeder.InstallAsync(
            game,
            Dlss5FeederConsumer.RenoDxDlss5,
            deepFriedChickenPackagePath: null,
            d3dCompiler47Path: null);

        Assert.False(result.Success);
        Assert.Contains("OptiScaler", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DLSS NR", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(records.Find(game, ManagedComponentKind.Dlss5Feeder));
    }

    [Fact]
    public async Task Remove_Dx9CoordinatorUnavailableFailsBeforeReShadeIniCleanup()
    {
        var root = Path.Combine(_root, "dx9-remove-preflight");
        var bin = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x014c, 128 * 1024);
        var ini = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=.\\RHI-DLSS5-Feeder.ini\n");
        var game = new DetectedGame
        {
            Name = "Feeder DX9 Remove Preflight",
            InstallPath = root,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Source = "Test",
            Runner = "Wine",
            GraphicsApi = GraphicsApi.DirectX9
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.Dlss5Feeder,
            BinaryDirectory = bin,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Feeder.FullStack"] = "1",
                ["Feeder.ReShadeIniPath"] = ini,
                ["Feeder.PresetPreviousExists"] = "0",
                ["Feeder.PresetSetValue"] = @".\RHI-DLSS5-Feeder.ini",
                [Dlss5FeederFullDeploymentService.MetaDx9Migration] = JsonSerializer.Serialize(new ReShadeDx9MigrationState(
                    Path.Combine(bin, "d3d9.dll"),
                    Path.Combine(bin, "dxgi.dll"),
                    "deadbeef",
                    new WineOverrideRecord { DllName = "d3d9", ChangedByRhi = true }))
            }
        });

        using var http = new HttpClient();
        var fileDeployment = new TransactionalFileDeploymentService();
        var dlss = new DlssManagerService(http, fileDeployment, Path.Combine(root, "cache-dlss"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json");
        var legacy = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder,
            "dlss5-feed",
            "DLSS5 Feeder",
            "https://release.invalid/latest",
            64 * 1024,
            DeployFileName64: "dlss5-feed.addon64"), Path.Combine(root, "cache-legacy"));
        var feeder = new Dlss5FeederFullDeploymentService(
            http, records, dlss, legacy, Path.Combine(root, "cache-feeder"), new WineDllOverrideService());

        var remove = await feeder.RemoveAsync(game);

        Assert.False(remove.Success);
        Assert.Contains("coordinator", remove.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"PresetPath=.\RHI-DLSS5-Feeder.ini", File.ReadAllText(ini));
        Assert.NotNull(records.Find(game, ManagedComponentKind.Dlss5Feeder));
    }

    [Fact]
    public void DeepFriedChickenBundle_OfficialLayoutExtractsAndReportsVersion()
    {
        var zip = Path.Combine(_root, "Deep-Fried-Chicken-v1.4.8-alpha.zip");
        var dest = Path.Combine(_root, "dfc-valid");
        CreateDeepFriedChickenZip(zip, "1.4.8-alpha", arm: true, corruptAddonHash: false, includeSums: true, windowsLayout: true);
        Assert.True(new FileInfo(zip).Length < 1024, "Regression fixture must remain highly compressible so outer ZIP size is not used as a validity signal.");

        var bundle = Dlss5FeederFullDeploymentService.ExtractDeepFriedChickenBundle(zip, dest);

        Assert.Equal("1.4.8-alpha", bundle.Version);
        Assert.True(File.Exists(bundle.AddonPath));
        Assert.True(File.Exists(bundle.BridgePath));
        Assert.True(File.Exists(bundle.ConfigPath));
        Assert.Equal("deep-fried-chicken.addon64", Path.GetFileName(bundle.AddonPath));
        Assert.Equal("deep-fried-chicken-nvngx.dll", Path.GetFileName(bundle.BridgePath));
    }

    [Fact]
    public async Task DeepFriedChickenBundle_AsyncProductionPathPreservesZipValidationContract()
    {
        var zip = Path.Combine(_root, "Deep-Fried-Chicken-v1.6.8-async.zip");
        var dest = Path.Combine(_root, "dfc-valid-async");
        CreateDeepFriedChickenZip(zip, "1.6.8", arm: true, corruptAddonHash: false, includeSums: true, windowsLayout: true);

        var bundle = await Dlss5FeederFullDeploymentService.ExtractDeepFriedChickenBundleAsync(zip, dest);

        Assert.Equal("1.6.8", bundle.Version);
        Assert.True(File.Exists(bundle.AddonPath));
        Assert.True(File.Exists(bundle.BridgePath));
        Assert.True(File.Exists(bundle.ConfigPath));
    }

    [Theory]
    [InlineData("Deep-Fried-Chicken-1.6.8.zip", true)]
    [InlineData("Deep-Fried-Chicken-1.6.8.7z", true)]
    [InlineData("Deep-Fried-Chicken-1.6.8.rar", false)]
    public void DeepFriedChickenArchiveSupport_AcceptsZipAnd7zOnly(string fileName, bool expected)
    {
        Assert.Equal(expected, Dlss5FeederFullDeploymentService.IsSupportedDeepFriedChickenArchive(fileName));
    }

    [Fact]
    public void DeepFriedChickenArchivePasswordDetection_RecognizesAesZipBehind7zFilename()
    {
        var archive = Path.Combine(_root, "Deep-Fried-Chicken-v1.6.8.7z");
        // ZIP local header + encrypted flag + WinZip AES method 99.
        File.WriteAllBytes(archive, [0x50, 0x4B, 0x03, 0x04, 0x33, 0x00, 0x01, 0x00, 0x63, 0x00]);

        Assert.True(Dlss5FeederFullDeploymentService.IsAesEncryptedZipArchive(archive));
    }

    [Fact]
    public void DeepFriedChickenArchivePassword_TracksCurrentPublicUpstreamPassword()
    {
        Assert.Equal("chicken", Dlss5FeederFullDeploymentService.DeepFriedChickenArchivePassword);
    }

    [Fact]
    public void DeepFriedChickenBundle_ExtractedDirectoryValidationPreservesHashContract()
    {
        var extracted = Path.Combine(_root, "dfc-extracted", "package", "bin");
        var destination = Path.Combine(_root, "dfc-extracted-validated");
        CreateDeepFriedChickenDirectory(extracted, "1.6.8", arm: true, corruptAddonHash: false, includeSums: true);

        var bundle = Dlss5FeederFullDeploymentService.ValidateExtractedDeepFriedChickenDirectory(
            Path.Combine(_root, "dfc-extracted"), destination);

        Assert.Equal("1.6.8", bundle.Version);
        Assert.True(File.Exists(bundle.AddonPath));
        Assert.True(File.Exists(bundle.BridgePath));
        Assert.True(File.Exists(bundle.ConfigPath));
    }

    [Fact]
    public void DeepFriedChickenBundle_RejectsHashMismatch()
    {
        var zip = Path.Combine(_root, "dfc-bad-hash.zip");
        CreateDeepFriedChickenZip(zip, "1.4.8-alpha", arm: true, corruptAddonHash: true, includeSums: true);

        var ex = Assert.Throws<InvalidDataException>(() =>
            Dlss5FeederFullDeploymentService.ExtractDeepFriedChickenBundle(zip, Path.Combine(_root, "dfc-bad-hash-out")));

        Assert.Contains("SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeepFriedChickenBundle_RejectsPackageWithoutSha256Sums()
    {
        var zip = Path.Combine(_root, "dfc-no-sums.zip");
        CreateDeepFriedChickenZip(zip, "1.4.8-alpha", arm: true, corruptAddonHash: false, includeSums: false);

        var ex = Assert.Throws<InvalidDataException>(() =>
            Dlss5FeederFullDeploymentService.ExtractDeepFriedChickenBundle(zip, Path.Combine(_root, "dfc-no-sums-out")));

        Assert.Contains("SHA256SUMS.txt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeepFriedChickenBundle_RejectsDisarmedConfig()
    {
        var zip = Path.Combine(_root, "dfc-disarmed.zip");
        CreateDeepFriedChickenZip(zip, "1.4.8-alpha", arm: false, corruptAddonHash: false, includeSums: true);

        var ex = Assert.Throws<InvalidDataException>(() =>
            Dlss5FeederFullDeploymentService.ExtractDeepFriedChickenBundle(zip, Path.Combine(_root, "dfc-disarmed-out")));

        Assert.Contains("arm=1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromDllMainMutation_AppendsAndRemovesOnlyRhiToken()
    {
        var ini = Path.Combine(_root, "reshade-addon-list.ini");
        File.WriteAllText(ini, "[ADDON]\nLoadFromDllMain=existing.addon64\n\n[GENERAL]\nPresetPath=.\\User.ini\n");

        var mutation = Dlss5FeederFullDeploymentService.EnsureLoadFromDllMainToken(ini, "deep-fried-chicken.addon64");
        Assert.True(mutation.AddedByRhi);
        Assert.Contains("LoadFromDllMain=existing.addon64,deep-fried-chicken.addon64", File.ReadAllText(ini));

        Dlss5FeederFullDeploymentService.RemoveLoadFromDllMainToken(ini, mutation);
        var text = File.ReadAllText(ini);
        Assert.Contains("LoadFromDllMain=existing.addon64", text);
        Assert.DoesNotContain("deep-fried-chicken.addon64", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromDllMainMutation_DoesNotOwnPreexistingToken()
    {
        var ini = Path.Combine(_root, "reshade-preexisting-dfc.ini");
        File.WriteAllText(ini, "[ADDON]\nLoadFromDllMain=deep-fried-chicken.addon64\n");

        var mutation = Dlss5FeederFullDeploymentService.EnsureLoadFromDllMainToken(ini, "deep-fried-chicken.addon64");
        Assert.False(mutation.AddedByRhi);

        Dlss5FeederFullDeploymentService.RemoveLoadFromDllMainToken(ini, mutation);
        Assert.Contains("deep-fried-chicken.addon64", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromDllMainMutation_PreservesUserTokenAddedLater()
    {
        var ini = Path.Combine(_root, "reshade-user-added-addon.ini");
        File.WriteAllText(ini, "[ADDON]\nDisabledAddons=Generic Depth\n");

        var mutation = Dlss5FeederFullDeploymentService.EnsureLoadFromDllMainToken(ini, "deep-fried-chicken.addon64");
        File.WriteAllText(ini, "[ADDON]\nLoadFromDllMain=deep-fried-chicken.addon64,user-later.addon64\nDisabledAddons=Generic Depth\n");

        Dlss5FeederFullDeploymentService.RemoveLoadFromDllMainToken(ini, mutation);
        var text = File.ReadAllText(ini);
        Assert.Contains("LoadFromDllMain=user-later.addon64", text);
        Assert.DoesNotContain("deep-fried-chicken.addon64", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromDllMainMutation_CreatesAddonSectionWhenMissing()
    {
        var ini = Path.Combine(_root, "reshade-no-addon-section.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=.\\User.ini\n");

        var mutation = Dlss5FeederFullDeploymentService.EnsureLoadFromDllMainToken(ini, "deep-fried-chicken.addon64");
        Assert.True(mutation.AddedByRhi);
        Assert.Contains("[ADDON]", File.ReadAllText(ini));
        Assert.Contains("LoadFromDllMain=deep-fried-chicken.addon64", File.ReadAllText(ini));

        Dlss5FeederFullDeploymentService.RemoveLoadFromDllMainToken(ini, mutation);
        Assert.DoesNotContain("LoadFromDllMain=", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateDeepFriedChickenDirectory(
        string directory,
        string version,
        bool arm,
        bool corruptAddonHash,
        bool includeSums)
    {
        Directory.CreateDirectory(directory);
        var addon = new byte[70 * 1024];
        addon[0] = (byte)'M';
        addon[1] = (byte)'Z';
        var bridge = new byte[4 * 1024];
        bridge[0] = (byte)'M';
        bridge[1] = (byte)'Z';
        var config = System.Text.Encoding.UTF8.GetBytes($"arm={(arm ? 1 : 0)}\nenabled=1\npasses=1\n");

        var addonHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(addon));
        var bridgeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bridge));
        if (corruptAddonHash)
            addonHash = new string('0', 64);

        File.WriteAllBytes(Path.Combine(directory, "deep-fried-chicken.addon64"), addon);
        File.WriteAllBytes(Path.Combine(directory, "deep-fried-chicken-nvngx.dll"), bridge);
        File.WriteAllBytes(Path.Combine(directory, "deep-fried-chicken.cfg"), config);
        if (includeSums)
        {
            var sums = $"Deep Fried Chicken {version}\n" +
                       "SHA-256 (release binaries only):\n" +
                       $"{addonHash}  deep-fried-chicken.addon64\n" +
                       $"{bridgeHash}  deep-fried-chicken-nvngx.dll\n";
            File.WriteAllText(Path.Combine(directory, "SHA256SUMS.txt"), sums);
        }
    }

    private static void CreateDeepFriedChickenZip(
        string zipPath,
        string version,
        bool arm,
        bool corruptAddonHash,
        bool includeSums,
        bool windowsLayout = false)
    {
        var addon = new byte[70 * 1024];
        addon[0] = (byte)'M';
        addon[1] = (byte)'Z';
        var bridge = new byte[4 * 1024];
        bridge[0] = (byte)'M';
        bridge[1] = (byte)'Z';
        var config = System.Text.Encoding.UTF8.GetBytes($"arm={(arm ? 1 : 0)}\nenabled=1\npasses=1\n");

        var addonHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(addon));
        var bridgeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bridge));
        if (corruptAddonHash)
            addonHash = new string('0', 64);

        using var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        var prefix = windowsLayout ? @"package\bin\" : "package/bin/";
        WriteEntry(archive, prefix + "deep-fried-chicken.addon64", addon);
        WriteEntry(archive, prefix + "deep-fried-chicken-nvngx.dll", bridge);
        WriteEntry(archive, prefix + "deep-fried-chicken.cfg", config);
        if (includeSums)
        {
            var sums = $"Deep Fried Chicken {version}\n" +
                       "SHA-256 (release binaries only):\n" +
                       $"{addonHash}  deep-fried-chicken.addon64\n" +
                       $"{bridgeHash}  deep-fried-chicken-nvngx.dll\n";
            WriteEntry(archive, prefix + "SHA256SUMS.txt", System.Text.Encoding.UTF8.GetBytes(sums));
        }
    }


    private static void WriteNativeCompiler(string path, bool unicodeMarkersOnly = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PeArchitectureDetectorTests.WritePe(path, 0x8664, 1_100_000);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = 4096;
        var markers = unicodeMarkersOnly
            ? System.Text.Encoding.Unicode.GetBytes("Microsoft (R) HLSL Shader Compiler 10.1 D3DCompiler_47.dll")
            : System.Text.Encoding.ASCII.GetBytes("Microsoft (R) HLSL Shader Compiler 10.1 D3DCompiler_47.dll");
        stream.Write(markers);
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive archive, string name, byte[] payload)
    {
        var entry = archive.CreateEntry(name);
        using var output = entry.Open();
        output.Write(payload);
    }



    [Fact]
    public void ReinstallPolicy_PreexistingDeepFriedConfig_IsReusableButProtectedBinariesStillBlock()
    {
        Assert.False(Dlss5FeederFullDeploymentService.IsBlockingPreexistingFullStackTarget("/game/deep-fried-chicken.cfg"));
        Assert.True(Dlss5FeederFullDeploymentService.IsBlockingPreexistingFullStackTarget("/game/deep-fried-chicken.addon64"));
        Assert.True(Dlss5FeederFullDeploymentService.IsBlockingPreexistingFullStackTarget("/game/nvngx_dlss.dll"));
    }

    [Fact]
    public void RemovePolicy_PreexistingConsumerConfig_IsAlwaysPreserved()
    {
        var file = new LinuxDeployedFile
        {
            Path = "/game/deep-fried-chicken.cfg",
            ExistedBeforeInstall = true,
            AllowUserChanges = true,
            Sha256 = "install-hash"
        };

        Assert.True(Dlss5FeederFullDeploymentService.ShouldPreserveOnRemove(file, "install-hash"));
        Assert.True(Dlss5FeederFullDeploymentService.ShouldPreserveOnRemove(file, "changed-hash"));
    }

    [Fact]
    public void FeederLayout_X86RenoDxUsesAddon32AndIsolates64BitConsumerInHost64()
    {
        var binaryDir = Path.Combine(_root, "x86-layout");
        var addonDir = Path.Combine(binaryDir, "addons");

        var layout = Dlss5FeederFullDeploymentService.ResolveFeederLayout(
            binaryDir, addonDir, PeArchitecture.X86, Dlss5FeederConsumer.RenoDxDlss5);

        Assert.Equal(Path.Combine(addonDir, "dlss5-feed.addon32"), layout.FeederTarget);
        Assert.Equal(Path.Combine(binaryDir, "host64"), layout.Host64Directory);
        Assert.Equal(Path.Combine(binaryDir, "host64", "dlss5-feed-host64.exe"), layout.Host64ExecutableTarget);
        Assert.Equal(Path.Combine(binaryDir, "host64", "renodx-dlss5.addon64"), layout.ConsumerTarget);
        Assert.Equal(Path.Combine(binaryDir, "host64", "nvngx_dlss.dll"), layout.DlssTarget);
        Assert.Equal(Path.Combine(binaryDir, "host64", "nvngx_dlssnr.dll"), layout.NrTarget);
        Assert.True(layout.RequiresHost64);
    }

    [Fact]
    public void FeederLayout_X64PreservesExistingTargets()
    {
        var binaryDir = Path.Combine(_root, "x64-layout");
        var addonDir = Path.Combine(binaryDir, "addons");

        var layout = Dlss5FeederFullDeploymentService.ResolveFeederLayout(
            binaryDir, addonDir, PeArchitecture.X64, Dlss5FeederConsumer.RenoDxDlss5);

        Assert.Equal(Path.Combine(addonDir, "dlss5-feed.addon64"), layout.FeederTarget);
        Assert.Equal(Path.Combine(addonDir, "renodx-dlss5.addon64"), layout.ConsumerTarget);
        Assert.Equal(Path.Combine(binaryDir, "nvngx_dlss.dll"), layout.DlssTarget);
        Assert.Equal(Path.Combine(binaryDir, "nvngx_dlssnr.dll"), layout.NrTarget);
        Assert.False(layout.RequiresHost64);
        Assert.Null(layout.Host64Directory);
        Assert.Null(layout.Host64ExecutableTarget);
    }

    [Fact]
    public void FeederLayout_RejectsUnprovenX86DeepFriedChickenConsumer()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            Dlss5FeederFullDeploymentService.ResolveFeederLayout(
                Path.Combine(_root, "x86-dfc"),
                Path.Combine(_root, "x86-dfc", "addons"),
                PeArchitecture.X86,
                Dlss5FeederConsumer.DeepFriedChicken));

        Assert.Contains("Deep Fried Chicken", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractReleasePayloadByName_FindsHost64HelperInWindowsArchiveLayout()
    {
        var zip = Path.Combine(_root, "feeder-host64.zip");
        var destination = Path.Combine(_root, "dlss5-feed-host64.exe");
        var payload = new byte[96 * 1024];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';

        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(@"host64\dlss5-feed-host64.exe");
            using var output = entry.Open();
            output.Write(payload);
        }

        var extracted = Dlss5FeederFullDeploymentService.ExtractReleasePayloadByName(
            zip, "dlss5-feed-host64.exe", destination, minimumSize: 64 * 1024, requirePe: true);

        Assert.Equal(destination, extracted);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public void RenoDxDlss5ReleaseQuery_TargetsDedicatedNeuralConsumerOnly()
    {
        var query = Dlss5FeederFullDeploymentService.CreateRenoDxDlss5ReleaseQuery();

        Assert.Equal("renodx-dlss5.addon64", query.ExactAssetName);
        Assert.Contains("renodx-dlss5", query.AssetNamePattern, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RankFTW/rhi-repo", query.ReleaseApiUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenoDxDlss5Consumer_DoesNotReuseDeepFriedChickenPackageContract()
    {
        Assert.NotEqual(Dlss5FeederConsumer.DeepFriedChicken, Dlss5FeederConsumer.RenoDxDlss5);
        Assert.False(Dlss5FeederFullDeploymentService.IsSupportedDeepFriedChickenArchive("renodx-dlss5.addon64"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
