using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// The proxy DLL names ReShade can be installed under (upstream <c>DllOverrideConstants.CommonDllNames</c>, the per-game
/// DLL-name dropdown since 2.7.6; #120) and how a ReShade payload is recognised under them on disk.
/// </summary>
public static class ProxyDllNames
{
    /// <summary>Upstream's list, in upstream's order.</summary>
    public static readonly IReadOnlyList<string> Offered =
    [
        "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll",
        "dinput8.dll", "version.dll", "winmm.dll", "ddraw.dll", "xinput1_3.dll", "msvcp140.dll", "bink2w64.dll",
        "ReShade64.dll", "ReShade32.dll",
    ];

    /// <summary>ShortFuse's loader name for ReShade (UAL loads it); a payload name, never offered.</summary>
    public const string ShortFuseAsiName = "ReShade64.asi";

    // The names every build before #120 checked by signature (unchanged rules for them).
    private static readonly HashSet<string> SignatureCheckedBefore120 = new(StringComparer.OrdinalIgnoreCase)
        { "dxgi.dll", "d3d9.dll", "d3d11.dll", "opengl32.dll" };

    private static readonly HashSet<string> ReShadeOwnNames = new(StringComparer.OrdinalIgnoreCase)
        { "ReShade64.dll", "ReShade32.dll", ShortFuseAsiName };

    /// <summary>The offered spelling of <paramref name="name"/> (case-insensitive), or null when it is not offered.</summary>
    /// <remarks>Only bare offered file names pass, so a stored or remote value can never name another folder.</remarks>
    public static string? Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return Offered.FirstOrDefault(n => n.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The offered names that make sense for a game of this bitness: ReShade64.dll and bink2w64.dll are 64-bit names,
    /// ReShade32.dll is 32-bit (upstream offers both to every game).
    /// </summary>
    public static IReadOnlyList<string> OfferedFor(bool is32Bit)
        => Offered.Where(n => is32Bit
                ? !n.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) && !n.Equals("bink2w64.dll", StringComparison.OrdinalIgnoreCase)
                : !n.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// False when <paramref name="name"/> is the proxy of another graphics API than <paramref name="api"/> (e.g. dxgi.dll
    /// for a DirectX 9 game): the game would not load it, so ReShade would not start. Names that are not a graphics API
    /// (dinput8, version, winmm…) and ReShade's own names load with any API.
    /// </summary>
    public static bool LoadsFor(string name, GraphicsApi api)
    {
        var family = GraphicsFamily(name);
        if (family is null) return true;
        return api switch
        {
            GraphicsApi.DirectX9 => family == "d3d9",
            GraphicsApi.DirectX10 or GraphicsApi.DirectX11 => family is "dxgi" or "d3d10" or "d3d11",
            GraphicsApi.DirectX12 => family is "dxgi" or "d3d12",
            GraphicsApi.OpenGL => family == "opengl32",
            _ => false
        };
    }

    /// <summary>True for the names Wine loads from the system: they need a per-game <c>native,builtin</c> override to
    /// load from the game folder. ReShade's own names are only loaded by another mod (OptiScaler's LoadReshade).</summary>
    public static bool NeedsWineOverride(string name)
        => !name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) && !name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the file at <paramref name="path"/> is ReShade, judged as the component scan does: ReShade's own names
    /// count by name; the names checked before #120 by ReShade's signature; any other offered name only with ReShade's
    /// signature and without OptiScaler's (OptiScaler mentions ReShade and can sit under the same names).
    /// </summary>
    public static bool IsReShadeOnDisk(string path)
    {
        var name = Path.GetFileName(path);
        if (ReShadeOwnNames.Contains(name)) return true;
        return LooksLikeReShadeUnder(name, path);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is ReShade by signature under an offered name (the add-on prerequisite check):
    /// as <see cref="IsReShadeOnDisk"/>, but ReShade64/32.dll also need the signature, and ReShade64.asi is not counted.
    /// </summary>
    public static bool HasReShadeSignatureUnder(string path)
    {
        var name = Path.GetFileName(path);
        if (Canonical(name) is null) return false;
        return LooksLikeReShadeUnder(name, path);
    }

    private static bool LooksLikeReShadeUnder(string name, string path)
    {
        if (Canonical(name) is null) return false;
        if (!BinarySignatureInspector.LooksLikeReShade(path)) return false;
        return SignatureCheckedBefore120.Contains(name) || ReShadeOwnNames.Contains(name) || !BinarySignatureInspector.LooksLikeOptiScaler(path);
    }

    private static string? GraphicsFamily(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant() switch
    {
        "dxgi" => "dxgi",
        "d3d9" => "d3d9",
        "d3d10" => "d3d10",
        "d3d11" => "d3d11",
        "d3d12" => "d3d12",
        "opengl32" => "opengl32",
        "ddraw" => "ddraw",
        _ => null
    };
}
