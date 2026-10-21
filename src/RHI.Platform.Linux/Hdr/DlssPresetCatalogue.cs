using System.Text.Json.Serialization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Hdr;

/// <summary>#211: one DLSS render preset as upstream lists it (its label and the driver-setting value).</summary>
public sealed record DlssPreset(string Name, uint Value);

/// <summary>#211: the DLSS preset lists for Super Resolution, Ray Reconstruction and Frame Generation.</summary>
public sealed record DlssPresetLists(
    IReadOnlyList<DlssPreset> SuperResolution,
    IReadOnlyList<DlssPreset> RayReconstruction,
    IReadOnlyList<DlssPreset> FrameGeneration);

/// <summary>#211: a manifest <c>dlssPresets</c> entry (upstream <c>ManifestPresetEntry</c>).</summary>
public sealed record UpstreamDlssPresetEntry(string Name, long Value, bool Disabled);

/// <summary>#211: manifest <c>dlssPresets</c> (upstream <c>ManifestDlssPresets</c>; <c>nr</c> is upstream's developer-only list, not read).</summary>
public sealed record UpstreamDlssPresetsDefinition(
    IReadOnlyList<UpstreamDlssPresetEntry> SuperResolution,
    IReadOnlyList<UpstreamDlssPresetEntry> RayReconstruction,
    IReadOnlyList<UpstreamDlssPresetEntry> FrameGeneration);

/// <summary>
/// #211: the DLSS preset lists the per-game NVIDIA settings offer — upstream RHI 2.8.7's built-in lists
/// (<c>DlssPresetService.SrPresets/RrPresets/FgPresets</c>) merged with the manifest's <c>dlssPresets</c> exactly as
/// upstream's <c>MergePresets</c> does: a new name goes in alphabetically between "Default" (first) and
/// "NVIDIA Recommended" (last), an existing name is kept, <c>disabled</c> removes a name (never "Default").
/// The library scan hands every manifest it reads to <see cref="Observe"/>; until one has been read (or when it has
/// no <c>dlssPresets</c>) the lists are <see cref="Fallback"/>.
/// </summary>
public static class DlssPresetCatalogue
{
    /// <summary>"Default": no override (the variable is not set).</summary>
    public const uint Default = 0;

    /// <summary>Upstream's "NVIDIA Recommended" for SR and RR (<c>RENDER_PRESET_Latest</c>).</summary>
    public const uint Recommended = 0x00FFFFFF;

    /// <summary>Upstream's "NVIDIA Recommended" for FG (<c>RENDER_PRESET_Default</c>).</summary>
    public const uint FrameGenerationRecommended = 0x00FFFFFE;

    /// <summary>The highest value a preset setting takes (<c>RENDER_PRESET_Latest</c>).</summary>
    public const uint MaxValue = 0x00FFFFFF;

    internal const string DefaultName = "Default";
    internal const string RecommendedName = "NVIDIA Recommended";

    /// <summary>Upstream RHI 2.8.7's built-in lists (<c>DlssPresetService.cs</c>).</summary>
    public static DlssPresetLists BuiltIn { get; } = new(
        [new(DefaultName, Default), new("J - TF1", 10), new("K - TF1", 11), new("L - TF2", 12), new("M - TF2", 13), new(RecommendedName, Recommended)],
        [new(DefaultName, Default), new("D - TF1", 4), new("E - TF1", 5), new(RecommendedName, Recommended)],
        [new(DefaultName, Default), new("A", 1), new("B", 2), new(RecommendedName, FrameGenerationRecommended)]);

    /// <summary>
    /// The upstream manifest's <c>dlssPresets</c> at the wave-7 sync (RHI-2.8.7 plus main <c>d1c39d0</c>, FG preset C).
    /// </summary>
    public static UpstreamDlssPresetsDefinition SyncedManifest { get; } = new(
        [new("F - CNN", 6, false), new("E - CNN", 5, false)],
        [new("F - TF2", 6, false)],
        [new("C", 3, false)]);

    /// <summary>The lists before (or without) a manifest: the built-in lists merged with <see cref="SyncedManifest"/>.</summary>
    public static DlssPresetLists Fallback { get; } = Merge(BuiltIn, SyncedManifest);

