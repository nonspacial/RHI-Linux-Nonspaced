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
            Apply = index => Saved(() => MfgUnlockIni.WriteMethod(binaryDirectory!, (MfgUnlockMethod)index), T("Mfgu.Method"))
        };
        var dlssSr = new ComponentSettingRow(T("Mfgu.DlssSr"), T("Mfgu.DlssSrTip"), [T("Ini.Off"), T("Ini.On")],
            available && MfgUnlockIni.ReadDlssSr(binaryDirectory!) ? 1 : 0)
        {
            Available = available,
            EnabledWhen = () => methodRow.SelectedIndex == (int)MfgUnlockMethod.XeMfgCuda,
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
}
