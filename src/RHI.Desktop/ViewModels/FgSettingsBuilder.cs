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
}
