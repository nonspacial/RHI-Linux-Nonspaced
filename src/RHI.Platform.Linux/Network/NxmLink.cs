using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// A Nexus Mods "Mod Manager Download" link: <c>nxm://&lt;game&gt;/mods/&lt;mod&gt;/files/&lt;file&gt;?key=…&amp;expires=…&amp;user_id=…</c>
/// (upstream <c>NxmProtocolHandler</c>). The nxm handler is a trust boundary — any web page can hand the desktop such a
/// link — so <see cref="TryParse"/> is strict: nxm scheme, a game domain of lower-case letters/digits/dashes, no user
/// info or port, exactly <c>/mods/&lt;n&gt;/files/&lt;n&gt;</c>, only the three known query keys, each in its own character set,
/// and 2 KiB at most. Nothing is ever installed from a link without the user confirming it.
/// </summary>
public sealed record NxmLink(string GameDomain, int ModId, int FileId, string? Key, string? Expires, string? UserId)
{
    public const int MaxLength = 2048;

    private static readonly Regex Domain = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyValue = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

    /// <summary>The mod's page on nexusmods.com (shown in the confirmation).</summary>
    public string ModPageUrl => $"https://www.nexusmods.com/{GameDomain}/mods/{ModId}";

    public static NxmLink? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength || raw.Any(char.IsControl)) return null;
        if (!raw.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort && uri.Port != -1 || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var domain = uri.Host.ToLowerInvariant();
        if (!Domain.IsMatch(domain)) return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase)
            || !Digits.IsMatch(segments[1]) || !int.TryParse(segments[1], out var modId) || modId <= 0
            || !Digits.IsMatch(segments[3]) || !int.TryParse(segments[3], out var fileId) || fileId <= 0)
            return null;

        string? key = null, expires = null, userId = null;
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) return null;
            var name = part[..eq];
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            switch (name)
            {
                case "key" when key is null && KeyValue.IsMatch(value): key = value; break;
                case "expires" when expires is null && Digits.IsMatch(value): expires = value; break;
                case "user_id" when userId is null && Digits.IsMatch(value): userId = value; break;
                default: return null; // an unknown, repeated or malformed parameter: not a link Nexus writes
            }
        }
        if ((key is null) != (expires is null)) return null; // a key is only usable with its expiry
        return new NxmLink(domain, modId, fileId, key, expires, userId);
    }
}

/// <summary>
/// How RHI-Linux identifies itself to Nexus Mods. The API wants an application name and version on every request.
/// SSO (the "log in with Nexus" window) needs an application slug that Nexus registers for each application; RHI-Linux
/// has none yet — upstream's slug (<c>rhi</c>) belongs to upstream RHI and is not borrowed. Until one is registered,
/// <see cref="SsoSlug"/> is null and only the API-key paste is offered. A registered slug goes into
/// <see cref="RegisteredSsoSlug"/>; for testing a registration before a build, <c>RHI_NEXUS_SSO_SLUG</c> overrides it.
/// </summary>
public static class NexusApplication
{
    public const string Name = "RHI-Linux";

    /// <summary>The slug Nexus registered for RHI-Linux (none yet: owner decision pending).</summary>
    public const string? RegisteredSsoSlug = null;

    private static readonly Regex Slug = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    public static string Version => RHI.Core.RhiProductInfo.Version;

    /// <summary>The SSO slug in use (environment override, else <see cref="RegisteredSsoSlug"/>), or null when SSO is unavailable.</summary>
    public static string? SsoSlug => SlugFrom(Environment.GetEnvironmentVariable("RHI_NEXUS_SSO_SLUG"));

    internal static string? SlugFrom(string? environmentValue)
    {
        var value = string.IsNullOrWhiteSpace(environmentValue) ? RegisteredSsoSlug : environmentValue.Trim();
        return value is not null && Slug.IsMatch(value) ? value : null;
    }
}
