using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class OptiScalerAdvancedSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optiscaler-advanced-tests-" + Guid.NewGuid().ToString("N"));

    public OptiScalerAdvancedSettingsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Snapshot_ExternalOptiScalerIsProtected()
    {
        var (game, store, _) = CreateGame("external", managed: false);
        var service = NewService(store);

        var snapshot = service.GetAdvancedSettings(game);

        Assert.False(snapshot.ManagedByRhi);
        Assert.Empty(snapshot.Settings);
        Assert.Contains("external", snapshot.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_ManagedRecordWithMissingIniReportsMissingFile()
    {
        var (game, store, ini) = CreateGame("missing", managed: true);
        File.Delete(ini);
        var service = NewService(store);

        var snapshot = service.GetAdvancedSettings(game);

        Assert.True(snapshot.ManagedByRhi);
        Assert.Empty(snapshot.Settings);
        Assert.Contains("missing", snapshot.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_ExposesPinnedFileLocalControlsButNotRhiChainOwnership()
    {
        var (game, store, _) = CreateGame("surface", managed: true);
        var service = NewService(store);

        var snapshot = service.GetAdvancedSettings(game);
        var ids = snapshot.Settings.Select(setting => setting.Id).ToArray();

        Assert.Contains("Dx11Upscaler", ids);
        Assert.Contains("Dx12Upscaler", ids);
        Assert.Contains("VulkanUpscaler", ids);
        Assert.Contains("FGInput", ids);
        Assert.Contains("FGOutput", ids);
        Assert.Contains("SrPreset", ids);
        Assert.Contains("RrPreset", ids);
        Assert.Contains("RenderScale", ids);
        Assert.Contains("DisableFlipMetering", ids);
        Assert.Contains("FramerateLimit", ids);
        Assert.Contains("MenuShortcutKey", ids);
        Assert.DoesNotContain("LoadReshade", ids);
        Assert.Contains("chain-owned", snapshot.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_NightlyExposesWindowsStreamlineDlssEnablerAndFgControls()
    {
        var (game, store, _) = CreateGame("nightly-surface", managed: true, variant: OptiScalerVariant.Nightly);
        var service = NewService(store);

        var snapshot = service.GetAdvancedSettings(game);
        var deploy = snapshot.Settings.Single(setting => setting.Id == "DeployStreamlineDlssEnabler");
        var version = snapshot.Settings.Single(setting => setting.Id == "StreamlineVersion");
        var input = snapshot.Settings.Single(setting => setting.Id == "FGInput");
        var output = snapshot.Settings.Single(setting => setting.Id == "FGOutput");
        var replacement = snapshot.Settings.Single(setting => setting.Id == "FGNvngxReplacement");

        Assert.Contains(deploy.Options, option => option.Value == "true" && option.Name == "Yes");
        Assert.Equal("2.12.0", version.CurrentValue);
        Assert.Contains(input.Options, option => option.Value == "dlssg" && option.Name == "DLSSG via Streamline");
        Assert.Contains(input.Options, option => option.Value == "nvngxfg" && option.Name == "DLSSG via Nvngx");
        Assert.Contains(output.Options, option => option.Value == "dlssg" && option.Name == "DLSSG");
        Assert.Contains(replacement.Options, option => option.Value == "Arturs" && option.Name == "Enabler");
    }

    [Fact]
    public void Snapshot_StableDoesNotExposeManagedStreamlineDeploymentToggle()
    {
        var (game, store, _) = CreateGame("stable-surface", managed: true, variant: OptiScalerVariant.Stable);
        var service = NewService(store);

        var ids = service.GetAdvancedSettings(game).Settings.Select(setting => setting.Id).ToArray();

        Assert.DoesNotContain("DeployStreamlineDlssEnabler", ids);
        Assert.DoesNotContain("StreamlineVersion", ids);
    }

    [Fact]
    public void Snapshot_ReadsExistingValuesAndPreservesUnknownCurrentChoice()
    {
        var (game, store, ini) = CreateGame("read", managed: true);
        File.WriteAllText(ini,
            "[Upscalers]\nDx12Upscaler=my-custom-upscaler\n\n[FrameGen]\nFGInput=fsrfg\nFGOutput=xefg\n\n[Framerate]\nFramerateLimit=120.000000\n");
        var service = NewService(store);

        var snapshot = service.GetAdvancedSettings(game);
        var dx12 = snapshot.Settings.Single(setting => setting.Id == "Dx12Upscaler");

        Assert.Equal("my-custom-upscaler", dx12.CurrentValue);
        Assert.Contains(dx12.Options, option => option.Value == "my-custom-upscaler");
        Assert.Equal("fsrfg", snapshot.Settings.Single(setting => setting.Id == "FGInput").CurrentValue);
        Assert.Equal("xefg", snapshot.Settings.Single(setting => setting.Id == "FGOutput").CurrentValue);
        Assert.Equal("120.000000", snapshot.Settings.Single(setting => setting.Id == "FramerateLimit").CurrentValue);
    }

    [Fact]
    public void SetAdvancedSetting_DirectChoicesWriteAtomicallyAndPreserveUnrelatedContent()
    {
        var (game, store, ini) = CreateGame("direct", managed: true);
        File.WriteAllText(ini, "; keep me\n[Plugins]\nLoadReshade=true\n[User]\nCustom=42\n");
        var service = NewService(store);

        Assert.True(service.SetAdvancedSetting(game, "Dx11Upscaler", "fsr31_12").Success);
        Assert.True(service.SetAdvancedSetting(game, "Dx12Upscaler", "fsr31").Success);
        Assert.True(service.SetAdvancedSetting(game, "VulkanUpscaler", "fsr31").Success);
        Assert.True(service.SetAdvancedSetting(game, "FrameGenEnabled", "true").Success);
        Assert.True(service.SetAdvancedSetting(game, "FGInput", "upscaler").Success);
        Assert.True(service.SetAdvancedSetting(game, "FGOutput", "fsrfg").Success);
        Assert.True(service.SetAdvancedSetting(game, "FGNvngxReplacement", "Arturs").Success);
        Assert.True(service.SetAdvancedSetting(game, "HUDFix", "true").Success);
        Assert.True(service.SetAdvancedSetting(game, "DisableHUDFix", "false").Success);
        Assert.True(service.SetAdvancedSetting(game, "DisableFlipMetering", "false").Success);
        Assert.True(service.SetAdvancedSetting(game, "FramerateLimit", "120.000000").Success);
        Assert.True(service.SetAdvancedSetting(game, "MenuShortcutKey", "0x2D").Success);

        var text = File.ReadAllText(ini);
        Assert.Contains("; keep me", text);
        Assert.Contains("Custom=42", text);
        Assert.Contains("Dx11Upscaler=fsr31_12", text);
        Assert.Contains("Dx12Upscaler=fsr31", text);
        Assert.Contains("VulkanUpscaler=fsr31", text);
        Assert.Contains("Enabled=true", text);
        Assert.Contains("FGInput=upscaler", text);
        Assert.Contains("FGOutput=fsrfg", text);
        Assert.Contains("FGNvngxReplacement=Arturs", text);
        Assert.Contains("HUDFix=true", text);
        Assert.Contains("DisableHUDFix=false", text);
        Assert.Contains("DisableFlipMetering=false", text);
        Assert.Contains("FramerateLimit=120.000000", text);
        Assert.Contains("ShortcutKey=0x2D", text);
        Assert.Contains("LoadReshade=true", text, StringComparison.OrdinalIgnoreCase);
    }

    // #273 (upstream 2.9.3): FG Nvngx Override was greyed out and not saved while FG Input was "DLSSG via Nvngx", where
    // OptiScaler uses it. Linux never greys a row; this pins that it is offered, written and read back with that input.
    [Fact]
    public void FgNvngxReplacement_WithDlssgViaNvngxInput_IsOfferedAndSaved()
    {
        var (game, store, ini) = CreateGame("nvngx-input", managed: true);
        File.WriteAllText(ini, "[FrameGen]\nFGInput=nvngxfg\nFGNvngxReplacement=None\n");
        var service = NewService(store);

        Assert.Contains(service.GetAdvancedSettings(game).Settings, setting => setting.Id == "FGNvngxReplacement");
        Assert.True(service.SetAdvancedSetting(game, "FGNvngxReplacement", "Arturs").Success);

        var settings = service.GetAdvancedSettings(game).Settings;
        Assert.Equal("nvngxfg", settings.Single(setting => setting.Id == "FGInput").CurrentValue);
        Assert.Equal("Arturs", settings.Single(setting => setting.Id == "FGNvngxReplacement").CurrentValue);
    }

    // #273 (upstream 2.8.9): changing the Upscaler API also changed the game's upscaler. Linux has no API selector: each
    // API has its own upscaler row, so a change to one never touches another.
    [Fact]
    public void UpscalerRows_ChangingOneApi_LeavesTheOthersAsTheyWere()
    {
        var (game, store, ini) = CreateGame("upscaler-api", managed: true);
        File.WriteAllText(ini, "[Upscalers]\nDx11Upscaler=xess\nDx12Upscaler=dlss\nVulkanUpscaler=fsr22\n");
        var service = NewService(store);

        Assert.True(service.SetAdvancedSetting(game, "Dx11Upscaler", "fsr22").Success);

        var settings = service.GetAdvancedSettings(game).Settings;
        Assert.Equal("fsr22", settings.Single(setting => setting.Id == "Dx11Upscaler").CurrentValue);
        Assert.Equal("dlss", settings.Single(setting => setting.Id == "Dx12Upscaler").CurrentValue);
        Assert.Equal("fsr22", settings.Single(setting => setting.Id == "VulkanUpscaler").CurrentValue);
    }

    // #270 (upstream 2.9.0): DXGI Spoofing ([Spoofing] Dxgi) on every version, Auto / True / False, after the frame-rate limit.
    [Theory]
    [InlineData(OptiScalerVariant.Stable)]
    [InlineData(OptiScalerVariant.Nightly)]
    [InlineData(OptiScalerVariant.F5)]
    public void DxgiSpoofing_IsOfferedOnEveryVersion_AndWritesSpoofingDxgi(OptiScalerVariant variant)
    {
        var (game, store, ini) = CreateGame("dxgi-" + variant, managed: true, variant);
        File.WriteAllText(ini, "[Spoofing]\nDxgi=auto\nVulkan=auto\n");
        var service = NewService(store);

        var rows = service.GetAdvancedSettings(game).Settings;
        var dxgi = rows.Single(setting => setting.Id == OptiScalerDeploymentService.DxgiSpoofingSettingId);
        Assert.Equal(["auto", "true", "false"], dxgi.Options.Select(option => option.Value));
        Assert.Equal("auto", dxgi.CurrentValue);
        Assert.Equal(rows.ToList().FindIndex(setting => setting.Id == "FramerateLimit") + 1, rows.ToList().IndexOf(dxgi));

        Assert.True(service.SetAdvancedSetting(game, OptiScalerDeploymentService.DxgiSpoofingSettingId, "false").Success);
        Assert.Contains("Dxgi=false", File.ReadAllText(ini));
        Assert.Contains("Vulkan=auto", File.ReadAllText(ini));
        Assert.Equal("false", service.GetAdvancedSettings(game).Settings.Single(setting => setting.Id == OptiScalerDeploymentService.DxgiSpoofingSettingId).CurrentValue);
        Assert.False(service.SetAdvancedSetting(game, OptiScalerDeploymentService.DxgiSpoofingSettingId, "yes").Success);
    }

    // #272 (upstream 2.9.3 FindCustomFgProviders): FG Nvngx Override lists the custom frame generation providers in
    // OptiScaler's custom folder (a folder with a dlssg folder holding a DLL) by name, and saves one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FgNvngxReplacement_ListsCustomFgProvidersByName_AndSavesOne(bool optiScalerFolder)
    {
        var (game, store, ini) = CreateGame("custom-fg-" + optiScalerFolder, managed: true, OptiScalerVariant.Nightly);
        File.WriteAllText(ini, "[FrameGen]\nFGInput=nvngxfg\nFGNvngxReplacement=None\n");
        var bin = Path.GetDirectoryName(ini)!;
        var custom = optiScalerFolder ? Path.Combine(bin, "OptiScaler", "custom") : Path.Combine(bin, "custom");
        Directory.CreateDirectory(Path.Combine(custom, "Apex Neural Rendering FG", "dlssg"));
        File.WriteAllText(Path.Combine(custom, "Apex Neural Rendering FG", "dlssg", "nr_fg.dll"), "MZ");
        Directory.CreateDirectory(Path.Combine(custom, "Empty", "dlssg")); // no DLL: not a provider
        Directory.CreateDirectory(Path.Combine(custom, "Upscaler", "fsr")); // not a frame generation provider
        var service = NewService(store);

        var row = service.GetAdvancedSettings(game).Settings.Single(setting => setting.Id == "FGNvngxReplacement");
        Assert.Equal(["None", "Nukems", "Arturs", "FFX", "Combo", "Apex Neural Rendering FG"], row.Options.Select(option => option.Value));
        Assert.Equal("Apex Neural Rendering FG", row.Options[^1].Name);

        Assert.True(service.SetAdvancedSetting(game, "FGNvngxReplacement", "Apex Neural Rendering FG").Success);
        Assert.Contains("FGNvngxReplacement=Apex Neural Rendering FG", File.ReadAllText(ini));
        Assert.False(service.SetAdvancedSetting(game, "FGNvngxReplacement", "Empty").Success);
    }

    [Fact]
    public void SetAdvancedSetting_InvalidChoiceIsRejectedWithoutChangingIni()
    {
        var (game, store, ini) = CreateGame("invalid", managed: true);
        var before = File.ReadAllText(ini);
        var service = NewService(store);

        var result = service.SetAdvancedSetting(game, "FGOutput", "totally-not-an-output");

        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllText(ini));
    }

    [Fact]
    public void SetAdvancedSetting_SrPresetTogglesOverrideAndPresetTogether()
    {
        var (game, store, ini) = CreateGame("sr", managed: true);
        var service = NewService(store);

        Assert.True(service.SetAdvancedSetting(game, "SrPreset", "13").Success);
        var enabled = File.ReadAllText(ini);
        Assert.Contains("RenderPresetOverride=true", enabled);
        Assert.Contains("RenderPresetForAll=13", enabled);
        Assert.Equal("13", service.GetAdvancedSettings(game).Settings.Single(setting => setting.Id == "SrPreset").CurrentValue);

        Assert.True(service.SetAdvancedSetting(game, "SrPreset", "auto").Success);
        var disabled = File.ReadAllText(ini);
        Assert.Contains("RenderPresetOverride=false", disabled);
        Assert.Contains("RenderPresetForAll=0", disabled);
        Assert.Equal("auto", service.GetAdvancedSettings(game).Settings.Single(setting => setting.Id == "SrPreset").CurrentValue);
    }

    [Fact]
    public void SetAdvancedSetting_RrPresetUsesDlssdSection()
    {
        var (game, store, ini) = CreateGame("rr", managed: true);
        var service = NewService(store);

        var result = service.SetAdvancedSetting(game, "RrPreset", "5");

        Assert.True(result.Success, result.Message);
        var text = File.ReadAllText(ini);
        Assert.Contains("[DLSSD]", text);
        Assert.Contains("RenderPresetOverride=true", text);
        Assert.Contains("RenderPresetForAll=5", text);
        Assert.Equal("5", service.GetAdvancedSettings(game).Settings.Single(setting => setting.Id == "RrPreset").CurrentValue);
    }

    [Fact]
    public void SetAdvancedSetting_ComboNvngxAndRrPresetFRoundTrip()
    {
        var (game, store, ini) = CreateGame("combo", managed: true);
        File.WriteAllText(ini, "[FrameGen]\nFGOutput=dlssg\n[User]\nCustom=42\n");
        var service = NewService(store);

        var before = service.GetAdvancedSettings(game).Settings;
        Assert.Contains(before.Single(s => s.Id == "FGNvngxReplacement").Options, o => o.Value == "Combo" && o.Name == "Combo");
        Assert.Contains(before.Single(s => s.Id == "RrPreset").Options, o => o.Value == "6" && o.Name == "Preset F");

        Assert.True(service.SetAdvancedSetting(game, "FGNvngxReplacement", "combo").Success);
        Assert.True(service.SetAdvancedSetting(game, "RrPreset", "6").Success);

        var text = File.ReadAllText(ini);
        Assert.Contains("FGNvngxReplacement=Combo", text);
        Assert.Contains("RenderPresetForAll=6", text);
        Assert.Contains("FGOutput=dlssg", text);
        Assert.Contains("Custom=42", text);
        var after = service.GetAdvancedSettings(game).Settings;
        var nvngx = after.Single(s => s.Id == "FGNvngxReplacement");
        var rr = after.Single(s => s.Id == "RrPreset");
        Assert.Equal("Combo", nvngx.CurrentValue);
        Assert.Equal("6", rr.CurrentValue);
        Assert.DoesNotContain(nvngx.Options, o => o.Name.StartsWith("Current / custom"));
        Assert.DoesNotContain(rr.Options, o => o.Name.StartsWith("Current / custom"));
    }

    [Fact]
    public void SetAdvancedSetting_RenderScaleMapsDisplayChoiceToOptiScalerRatio()
    {
        var (game, store, ini) = CreateGame("scale", managed: true);
        var service = NewService(store);

        Assert.True(service.SetAdvancedSetting(game, "RenderScale", "1.5").Success);
        var enabled = File.ReadAllText(ini);
        Assert.Contains("UpscaleRatioOverrideEnabled=true", enabled);
        Assert.Contains("UpscaleRatioOverrideValue=1.5", enabled);
        Assert.Equal("1.5", service.GetAdvancedSettings(game).Settings.Single(setting => setting.Id == "RenderScale").CurrentValue);

        Assert.True(service.SetAdvancedSetting(game, "RenderScale", "off").Success);
        var disabled = File.ReadAllText(ini);
        Assert.Contains("UpscaleRatioOverrideEnabled=false", disabled);
        Assert.Contains("UpscaleRatioOverrideValue=1.5", disabled); // preserve user's last ratio while disabled
        Assert.Equal("off", service.GetAdvancedSettings(game).Settings.Single(setting => setting.Id == "RenderScale").CurrentValue);
    }

    [Fact]
    public void SetAdvancedSetting_DoesNotExposeOrMutateLoadReShadeChainState()
    {
        var (game, store, ini) = CreateGame("chain", managed: true);
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=true\n");
        var service = NewService(store);

        Assert.False(service.SetAdvancedSetting(game, "LoadReshade", "false").Success);
        Assert.Contains("LoadReshade=true", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IniEditor_SetValuesPreservesCrLfCommentsAndUnrelatedKeys()
    {
        var (_, _, ini) = CreateGame("editor", managed: true);
        File.WriteAllText(ini, "; top\r\n[FrameGen]\r\n; comment\r\nFGInput=auto\r\nOther=keep\r\n");
        var bin = Path.GetDirectoryName(ini)!;

        OptiScalerIniEditor.SetValues(bin,
        [
            ("FrameGen", "FGInput", "fsrfg"),
            ("Framerate", "FramerateLimit", "90.000000")
        ]);

        var text = File.ReadAllText(ini);
        Assert.Contains("\r\n", text);
        Assert.Contains("; top\r\n", text);
        Assert.Contains("; comment\r\n", text);
        Assert.Contains("Other=keep", text);
        Assert.Contains("FGInput=fsrfg", text);
        Assert.Contains("[Framerate]", text);
        Assert.Contains("FramerateLimit=90.000000", text);
    }

    private (DetectedGame Game, LinuxDeploymentRecordStore Store, string IniPath) CreateGame(
        string name,
        bool managed,
        OptiScalerVariant variant = OptiScalerVariant.Stable)
    {
        var gameRoot = Path.Combine(_root, name, "game");
        var bin = Path.Combine(gameRoot, "bin");
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var ini = Path.Combine(bin, "OptiScaler.ini");
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=auto\n");
        var game = new DetectedGame
        {
            Name = "Game " + name,
            InstallPath = gameRoot,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX12,
            SteamAppId = Math.Abs(name.GetHashCode()) + 1000
        };
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json"));
        if (managed)
        {
            store.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.OptiScaler,
                BinaryDirectory = bin,
                Version = "1.0",
                SourceUrl = "https://example.invalid/OptiScaler.7z",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [OptiScalerDeploymentService.VariantMetadataKey] = variant.ToString()
                },
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = ini,
                        ExistedBeforeInstall = false,
                        AllowUserChanges = true
                    }
                ]
            });
        }
        return (game, store, ini);
    }

    private static OptiScalerDeploymentService NewService(LinuxDeploymentRecordStore store)
        => new(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), new NeverPackageProvider());

    private sealed class NeverPackageProvider : IOptiScalerPackageProvider
    {
        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Advanced settings tests must not download or resolve packages.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
