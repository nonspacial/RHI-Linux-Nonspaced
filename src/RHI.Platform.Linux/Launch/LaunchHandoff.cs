using System.Globalization;
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
    /// <summary>
    /// The app that owns an Xbox / Game Pass install (PartingX, the Xodus GUI) is opened; the user starts the game there
    /// (Refs #90). A direct <c>xodus-cli run</c> is not a hand-off: it runs the game.
    /// </summary>
    Xbox,
    /// <summary>Another opener or launcher (a URI handler, a launcher's Flatpak) that starts the game on its own.</summary>
    Other
}

/// <summary>
/// The one store hand-off classification (Refs #106): the launcher's working-directory choice and the HDR
/// launch profile both ask it, so a Heroic game started through <c>xdg-open heroic://…</c> is a hand-off for
/// both (it was a direct launch for HDR, which wrapped <c>xdg-open</c> and reported HDR applied, #46).
/// The launch command decides, because it is what actually runs: a store card whose command the user
/// replaced with a direct one is a direct launch. Only a card without a command falls back to its source.
/// The command is read through the wrappers people put in front of a store client (gamemoderun, prime-run,
/// mangohud, <c>gamescope … --</c>, nice/ionice/taskset, <c>env</c>, <c>sh -c '…'</c>, <c>cd … &amp;&amp;</c>),
/// and a <c>steam:</c>, <c>heroic:</c> or <c>lutris:</c> URL anywhere in the command that runs is that store's
/// hand-off (a Heroic AppImage started with <c>heroic://launch/…</c>, say).
/// </summary>
public static class LaunchHandoffClassifier
{
    private static readonly string[] UriOpeners = ["xdg-open", "gio", "kde-open", "kde-open5", "kioclient", "kioclient5", "gnome-open"];
    private static readonly string[] SteamPrograms = ["steam", "steam.sh", "steam-runtime", "steam-native"];
    private static readonly string[] HeroicPrograms = ["heroic", "heroic-games-launcher"];
    // The apps that own Xbox installs on Linux (Refs #90); xodus-cli is not one (`xodus-cli run` runs the game).
    private static readonly string[] XboxPrograms = ["partingx", "xodus-gui"];
    private static readonly string[] Shells = ["sh", "bash", "dash", "zsh", "ksh", "mksh", "busybox-sh"];
    // Shell set-up that runs before the program (`cd /game && steam …`, `export X=1; steam …`).
    private static readonly string[] SetupCommands = ["cd", "pushd", "popd", "export", "unset", "set", "umask", "ulimit", "source", ".", ":", "true", "trap", "shopt", "alias"];

    // Launchers distributed as Flatpaks that start the game themselves; any other Flatpak app is the game.
    private static readonly string[] LauncherFlatpaks = ["com.usebottles.bottles", "io.itch.itch", "com.playonlinux.PlayOnLinux4"];

    // flatpak run options that take their value as the next word (`--branch stable`); `--opt=value` is one word.
    private static readonly string[] FlatpakValueOptions =
    [
        "--arch", "--branch", "--command", "--cwd", "--runtime", "--runtime-version", "--commit", "--runtime-commit",
        "--env", "--unset-env", "--filesystem", "--nofilesystem", "--socket", "--nosocket", "--device", "--nodevice",
        "--share", "--unshare", "--allow", "--disallow", "--persist", "--talk-name", "--no-talk-name", "--own-name",
        "--system-talk-name", "--system-no-talk-name", "--system-own-name", "--add-policy", "--remove-policy",
        "--app-path", "--usr-path", "--instance-id-fd", "--parent-pid", "--installation", "--env-fd", "--sandbox-flag"
    ];

    /// <summary>How a wrapper finds the program it runs: its options, how many of them take a value word, fixed arguments.</summary>
    private sealed record Wrapper(string[] ValueOptions, int Arguments = 0, bool NumericArguments = false, bool AfterDoubleDash = false);

