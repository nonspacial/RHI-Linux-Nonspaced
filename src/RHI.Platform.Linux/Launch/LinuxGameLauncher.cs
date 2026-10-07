using System.Diagnostics;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Launch;

public sealed class LinuxGameLauncher : IGameLaunchOptionsLauncher
{
    // Programs that hand the launch to a store client instead of running a local executable.
    private static readonly string[] StoreHandoffPrograms = ["steam", "lutris", "heroic", "xdg-open", "gio", "flatpak"];

    /// <summary>How long a launch is watched for an immediate failure before it is left to run.</summary>
    public static readonly TimeSpan DefaultEarlyExitWindow = TimeSpan.FromSeconds(3);

    /// <summary>Per-launch stderr logs kept in the log folder; older ones are deleted when a launch starts.</summary>
    internal const int KeptLaunchLogs = 20;

    private readonly TimeSpan _earlyExitWindow;
    private readonly string _logDirectory;

    public LinuxGameLauncher() : this(DefaultEarlyExitWindow) { }

    internal LinuxGameLauncher(TimeSpan earlyExitWindow, string? logDirectory = null)
    {
        _earlyExitWindow = earlyExitWindow;
        _logDirectory = logDirectory ?? DefaultLogDirectory;
    }

    /// <summary><c>$XDG_STATE_HOME/rhi-linux/logs/launch</c>: one stderr file per launch.</summary>
    internal static string DefaultLogDirectory => Path.Combine(LinuxPaths.LogDirectory, "launch");

    public bool CanLaunch(DetectedGame game) => !string.IsNullOrWhiteSpace(game.LaunchCommand);

    public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => LaunchAsync(game, new GameLaunchOptions(), cancellationToken);

