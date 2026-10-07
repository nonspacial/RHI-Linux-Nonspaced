using System.Diagnostics;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Launch;

public sealed class LinuxGameLauncher : IGameLaunchOptionsLauncher
{
    // Programs that hand the launch to a store client instead of running a local executable.
    private static readonly string[] StoreHandoffPrograms = ["steam", "lutris", "heroic", "xdg-open", "gio", "flatpak"];

    /// <summary>How long a launch is watched for an immediate failure before it is left to run.</summary>
    public static readonly TimeSpan DefaultEarlyExitWindow = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _earlyExitWindow;

    public LinuxGameLauncher() : this(DefaultEarlyExitWindow) { }

    internal LinuxGameLauncher(TimeSpan earlyExitWindow) => _earlyExitWindow = earlyExitWindow;

    public bool CanLaunch(DetectedGame game) => !string.IsNullOrWhiteSpace(game.LaunchCommand);

    public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => LaunchAsync(game, new GameLaunchOptions(), cancellationToken);

    /// <summary>
    /// Starts the launch command and watches it briefly: a command that ends at once with a failure exit
    /// code (program missing, store client refused) throws <see cref="GameLaunchFailedException"/> with its
    /// last stderr line. A command still running after the window is left to run; its stderr keeps being
    /// drained (so a full pipe never blocks it) and the process handle is released when it ends.
    /// </summary>
    public async Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanLaunch(game)) throw new InvalidOperationException("No launch command is known for this game.");

        var startInfo = BuildStartInfo(game, options);
        startInfo.RedirectStandardError = true;
        var process = new Process { StartInfo = startInfo };
        var errors = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                errors.Enqueue(e.Data.Trim());
                while (errors.Count > 20) errors.Dequeue();
            }
        };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
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
        string? detail;
        using (process)
        {
            await exited.ConfigureAwait(false); // also waits for stderr to be read to the end
            exitCode = process.ExitCode;
            lock (errors) detail = errors.LastOrDefault();
        }
        if (exitCode != 0) throw new GameLaunchFailedException(exitCode, detail);
    }

    internal static ProcessStartInfo BuildStartInfo(DetectedGame game, GameLaunchOptions options)
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
        // Not a login shell: the desktop session already has the user's environment, and sourcing the
        // login profiles on every launch is slow and can print into (or change) the launch.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(BuildShellCommand(game.LaunchCommand!, options.CommandPrefix));
        return psi;
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

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
