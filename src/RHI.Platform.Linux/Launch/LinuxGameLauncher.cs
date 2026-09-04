using System.Diagnostics;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Launch;

public sealed class LinuxGameLauncher : IGameLauncher
{
    public bool CanLaunch(DetectedGame game) => !string.IsNullOrWhiteSpace(game.LaunchCommand);

    public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanLaunch(game)) throw new InvalidOperationException("No launch command is known for this game.");

        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("exec " + game.LaunchCommand!);
        Process.Start(psi);
        return Task.CompletedTask;
    }
}
