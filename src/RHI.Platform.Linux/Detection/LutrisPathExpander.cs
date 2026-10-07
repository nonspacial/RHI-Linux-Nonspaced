using System.Text;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// POSIX-style expansion for paths in Lutris configs: a leading <c>~</c> or <c>~user</c>, <c>$VAR</c> and
/// <c>${VAR}</c> anywhere (with <c>$GAMEDIR</c> taken from the game's <c>pga.db</c> directory), and the
/// Windows-style <c>%VAR%</c> that .NET already expands. Unknown variables stay literal, so the path
/// names nothing rather than collapsing to a shorter, real directory such as <c>/</c>.
/// </summary>
internal static class LutrisPathExpander
{
    public static string? Expand(string? value, string home, string? gameDir = null, Func<string, string?>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        environment ??= Environment.GetEnvironmentVariable;
        var text = value.Trim();

        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
            text = home + text[1..];
        else if (text.StartsWith('~'))
        {
            var slash = text.IndexOf('/');
            var user = slash < 0 ? text[1..] : text[1..slash];
            if (UserHome(user) is { } userHome)
                text = userHome + (slash < 0 ? string.Empty : text[slash..]);
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '$' || i + 1 >= text.Length) { builder.Append(text[i]); continue; }
            string name;
            int consumed;
            if (text[i + 1] == '{')
            {
                var close = text.IndexOf('}', i + 2);
                if (close < 0) { builder.Append(text[i]); continue; }
                name = text[(i + 2)..close];
                consumed = close - i + 1;
            }
            else
            {
                var end = i + 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                name = text[(i + 1)..end];
                consumed = end - i;
            }
            var replacement = name.Length == 0 ? null
                : name == "GAMEDIR" ? gameDir
                : name == "HOME" ? home
                : environment(name);
            if (replacement is null) { builder.Append(text, i, consumed); }
            else builder.Append(replacement.TrimEnd('/') is { Length: > 0 } r ? r : replacement);
            i += consumed - 1;
        }
        return Environment.ExpandEnvironmentVariables(builder.ToString());
    }

    /// <summary>Home directory of another user, from the passwd database file.</summary>
    internal static string? UserHome(string user, string passwdPath = "/etc/passwd")
    {
        if (user.Length == 0) return null;
        try
        {
            foreach (var line in File.ReadLines(passwdPath))
            {
                var fields = line.Split(':');
                if (fields.Length >= 6 && fields[0] == user && fields[5].StartsWith('/')) return fields[5];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the passwd file ~user stays literal.
            RHI.Core.Services.DetectorDiagnostics.Warn($"{passwdPath} unreadable; ~{user} left unexpanded", ex);
        }
        return null;
    }
}
