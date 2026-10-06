using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

public sealed record OptiScalerAdvancedOption(string Value, string Name);

public sealed record OptiScalerAdvancedSetting(
    string Id,
    string Group,
    string Label,
    string CurrentValue,
    IReadOnlyList<OptiScalerAdvancedOption> Options,
    string? Help = null);

public sealed record OptiScalerAdvancedSnapshot(
    string? IniPath,
    bool ManagedByRhi,
    IReadOnlyList<OptiScalerAdvancedSetting> Settings,
    string? Warning = null);

public sealed partial class OptiScalerDeploymentService
{
    private static readonly OptiScalerAdvancedOption[] AutoBoolOptions =
    [
        new("auto", "Auto"),
        new("true", "On"),
        new("false", "Off")
    ];

    private static readonly OptiScalerAdvancedOption[] Dx11UpscalerOptions = Options(
        ("auto", "Auto"), ("fsr22", "FSR 2.2"), ("fsr31", "FSR 3.1 / FSR4"),
        ("xess", "XeSS native"), ("xess_12", "XeSS via DX11on12"),
        ("fsr21_12", "FSR 2.1 via DX11on12"), ("fsr22_12", "FSR 2.2 via DX11on12"),
        ("fsr31_12", "FSR 3.1 / FSR4 via DX11on12"), ("dlss", "DLSS"));

    private static readonly OptiScalerAdvancedOption[] Dx12UpscalerOptions = Options(
        ("auto", "Auto"), ("xess", "XeSS"), ("fsr21", "FSR 2.1"),
        ("fsr22", "FSR 2.2"), ("fsr31", "FSR 3.1 / FSR4"), ("dlss", "DLSS"));

    private static readonly OptiScalerAdvancedOption[] VulkanUpscalerOptions = Options(
        ("auto", "Auto"), ("fsr21", "FSR 2.1 native Vulkan"), ("fsr22", "FSR 2.2 native Vulkan"),
        ("fsr31", "FSR 3.1 / FSR4 native Vulkan"), ("xess", "XeSS native Vulkan"),
        ("fsr21_12", "FSR 2.1 via VKon12"), ("fsr31_12", "FSR 3.1 / FSR4 via VKon12"),
        ("dlss", "DLSS"));

    private static readonly OptiScalerAdvancedOption[] FgInputOptions = Options(
        ("auto", "Auto (Default)"), ("upscaler", "OptiFG (Upscaler)"),
        ("dlssg", "DLSSG via Streamline"), ("nvngxfg", "DLSSG via Nvngx"),
        ("fsrfg", "FSR 3.1 FG"), ("fsrfg30", "FSR 3.0 FG"), ("xefg", "XeFG"));

    private static readonly OptiScalerAdvancedOption[] FgOutputOptions = Options(
        ("auto", "Auto (Default)"), ("fsrfg", "FSR FG"), ("dlssg", "DLSSG"), ("xefg", "XeFG"));

    private static readonly OptiScalerAdvancedOption[] FgNvngxOptions = Options(
        ("None", "None (Real DLSSG)"), ("Nukems", "Nukem's"),
        ("Arturs", "Enabler"), ("FFX", "FSR 3/4 FG"), ("Combo", "Combo"));

    private static readonly OptiScalerAdvancedOption[] SrPresetOptions = BuildPresetOptions(15);
    // OptiScaler's [DLSSD] ordering is 0 = Default, 1 = A ... so Preset F is 6. Upstream 2.7.6 writes 5
    // for F, which OptiScaler documents as E; we follow OptiScaler's own numbering.
    private static readonly OptiScalerAdvancedOption[] RrPresetOptions = BuildPresetOptions(6);

    private static readonly OptiScalerAdvancedOption[] RenderScaleOptions = Options(
        ("off", "Off"), ("1.0", "100% DLAA"), ("1.0101", "99% DLAA Alt"),
        ("1.136", "88% DLAA Lite"), ("1.3", "77% Ultra Quality"), ("1.333", "75% Quality+"),
        ("1.5", "67% Quality"), ("1.724", "58% Balanced"), ("2.0", "50% Performance"),
        ("2.222", "45% Performance-"), ("3.0", "33% Ultra Performance"));

    private static readonly OptiScalerAdvancedOption[] FramerateOptions = Options(
        ("auto", "Auto / Off"), ("30.000000", "30 FPS"), ("40.000000", "40 FPS"),
        ("45.000000", "45 FPS"), ("50.000000", "50 FPS"), ("60.000000", "60 FPS"),
        ("72.000000", "72 FPS"), ("90.000000", "90 FPS"), ("100.000000", "100 FPS"),
        ("120.000000", "120 FPS"), ("138.000000", "138 FPS"), ("141.000000", "141 FPS"),
        ("144.000000", "144 FPS"), ("165.000000", "165 FPS"), ("180.000000", "180 FPS"),
        ("240.000000", "240 FPS"));