    /// <summary>The app's one source (the library scan feeds it; the NVIDIA settings read it).</summary>
    internal static DlssPresetSource Shared { get; } = new();

    /// <summary>The lists from the newest manifest read, else <see cref="Fallback"/>.</summary>
    public static DlssPresetLists Current => Shared.Current;

    /// <summary>
    /// The library scan read <paramref name="policy"/>: its <c>dlssPresets</c> (if any) become <see cref="Current"/>.
    /// Cheap for every game of one scan (the same snapshot object is skipped).
    /// </summary>
    public static void Observe(UpstreamLibraryPolicySnapshot policy) => Shared.Observe(policy);

    /// <summary>Upstream's <c>MergePresets</c> per list.</summary>
    public static DlssPresetLists Merge(DlssPresetLists lists, UpstreamDlssPresetsDefinition? manifest)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (manifest is null) return lists;
        return new DlssPresetLists(
            Merge(lists.SuperResolution, manifest.SuperResolution),
            Merge(lists.RayReconstruction, manifest.RayReconstruction),
            Merge(lists.FrameGeneration, manifest.FrameGeneration));
    }

    private static IReadOnlyList<DlssPreset> Merge(IReadOnlyList<DlssPreset> existing, IReadOnlyList<UpstreamDlssPresetEntry> entries)
    {
        var merged = new List<DlssPreset>(existing);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var name = entry.Name.Trim();
            if (entry.Disabled)
            {
                if (!name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
                    merged.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            // A value outside the driver setting's range (or one that collides with Default) cannot be offered.
            if (entry.Value <= 0 || entry.Value > MaxValue) continue;
            if (merged.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var insert = merged.Count;
            for (var i = 1; i < merged.Count; i++)
            {
                if (merged[i].Name.Equals(RecommendedName, StringComparison.OrdinalIgnoreCase)
                    || string.Compare(name, merged[i].Name, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    insert = i;
                    break;
                }
            }
            merged.Insert(insert, new DlssPreset(name, (uint)entry.Value));
        }
        return merged;
    }

    /// <summary>Manifest document → snapshot (null when the manifest has no <c>dlssPresets</c>).</summary>
    internal static UpstreamDlssPresetsDefinition? FromManifest(UpstreamManifestDlssPresets? presets)
    {
        if (presets is null) return null;
        static IReadOnlyList<UpstreamDlssPresetEntry> List(List<UpstreamManifestDlssPresetEntry?>? entries)
            => (entries ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => new UpstreamDlssPresetEntry(e!.Name!.Trim(), e.Value ?? 0, e.Disabled == true)).ToArray();
        return new UpstreamDlssPresetsDefinition(List(presets.Sr), List(presets.Rr), List(presets.Fg));
    }
}

/// <summary>#211: manifest <c>dlssPresets</c> as read (upstream <c>ManifestDlssPresets</c>).</summary>
internal sealed class UpstreamManifestDlssPresets
{
    [JsonPropertyName("sr")]
    public List<UpstreamManifestDlssPresetEntry?>? Sr { get; set; }

    [JsonPropertyName("rr")]
    public List<UpstreamManifestDlssPresetEntry?>? Rr { get; set; }

    [JsonPropertyName("fg")]
    public List<UpstreamManifestDlssPresetEntry?>? Fg { get; set; }
}

/// <summary>#211: one manifest preset entry (upstream <c>ManifestPresetEntry</c>).</summary>
internal sealed class UpstreamManifestDlssPresetEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public long? Value { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }
}

/// <summary>
/// #211: the DLSS preset lists one manifest reader last saw. The app uses <see cref="DlssPresetCatalogue.Shared"/>;
/// tests use their own instance, so a test never changes the lists other tests read.
/// </summary>
internal sealed class DlssPresetSource
{
    private DlssPresetLists? _current;
    private UpstreamDlssPresetsDefinition? _observed;

    public DlssPresetLists Current => Volatile.Read(ref _current) ?? DlssPresetCatalogue.Fallback;

    public void Observe(UpstreamLibraryPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var presets = policy.DlssPresets;
        if (presets is null || ReferenceEquals(presets, Volatile.Read(ref _observed))) return;
        Volatile.Write(ref _current, DlssPresetCatalogue.Merge(DlssPresetCatalogue.BuiltIn, presets));
        Volatile.Write(ref _observed, presets);
    }
}
