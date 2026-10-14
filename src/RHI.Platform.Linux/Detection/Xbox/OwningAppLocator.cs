using System.Text.Json;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>
/// Finds how to open the app that owns an Xbox install (Refs #90; owner decision B): PartingX for its titles, the Xodus
/// GUI for Xodus-only installs. Play on such a card opens that app and never runs the game. Sources, in order:
/// a PartingX breadcrumb's launcher, the <c>run.sh</c> of a PartingX instance folder (the folder holding its
/// <c>state/partingx</c>), and, for the user's default state folder only, a desktop entry or a program on PATH (they
/// start PartingX with the default state, so they would open another instance for any other folder).
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

    /// <summary>How to open the PartingX instance in <paramref name="stateDir"/>.</summary>
    public string? PartingX(string stateDir, bool isDefaultStateDir, XboxBreadcrumb? breadcrumb)
    {
        if (breadcrumb?.Launcher is { } launcher && IsExecutableFile(launcher))
            return string.Join(' ', breadcrumb.LauncherArgs.Prepend(launcher).Select(Shell));
        if (InstanceScript(stateDir) is { } script) return Shell(script);
        if (!isDefaultStateDir) return null;
        return DesktopEntry(stem => stem.Contains("partingx", StringComparison.OrdinalIgnoreCase),
                   name => name.Equals("PartingX", StringComparison.OrdinalIgnoreCase))
               ?? OnPath("partingx", "PartingX");
    }

    /// <summary>How to open the Xodus GUI (upstream app id <c>io.github.xodus-gaming.xodus</c>); null while none is installed.</summary>
    public string? Xodus()
        => DesktopEntry(stem => stem.Equals("io.github.xodus-gaming.xodus", StringComparison.OrdinalIgnoreCase),
               name => name.Equals("Xodus", StringComparison.OrdinalIgnoreCase))
           ?? OnPath("xodus-gui");

    // <instance>/state/partingx → <instance>/run.sh, when it is an executable script that starts PartingX.
    private static string? InstanceScript(string stateDir)
    {
        var stateHome = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(stateDir));
        var instance = stateHome is null ? null : Path.GetDirectoryName(stateHome);
        if (instance is null || !Path.GetFileName(stateDir.TrimEnd('/')).Equals("partingx", StringComparison.Ordinal)) return null;
        var script = Path.Combine(instance, "run.sh");
        if (!IsExecutableFile(script)) return null;
        try
        {
            using var reader = new StreamReader(script);
            var buffer = new char[64 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains("PartingX", StringComparison.OrdinalIgnoreCase) ? script : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("scan", $"PartingX script {script} unreadable", ex);
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
                if (entry is null) continue;
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

    private static bool IsExecutableFile(string path)
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

/// <summary>
/// A PartingX instance announcing itself (owner decision C; the file PartingX is asked to write — format proposed here):
/// <c>$XDG_DATA_HOME/partingx/instances/*.json</c> = <c>{"schema":1,"stateDir":"/abs/state/partingx",
/// "launcher":"/abs/PartingX.AppImage or run.sh","launcherArgs":[]}</c>. <c>$XDG_DATA_HOME</c> because an instance that
/// moves its state (the acceptance <c>run.sh</c> sets <c>XDG_STATE_HOME</c>) still shares the user's data home. Unknown
/// fields are ignored; a file without an absolute <c>stateDir</c> is skipped.
/// </summary>
internal sealed record XboxBreadcrumb(string StateDir, string? Launcher, IReadOnlyList<string> LauncherArgs)
{
    public static IReadOnlyList<XboxBreadcrumb> ReadAll(string? directory)
    {
        var crumbs = new List<XboxBreadcrumb>();
        if (directory is null || !Directory.Exists(directory)) return crumbs;
        string[] files;
        try { files = Directory.GetFiles(directory, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("scan", $"PartingX instance list {directory} unreadable", ex);
            return crumbs;
        }
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(PartingXInstance.ReadSmall(file, 64 * 1024));
                var root = doc.RootElement;
                var state = PartingXInstance.String(root, "stateDir");
                if (state is null || !Path.IsPathRooted(state)) continue;
                var launcher = PartingXInstance.String(root, "launcher");
                var args = root.TryGetProperty("launcherArgs", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray()
                    : [];
                crumbs.Add(new XboxBreadcrumb(state, launcher is not null && Path.IsPathRooted(launcher) ? launcher : null, args));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warn("scan", $"PartingX instance file {file} unreadable", ex);
            }
        }
        return crumbs;
    }
}
