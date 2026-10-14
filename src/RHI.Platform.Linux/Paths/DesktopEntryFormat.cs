using System.Reflection;
using System.Text;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// Writing freedesktop.org desktop entries (Desktop Entry Specification 1.5) for the files RHI creates on an explicit
/// user choice: game shortcuts (#162) and the nxm:// handler entry (#99). Exec arguments are always quoted; inside the
/// quotes <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> take the Exec rule's backslash, and that backslash is itself written
/// doubled because the string-type unescape runs first (file <c>\\$</c> → Exec <c>\$</c> → <c>$</c>; a backslash is four).
/// A single backslash before <c>"</c>/<c>`</c>/<c>$</c> is not a valid string escape: GLib refuses the whole value and
/// only KDE keeps it. <c>%</c> is doubled (field codes) — the rule <c>install-user.sh</c> uses for RHI's own menu entry.
/// String values (Name, Comment) escape backslash, newline, tab and carriage return.
/// </summary>
public static class DesktopEntryFormat
{
    /// <summary>One quoted Exec argument. Refuses (ArgumentException) a value with a control character (newline, NUL…).</summary>
    public static string QuoteExecArgument(string argument)
    {
        if (argument.Any(char.IsControl)) throw new ArgumentException("A desktop entry argument cannot hold control characters.", nameof(argument));
        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        foreach (var c in argument)
        {
            quoted.Append(c switch
            {
                '\\' => @"\\\\",
                '"' => @"\\""",
                '`' => @"\\`",
                '$' => @"\\$",
                '%' => "%%",
                _ => c.ToString()
            });
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>An Exec value: every argument quoted, separated by spaces, plus raw field codes (e.g. <c>%u</c>) at the end.</summary>
    public static string ExecLine(IEnumerable<string> arguments, string? fieldCode = null)
    {
        var line = string.Join(' ', arguments.Select(QuoteExecArgument));
        return fieldCode is null ? line : line + " " + fieldCode;
    }

    /// <summary>A string/localestring value: control characters other than tab/newline/CR dropped, the rest escaped.</summary>
    public static string EscapeString(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': escaped.Append(@"\\"); break;
                case '\n': escaped.Append(@"\n"); break;
                case '\t': escaped.Append(@"\t"); break;
                case '\r': escaped.Append(@"\r"); break;
                default:
                    if (!char.IsControl(c)) escaped.Append(c);
                    break;
            }
        }
        var text = escaped.ToString();
        return text.StartsWith(' ') ? @"\s" + text[1..] : text;
    }

    /// <summary>
    /// How to start this RHI from a desktop entry: the AppImage (<c>$APPIMAGE</c>) when it runs from one, else the
    /// executable of this process (the self-contained build or <c>install-user.sh</c>'s install), or <c>dotnet RHI.dll</c>
    /// in a development run. Null when none can be told.
    /// </summary>
    public static IReadOnlyList<string>? RhiCommand(Func<string, string?>? environment = null, string? processPath = null, string? entryAssembly = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (environment("APPIMAGE") is { Length: > 0 } appImage && Path.IsPathRooted(appImage) && File.Exists(appImage))
            return [appImage];
        processPath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathRooted(processPath)) return null;
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.Ordinal))
        {
            entryAssembly ??= Assembly.GetEntryAssembly()?.Location;
            return string.IsNullOrWhiteSpace(entryAssembly) ? null : [processPath, entryAssembly];
        }
        return [processPath];
    }

    /// <summary>The user's applications folder (<c>$XDG_DATA_HOME/applications</c>).</summary>
    public static string ApplicationsFolder => Path.Combine(LinuxPaths.DataHome, "applications");
}
