using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>The settings, status lines and file buttons of one frame-generation mod's ⚙ (w14/extras-fg).</summary>
public sealed record FgSettingsContent(
    IReadOnlyList<ComponentSettingSection> Sections,
    IReadOnlyList<ComponentDialogLine> Lines,
    IReadOnlyList<ComponentFileButton> Files);

/// <summary>
/// w14/extras-fg: builds the ⚙ content of the frame-generation mods from their files — MFG Unlock (#279). The values come
/// from, and go to, the mod's own file through the platform editors; the labels and help texts are upstream's.
/// </summary>
public static class FgSettingsBuilder
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>MFG Unlock's method names (the same in every language, as in the mod's panel).</summary>
    public static readonly IReadOnlyList<string> MfgUnlockMethods = ["NVIDIA DLSS-G", "Intel XeMFG (CUDA)", "XeMFG via DLSS-G (XMX)"];

    private static (bool, string) Saved(Action write, string label)
    {
        try
        {
            write();
            return (true, T("Fg.Saved"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (false, TF("Fg.SaveFailed", label, UiLocalizationService.Present(ex.Message)));
        }
    }

    /// <summary>
    /// #279 (upstream 2.8.9 <c>BuildMfgAdaCogContent</c>): Frame generation method and DLSS-SR input in
    /// <c>MFGAdaUnlock.ini</c>, whether Intel's XeSS files are right, the graphics-API warning, and Open MFGAdaUnlock.ini.
    /// </summary>
    public static FgSettingsContent MfgUnlock(DetectedGame game, string? binaryDirectory, IReadOnlyList<MfgUnlockXessFile> xess, bool installed, Action<string> show)
    {
        var available = binaryDirectory is not null;
        var method = available ? MfgUnlockIni.ReadMethod(binaryDirectory!) : MfgUnlockMethod.DlssG;
        ComponentSettingRow? methodRow = null;
        methodRow = new ComponentSettingRow(T("Mfgu.Method"), T("Mfgu.MethodTip"), MfgUnlockMethods, (int)method)
        {
            Available = available,
            UnavailableReason = T("Cog.GreyedNotInstalled"), // #302: why it is greyed
            Apply = index => Saved(() => MfgUnlockIni.WriteMethod(binaryDirectory!, (MfgUnlockMethod)index), T("Mfgu.Method"))
        };
        var dlssSr = new ComponentSettingRow(T("Mfgu.DlssSr"), T("Mfgu.DlssSrTip"), [T("Ini.Off"), T("Ini.On")],
            available && MfgUnlockIni.ReadDlssSr(binaryDirectory!) ? 1 : 0)
        {
            Available = available,
            EnabledWhen = () => methodRow.SelectedIndex == (int)MfgUnlockMethod.XeMfgCuda,
            DisabledReason = T("Cog.GreyedXeMfgCuda"), // #302: why it is greyed
            UnavailableReason = T("Cog.GreyedNotInstalled"),
            Apply = index => Saved(() => MfgUnlockIni.WriteDlssSr(binaryDirectory!, index == 1), T("Mfgu.DlssSr"))
        };

        var lines = new List<ComponentDialogLine>();
        if (installed && xess.Count > 0)
        {
            var missing = xess.Where(file => file.State == IntelXessFileState.Missing).Select(file => file.Name).ToArray();
            var other = xess.Where(file => file.State == IntelXessFileState.OtherVersion).Select(file => $"{file.Name} {file.Version ?? "?"}".TrimEnd()).ToArray();
            lines.Add(missing.Length > 0 ? ComponentDialogLine.Warning(TF("Mfgu.XessMissing", string.Join(", ", missing)))
                : other.Length > 0 ? ComponentDialogLine.Warning(TF("Mfgu.XessOtherVersion", string.Join(", ", other)))
                : ComponentDialogLine.Plain(T("Mfgu.XessReady")));
        }
        switch (MfgUnlockIni.ApiSupport(game))
        {
            case MfgUnlockApiSupport.VulkanExperimental:
                lines.Add(ComponentDialogLine.Warning(T("Mfgu.ApiVulkan")));
                break;
            case MfgUnlockApiSupport.NotSupported:
                lines.Add(ComponentDialogLine.Warning(TF("Mfgu.ApiNotSupported", ApiName(game))));
                break;
        }
        lines.Add(ComponentDialogLine.Plain(T("Mfgu.OtherSettings")));

        var files = available
            ? new[] { new ComponentFileButton(TF("Fg.OpenFile", MfgUnlockIni.FileName), MfgUnlockIni.PathIn(binaryDirectory!), show) }
            : [];
        return new FgSettingsContent([new ComponentSettingSection(T("Win.FrameGeneration"), [methodRow, dlssSr])], lines, files);
    }

    /// <summary>The game's API as upstream names it in the warning (its primary API, else the first known one).</summary>
    internal static string ApiName(DetectedGame game)
    {
        var api = game.EffectiveGraphicsApi != GraphicsApi.Unknown ? game.EffectiveGraphicsApi : game.EffectiveGraphicsApis.FirstOrDefault();
        return api switch
        {
            GraphicsApi.DirectX8 => "DirectX 8",
            GraphicsApi.DirectX9 => "DirectX 9",
            GraphicsApi.DirectX10 => "DirectX 10",
            GraphicsApi.DirectX11 => "DirectX 11",
            GraphicsApi.OpenGL => "OpenGL",
            _ => T("Mfgu.OlderApi")
        };
    }

    /// <summary>
    /// #281 (upstream 2.8.9 <c>Dlssg2030Settings</c>): the mod's six main settings as dropdowns, written to the installed
    /// <c>dlssg_sm86.ini</c> (greyed out, with upstream's warning, until it is installed), then Open dlssg_sm86.ini and
    /// Open log. <paramref name="lines"/> is the installed INI (null when not installed); <paramref name="write"/> saves
    /// one choice and returns the refusal or null.
    /// </summary>
    public static FgSettingsContent Dlssg2030(IReadOnlyList<string>? lines, string? binaryDirectory, Func<Dlssg2030Setting, int, (string? Refusal, IReadOnlyList<string>? After)> write, Action<string> show)
    {
        var available = lines is not null;
        var current = lines ?? [];
        ComponentSettingRow Row(Dlssg2030Setting setting, string label, string tip, IReadOnlyList<string> options, Action<IReadOnlyList<string>>? after = null)
        {
            return new ComponentSettingRow(label, tip, options, Dlssg2030Ini.IndexOf(setting, current))
            {
                Available = available,
                UnavailableReason = UiLocalizationService.Present(Dlssg2030Ini.NotInstalledMessage), // #302: why it is greyed
                Apply = index =>
                {
                    try
                    {
                        var (refusal, written) = write(setting, index);
                        if (refusal is not null) return (false, UiLocalizationService.Present(refusal));
                        if (written is not null) after?.Invoke(written);
                        return (true, TF("Dsg.Saved", label));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        return (false, TF("Fg.SaveFailed", label, UiLocalizationService.Present(ex.Message)));
                    }
                }
            };
        }

        var maxFrames = Row(Dlssg2030Ini.MaxFrames, T("Dsg.MaxFrames"), T("Dsg.MaxFramesTip"), ["2X", "3X", T("Dsg.FourXDefault"), "5X", "6X", T("Dsg.GameDecides")]);
        // Choosing XeSS at 5X/6X moves Max Frames to 4X (upstream): the dropdown follows what was written.
        var engine = Row(Dlssg2030Ini.Engine, T("Dsg.Engine"), T("Dsg.EngineTip"), [T("Dsg.EngineDlss"), "XeSS"],
            after => maxFrames.Show(Dlssg2030Ini.IndexOf(Dlssg2030Ini.MaxFrames, after)));
        var rows = new[]
        {
            Row(Dlssg2030Ini.Enabled, T("Win.FrameGeneration"), T("Dsg.EnabledTip"), [T("Ini.On"), T("Ini.Off")]),
            engine,
            maxFrames,
            Row(Dlssg2030Ini.Preset, "Preset", T("Dsg.PresetTip"), [T("Dsg.PresetA"), T("Dsg.PresetB"), "C: FG Lite"]),
            Row(Dlssg2030Ini.SpeedTier, T("Dsg.Speed"), T("Dsg.SpeedTip"),
                [T("Dsg.SpeedExact"), T("Dsg.SpeedExactOptimised"), T("Dsg.SpeedFast"), T("Dsg.SpeedFastest")]),
            Row(Dlssg2030Ini.SuperResolution, "Super Resolution", T("Dsg.SrTip"), [T("Dsg.SrOff"), "M-INT8", "L-INT8", "M-FP16", "L-FP16"]),
        };
        var lines2 = available ? Array.Empty<ComponentDialogLine>() : [ComponentDialogLine.Warning(UiLocalizationService.Present(Dlssg2030Ini.NotInstalledMessage))];
        var files = binaryDirectory is null
            ? Array.Empty<ComponentFileButton>()
            : [new ComponentFileButton(TF("Fg.OpenFile", Dlssg2030UnlockDeploymentService.IniFileName), Path.Combine(binaryDirectory, Dlssg2030UnlockDeploymentService.IniFileName), show),
               new ComponentFileButton(T("Dsg.OpenLog"), Path.Combine(binaryDirectory, Dlssg2030Ini.LogRelativePath), show)];
        return new FgSettingsContent([new ComponentSettingSection("", rows)], lines2, files);
    }

    /// <summary>
    /// #280 (upstream 2.9.3 <c>DetailPanelBuilder.Extras.RtxEncoreSettings</c>): RTX Encore's settings by upstream's
    /// sections, written straight into <c>rtx-encore.jsonc</c> beside RTX Encore (comments kept; created when missing).
    /// Greyed out until RTX Encore is installed. Smooth Motion is left out on Linux (it needs the Windows driver).
    /// </summary>
    public static IReadOnlyList<ComponentSettingSection> RtxEncore(string? configPath)
    {
        var available = configPath is not null;
        var text = available ? RtxEncoreConfig.Read(configPath!) : null;
        var rows = new Dictionary<RtxEncoreSetting, ComponentSettingRow>();
        string[] onOff = [T("Ini.Off"), T("Ini.On")];
        var game = T("Win.Game");

        ComponentSettingRow Row(RtxEncoreSetting setting, string label, IReadOnlyList<string>? options = null, Func<bool>? when = null,
            string? tip = null, string? greyed = null)
        {
            var current = RtxEncoreSettings.ValueOf(text, setting);
            (bool, string) Write(int value) => Saved(() => RtxEncoreConfig.SetInFile(configPath!, setting.Section, setting.Key,
                setting.IsNumber ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : setting.Values[value]), label);
            var row = setting.IsNumber
                ? new ComponentSettingRow(label, tip, int.TryParse(current, out var number) ? number : int.Parse(setting.Default, System.Globalization.CultureInfo.InvariantCulture), setting.Min, setting.Max)
                    { Available = available, EnabledWhen = when, Apply = Write, DisabledReason = greyed, UnavailableReason = T("Enc.NotInstalled") }
                : new ComponentSettingRow(label, tip, options ?? (setting.IsBool ? onOff : setting.Values), setting.IndexOf(current))
                    { Available = available, EnabledWhen = when, Apply = Write, DisabledReason = greyed, UnavailableReason = T("Enc.NotInstalled") };
            rows[setting] = row;
            return row;
        }
        bool Is(RtxEncoreSetting setting, string value) => rows.TryGetValue(setting, out var row) && row.SelectedIndex == setting.Values.ToList().IndexOf(value);

        var frameGeneration = new[]
        {
            Row(RtxEncoreSettings.FgEngine, T("Dsg.Engine"), [game, "DLSS-FG", "XeFG"], tip: T("Enc.TipEngine")),
            Row(RtxEncoreSettings.Mode, T("Enc.Mode"), [T("Dsg.GameDecides"), T("Nv.FgFixed"), T("Nv.FgDynamic")], tip: T("Enc.TipMode")),
            Row(RtxEncoreSettings.Multiplier, T("Enc.Multiplier"), ["2x", "3x", "4x", "5x", "6x"], () => Is(RtxEncoreSettings.Mode, "fixed"), T("Enc.TipMultiplier"), T("Cog.GreyedFixedMode")),
            Row(RtxEncoreSettings.FgPreset, T("Enc.FgPreset"), [game, "A", "B", "C"], tip: T("Enc.TipFgPreset")),
            Row(RtxEncoreSettings.DynamicTarget, T("Enc.DynamicTarget"), when: () => Is(RtxEncoreSettings.Mode, "dynamic"), tip: T("Enc.TipDynamicTarget"), greyed: T("Cog.GreyedDynamicMode")),
            Row(RtxEncoreSettings.Dynamic56, T("Enc.Dynamic56"), when: () => Is(RtxEncoreSettings.Mode, "dynamic"), tip: T("Enc.TipDynamic56"), greyed: T("Cog.GreyedDynamicMode")),
            Row(RtxEncoreSettings.FlipMetering, T("Enc.FlipMetering"), tip: T("Enc.TipFlipMetering")),
            Row(RtxEncoreSettings.FrameLimit, T("Enc.FrameLimit"), tip: T("Enc.TipFrameLimit")),
        };
        bool Xefg() => Is(RtxEncoreSettings.FgEngine, "xefg");
        var xefg = new[]
        {
            Row(RtxEncoreSettings.XefgTensorCores, T("Enc.XefgTensorCores"), when: Xefg, tip: T("Enc.TipXefgTensorCores"), greyed: T("Cog.GreyedXefg")),
            Row(RtxEncoreSettings.XefgPacing, T("Enc.XefgPacing"), when: Xefg, tip: T("Enc.TipXefgPacing"), greyed: T("Cog.GreyedXefg")),
            Row(RtxEncoreSettings.XefgUi, T("Enc.XefgUi"), [T("Enc.Interpolate"), T("Enc.GameLayers")], Xefg, T("Enc.TipXefgUi"), T("Cog.GreyedXefg")),
            Row(RtxEncoreSettings.XefgAlpha, T("Enc.XefgAlpha"), [T("Enc.Unknown"), T("Enc.Straight"), T("Enc.Premultiplied")], Xefg, T("Enc.TipXefgAlpha"), T("Cog.GreyedXefg")),
        };
        var quality = new[]
        {
            Row(RtxEncoreSettings.HighMultiplierQuality, T("Enc.HighMultiplierQuality"), tip: T("Enc.TipHighMultiplierQuality")),
            Row(RtxEncoreSettings.FrameProtection, T("Enc.FrameProtection"), tip: T("Enc.TipFrameProtection")),
            Row(RtxEncoreSettings.FastFrameGeneration, T("Enc.FastFrameGeneration"), tip: T("Enc.TipFastFrameGeneration")),
            Row(RtxEncoreSettings.ProtectionTuning, T("Enc.ProtectionTuning"), [T("Enc.Refined"), T("Enc.Classic")], tip: T("Enc.TipProtectionTuning")),
        };
        var hud = new[]
        {
            Row(RtxEncoreSettings.AutoUi, T("Enc.AutoUi"), tip: T("Enc.TipAutoUi")),
            Row(RtxEncoreSettings.UiAssist, T("Enc.UiAssist"), tip: T("Enc.TipUiAssist")),
            Row(RtxEncoreSettings.ForceUi, T("Enc.ForceUi"), tip: T("Enc.TipForceUi")),
        };
        string[] presets = ["A", "B", "C", "D", "E", "F"];
        var upscaling = new[]
        {
            Row(RtxEncoreSettings.Upscaler, T("Enc.Upscaler"), [game, "XeSS"], tip: T("Enc.TipUpscaler")),
            Row(RtxEncoreSettings.XessTensorCores, T("Enc.XessTensorCores"), when: () => Is(RtxEncoreSettings.Upscaler, "xess"), tip: T("Enc.TipXessTensorCores"), greyed: T("Cog.GreyedXess")),
            Row(RtxEncoreSettings.RenderScale, T("Enc.RenderScale"), [game, "DLAA", T("Enc.Quality"), T("Enc.Balanced"), T("Enc.Performance"), T("Enc.UltraPerformance"), T("Win.Custom")], tip: T("Enc.TipRenderScale")),
            Row(RtxEncoreSettings.CustomScale, T("Enc.CustomScale"), when: () => Is(RtxEncoreSettings.RenderScale, "custom"), tip: T("Enc.TipCustomScale"), greyed: T("Cog.GreyedCustomScale")),
            Row(RtxEncoreSettings.FastSr, T("Enc.FastSr"), tip: T("Enc.TipFastSr")),
            Row(RtxEncoreSettings.SrPreset, T("Enc.SrPreset"), [game, T("Ini.LumaHdrDefault"), .. presets, "J", "K", "L", "M"], tip: T("Enc.TipSrPreset")),
            Row(RtxEncoreSettings.RrPreset, T("Enc.RrPreset"), [game, T("Ini.LumaHdrDefault"), .. presets], tip: T("Enc.TipRrPreset")),
            Row(RtxEncoreSettings.SrHdr, T("Enc.SrHdr"), [game, T("Ini.On"), T("Ini.Off")], tip: T("Enc.TipSrHdr")),
            Row(RtxEncoreSettings.SrAutoExposure, T("Enc.SrAutoExposure"), [game, T("Ini.On"), T("Ini.Off")], tip: T("Enc.TipSrAutoExposure")),
        };
        var neural = new[]
        {
            Row(RtxEncoreSettings.NrEnabled, T("Sec.NeuralRendering"), tip: T("Enc.TipNrEnabled")),
            Row(RtxEncoreSettings.NrModel, T("Enc.NrModel"), ["B", "A", T("Ini.PeakAuto")], tip: T("Enc.TipNrModel")),
            Row(RtxEncoreSettings.NrPrecision, T("Enc.NrPrecision"), [T("Dsg.SpeedExact"), T("Dsg.SpeedFast")], tip: T("Enc.TipNrPrecision")),
            Row(RtxEncoreSettings.NrFast, T("Enc.NrFast"), tip: T("Enc.TipNrFast")),
        };
        var compatibility = new[]
        {
            Row(RtxEncoreSettings.GpuSeries, T("Enc.GpuSeries"), [T("Ini.PeakAuto"), "RTX 40", "RTX 30", "RTX 20"], tip: T("Enc.TipGpuSeries")),
            Row(RtxEncoreSettings.ForceOta, T("Enc.ForceOta"), tip: T("Enc.TipForceOta")),
            Row(RtxEncoreSettings.DisableMenu, T("Enc.DisableMenu"), tip: T("Enc.TipDisableMenu")),
            Row(RtxEncoreSettings.FgInMenus, T("Enc.FgInMenus"), tip: T("Enc.TipFgInMenus")),
            Row(RtxEncoreSettings.Hair, T("Enc.Hair"), tip: T("Enc.TipHair")),
        };
        return
        [
            // #297: two per row, as upstream's RTX Encore ⚙ (AddCompactGrid).
            new ComponentSettingSection(T("Win.FrameGeneration"), frameGeneration, Compact: true),
            new ComponentSettingSection(T("Enc.SectionXefg"), xefg, Compact: true),
            new ComponentSettingSection(T("Enc.SectionQuality"), quality, Compact: true),
            new ComponentSettingSection(T("Enc.SectionHud"), hud, Compact: true),
            new ComponentSettingSection(T("Enc.SectionUpscaling"), upscaling, Compact: true),
            new ComponentSettingSection(T("Sec.NeuralRendering"), neural, Compact: true),
            new ComponentSettingSection(T("Enc.SectionCompatibility"), compatibility, Compact: true),
        ];
    }

    /// <summary>
    /// #280: the RTX Encore ⚙'s notes: what the settings are, why they are greyed out, a warning while the game runs
    /// (w14/extras-fg-2, upstream's order: after the file notes), the XeSS files, the recommended name.
    /// </summary>
    public static IReadOnlyList<ComponentDialogLine> RtxEncoreLines(bool installed, bool configExists, RtxEncoreXessStatus? xess, string? recommendedName,
        bool gameRunning)
    {
        var lines = new List<ComponentDialogLine>();
        if (recommendedName is not null) lines.Add(ComponentDialogLine.Plain(TF("Enc.Recommended", recommendedName)));
        lines.Add(ComponentDialogLine.Plain(T("Enc.SettingsIntro")));
        if (!installed) lines.Add(ComponentDialogLine.Warning(T("Enc.NotInstalled")));
        else if (!configExists) lines.Add(ComponentDialogLine.Plain(T("Enc.NoFile")));
        if (gameRunning) lines.Add(ComponentDialogLine.Warning(T("Enc.GameRunning")));
        if (xess is { Installed: true }) lines.Add(ComponentDialogLine.Plain(TF("Enc.XessInstalled", xess.ReplacedGameCopies)));
        else if (xess is { Installed: false }) lines.Add(ComponentDialogLine.Warning(T("Enc.XessMissing")));
        return lines;
    }
}
