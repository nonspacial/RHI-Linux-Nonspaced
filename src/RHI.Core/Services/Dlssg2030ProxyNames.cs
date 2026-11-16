namespace RHI.Core.Services;

/// <summary>
/// The proxy DLL names of the DLSSG 20/30 FG Unlock (sdli1995/dlssg_for_sm86, #261), here so the component probe and the
/// deployment service share one list.
/// </summary>
public static class Dlssg2030ProxyNames
{
    /// <summary>
    /// The names RHI offers, in the order the mod's <c>alternatives/README.md</c> says to try them: version.dll first, then
    /// winmm, dbghelp, dinput8, and dxgi or d3d12 (render-path proxies, last resort). Each is its own build.
    /// </summary>
    public static readonly IReadOnlyList<string> Offered =
        ["version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll"];

    /// <summary>No longer offered since #261; older installs still run under it until Update moves them to version.dll.</summary>
    public const string RetiredWinhttp = "winhttp.dll";

    /// <summary>Every name an install may be found under: <see cref="Offered"/> and <see cref="RetiredWinhttp"/>.</summary>
    public static readonly IReadOnlyList<string> Detected = [.. Offered, RetiredWinhttp];
}
