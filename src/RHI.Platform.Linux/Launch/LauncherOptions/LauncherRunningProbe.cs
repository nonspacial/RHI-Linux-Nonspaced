namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>Whether a launcher is running (it would write its own copy of its settings over RHI's change).</summary>
public interface ILauncherRunningProbe
{
    bool IsRunning(LauncherKind kind);
}

/// <summary>
/// #318: finds a running launcher in <c>/proc</c> (native and Flatpak alike: a sandboxed process is still listed on the host).
/// Steam keeps localconfig.vdf in memory and writes it back when it exits; Lutris and Heroic save a game's whole settings
/// file from what they loaded. A process counts by its name (<c>comm</c>) or, for Lutris run by a Python interpreter, by the
/// script it runs.
/// </summary>
public sealed class ProcLauncherRunningProbe(string procRoot = "/proc") : ILauncherRunningProbe
{
    private static readonly Dictionary<LauncherKind, string[]> Names = new()
    {
        [LauncherKind.Steam] = ["steam"],
        [LauncherKind.Lutris] = ["lutris"],
        [LauncherKind.Heroic] = ["heroic", "heroic-games-launcher"],
    };

    private static readonly string[] Interpreters = ["python", "python3"];

    public bool IsRunning(LauncherKind kind)
    {
        var names = Names[kind];
        IEnumerable<string> processes;
        try { processes = Directory.EnumerateDirectories(procRoot).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        foreach (var process in processes)
        {
            if (!int.TryParse(Path.GetFileName(process), out _)) continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(process, "comm")).Trim();
                if (names.Contains(comm, StringComparer.Ordinal)) return true;
                if (!Interpreters.Any(p => comm.StartsWith(p, StringComparison.Ordinal))) continue;
                var argv = File.ReadAllText(Path.Combine(process, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (argv.Skip(1).Take(2).Any(a => names.Contains(Path.GetFileName(a), StringComparer.Ordinal))) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // exited meanwhile, or another user's process
            }
        }
        return false;
    }
}
