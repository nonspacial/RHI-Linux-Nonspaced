using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What a Feeder install changed in a preset that was already there, so its removal can take exactly that out again.</summary>
/// <param name="AddedTechniques">Techniques entries the install added (not there before).</param>
/// <param name="AddedSorting">TechniqueSorting entries the install added (not there before).</param>
/// <param name="RemovedFlows">LumeniteFX flow effects switched off because the built-in motion vectors replace them.</param>
/// <param name="PreviousDefinitions"><c>[DLSS5_Feed.fx] PreprocessorDefinitions</c> before the install; null = no such key.</param>
/// <param name="SetDefinitions">The value the install wrote there.</param>
/// <param name="SectionCreated">The install added the <c>[DLSS5_Feed.fx]</c> section.</param>
/// <param name="TechniquesKeyCreated">The install added the Techniques line.</param>
/// <param name="SortingKeyCreated">The install added the TechniqueSorting line.</param>
public sealed record FeederPresetChange(
    IReadOnlyList<string> AddedTechniques,
    IReadOnlyList<string> AddedSorting,
    IReadOnlyList<string> RemovedFlows,
    string? PreviousDefinitions,
    string SetDefinitions,
    bool SectionCreated,
    bool TechniquesKeyCreated,
    bool SortingKeyCreated);

/// <summary>
/// w14/nr (#275): the ReShade preset settings DLSS5 Feeder 2.0 needs, as Feeder's own installer (<c>Install-DLSS5Feeder.ps1</c>,
/// v2.0.0) and upstream RHI 2.9.3 (<c>FeederPreset.cs</c>, <c>cdff33f</c>) do them. Pure text edits.
/// <list type="bullet">
/// <item>The motion-vector provider is the per-effect preprocessor definition <c>DLSS5_MV_PROVIDER</c> in the preset's
/// <c>[DLSS5_Feed.fx]</c> section (ReShade.ini's global list is ignored for it).</item>
/// <item>A new install uses 5, the estimator built into <c>DLSS5_Feed.fx</c> since 2.0.0 (nothing else to download), or 3
/// (LumeniteFX Kernel) with an older shader. A preset that already names a provider keeps it.</item>
/// <item>An existing preset is merged into (only Techniques / TechniqueSorting and that one definition change), never replaced.
/// Linux addition: <see cref="Unmerge"/> takes the install's changes out again on removal, where upstream leaves them.</item>
/// </list>
/// </summary>
public static class FeederPreset
{
    public const string FeedTechnique = "DLSS5_Feed@DLSS5_Feed.fx";
    public const string DebugTechnique = "DLSS5_Feed_Debug@DLSS5_Feed.fx";
    public const string EffectSection = "DLSS5_Feed.fx";
    public const string DefinitionsKey = "PreprocessorDefinitions";
    public const int BuiltInProvider = 5;
    public const int LumeniteProvider = 3;

    private static readonly Regex ProviderDefinition = new(@"(?i)(^|,)\s*DLSS5_MV_PROVIDER\s*=\s*([0-5])\s*(,|$)", RegexOptions.CultureInvariant);
    private static readonly Regex ProviderPart = new(@"(?i)^DLSS5_MV_PROVIDER\s*=", RegexOptions.CultureInvariant);
    private static readonly Regex LumeniteFlow = new(@"(?i)@lumenite_(Kernel|QuantMotion)\.fx$", RegexOptions.CultureInvariant);

    /// <summary>The ReShade technique a provider needs enabled before DLSS5_Feed, or null (built in, or one RHI does not install).</summary>
    public static string? ProviderTechnique(int provider) => provider switch
    {
        3 => "Lumenite_Kernel@lumenite_Kernel.fx",
        4 => "Lumenite_QuantMotion@lumenite_QuantMotion.fx",
        _ => null,
    };

    /// <summary>True for providers that need the LumeniteFX shader pack.</summary>
    public static bool NeedsLumenite(int provider) => provider is 3 or 4;

    /// <summary>True when the DLSS5_Feed.fx text has the built-in estimator (2.0.0 and newer), as Feeder's installer checks.</summary>
    public static bool HasBuiltInEstimator(string? fxText)
        => fxText is not null && Regex.IsMatch(fxText, @"float4\s+BaseLevel\s*\(", RegexOptions.CultureInvariant);

