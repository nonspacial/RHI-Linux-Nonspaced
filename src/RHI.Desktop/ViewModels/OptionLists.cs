using System.Collections.ObjectModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// One option of a dropdown: the value RHI stores, and its label — a <see cref="UiStrings"/> key (localised) or a fixed
/// label (product names, "DirectX 11"). <paramref name="Aliases"/> are labels older builds showed or stored.
/// </summary>
public sealed record OptionEntry<T>(T Value, string? TextKey = null, string? FixedLabel = null, params string[] Aliases);

/// <summary>
/// The single display↔value mapping of one option list (UI-20, Refs #87). Labels are read in the current UI language;
/// a label is read back in either language (a dialog or dropdown can outlive a language switch), as the stored value
/// itself, or as an older label. Anything else reads as the fallback value.
/// </summary>
public sealed class OptionList<T>
{
    private readonly OptionEntry<T>[] _entries;
    private readonly IEqualityComparer<T> _comparer;

    public OptionList(T fallback, IEqualityComparer<T>? comparer, params OptionEntry<T>[] entries)
    {
        if (entries.Length == 0) throw new ArgumentException("An option list needs options.", nameof(entries));
        _entries = entries;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        Fallback = fallback;
        if (!entries.Any(e => _comparer.Equals(e.Value, fallback)))
            throw new ArgumentException("The fallback must be one of the options.", nameof(fallback));
    }

    public T Fallback { get; }

    /// <summary>The stored values, in display order.</summary>
    public IReadOnlyList<T> Values => _entries.Select(e => e.Value).ToArray();

    /// <summary>The labels in the current UI language, in display order.</summary>
    public IReadOnlyList<string> Items => _entries.Select(Label).ToArray();

    /// <summary>The label of <paramref name="value"/> in the current UI language (the fallback's for an unknown value).</summary>
    public string Display(T value) => Label(Find(value) ?? Find(Fallback)!);

    /// <summary>The value behind <paramref name="display"/> (see the class summary); the fallback when nothing matches.</summary>
    public T Value(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return Fallback;
        var text = display.Trim();
        foreach (var entry in _entries)
            if (Labels(entry).Any(label => string.Equals(label, text, StringComparison.OrdinalIgnoreCase)))
                return entry.Value;
        return Fallback;
    }

    /// <summary>Every label <paramref name="entry"/> is read back from (both languages, the value, aliases).</summary>
    internal IEnumerable<string> Labels(OptionEntry<T> entry)
    {
        yield return Label(entry);
        if (entry.TextKey is { } key)
            foreach (var language in OptionLists.Languages)
                yield return UiLocalizationService.TextFor(language, key);
        if (entry.Value?.ToString() is { Length: > 0 } stored) yield return stored;
        foreach (var alias in entry.Aliases) yield return alias;
    }

    internal IReadOnlyList<OptionEntry<T>> Entries => _entries;

    /// <summary>
    /// The options as <c>{Value, Display}</c> items for a dropdown bound to values (UI-12/20, Refs #89 unit 6): the
    /// selection is the item, never its label, and a language change relabels the same items in place.
    /// </summary>
    public OptionItems<T> Options() => new(_entries.Select(entry => new OptionItem<T>(this, entry.Value)).ToArray(), _comparer, this);

    /// <summary>Replaces <paramref name="target"/>'s items with the labels in the current UI language.</summary>
    public void Fill(ObservableCollection<string> target)
    {
        target.Clear();
        foreach (var item in Items) target.Add(item);
    }

    private OptionEntry<T>? Find(T value) => _entries.FirstOrDefault(e => _comparer.Equals(e.Value, value));

    private static string Label(OptionEntry<T> entry)
        => entry.TextKey is { } key ? UiLocalizationService.Text(key) : entry.FixedLabel ?? entry.Value?.ToString() ?? "";
}

/// <summary>A dropdown option as the view sees it: its label in the current UI language (item templates bind this).</summary>
public abstract class OptionItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public abstract string Display { get; }

    /// <summary>The UI language changed: the bound label follows.</summary>
    internal void Relabel() => OnPropertyChanged(nameof(Display));

    public override string ToString() => Display;
}

/// <summary>One dropdown option: its stored value and its label in the current UI language.</summary>
public sealed class OptionItem<T> : OptionItem
{
    private readonly OptionList<T> _list;

    internal OptionItem(OptionList<T> list, T value)
    {
        _list = list;
        Value = value;
    }

    public T Value { get; }

    public override string Display => _list.Display(Value);
}

/// <summary>A list's <see cref="OptionItem{T}"/>s, created once; look one up by value or by any of its labels.</summary>
public sealed class OptionItems<T> : System.Collections.ObjectModel.ReadOnlyCollection<OptionItem<T>>
{
    private readonly IEqualityComparer<T> _comparer;
    private readonly OptionList<T> _list;

