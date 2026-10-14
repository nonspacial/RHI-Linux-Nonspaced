using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>Which proxy name a ReShade install uses, and why (#120).</summary>
public enum ProxyNameSource
{
    /// <summary>The name the user chose for this game.</summary>
    User,
    /// <summary>The RHI manifest's <c>dllNameOverrides</c> name for this game (upstream behaviour).</summary>
    Manifest,
    /// <summary>The name the game's graphics API loads (dxgi.dll, d3d9.dll, opengl32.dll).</summary>
    GraphicsApi
}

/// <param name="IgnoredManifestName">A manifest name not used because the game's graphics API would not load it.</param>
public sealed record ProxyNameResolution(string Name, ProxyNameSource Source, string? IgnoredManifestName = null);

/// <summary>
/// The one place that knows where RHI's ReShade lives under a user-chosen proxy name (#120): which file of a ReShade
/// record is its payload, whether a game folder holds ReShade under any name it can be installed as, and which name the
/// next install uses. Every consumer that used to look for ReShade by a fixed name list goes through here.
/// </summary>
public static class ProxyPayloadLocator
{
    /// <summary>True for a path a ReShade record can hold as its payload: any offered name or ShortFuse's ReShade64.asi.</summary>
    /// <remarks>A ReShade record otherwise holds only D3DCompiler_47.dll and ReShade.ini, which are never offered.</remarks>
    public static bool IsReShadePayloadPath(string path)
    {
        var name = Path.GetFileName(path);
        return ProxyDllNames.Canonical(name) is not null || name.Equals(ProxyDllNames.ShortFuseAsiName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The payload file of a ReShade record, whatever name it was installed under.</summary>
    public static LinuxDeployedFile? ReShadePayload(LinuxDeploymentRecord? record)
        => record?.Files.FirstOrDefault(file => IsReShadePayloadPath(file.Path));

    /// <summary>
    /// Whether <paramref name="binaryDirectory"/> holds ReShade under any name it can be installed as (by signature; the
    /// prerequisite check of ReShade add-ons). OptiScaler under a new name is not taken for ReShade.
    /// </summary>
    public static bool HasReShadeOnDisk(string binaryDirectory) => FindReShadeOnDisk(binaryDirectory) is not null;

    /// <summary>The first file that makes <see cref="HasReShadeOnDisk"/> true (any spelling of an offered name), or null.</summary>
    /// <param name="knownNotReShade">Full paths known to be another component (RHI's OptiScaler proxy, which mentions
    /// ReShade); skipped.</param>
    public static string? FindReShadeOnDisk(string binaryDirectory, IReadOnlySet<string>? knownNotReShade = null)
    {
        foreach (var name in ProxyDllNames.Offered)
        {
            if (ReShadeDeploymentService.FindFileCaseInsensitive(binaryDirectory, name) is not { } path) continue;
            if (knownNotReShade?.Contains(Path.GetFullPath(path)) == true) continue;
            if (ProxyDllNames.HasReShadeSignatureUnder(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// The name a new ReShade install uses: the user's choice, else the manifest's name (as upstream), else the graphics
    /// API's proxy. A manifest name the game's API would not load (dxgi.dll for a DirectX 9 game: upstream sets it for
    /// titles that use dgVoodoo2, which RHI Linux moves ReShade for only when that translation is installed) is ignored.
    /// Null when the API has no ReShade proxy on Linux.
    /// </summary>
    public static ProxyNameResolution? Resolve(DetectedGame game, string? userChoice)
    {
        var api = game.EffectiveGraphicsApi;
        var apiName = ReShadeDeploymentService.SelectProxyName(api);
        if (apiName is null) return null;
        if (ProxyDllNames.Canonical(userChoice) is { } user) return new(user, ProxyNameSource.User);
        if (ProxyDllNames.Canonical(game.ReShadeDllNameOverride) is { } manifest)
            return ProxyDllNames.LoadsFor(manifest, api)
                ? new(manifest, ProxyNameSource.Manifest)
                : new(apiName, ProxyNameSource.GraphicsApi, manifest);
        return new(apiName, ProxyNameSource.GraphicsApi);
    }
}
