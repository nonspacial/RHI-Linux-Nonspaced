using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class LumaFrameworkDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-tests-" + Guid.NewGuid().ToString("N"));

    public LumaFrameworkDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Eligibility_GenericUnrealDx11X64OnlyAndFailsClosedForUe5()
    {
        var game = CreateGame();
        Assert.True(LumaFrameworkDeploymentService.IsGameEligible(game));

        game.GraphicsApi = GraphicsApi.DirectX12;
        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game));

        game.GraphicsApi = GraphicsApi.DirectX11;
        game.EngineHint = "Unreal Engine 5.4";
        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game));

        game.EngineHint = "Unity";
        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game));

        game.EngineHint = "Unreal Engine 4.27";
        game.ForcedArchitecture = PeArchitecture.X86;
        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public void Eligibility_Ue5NeedsExplicitDx11AuthorityEvenWhenDetectorReportsDx11()
    {
        var game = CreateGame();
        game.EngineHint = "Unreal Engine 5.4";
        game.GraphicsApi = GraphicsApi.DirectX11;
        game.UserGraphicsApiOverride = null;

        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public void Eligibility_Ue5ExplicitDx11OverrideEnablesPinnedUpstreamException()
    {
        var game = CreateGame();
        game.EngineHint = "Unreal Engine 5.4";
        game.GraphicsApi = GraphicsApi.DirectX12;
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;

        Assert.True(LumaFrameworkDeploymentService.IsGameEligible(game));
        Assert.Contains("explicit DirectX 11 override", LumaFrameworkDeploymentService.EligibilityReason(game), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligibility_UnknownUnrealGenerationFailsClosedWithoutExplicitDx11()
    {
        var game = CreateGame();
        game.EngineHint = "Unreal Engine";
        game.GraphicsApi = GraphicsApi.DirectX11;
        game.UserGraphicsApiOverride = null;

        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game));
        Assert.Contains("generation", LumaFrameworkDeploymentService.EligibilityReason(game), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligibility_UnknownUnrealGenerationAllowsExplicitDx11Authority()
    {
        var game = CreateGame();
        game.EngineHint = "Unreal Engine";
        game.GraphicsApi = GraphicsApi.DirectX12;
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;

        Assert.True(LumaFrameworkDeploymentService.IsGameEligible(game));
        Assert.Contains("explicit DirectX 11 override", LumaFrameworkDeploymentService.EligibilityReason(game), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligibility_ExplicitDx12OverrideBlocksDetectedDx11()
    {
        var game = CreateGame();
        game.GraphicsApi = GraphicsApi.DirectX11;
        game.UserGraphicsApiOverride = GraphicsApi.DirectX12;

        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game));
        Assert.Contains("explicit API override", LumaFrameworkDeploymentService.EligibilityReason(game), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EligibilityReason_Ue5WithoutOverrideExplainsAuthorityRequirement()
    {
        var game = CreateGame();
        game.EngineHint = "Unreal Engine 5.3";
        game.GraphicsApi = GraphicsApi.DirectX11;

        var reason = LumaFrameworkDeploymentService.EligibilityReason(game);

        Assert.Contains("explicitly forces DirectX 11", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligibility_Ue4ExplicitDx11CanOverrideAmbiguousDetectedApi()
    {
        var game = CreateGame();
        game.GraphicsApi = GraphicsApi.DirectX12;
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;

        Assert.True(LumaFrameworkDeploymentService.IsGameEligible(game));
    }

    [Fact]
    public void RequiresDgVoodoo_UsesManifestAuthorityOnlyForDx9NamedGames()
    {
        var game = CreateGame();
        game.Name = "Borderlands 2";
        game.GraphicsApi = GraphicsApi.DirectX9;
        var manifestGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Borderlands 2", "Vanquish" };

        Assert.True(LumaFrameworkDeploymentService.RequiresDgVoodoo(game, manifestGames));

        game.GraphicsApi = GraphicsApi.DirectX11;
        Assert.False(LumaFrameworkDeploymentService.RequiresDgVoodoo(game, manifestGames));

        game.GraphicsApi = GraphicsApi.DirectX9;
        game.Name = "Unlisted DX9 Game";
        Assert.False(LumaFrameworkDeploymentService.RequiresDgVoodoo(game, manifestGames));
    }

    [Fact]
    public void RequiresDgVoodoo_AcceptsCanonicalManifestIdentity()
    {
        var game = CreateGame();
        game.Name = "Launcher Alias";
        game.CanonicalManifestName = "The Witcher 2: Assassins of Kings Enhanced Edition";
        game.GraphicsApi = GraphicsApi.DirectX9;
        var manifestGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "The Witcher 2: Assassins of Kings Enhanced Edition"
        };

        Assert.True(LumaFrameworkDeploymentService.RequiresDgVoodoo(game, manifestGames));
    }

    [Fact]
    public async Task ResolveLatest_SelectsGenericUnrealAssetAndPublishedDigest()
    {
        var zip = MakeLumaZip();
        var digest = Sha256(zip);
        using var http = new HttpClient(new FakeHandler(req =>
        {
            Assert.Equal(
                GitHubReleaseAssetResolver.NormalizeReleaseApiUrl(LumaFrameworkDeploymentService.LatestReleaseApiUrl),
                req.RequestUri!.AbsoluteUri);
            return JsonResponse("[" + ReleaseJson("latest-630", "https://github.com/rhi-tests/luma.zip", digest) + "]");
        }));
        var service = NewService(http, "resolve-records.json");

        var release = await service.ResolveLatestAsync();

        Assert.Equal("latest-630", release.Version);
        Assert.Equal(LumaFrameworkDeploymentService.GenericUnrealAssetName, release.AssetName);
        Assert.Equal(digest, release.Sha256);
    }

    [Fact]
    public async Task ExtractionPlan_SkipsBundledReShadeDlssAndRoutesAddonPath()
    {
        var game = CreateGame();
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "addons"));
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var zipPath = Path.Combine(_root, "layout.zip");
        File.WriteAllBytes(zipPath, MakeLumaZip());
        var staging = Path.Combine(_root, "staging-layout");
        Directory.CreateDirectory(staging);

        var plan = await LumaFrameworkDeploymentService.BuildExtractionPlanAsync(zipPath, game.BinaryDirectory!, staging, CancellationToken.None);
        var targets = plan.Select(x => Path.GetFullPath(x.TargetPath)).ToArray();

        Assert.Contains(Path.Combine(game.BinaryDirectory!, "addons", "Luma.addon64"), targets);
        Assert.Contains(Path.Combine(game.BinaryDirectory!, "Shaders", "Luma.fx"), targets);
        Assert.Contains(Path.Combine(game.BinaryDirectory!, "Config", "luma-settings.ini"), targets);
        Assert.DoesNotContain(targets, x => Path.GetFileName(x).Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targets, x => Path.GetFileName(x).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targets, x => Path.GetFileName(x).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targets, x => Path.GetFileName(x).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractionPlan_RoutesArchitectureNeutralAddonToConfiguredAddonPath()
    {
        var game = CreateGame();
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "addons"));
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var zipPath = Path.Combine(_root, "neutral-addon.zip");
        using (var file = File.Create(zipPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            WriteEntry(zip, "Luma.addon", Enumerable.Range(0, 96 * 1024).Select(i => (byte)(i * 11 + 5)).ToArray());
        var staging = Path.Combine(_root, "staging-neutral-addon");
        Directory.CreateDirectory(staging);

        var plan = await LumaFrameworkDeploymentService.BuildExtractionPlanAsync(zipPath, game.BinaryDirectory!, staging, CancellationToken.None);

        Assert.Contains(plan, item => item.TargetPath == Path.Combine(game.BinaryDirectory!, "addons", "Luma.addon"));
    }

    [Fact]
    public async Task PackagePlan_DirectArchitectureNeutralAddonRoutesWithoutZipExtraction()
    {
        var game = CreateGame();
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "addons"));
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var package = Path.Combine(_root, "Luma.addon");
        var bytes = new byte[96 * 1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(package, bytes);
        var staging = Path.Combine(_root, "staging-direct-addon");
        Directory.CreateDirectory(staging);

        var plan = await LumaFrameworkDeploymentService.BuildPackagePlanAsync(package, "Luma.addon", game.BinaryDirectory!, staging, CancellationToken.None);

        var item = Assert.Single(plan);
        Assert.Equal(Path.Combine(game.BinaryDirectory!, "addons", "Luma.addon"), item.TargetPath);
        Assert.True(File.Exists(item.StagingPath));
    }


    [Fact]
    public async Task InstallLocalPackage_DirectLumaAddonUsesManagedFlowWithoutNetworkAndDisablesAutoReinstall()
    {
        var game = CreateGame();
        var records = NewRecords("local-addon-records.json");
        SeedManagedReShade(records, game);
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "addons"));
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var package = Path.Combine(_root, "Luma-Custom.addon");
        File.WriteAllBytes(package, MakePePayload(0x8664, 96 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Local package install must not use network.")));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-local-addon"));

        var install = await service.InstallLocalPackageAsync(game, package);

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "addons", "Luma-Custom.addon")));
        var record = records.Find(game, ManagedComponentKind.LumaFramework);
        Assert.NotNull(record);
        Assert.Equal("1", record!.Metadata[LumaFrameworkDeploymentService.MetaLocalPackage]);
        Assert.Null(record.SourceUrl);

        var update = await service.CheckUpdateAsync(game);
        Assert.True(update.Managed);
        Assert.False(update.UpdateAvailable);
        Assert.Contains("Drop", update.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallLocalPackage_RejectsArchitectureMismatchBeforeMutatingManagedState()
    {
        var game = CreateGame(); // x64
        var records = NewRecords("local-addon-arch-records.json");
        SeedManagedReShade(records, game);
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "addons"));
        var iniPath = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        const string originalIni = "[ADDON]\nAddonPath=.\\addons\n\n[GENERAL]\nKeep=1\n";
        File.WriteAllText(iniPath, originalIni);
        var package = Path.Combine(_root, "Luma-Mismatch.addon32");
        File.WriteAllBytes(package, MakePePayload(0x014c, 96 * 1024));
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Architecture rejection must happen before network.")));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-local-addon-arch"));

        var install = await service.InstallLocalPackageAsync(game, package);

        Assert.False(install.Success);
        Assert.Contains("architecture", install.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalIni, File.ReadAllText(iniPath));
        Assert.Null(records.Find(game, ManagedComponentKind.LumaFramework));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "addons", "Luma-Mismatch.addon32")));
    }

    [Fact]
    public async Task ExtractionPlan_RejectsZipSlip()
    {
        var game = CreateGame();
        var zipPath = Path.Combine(_root, "zipslip.zip");
        using (var file = File.Create(zipPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("nope");
        }
        var staging = Path.Combine(_root, "staging-zipslip");
        Directory.CreateDirectory(staging);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LumaFrameworkDeploymentService.BuildExtractionPlanAsync(zipPath, game.BinaryDirectory!, staging, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Install_RequiresRhiManagedReShadeBeforeNetwork()
    {
        var game = CreateGame();
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[GENERAL]\n");
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var service = NewService(http, "require-rs-records.json");

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("RHI-managed ReShade", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_ProtectsExternalPackageTargetWithoutMutatingIni()
    {
        var game = CreateGame();
        var records = NewRecords("external-records.json");
        SeedManagedReShade(records, game);
        var iniPath = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        var ini = "[ADDON]\nAddonPath=.\\addons\n\n[GENERAL]\nKeep=1\n";
        File.WriteAllText(iniPath, ini);
        var external = Path.Combine(game.BinaryDirectory!, "Shaders", "Luma.fx");
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        File.WriteAllText(external, "external-user-file");
        var zip = MakeLumaZip();
        var digest = Sha256(zip);
        using var http = BuildHttp(zip, digest);
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-external"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("Foreign Luma protection", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external-user-file", File.ReadAllText(external));
        Assert.Equal(ini, File.ReadAllText(iniPath));
        Assert.Null(records.Find(game, ManagedComponentKind.LumaFramework));
    }

    [Fact]
    public async Task InstallAndRemove_DeploysOwnedFilesPreservesRhiProxiesAndRestoresLumaIni()
    {
        var game = CreateGame();
        var records = NewRecords("lifecycle-records.json");
        SeedManagedReShade(records, game);
        var iniPath = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        File.WriteAllText(iniPath,
            "[ADDON]\nAddonPath=.\\addons\n\n[Luma]\nEnableHDR=0\nUserSetting=keep\n\n[GENERAL]\nKeep=1\n");
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var dlss = Path.Combine(game.BinaryDirectory!, "nvngx_dlss.dll");
        File.WriteAllText(dxgi, "rhi-reshade-proxy");
        File.WriteAllText(dlss, "rhi-dlss-runtime");
        var zip = MakeLumaZip();
        var digest = Sha256(zip);
        using var http = BuildHttp(zip, digest);
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-lifecycle"));

        var install = await service.InstallAsync(game);

        Assert.True(install.Success, install.Message);
        var addon = Path.Combine(game.BinaryDirectory!, "addons", "Luma.addon64");
        var shader = Path.Combine(game.BinaryDirectory!, "Shaders", "Luma.fx");
        var config = Path.Combine(game.BinaryDirectory!, "Config", "luma-settings.ini");
        Assert.True(File.Exists(addon));
        Assert.True(File.Exists(shader));
        Assert.True(File.Exists(config));
        Assert.Equal("rhi-reshade-proxy", File.ReadAllText(dxgi));
        Assert.Equal("rhi-dlss-runtime", File.ReadAllText(dlss));
        var installedIni = File.ReadAllText(iniPath);
        Assert.Contains("EnableHDR=1", installedIni);
        Assert.Contains("DisplayMode=1", installedIni);
        Assert.Contains("UserSetting=keep", installedIni);
        var record = records.Find(game, ManagedComponentKind.LumaFramework);
        Assert.NotNull(record);
        Assert.Equal(3, record!.Files.Count);
        Assert.All(record.Files, file => Assert.False(string.IsNullOrWhiteSpace(file.Sha256)));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(addon));
        Assert.False(File.Exists(shader));
        Assert.False(File.Exists(config));
        Assert.Equal("rhi-reshade-proxy", File.ReadAllText(dxgi));
        Assert.Equal("rhi-dlss-runtime", File.ReadAllText(dlss));
        var removedIni = File.ReadAllText(iniPath);
        Assert.Contains("EnableHDR=0", removedIni);
        Assert.DoesNotContain("DisplayMode=1", removedIni);
        Assert.Contains("UserSetting=keep", removedIni);
        Assert.Null(records.Find(game, ManagedComponentKind.LumaFramework));
    }

    [Fact]
    public async Task Remove_BlocksModifiedOwnedFileBeforeIniCleanup()
    {
        var game = CreateGame();
        var records = NewRecords("modified-records.json");
        SeedManagedReShade(records, game);
        var iniPath = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        File.WriteAllText(iniPath, "[ADDON]\nAddonPath=.\\addons\n");
        var zip = MakeLumaZip();
        var digest = Sha256(zip);
        using var http = BuildHttp(zip, digest);
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-modified"));
        var install = await service.InstallAsync(game);
        Assert.True(install.Success, install.Message);
        var shader = Path.Combine(game.BinaryDirectory!, "Shaders", "Luma.fx");
        File.AppendAllText(shader, "user-edit");

        var remove = await service.RemoveAsync(game);

        Assert.False(remove.Success);
        Assert.Contains("modified", remove.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(shader));
        Assert.Contains("EnableHDR=1", File.ReadAllText(iniPath));
        Assert.NotNull(records.Find(game, ManagedComponentKind.LumaFramework));
    }

    [Fact]
    public async Task Remove_Dx9CoordinatorUnavailableFailsBeforeLumaIniCleanup()
    {
        var game = CreateGame();
        game.GraphicsApi = GraphicsApi.DirectX9;
        var records = NewRecords("dx9-remove-preflight-records.json");
        var iniPath = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        File.WriteAllText(iniPath, "[ADDON]\nAddonPath=.\\addons\n");
        var iniState = LumaIniOverrideManager.Apply(game.BinaryDirectory!);
        Assert.Contains("EnableHDR=1", File.ReadAllText(iniPath));

        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.LumaFramework,
            BinaryDirectory = game.BinaryDirectory!,
            Version = "test-dx9",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [LumaIniOverrideManager.MetadataKey] = LumaIniOverrideManager.Serialize(iniState),
                [LumaFrameworkDeploymentService.MetaDx9Migration] = JsonSerializer.Serialize(new ReShadeDx9MigrationState(
                    Path.Combine(game.BinaryDirectory!, "d3d9.dll"),
                    Path.Combine(game.BinaryDirectory!, "dxgi.dll"),
                    "deadbeef",
                    new WineOverrideRecord { DllName = "d3d9", ChangedByRhi = true }))
            }
        });
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("Network must not be used.")));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-dx9-remove-preflight"));

        var remove = await service.RemoveAsync(game);

        Assert.False(remove.Success);
        Assert.Contains("coordinator", remove.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EnableHDR=1", File.ReadAllText(iniPath));
        Assert.Contains("DisplayMode=1", File.ReadAllText(iniPath));
        Assert.NotNull(records.Find(game, ManagedComponentKind.LumaFramework));
    }

    [Fact]
    public void ExistingDx9Translation_RequiresSafeRemovalBeforeRequirementCanDisappear()
    {
        var record = new LinuxDeploymentRecord
        {
            Component = ManagedComponentKind.LumaFramework,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [LumaFrameworkDeploymentService.MetaDx9Migration] = "{\"OriginalPath\":\"d3d9.dll\"}"
            }
        };

        Assert.True(LumaFrameworkDeploymentService.RequiresDx9RemovalBeforeUpdate(record, newRequiresDgVoodoo: false));
        Assert.False(LumaFrameworkDeploymentService.RequiresDx9RemovalBeforeUpdate(record, newRequiresDgVoodoo: true));
        Assert.False(LumaFrameworkDeploymentService.RequiresDx9RemovalBeforeUpdate(null, newRequiresDgVoodoo: false));
    }

    [Fact]
    public async Task Remove_PreservesUserChangedLumaIniValue()
    {
        var game = CreateGame();
        var records = NewRecords("user-ini-records.json");
        SeedManagedReShade(records, game);
        var iniPath = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        File.WriteAllText(iniPath, "[ADDON]\nAddonPath=.\\addons\n");
        var zip = MakeLumaZip();
        var digest = Sha256(zip);
        using var http = BuildHttp(zip, digest);
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-user-ini"));
        var install = await service.InstallAsync(game);
        Assert.True(install.Success, install.Message);
        var text = File.ReadAllText(iniPath).Replace("EnableHDR=1", "EnableHDR=7", StringComparison.Ordinal);
        File.WriteAllText(iniPath, text);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        var after = File.ReadAllText(iniPath);
        Assert.Contains("EnableHDR=7", after);
        Assert.DoesNotContain("DisplayMode=1", after);
    }

    [Fact]
    public async Task Install_GenericWikiMetadataRecordsFieldsAppliesOwnedEngineIniAndRollsBackOnRemove()
    {
        const string wikiUrl = "https://github.com/rhi-tests/luma-generic-wiki";
        var root = Path.Combine(_root, "metadata-lifecycle");
        var binary = Path.Combine(root, "GameProject", "Binaries", "Win64");
        var prefix = Path.Combine(root, "pfx");
        var localAppData = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local");
        var engineConfig = Path.Combine(localAppData, "MetaProject", "Saved", "Config", "Windows");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(engineConfig);
        var exe = Path.Combine(binary, "GameProject-Win64-Shipping.exe");
        File.WriteAllBytes(exe, MakePePayload(0x8664, 4096));
        var game = new DetectedGame
        {
            Name = "Generic UE Metadata Test",
            InstallPath = root,
            BinaryDirectory = binary,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Source = "Steam",
            Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX11,
            EngineHint = "Unreal Engine 4.27",
            EngineIniProjectOverride = "MetaProject"
        };
        Directory.CreateDirectory(Path.Combine(binary, "addons"));
        File.WriteAllText(Path.Combine(binary, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var engineIni = Path.Combine(engineConfig, "Engine.ini");
        File.WriteAllText(engineIni, "[SystemSettings]\nUserKeep=42\n");

        var records = NewRecords("metadata-records.json");
        SeedManagedReShade(records, game);
        var zip = MakeLumaZip();
        var digest = Sha256(zip);
        var wiki = """
        <table>
          <tr><th>Name</th><th>HDR</th><th>DLSS / FSR</th><th>Notes</th><th>UE Version</th></tr>
          <tr><td>Generic UE Metadata Test</td><td>✅</td><td>✅</td>
          <td>Launch argument: <code>-dx11</code><details><summary>Modify Engine.ini</summary><code>[SystemSettings]</code><br/><code>r.Luma.Metadata=1</code></details></td>
          <td>4.27.2</td></tr>
        </table>
        """;
        using var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            wikiUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wiki, Encoding.UTF8, "text/html") },
            var url when url == GitHubReleaseAssetResolver.NormalizeReleaseApiUrl(LumaFrameworkDeploymentService.LatestReleaseApiUrl)
                => JsonResponse("[" + ReleaseJson("latest-630", "https://github.com/rhi-tests/luma-metadata.zip", digest) + "]"),
            "https://github.com/rhi-tests/luma-metadata.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) },
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-metadata"), catalog);

        var install = await service.InstallAsync(game);

        Assert.True(install.Success, install.Message);
        var record = records.Find(game, ManagedComponentKind.LumaFramework);
        Assert.NotNull(record);
        Assert.Equal("Generic UE Metadata Test", record!.Metadata["LumaFramework.GenericName"]);
        Assert.Equal("-dx11", record.Metadata["LumaFramework.LaunchArgs"]);
        Assert.Equal("1", record.Metadata["LumaFramework.HdrSupported"]);
        Assert.Equal("1", record.Metadata["LumaFramework.DlssFsrSupported"]);
        Assert.Equal("0", record.Metadata["LumaFramework.DlssFsrBlocked"]);
        Assert.Equal("4.27.2", record.Metadata["LumaFramework.UeVersion"]);
        Assert.False(string.IsNullOrWhiteSpace(record.Metadata[LumaEngineIniOverrideManager.MetadataKey]));
        var afterInstall = File.ReadAllText(engineIni);
        Assert.Contains("UserKeep=42", afterInstall);
        Assert.Contains("r.Luma.Metadata=1", afterInstall);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        var afterRemove = File.ReadAllText(engineIni);
        Assert.Contains("UserKeep=42", afterRemove);
        Assert.DoesNotContain("r.Luma.Metadata=", afterRemove);
        Assert.Null(records.Find(game, ManagedComponentKind.LumaFramework));
    }

    [Fact]
    public void Eligibility_GenericWikiDlssBlockedWithoutHdrSuppressesGenericLuma()
    {
        var game = CreateGame();
        var metadata = new LumaGenericGameEntry(
            game.Name,
            "DLSS/FSR path is blocked",
            Array.Empty<LumaEngineIniEntry>(),
            null,
            HdrSupported: false,
            DlssFsrSupported: false,
            DlssFsrBlocked: true,
            UeVersion: "4.27");

        Assert.False(LumaFrameworkDeploymentService.IsGameEligible(game, metadata));
        Assert.Contains("blocks DLSS/FSR", LumaFrameworkDeploymentService.EligibilityReason(game, metadata), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComponentProbe_LumaNeedsAddonFootprintNotArbitraryLumaNamedFile()
    {
        var game = CreateGame();
        var unrelated = Path.Combine(game.BinaryDirectory!, "luma-notes.txt");
        File.WriteAllText(unrelated, "unrelated");
        var withoutAddon = ComponentProbe.Probe(game, [unrelated]);
        Assert.Equal(ComponentAvailability.Available,
            withoutAddon.Single(c => c.Kind == ManagedComponentKind.LumaFramework).Availability);

        var addon = Path.Combine(game.BinaryDirectory!, "Luma.addon64");
        File.WriteAllText(addon, "addon");
        var withAddon = ComponentProbe.Probe(game, [unrelated, addon]);
        Assert.Equal(ComponentAvailability.Installed,
            withAddon.Single(c => c.Kind == ManagedComponentKind.LumaFramework).Availability);
    }

    private LumaFrameworkDeploymentService NewService(HttpClient http, string recordsName)
        => new(http, NewRecords(recordsName), Path.Combine(_root, "cache-" + recordsName));

    private LinuxDeploymentRecordStore NewRecords(string name)
        => new(Path.Combine(_root, name));

    private static void SeedManagedReShade(LinuxDeploymentRecordStore records, DetectedGame game)
    {
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!,
            Version = "test"
        });
    }

    private DetectedGame CreateGame()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "game.exe");
        File.WriteAllBytes(exe, MakePePayload(0x8664, 4096));
        return new DetectedGame
        {
            Name = "Generic UE4 Test",
            InstallPath = dir,
            BinaryDirectory = dir,
            ExecutablePath = exe,
            PrefixPath = Path.Combine(dir, "pfx"),
            Source = "Steam",
            Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX11,
            EngineHint = "Unreal Engine 4.27"
        };
    }

    private HttpClient BuildHttp(byte[] zip, string digest)
        => new(new FakeHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == GitHubReleaseAssetResolver.NormalizeReleaseApiUrl(LumaFrameworkDeploymentService.LatestReleaseApiUrl))
                return JsonResponse("[" + ReleaseJson("latest-630", "https://github.com/rhi-tests/luma.zip", digest) + "]");
            if (url == "https://github.com/rhi-tests/luma.zip")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));

    private static string ReleaseJson(string tag, string url, string digest)
        => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            assets = new[]
            {
                new
                {
                    name = LumaFrameworkDeploymentService.GenericUnrealAssetName,
                    browser_download_url = url,
                    digest = "sha256:" + digest
                }
            }
        });

    private static byte[] MakeLumaZip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "Luma.addon64", Enumerable.Range(0, 96 * 1024).Select(i => (byte)(i * 13 + 7)).ToArray());
            WriteEntry(zip, "Shaders/Luma.fx", Encoding.UTF8.GetBytes("// luma shader\n" + new string('x', 2048)));
            WriteEntry(zip, "Config/luma-settings.ini", Encoding.UTF8.GetBytes("[Luma]\nTest=1\n"));
            WriteEntry(zip, "dxgi.dll", Encoding.UTF8.GetBytes("bundled proxy must be skipped"));
            WriteEntry(zip, "ReShade64.dll", Encoding.UTF8.GetBytes("bundled reshade must be skipped"));
            WriteEntry(zip, "ReShade.ini", Encoding.UTF8.GetBytes("bundled ini must be skipped"));
            WriteEntry(zip, "nvngx_dlss.dll", Encoding.UTF8.GetBytes("bundled dlss must be skipped"));
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] MakePePayload(ushort machine, int size)
    {
        var bytes = new byte[Math.Max(size, 512)];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