    private static readonly OptiScalerAdvancedOption[] ShortcutOptions = Options(
        ("auto", "Auto / Insert"), ("0x2D", "Insert"), ("0x24", "Home"), ("-1", "Disabled"));

    public OptiScalerAdvancedSnapshot GetAdvancedSettings(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var record = GetRecord(game);
        if (record is null)
            return new(null, false, Array.Empty<OptiScalerAdvancedSetting>(),
                "OptiScaler is not RHI-managed for this game. External OptiScaler.ini files are protected from modification.");

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var iniPath = Path.Combine(binaryDirectory, "OptiScaler.ini");
        if (!File.Exists(iniPath))
            return new(iniPath, true, Array.Empty<OptiScalerAdvancedSetting>(),
                "The RHI-managed OptiScaler record exists, but OptiScaler.ini is missing.");

        string Read(string section, string key, string fallback = "auto")
            => NormalizeIniValue(OptiScalerIniEditor.GetValue(binaryDirectory, section, key), fallback);

        var srPreset = ReadPreset(binaryDirectory, "DLSS", SrPresetOptions);
        var rrPreset = ReadPreset(binaryDirectory, "DLSSD", RrPresetOptions);
        var renderScale = ReadRenderScale(binaryDirectory);
        var variant = GetInstalledVariant(record);
        var fgStackEnabled = IsManagedFgStackEnabled(record);
        var streamlineVersion = GetSelectedStreamlineVersion(record);

        var rows = new List<OptiScalerAdvancedSetting>
        {
            new("Dx11Upscaler", "Upscalers", "DirectX 11 upscaler", Read("Upscalers", "Dx11Upscaler"), EnsureCurrent(Dx11UpscalerOptions, Read("Upscalers", "Dx11Upscaler")),
                "Valores compatíveis do OptiScaler. As opções DX11on12 permanecem explícitas."),
            new("Dx12Upscaler", "Upscalers", "DirectX 12 upscaler", Read("Upscalers", "Dx12Upscaler"), EnsureCurrent(Dx12UpscalerOptions, Read("Upscalers", "Dx12Upscaler"))),
            new("VulkanUpscaler", "Upscalers", "Vulkan upscaler", Read("Upscalers", "VulkanUpscaler"), EnsureCurrent(VulkanUpscalerOptions, Read("Upscalers", "VulkanUpscaler"))),

            new("FrameGenEnabled", "Frame Generation", "Frame Generation", Read("FrameGen", "Enabled"), AutoBoolOptions,
                "Controls OptiScaler FrameGen.Enabled."),
            new("FGInput", "Frame Generation", "FG input/source", Read("FrameGen", "FGInput"), EnsureCurrent(FgInputOptions, Read("FrameGen", "FGInput"))),
            new("FGOutput", "Frame Generation", "FG output", Read("FrameGen", "FGOutput"), EnsureCurrent(FgOutputOptions, Read("FrameGen", "FGOutput"))),
            new("FGNvngxReplacement", "Frame Generation", "FG Nvngx Replacement", Read("FrameGen", "FGNvngxReplacement", "None"), EnsureCurrent(FgNvngxOptions, Read("FrameGen", "FGNvngxReplacement", "None")),
                "Only relevant when FG Output = DLSSG. Enabler uses dlss-enabler-headless.dll from the managed OptiScaler Streamline stack. Combo needs the FSR 3/4 FG and Enabler files: FSR FG renders the middle frames, Enabler the rest."),
            new("HUDFix", "Frame Generation", "HUD Fix", Read("OptiFG", "HUDFix"), AutoBoolOptions),
            new("DisableHUDFix", "Frame Generation", "Disable HUD Fix tracking", Read("OptiFG", "DisableHUDFix"), AutoBoolOptions),

            new("SrPreset", "DLSS Overrides", "DLSS SR render preset", srPreset, EnsureCurrent(SrPresetOptions, srPreset),
                "Auto disables OptiScaler's DLSS RenderPresetOverride. Explicit values enable it and set RenderPresetForAll."),
            new("RrPreset", "DLSS Overrides", "DLSS RR render preset", rrPreset, EnsureCurrent(RrPresetOptions, rrPreset),
                "Auto disables OptiScaler's DLSSD RenderPresetOverride."),
            new("RenderScale", "DLSS Overrides", "Internal render scale", renderScale, EnsureCurrent(RenderScaleOptions, renderScale),
                "This is OptiScaler's file-local UpscaleRatio override, not an NVIDIA driver-profile render-scale override."),

            new("DisableFlipMetering", "Compatibility", "Disable NVIDIA Flip Metering", Read("NvApi", "DisableFlipMetering"), AutoBoolOptions,
                "OptiScaler documents this primarily for Fakenvapi/NukemFG compatibility."),
            new("FramerateLimit", "Compatibility", "Framerate limit", Read("Framerate", "FramerateLimit"), EnsureCurrent(FramerateOptions, Read("Framerate", "FramerateLimit")),
                "OptiScaler's Reflex-based limiter. Auto/Off leaves the limiter disabled by default."),
            new("MenuShortcutKey", "Menu", "OptiScaler menu shortcut", Read("Menu", "ShortcutKey"), EnsureCurrent(ShortcutOptions, Read("Menu", "ShortcutKey")))
        };

        if (variant is OptiScalerVariant.Nightly or OptiScalerVariant.DlssNr)
        {
            var insertAt = rows.FindIndex(setting => setting.Id == "FGInput");
            if (insertAt < 0) insertAt = 0;
            rows.Insert(insertAt, new OptiScalerAdvancedSetting(
                "DeployStreamlineDlssEnabler",
                "Frame Generation",
                "Streamline / DLSS Enabler",
                fgStackEnabled ? "true" : "false",
                YesNoOptions,
                "Matches upstream Windows RHI: deploys Streamline DLLs to OptiScaler/Streamline and dlss-enabler-headless.dll to OptiScaler/."));
            rows.Insert(insertAt + 1, new OptiScalerAdvancedSetting(
                "StreamlineVersion",
                "Frame Generation",
                "Streamline Version",
                streamlineVersion,
                GetStreamlineVersionOptions(record),
                "Version used by the OptiScaler-managed Streamline stack. Upstream Windows RHI defaults to 2.12.0 when available."));
        }

        return new(iniPath, true, rows,
            "LoadReshade is intentionally chain-owned by RHI Linux and is not exposed here. OptiScaler Nightly/DLSS NR can deploy the same Streamline + DLSS Enabler stack exposed by upstream Windows RHI.");
    }

