using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A recommended OptiScaler fork (#271, upstream 2.9.2/2.9.3 manifest <c>osForks</c>).</summary>
/// <param name="Id">The manifest id (<c>venom</c>).</param>
/// <param name="Repository">The GitHub repository its releases come from; nothing else is ever downloaded for it.</param>
/// <param name="NeedsNrDll">Neural Rendering fork: NVIDIA's <c>nvngx_dlssnr.dll</c> goes beside it (one owner, #157).</param>
/// <param name="AssetPattern">Manifest <c>assetPattern</c>: a regular expression the package's file name must match (upstream's
/// override of <see cref="OptiScalerForks.ScoreAsset"/>); null = the best-scored .7z/.zip.</param>
/// <param name="IncludePrereleases">Manifest <c>includePrereleases</c> (default true: most forks publish only prereleases).</param>
/// <param name="Disabled">Manifest <c>disabled</c>: hidden from the lists; games that have it keep updating and removing it.</param>
public sealed record OptiScalerFork(
    string Id,
    string Label,
    string Repository,
    bool NeedsNrDll,
    string Description,
    string? AssetPattern = null,
    bool IncludePrereleases = true,
    bool Disabled = false)
{
    public OptiScalerVariant Variant => OptiScalerVariant.Fork(Id);

    /// <summary>The asset pattern, or null when absent or not a valid expression (then the score decides, as without one).</summary>
    internal Regex? AssetRegex
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AssetPattern)) return null;
            try { return new Regex(AssetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return null; }
        }
    }
}

/// <summary>
/// #271: the recommended OptiScaler forks, each an OptiScaler version after Stable, Nightly, DLSS NR and F5 (upstream
/// <c>OsForkService</c>/<c>OsVariants</c>). The list is the manifest's <c>osForks</c> (upstream 2.9.4 format) once the
/// manifest has been read; until then, and when the manifest has none, the four forks upstream's 2.9.4 manifest lists are
/// built in. A fork the manifest drops or disables stays known to the games that have it (update and removal keep working)
/// but leaves the lists. Updated from their GitHub releases like the built-ins (newest by publish date, prereleases included
/// unless the manifest says otherwise, drafts skipped). Every fork is treated as an OptiScaler 10 build (upstream assumes so
/// until a build is staged; every current fork is): the Nightly-family ⚙ rows, plugin folder and INI template.
/// </summary>
public static partial class OptiScalerForks
{
    /// <summary>Upstream's manifest <c>osForks</c> as of RHI 2.9.4: the fallback before (or without) a manifest.</summary>
    public static IReadOnlyList<OptiScalerFork> BuiltIn { get; } =
    [
        new("venom", "Venom", "LorenzoKy/Optiscaler-Venom", false,
            "Daily OptiScaler 10 builds by LorenzoKy with extra fixes on top of the official nightly."),
        new("nr-presr-mfg", "NR PreSR MFG", "ShyVortex/OptiScaler-NR-PreSR-MFG", true,
            "ShyVortex's Neural Rendering fork with pre-upscale NR, multi frame generation and an RTX 30 DLSS-FG backend."),
        new("feeder-nr", "Feeder NR", "jlrouzies-fr/OptiScaler-DLSSNR-PreSR-Multipass", true,
            "DLSS5 Feeder author's Neural Rendering fork (wilsjo2's PreSR Multipass plus Feeder fixes). From v0.8.93 the Feeder menu (Alt+C) changes its settings live."),
        new("opennr", "OpenNR", "MatheusFerreiraS/OpenNR-OptiScaler", false,
            "Neural Rendering fork by MatheusFerreiraS that ships its own NR runtime."),
    ];

    private static readonly OptiScalerForkRegistry Default = new();
    private static readonly AsyncLocal<OptiScalerForkRegistry?> FlowRegistry = new();

    /// <summary>The process's fork list (a test's own registry in its async flow).</summary>
    public static OptiScalerForkRegistry Registry => FlowRegistry.Value ?? Default;

    /// <summary>Raised after the manifest changed the process's list (version dropdowns rebuild).</summary>
    public static event Action? Changed
    {
        add => Default.Changed += value;
        remove => Default.Changed -= value;
    }

    /// <summary>The forks to offer, by label (upstream's order): the manifest's (not disabled), else the built-in four.</summary>
    public static IReadOnlyList<OptiScalerFork> All => Registry.All;

    /// <summary>Takes the manifest's <c>osForks</c> (<see cref="Parse"/>d); null or empty keeps what is known.</summary>
    public static void SetFromManifest(IReadOnlyList<OptiScalerFork>? forks) => Registry.SetFromManifest(forks);

