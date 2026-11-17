using System.Text;

namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>
/// #318: the text rules for putting a game's launch arguments into a launcher's own launch options. RHI's arguments are
/// read by the Windows rules (<see cref="LaunchArguments.Split"/>); Steam's launch options, Lutris' <c>args</c> and Heroic's
/// <c>launcherArgs</c> are all read as a POSIX shell line (Steam runs them through a shell, Lutris and Heroic split them
/// with <c>shlex</c>), so each argument is shell-quoted where it needs it (<see cref="ShellJoin"/>). The user's own options
/// are always kept: RHI's part is appended, or for Steam put right after the first <c>%command%</c> (the game's own
/// command line, where Steam's arguments go), so <c>gamemoderun %command%</c> and <c>VAR=1 %command% | tee log</c> keep
/// working. Removal takes out exactly the part RHI added (<see cref="RemovePart"/>).
/// </summary>
public static class LauncherOptionsText
{
    /// <summary>Steam's placeholder for the game's own command in launch options.</summary>
    public const string SteamCommandToken = "%command%";

    /// <summary>The arguments as one shell line: each word as it is when it needs no quoting, else in single quotes.</summary>
    public static string ShellJoin(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(ShellWord));
    }

    /// <summary>One shell word: unchanged when it is only safe characters, else single-quoted (<c>'</c> as <c>'\''</c>).</summary>
    public static string ShellWord(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && value.All(IsSafe)) return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    // Characters a POSIX shell (and shlex) never treat specially in a word. '%' keeps Steam's %command% out: an argument
    // spelled %command% is quoted so Steam does not substitute it.
    private static bool IsSafe(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
        or '-' or '_' or '.' or '/' or ',' or ':' or '+' or '=' or '@';

    /// <summary>
    /// The launcher's options with RHI's <paramref name="part"/> added: the part alone when there were none, after the first
    /// <c>%command%</c> for Steam when the options have one, else at the end. The user's text is kept character for character.
    /// </summary>
    public static string Merge(string? existing, string part, bool steam)
    {
        ArgumentException.ThrowIfNullOrEmpty(part);
        var current = existing ?? "";
        if (current.Trim().Length == 0) return part;
        if (steam)
        {
            var token = current.IndexOf(SteamCommandToken, StringComparison.Ordinal);
            if (token >= 0)
            {
                var end = token + SteamCommandToken.Length;
                return current[..end] + " " + part + current[end..];
            }
        }
        return current.TrimEnd() + " " + part;
    }

    /// <summary>
    /// <paramref name="current"/> without RHI's <paramref name="part"/> and the one space that joined it, or null when the
    /// part is not there as whole words any more (the user edited it). The last whole-word match is taken (RHI adds at the
    /// end, or right after <c>%command%</c>).
    /// </summary>
    public static string? RemovePart(string current, string part)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(part);
        var at = current.LastIndexOf(part, StringComparison.Ordinal);
        while (at >= 0)
        {
            var end = at + part.Length;
            var startsWord = at == 0 || char.IsWhiteSpace(current[at - 1]);
            var endsWord = end == current.Length || char.IsWhiteSpace(current[end]);
            if (startsWord && endsWord)
            {
                // Take the space before the part (or after it, when it opened the text).
                if (at > 0) return current[..(at - 1)] + current[end..];
                return end < current.Length ? current[(end + 1)..] : "";
            }
            at = at == 0 ? -1 : current.LastIndexOf(part, at - 1, StringComparison.Ordinal);
        }
        return null;
    }

    /// <summary>Whether <paramref name="current"/> already holds <paramref name="part"/> as whole words.</summary>
    public static bool Contains(string? current, string part) => current is not null && RemovePart(current, part) is not null;

    /// <summary>Steam's text KeyValues escaping for a quoted value (<c>\</c>, <c>"</c>, line breaks and tabs).</summary>
    public static string VdfEscape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString()
            });
        }
        return builder.ToString();
    }
}
