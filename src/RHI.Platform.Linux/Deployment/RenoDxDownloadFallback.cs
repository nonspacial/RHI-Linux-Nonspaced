namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// The links a RenoDX add-on download tries, in order, when the one before it cannot be reached (upstream
/// <c>ModInstallService.InstallAsync</c> candidates, 2.9.5 beta). The first is always the catalogue's link; every
/// alternative still has to pass the RenoDX download policy, so an untrusted one is never fetched.
/// </summary>
public static class RenoDxDownloadFallback
{
    public static IReadOnlyList<string> Candidates(string url)
    {
        var candidates = new List<string> { url };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return candidates;
        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrEmpty(fileName)) return candidates;

        // #268: Voosh's new GitHub Pages location, his old release, his old Pages repository, ShortFuse's Pages.
        if (fileName.Equals(GenericUnityAddonUrls.File64, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(GenericUnityAddonUrls.File32, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add($"https://notvoosh.github.io/renodx/{fileName}");
            candidates.Add($"https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/{fileName}");
            candidates.Add($"https://notvoosh.github.io/renodx-unity/{fileName}");
            candidates.Add($"https://clshortfuse.github.io/renodx/{fileName}");
        }

        // The generic Unreal add-on: ShortFuse's GitHub release, then his Pages.
        if (fileName.Equals("renodx-unrealengine.addon64", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add($"https://github.com/clshortfuse/renodx/releases/download/snapshot/{fileName}");
            candidates.Add($"https://clshortfuse.github.io/renodx/{fileName}");
        }

        // #287 (upstream 2.8.9 ef62d59): an add-on on a modder's own site (<owner>.github.io/<repo>/<file>, such as Marat's
        // UE-Extended) falls back to the same file in that modder's GitHub snapshot release, for networks that cannot reach
        // github.io; then to ShortFuse's release.
        if (uri.Host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase))
        {
            var owner = uri.Host[..^".github.io".Length];
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (owner.Length > 0 && !owner.Contains('.') && segments.Length == 2)
                candidates.Add($"https://github.com/{owner}/{segments[0]}/releases/download/snapshot/{fileName}");
            candidates.Add($"https://github.com/clshortfuse/renodx/releases/download/snapshot/{fileName}");
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
