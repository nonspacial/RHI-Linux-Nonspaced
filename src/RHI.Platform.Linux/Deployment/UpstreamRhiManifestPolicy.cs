using System.Text.Json.Serialization;

namespace RHI.Platform.Linux.Deployment;

public sealed record UpstreamSplitGameDefinition(string Name, string SubPath);
public sealed record UpstreamExternalDefinition(string Url, string Label);
public sealed record UpstreamDllNamesDefinition(string? ReShade, string? DisplayCommander);
public sealed record UpstreamUeExtendedCompatibilityDefinition(bool? Hdr, bool? Lut);
public sealed record UpstreamGameNoteDefinition(string? Notes, string? Url, string? UrlLabel);

/// <summary>#184: keys of <see cref="UpstreamLibraryPolicySnapshot.ComponentGameNotes"/> (one per manifest <c>*GameInfo</c> map).</summary>
public static class ComponentNoteKeys
{
    public const string ReFramework = "reframework";
    public const string ReLimiter = "relimiter";
    public const string DisplayCommander = "displayCommander";
    public const string OptiScaler = "optiScaler";
    public const string Luma = "luma";
    /// <summary>w5 integration: manifest <c>reshadeGameInfo</c> (#186), also shown in ReShade's ⓘ dialog.</summary>
    public const string ReShade = "reshade";
}

