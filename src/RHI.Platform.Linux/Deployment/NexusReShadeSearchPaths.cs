using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>Nexus materializes a temporary launch chain, so ReShade cannot resolve payload-relative shader paths there.</summary>
internal static class NexusReShadeSearchPaths
{
    /// <summary>The persistent payload directory for add-ons, rather than Nexus's temporary launch chain.</summary>
    internal static string? AddonPathForGame(DetectedGame game)
    {
        if (!string.Equals(game.Runner, "Nexus Launcher", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(game.PrefixPath)
            || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return null;

        return XnaGacReShade.WindowsPathOf(game.PrefixPath, game.BinaryDirectory);
    }

    internal static IReadOnlyList<string> ForGame(DetectedGame game, IReadOnlyList<string> paths)
    {
        if (!string.Equals(game.Runner, "Nexus Launcher", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(game.PrefixPath)
            || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return paths;

        return paths.Select(path =>
        {
            if (!path.StartsWith(@".\", StringComparison.Ordinal)) return path;
            var payloadPath = Path.Combine(game.BinaryDirectory, path[2..].Replace('\\', Path.DirectorySeparatorChar));
            return XnaGacReShade.WindowsPathOf(game.PrefixPath, payloadPath);
        }).ToArray();
    }
}