    internal OptionItems(IList<OptionItem<T>> items, IEqualityComparer<T> comparer, OptionList<T> list) : base(items)
    {
        _comparer = comparer;
        _list = list;
    }

    /// <summary>The item holding <paramref name="value"/>, or the list's fallback item.</summary>
    public OptionItem<T> For(T value)
        => this.FirstOrDefault(item => _comparer.Equals(item.Value, value)) ?? this.First(item => _comparer.Equals(item.Value, _list.Fallback));

    /// <summary>The item a label (either language, stored value or an old alias) stands for.</summary>
    public OptionItem<T> ForDisplay(string? display) => For(_list.Value(display));

    /// <summary>The UI language changed: every item's label follows (the same items stay selected).</summary>
    public void Relabel()
    {
        foreach (var item in this) item.Relabel();
    }
}

/// <summary>
/// Every option list the main window and its dialogs show, each with its one display↔value mapping (UI-20, Refs #87).
/// The view model and the Settings / Edit game dialogs read labels and values only through these.
/// </summary>
public static class OptionLists
{
    /// <summary>The UI languages a label is read back in.</summary>
    internal static readonly string[] Languages = ["pt-BR", "en-US"];

    public static readonly OptionList<string> ReShadeChannel = new("Stable", StringComparer.OrdinalIgnoreCase,
        new("Stable", "Ui.OptionStable"),
        new("Nightly", "Ui.OptionNightly", null, "Desenvolvimento", "Desenvolvimento (Nightly)"),
        new("Legacy", "Ui.OptionLegacy", null, "Legado"),
        new("Custom", "Ui.OptionCustom", null, "Personalizado"),
        new("NoAddons", "Names.OptionNoAddons", null, "No Addons", "Sem add-ons")); // w3/dllnames (#91)

    /// <summary>w3/dllnames (#97): the GPU an OptiScaler INI template is for; brand names, not translated (upstream's list).</summary>
    public static readonly OptionList<string> OptiScalerGpuType = new("NVIDIA", StringComparer.OrdinalIgnoreCase,
        new("NVIDIA", null, "NVIDIA"),
        new("AMD", null, "AMD"),
        new("Intel", null, "Intel"));

    /// <summary>w3/dllnames (#97): OptiScaler "DLSS inputs" for AMD/Intel templates (Yes first, upstream's order).</summary>
    public static readonly OptionList<bool> OptiScalerDlssInputs = new(true, null,
        new(true, "Names.Yes"),
        new(false, "Names.No"));

    /// <summary>UI languages are listed by their own (untranslated) names.</summary>
    public static readonly OptionList<string> UiLanguage = new("pt-BR", StringComparer.OrdinalIgnoreCase,
        new("pt-BR", null, "Português (Brasil)"),
        new("en-US", null, "English (United States)"));

    public static readonly OptionList<string> ViewMode = new("Detail", StringComparer.OrdinalIgnoreCase,
        new("Detail", "Win.Detailed"),
        new("Compact", "Win.Compact"));

    public static readonly OptionList<OptiScalerVariant> OptiScalerVariant = new(Platform.Linux.Deployment.OptiScalerVariant.Stable, null,
        new(Platform.Linux.Deployment.OptiScalerVariant.Stable, "Ui.OptionStable"),
        new(Platform.Linux.Deployment.OptiScalerVariant.Nightly, "Ui.OptionNightly", null, "Desenvolvimento", "Desenvolvimento (Nightly)"),
        new(Platform.Linux.Deployment.OptiScalerVariant.DlssNr, null, "DLSS NR"),
        new(Platform.Linux.Deployment.OptiScalerVariant.F5, null, "F5 DLSSNR")); // w3/sync #157

    public static readonly OptionList<LumaModePreference> LumaMode = new(LumaModePreference.Auto, null,
        new(LumaModePreference.Auto, "Ui.OptionAutomatic"),
        new(LumaModePreference.Enabled, "Ui.OptionEnabled", null, "Ativado"),
        new(LumaModePreference.Disabled, "Ui.OptionDisabled", null, "Desativado"));

    public static readonly OptionList<string> HdrMode = new(LinuxHdrModes.Off, StringComparer.OrdinalIgnoreCase,
        new(LinuxHdrModes.Off, "Ui.HdrOff"),
        new(LinuxHdrModes.NativeWayland, "Ui.HdrNativeWayland"),
        new(LinuxHdrModes.Gamescope, "Ui.HdrGamescope"));

