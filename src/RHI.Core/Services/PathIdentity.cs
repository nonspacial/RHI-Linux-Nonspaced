namespace RHI.Core.Services;

/// <summary>
/// Filesystem identity of a library path: absolute, every symlinked component resolved, no trailing
/// separator, compared ordinally (Linux filesystems are case-sensitive). Two spellings of one folder
/// (<c>~/Games/X</c> through a symlink and <c>/media/disk/Games/X</c>, or a trailing <c>/</c>) get the
/// same identity; a path that cannot be resolved keeps its lexical full path.
/// </summary>
public static class PathIdentity
{
    private const int MaxLinkHops = 40;

    public static string Canonicalize(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return TrimTrailingSeparators(path.Trim());
        }

        var hops = 0;
        var restart = true;
        while (restart)
        {
            restart = false;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return TrimTrailingSeparators(full);
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                var target = ReadLink(next);
                if (target is null)
                {
                    current = next;
                    continue;
                }

                if (++hops > MaxLinkHops) return TrimTrailingSeparators(full); // link loop: keep the lexical path
                var resolved = Path.IsPathRooted(target) ? target : Path.Combine(current, target);
                var rest = parts.Skip(i + 1).ToArray();
                try
                {
                    full = Path.GetFullPath(rest.Length == 0 ? resolved : Path.Combine([resolved, .. rest]));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return TrimTrailingSeparators(Path.GetFullPath(path));
                }
                restart = true;
                break;
            }

            if (!restart) full = current;
        }

        return TrimTrailingSeparators(full);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or lies below it, after symlink resolution.</summary>
    public static bool IsSameOrInside(string candidate, string root)
    {
        var c = Canonicalize(candidate);
        var r = Canonicalize(root);
        if (c.Equals(r, StringComparison.Ordinal)) return true;
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static bool AreSame(string a, string b)
        => Canonicalize(a).Equals(Canonicalize(b), StringComparison.Ordinal);

    private static string? ReadLink(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable component: treat it as a plain directory; the lexical path is still a valid identity.
            return null;
        }
    }

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || (!string.IsNullOrEmpty(root) && trimmed.Length < root.Length) ? root ?? path : trimmed;
    }
}
