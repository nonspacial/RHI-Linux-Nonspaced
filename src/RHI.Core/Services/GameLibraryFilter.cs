using RHI.Core.Models;

namespace RHI.Core.Services;

public enum GameLibraryFilterMode
{
    AllGames,
    Favourites,
    Installed,
    Unreal,
    Unity,
    Other,
    RenoDX,
    Luma,
    Hidden
}

public readonly record struct GameLibraryFilterFacts(bool Installed, bool RenoDx, bool Luma);

/// <summary>Pure filtering/search contract shared by the Linux UI and regression tests.</summary>
public static class GameLibraryFilter
{
    /// <param name="customQuery">
    /// The active saved custom filter's search (Refs #193, upstream <c>CustomFilter</c>): a second search pass on top of
    /// the search box and the filter tab, independent of both. Null or blank = none.
    /// </param>
    public static bool Matches(
        DetectedGame game,
        GameLibraryFilterMode mode,
        string? search,
        GameLibraryFilterFacts facts,
        string? customQuery = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!MatchesSearch(game, search)) return false;
        if (!MatchesSearch(game, customQuery)) return false;

        if (mode == GameLibraryFilterMode.Hidden) return game.IsHidden;
        if (mode == GameLibraryFilterMode.Favourites) return game.IsFavourite; // upstream intentionally includes hidden favourites
        if (game.IsHidden) return false;

        return mode switch
        {
            GameLibraryFilterMode.AllGames => true,
            GameLibraryFilterMode.Installed => facts.Installed,
            GameLibraryFilterMode.Unreal => IsUnreal(game),
            GameLibraryFilterMode.Unity => IsUnity(game),
            GameLibraryFilterMode.Other => !IsUnreal(game) && !IsUnity(game),
            GameLibraryFilterMode.RenoDX => facts.RenoDx,
            GameLibraryFilterMode.Luma => facts.Luma,
            _ => true
        };
    }

    public static bool MatchesSearch(DetectedGame game, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var q = search.Trim();
        bool Has(string? value) => !string.IsNullOrWhiteSpace(value) && value.Contains(q, StringComparison.OrdinalIgnoreCase);

        // The Edit game display name and the detected name both find the card (Refs #190).
        return Has(game.DisplayName())
            || Has(game.Name)
            || Has(game.Source)
            || Has(game.Runner)
            || Has(game.CompatibilityTool)
            || Has(game.CompatibilityLayerType)
            || Has(game.EngineHint)
            || Has(game.CanonicalManifestName)
            || Has(game.AuthorOverride)
            || Has(game.WikiStatusOverride)
            || Has(game.ManifestGameNote)
            || Has(game.InstallPath)
            || Has(game.ExecutablePath)
            || Has(game.PrefixPath)
            || game.EffectiveGraphicsApis.Any(api => Has(FormatApi(api)))
            || (DeclaredArchitecture(game) is PeArchitecture.X86 && Has("32-bit"))
            || (DeclaredArchitecture(game) is PeArchitecture.X64 && Has("64-bit"))
            || (game.NativeHdrByManifest && Has("HDR"))
            || (game.IsManuallyAdded && Has("Manual"));
    }

    // Search reads only declared bitness (Edit game, then the manifest), never the exe header: a keystroke must not read files.
    private static PeArchitecture? DeclaredArchitecture(DetectedGame game)
        => game.UserArchitectureOverride is PeArchitecture.X86 or PeArchitecture.X64 ? game.UserArchitectureOverride : game.ForcedArchitecture;

    private static bool IsUnreal(DetectedGame game)
        => game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsUnity(DetectedGame game)
        => game.EngineHint?.Contains("Unity", StringComparison.OrdinalIgnoreCase) == true;

    private static string FormatApi(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX8 => "DirectX 8 DX8",
        GraphicsApi.DirectX9 => "DirectX 9 DX9",
        GraphicsApi.DirectX10 => "DirectX 10 DX10",
        GraphicsApi.DirectX11 => "DirectX 11 DX11",
        GraphicsApi.DirectX12 => "DirectX 12 DX12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => "Unknown"
    };
}
