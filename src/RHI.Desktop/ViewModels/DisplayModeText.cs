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

    public static string Label(DisplayModeItem item) => T("Dm.Item." + item.Key);
    public static string Help(DisplayModeItem item) => T("Dm.Help." + item.Key);
    public static string Group(DisplayModeGroup group) => T("Dm.Group." + group);

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
                    return T("Dm.Choice.Off");
                if (item.Choices == DisplayModeChoices.Resolution && value.StartsWith(DisplayModeItems.ResolutionOn, StringComparison.Ordinal))
                    return T("Dm.Choice.ResolutionOn");
                return Choices(item).FirstOrDefault(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;
            case DisplayModeValueKind.ReShadeKey:
                return ReShadeHotkeyCodec.TryParse(value, out var binding) ? binding.ToDisplayText() : value;
            case DisplayModeValueKind.Text:
                return value.Length == 0 ? T("Dm.None") : value;
            case DisplayModeValueKind.Number:
            case DisplayModeValueKind.Decimal:
                if (item.AllowZero && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == 0) return T("Dm.Choice.Off");
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
                return [new("0", T("Dm.Choice.Off")), new("1", T("Dm.Choice.On"))];
            case DisplayModeChoices.FpsLimit:
                return DisplayModeItems.FpsLimitValues.Select(v => new DisplayModeChoice(v, v switch
                {
                    DisplayModeItems.FpsGlobal => T("Dm.Choice.FollowGlobal"),
                    "0" => T("Dm.Choice.Off"),
                    _ => $"{v} FPS"
                })).ToList();
            case DisplayModeChoices.FrameGeneration:
                return DisplayModeItems.FrameGenerationValues.Select(v => new DisplayModeChoice(v, v switch
                {
                    DisplayModeItems.FrameGenerationOff => T("Dm.Choice.Off"),
                    DisplayModeItems.FrameGenerationDynamic => T("Dm.Choice.Dynamic"),
                    _ => (int.Parse(v[DisplayModeItems.FrameGenerationFixedPrefix.Length..], CultureInfo.InvariantCulture) + 1) + "x"
                })).ToList();
            case DisplayModeChoices.SrPreset:
                return Presets(OptionLists.DlssPresets(DlssPresetCatalogue.Current.SuperResolution));
            case DisplayModeChoices.RrPreset:
                return Presets(OptionLists.DlssPresets(DlssPresetCatalogue.Current.RayReconstruction));
            case DisplayModeChoices.RenderScale:
                return DisplayModeItems.RenderScaleValues.Select(v => new DisplayModeChoice(v,
                    v == "0" ? T("Dm.Choice.GameDecides") : OptionLists.NvidiaRenderScale.Display(uint.Parse(v, CultureInfo.InvariantCulture)))).ToList();
            case DisplayModeChoices.HdrMode:
                return OptionLists.HdrMode.Values.Select(v => new DisplayModeChoice(v, OptionLists.HdrMode.Display(v))).ToList();
            case DisplayModeChoices.Resolution:
                var list = new List<DisplayModeChoice> { new(DisplayModeItems.ResolutionOff, T("Dm.Choice.Off")), new(DisplayModeItems.ResolutionOn, T("Dm.Choice.ResolutionOn")) };
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
    public static string Reason(DisplayModeUnavailable reason) => T("Dm.Why." + reason);

    /// <summary>A mode name problem.</summary>
    public static string NameProblem(DisplayModeNameProblem problem)
        => problem == DisplayModeNameProblem.TooLong ? TF("Dm.Name.TooLong", DisplayModeStore.MaxNameLength) : T("Dm.Name." + problem);

    /// <summary>One line for the status bar (upstream <c>SwitchResult.Summary</c>).</summary>
    public static string Summary(DisplayModeService.SwitchResult result)
    {
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
