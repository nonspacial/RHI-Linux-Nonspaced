using System.Text.Json;

namespace RHI.Platform.Linux.Deployment;

/// <summary>HSF's D3D12 route (upstream <c>HsfService.Route</c>): none (HSF decides), the game's own Streamline 2.x, or HSF's.</summary>
public enum HsfRoute { None, GameStreamline, BringIn }

/// <summary>One HSF release: its version (tag without "v", safe characters only) and its zip asset.</summary>
public sealed record HsfRelease(string Version, string Url, string? Digest, string AssetName);

/// <summary>
/// #277 (upstream 2.9.3/2.9.4 <c>HsfService</c>, HSF 0.9.8): the parts of HSF's package and settings that do not touch
/// a game — the release list, where each zip entry goes, the D3D12 route and the HSF.ini carried over on a new release.
/// Release zip layout (HSF-0.9.8.8.zip, pinned in the tests): <c>hsf.addon64</c>/<c>hsf.addon32</c>, <c>HSF.ini</c>,
/// <c>LICENSE.txt</c>, <c>README.txt</c>, <c>HSF_streamline/</c> (Streamline 2.x, <c>nvngx_dlssg.dll</c>,
/// <c>hsf-dlssg-helper64.exe</c>), <c>licenses/</c> and <c>reshade-shaders/Shaders/HSF.fx</c>.
/// </summary>
public static class HsfPackage
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/Capt-Hunter/HSF/releases?per_page=10";
    public const string ReleasesPage = "https://github.com/Capt-Hunter/HSF";
    public const string Addon64 = "hsf.addon64";
    public const string Addon32 = "hsf.addon32";
    public const string StreamlineFolder = "HSF_streamline";
    public const string HelperExe = "hsf-dlssg-helper64.exe";
    public const string IniName = "HSF.ini";
    public const string ShaderFx = "HSF.fx";
    public const string LicenseFile = "LICENSE.txt";
    public const string Section = "HSF";
    public const string InGameDlssgKey = "InGameDlssg";
    public const string SlBringInKey = "SlBringIn";

    /// <summary>Files HSF writes beside its add-on while it runs (removed with it, as upstream).</summary>
    public static readonly IReadOnlyList<string> HsfLogs =
        ["HSF_sl_bring.log", "HSF_sl_bring.old.log", "HSF_sl_probe.log", "HSF_sl_probe.old.log"];

    /// <summary>Staged files a release must have (relative paths, '/' separated).</summary>
    public static readonly IReadOnlyList<string> RequiredFiles =
    [
        Addon64, Addon32, IniName, ShaderFx,
        StreamlineFolder + "/sl.interposer.dll", StreamlineFolder + "/nvngx_dlssg.dll",
        StreamlineFolder + "/" + HelperExe, StreamlineFolder + "/" + LicenseFile,
    ];

    /// <summary>Keys of the old HSF.ini not carried into a new release's clean one: a launch marker and retired keys.</summary>
    private static readonly HashSet<string> IniDropKeys = new(StringComparer.OrdinalIgnoreCase) { "Dx9ExRun", "Presenter", "Generated", "Pacing" };

    public static string AddonFor(bool is32Bit) => is32Bit ? Addon32 : Addon64;

    /// <summary>The newest release with an HSF-*.zip (pre-releases included, drafts skipped), or null.</summary>
    public static HsfRelease? ParseNewestRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tag) || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.StartsWith("HSF", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;
                // Version strings become record values and cache folder names: safe characters only.
                var version = new string(tag.TrimStart('v', 'V').Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
                if (version.Length == 0) continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                return new HsfRelease(version, url, digest, name);
            }
        }
        return null;
    }

    /// <summary>
    /// Where a zip entry is staged ('/' separated), or null for anything else: the add-ons, HSF.ini and HSF.fx by name;
    /// HSF_streamline/ keeps its sub-paths (helper logs left out); LICENSE.txt, README.txt and licenses/ go into
    /// HSF_streamline/ (inside HSF's own folder they never clash with a game's LICENSE.txt and leave with it). A path that
    /// climbs out ("..") or is rooted is never staged.
    /// </summary>
    public static string? StagedPathFor(string entryFullName)
    {
        var p = entryFullName.Replace('\\', '/').TrimStart('/');
        if (p.Split('/').Any(part => part is ".." or ".") || Path.IsPathRooted(p) || p.Contains(':')) return null;
        var name = p[(p.LastIndexOf('/') + 1)..];
        if (name.Length == 0) return null;
        if (name.Equals(Addon64, StringComparison.OrdinalIgnoreCase)) return Addon64;
        if (name.Equals(Addon32, StringComparison.OrdinalIgnoreCase)) return Addon32;
        if (name.Equals(ShaderFx, StringComparison.OrdinalIgnoreCase)) return ShaderFx;
        if (name.Equals(IniName, StringComparison.OrdinalIgnoreCase) && !p.Contains('/')) return IniName;
        if (!p.Contains('/') && (name.Equals(LicenseFile, StringComparison.OrdinalIgnoreCase) || name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)))
            return StreamlineFolder + "/" + name;
        if (p.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase) && p.Length > "licenses/".Length)
            return StreamlineFolder + "/" + p;
        var at = p.IndexOf(StreamlineFolder + "/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = p[(at + StreamlineFolder.Length + 1)..];
        return rest.Length == 0 || rest.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? null : StreamlineFolder + "/" + rest;
    }

    /// <summary>
    /// The D3D12 route HSF would pick (upstream <c>PickRoute</c>): none for 32-bit or non-DX12 games (HSF's D3D12 routes
    /// are 64-bit only) and for Streamline 1.x (HSF leaves it alone); the game's own Streamline for 2.x, else HSF's own.
    /// <paramref name="hasStreamline"/> null = unknown: no guess.
    /// </summary>
    public static HsfRoute PickRoute(bool isDx12, bool is32Bit, bool? hasStreamline, string? streamlineVersion)
    {
        if (!isDx12 || is32Bit || hasStreamline is null) return HsfRoute.None;
        if (hasStreamline == false) return HsfRoute.BringIn;
        return streamlineVersion is not null && streamlineVersion.TrimStart('v', 'V').StartsWith("1.", StringComparison.Ordinal)
            ? HsfRoute.None : HsfRoute.GameStreamline;
    }

    /// <summary>
    /// Writes the route into HSF.ini's lines when neither key is set yet (value 2 = "HSF's own choice": HSF takes it back
    /// itself when the game turns out to run D3D11), so a DirectX 12 game generates from its first launch. True if written.
    /// </summary>
    public static bool PresetRoute(List<string> lines, HsfRoute route)
    {
        if (route == HsfRoute.None) return false;
        if (IniLines.Get(lines, Section, SlBringInKey) is not null || IniLines.Get(lines, Section, InGameDlssgKey) is not null) return false;
        return IniLines.Set(lines, Section, route == HsfRoute.GameStreamline ? InGameDlssgKey : SlBringInKey, "2");
    }

    /// <summary>Sets the route now (the ⚙'s choice): the chosen key = 2 and the other removed; both removed for None.</summary>
    public static bool SetRoute(List<string> lines, HsfRoute route)
    {
        var changed = IniLines.Remove(lines, Section, InGameDlssgKey);
        changed |= IniLines.Remove(lines, Section, SlBringInKey);
        if (route != HsfRoute.None) changed |= IniLines.Set(lines, Section, route == HsfRoute.GameStreamline ? InGameDlssgKey : SlBringInKey, "2");
        return changed;
    }

    /// <summary>The route HSF.ini holds: a key at 0 = switched off (null), either key on, else None (HSF decides).</summary>
    public static (HsfRoute Route, bool SwitchedOff) ReadRoute(IReadOnlyList<string> lines)
    {
        var drive = IniLines.Get(lines, Section, InGameDlssgKey);
        var bring = IniLines.Get(lines, Section, SlBringInKey);
        if (drive == "0" || bring == "0") return (HsfRoute.None, true);
        if (!string.IsNullOrEmpty(drive)) return (HsfRoute.GameStreamline, false);
        if (!string.IsNullOrEmpty(bring)) return (HsfRoute.BringIn, false);
        return (HsfRoute.None, false);
    }

    /// <summary>
    /// A new release's clean HSF.ini with every key of the old one set in it (old values win), except the dropped [HSF]
    /// keys; other sections ([LaunchPadUndo]) come across whole. CRLF line ends, as HSF writes.
    /// </summary>
    public static string MergeIni(string clean, string? old)
    {
        var lines = clean.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (!string.IsNullOrEmpty(old))
        {
            string? section = null;
            foreach (var raw in old.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                if (section is null || line.Length == 0 || line.StartsWith(';')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (section.Equals(Section, StringComparison.OrdinalIgnoreCase) && IniDropKeys.Contains(key)) continue;
                IniLines.Set(lines, section, key, line[(eq + 1)..].Trim());
            }
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// iMMERSE Launchpad's technique (upstream <c>UpliftService.LaunchpadToken</c>): it must run before HSF (and GITC
    /// Uplift), which read its motion in the same frame. w14/nr-2: the shared service's (moving it first is
    /// <see cref="ImmerseLaunchpadService.KeepFirst"/>).
    /// </summary>
    public const string LaunchpadToken = ImmerseLaunchpadService.Token;
}
