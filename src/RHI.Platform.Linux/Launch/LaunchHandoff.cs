using System.Text;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Launch;

/// <summary>Which store client a game's launch command hands the start to (None: RHI runs the game itself).</summary>
public enum LaunchHandoff
{
    /// <summary>The command runs the game (or the user's own wrapper) directly.</summary>
    None,
    Steam,
    Heroic,
    Lutris,
    /// <summary>Another opener or sandbox (a URI handler, a Flatpak app) that starts the game on its own.</summary>
    Other
}

/// <summary>
/// The one store hand-off classification (Refs #106): the launcher's working-directory choice and the HDR
/// launch profile both ask it, so a Heroic game started through <c>xdg-open heroic://…</c> is a hand-off for
/// both (it was a direct launch for HDR, which wrapped <c>xdg-open</c> and reported HDR applied, #46).
/// The launch command decides, because it is what actually runs: a store card whose command the user
/// replaced with a direct one is a direct launch. Only a card without a command falls back to its source.
/// </summary>
public static class LaunchHandoffClassifier
{
    private static readonly string[] UriOpeners = ["xdg-open", "gio", "kde-open", "kde-open5", "kioclient", "kioclient5", "gnome-open"];
    // Words that run the next word as the program, unchanged.
    private static readonly string[] TransparentPrefixes = ["exec", "nohup", "setsid", "command"];

    public static LaunchHandoff Classify(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!string.IsNullOrWhiteSpace(game.LaunchCommand))
            return ClassifyCommand(game.LaunchCommand);

        var source = game.Source ?? string.Empty;
        if (source.Equals("Steam", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (source.StartsWith("Heroic", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Heroic;
        if (source.Equals("Lutris", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        return LaunchHandoff.None;
    }

    public static bool IsHandoff(DetectedGame game) => Classify(game) != LaunchHandoff.None;

    internal static LaunchHandoff ClassifyCommand(string command)
    {
        var words = ShellWords(command);
        var index = SkipToProgram(words, 0);
        if (index >= words.Count) return LaunchHandoff.None;

        var word = words[index];
        if (word.Contains("://", StringComparison.Ordinal)) return ByUri(word);
        var program = Path.GetFileName(word);
        if (program.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
            program.Equals("steam.sh", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (program.Equals("lutris", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        if (program.Equals("heroic", StringComparison.OrdinalIgnoreCase) ||
            program.Equals("heroic-games-launcher", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Heroic;
        if (program.Equals("flatpak", StringComparison.OrdinalIgnoreCase)) return ByFlatpakApp(words, index + 1);
        if (UriOpeners.Contains(program, StringComparer.OrdinalIgnoreCase))
        {
            // `gio open URI`, `kioclient exec URI`: the first argument that is not a verb or option.
            var uri = words.Skip(index + 1).FirstOrDefault(arg => !arg.StartsWith('-') && (arg.Contains(':') || arg.Contains('/')));
            return uri is null ? LaunchHandoff.Other : ByUri(uri);
        }
        return LaunchHandoff.None;
    }

    private static int SkipToProgram(IReadOnlyList<string> words, int index)
    {
        while (index < words.Count)
        {
            var word = words[index];
            if (IsAssignment(word) || TransparentPrefixes.Contains(word, StringComparer.Ordinal))
            {
                index++;
                continue;
            }
            if (Path.GetFileName(word).Equals("env", StringComparison.Ordinal))
            {
                index++;
                while (index < words.Count && (words[index].StartsWith('-') || IsAssignment(words[index])))
                {
                    // `env -u NAME` / `env --unset NAME` / `env -C DIR` take the next word.
                    if (words[index] is "-u" or "--unset" or "-C" or "--chdir") index++;
                    index++;
                }
                continue;
            }
            break;
        }
        return index;
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        if (equals <= 0) return false;
        for (var i = 0; i < equals; i++)
        {
            var ch = word[i];
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_') || (i == 0 && char.IsAsciiDigit(ch))) return false;
        }
        return true;
    }

    private static LaunchHandoff ByFlatpakApp(IReadOnlyList<string> words, int index)
    {
        // `flatpak run [--option[=value]…] APP-ID [args]`
        if (index < words.Count && words[index] == "run") index++;
        while (index < words.Count && words[index].StartsWith('-')) index++;
        if (index >= words.Count) return LaunchHandoff.Other;
        var app = words[index];
        if (app.Equals("com.valvesoftware.Steam", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (app.Equals("net.lutris.Lutris", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        if (app.Equals("com.heroicgameslauncher.hgl", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Heroic;
        return LaunchHandoff.Other;
    }

    private static LaunchHandoff ByUri(string uri)
    {
        if (uri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (uri.StartsWith("heroic:", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Heroic;
        if (uri.StartsWith("lutris:", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        return LaunchHandoff.Other;
    }

    /// <summary>
    /// POSIX shell words of a simple command: single quotes literal, double quotes with backslash escapes,
    /// backslash outside quotes. Stops at the first unquoted command separator; expansions are not performed
    /// (this only has to find the program and its first arguments).
    /// </summary>
    internal static List<string> ShellWords(string command)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '\'')
            {
                inWord = true;
                var end = command.IndexOf('\'', i + 1);
                if (end < 0) end = command.Length;
                current.Append(command, i + 1, Math.Max(0, end - i - 1));
                i = end;
            }
            else if (ch == '"')
            {
                inWord = true;
                for (i++; i < command.Length && command[i] != '"'; i++)
                {
                    if (command[i] == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\' or '$' or '`')
                        i++;
                    current.Append(command[i]);
                }
            }
            else if (ch == '\\' && i + 1 < command.Length)
            {
                inWord = true;
                current.Append(command[++i]);
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (inWord) words.Add(current.ToString());
                current.Clear();
                inWord = false;
            }
            else if (ch is ';' or '&' or '|' or '\n')
            {
                break;
            }
            else
            {
                inWord = true;
                current.Append(ch);
            }
        }
        if (inWord) words.Add(current.ToString());
        return words;
    }
}