    /// <summary>The provider a preset names in [DLSS5_Feed.fx], or null.</summary>
    public static int? ReadProvider(string? presetText)
    {
        var definitions = ReShadePresetText.GetKey(presetText, EffectSection, DefinitionsKey);
        if (definitions is null) return null;
        var match = ProviderDefinition.Match(definitions);
        return match.Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Keep the preset's provider; otherwise built in when the shader has it, else LumeniteFX.</summary>
    public static int ChooseProvider(string? presetText, string? fxText)
        => ReadProvider(presetText) ?? (HasBuiltInEstimator(fxText) ? BuiltInProvider : LumeniteProvider);

    /// <summary>
    /// The preset with DLSS5_Feed (and its provider's technique before it) enabled and the provider set; with no existing
    /// preset, a minimal new one. As the installer does, the feed's own entries move to the end of both lists, and with the
    /// built-in provider a LumeniteFX flow effect that was only there for the feed is switched off.
    /// </summary>
    public static string Merge(string? existing, int provider) => MergeTracked(existing, provider).Text;

    /// <summary><see cref="Merge"/>, telling what changed so <see cref="Unmerge"/> can take it out again.</summary>
    public static (string Text, FeederPresetChange Change) MergeTracked(string? existing, int provider)
    {
        var lead = ProviderTechnique(provider);
        var setDefinitions = $"DLSS5_MV_PROVIDER={provider}";
        if (string.IsNullOrWhiteSpace(existing))
        {
            var list = (lead is not null ? lead + "," : "") + FeedTechnique;
            var text = $"Techniques={list}\r\nTechniqueSorting={list},{DebugTechnique}\r\n\r\n"
                       + $"[{EffectSection}]\r\nDEBUG_VIEW=0\r\nMV_SCALE=1.000000\r\nMV_SIGN=1.000000,1.000000\r\n"
                       + $"{DefinitionsKey}={setDefinitions}\r\n";
            var all = ReShadePresetText.Split(list);
            return (text, new FeederPresetChange(all, [.. all, DebugTechnique], [], null, setDefinitions, true, true, true));
        }

        var result = existing;
        var added = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var removedFlows = new List<string>();
        var keyCreated = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in new[] { ReShadePresetText.TechniquesKey, ReShadePresetText.SortingKey })
        {
            var current = ReShadePresetText.GetKey(result, "", key);
            keyCreated[key] = current is null;
            var before = ReShadePresetText.Split(current ?? "");
            var items = before
                .Where(t => !t.Equals(lead ?? "", StringComparison.OrdinalIgnoreCase)
                            && !t.Equals(FeedTechnique, StringComparison.OrdinalIgnoreCase)
                            && !t.StartsWith("DLSS5_Feed_Debug@", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (provider == BuiltInProvider && key == ReShadePresetText.TechniquesKey)
            {
                var otherLumenite = items.Any(t => t.Contains("@lumenite_", StringComparison.OrdinalIgnoreCase) && !LumeniteFlow.IsMatch(t));
                if (!otherLumenite)
                {
                    removedFlows.AddRange(items.Where(t => LumeniteFlow.IsMatch(t)));
                    items.RemoveAll(t => LumeniteFlow.IsMatch(t));
                }
            }
            if (lead is not null) items.Add(lead);
            items.Add(FeedTechnique);
            if (key == ReShadePresetText.SortingKey) items.Add(DebugTechnique);
            added[key] = items.Where(t => !before.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            result = ReShadePresetText.SetKey(result, "", key, string.Join(",", items));
        }

        var previousDefinitions = ReShadePresetText.GetKey(result, EffectSection, DefinitionsKey);
        var sectionCreated = !HasSection(result, EffectSection);
        var parts = ReShadePresetText.Split(previousDefinitions ?? "")
            .Where(p => !ProviderPart.IsMatch(p))
            .Append(setDefinitions);
        var written = string.Join(",", parts);
        result = ReShadePresetText.SetKey(result, EffectSection, DefinitionsKey, written);
        return (result, new FeederPresetChange(
            added[ReShadePresetText.TechniquesKey], added[ReShadePresetText.SortingKey], removedFlows,
            previousDefinitions, written, sectionCreated,
            keyCreated[ReShadePresetText.TechniquesKey], keyCreated[ReShadePresetText.SortingKey]));
    }

    /// <summary>
    /// Linux addition: takes a merge back out of a preset that was there before the install. The entries the install added
    /// leave both lists (a list line it added goes when nothing else is left in it), LumeniteFX flow effects it switched off
    /// come back on, and the provider definition goes back to what it was while it still holds the value the install wrote
    /// (a value the user changed since stays). The user's other effects, values and order are not touched.
    /// </summary>
    public static string Unmerge(string text, FeederPresetChange change)
    {
        var result = text;
        foreach (var (key, tokens, created) in new[]
                 {
                     (ReShadePresetText.TechniquesKey, change.AddedTechniques, change.TechniquesKeyCreated),
                     (ReShadePresetText.SortingKey, change.AddedSorting, change.SortingKeyCreated)
                 })
        {
            var current = ReShadePresetText.GetKey(result, "", key);
            if (current is null) continue;
            var items = ReShadePresetText.Split(current);
            items.RemoveAll(t => tokens.Contains(t, StringComparer.OrdinalIgnoreCase));
            if (key == ReShadePresetText.TechniquesKey)
                foreach (var flow in change.RemovedFlows)
                    if (!items.Contains(flow, StringComparer.OrdinalIgnoreCase)) items.Add(flow);
            result = items.Count == 0 && created
                ? ReShadePresetText.RemoveKey(result, "", key)
                : ReShadePresetText.SetKey(result, "", key, string.Join(",", items));
        }

        var definitions = ReShadePresetText.GetKey(result, EffectSection, DefinitionsKey);
        if (definitions is not null && definitions.Equals(change.SetDefinitions, StringComparison.Ordinal))
            result = change.PreviousDefinitions is null
                ? ReShadePresetText.RemoveKey(result, EffectSection, DefinitionsKey, dropEmptySection: change.SectionCreated)
                : ReShadePresetText.SetKey(result, EffectSection, DefinitionsKey, change.PreviousDefinitions);
        return result;
    }

    /// <summary>ReShade.ini text with DLSS5_MV_PROVIDER removed from [GENERAL] PreprocessorDefinitions (it is ignored there).</summary>
    public static string RemoveGlobalProvider(string iniText)
    {
        var definitions = ReShadePresetText.GetKey(iniText, "GENERAL", DefinitionsKey);
        if (definitions is null || !definitions.Contains("DLSS5_MV_PROVIDER", StringComparison.OrdinalIgnoreCase)) return iniText;
        var kept = ReShadePresetText.Split(definitions).Where(p => !ProviderPart.IsMatch(p));
        return ReShadePresetText.SetKey(iniText, "GENERAL", DefinitionsKey, string.Join(",", kept));
    }

    private static bool HasSection(string text, string section)
        => text.Split('\n').Any(line => line.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
}
