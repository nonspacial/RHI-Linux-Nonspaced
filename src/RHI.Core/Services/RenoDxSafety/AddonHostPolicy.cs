namespace RHI.Core.Services.RenoDxSafety;

/// <summary>
/// Raised when a download link is not HTTPS on one of the policy's hosts. Nothing has been fetched
/// when this is thrown; the message ends with the refused link so PT-BR prefix pairs can translate it.
/// </summary>
public sealed class UntrustedDownloadException : Exception
{
    public UntrustedDownloadException(string? url, AddonHostPolicy policy)
        : base($"{policy.RefusalPrefix}{url}")
    {
        Url = url;
        PolicyName = policy.Name;
    }

    public string? Url { get; }
    public string PolicyName { get; }
}

/// <summary>
/// HTTPS + host allow-list for executable payloads (DLLs, ReShade add-ons) that RHI writes into a game folder.
/// Rules: absolute URI, scheme https, default port, no user-info, host equal to an allowed host or exactly one
/// DNS label under an allowed parent domain (e.g. <c>marat569.github.io</c>, never <c>a.b.github.io</c>).
/// Callers validate the requested link before any HTTP call and the final (post-redirect) URI before reading
/// the body.
/// Lane-local (w1/renodx-p0): the integrator folds this into <c>RHI.Core.Deployment.TrustedDownloadPolicy</c>
/// (single-label subdomain parents, port/user-info/DNS checks, name and refusal prefix).
/// </summary>
public sealed class AddonHostPolicy
{
    private readonly HashSet<string> _hosts;
    private readonly string[] _parentDomains;

    public AddonHostPolicy(string name, string hostDescription, IEnumerable<string> hosts, IEnumerable<string>? singleLabelSubdomainsOf = null)
    {
        Name = name;
        RefusalPrefix = $"Download refused: {name} links must be HTTPS on a trusted host ({hostDescription}). Refused link: ";
        _hosts = new HashSet<string>(hosts.Select(h => h.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        _parentDomains = (singleLabelSubdomainsOf ?? []).Select(d => d.Trim().TrimStart('.').ToLowerInvariant()).ToArray();
    }

    public string Name { get; }

    /// <summary>Fixed text before the refused link in <see cref="UntrustedDownloadException"/> (localisation key).</summary>
    public string RefusalPrefix { get; }
    public IReadOnlyCollection<string> Hosts => _hosts;
    public IReadOnlyList<string> SubdomainParents => _parentDomains;

    /// <summary>GitHub repositories, release assets and raw files (the hosts a release download redirects through).</summary>
    public static readonly AddonHostPolicy GitHub = new(
        "GitHub",
        "GitHub",
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "raw.githubusercontent.com"]);

    /// <summary>
    /// RenoDX add-ons. Derived from upstream RHI 2.8.5 data (docs/RenoDXdb.json, manifest.json snapshotOverrides,
    /// ModInstallService fallbacks) and the live RenoDX wiki/rhi-repo database: every add-on link is HTTPS on
    /// github.com (release downloads redirect to release-assets/objects.githubusercontent.com), on a mod
    /// author's GitHub Pages site (<c>&lt;author&gt;.github.io</c>: clshortfuse, marat569, oopydoopy, notvoosh, …;
    /// new authors appear in the live database) or on renodx.com, where clshortfuse.github.io redirects.
    /// </summary>
    public static readonly AddonHostPolicy RenoDxAddons = new(
        "RenoDX add-on",
        "GitHub, GitHub Pages or renodx.com",
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "raw.githubusercontent.com", "renodx.com"],
        ["github.io"]);

    /// <summary>ReShade add-ons from crosire's Addons.ini and the upstream manifest: GitHub and GitHub Pages.</summary>
    public static readonly AddonHostPolicy ReShadeAddons = new(
        "ReShade add-on",
        "GitHub or GitHub Pages",
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "raw.githubusercontent.com"],
        ["github.io"]);

    public bool IsTrusted(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsTrusted(uri);

    public bool IsTrusted(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (_hosts.Contains(host)) return true;
        foreach (var parent in _parentDomains)
        {
            if (!host.EndsWith("." + parent, StringComparison.Ordinal)) continue;
            var label = host[..^(parent.Length + 1)];
            if (label.Length > 0 && !label.Contains('.')) return true;
        }
        return false;
    }

    /// <summary>Throws <see cref="UntrustedDownloadException"/> unless <paramref name="url"/> is trusted.</summary>
    public Uri Validate(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsTrusted(uri)) return uri;
        throw new UntrustedDownloadException(url, this);
    }

    /// <summary>Checks the URI a response actually came from (after redirects); null means no redirect information.</summary>
    public void ValidateFinal(HttpResponseMessage response, string requestedUrl)
    {
        var final = response.RequestMessage?.RequestUri;
        if (final is null) { Validate(requestedUrl); return; }
        if (!IsTrusted(final)) throw new UntrustedDownloadException(final.AbsoluteUri, this);
    }
}
