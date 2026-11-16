namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #271: the OptiScaler versions RHI can install, in one list (upstream 2.9.2 <c>OsVariants</c>): the four built-ins, the
/// recommended forks (<see cref="OptiScalerForks"/>, updated from GitHub) and the user's custom builds
/// (<see cref="OptiScalerCustomBuilds"/>, never updated). Every dropdown, label and "what kind of build is this" question
/// goes through here, so a custom build behaves like a built-in one everywhere.
/// </summary>
public static class OptiScalerVersions
{
    /// <summary>Built-ins, then forks (by label), then custom builds (by name): upstream's dropdown order.</summary>
    public static IReadOnlyList<OptiScalerVariant> All()
        => [.. OptiScalerVariant.BuiltIns, .. OptiScalerForks.All.Select(fork => fork.Variant), .. OptiScalerCustomBuilds.All.Select(build => build.Variant)];

    /// <summary>A built-in, a fork RHI knows (listed, disabled or built in) or a custom build that still exists.</summary>
    public static bool IsKnown(OptiScalerVariant variant)
        => variant.IsBuiltIn || OptiScalerForks.Get(variant) is not null || OptiScalerCustomBuilds.Get(variant) is not null;

    /// <summary>The English label ("F5 DLSSNR", "Venom", "Custom: My build"); a removed custom build reads "Custom build (removed)".</summary>
    public static string Label(OptiScalerVariant variant)
    {
        if (variant == OptiScalerVariant.Stable) return "Stable";
        if (variant == OptiScalerVariant.Nightly) return "Nightly";
        if (variant == OptiScalerVariant.DlssNr) return "DLSS NR";
        if (variant == OptiScalerVariant.F5) return "F5 DLSSNR";
        if (variant.IsFork) return OptiScalerForks.Get(variant)?.Label ?? RemovedForkLabel;
        if (variant.IsCustom) return OptiScalerCustomBuilds.Get(variant)?.Label ?? RemovedCustomLabel;
        return variant.Id;
    }

    public const string RemovedCustomLabel = "Custom build (removed)";
    public const string RemovedForkLabel = "OptiScaler fork (removed)";

    /// <summary>Builds that need NVIDIA's nvngx_dlssnr.dll beside them: DLSS NR, F5, an NR fork, or a custom build ticked so.</summary>
    public static bool NeedsNrDll(OptiScalerVariant variant)
        => variant == OptiScalerVariant.DlssNr || variant == OptiScalerVariant.F5
           || OptiScalerForks.Get(variant)?.NeedsNrDll == true
           || OptiScalerCustomBuilds.Get(variant)?.NeedsNrDll == true;

    /// <summary>An OptiScaler 10 build (an <c>OptiScaler/</c> helper folder): the advanced ⚙ rows, plugin folder and Nightly template.</summary>
    public static bool IsOs10(OptiScalerVariant variant) => OptiScalerForks.IsOs10(variant);

    /// <summary>Versions updated from their GitHub releases (every built-in and fork); custom builds never are (upstream).</summary>
    public static bool IsUpdatable(OptiScalerVariant variant) => !variant.IsCustom;
}
