using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Wine prefixes that belong to a launcher with its own detector (Lutris configs, umu defaults).
/// The Wine detector must not list their contents again: the launcher already lists the game,
/// and a direct `wine` launch bypasses the runner the launcher built the prefix with.
/// </summary>
internal sealed partial class LauncherOwnedPrefixes
{
    private readonly HashSet<string> _lutrisPrefixes;
    private readonly string _umuRoot;

    private LauncherOwnedPrefixes(HashSet<string> lutrisPrefixes, string umuRoot)
    {
        _lutrisPrefixes = lutrisPrefixes;
        _umuRoot = umuRoot;
    }

    /// <param name="dataHome">
    /// <c>$XDG_DATA_HOME</c>, where Lutris keeps <c>pga.db</c>; without it a custom data home plus legacy
    /// <c>~/.config/lutris/games</c> configs leave <c>$GAMEDIR</c> prefixes unresolved (the Lutris detector
    /// passes the same value, so both agree on which prefixes Lutris owns).
    /// </param>
    public static LauncherOwnedPrefixes Load(string home, IEnumerable<string> lutrisConfigRoots, string? dataHome = null)
    {
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var roots = lutrisConfigRoots.Where(Directory.Exists).ToList();
        var library = LutrisPga.Load(LutrisPga.Candidates(roots, home, dataHome));
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.yml").Concat(Directory.EnumerateFiles(root, "*.yaml")).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RHI.Core.Services.DetectorDiagnostics.Warn($"Lutris config folder {root} unreadable; its prefixes are not known to be Lutris-owned", ex);
                continue;
            }
            foreach (var file in files)
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RHI.Core.Services.DetectorDiagnostics.Warn($"Lutris config {file} unreadable; its prefix is not known to be Lutris-owned", ex);
                    continue;
                }
                library.TryGetValue(Path.GetFileNameWithoutExtension(file), out var pga);
                var gameDir = string.IsNullOrWhiteSpace(pga?.Directory) ? null : pga!.Directory;
                foreach (var value in ReadScalars(text, "prefix"))
                {
                    var expanded = Expand(value, home, gameDir);
                    if (expanded is not null) prefixes.Add(Canonicalise(expanded));
                }
            }
        }
        // umu-launcher's default: $HOME/Games/umu/$GAMEID.
        return new LauncherOwnedPrefixes(prefixes, Canonicalise(Path.Combine(home, "Games", "umu")));
    }

    public bool Owns(string prefixPath)
    {
        var canonical = Canonicalise(prefixPath);
        return _lutrisPrefixes.Contains(canonical) ||
               canonical.StartsWith(_umuRoot + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every `key:` scalar in a Lutris YAML file (game.prefix and installer task prefixes alike), with
    /// Lutris' folded continuation lines joined back. A key with an inline value owns no children, so
    /// every following line indented deeper than the key continues the value, even one that contains
    /// `: ` (a wrapped path such as `…/Games: The Collection/x.exe`).
    /// </summary>
    internal static IEnumerable<string> ReadScalars(string text, string key)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], $@"^(?<indent>[ \t]*){Regex.Escape(key)}\s*:\s*(?<v>\S.*)$");
            if (!match.Success) continue;
            var value = match.Groups["v"].Value.Trim();
            var keyColumn = match.Groups["indent"].Value.Length;
            while (i + 1 < lines.Length &&
                   !string.IsNullOrWhiteSpace(lines[i + 1]) &&
                   lines[i + 1].Length - lines[i + 1].TrimStart().Length > keyColumn)
            {
                value += " " + lines[++i].Trim();
            }
            yield return value.Trim().Trim('"', '\'');
        }
    }

    private static string? Expand(string value, string home, string? gameDir)
    {
        var expanded = LutrisPathExpander.Expand(value, home, gameDir);
        // Unresolved placeholders such as $GAMEDIR (no pga.db directory) name no real directory.
        return expanded is not null && Path.IsPathRooted(expanded) && !expanded.Contains('$') ? expanded : null;
    }

    /// <summary>
    /// Absolute path with every symlink resolved and no trailing slash, so `~/Games/x/`,
    /// `/mnt/disk/Games/x` (via a `~/Games` link) and `x/pfx` (Proton's `pfx -> .`) compare equal.
    /// Components that do not exist are kept as written.
    /// </summary>
    internal static string Canonicalise(string path)
    {
        var current = Path.GetFullPath(path);
        for (var hop = 0; hop < 40; hop++)
        {
            var next = ResolveFirstLink(current);
            if (next is null) break;
            current = next;
        }
        return current.Length > 1 ? current.TrimEnd('/') : current;
    }

    private static string? ResolveFirstLink(string fullPath)
    {
        var parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var built = "/";
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = Path.Combine(built, parts[i]);
            string? target;
            try { target = new FileInfo(candidate).LinkTarget; } catch { target = null; }
            if (target is not null)
            {
                var resolved = Path.IsPathRooted(target) ? target : Path.Combine(built, target);
                return Path.GetFullPath(Path.Combine([resolved, .. parts[(i + 1)..]]));
            }
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return null;
            built = candidate;
        }
        return null;
    }
}
