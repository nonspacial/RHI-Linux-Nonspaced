using System.Globalization;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>One entry of a display mode value list: the stored value and what the user reads.</summary>
public sealed record DisplayModeChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// #311: display mode words in the UI language — item names, help, groups, values as the user reads them ("700 nits",
/// "Follow global", "2x"), the value lists a mode's Edit… offers, reasons an item cannot be used, and the switch summary.
/// </summary>
public static class DisplayModeText
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    // Every item's name and help key, written out so the key checks see each one.
    private static readonly IReadOnlyDictionary<string, (string Label, string Help)> ItemKeys = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        [DisplayModeItemKeys.RdxPeakNits] = ("DmItem.RdxPeakNits", "DmHelp.RdxPeakNits"),
        [DisplayModeItemKeys.RdxGameNits] = ("DmItem.RdxGameNits", "DmHelp.RdxGameNits"),
        [DisplayModeItemKeys.RdxUiNits] = ("DmItem.RdxUiNits", "DmHelp.RdxUiNits"),
        [DisplayModeItemKeys.UlTargetFps] = ("DmItem.UlTargetFps", "DmHelp.UlTargetFps"),
        [DisplayModeItemKeys.DcFpsLimit] = ("DmItem.DcFpsLimit", "DmHelp.DcFpsLimit"),
        [DisplayModeItemKeys.DriverFpsCap] = ("DmItem.DriverFpsCap", "DmHelp.DriverFpsCap"),
        [DisplayModeItemKeys.MfgMode] = ("DmItem.MfgMode", "DmHelp.MfgMode"),
        [DisplayModeItemKeys.DlssSrPreset] = ("DmItem.DlssSrPreset", "DmHelp.DlssSrPreset"),
        [DisplayModeItemKeys.DlssSrScale] = ("DmItem.DlssSrScale", "DmHelp.DlssSrScale"),
        [DisplayModeItemKeys.DlssRrPreset] = ("DmItem.DlssRrPreset", "DmHelp.DlssRrPreset"),
        [DisplayModeItemKeys.DlssRrScale] = ("DmItem.DlssRrScale", "DmHelp.DlssRrScale"),
        [DisplayModeItemKeys.RsPreset] = ("DmItem.RsPreset", "DmHelp.RsPreset"),
        [DisplayModeItemKeys.RsOverlayKey] = ("DmItem.RsOverlayKey", "DmHelp.RsOverlayKey"),
        [DisplayModeItemKeys.RsScreenshotKey] = ("DmItem.RsScreenshotKey", "DmHelp.RsScreenshotKey"),
        [DisplayModeItemKeys.RsGamepadNav] = ("DmItem.RsGamepadNav", "DmHelp.RsGamepadNav"),
        [DisplayModeItemKeys.UlOsdKey] = ("DmItem.UlOsdKey", "DmHelp.UlOsdKey"),
        [DisplayModeItemKeys.Hdr] = ("DmItem.Hdr", "DmHelp.Hdr"),
        [DisplayModeItemKeys.Resolution] = ("DmItem.Resolution", "DmHelp.Resolution"),
        [DisplayModeItemKeys.LaunchArgs] = ("DmItem.LaunchArgs", "DmHelp.LaunchArgs"), // w14/layouts-c
    };

    public static string Label(DisplayModeItem item) => ItemKeys.TryGetValue(item.Key, out var k) ? T(k.Label) : item.Key;
    public static string Help(DisplayModeItem item) => ItemKeys.TryGetValue(item.Key, out var k) ? T(k.Help) : "";

    public static string Group(DisplayModeGroup group) => T(group switch
    {
        DisplayModeGroup.HdrAndBrightness => "DmGroup.HdrAndBrightness",
        DisplayModeGroup.FrameRate => "DmGroup.FrameRate",
        DisplayModeGroup.Image => "DmGroup.Image",
        DisplayModeGroup.Controls => "DmGroup.Controls",
        _ => "DmGroup.Launch"
    });

    /// <summary>The name of a mode ("Custom" and "Off" in the UI language; the user's own names as typed).</summary>
    public static string ModeName(string? modeId, string? name)
        => modeId is null ? T("Dm.Off") : DisplayModeStore.IsCustom(modeId) ? T("Dm.Custom") : name ?? modeId;

    /// <summary>A value as shown to the user.</summary>
    public static string Format(DisplayModeItem item, string? value)
    {
        if (value is null) return T("Dm.NotSet");
        switch (item.Kind)
        {
            case DisplayModeValueKind.Choice:
                if (item.Choices == DisplayModeChoices.Resolution && value.StartsWith(DisplayModeItems.ResolutionOff, StringComparison.Ordinal))
                    return T("DmChoice.Off");
                if (item.Choices == DisplayModeChoices.Resolution && value.StartsWith(DisplayModeItems.ResolutionOn, StringComparison.Ordinal))
                    return T("DmChoice.ResolutionOn");
                return Choices(item).FirstOrDefault(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;
            case DisplayModeValueKind.ReShadeKey:
                return ReShadeHotkeyCodec.TryParse(value, out var binding) ? binding.ToDisplayText() : value;
            case DisplayModeValueKind.Text:
                return value.Length == 0 ? T("Dm.None") : value;
            case DisplayModeValueKind.Number:
            case DisplayModeValueKind.Decimal:
                if (item.AllowZero && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == 0) return T("DmChoice.Off");
                return item.Unit is null ? value : $"{value} {item.Unit}";
            default:
                return value;
        }
    }

    /// <summary>The values a Choice item offers, in order (not "not set": the editors add that).</summary>
    public static IReadOnlyList<DisplayModeChoice> Choices(DisplayModeItem item, IEnumerable<string>? resolutions = null)
    {
        switch (item.Choices)
        {
            case DisplayModeChoices.OffOn01:
                return [new("0", T("DmChoice.Off")), new("1", T("DmChoice.On"))];
            case DisplayModeChoices.FpsLimit:
                return DisplayModeItems.FpsLimitValues.Select(v => new DisplayModeChoice(v, v switch
                {
                    DisplayModeItems.FpsGlobal => T("DmChoice.FollowGlobal"),
                    "0" => T("DmChoice.Off"),
                    _ => $"{v} FPS"
                })).ToList();
            case DisplayModeChoices.FrameGeneration:
                return DisplayModeItems.FrameGenerationValues.Select(v => new DisplayModeChoice(v, v switch
                {
                    DisplayModeItems.FrameGenerationOff => T("DmChoice.Off"),
                    DisplayModeItems.FrameGenerationDynamic => T("DmChoice.Dynamic"),
                    _ => (int.Parse(v[DisplayModeItems.FrameGenerationFixedPrefix.Length..], CultureInfo.InvariantCulture) + 1) + "x"
                })).ToList();
            case DisplayModeChoices.SrPreset:
                return Presets(OptionLists.DlssPresets(DlssPresetCatalogue.Current.SuperResolution));
            case DisplayModeChoices.RrPreset:
                return Presets(OptionLists.DlssPresets(DlssPresetCatalogue.Current.RayReconstruction));
            case DisplayModeChoices.RenderScale:
                return DisplayModeItems.RenderScaleValues.Select(v => new DisplayModeChoice(v,
                    v == "0" ? T("DmChoice.GameDecides") : OptionLists.NvidiaRenderScale.Display(uint.Parse(v, CultureInfo.InvariantCulture)))).ToList();
            case DisplayModeChoices.HdrMode:
                return OptionLists.HdrMode.Values.Select(v => new DisplayModeChoice(v, OptionLists.HdrMode.Display(v))).ToList();
            case DisplayModeChoices.Resolution:
                var list = new List<DisplayModeChoice> { new(DisplayModeItems.ResolutionOff, T("DmChoice.Off")), new(DisplayModeItems.ResolutionOn, T("DmChoice.ResolutionOn")) };
                foreach (var size in (resolutions ?? OptionLists.GameResolution.Values).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (DisplayResolution.Parse(size) is { } parsed) list.Add(new(parsed.ToString(), parsed.ToString()));
                return list;
            default:
                return [];
        }
    }

    private static IReadOnlyList<DisplayModeChoice> Presets(OptionList<uint> list)
        => list.Values.Select(v => new DisplayModeChoice(v.ToString(CultureInfo.InvariantCulture), list.Display(v))).ToList();

    /// <summary>Why an item cannot be used for a game now.</summary>
    public static string Reason(DisplayModeUnavailable reason) => reason switch
    {
        DisplayModeUnavailable.None => "",
        DisplayModeUnavailable.NoGameFolder => T("DmWhy.NoGameFolder"),
        DisplayModeUnavailable.NoReShadeIni => T("DmWhy.NoReShadeIni"),
        DisplayModeUnavailable.ReShadeNotManaged => T("DmWhy.ReShadeNotManaged"),
        DisplayModeUnavailable.RenoDxNotManaged => T("DmWhy.RenoDxNotManaged"),
        DisplayModeUnavailable.ReLimiterNotManaged => T("DmWhy.ReLimiterNotManaged"),
        DisplayModeUnavailable.NoReLimiterIni => T("DmWhy.NoReLimiterIni"),
        DisplayModeUnavailable.DisplayCommanderNotManaged => T("DmWhy.DisplayCommanderNotManaged"),
        DisplayModeUnavailable.NvidiaNotOffered => T("DmWhy.NvidiaNotOffered"),
        DisplayModeUnavailable.FpsLimitNotOffered => T("DmWhy.FpsLimitNotOffered"),
        DisplayModeUnavailable.HdrNotOffered => T("DmWhy.HdrNotOffered"),
        DisplayModeUnavailable.LaunchArgsNotOffered => T("DmWhy.LaunchArgsNotOffered"), // w14/layouts-c
        _ => T("DmWhy.ResolutionNotOffered")
    };

    /// <summary>A mode name problem.</summary>
    public static string NameProblem(DisplayModeNameProblem problem)
        => problem switch
        {
            DisplayModeNameProblem.TooLong => TF("DmName.TooLong", DisplayModeStore.MaxNameLength),
            DisplayModeNameProblem.Empty => T("DmName.Empty"),
            DisplayModeNameProblem.Reserved => T("DmName.Reserved"),
            _ => T("DmName.Taken")
        };

    /// <summary>One line for the status bar (upstream <c>SwitchResult.Summary</c>).</summary>
    public static string Summary(DisplayModeService.SwitchResult result)
    {
        // w14 final review m7: a file that could not be read stopped the switch; what it wrote went back.
        if (result.Error is { } error)
        {
            var stopped = TF("Dm.SwitchStopped", result.GameName, UiLocalizationService.Present(error).TrimEnd('.'));
            return result.RollbackProblem is { } notBack ? stopped + " " + TF("Dm.SwitchStoppedNotPutBack", UiLocalizationService.Present(notBack)) : stopped;
        }
        // M3: Off could not put every value back: the game stays in its mode (or its pending restore stays).
        var notRestored = result.NotRestored.ToList();
        if (notRestored.Count > 0)
        {
            var what = string.Join(", ", notRestored.Select(i => Label(i.Item)));
            var line = result.ModeId is { } still
                ? TF("Dm.OffIncomplete", result.GameName, ModeName(still, result.ModeName), what)
                : TF("Dm.PendingIncomplete", result.GameName, what);
            var problems = notRestored.Where(i => i.Problem is not null).Select(i => UiLocalizationService.Present(i.Problem).TrimEnd('.')).Distinct().ToList();
            return problems.Count == 0 ? line : line + " (" + string.Join("; ", problems) + ")";
        }
        var text = result.TurnedOff ? TF("Dm.TurnedOffLine", result.GameName) : TF("Dm.SwitchedLine", result.GameName, ModeName(result.ModeId, result.ModeName));
        var written = result.Written.ToList();
        text += written.Count > 0
            ? " " + TF("Dm.SetLine", string.Join(", ", written.Select(i => $"{Label(i.Item)} {Format(i.Item, i.Value)}")))
            : " " + T(result.TurnedOff || result.Items.Count > 0 ? "Dm.NothingToChange" : "Dm.NothingYet");
        var failed = result.Failed.ToList();
        if (failed.Count > 0) text += " " + TF("Dm.CouldNotSet", string.Join(", ", failed.Select(i => Label(i.Item))));
        return text;
    }

    /// <summary>"Swaps: …" for a mode button's tooltip (upstream <c>ModeButtonTooltip</c>).</summary>
    public static string ModeTip(IReadOnlyList<string> swapped, string modeName, bool active)
    {
        var what = swapped.Count == 0
            ? T("Dm.SwapsNothing")
            : string.Join(", ", swapped.Select(DisplayModeItems.Get).OfType<DisplayModeItem>().Select(Label));
        return active ? TF("Dm.ModeTipActive", modeName, what) : TF("Dm.ModeTip", modeName, what);
    }
}
