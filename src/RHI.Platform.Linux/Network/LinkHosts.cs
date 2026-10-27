namespace RHI.Platform.Linux.Network;

/// <summary>
/// The sites each kind of web link RHI opens may point at (#183/#184 review): a manifest override, a community README or a
/// database field can only name a page on its kind's own sites, so a tampered list never puts an unrelated site behind
/// "Ultrawide fix" or a mod-page button. Hosts match exactly (no wildcard subdomains). Checked by
/// <see cref="GameLinkCatalogue.SafePage(string?, IReadOnlySet{string})"/> together with https, default port and no credentials.
/// </summary>
public static class LinkHosts
{
    /// <summary>PCGamingWiki pages (manifest <c>pcgwUrlOverrides</c>, <c>pcgw_data.json</c> titles, PCGW search results).</summary>
    public static readonly IReadOnlySet<string> Pcgw = Set("www.pcgamingwiki.com", "pcgamingwiki.com");

    /// <summary>Ultra+ game pages (manifest <c>ultraPlusUrlOverrides</c>, the Ultra+ games list).</summary>
    public static readonly IReadOnlySet<string> UltraPlus = Set("theultraplace.com", "www.theultraplace.com");

    /// <summary>Ultrawide fixes: Lyall on Codeberg, RoseTheFlower and p1xel8ted on GitHub, fixes published on Nexus Mods.</summary>
    public static readonly IReadOnlySet<string> UltrawideFix = Set("codeberg.org", "github.com", "www.nexusmods.com", "nexusmods.com");

    /// <summary>The HDR Gaming Database's analysis threads (GitHub discussions).</summary>
    public static readonly IReadOnlySet<string> HdrDatabase = Set("github.com");

    /// <summary>A RenoDX mod's discussion / instructions page (GitHub discussions).</summary>
    public static readonly IReadOnlySet<string> Discussion = Set("github.com");

    /// <summary>A RenoDX mod published as a page RHI cannot download from: Nexus Mods or GameBanana.</summary>
    public static readonly IReadOnlySet<string> ModPage = Set("www.nexusmods.com", "nexusmods.com", "gamebanana.com", "www.gamebanana.com");

    /// <summary>#245: a mod's own page on Nexus Mods (a Luma mod the Luma wiki lists only there).</summary>
    public static readonly IReadOnlySet<string> NexusMod = Set("www.nexusmods.com", "nexusmods.com");

    /// <summary>A RenoDX mod published only on Discord.</summary>
    public static readonly IReadOnlySet<string> Discord = Set("discord.com", "www.discord.com", "discord.gg");

    /// <summary>A component's wiki page for the game (OptiScaler's compatibility entries live in its GitHub wiki).</summary>
    public static readonly IReadOnlySet<string> Wiki = Set("github.com");

    /// <summary>Component release pages and change logs.</summary>
    public static readonly IReadOnlySet<string> Releases = Set("github.com");

    /// <summary>
    /// The link of a manifest game note (ⓘ dialog): the community sites the manifest's notes point at today (Nexus Mods,
    /// Codeberg, GitHub, Ultra+, Steam store, Discord), plus the other link kinds' sites.
    /// </summary>
    public static readonly IReadOnlySet<string> ManifestNote = Set(
        [.. Pcgw, .. UltraPlus, .. UltrawideFix, .. ModPage, .. Discord, "store.steampowered.com"]);

    private static IReadOnlySet<string> Set(params string[] hosts) => new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
}