    public ComponentOperationResult SetAdvancedSetting(DetectedGame game, string id, string value)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(id)) return ComponentOperationResult.Fail("OptiScaler setting id is empty.");

        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("OptiScaler is external/unmanaged. RHI Linux will not modify its OptiScaler.ini.");

        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var iniPath = Path.Combine(binaryDirectory, "OptiScaler.ini");
        if (!File.Exists(iniPath))
            return ComponentOperationResult.Fail("RHI-managed OptiScaler.ini is missing; no setting was changed.");

        try
        {
            var normalizedId = id.Trim();
            switch (normalizedId)
            {
                case "Dx11Upscaler":
                    WriteChoice(binaryDirectory, "Upscalers", "Dx11Upscaler", value, Dx11UpscalerOptions); break;
                case "Dx12Upscaler":
                    WriteChoice(binaryDirectory, "Upscalers", "Dx12Upscaler", value, Dx12UpscalerOptions); break;
                case "VulkanUpscaler":
                    WriteChoice(binaryDirectory, "Upscalers", "VulkanUpscaler", value, VulkanUpscalerOptions); break;
                case "FrameGenEnabled":
                    WriteChoice(binaryDirectory, "FrameGen", "Enabled", value, AutoBoolOptions); break;
                case "FGInput":
                    WriteChoice(binaryDirectory, "FrameGen", "FGInput", value, FgInputOptions); break;
                case "FGOutput":
                    WriteChoice(binaryDirectory, "FrameGen", "FGOutput", value, FgOutputOptions); break;
                case "FGNvngxReplacement":
                    WriteChoice(binaryDirectory, "FrameGen", "FGNvngxReplacement", value, FgNvngxOptions); break;
                case "HUDFix":
                    WriteChoice(binaryDirectory, "OptiFG", "HUDFix", value, AutoBoolOptions); break;
                case "DisableHUDFix":
                    WriteChoice(binaryDirectory, "OptiFG", "DisableHUDFix", value, AutoBoolOptions); break;
                case "DisableFlipMetering":
                    WriteChoice(binaryDirectory, "NvApi", "DisableFlipMetering", value, AutoBoolOptions); break;
                case "FramerateLimit":
                    WriteChoice(binaryDirectory, "Framerate", "FramerateLimit", value, FramerateOptions); break;
                case "MenuShortcutKey":
                    WriteChoice(binaryDirectory, "Menu", "ShortcutKey", value, ShortcutOptions); break;
                case "SrPreset":
                    WritePreset(binaryDirectory, "DLSS", value, SrPresetOptions); break;
                case "RrPreset":
                    WritePreset(binaryDirectory, "DLSSD", value, RrPresetOptions); break;
                case "RenderScale":
                    WriteRenderScale(binaryDirectory, value); break;
                default:
                    return ComponentOperationResult.Fail($"Unknown OptiScaler advanced setting: {id}");
            }

            return ComponentOperationResult.Ok($"OptiScaler setting '{normalizedId}' updated in OptiScaler.ini.", iniPath);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"OptiScaler setting was not changed: {ex.Message}");
        }
    }

    private static void WriteChoice(
        string binaryDirectory,
        string section,
        string key,
        string value,
        IReadOnlyList<OptiScalerAdvancedOption> allowed)
    {
        var normalized = value?.Trim() ?? "";
        if (!allowed.Any(option => option.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentOutOfRangeException(nameof(value), $"Unsupported value '{value}' for [{section}] {key}.");
        var canonical = allowed.First(option => option.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)).Value;
        OptiScalerIniEditor.SetValue(binaryDirectory, section, key, canonical);
    }

    private static void WritePreset(
        string binaryDirectory,
        string section,
        string value,
        IReadOnlyList<OptiScalerAdvancedOption> allowed)
    {
        var normalized = value?.Trim() ?? "";
        if (!allowed.Any(option => option.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentOutOfRangeException(nameof(value), $"Unsupported {section} render preset '{value}'.");

        if (normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            OptiScalerIniEditor.SetValues(binaryDirectory,
            [
                (section, "RenderPresetOverride", "false"),
                (section, "RenderPresetForAll", "0")
            ]);
            return;
        }

        OptiScalerIniEditor.SetValues(binaryDirectory,
        [
            (section, "RenderPresetOverride", "true"),
            (section, "RenderPresetForAll", normalized)
        ]);
    }

    private static string ReadPreset(
        string binaryDirectory,
        string section,
        IReadOnlyList<OptiScalerAdvancedOption> allowed)
    {
        var enabled = NormalizeIniValue(OptiScalerIniEditor.GetValue(binaryDirectory, section, "RenderPresetOverride"), "auto");
        if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase)) return "auto";
        var raw = NormalizeIniValue(OptiScalerIniEditor.GetValue(binaryDirectory, section, "RenderPresetForAll"), "0");
        return allowed.Any(option => option.Value.Equals(raw, StringComparison.OrdinalIgnoreCase)) ? raw : raw;
    }

    private static void WriteRenderScale(string binaryDirectory, string value)
    {
        var normalized = value?.Trim() ?? "";
        if (!RenderScaleOptions.Any(option => option.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentOutOfRangeException(nameof(value), $"Unsupported OptiScaler render scale '{value}'.");
        if (normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            OptiScalerIniEditor.SetValue(binaryDirectory, "UpscaleRatio", "UpscaleRatioOverrideEnabled", "false");
            return;
        }
        OptiScalerIniEditor.SetValues(binaryDirectory,
        [
            ("UpscaleRatio", "UpscaleRatioOverrideEnabled", "true"),
            ("UpscaleRatio", "UpscaleRatioOverrideValue", normalized)
        ]);
    }

    private static string ReadRenderScale(string binaryDirectory)
    {
        var enabled = NormalizeIniValue(OptiScalerIniEditor.GetValue(binaryDirectory, "UpscaleRatio", "UpscaleRatioOverrideEnabled"), "auto");
        if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase)) return "off";
        return NormalizeIniValue(OptiScalerIniEditor.GetValue(binaryDirectory, "UpscaleRatio", "UpscaleRatioOverrideValue"), "1.3");
    }

    private static string NormalizeIniValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static OptiScalerAdvancedOption[] Options(params (string Value, string Name)[] options)
        => options.Select(option => new OptiScalerAdvancedOption(option.Value, option.Name)).ToArray();

    private static OptiScalerAdvancedOption[] BuildPresetOptions(int highestPreset)
    {
        var result = new List<OptiScalerAdvancedOption>
        {
            new("auto", "Auto / no override"),
            new("0", "Default")
        };
        for (var value = 1; value <= highestPreset; value++)
            result.Add(new(value.ToString(), $"Preset {(char)('A' + value - 1)}"));
        return result.ToArray();
    }

    private static IReadOnlyList<OptiScalerAdvancedOption> EnsureCurrent(
        IReadOnlyList<OptiScalerAdvancedOption> options,
        string current)
    {
        if (options.Any(option => option.Value.Equals(current, StringComparison.OrdinalIgnoreCase))) return options;
        return options.Concat([new OptiScalerAdvancedOption(current, $"Current / custom: {current}")]).ToArray();
    }
}