    /// <summary>null is "Automatic" (no override).</summary>
    public static readonly OptionList<GraphicsApi?> GraphicsApiOverride = new(null, null,
        new(null, "Ui.OptionAutomatic"),
        new(GraphicsApi.DirectX8, null, "DirectX 8"),
        new(GraphicsApi.DirectX9, null, "DirectX 9"),
        new(GraphicsApi.DirectX10, null, "DirectX 10"),
        new(GraphicsApi.DirectX11, null, "DirectX 11"),
        new(GraphicsApi.DirectX12, null, "DirectX 12"),
        new(GraphicsApi.Vulkan, null, "Vulkan"),
        new(GraphicsApi.OpenGL, null, "OpenGL"));

    /// <summary>Library filter names (the chips pass the stored value; the game count shows the label).</summary>
    public static readonly OptionList<string> LibraryFilter = new("All Games", StringComparer.OrdinalIgnoreCase,
        new("All Games", "Ui.All"),
        new("Favourites", "Ui.Favourites"),
        new("Installed", "Ui.Installed"),
        new("Unreal", null, "Unreal"),
        new("Unity", null, "Unity"),
        new("Other", "Ui.Other"),
        new("RenoDX", null, "RenoDX"),
        new("Luma", null, "Luma"),
        new("Hidden", "Ui.Hidden"));

    /// <summary>Edit game's launcher badge: stored tokens stay canonical (PT-BR "Automático"/"Outro" in older files).</summary>
    public static readonly OptionList<string> LaunchProvider = new(LinuxGameIdentityService.AutomaticLaunchProvider, StringComparer.OrdinalIgnoreCase,
        LinuxGameIdentityService.LaunchProviderOptions.Select(value =>
            value == LinuxGameIdentityService.AutomaticLaunchProvider ? new OptionEntry<string>(value, "Ui.OptionAutomatic", null, "Automatic")
            : value == LinuxGameIdentityService.OtherLaunchProvider ? new OptionEntry<string>(value, "Win.ProviderOther", null, "Other")
            : new OptionEntry<string>(value, null, value)).ToArray());

    // w5/update-policy rows
    // w5/catalogue-info rows
    // w5/ini-presets rows
    // w5/shell-launch rows
    /// <summary>Edit game's bitness (Refs #190): null is "Automatic" (the manifest's override, else the exe header).</summary>
    public static readonly OptionList<PeArchitecture?> Bitness = new(null, null,
        new(null, "Ui.OptionAutomatic"),
        new(PeArchitecture.X86, "Shell.Bitness32"),
        new(PeArchitecture.X64, "Shell.Bitness64"));

    /// <summary>#191: the game resolutions offered for the resolution auto-toggle (common 16:9, 16:10 and 21:9 sizes).</summary>
    public static readonly OptionList<string> GameResolution = new("1920x1080", StringComparer.OrdinalIgnoreCase,
        new[] { "1280x720", "1280x800", "1366x768", "1600x900", "1680x1050", "1920x1080", "1920x1200", "2560x1080",
                "2560x1440", "2560x1600", "3440x1440", "3840x1600", "3840x2160", "5120x1440" }
            .Select(size => new OptionEntry<string>(size, null, size.Replace("x", " × ", StringComparison.Ordinal))).ToArray());

    /// <summary>
    /// #187: the global OptiScaler overlay key: "" keeps each INI template's own key (default), then OptiScaler's Auto,
    /// upstream's keys (virtual-key codes) and Disabled.
    /// </summary>
    public static readonly OptionList<string> OptiScalerMenuKey = new("", StringComparer.OrdinalIgnoreCase,
        [
            new("", "Shell.MenuKeyTemplate"),
            .. OptiScalerDeploymentService.MenuKeyOptions.Select(option => option.Value switch
            {
                "auto" => new OptionEntry<string>("auto", "Shell.MenuKeyAuto"),
                "-1" => new OptionEntry<string>("-1", "Ui.OptionDisabled"),
                _ => new OptionEntry<string>(option.Value, null, option.Name)
            })
        ]);
    // w5/reshade-core rows

    /// <summary>The "Latest" entry heading every version list.</summary>
    public static string LatestDisplay() => UiLocalizationService.Text("Ui.OptionLatest");

    /// <summary>True for the "Latest" entry in either language (a list can outlive a language switch).</summary>
    public static bool IsLatest(string? value) => IsKeyText(value, "Ui.OptionLatest");

    /// <summary>The "Do not change" entry of the DLSS default-version lists.</summary>
    public static string DoNotChangeDisplay() => UiLocalizationService.Text("Win.DoNotChange");

    /// <summary>True for the "Do not change" entry in either language.</summary>
    public static bool IsDoNotChange(string? value) => IsKeyText(value, "Win.DoNotChange");

    private static bool IsKeyText(string? value, string key)
        => !string.IsNullOrWhiteSpace(value) &&
           OptionLists.Languages.Any(language =>
               string.Equals(value.Trim(), UiLocalizationService.TextFor(language, key), StringComparison.OrdinalIgnoreCase));
}
