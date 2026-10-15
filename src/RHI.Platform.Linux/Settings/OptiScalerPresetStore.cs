using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

/// <summary>
/// One OptiScaler preset slot (Refs #187, upstream <c>Models/OsPreset.cs</c>): a user label and the OptiScaler ⚙
/// settings it captured, keyed by the ⚙ setting id (<c>FGInput</c>, <c>SrPreset</c>, <c>DeployStreamlineDlssEnabler</c>…)
/// with the value the ⚙ stores. A setting that was not captured is absent and is not applied.
/// </summary>
public sealed class OptiScalerPreset
{
    public string? Name { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The four global preset slots; a slot nothing was saved into is null.</summary>
public sealed class OptiScalerPresetSlots
{
    public const int Count = 4;

    public List<OptiScalerPreset?> Slots { get; set; } = [];

    /// <summary>Exactly <see cref="Count"/> slots (extra entries dropped, missing ones empty), names trimmed.</summary>
    public OptiScalerPresetSlots Normalize()
    {
        var slots = (Slots ?? []).Take(Count).ToList();
        while (slots.Count < Count) slots.Add(null);
        foreach (var slot in slots.Where(slot => slot is not null))
        {
            slot!.Name = string.IsNullOrWhiteSpace(slot.Name) ? null : slot.Name.Trim();
            slot.Settings = new Dictionary<string, string>(
                (slot.Settings ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null),
                StringComparer.Ordinal);
        }
        Slots = slots;
        return this;
    }
}

/// <summary>
/// <c>~/.config/rhi-linux/optiscaler-presets.json</c>: the four global OptiScaler preset slots (Refs #187, upstream
/// <c>OsPresetService</c>: global, not per game) through <see cref="JsonFileStore{T}"/> (missing = upstream's first-run
/// slots; unreadable = copied aside and saves refused until it reads again). Nothing is written until the user saves a slot.
/// </summary>
public sealed class OptiScalerPresetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly JsonFileStore<OptiScalerPresetSlots> _file;

    public OptiScalerPresetStore(string? path = null)
        => _file = new JsonFileStore<OptiScalerPresetSlots>(path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "optiscaler-presets.json"), Options,
            parse: text => JsonSerializer.Deserialize<OptiScalerPresetSlots>(text, Options)?.Normalize());

    public string FilePath => _file.FilePath;

    /// <summary>The saved slots, or upstream's first-run slots (slot 1 "DLSS Enabler") when none were saved.</summary>
    public OptiScalerPresetSlots Load() => _file.LoadOr(Defaults).Normalize();

    /// <summary>Saves <paramref name="slots"/> (throws an <see cref="IOException"/> when refused).</summary>
    public void Save(OptiScalerPresetSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _file.Save(slots.Normalize());
    }

    /// <summary>
    /// Upstream's first-run slots: slot 1 is "DLSS Enabler" (FG input = the upscaler, FG output = DLSSG, Nvngx
    /// replacement = Enabler, Streamline / DLSS Enabler deployed); the others are empty.
    /// </summary>
    public static OptiScalerPresetSlots Defaults() => new OptiScalerPresetSlots
    {
        Slots =
        [
            new OptiScalerPreset
            {
                Name = "DLSS Enabler",
                Settings = new(StringComparer.Ordinal)
                {
                    ["DeployStreamlineDlssEnabler"] = "true",
                    ["FGInput"] = "upscaler",
                    ["FGOutput"] = "dlssg",
                    ["FGNvngxReplacement"] = "Arturs"
                }
            },
            null, null, null
        ]
    }.Normalize();
}