    /// <summary>
    /// Starts the launch command and watches it briefly: a command that ends at once with a failure exit
    /// code (program missing, store client refused) throws <see cref="GameLaunchFailedException"/> with its
    /// last stderr line. The command's stderr goes to a per-launch file under
    /// <see cref="DefaultLogDirectory"/>, never to a pipe: a pipe would tie the game to RHI's lifetime
    /// (every stderr write fails once RHI exits, and a <c>set -e</c> wrapper that logs to stderr aborts).
    /// A command still running after the window is left to run and its process handle is released when
    /// it ends.
    /// </summary>
    public async Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanLaunch(game)) throw new InvalidOperationException("No launch command is known for this game.");

        var stderrLog = CreateLaunchLog(game);
        var process = new Process { StartInfo = BuildStartInfo(game, options, stderrLog) };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        var exited = process.WaitForExitAsync(CancellationToken.None);
        if (await Task.WhenAny(exited, Task.Delay(_earlyExitWindow, CancellationToken.None)).ConfigureAwait(false) != exited)
        {
            _ = exited.ContinueWith(_ => process.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        int exitCode;
        using (process)
        {
            await exited.ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        if (exitCode != 0) throw new GameLaunchFailedException(exitCode, LastLine(stderrLog));
    }

    internal static ProcessStartInfo BuildStartInfo(DetectedGame game, GameLaunchOptions options, string? stderrLog = null)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Wine maps the Unix cwd to the Windows current directory; without this a direct launch
            // inherits RHI's cwd and games that load data relative to it fail (upstream 2.7.9).
            WorkingDirectory = ResolveWorkingDirectory(game) ?? string.Empty
        };
        if (options.Environment is not null)
        {
            foreach (var pair in options.Environment)
                psi.Environment[pair.Key] = pair.Value;
        }

        // Commands RHI builds (store hand-offs, direct Wine launches) name absolute paths or programs in
        // the session PATH, so they skip the login profiles: sourcing them on every launch is slow and can
        // print into (or change) the launch. A command the user typed runs the way their terminal runs it:
        // a login shell keeps what ~/.profile sets up (PATH additions, exported variables), which a session
        // started from the desktop menu often lacks, and ~/.local/bin and ~/bin are on PATH even when the
        // profile does not add them.
        var userCommand = IsUserCommand(game);
        if (userCommand) AddUserBinDirectories(psi);
        psi.ArgumentList.Add(userCommand ? "-lc" : "-c");
        var command = BuildShellCommand(game.LaunchCommand!, options.CommandPrefix);
        psi.ArgumentList.Add(stderrLog is null ? command : $"exec 2>>{ShellQuote(stderrLog)}\n{command}");
        return psi;
    }

    /// <summary>A launch command the user typed (Add game dialog), as opposed to one a detector built.</summary>
    internal static bool IsUserCommand(DetectedGame game)
        => game.IsManuallyAdded || string.Equals(game.Source, "Manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts <c>~/.local/bin</c> and <c>~/bin</c> (when they exist) in front of PATH unless PATH already
    /// has them, so a wrapper script the user installed there runs by its bare name even when their
    /// ~/.profile does not add it (several distributions' default profiles do not).
    /// </summary>
    private static void AddUserBinDirectories(ProcessStartInfo psi)
    {
        var home = psi.Environment.TryGetValue("HOME", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : LinuxPaths.Home;
        if (string.IsNullOrWhiteSpace(home)) return;
        var path = psi.Environment.TryGetValue("PATH", out var current) ? current ?? string.Empty : string.Empty;
        var entries = path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.TrimEnd('/'))
            .ToHashSet(StringComparer.Ordinal);
        var missing = new[] { Path.Combine(home, ".local", "bin"), Path.Combine(home, "bin") }
            .Where(dir => Directory.Exists(dir) && !entries.Contains(dir.TrimEnd('/')))
            .ToArray();
        if (missing.Length == 0) return;
        psi.Environment["PATH"] = string.Join(':', missing) + (path.Length > 0 ? ":" + path : string.Empty);
    }

    /// <summary>
    /// Launch executable's directory (else the install folder) for direct Wine/manual launches; null for
    /// store hand-offs, whose client picks the game's working directory itself.
    /// </summary>
    internal static string? ResolveWorkingDirectory(DetectedGame game)
    {
        if (IsStoreHandoff(game)) return null;

        var exeDirectory = string.IsNullOrWhiteSpace(game.LaunchExecutablePath)
            ? null
            : Path.GetDirectoryName(game.LaunchExecutablePath);
        if (!string.IsNullOrWhiteSpace(exeDirectory) && Directory.Exists(exeDirectory)) return exeDirectory;
        return !string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath)
            ? game.InstallPath
            : null;
    }

    private static bool IsStoreHandoff(DetectedGame game)
    {
        var source = game.Source ?? string.Empty;
        if (source.Equals("Steam", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("Lutris", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("Heroic", StringComparison.OrdinalIgnoreCase)) return true;

        var command = game.LaunchCommand ?? string.Empty;
        if (command.Contains("://", StringComparison.Ordinal)) return true;
        // First word that is not a leading NAME=value assignment. Quoting is ignored: this only has to
        // recognise a bare store client name.
        var program = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(word => word.IndexOf('=') <= 0 || word.StartsWith('/') || word.StartsWith('\''));
        return program is not null &&
               StoreHandoffPrograms.Contains(Path.GetFileName(program), StringComparer.OrdinalIgnoreCase);
    }

    internal static string BuildShellCommand(string launchCommand, string? commandPrefix)
    {
        // No leading `exec`: `exec WINEPREFIX=... wine game.exe` treats the assignment as the program
        // and the launch fails; bash still execs a lone simple command itself.
        if (string.IsNullOrWhiteSpace(commandPrefix)) return launchCommand;

        // Keep the original launch string intact inside a child shell. This matters for commands
        // such as `WINEPREFIX=... wine game.exe`: placing Gamescope directly before the assignment
        // would make the assignment look like an executable after `--`.
        return $"exec {commandPrefix.Trim()} /bin/sh -c {ShellQuote(launchCommand)}";
    }

    /// <summary>
    /// A fresh, empty stderr file for this launch (the oldest logs beyond <see cref="KeptLaunchLogs"/> are
    /// deleted first); null when the log folder cannot be written, and the launch then keeps RHI's own
    /// stderr (still no pipe) and a failure is reported without its last line.
    /// </summary>
    private string? CreateLaunchLog(DetectedGame game)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            foreach (var old in Directory.EnumerateFiles(_logDirectory, "*.log")
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                         .Skip(KeptLaunchLogs - 1))
                File.Delete(old);
            var path = Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{FileSafe(game.Name)}-{Guid.NewGuid():N}.log");
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[RHI launch] no stderr log in {_logDirectory} ({ex.Message}); a failed launch is reported without its stderr");
            return null;
        }
    }

    private static string FileSafe(string? name)
    {
        var builder = new StringBuilder();
        foreach (var ch in name ?? string.Empty)
            builder.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        var text = builder.ToString().Trim('_');
        return text.Length == 0 ? "game" : text.Length > 60 ? text[..60] : text;
    }

    /// <summary>Last non-empty line of the launch's stderr file (its final 64 KiB).</summary>
    private static string? LastLine(string? stderrLog)
    {
        if (stderrLog is null) return null;
        try
        {
            using var stream = new FileStream(stderrLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Max(0, stream.Length - 64 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd()
                .Split('\n')
                .Select(line => line.Trim())
                .LastOrDefault(line => line.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[RHI launch] stderr log {stderrLog} unreadable ({ex.Message})");
            return null;
        }
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
