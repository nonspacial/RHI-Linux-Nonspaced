using System.Text;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Linux launcher titles are not always upstream's canonical manifest title ("Expedition 33" vs
/// "Clair Obscur: Expedition 33"), so a manifest title may also be recovered from the game's own
/// folder names (Refs #62):
/// <list type="bullet">
/// <item>The title must equal a whole segment the game owns: the game folder (and the original root
/// before a manifest install-path override), a folder between it and the executable, or the executable
/// stem.</item>
/// <item>A parent folder (at most two levels up, never at or above <c>$HOME</c>, a Steam library or a
/// Wine <c>drive_c</c>) counts only when it contains the whole title and the card's own title is part of
/// that title, so <c>Clair Obscur. Expedition 33 (2025)/Expedition 33</c> resolves but "Ship of Harkinian"
/// in <c>~/Games/Hades/shipwright</c> does not become Hades and inherit its overrides.</item>
/// </list>
/// </summary>
internal static class GamePathIdentity
{
    private const int MaxParentLevels = 2;

    private static readonly string[] LibraryRootNames = ["common", "steamapps", "drive_c", "users", "games", "library", "libraries"];

    internal sealed record Evidence(IReadOnlySet<string> Own, IReadOnlyList<string> Parents, string Title)
    {
        public bool IsEmpty => Own.Count == 0 && Parents.Count == 0;
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var separator = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                separator = false;
            }
            else if (!separator)
            {
                builder.Append(' ');
                separator = true;
            }
        }
        return builder.ToString().Trim();
    }

    public static Evidence Of(DetectedGame game)
    {
        var own = new HashSet<string>(StringComparer.Ordinal);
        AddLast(own, game.InstallPath);
        // A split child's original root is the bundle's folder, which names the bundle, not the child.
        var originalRoot = game.IsManifestSplitChild ? null : game.ManifestInstallRoot;
        AddLast(own, originalRoot);
        AddBelow(own, game.InstallPath, game.BinaryDirectory);
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            AddBelow(own, game.InstallPath, Path.GetDirectoryName(game.ExecutablePath));
            Add(own, Path.GetFileNameWithoutExtension(game.ExecutablePath));
        }

        var parents = game.IsManifestSplitChild ? [] : Parents(originalRoot ?? game.InstallPath);
        return new Evidence(own, parents, Normalize(game.Name ?? ""));
    }

    /// <summary>True when <paramref name="title"/> is identified by the game's own path evidence (see the class summary).</summary>
    public static bool Matches(Evidence evidence, string title)
    {
        var canonical = Normalize(title);
        if (canonical.Length == 0) return false;
        if (evidence.Own.Contains(canonical)) return true;
        return evidence.Title.Length > 0
               && ContainsPhrase(canonical, evidence.Title)
               && evidence.Parents.Any(parent => ContainsPhrase(parent, canonical));
    }

    private static IReadOnlyList<string> Parents(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return [];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
        var result = new List<string>(MaxParentLevels);
        DirectoryInfo? current;
        try { current = new DirectoryInfo(root.TrimEnd(Path.DirectorySeparatorChar)).Parent; }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return []; }
        for (var level = 0; current?.Parent is not null && level < MaxParentLevels; level++, current = current.Parent)
        {
            var full = current.FullName.TrimEnd(Path.DirectorySeparatorChar);
            if (home.Length > 0 && (home.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal) || full == home)) break;
            if (LibraryRootNames.Contains(current.Name, StringComparer.OrdinalIgnoreCase)) break;
            var normalized = Normalize(current.Name);
            if (normalized.Length > 0) result.Add(normalized);
        }
        return result;
    }

    private static bool ContainsPhrase(string haystack, string phrase)
        => (" " + haystack + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);

    private static void AddLast(HashSet<string> result, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Add(result, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    private static void AddBelow(HashSet<string> result, string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return;
        string relative;
        try { relative = Path.GetRelativePath(root, path); }
        catch (ArgumentException) { return; }
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            Add(result, segment);
    }

    private static void Add(HashSet<string> result, string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return;
        var normalized = Normalize(segment);
        if (normalized.Length > 0) result.Add(normalized);
    }
}