    // Programs that run the rest of the line as the program, unchanged apart from their own options.
    private static readonly Dictionary<string, Wrapper> Wrappers = new(StringComparer.Ordinal)
    {
        ["exec"] = new(["-a"]),
        ["nohup"] = new([]),
        ["setsid"] = new([]),
        ["command"] = new([]),
        ["time"] = new(["-f", "-o", "--format", "--output"]),
        ["gamemoderun"] = new([]),
        ["mangohud"] = new([]),
        ["prime-run"] = new([]),
        ["primusrun"] = new([]),
        ["pvkrun"] = new([]),
        ["nvidia-offload"] = new([]),
        ["optirun"] = new(["-b", "-c", "-d", "-C"]),
        ["obs-gamecapture"] = new([]),
        ["obs-vkcapture"] = new([]),
        ["dbus-run-session"] = new([]),
        ["nice"] = new(["-n", "--adjustment"]),
        ["ionice"] = new(["-c", "-n", "--class", "--classdata"]),
        // `taskset [-c] MASK|LIST PROGRAM`, `chrt [policy] PRIORITY PROGRAM`
        ["taskset"] = new([], Arguments: 1),
        ["chrt"] = new(["-T", "-P", "-D", "--sched-runtime", "--sched-period", "--sched-deadline"], Arguments: 1),
        ["stdbuf"] = new(["-i", "-o", "-e", "--input", "--output", "--error"]),
        // `strangle [opts] FPS [FPS-on-battery] PROGRAM`
        ["strangle"] = new([], NumericArguments: true),
        // `gamescope [opts] -- PROGRAM`: `--` marks the program; without it (`gamescope -W 1920 steam`, which gamescope
        // also runs) its options are skipped by the value-taking ones `gamescope --help` lists.
        ["gamescope"] = new(
        [
            "-W", "-H", "-w", "-h", "-r", "-m", "-S", "-F", "-s", "-R", "-T", "-C", "-o", "-O",
            "--output-width", "--output-height", "--nested-width", "--nested-height", "--nested-refresh", "--max-scale",
            "--scaler", "--filter", "--sharpness", "--fsr-sharpness", "--mouse-sensitivity", "--backend", "--cursor",
            "--ready-fd", "--stats-path", "--hide-cursor-delay", "--xwayland-count", "--prefer-vk-device",
            "--force-orientation", "--cursor-scale-height", "--virtual-connector-strategy", "--sdr-gamut-wideness",
            "--hdr-sdr-content-nits", "--hdr-itm-sdr-nits", "--hdr-itm-target-nits", "--framerate-limit",
            "--nested-unfocused-refresh", "--prefer-output", "--default-touch-mode", "--generate-drm-mode",
            "--display-index", "--reshade-effect", "--reshade-technique-idx", "--mura-map", "--vr-overlay-key",
            "--vr-app-overlay-key", "--vr-overlay-explicit-name", "--vr-overlay-default-name", "--vr-overlay-icon",
            "--vr-overlay-physical-width", "--vr-overlay-physical-curvature", "--vr-overlay-physical-pre-curve-pitch",
            "--vr-scrolls-speed"
        ], AfterDoubleDash: true),
    };

    private const int MaxNesting = 4;