    /// <summary>
    /// Upstream's manifest checks (<c>OsForkService.SetDefinitions</c>): an id of <c>[a-z0-9][a-z0-9-]{0,39}</c> and a
    /// GitHub <c>owner/name</c> repository; anything else is skipped. Manifest order is kept.
    /// </summary>
    public static IReadOnlyList<OptiScalerFork> Parse(IEnumerable<KeyValuePair<string, UpstreamManifestOsFork?>>? forks)
    {
        var result = new List<OptiScalerFork>();
        foreach (var (id, def) in forks ?? [])
        {
            if (def?.Repo is not { } repo || !ForkId().IsMatch(id ?? "") || !ForkRepository().IsMatch(repo) || repo.Split('/')[1].All(ch => ch == '.')) continue;
            result.Add(new OptiScalerFork(id!, string.IsNullOrWhiteSpace(def.Label) ? id! : def.Label.Trim(), repo, def.NeedsNrDll == true,
                def.Description?.Trim() ?? "", def.AssetPattern, def.IncludePrereleases ?? true, def.Disabled == true));
        }
        return result;
    }

    /// <summary>Test seam: <paramref name="registry"/> in this async flow (and the tasks it starts) until disposed.</summary>
    internal static IDisposable UseInThisFlow(OptiScalerForkRegistry registry)
    {
        var previous = FlowRegistry.Value;
        FlowRegistry.Value = registry;
        return new FlowRestore(previous);
    }

    private sealed class FlowRestore(OptiScalerForkRegistry? previous) : IDisposable
    {
        public void Dispose() => FlowRegistry.Value = previous;
    }

    /// <summary>The fork a version names: listed, disabled, seen earlier this session, or one of the built-in four.</summary>
    public static OptiScalerFork? Get(OptiScalerVariant variant) => Registry.Get(variant);

    public static bool IsFork(OptiScalerVariant variant) => Get(variant) is not null;

    /// <summary>An OptiScaler 10 build (an <c>OptiScaler/</c> helper folder): Nightly, DLSS NR, F5, every fork, and a custom build with that layout.</summary>
    public static bool IsOs10(OptiScalerVariant variant)
        => variant == OptiScalerVariant.Nightly || variant == OptiScalerVariant.DlssNr || variant == OptiScalerVariant.F5
           || variant.IsFork
           || (variant.IsCustom && OptiScalerCustomBuilds.Get(variant) is { IsOs10: true });

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

    /// <summary>
    /// A fork's package asset: with a manifest <c>assetPattern</c>, a .7z/.zip whose name matches it (upstream lets the
    /// pattern replace the scoring); without one, <see cref="ScoreAsset"/> above 0.
    /// </summary>
    public static bool IsPackageAsset(OptiScalerFork fork, string name)
    {
        if (fork.AssetRegex is { } pattern)
        {
            try
            {
                return pattern.IsMatch(name)
                       && (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            }
            catch (RegexMatchTimeoutException) { return false; }
        }
        return ScoreAsset(name) > 0;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ForkId();

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ForkRepository();
}

/// <summary>
/// #271: one list of recommended forks: the built-in four until a manifest lists forks, then the manifest's. Every fork it
/// has seen stays known (disabled or dropped later), so a game that has one keeps updating and removing it.
/// </summary>
public sealed class OptiScalerForkRegistry
{
    private readonly object _gate = new();
    private IReadOnlyList<OptiScalerFork>? _fromManifest;
    private readonly ConcurrentDictionary<string, OptiScalerFork> _known = new(StringComparer.Ordinal);

    public event Action? Changed;

    private IReadOnlyList<OptiScalerFork> Definitions
    {
        get { lock (_gate) return _fromManifest ?? OptiScalerForks.BuiltIn; }
    }

    public IReadOnlyList<OptiScalerFork> All
        => Definitions.Where(fork => !fork.Disabled).OrderBy(fork => fork.Label, StringComparer.OrdinalIgnoreCase).ToArray();

    public void SetFromManifest(IReadOnlyList<OptiScalerFork>? forks)
    {
        if (forks is null || forks.Count == 0) return;
        lock (_gate)
        {
            if (_fromManifest is { } current && current.SequenceEqual(forks)) return;
            _fromManifest = forks;
        }
        foreach (var fork in forks) _known[fork.Id] = fork;
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { RHI.Core.Services.AppLog.Warn("optiscaler", "OptiScaler fork list listener failed", ex); }
    }

    public OptiScalerFork? Get(OptiScalerVariant variant)
    {
        if (!variant.IsFork || variant.Key is not { } id) return null;
        return Definitions.FirstOrDefault(fork => fork.Id == id)
               ?? _known.GetValueOrDefault(id)
               ?? OptiScalerForks.BuiltIn.FirstOrDefault(fork => fork.Id == id);
    }
}
