namespace RHI.Core.Services;

/// <summary>
/// #229: an NVIDIA RTX card generation, as far as Neural Rendering builds are labelled for one. Unknown = RHI cannot
/// tell (no NVIDIA driver, no model, a model it does not know, or a card from before RTX).
/// </summary>
public enum RtxGeneration
{
    Unknown = 0,
    Rtx20 = 20,
    Rtx30 = 30,
    Rtx40 = 40,
    Rtx50 = 50
}

/// <summary>
/// #229 (owner decision 2026-10-22; behavioural port of wizardkof v0.9.25.0-beta.1 <c>DlssNrGpuPackagePolicy</c>): which
/// Neural Rendering DLLs of the DLSS manifest are made for the user's card, judged by the manifest's version LABEL only
/// (<c>310.8.3 (50xx)</c>, <c>310.8.2 (20/30/40/50)</c>) — never by the DLL's CUDA fatbin list (the real 310.8.3 (50xx)
/// DLL lists sm_75/86/89/120, so a fatbin check passes the wrong file) and never by <c>nvidia-smi</c>.
/// <list type="bullet">
/// <item>An entry whose label names generations fits exactly those; one without a label RHI can read fits every card
/// (it cannot be judged, so it is never hidden).</item>
/// <item>An unknown card is offered every entry, newest first, as upstream (RHI never blocks the mod).</item>
/// <item>"Latest" for a card = the first fitting entry in manifest order (the manifest lists newest first).</item>
/// </list>
/// The one owner of this rule: every NR list and every "Latest" NR install asks it (through
/// <see cref="DlssManagerService"/> for the service paths).
/// </summary>
public static class NeuralRenderingGpuPolicy
{
    private static readonly char[] Separators = ['/', ',', '+', '&', ' ', '\t'];

    /// <summary>
    /// The generations the label of <paramref name="version"/> names, or null when it has no label RHI can read
    /// (<c>310.8.99</c>, <c>310.8.98 (beta)</c>): such an entry cannot be judged and fits every card.
    /// Accepted spellings per part: <c>50xx</c>, <c>50</c>, <c>RTX 50</c>, <c>RTX50</c>, <c>50-series</c>, <c>40x</c>.
    /// </summary>
    public static IReadOnlySet<RtxGeneration>? GenerationsIn(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var start = version.LastIndexOf('(');
        var end = start < 0 ? -1 : version.IndexOf(')', start + 1);
        if (start < 0 || end <= start) return null;
        var label = version[(start + 1)..end].Trim();
        // "RTX 50" reads as one part: the word RTX is glued to the number that follows it.
        label = System.Text.RegularExpressions.Regex.Replace(label, @"(?i)\bRTX\s*", "");
        var parts = label.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var generations = new HashSet<RtxGeneration>();
        foreach (var raw in parts)
        {
            var part = raw.Trim().ToLowerInvariant();
            if (part is "series" or "only" or "and" or "cards" or "gpus") continue; // "(RTX 40 Series)", "(50xx only)"
            foreach (var suffix in new[] { "-series", "series", "xx", "x", "s" })
                if (part.Length > suffix.Length && part.EndsWith(suffix, StringComparison.Ordinal))
                {
                    part = part[..^suffix.Length];
                    break;
                }
            RtxGeneration? generation = part switch
            {
                "20" => RtxGeneration.Rtx20,
                "30" => RtxGeneration.Rtx30,
                "40" => RtxGeneration.Rtx40,
                "50" => RtxGeneration.Rtx50,
                _ => null
            };
            // One part RHI cannot read makes the whole label unreadable: never half-judge an entry.
            if (generation is null) return null;
            generations.Add(generation.Value);
        }
        return generations.Count == 0 ? null : generations;
    }

    /// <summary>Whether <paramref name="version"/> is offered to a <paramref name="card"/> (unknown card: always).</summary>
    public static bool Fits(string? version, RtxGeneration card)
        => card == RtxGeneration.Unknown || GenerationsIn(version) is not { } generations || generations.Contains(card);

    /// <summary>
    /// True only when the card is known AND the label names other generations: the one case RHI hides an entry, skips
    /// a saved default and notes a hand-picked pin. False for an unknown card or an unlabelled entry.
    /// </summary>
    public static bool MadeForAnotherGeneration(string? version, RtxGeneration card) => !Fits(version, card);

    /// <summary>The entries offered to <paramref name="card"/>, in manifest order.</summary>
    public static IReadOnlyList<T> ForCard<T>(IEnumerable<T> entries, Func<T, string?> version, RtxGeneration card)
        => entries.Where(entry => Fits(version(entry), card)).ToArray();

    /// <summary>The version labels offered to <paramref name="card"/>, in manifest order.</summary>
    public static IReadOnlyList<string> ForCard(IEnumerable<string> versions, RtxGeneration card)
        => ForCard(versions, static version => version, card);

    /// <summary>"Latest" for <paramref name="card"/>: the first fitting entry in manifest order; null when none fits.</summary>
    public static string? LatestFor(IEnumerable<string> versions, RtxGeneration card)
        => versions.FirstOrDefault(version => Fits(version, card));

    /// <summary>The generation as users read it ("RTX 40"); empty for unknown.</summary>
    public static string Display(RtxGeneration generation)
        => generation == RtxGeneration.Unknown ? "" : $"RTX {(int)generation}";
}