    public static LaunchHandoff Classify(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        // An Xbox card's own command (PartingX's Play entry point, or the owning app's opener, which may be that app's
        // start script naming no store) is the owning app's hand-off; a command the user typed instead is read by its words.
        if (game.UserLaunchCommand is null && game.XboxPackage?.OpenCommand is { } open &&
            string.Equals(open, game.LaunchCommand, StringComparison.Ordinal))
            return LaunchHandoff.Xbox;
        if (!string.IsNullOrWhiteSpace(game.LaunchCommand))
            return ClassifyCommand(game.LaunchCommand, 0, OwningAppPrograms(game.XboxPackage));

        var source = game.Source ?? string.Empty;
        if (source.Equals("Steam", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (source.StartsWith("Heroic", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Heroic;
        if (source.Equals("Lutris", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        if (source.Equals("Xbox", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Xbox;
        return LaunchHandoff.None;
    }

    public static bool IsHandoff(DetectedGame game) => Classify(game) != LaunchHandoff.None;

    internal static LaunchHandoff ClassifyCommand(string command) => ClassifyCommand(command, 0, []);

    private static LaunchHandoff ClassifyCommand(string command, int nesting, IReadOnlyCollection<string> owningApps)
    {
        var words = ProgramCommand(SimpleCommands(command));
        return words is null ? LaunchHandoff.None : ClassifyWords(words, nesting, owningApps);
    }

    /// <summary>
    /// The programs that start an Xbox card's owning app (scan re-review minor 1): its Play/Show entry point's launcher
    /// (the breadcrumb's <c>argv[0]</c>, e.g. a PartingX instance's <c>run.sh</c>) and the opener's program. A command
    /// the user typed that runs one of them is still the owning app's hand-off, whatever the file is called.
    /// </summary>
    private static IReadOnlyCollection<string> OwningAppPrograms(XboxPackageInfo? package)
    {
        if (package is null) return [];
        var programs = new HashSet<string>(StringComparer.Ordinal);
        if (package.PlayArgv is [var play, ..] && !string.IsNullOrWhiteSpace(play)) programs.Add(play);
        if (package.ShowArgv is [var show, ..] && !string.IsNullOrWhiteSpace(show)) programs.Add(show);
        // The opener counts only as a launcher file of its own (an instance's run.sh), never a generic program it goes
        // through (xdg-open, flatpak, a shell, env): those start anything.
        if (package.OpenCommand is { } open && ProgramCommand(SimpleCommands(open)) is { } words &&
            SkipToProgram(words, 0) is var index && index < words.Count && Path.IsPathRooted(words[index]) &&
            !IsGenericProgram(Path.GetFileName(words[index])))
            programs.Add(words[index]);
        return programs;
    }

    private static bool IsGenericProgram(string program)
        => UriOpeners.Contains(program, StringComparer.OrdinalIgnoreCase) || Shells.Contains(program, StringComparer.Ordinal) ||
           program.Equals("flatpak", StringComparison.OrdinalIgnoreCase) || program.Equals("env", StringComparison.Ordinal);

    /// <summary>
    /// The simple command that runs the program: shell set-up before it (<c>cd</c>, <c>export</c>, a bare
    /// assignment) is passed over, and so is the fallback after <c>||</c> (<c>cd /g || exit 1</c>).
    /// </summary>
    private static List<string>? ProgramCommand(IReadOnlyList<(List<string> Words, string Separator)> commands)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            var words = commands[i].Words;
            var index = SkipToProgram(words, 0);
            if (index < words.Count && !SetupCommands.Contains(words[index], StringComparer.Ordinal))
                return words;
            if (commands[i].Separator == "||") i++;
        }
        return null;
    }

    private static LaunchHandoff ClassifyWords(List<string> words, int nesting, IReadOnlyCollection<string> owningApps)
    {
        var index = SkipToProgram(words, 0);
        if (index >= words.Count) return LaunchHandoff.None;

        var word = words[index];
        if (owningApps.Contains(word)) return LaunchHandoff.Xbox;
        if (word.Contains("://", StringComparison.Ordinal)) return ByUri(word);
        var program = Path.GetFileName(word);
        if (SteamPrograms.Contains(program, StringComparer.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (program.Equals("lutris", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        if (HeroicPrograms.Contains(program, StringComparer.OrdinalIgnoreCase) ||
            (program.StartsWith("heroic", StringComparison.OrdinalIgnoreCase) && program.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)))
            return LaunchHandoff.Heroic;
        if (XboxPrograms.Contains(program, StringComparer.OrdinalIgnoreCase) ||
            (program.StartsWith("partingx", StringComparison.OrdinalIgnoreCase) && program.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)))
            return LaunchHandoff.Xbox;
        if (UriOpeners.Contains(program, StringComparer.OrdinalIgnoreCase))
        {
            // `gio open URI`, `kioclient exec URI`: the first argument that is not a verb or option.
            var uri = words.Skip(index + 1).FirstOrDefault(arg => !arg.StartsWith('-') && (arg.Contains(':') || arg.Contains('/')));
            return uri is null ? LaunchHandoff.Other : ByUri(uri);
        }
        if (Shells.Contains(program, StringComparer.Ordinal))
        {
            // `sh -c 'steam -applaunch 1'`: the inner command decides; a script file is the user's own launch.
            var inner = ShellCommandString(words, index + 1);
            if (inner is not null && nesting < MaxNesting) return ClassifyCommand(inner, nesting + 1, owningApps);
        }
        else if (program.Equals("flatpak", StringComparison.OrdinalIgnoreCase) &&
                 ByFlatpakApp(words, index + 1) is var flatpak && flatpak != LaunchHandoff.None)
        {
            return flatpak;
        }

        // A store URL handed to whatever runs (a Heroic AppImage, a Flatpak, the user's wrapper) is that store's start.
        foreach (var argument in words.Skip(index + 1))
            if (StoreUri(argument) is { } store) return store;
        return LaunchHandoff.None;
    }

    private static int SkipToProgram(IReadOnlyList<string> words, int index)
    {
        while (index < words.Count)
        {
            var word = words[index];
            if (IsAssignment(word) || word is "{" or "!")
            {
                index++;
                continue;
            }
            var name = Path.GetFileName(word);
            if (name.Equals("env", StringComparison.Ordinal))
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
            if (Wrappers.TryGetValue(name, out var wrapper))
            {
                index = SkipWrapper(words, index + 1, wrapper);
                continue;
            }
            break;
        }
        return index;
    }

    private static int SkipWrapper(IReadOnlyList<string> words, int index, Wrapper wrapper)
    {
        if (wrapper.AfterDoubleDash)
        {
            // `--` decides when present (its options may take values the table does not know).
            for (var i = index; i < words.Count; i++)
                if (words[i] == "--") return i + 1;
        }
        while (index < words.Count && words[index].Length > 1 && words[index].StartsWith('-'))
        {
            if (words[index] == "--") return index + 1 + Math.Min(wrapper.Arguments, words.Count - index - 1);
            if (wrapper.ValueOptions.Contains(words[index], StringComparer.Ordinal)) index++;
            index++;
        }
        index = Math.Min(words.Count, index + wrapper.Arguments);
        if (wrapper.NumericArguments)
            while (index < words.Count && double.TryParse(words[index], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) index++;
        return index;
    }

    /// <summary>The command string of <c>sh [options] -c STRING</c>; null for a script file or no <c>-c</c>.</summary>
    private static string? ShellCommandString(IReadOnlyList<string> words, int index)
    {
        var hasCommandOption = false;
        while (index < words.Count && words[index].Length > 1 && words[index][0] is '-' or '+')
        {
            var option = words[index];
            if (option == "--") { index++; break; }
            // `-o OPT`, `-O OPT` and bash's `--rcfile FILE`/`--init-file FILE` take the next word.
            if (option is "-o" or "+o" or "-O" or "+O" or "--rcfile" or "--init-file") index++;
            else if (option[0] == '-' && !option.StartsWith("--", StringComparison.Ordinal) && option.Contains('c')) hasCommandOption = true;
            index++;
        }
        return hasCommandOption && index < words.Count ? words[index] : null;
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

    /// <summary>
    /// <c>flatpak run [options] APP-ID [args]</c>: the store clients by app id, other launchers as Other. Any other
    /// app is the game itself, run (and waited for) by <c>flatpak run</c>: a direct launch.
    /// </summary>
    private static LaunchHandoff ByFlatpakApp(IReadOnlyList<string> words, int index)
    {
        if (index < words.Count && words[index] == "run") index++;
        while (index < words.Count && words[index].StartsWith('-'))
        {
            if (FlatpakValueOptions.Contains(words[index], StringComparer.Ordinal)) index++;
            index++;
        }
        if (index >= words.Count) return LaunchHandoff.None;
        // `APP-ID//BRANCH` and `APP-ID/ARCH/BRANCH` name the same app (`com.valvesoftware.Steam//beta`).
        var app = words[index];
        if (app.IndexOf('/') is > 0 and var slash) app = app[..slash];
        if (app.Equals("com.valvesoftware.Steam", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (app.Equals("net.lutris.Lutris", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        if (app.Equals("com.heroicgameslauncher.hgl", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Heroic;
        if (app.Equals("io.github.xodus-gaming.xodus", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Xbox;
        if (LauncherFlatpaks.Contains(app, StringComparer.OrdinalIgnoreCase)) return LaunchHandoff.Other;
        return LaunchHandoff.None;
    }

    private static LaunchHandoff ByUri(string uri) => StoreUri(uri) ?? LaunchHandoff.Other;

    /// <summary>The store a <c>steam:</c>/<c>heroic:</c>/<c>lutris:</c> URL (or <c>--option=URL</c>) names, else null.</summary>
    private static LaunchHandoff? StoreUri(string word)
    {
        var equals = word.IndexOf('=');
        var value = equals > 0 && word.StartsWith('-') ? word[(equals + 1)..] : word;
        if (value.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Steam;
        if (value.StartsWith("heroic:", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Heroic;
        if (value.StartsWith("lutris:", StringComparison.OrdinalIgnoreCase)) return LaunchHandoff.Lutris;
        if (value.StartsWith("partingx:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("xodus:", StringComparison.OrdinalIgnoreCase))
            return LaunchHandoff.Xbox;
        return null;
    }

    /// <summary>Words of the first simple command (see <see cref="SimpleCommands"/>).</summary>
    internal static List<string> ShellWords(string command)
    {
        var commands = SimpleCommands(command);
        return commands.Count == 0 ? [] : commands[0].Words;
    }

    /// <summary>
    /// POSIX shell simple commands with the separator after each (<c>;</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>,
    /// <c>&amp;</c>, newline, parentheses): single quotes literal, double quotes with backslash escapes, backslash
    /// outside quotes, <c>#</c> comments. Expansions are not performed (this only has to find the program and its
    /// arguments).
    /// </summary>
    internal static List<(List<string> Words, string Separator)> SimpleCommands(string command)
    {
        var commands = new List<(List<string>, string)>();
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;

        void EndWord()
        {
            if (inWord) words.Add(current.ToString());
            current.Clear();
            inWord = false;
        }

        void EndCommand(string separator)
        {
            EndWord();
            if (words.Count > 0) commands.Add((words, separator));
            words = [];
        }

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
                if (command[++i] != '\n') current.Append(command[i]);
            }
            else if (ch == '#' && !inWord)
            {
                while (i + 1 < command.Length && command[i + 1] != '\n') i++;
            }
            else if (ch is ';' or '&' or '|' or '\n' or '(' or ')')
            {
                var separator = ch.ToString();
                if (ch is '&' or '|' && i + 1 < command.Length && command[i + 1] == ch) separator += command[++i];
                EndCommand(separator);
            }
            else if (char.IsWhiteSpace(ch))
            {
                EndWord();
            }
            else
            {
                inWord = true;
                current.Append(ch);
            }
        }
        EndCommand(string.Empty);
        return commands;
    }
}
