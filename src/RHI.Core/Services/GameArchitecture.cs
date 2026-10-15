using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// The one answer to "is this game 32-bit or 64-bit?" for every installer, eligibility gate and payload pick
/// (Refs #167). In order (upstream's <c>ResolveIs32Bit</c>: user override, manifest, header):
/// <list type="number">
/// <item>the bitness chosen in Edit game (<see cref="DetectedGame.UserArchitectureOverride"/>, x86/x64 only, Refs #190);</item>
/// <item>the RHI manifest's bitness override (<see cref="DetectedGame.ForcedArchitecture"/>, x86/x64 only);</item>
/// <item>the game exe's PE header;</item>
/// <item>when that header cannot be read (an Xbox / Game Pass exe is encrypted on disk), the architecture the game's
/// package declares (<c>appxmanifest.xml</c> / <c>MicrosoftGame.config</c>, <see cref="XboxPackageInfo.Architecture"/>),
/// as long as the exe itself is there.</item>
/// </list>
/// Never read the exe's header for bitness anywhere else (<c>GameArchitectureContractTests</c>): a direct read gives
/// Unknown on every encrypted exe and refuses installs the package would allow.
/// </summary>
public static class GameArchitecture
{
    public static PeArchitecture Of(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.UserArchitectureOverride is PeArchitecture.X86 or PeArchitecture.X64) return game.UserArchitectureOverride.Value;
        if (game.ForcedArchitecture is PeArchitecture.X86 or PeArchitecture.X64) return game.ForcedArchitecture.Value;
        var header = PeArchitectureDetector.Detect(game.ExecutablePath);
        if (header != PeArchitecture.Unknown) return header;
        return FromPackage(game) ?? PeArchitecture.Unknown;
    }

    /// <summary>
    /// The bitness a game would have with <paramref name="userOverride"/> as its Edit game choice (null = Automatic):
    /// Edit game asks this before saving a change, to tell whether the effective bitness moves.
    /// </summary>
    public static PeArchitecture With(DetectedGame game, PeArchitecture? userOverride)
    {
        ArgumentNullException.ThrowIfNull(game);
        var copy = game.ShallowClone();
        copy.UserArchitectureOverride = userOverride;
        return Of(copy);
    }

    private static PeArchitecture? FromPackage(DetectedGame game)
        => game.XboxPackage?.Architecture is { } declared && declared != PeArchitecture.Unknown &&
           !string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath)
            ? declared
            : null;
}
