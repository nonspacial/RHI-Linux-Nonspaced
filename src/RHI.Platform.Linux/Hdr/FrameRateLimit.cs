using RHI.Core.Models;
using RHI.Platform.Linux.Launch;

namespace RHI.Platform.Linux.Hdr;

/// <summary>Why the FPS limit applies to a game (or not). Codes only; the desktop words them.</summary>
public enum FrameRateLimitReason
{
    /// <summary>The limit applies: RHI sets it at launch, or the launcher's launch options take it.</summary>
    Ready,
    NoGame,
    /// <summary>A native Linux game: no DXVK or VKD3D-Proton to read the variables.</summary>
    NativeLinux,
    /// <summary>A store client or the Xbox app starts the game and takes no launch variables.</summary>
    LauncherTakesNoVariables
}

/// <summary>
/// #310: the FPS limit part of a launch plan. <see cref="Environment"/> holds the variables RHI adds (empty for none);
/// <see cref="Limit"/> is the cap in effect for this launch (null = none).
/// </summary>
public sealed record FrameRateLimitPlan(FrameRateLimitReason Reason, uint? Limit, IReadOnlyDictionary<string, string> Environment)
{
    public static FrameRateLimitPlan Hidden(FrameRateLimitReason reason)
        => new(reason, null, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The per-game FPS Limit row applies to the game.</summary>
    public bool Shown => Reason == FrameRateLimitReason.Ready;

    public bool HasVariables => Environment.Count > 0;
}

/// <summary>
/// #310 (upstream 2.8.9 per-game FPS limit in Driver Settings; owner decision 2026-11-16 for its Linux equivalent): the
/// NVIDIA driver profile's frame-rate limiter does not exist under Wine/Proton, so RHI hands the cap to the translation
/// layers that render the game: <c>DXVK_FRAME_RATE</c> (Direct3D 8–11 through DXVK) and <c>VKD3D_FRAME_RATE</c>
/// (Direct3D 12 through VKD3D-Proton), set in the game's launch variables like the NVIDIA settings (#211). Both are
/// written whatever the game's API (each layer reads only its own). Nothing is written to disk.
/// </summary>
public static class FrameRateLimit
{
    public const string DxvkVariable = "DXVK_FRAME_RATE";
    public const string Vkd3dVariable = "VKD3D_FRAME_RATE";

    /// <summary>Upstream's custom bounds ("Custom…" takes 20–1000).</summary>
    public const uint Min = 20;
    public const uint Max = 1000;

    /// <summary>Per-game "Off": no cap for this game, even with a global one.</summary>
    public const uint Off = 0;

    /// <summary>Upstream's presets (<c>DlssPresetService.FpsLimiterPresets</c>): VRR caps a few frames under each refresh rate.</summary>
    public static readonly IReadOnlyList<(uint Fps, uint RefreshHz)> Presets =
    [
        (59, 60), (73, 75), (97, 100), (116, 120), (138, 144), (157, 165), (171, 180), (189, 200), (224, 240), (258, 280),
        (275, 300), (324, 360), (416, 480)
    ];

    /// <summary>A stored cap the layers take (20–1000), else null.</summary>
    public static uint? Valid(uint? fps) => fps is >= Min and <= Max ? fps : null;

    /// <summary>
    /// The cap for one launch. <paramref name="perGame"/>: null = Global (follows <paramref name="global"/>), 0 = Off, else
    /// the game's own cap. Upstream's rule: a game with ReLimiter or Display Commander gets the driver limit Off when a
    /// global cap is set (both limiters at once can stutter), so Global gives no cap there; a cap chosen for the game itself
    /// still applies, as upstream lets the user set one after the install.
    /// </summary>
    public static uint? Effective(uint? perGame, uint global, bool frameLimiterInstalled)
        => perGame switch
        {
            null => frameLimiterInstalled ? null : Valid(global),
            Off => null,
            _ => Valid(perGame)
        };

    /// <summary>The launch variables for <paramref name="limit"/> (none for null).</summary>
    public static IReadOnlyDictionary<string, string> Environment(uint? limit)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Valid(limit) is { } fps)
        {
            var value = fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            env[DxvkVariable] = value;
            env[Vkd3dVariable] = value;
        }
        return env;
    }

    /// <summary>The plan for <paramref name="game"/> with <paramref name="limit"/> in effect.</summary>
    public static FrameRateLimitPlan Plan(DetectedGame game, uint? limit)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return FrameRateLimitPlan.Hidden(FrameRateLimitReason.NativeLinux);
        if (LaunchHandoffClassifier.Classify(game) is LaunchHandoff.StoreClient or LaunchHandoff.Xbox)
            return FrameRateLimitPlan.Hidden(FrameRateLimitReason.LauncherTakesNoVariables);
        return new FrameRateLimitPlan(FrameRateLimitReason.Ready, Valid(limit), Environment(limit));
    }
}