/// <summary>
/// Normalized, Linux-consumable policy snapshot from upstream RHI's remote manifest.
/// It intentionally contains policy metadata even when the corresponding deployment
/// backend is not implemented yet, so future features can consume one shared authority.
/// </summary>
public sealed class UpstreamLibraryPolicySnapshot
{
    public int Version { get; init; }
    /// <summary>All game-title keys known to this manifest, including fields consumed by later probes.</summary>
    public IReadOnlySet<string> KnownGameNames { get; init; } = EmptySet();
    public IReadOnlySet<string> Blacklist { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlacklistPrefixes { get; init; } = [];
    public IReadOnlyDictionary<string, string> WikiNameOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> InstallPathOverrides { get; init; } = EmptyStrings();
    /// <summary>Per-game upstream engine-files template filename. The template itself is fetched separately and applied transactionally.</summary>
    public IReadOnlyDictionary<string, string> EngineIniFiles { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, IReadOnlyList<UpstreamSplitGameDefinition>> SplitGames { get; init; }
        = new Dictionary<string, IReadOnlyList<UpstreamSplitGameDefinition>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> SteamAppIdOverrides { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> WikiUnlinks { get; init; } = EmptySet();
    public IReadOnlySet<string> DlssSkipGames { get; init; } = EmptySet();
    public IReadOnlySet<string> UeExtendedGames { get; init; } = EmptySet();
    public IReadOnlySet<string> NativeHdrGames { get; init; } = EmptySet();
    public IReadOnlySet<string> NoUeExtendedGames { get; init; } = EmptySet();
    public IReadOnlyDictionary<string, UpstreamUeExtendedCompatibilityDefinition> UeExtendedCompatibility { get; init; }
        = new Dictionary<string, UpstreamUeExtendedCompatibilityDefinition>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, UpstreamExternalDefinition> ForceExternalOnly { get; init; }
        = new Dictionary<string, UpstreamExternalDefinition>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, UpstreamDllNamesDefinition> DllNameOverrides { get; init; }
        = new Dictionary<string, UpstreamDllNamesDefinition>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> OptiScalerDllOverrides { get; init; } = EmptyStrings();
    /// <summary>#155: game → DLSS folder relative to the game root (manifest <c>dlssPathOverrides</c>).</summary>
    public IReadOnlyDictionary<string, string> DlssPathOverrides { get; init; } = EmptyStrings();
    /// <summary>#155: game → Streamline folder relative to the game root (manifest <c>streamlinePathOverrides</c>).</summary>
    public IReadOnlyDictionary<string, string> StreamlinePathOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> LaunchExeOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> PdUpscalerGames { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> WikiStatusOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> NexusUrlOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> PcgwUrlOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> UwFixUrlOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> UltraPlusUrlOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, string> AuthorOverrides { get; init; } = EmptyStrings();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> InstallWarnings { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, UpstreamGameNoteDefinition> GameNotes { get; init; }
        = new Dictionary<string, UpstreamGameNoteDefinition>(StringComparer.OrdinalIgnoreCase);
    // w5/update-policy rows
    // w5/catalogue-info rows
    /// <summary>#183: PCGamingWiki's AppID redirect (<c>api/appid.php</c>) is usable again (manifest <c>pcgwUseAppId</c>).</summary>
    public bool PcgwUseAppId { get; init; }
    /// <summary>#183: a higher value than the one stored wipes RHI's PCGamingWiki link cache (manifest <c>pcgwUrlCacheVersion</c>).</summary>
    public int PcgwUrlCacheVersion { get; init; }
    /// <summary>#184: Luma's per-game notes for the ⓘ dialog (manifest <c>lumaGameNotes</c>).</summary>
    public IReadOnlyDictionary<string, UpstreamGameNoteDefinition> LumaGameNotes { get; init; }
        = new Dictionary<string, UpstreamGameNoteDefinition>(StringComparer.OrdinalIgnoreCase);
    /// <summary>#184: game → its name on the OptiScaler wiki's compatibility lists (manifest <c>optiScalerWikiNames</c>).</summary>
    public IReadOnlyDictionary<string, string> OptiScalerWikiNames { get; init; } = EmptyStrings();
    /// <summary>
    /// #184: per-component game notes for the ⓘ dialogs, by component key (<see cref="ComponentNoteKeys"/>): manifest
    /// <c>reframeworkGameInfo</c>, <c>relimiterGameInfo</c>, <c>displayCommanderGameInfo</c>, <c>optiScalerGameInfo</c>,
    /// <c>lumaGameInfo</c> (RenoDX's are <c>gameNotes</c>, already on the card).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, UpstreamGameNoteDefinition>> ComponentGameNotes { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, UpstreamGameNoteDefinition>>(StringComparer.Ordinal);
    // w5/ini-presets rows
    // w5/shell-launch rows
    // w5/reshade-core rows
    /// <summary>#186: manifest <c>legacyReShadeVersions</c>: the ReShade version a fresh install of these games uses (Max Payne 3 → 6.4.1).</summary>
    public IReadOnlyDictionary<string, string> LegacyReShadeVersions { get; init; } = EmptyStrings();
    /// <summary>#186: manifest <c>reshadeGameInfo</c>: a per-game note shown with ReShade (same shape as <c>gameNotes</c>).</summary>
    public IReadOnlyDictionary<string, UpstreamGameNoteDefinition> ReShadeGameInfo { get; init; }
        = new Dictionary<string, UpstreamGameNoteDefinition>(StringComparer.OrdinalIgnoreCase);
    // w7/smalls rows
    // w7/reshade rows
    // w7/shader-sets rows
    // w7/nvapi-launch rows
    // w7/dxvk-hdr rows

    private static IReadOnlyDictionary<string, string> EmptyStrings()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlySet<string> EmptySet()
        => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed partial class UpstreamManifestDocument
{
    [JsonPropertyName("blacklist")]
    public List<string>? Blacklist { get; set; }

    [JsonPropertyName("blacklistPrefixes")]
    public List<string>? BlacklistPrefixes { get; set; }

    [JsonPropertyName("wikiNameOverrides")]
    public Dictionary<string, string>? WikiNameOverrides { get; set; }

    [JsonPropertyName("installPathOverrides")]
    public Dictionary<string, string>? InstallPathOverrides { get; set; }

    [JsonPropertyName("splitGames")]
    public Dictionary<string, List<UpstreamManifestSplitGame>>? SplitGames { get; set; }

    [JsonPropertyName("steamAppIdOverrides")]
    public Dictionary<string, int>? SteamAppIdOverrides { get; set; }

    [JsonPropertyName("wikiUnlinks")]
    public List<string>? WikiUnlinks { get; set; }

    [JsonPropertyName("dlssSkipGames")]
    public List<string>? DlssSkipGames { get; set; }

    [JsonPropertyName("ueExtendedGames")]
    public List<string>? UeExtendedGames { get; set; }

    [JsonPropertyName("nativeHdrGames")]
    public List<string>? NativeHdrGames { get; set; }

    [JsonPropertyName("noUeExtendedGames")]
    public List<string>? NoUeExtendedGames { get; set; }

    [JsonPropertyName("ueExtendedCompatibility")]
    public Dictionary<string, UpstreamManifestUeExtendedCompatibility>? UeExtendedCompatibility { get; set; }

    [JsonPropertyName("forceExternalOnly")]
    public Dictionary<string, UpstreamManifestForceExternal>? ForceExternalOnly { get; set; }

    [JsonPropertyName("dllNameOverrides")]
    public Dictionary<string, UpstreamManifestDllNames>? DllNameOverrides { get; set; }

    [JsonPropertyName("optiScalerDllOverrides")]
    public Dictionary<string, string>? OptiScalerDllOverrides { get; set; }

    [JsonPropertyName("dlssPathOverrides")]
    public Dictionary<string, string>? DlssPathOverrides { get; set; }

    [JsonPropertyName("streamlinePathOverrides")]
    public Dictionary<string, string>? StreamlinePathOverrides { get; set; }

    [JsonPropertyName("launchExeOverrides")]
    public Dictionary<string, string>? LaunchExeOverrides { get; set; }

    [JsonPropertyName("pdUpscalerGames")]
    public Dictionary<string, string>? PdUpscalerGames { get; set; }

    [JsonPropertyName("wikiStatusOverrides")]
    public Dictionary<string, string>? WikiStatusOverrides { get; set; }

    [JsonPropertyName("nexusUrlOverrides")]
    public Dictionary<string, string>? NexusUrlOverrides { get; set; }

    [JsonPropertyName("pcgwUrlOverrides")]
    public Dictionary<string, string>? PcgwUrlOverrides { get; set; }

    [JsonPropertyName("uwFixUrlOverrides")]
    public Dictionary<string, string>? UwFixUrlOverrides { get; set; }

    [JsonPropertyName("ultraPlusUrlOverrides")]
    public Dictionary<string, string>? UltraPlusUrlOverrides { get; set; }

    [JsonPropertyName("authorOverrides")]
    public Dictionary<string, string>? AuthorOverrides { get; set; }

    [JsonPropertyName("installWarnings")]
    public Dictionary<string, Dictionary<string, string>>? InstallWarnings { get; set; }

    [JsonPropertyName("gameNotes")]
    public Dictionary<string, UpstreamManifestGameNote>? GameNotes { get; set; }
    // w5/update-policy rows
    // w5/catalogue-info rows
    [JsonPropertyName("pcgwUseAppId")]
    public bool? PcgwUseAppId { get; set; }

    [JsonPropertyName("pcgwUrlCacheVersion")]
    public int? PcgwUrlCacheVersion { get; set; }

    [JsonPropertyName("lumaGameNotes")]
    public Dictionary<string, UpstreamManifestGameNote>? LumaGameNotes { get; set; }

    [JsonPropertyName("optiScalerWikiNames")]
    public Dictionary<string, string>? OptiScalerWikiNames { get; set; }

    [JsonPropertyName("reframeworkGameInfo")]
    public Dictionary<string, UpstreamManifestGameNote>? ReFrameworkGameInfo { get; set; }

    [JsonPropertyName("relimiterGameInfo")]
    public Dictionary<string, UpstreamManifestGameNote>? ReLimiterGameInfo { get; set; }

    [JsonPropertyName("displayCommanderGameInfo")]
    public Dictionary<string, UpstreamManifestGameNote>? DisplayCommanderGameInfo { get; set; }

    [JsonPropertyName("optiScalerGameInfo")]
    public Dictionary<string, UpstreamManifestGameNote>? OptiScalerGameInfo { get; set; }

    [JsonPropertyName("lumaGameInfo")]
    public Dictionary<string, UpstreamManifestGameNote>? LumaGameInfo { get; set; }
    // w5/ini-presets rows
    // w5/shell-launch rows
    // w5/reshade-core rows
    [JsonPropertyName("legacyReShadeAvailable")]
    public List<string>? LegacyReShadeAvailable { get; set; }
    [JsonPropertyName("legacyReShadeVersions")]
    public Dictionary<string, string>? LegacyReShadeVersions { get; set; }
    [JsonPropertyName("reshadeGameInfo")]
    public Dictionary<string, UpstreamManifestGameNote>? ReShadeGameInfo { get; set; }
    // w7/smalls rows
    // w7/reshade rows
    // w7/shader-sets rows
    // w7/nvapi-launch rows
    // w7/dxvk-hdr rows
}

internal sealed class UpstreamManifestSplitGame
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("subPath")]
    public string? SubPath { get; set; }
}

internal sealed class UpstreamManifestForceExternal
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

internal sealed class UpstreamManifestDllNames
{
    [JsonPropertyName("reshade")]
    public string? ReShade { get; set; }

    [JsonPropertyName("dc")]
    public string? DisplayCommander { get; set; }
}

internal sealed class UpstreamManifestUeExtendedCompatibility
{
    [JsonPropertyName("hdr")]
    public bool? Hdr { get; set; }

    [JsonPropertyName("lut")]
    public bool? Lut { get; set; }
}

internal sealed class UpstreamManifestGameNote
{
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("notesUrl")]
    public string? Url { get; set; }

    [JsonPropertyName("notesUrlLabel")]
    public string? UrlLabel { get; set; }
}

public sealed partial class UpstreamRhiManifestService
{
    public async Task<UpstreamLibraryPolicySnapshot> FetchLibraryPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);

        return new UpstreamLibraryPolicySnapshot
        {
            Version = manifest.Version,
            KnownGameNames = BuildKnownGameNames(manifest),
            Blacklist = NormalizeSet(manifest.Blacklist),
            BlacklistPrefixes = NormalizeList(manifest.BlacklistPrefixes),
            WikiNameOverrides = NormalizeStringDictionary(manifest.WikiNameOverrides),
            InstallPathOverrides = NormalizeStringDictionaryAllowEmpty(manifest.InstallPathOverrides),
            EngineIniFiles = NormalizeStringDictionary(manifest.EngineIniFiles),
            SplitGames = NormalizeSplitGames(manifest.SplitGames),
            SteamAppIdOverrides = NormalizeIntDictionary(manifest.SteamAppIdOverrides),
            WikiUnlinks = NormalizeSet(manifest.WikiUnlinks),
            DlssSkipGames = NormalizeSet(manifest.DlssSkipGames),
            UeExtendedGames = NormalizeSet(manifest.UeExtendedGames),
            NativeHdrGames = NormalizeSet(manifest.NativeHdrGames),
            NoUeExtendedGames = NormalizeSet(manifest.NoUeExtendedGames),
            UeExtendedCompatibility = NormalizeUeExtendedCompatibility(manifest.UeExtendedCompatibility),
            ForceExternalOnly = NormalizeExternal(manifest.ForceExternalOnly),
            DllNameOverrides = NormalizeDllNames(manifest.DllNameOverrides),
            OptiScalerDllOverrides = NormalizeStringDictionary(manifest.OptiScalerDllOverrides),
            DlssPathOverrides = NormalizeStringDictionary(manifest.DlssPathOverrides),
            StreamlinePathOverrides = NormalizeStringDictionary(manifest.StreamlinePathOverrides),
            LaunchExeOverrides = NormalizeStringDictionary(manifest.LaunchExeOverrides),
            PdUpscalerGames = NormalizeStringDictionary(manifest.PdUpscalerGames),
            WikiStatusOverrides = NormalizeStringDictionary(manifest.WikiStatusOverrides),
            NexusUrlOverrides = NormalizeStringDictionary(manifest.NexusUrlOverrides),
            PcgwUrlOverrides = NormalizeStringDictionary(manifest.PcgwUrlOverrides),
            UwFixUrlOverrides = NormalizeStringDictionary(manifest.UwFixUrlOverrides),
            UltraPlusUrlOverrides = NormalizeStringDictionary(manifest.UltraPlusUrlOverrides),
            AuthorOverrides = NormalizeStringDictionary(manifest.AuthorOverrides),
            InstallWarnings = NormalizeNestedDictionary(manifest.InstallWarnings),
            GameNotes = NormalizeGameNotes(manifest.GameNotes),
            // w5/update-policy rows
            // w5/catalogue-info rows
            PcgwUseAppId = manifest.PcgwUseAppId == true,
            PcgwUrlCacheVersion = Math.Max(0, manifest.PcgwUrlCacheVersion ?? 0),
            LumaGameNotes = NormalizeGameNotes(manifest.LumaGameNotes),
            OptiScalerWikiNames = NormalizeStringDictionary(manifest.OptiScalerWikiNames),
            ComponentGameNotes = new Dictionary<string, IReadOnlyDictionary<string, UpstreamGameNoteDefinition>>(StringComparer.Ordinal)
            {
                [ComponentNoteKeys.ReFramework] = NormalizeGameNotes(manifest.ReFrameworkGameInfo),
                [ComponentNoteKeys.ReShade] = NormalizeGameNotes(manifest.ReShadeGameInfo), // w5 integration (#186 × #184)
                [ComponentNoteKeys.ReLimiter] = NormalizeGameNotes(manifest.ReLimiterGameInfo),
                [ComponentNoteKeys.DisplayCommander] = NormalizeGameNotes(manifest.DisplayCommanderGameInfo),
                [ComponentNoteKeys.OptiScaler] = NormalizeGameNotes(manifest.OptiScalerGameInfo),
                [ComponentNoteKeys.Luma] = NormalizeGameNotes(manifest.LumaGameInfo),
            },
            // w5/ini-presets rows
            // w5/shell-launch rows
            // w5/reshade-core rows
            LegacyReShadeVersions = NormalizeLegacyReShadeVersions(manifest.LegacyReShadeVersions),
            ReShadeGameInfo = NormalizeGameNotes(manifest.ReShadeGameInfo),
            // w7/smalls rows
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
        };
    }


    private static IReadOnlySet<string> BuildKnownGameNames(UpstreamManifestDocument manifest)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static void Add(HashSet<string> target, IEnumerable<string>? values)
        {
            if (values is null) return;
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) target.Add(value.Trim());
        }

