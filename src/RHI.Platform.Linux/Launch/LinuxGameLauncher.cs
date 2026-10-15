using System.Diagnostics;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Launch;

public sealed class LinuxGameLauncher : IGameLaunchOptionsLauncher
{
    /// <summary>Refusal when launch options are asked for on a store hand-off (fixed text; PT-BR presentation pair).</summary>
    public const string HandoffProfileRefused =
        "Launch profile not applied: a store launcher starts this game, so the profile has to be entered in that launcher.";

    /// <summary>How long a launch is watched for an immediate failure before it is left to run.</summary>
    public static readonly TimeSpan DefaultEarlyExitWindow = TimeSpan.FromSeconds(3);

    /// <summary>Per-launch stderr logs kept in the log folder; older ones are deleted when a launch starts.</summary>
    internal const int KeptLaunchLogs = 20;

    /// <summary>
    /// Size cap of one per-launch stderr log. A direct Wine launch prints <c>fixme:</c> lines for the whole
    /// session and a Steam client started by <c>steam -applaunch</c> keeps writing for days; past the cap
    /// the rest is read and discarded (never refused, so the game's writes keep succeeding).
    /// </summary>
    internal const long MaxLaunchLogBytes = 1024 * 1024;

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
    /// last stderr line. The command's stderr goes to a per-launch file (capped, see <see cref="WithCappedStderrLog"/>) under
    /// <see cref="DefaultLogDirectory"/>, never to a pipe: a pipe would tie the game to RHI's lifetime
    /// (every stderr write fails once RHI exits, and a <c>set -e</c> wrapper that logs to stderr aborts).
    /// A command still running after the window is left to run and its process handle is released when
    /// it ends.
    /// </summary>
    public async Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanLaunch(game)) throw new InvalidOperationException("No launch command is known for this game.");
        // Wrapping a store client (xdg-open heroic://…, steam -applaunch) never reaches the game it starts later,
        // so a launch profile on a hand-off would be reported as applied without effect (#46).
        if (((options.Environment?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(options.CommandPrefix)) &&
            LaunchHandoffClassifier.IsHandoff(game))
            throw new InvalidOperationException(HandoffProfileRefused);

        // The installer app's own Play entry point runs as an argument vector with no shell, so there is no stderr redirection to a
        // per-launch log: the installer app keeps its own logs, and an early failure is still reported by its exit code.
        var stderrLog = XboxEntryPoint(game) is null ? CreateLaunchLog(game) : null;
        await RunAndWatchAsync(BuildStartInfo(game, options, stderrLog), stderrLog).ConfigureAwait(false);
    }

    /// <summary>True when the card can open its title in the owning app (the installer app's <c>--show</c> entry point, Refs #90).</summary>
    public static bool CanShowInOwningApp(DetectedGame game) => game.XboxPackage?.ShowArgv is { Count: > 0 };

    /// <summary>
    /// "Show in the app that installed it" (Refs #90, spec item 3): executes the breadcrumb's <c>launch.argv</c> + <c>entryPoints.show</c>
    /// for the title, with <c>launch.env</c>, as an argument vector (no shell), watched for an early failure like Play.
    /// </summary>
    public async Task ShowInOwningAppAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (game.XboxPackage is not { ShowArgv: { Count: > 0 } argv } package)
            throw new InvalidOperationException("This card has no owning app to show it in.");
        await RunAndWatchAsync(ArgvStartInfo(argv, package.LaunchEnvironment), stderrLog: null).ConfigureAwait(false);
    }

    /// <summary>
    /// The card's own Play entry point of the installer app while it is the card's command (no Edit game command replaced it); null
    /// otherwise. Refs #90: Play = the installer app's <c>--play &lt;storeId&gt;</c>, its normal launch path.
    /// </summary>
    internal static (IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string>? Environment)? XboxEntryPoint(DetectedGame game)
        => game.XboxPackage is { PlayArgv: { Count: > 0 } argv } package && game.UserLaunchCommand is null &&
           string.Equals(package.OpenCommand, game.LaunchCommand, StringComparison.Ordinal)
            ? (argv, package.LaunchEnvironment)
            : null;

    /// <summary>An argument vector executed as is: <c>argv[0]</c> is the program, every other entry one argument, no shell.</summary>
    internal static ProcessStartInfo ArgvStartInfo(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? environment)
    {
        // A hand-off: the owning app picks the game's working directory itself.
        var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in argv.Skip(1)) psi.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment)
                psi.Environment[pair.Key] = pair.Value;
        return psi;
    }

    private async Task RunAndWatchAsync(ProcessStartInfo startInfo, string? stderrLog)
    {
        var process = new Process { StartInfo = startInfo };
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
        // A hand-off refuses launch options before this point, so the entry point runs exactly as the installer app announced it.
        if (XboxEntryPoint(game) is { } entry) return ArgvStartInfo(entry.Argv, entry.Environment);
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
        psi.ArgumentList.Add(stderrLog is null ? command : WithCappedStderrLog(command, stderrLog));
        return psi;
    }

    /// <summary>
    /// Sends the command's stderr to <paramref name="stderrLog"/> through a FIFO read by a small reader the
    /// launch shell starts (not RHI, so the game never depends on RHI's lifetime): it copies the first
    /// <see cref="MaxLaunchLogBytes"/>, appends a note when there is more, then drains the rest to
    /// /dev/null for as long as anything writes. The command runs in a subshell so an <c>exit</c> or
    /// <c>exec</c> in it still lets the shell close its own stderr and wait for the reader before exiting
    /// with the command's status: an early failure's last line is in the file before RHI reads it (when stdbuf is
    /// available the copy is unbuffered, so a line is there as soon as the reader has it; without stdbuf GNU head may
    /// hold the last line in its buffer while a background child keeps the FIFO open). That wait is bounded (<see cref="ReaderWaitTenths"/>): a background child the command started (a wrapper's
    /// daemon, the Steam client after <c>steam -applaunch</c>) keeps the FIFO open, so the reader only ends when
    /// that child does. The shell then exits with the command's status anyway, inside RHI's early-exit window and
    /// without lingering for the whole session; the reader stays behind, still draining the child's stderr.
    /// Without mkfifo the file is appended to directly, uncapped.
    /// </summary>
    internal static string WithCappedStderrLog(string command, string stderrLog)
    {
        var log = ShellQuote(stderrLog);
        var fifo = ShellQuote(stderrLog + ".fifo");
        return
            $"if mkfifo -m 600 {fifo} 2>/dev/null; then\n" +
            // GNU head writes through stdio: run under stdbuf when it is available, each line is in the file as soon as it
            // is read, even while a background child keeps the FIFO open (busybox head has no stdio buffer and no stdbuf).
            "  rhi_unbuffered=\n" +
            "  command -v stdbuf >/dev/null 2>&1 && rhi_unbuffered='stdbuf -o0'\n" +
            $"  {{ $rhi_unbuffered head -c {MaxLaunchLogBytes}; if [ \"$(head -c 1 | wc -c)\" -gt 0 ]; then " +
            $"printf '\\n[RHI] stderr log capped at {MaxLaunchLogBytes / (1024 * 1024)} MiB; later output was discarded.\\n'; cat >/dev/null; fi; }} " +
            $"<{fifo} >>{log} 2>/dev/null &\n" +
            "  rhi_stderr_reader=$!\n" +
            $"  exec 2>{fifo}\n" +
            $"  rm -f {fifo}\n" +
            "else\n" +
            $"  exec 2>>{log}\n" +
            "  rhi_stderr_reader=\n" +
            "fi\n" +
            $"(\n{command}\n)\n" +
            "rhi_status=$?\n" +
            "exec 2>&-\n" +
            "if [ -n \"$rhi_stderr_reader\" ]; then\n" +
            "  rhi_waited=0\n" +
            $"  while [ \"$rhi_waited\" -lt {ReaderWaitTenths} ]; do\n" +
            // Reader gone or a zombie (shells that reap lazily, e.g. dash, keep it as Z until `wait`).
            "    rhi_stat=\n" +
            "    read -r rhi_stat 2>/dev/null <\"/proc/$rhi_stderr_reader/stat\"\n" +
            "    case \"${rhi_stat##*) }\" in ''|Z*|X*) break ;; esac\n" +
            "    sleep 0.1\n" +
            "    rhi_waited=$((rhi_waited + 1))\n" +
            "  done\n" +
            "fi\n" +
            "exit $rhi_status";
    }

    /// <summary>
    /// How long, in tenths of a second, the launch shell waits for its stderr reader after the command ended: long
    /// enough for the last line to reach the file, well inside <see cref="DefaultEarlyExitWindow"/>.
    /// </summary>
    internal const int ReaderWaitTenths = 10;

    /// <summary>A launch command the user typed (Add game or Edit game), as opposed to one a detector built.</summary>
    internal static bool IsUserCommand(DetectedGame game)
        => game.IsManuallyAdded || string.Equals(game.Source, "Manual", StringComparison.OrdinalIgnoreCase)
           || game.UserLaunchCommand is not null;

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
        if (LaunchHandoffClassifier.IsHandoff(game)) return null;

        var exeDirectory = string.IsNullOrWhiteSpace(game.LaunchExecutablePath)
            ? null
            : Path.GetDirectoryName(game.LaunchExecutablePath);
        if (!string.IsNullOrWhiteSpace(exeDirectory) && Directory.Exists(exeDirectory)) return exeDirectory;
        return !string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath)
            ? game.InstallPath
            : null;
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
            // A FIFO is unlinked as soon as both ends are open; one is left only by a shell killed in between.
            foreach (var fifo in Directory.EnumerateFiles(_logDirectory, "*.log.fifo")
                         .Where(fifo => File.GetLastWriteTimeUtc(fifo) < DateTime.UtcNow.AddHours(-1)))
                RHI.Core.Deployment.FileCleanup.TryDelete(fifo);
            var path = Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{FileSafe(game.Name)}-{Guid.NewGuid():N}.log");
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("launch", $"no stderr log in {_logDirectory}; a failed launch is reported without its stderr", ex);
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
            AppLog.Warn("launch", $"stderr log {stderrLog} unreadable", ex);
            return null;
        }
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
