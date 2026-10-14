using System.Text.Json;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>
/// Finds how to open the app that owns an Xbox install when the installer app's breadcrumb gives no entry point (Refs #90; spec
/// item 4: a card without a breadcrumb-backed instance opens the owning app if found, else Play is disabled). Sources,
/// in order: the <c>run.sh</c> of an installer-app instance folder (the folder holding its <c>state/{app}</c>), and, for
/// the user's default state folder only, the installer app's own desktop entry or a program on PATH (they start the installer app with
/// the default state, so they would open another instance for any other folder). The Xodus GUI for Xodus-only
/// installs. Desktop entries that start one title (a per-game shortcut) are never taken for the app.
/// </summary>
internal sealed class OwningAppLocator(IReadOnlyList<string> applicationDirs, IReadOnlyList<string> pathDirs)
{
    public static OwningAppLocator FromEnvironment(string home, string dataHome)
    {
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        var applications = new[] { dataHome, Path.Combine(dataHome, "flatpak", "exports", "share"), "/var/lib/flatpak/exports/share" }
            .Concat(dataDirs)
            .Select(dir => Path.Combine(dir, "applications"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(home, ".local", "bin"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new OwningAppLocator(applications, path);
    }

    /// <summary>How to open the installer app's instance in <paramref name="stateDir"/> without its breadcrumb.</summary>
    public string? InstallerApp(string stateDir, bool isDefaultStateDir)
    {
        if (InstanceScript(stateDir) is { } script) return Shell(script);
        if (!isDefaultStateDir) return null;
        // The installer app's own entry: its id ({app}, or an app id ending in .{app}) or exactly its name.
        return DesktopEntry(stem => stem.Equals(XboxInstallerApp.FileTag, StringComparison.OrdinalIgnoreCase) ||
                                    stem.EndsWith("." + XboxInstallerApp.FileTag, StringComparison.OrdinalIgnoreCase),
                   name => name.Equals(XboxInstallerApp.ProductTag, StringComparison.Ordinal))
               ?? OnPath(XboxInstallerApp.FileTag, XboxInstallerApp.ProductTag);
    }

    /// <summary>
    /// An argument vector and its environment as one shell line (<c>env 'K=V' … 'argv0' 'arg' …</c>), for display and
    /// for Edit game's detected launch: typed back as a command it does the same. RHI itself executes the vector.
    /// <c>env</c> reads a word starting with <c>-</c> as an option and any word holding <c>=</c> as an assignment, so a
    /// variable name starting with <c>-</c> gets <c>--</c> before the assignments, and a program path holding <c>=</c>
    /// is started through <c>sh -c 'exec "$0" "$@"'</c> (scan re-review nit 2).
    /// </summary>
    public static string Render(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? environment)
    {
        var words = new List<string>();
        if (environment is { Count: > 0 })
        {
            words.Add("env");
            if (environment.Keys.Any(name => name.StartsWith('-'))) words.Add("--");
            words.AddRange(environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => Shell($"{pair.Key}={pair.Value}")));
            if (argv.Count > 0 && argv[0].Contains('=')) words.AddRange(["sh", "-c", Shell("exec \"$0\" \"$@\"")]);
        }
        words.AddRange(argv.Select(Shell));
        return string.Join(' ', words);
    }

    /// <summary>How to open the Xodus GUI (upstream app id <c>io.github.xodus-gaming.xodus</c>); null while none is installed.</summary>
    public string? Xodus()
        => DesktopEntry(stem => stem.Equals("io.github.xodus-gaming.xodus", StringComparison.OrdinalIgnoreCase),
               name => name.Equals("Xodus", StringComparison.OrdinalIgnoreCase))
           ?? OnPath("xodus-gui");

    // <instance>/state/{app} → <instance>/run.sh, when it is an executable script that starts the installer app.
    private static string? InstanceScript(string stateDir)
    {
        var stateHome = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(stateDir));
        var instance = stateHome is null ? null : Path.GetDirectoryName(stateHome);
        if (instance is null || !Path.GetFileName(stateDir.TrimEnd('/')).Equals(XboxInstallerApp.FolderName, StringComparison.Ordinal)) return null;
        var script = Path.Combine(instance, "run.sh");
        if (!IsExecutableFile(script)) return null;
        try
        {
            using var reader = new StreamReader(script);
            var buffer = new char[64 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains(XboxInstallerApp.ProductTag, StringComparison.OrdinalIgnoreCase) ? script : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("scan", $"Xbox app script {script} unreadable", ex);
            return null;
        }
    }

    private string? DesktopEntry(Func<string, bool> stemMatches, Func<string, bool> nameMatches)
    {
        foreach (var dir in applicationDirs.Where(Directory.Exists))
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.desktop"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("scan", $"Application folder {dir} unreadable", ex);
                continue;
            }
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                var entry = ReadDesktopEntry(file);
                if (entry is null || StartsOneTitle(entry.Value.Exec)) continue;
                if (stemMatches(Path.GetFileNameWithoutExtension(file)) || nameMatches(entry.Value.Name))
                    return entry.Value.Exec;
            }
        }
        return null;
    }

    /// <summary>Name and Exec (field codes removed) of a visible application entry.</summary>
    internal static (string Name, string Exec)? ReadDesktopEntry(string file)
    {
        string[] lines;
        try { lines = new FileInfo(file).Length > 256 * 1024 ? [] : File.ReadAllLines(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("scan", $"Desktop entry {file} unreadable", ex);
            return null;
        }
        string? name = null, exec = null;
        var inMain = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inMain = line == "[Desktop Entry]"; continue; }
            if (!inMain) continue;
            if (line.StartsWith("Name=", StringComparison.Ordinal)) name ??= line[5..].Trim();
            else if (line.StartsWith("Exec=", StringComparison.Ordinal)) exec ??= line[5..].Trim();
            else if (line is "Hidden=true") return null;
        }
        if (name is null || string.IsNullOrWhiteSpace(exec)) return null;
        var command = System.Text.RegularExpressions.Regex.Replace(exec, "%[fFuUdDnNickvm]", string.Empty).Replace("%%", "%").Trim();
        return command.Length == 0 ? null : (name, command);
    }

    /// <summary>
    /// A shortcut that starts one game (the installer app's <c>--play</c>/<c>--show</c>/<c>--launch</c>, a store id, a
    /// <c>{app}://</c> or <c>xodus://</c> link with a target, RHI's own <c>--launch</c> shortcut): never the app's
    /// own entry, and taken for it, Play on every card would start that one game (w3-scan review minor 8).
    /// </summary>
    internal static bool StartsOneTitle(string exec)
        => exec.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('"', '\''))
            .Any(word => word is "--play" or "--show" or "--launch" ||
                         word.StartsWith("--play=", StringComparison.Ordinal) || word.StartsWith("--show=", StringComparison.Ordinal) ||
                         word.StartsWith("--launch=", StringComparison.Ordinal) ||
                         System.Text.RegularExpressions.Regex.IsMatch(word, "^(?=.*[0-9])[0-9A-Z]{12}$") ||
                         System.Text.RegularExpressions.Regex.IsMatch(word, $"^({XboxInstallerApp.FileTag}|xodus)://.+", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private string? OnPath(params string[] programs)
    {
        foreach (var dir in pathDirs)
            foreach (var program in programs)
            {
                var candidate = Path.Combine(dir, program);
                if (IsExecutableFile(candidate)) return Shell(candidate);
            }
        return null;
    }

    internal static bool IsExecutableFile(string path)
    {
        try
        {
            return File.Exists(path) &&
                   (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("scan", $"{path} unreadable", ex);
            return false;
        }
    }

    internal static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