        // Launcher/library policy keys.
        Add(result, manifest.Blacklist);
        Add(result, manifest.WikiNameOverrides?.Keys);
        Add(result, manifest.InstallPathOverrides?.Keys);
        Add(result, manifest.SplitGames?.Keys);
        if (manifest.SplitGames is not null)
            foreach (var entries in manifest.SplitGames.Values)
                Add(result, entries?.Select(x => x.Name ?? ""));
        Add(result, manifest.SteamAppIdOverrides?.Keys);
        Add(result, manifest.WikiUnlinks);
        Add(result, manifest.DlssSkipGames);
        Add(result, manifest.UeExtendedGames);
        Add(result, manifest.NativeHdrGames);
        Add(result, manifest.NoUeExtendedGames);
        Add(result, manifest.UeExtendedCompatibility?.Keys);
        Add(result, manifest.ForceExternalOnly?.Keys);
        Add(result, manifest.DllNameOverrides?.Keys);
        Add(result, manifest.OptiScalerDllOverrides?.Keys);
        Add(result, manifest.DlssPathOverrides?.Keys);
        Add(result, manifest.StreamlinePathOverrides?.Keys);
        Add(result, manifest.LaunchExeOverrides?.Keys);
        Add(result, manifest.PdUpscalerGames?.Keys);
        Add(result, manifest.WikiStatusOverrides?.Keys);
        Add(result, manifest.NexusUrlOverrides?.Keys);
        Add(result, manifest.PcgwUrlOverrides?.Keys);
        Add(result, manifest.UwFixUrlOverrides?.Keys);
        Add(result, manifest.UltraPlusUrlOverrides?.Keys);
        Add(result, manifest.AuthorOverrides?.Keys);
        Add(result, manifest.InstallWarnings?.Keys);
        Add(result, manifest.GameNotes?.Keys);

