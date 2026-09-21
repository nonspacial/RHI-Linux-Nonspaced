using System.Diagnostics;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Launch;

public sealed class LinuxGameLauncher : IGameLaunchOptionsLauncher
{
    public bool CanLaunch(DetectedGame game) => !string.IsNullOrWhiteSpace(game.LaunchCommand);

    public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => LaunchAsync(game, new GameLaunchOptions(), cancellationToken);

    public Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanLaunch(game)) throw new InvalidOperationException("No launch command is known for this game.");

        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (options.Environment is not null)
        {
            foreach (var pair in options.Environment)
                psi.Environment[pair.Key] = pair.Value;
        }
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(BuildShellCommand(game.LaunchCommand!, options.CommandPrefix));
        Process.Start(psi);
        return Task.CompletedTask;
    }

    internal static string BuildShellCommand(string launchCommand, string? commandPrefix)
    {
        if (string.IsNullOrWhiteSpace(commandPrefix)) return "exec " + launchCommand;

        // Keep the original launch string intact inside a child shell. This matters for commands
        // such as `WINEPREFIX=... wine game.exe`: placing Gamescope directly before the assignment
        // would make the assignment look like an executable after `--`.
        return $"exec {commandPrefix.Trim()} /bin/sh -lc {ShellQuote(launchCommand)}";
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
