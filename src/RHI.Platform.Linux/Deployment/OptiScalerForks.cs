namespace RHI.Platform.Linux.Deployment;

/// <summary>A recommended OptiScaler fork (#271, upstream 2.9.2/2.9.3 manifest <c>osForks</c>).</summary>
/// <param name="Id">The manifest id (<c>venom</c>).</param>
/// <param name="Repository">The GitHub repository its releases come from; nothing else is ever downloaded for it.</param>
/// <param name="NeedsNrDll">Neural Rendering fork: NVIDIA's <c>nvngx_dlssnr.dll</c> goes beside it (one owner, #157).</param>
public sealed record OptiScalerFork(OptiScalerVariant Variant, string Id, string Label, string Repository, bool NeedsNrDll, string Description);

/// <summary>
/// #271: the recommended OptiScaler forks, each an OptiScaler version beside Stable, Nightly, DLSS NR and F5 (upstream
/// <c>OsForkService</c>/<c>OsVariants</c>). Updated from their GitHub releases like the built-ins (newest by publish date,
/// prereleases included, drafts skipped). Every current fork is an OptiScaler 10 build: it gets the Nightly-family ⚙
/// rows, plugin folder and INI template. Linux deviation: the list is upstream's manifest <c>osForks</c> as of RHI 2.9.4,
/// built in; a fork upstream adds to its manifest later needs an RHI Linux update.
/// </summary>
public static class OptiScalerForks
{
    public static IReadOnlyList<OptiScalerFork> All { get; } =
    [
        new(OptiScalerVariant.Venom, "venom", "Venom", "LorenzoKy/Optiscaler-Venom", false,
            "Daily OptiScaler 10 builds by LorenzoKy with extra fixes on top of the official nightly."),
        new(OptiScalerVariant.NrPreSrMfg, "nr-presr-mfg", "NR PreSR MFG", "ShyVortex/OptiScaler-NR-PreSR-MFG", true,
            "ShyVortex's Neural Rendering fork with pre-upscale NR, multi frame generation and an RTX 30 DLSS-FG backend."),
        new(OptiScalerVariant.FeederNr, "feeder-nr", "Feeder NR", "jlrouzies-fr/OptiScaler-DLSSNR-PreSR-Multipass", true,
            "DLSS5 Feeder author's Neural Rendering fork (wilsjo2's PreSR Multipass plus Feeder fixes). From v0.8.93 the Feeder menu (Alt+C) changes its settings live."),
        new(OptiScalerVariant.OpenNr, "opennr", "OpenNR", "MatheusFerreiraS/OpenNR-OptiScaler", false,
            "Neural Rendering fork by MatheusFerreiraS that ships its own NR runtime."),
    ];

    public static OptiScalerFork? Get(OptiScalerVariant variant) => All.FirstOrDefault(fork => fork.Variant == variant);

    public static bool IsFork(OptiScalerVariant variant) => Get(variant) is not null;

    /// <summary>An OptiScaler 10 build (an <c>OptiScaler/</c> helper folder): Nightly, DLSS NR, F5 and every fork.</summary>
    public static bool IsOs10(OptiScalerVariant variant)
        => variant is OptiScalerVariant.Nightly or OptiScalerVariant.DlssNr or OptiScalerVariant.F5 || IsFork(variant);

    /// <summary>
    /// How likely an asset is the fork's OptiScaler build (upstream <c>OsReleaseService.ScoreAsset</c>; 0 = not usable): a
    /// .7z or .zip, not an "Optional-…" companion, forwarder, troubleshooting note, source, symbol or checksum file; "optiscaler"
    /// in the name scores higher, and .7z wins a tie.
    /// </summary>
    public static int ScoreAsset(string name)
    {
        var n = name.ToLowerInvariant();
        if (!(n.EndsWith(".7z", StringComparison.Ordinal) || n.EndsWith(".zip", StringComparison.Ordinal))) return 0;
        if (n.StartsWith("optional", StringComparison.Ordinal) || n.Contains("forwarder", StringComparison.Ordinal) || n.Contains("troubleshoot", StringComparison.Ordinal)
            || n.Contains("source", StringComparison.Ordinal) || n.Contains("symbols", StringComparison.Ordinal) || n.Contains("pdb", StringComparison.Ordinal)) return 0;
        var score = 10;
        if (n.Contains("optiscaler", StringComparison.Ordinal)) score += 20;
        if (n.EndsWith(".7z", StringComparison.Ordinal)) score += 1;
        return score;
    }
}