        // Existing deployment/runtime fields are part of the same canonical-name authority.
        Add(result, manifest.EngineOverrides?.Keys);
        Add(result, manifest.EngineHintOverrides?.Keys);
        Add(result, manifest.EngineIniPathOverrides?.Keys);
        Add(result, manifest.EngineIniFiles?.Keys);
        Add(result, manifest.GraphicsApiOverrides?.Keys);
        Add(result, manifest.SnapshotOverrides?.Keys);
        Add(result, manifest.ThirtyTwoBitGames);
        Add(result, manifest.SixtyFourBitGames);
        Add(result, manifest.RenoDxIniOverrides?.Keys);
        Add(result, manifest.DofFixForceGames);
        Add(result, manifest.DofFixSkipGames);
        Add(result, manifest.LumaNameOverrides?.Keys);
        Add(result, manifest.LumaDefaultGames);
        Add(result, manifest.LumaRenoDxCompat);
        // w5/update-policy rows
        // w5/catalogue-info rows
        Add(result, manifest.LumaGameNotes?.Keys);
        Add(result, manifest.OptiScalerWikiNames?.Keys);
        Add(result, manifest.ReFrameworkGameInfo?.Keys);
        Add(result, manifest.ReLimiterGameInfo?.Keys);
        Add(result, manifest.DisplayCommanderGameInfo?.Keys);
        Add(result, manifest.OptiScalerGameInfo?.Keys);
        Add(result, manifest.LumaGameInfo?.Keys);
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
        Add(result, manifest.LegacyReShadeVersions?.Keys);
        Add(result, manifest.ReShadeGameInfo?.Keys);
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        return result;
    }

    private static IReadOnlySet<string> NormalizeSet(IEnumerable<string>? values)
        => new HashSet<string>(
            (values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string>? values)
        => (values ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyDictionary<string, string> NormalizeStringDictionary(
        IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, value) in values)
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                result[key.Trim()] = value.Trim();
        return result;
    }

    private static IReadOnlyDictionary<string, string> NormalizeStringDictionaryAllowEmpty(
        IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, value) in values)
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
                result[key.Trim()] = value.Trim();
        return result;
    }

    private static IReadOnlyDictionary<string, int> NormalizeIntDictionary(
        IReadOnlyDictionary<string, int>? values)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, value) in values)
            if (!string.IsNullOrWhiteSpace(key) && value > 0)
                result[key.Trim()] = value;
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<UpstreamSplitGameDefinition>> NormalizeSplitGames(
        IReadOnlyDictionary<string, List<UpstreamManifestSplitGame>>? values)
    {
        var result = new Dictionary<string, IReadOnlyList<UpstreamSplitGameDefinition>>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, entries) in values)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var normalized = (entries ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.SubPath))
                .Select(x => new UpstreamSplitGameDefinition(x.Name!.Trim(), x.SubPath!.Trim()))
                .ToArray();
            if (normalized.Length > 0) result[key.Trim()] = normalized;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, UpstreamUeExtendedCompatibilityDefinition> NormalizeUeExtendedCompatibility(
        IReadOnlyDictionary<string, UpstreamManifestUeExtendedCompatibility>? values)
    {
        var result = new Dictionary<string, UpstreamUeExtendedCompatibilityDefinition>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, entry) in values)
            if (!string.IsNullOrWhiteSpace(key) && entry is not null)
                result[key.Trim()] = new(entry.Hdr, entry.Lut);
        return result;
    }

    private static IReadOnlyDictionary<string, UpstreamExternalDefinition> NormalizeExternal(
        IReadOnlyDictionary<string, UpstreamManifestForceExternal>? values)
    {
        var result = new Dictionary<string, UpstreamExternalDefinition>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, entry) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(entry?.Url)) continue;
            result[key.Trim()] = new(entry.Url!.Trim(), string.IsNullOrWhiteSpace(entry.Label) ? "Open external mod" : entry.Label!.Trim());
        }
        return result;
    }

    private static IReadOnlyDictionary<string, UpstreamDllNamesDefinition> NormalizeDllNames(
        IReadOnlyDictionary<string, UpstreamManifestDllNames>? values)
    {
        var result = new Dictionary<string, UpstreamDllNamesDefinition>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, entry) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || entry is null) continue;
            result[key.Trim()] = new(NullIfWhiteSpace(entry.ReShade), NullIfWhiteSpace(entry.DisplayCommander));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NormalizeNestedDictionary(
        IReadOnlyDictionary<string, Dictionary<string, string>>? values)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, nested) in values)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var normalized = NormalizeStringDictionary(nested);
            if (normalized.Count > 0) result[key.Trim()] = normalized;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, UpstreamGameNoteDefinition> NormalizeGameNotes(
        IReadOnlyDictionary<string, UpstreamManifestGameNote>? values)
    {
        var result = new Dictionary<string, UpstreamGameNoteDefinition>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (key, note) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || note is null) continue;
            result[key.Trim()] = new(NullIfWhiteSpace(note.Notes), NullIfWhiteSpace(note.Url), NullIfWhiteSpace(note.UrlLabel));
        }
        return result;
    }
}
