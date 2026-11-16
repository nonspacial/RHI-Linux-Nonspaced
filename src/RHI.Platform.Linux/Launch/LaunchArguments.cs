using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Detection;

namespace RHI.Platform.Linux.Launch;

/// <summary>How a game's launch arguments reach it when RHI launches the game (w14/layouts-c, upstream 2.9.0+ per-game arguments).</summary>
public enum LaunchArgumentsRoute
{
    /// <summary>RHI runs the game's program itself (a standalone Wine prefix game): the arguments go on the program's argv.</summary>
    Direct,
    /// <summary>A Steam game RHI starts through the Steam client: <c>steam -applaunch &lt;appid&gt; &lt;args&gt;</c> (upstream).</summary>
    Steam,
    /// <summary>
    /// A Steam game RHI can only start through the <c>steam://</c> link (no <c>steam</c> program on PATH): the link cannot carry
    /// arguments, so they go in Steam's launch options.
    /// </summary>
    SteamLinkOnly,
    /// <summary>A non-Steam game in Steam: Steam starts the shortcut, so the arguments go in the shortcut's launch options.</summary>
    SteamShortcut,
    /// <summary>The launch command was typed by the user (Add game, Edit game): RHI never splices arguments into it.</summary>
    UserCommand,
    /// <summary>Another launcher starts the game (Heroic, Lutris, a store client, the Xbox app, a URI opener): set them there.</summary>
    Launcher,
    /// <summary>RHI cannot launch the game at all (no launch command).</summary>
    NoCommand
}

/// <summary>
/// Per-game launch arguments (upstream 2.9.0+ <c>LaunchArgsOverrides</c>, Game Overrides' Launch arguments box). Upstream passes the
/// text as the Windows command line of the exe it starts, or after <c>steam -applaunch &lt;appid&gt;</c>. On Linux:
/// <list type="bullet">
/// <item>the text is split into arguments by the Windows rules (<see cref="Split"/>), so it means what it means on Windows; Wine
/// rebuilds the Windows command line from them;</item>
/// <item>a command RHI built itself (a standalone Wine prefix game) gets them appended, each one shell-quoted;</item>
/// <item>a Steam game starts through <c>steam -applaunch &lt;appid&gt; &lt;args&gt;</c> (the hand-off keeps Steam's own working
/// directory); without arguments the usual hand-off is unchanged;</item>
/// <item>a command the user typed and every other launcher are left as they are, and the page says where to add them.</item>
/// </list>
/// </summary>
public static class LaunchArguments
{
    /// <summary>The longest text kept (Windows' own command line limit is 32767 characters, the exe path included).</summary>
    public const int MaxLength = 4096;

