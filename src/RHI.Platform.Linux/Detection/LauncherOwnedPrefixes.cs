using System.Text.RegularExpressions;
using RHI.Core.Services;

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
    private readonly IReadOnlyList<string> _partingXEnvironments;

    private LauncherOwnedPrefixes(HashSet<string> lutrisPrefixes, string umuRoot, IReadOnlyList<string> partingXEnvironments)
    {
        _lutrisPrefixes = lutrisPrefixes;
        _umuRoot = umuRoot;
        _partingXEnvironments = partingXEnvironments;
    }

    /// <param name="dataHome">
    /// <c>$XDG_DATA_HOME</c>, where Lutris keeps <c>pga.db</c>; without it a custom data home plus legacy
    /// <c>~/.config/lutris/games</c> configs leave <c>$GAMEDIR</c> prefixes unresolved (the Lutris detector
    /// passes the same value, so both agree on which prefixes Lutris owns).
    /// </param>
    /// <param name="partingXEnvironments">
    /// Environment roots of the PartingX instances RHI knows (<see cref="Xbox.PartingXInstance.KnownEnvironmentRoots"/>):
    /// every prefix under one is PartingX's, whatever that folder holds yet (Refs #90).
    /// </param>
    public static LauncherOwnedPrefixes Load(string home, IEnumerable<string> lutrisConfigRoots, string? dataHome = null,
        IReadOnlyList<string>? partingXEnvironments = null)
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
                    if (expanded is not null) prefixes.Add(PathIdentity.Canonicalize(expanded));
                }
            }
        }
        // umu-launcher's default: $HOME/Games/umu/$GAMEID.
        return new LauncherOwnedPrefixes(prefixes, PathIdentity.Canonicalize(Path.Combine(home, "Games", "umu")), partingXEnvironments ?? []);
    }

    public bool Owns(string prefixPath)
    {
        var canonical = PathIdentity.Canonicalize(prefixPath);
        return _lutrisPrefixes.Contains(canonical) ||
               canonical.StartsWith(_umuRoot + "/", StringComparison.Ordinal) ||
               _partingXEnvironments.Any(root => canonical.StartsWith(root + "/", StringComparison.Ordinal)) ||
               InPartingXEnvironment(canonical);
    }

    /// <summary>
    /// A title prefix (<c>&lt;env&gt;/default/&lt;id&gt;/prefix</c>) or runtime template under a PartingX environment folder RHI
    /// knows by its layout alone (Refs #90): PartingX lists, prepares and launches those itself; the Xbox detector lists
    /// their games. Instances RHI knows of are matched by their environment root instead (no layout needed).
    /// </summary>
    private static bool InPartingXEnvironment(string prefix)
    {
        var current = Path.GetDirectoryName(prefix);
        for (var level = 0; current is not null && level < 4; level++, current = Path.GetDirectoryName(current))
            if (Xbox.PartingXInstance.IsEnvironmentRoot(current)) return true;
        return false;
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
}
