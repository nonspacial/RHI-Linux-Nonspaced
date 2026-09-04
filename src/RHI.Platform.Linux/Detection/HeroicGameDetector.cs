using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Tolerant Heroic detector. Heroic has changed its cache schema several times, so this
/// intentionally walks JSON objects looking for install_path/installPath + title/appName.
/// </summary>
public sealed class HeroicGameDetector : IGameDetector
{
    public string Name => "Heroic";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var result = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var heroicRoot in LinuxPaths.HeroicRoots())
        {
            var jsonFiles = SafeJsonFiles(heroicRoot);
            foreach (var file in jsonFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    Visit(doc.RootElement, result);
                }
                catch { }
            }

            // Prefix folders are useful even if the installed-game cache schema changes.
            var prefixRoot = Path.Combine(heroicRoot, "Prefixes");
            if (!Directory.Exists(prefixRoot))
                prefixRoot = Path.Combine(LinuxPaths.Home, "Games", "Heroic", "Prefixes");
            if (Directory.Exists(prefixRoot))
                AttachPrefixes(result.Values, prefixRoot);
        }
        return result.Values.ToArray();
    }

    private static IEnumerable<string> SafeJsonFiles(string root)
    {
        var likely = new[] { "installed.json", "store_cache", "gog_store", "legendaryConfig", "nile_config" };
        foreach (var key in likely)
        {
            var path = Path.Combine(root, key);
            if (File.Exists(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) yield return path;
            if (!Directory.Exists(path)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files.Take(200)) yield return file;
        }
    }

    private static void Visit(JsonElement element, IDictionary<string, DetectedGame> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var props = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var install = StringValue(props, "install_path", "installPath", "installLocation", "path");
            var name = StringValue(props, "title", "name", "app_title", "appName");
            if (!string.IsNullOrWhiteSpace(install) && !string.IsNullOrWhiteSpace(name) && Directory.Exists(install))
            {
                var source = StringValue(props, "platform", "store", "runner") ?? "Heroic";
                var appName = StringValue(props, "app_name", "appName", "app_name_string");
                var game = new DetectedGame
                {
                    Name = name,
                    InstallPath = install,
                    Source = $"Heroic/{source}",
                    EpicAppName = appName,
                    ExecutablePath = DetectionHelpers.FindLikelyExecutable(install),
                    Runner = "Wine/Proton (Heroic)",
                    GraphicsApi = DetectionHelpers.GuessGraphicsApi(install),
                    LaunchCommand = !string.IsNullOrWhiteSpace(appName)
                        ? $"xdg-open {EscapeShell(BuildHeroicUri(appName, StringValue(props, "runner", "store") ?? source))}"
                        : null
                };
                result[Path.GetFullPath(install)] = game;
            }
            foreach (var p in props.Values) Visit(p, result);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Visit(item, result);
        }
    }

    private static string? StringValue(Dictionary<string, JsonElement> props, params string[] keys)
    {
        foreach (var key in keys)
            if (props.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static void AttachPrefixes(IEnumerable<DetectedGame> games, string prefixRoot)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(prefixRoot); } catch { return; }
        foreach (var game in games)
        {
            var normalized = Normalize(game.Name);
            var best = dirs
                .Select(d => (path: d, score: Similarity(normalized, Normalize(Path.GetFileName(d)))))
                .OrderByDescending(x => x.score)
                .FirstOrDefault();
            if (best.score >= 0.70)
                game.PrefixPath = FindWinePrefix(best.path);
        }
    }

    private static string FindWinePrefix(string root)
    {
        if (Directory.Exists(Path.Combine(root, "drive_c"))) return root;
        var pfx = Path.Combine(root, "pfx");
        return Directory.Exists(Path.Combine(pfx, "drive_c")) ? pfx : root;
    }

    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a)) return (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        var common = a.Intersect(b).Count();
        return (double)common / Math.Max(a.Length, b.Length);
    }
    private static string BuildHeroicUri(string appName, string runner)
        => $"heroic://launch?appName={Uri.EscapeDataString(appName)}&runner={Uri.EscapeDataString(runner)}";

    private static string EscapeShell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