    /// <summary>
    /// The text as it is saved: trimmed, line breaks and tabs as spaces (a single-line box can be pasted into), null when empty.
    /// Null too for text RHI cannot pass (a NUL character, longer than <see cref="MaxLength"/>); <see cref="Problem"/> says so.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (text is null) return null;
        var flat = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length == 0 || Problem(flat) is not null ? null : flat;
    }

    /// <summary>Why <paramref name="text"/> cannot be saved (English; the desktop words it), or null.</summary>
    public static string? Problem(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains('\0')) return "Launch arguments cannot contain a NUL character.";
        return text.Length > MaxLength ? $"Launch arguments are limited to {MaxLength} characters." : null;
    }

    /// <summary>
    /// Splits a Windows command line into arguments as the Microsoft C runtime does (<c>CommandLineToArgvW</c> for the arguments
    /// after the program): spaces separate, double quotes group, <c>2n</c> backslashes before a quote give <c>n</c> and the quote
    /// groups, <c>2n+1</c> give <c>n</c> and a literal quote, <c>""</c> inside quotes is a literal quote, other backslashes are kept.
    /// </summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return args;
        var current = new StringBuilder();
        var inQuotes = false;
        var inArgument = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch is ' ' or '\t' && !inQuotes)
            {
                if (inArgument) args.Add(current.ToString());
                current.Clear();
                inArgument = false;
                i++;
                continue;
            }
            inArgument = true;
            if (ch == '\\')
            {
                var backslashes = 0;
                while (i < text.Length && text[i] == '\\') { backslashes++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                    // An even run leaves the quote to the loop: it opens or closes a group.
                }
                else current.Append('\\', backslashes);
                continue;
            }
            if (ch == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"'); // "" inside a group: a literal quote
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;
                i++;
                continue;
            }
            current.Append(ch);
            i++;
        }
        if (inArgument) args.Add(current.ToString());
        return args;
    }

    /// <summary>Whether <c>steam</c> is a program on this session's PATH (the host Steam client).</summary>
    public static bool SteamOnPath(string program)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, program)));

    /// <summary>How the arguments reach <paramref name="game"/> when RHI launches it.</summary>
    /// <param name="onPath">Whether a program is on PATH (default <see cref="SteamOnPath"/>; tests pass their own).</param>
    public static LaunchArgumentsRoute RouteFor(DetectedGame game, Func<string, bool>? onPath = null)
        => Plan(game, onPath ?? SteamOnPath).Route;

    /// <summary>
    /// The command RHI runs for <paramref name="game"/> with <paramref name="arguments"/>: the game's own command with them
    /// appended (each shell-quoted) where they apply, the game's own command unchanged otherwise (no arguments, a command the
    /// user typed, another launcher). Never null for a game that has a command.
    /// </summary>
    public static string? CommandWith(DetectedGame game, IReadOnlyList<string>? arguments, Func<string, bool>? onPath = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (arguments is not { Count: > 0 }) return game.LaunchCommand;
        var plan = Plan(game, onPath ?? SteamOnPath);
        if (plan.Base is null || plan.Route is not (LaunchArgumentsRoute.Direct or LaunchArgumentsRoute.Steam)) return game.LaunchCommand;
        return plan.Base + " " + string.Join(' ', arguments.Select(ShellQuote));
    }

    private sealed record ArgumentsPlan(LaunchArgumentsRoute Route, string? Base = null);

    private static ArgumentsPlan Plan(DetectedGame game, Func<string, bool> onPath)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(game.LaunchCommand)) return new(LaunchArgumentsRoute.NoCommand);
        if (LinuxGameLauncher.IsUserCommand(game)) return new(LaunchArgumentsRoute.UserCommand);
        if (string.Equals(game.Source, SteamGameDetector.ShortcutSource, StringComparison.OrdinalIgnoreCase))
            return new(LaunchArgumentsRoute.SteamShortcut);
        var command = game.LaunchCommand!;
        var handoff = LaunchHandoffClassifier.Classify(game);
        if (handoff == LaunchHandoff.Steam) return SteamPlan(game, command, onPath);
        // A launcher's own exe that opens the launcher (the user starts the game there) is not the game.
        if (handoff != LaunchHandoff.None || !string.IsNullOrWhiteSpace(game.ParentLauncherName) || game.XboxPackage is not null)
            return new(LaunchArgumentsRoute.Launcher);
        // Appended words only reach the program of a single simple command (never a `cd … && wine …` line's last part by luck).
        return LaunchHandoffClassifier.SimpleCommands(command).Count == 1
            ? new(LaunchArgumentsRoute.Direct, command.TrimEnd())
            : new(LaunchArgumentsRoute.Launcher);
    }

    /// <summary>
    /// The Steam commands RHI builds (<see cref="SteamGameDetector.LaunchCommandFor"/>): <c>steam -applaunch N</c> and the
    /// Flatpak client's <c>flatpak run com.valvesoftware.Steam -applaunch N</c> take the arguments after the AppID; the
    /// <c>steam://rungameid/N</c> link becomes <c>steam -applaunch N</c> when the host Steam program is there, else it cannot
    /// carry them (upstream falls back to the link the same way).
    /// </summary>
    private static ArgumentsPlan SteamPlan(DetectedGame game, string command, Func<string, bool> onPath)
    {
        if (game.SteamAppId is not { } appId || appId <= 0) return new(LaunchArgumentsRoute.Launcher);
        var commands = LaunchHandoffClassifier.SimpleCommands(command);
        if (commands.Count != 1) return new(LaunchArgumentsRoute.Launcher);
        var id = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var words = commands[0].Words;
        if (words is ["steam", "-applaunch", var a] && a == id) return new(LaunchArgumentsRoute.Steam, $"steam -applaunch {id}");
        if (words is ["flatpak", "run", "com.valvesoftware.Steam", "-applaunch", var f] && f == id)
            return new(LaunchArgumentsRoute.Steam, $"flatpak run com.valvesoftware.Steam -applaunch {id}");
        if (words is ["xdg-open", var link] && link == "steam://rungameid/" + id)
            return onPath("steam") ? new(LaunchArgumentsRoute.Steam, $"steam -applaunch {id}") : new(LaunchArgumentsRoute.SteamLinkOnly);
        return new(LaunchArgumentsRoute.Launcher);
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
