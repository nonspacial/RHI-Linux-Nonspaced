using System.Text.RegularExpressions;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Detection;

internal static partial class DetectionHelpers
{
    [GeneratedRegex("\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"")]
    private static partial Regex VdfPairRegex();

    public static Dictionary<string, string> ParseFlatVdf(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPairRegex().Matches(text))
            result[m.Groups["key"].Value] = m.Groups["value"].Value.Replace("\\\\", "\\");
        return result;
    }

    public static string? FindLikelyExecutable(string root)
    {
        if (!Directory.Exists(root)) return null;
        var ignore = new[] { "unins", "crash", "report", "setup", "install", "redist", "vcredist", "unitycrashhandler" };
        var candidates = new List<(string path, long size, int depth)>();
        var baseDepth = Depth(root);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && candidates.Count < 250)
        {
            var dir = pending.Pop();
            var depth = Depth(dir) - baseDepth;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    var name = Path.GetFileName(file);
                    if (ignore.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { }
                    candidates.Add((file, size, depth));
                }
            }
            catch { }
            if (depth >= 4) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var n = Path.GetFileName(sub);
                    if (n.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("Engine", StringComparison.OrdinalIgnoreCase) && depth >= 2)
                        continue;
                    pending.Push(sub);
                }
            }
            catch { }
        }

        return candidates
            .OrderBy(x => x.depth)
            .ThenByDescending(x => x.size)
            .Select(x => x.path)
            .FirstOrDefault();
    }

    public static GraphicsApi GuessGraphicsApi(string root)
    {
        if (!Directory.Exists(root)) return GraphicsApi.Unknown;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly))
                names.Add(Path.GetFileName(file));
        }
        catch { }

        if (names.Contains("d3d12.dll") || names.Contains("dxgi.dll") && names.Any(n => n.Contains("streamline", StringComparison.OrdinalIgnoreCase)))
            return GraphicsApi.DirectX12;
        if (names.Contains("d3d11.dll")) return GraphicsApi.DirectX11;
        if (names.Contains("d3d9.dll")) return GraphicsApi.DirectX9;
        if (names.Contains("vulkan-1.dll")) return GraphicsApi.Vulkan;
        return GraphicsApi.Unknown;
    }

    private static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);
}
