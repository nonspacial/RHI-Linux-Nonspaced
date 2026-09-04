using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

public sealed partial class LutrisGameDetector : IGameDetector
{
    public string Name => "Lutris";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private static IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var games = new List<DetectedGame>();
        foreach (var configRoot in LinuxPaths.LutrisConfigRoots())
        {
            foreach (var file in Directory.EnumerateFiles(configRoot, "*.yml").Concat(Directory.EnumerateFiles(configRoot, "*.yaml")))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = File.ReadAllText(file);
                    var name = MatchValue(text, "name") ?? Path.GetFileNameWithoutExtension(file).Replace('-', ' ');
                    var exe = MatchValue(text, "exe");
                    var prefix = MatchValue(text, "prefix");
                    exe = Expand(exe);
                    prefix = Expand(prefix);
                    string? install = exe is null ? null : Path.GetDirectoryName(exe);
                    if (install is null || !Directory.Exists(install))
                    {
                        if (prefix is not null && Directory.Exists(prefix)) install = prefix;
                        else continue;
                    }
                    var slug = Path.GetFileNameWithoutExtension(file);
                    games.Add(new DetectedGame
                    {
                        Name = name,
                        InstallPath = install,
                        Source = "Lutris",
                        ExecutablePath = File.Exists(exe) ? exe : DetectionHelpers.FindLikelyExecutable(install),
                        PrefixPath = Directory.Exists(prefix) ? prefix : null,
                        Runner = MatchValue(text, "runner") ?? "Lutris",
                        LaunchCommand = "lutris " + Shell($"lutris:rungame/{slug}"),
                        GraphicsApi = DetectionHelpers.GuessGraphicsApi(install)
                    });
                }
                catch { }
            }
        }
        return games;
    }

    private static string? MatchValue(string text, string key)
    {
        var pattern = $@"(?m)^\s*{Regex.Escape(key)}\s*:\s*[""']?(?<v>[^\r\n""']+)";
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string? Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("~/")) return Path.Combine(LinuxPaths.Home, value[2..]);
        return Environment.ExpandEnvironmentVariables(value);
    }
}
