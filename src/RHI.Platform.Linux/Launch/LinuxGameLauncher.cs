using System.Diagnostics;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Launch;

public sealed class LinuxGameLauncher : IGameLaunchOptionsLauncher
{
    // Programs that hand the launch to a store client instead of running a local executable.
    private static readonly string[] StoreHandoffPrograms = ["steam", "lutris", "heroic", "xdg-open", "gio"];

    public bool CanLaunch(DetectedGame game) => !string.IsNullOrWhiteSpace(game.LaunchCommand);

    public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => LaunchAsync(game, new GameLaunchOptions(), cancellationToken);

    public Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanLaunch(game)) throw new InvalidOperationException("No launch command is known for this game.");

        Process.Start(BuildStartInfo(game, options));
        return Task.CompletedTask;
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
        psi.ArgumentList.Add("-lc");
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
        return $"exec {commandPrefix.Trim()} /bin/sh -lc {ShellQuote(launchCommand)}";
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
