using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// Conservative local component detection. It reports what is actually present and never
/// claims that a Windows-only driver override is available on Linux.
/// </summary>
public static class ComponentProbe
{
    public static IReadOnlyList<ManagedComponent> Probe(DetectedGame game)
    {
        var root = game.InstallPath;
        var files = SafeEnumerateFiles(root, maxDepth: 3).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new[]
        {
            Make(ManagedComponentKind.ReShade, "ReShade", files.Contains("ReShade64.dll") || files.Contains("dxgi.dll") || files.Contains("d3d11.dll")),
            Make(ManagedComponentKind.RenoDX, "RenoDX", files.Any(f => f?.Contains("renodx", StringComparison.OrdinalIgnoreCase) == true)),
            Make(ManagedComponentKind.OptiScaler, "OptiScaler", files.Any(f => f?.Contains("optiscaler", StringComparison.OrdinalIgnoreCase) == true)),
            Make(ManagedComponentKind.LumaFramework, "Luma Framework", files.Any(f => f?.Contains("luma", StringComparison.OrdinalIgnoreCase) == true)),
            Make(ManagedComponentKind.REFramework, "RE Framework", files.Contains("dinput8.dll") && files.Any(f => f?.Contains("reframework", StringComparison.OrdinalIgnoreCase) == true)),
            Make(ManagedComponentKind.Dlss, "DLSS", files.Contains("nvngx_dlss.dll")),
            Make(ManagedComponentKind.Streamline, "Streamline", files.Contains("sl.interposer.dll") || files.Contains("sl.common.dll")),
            Make(ManagedComponentKind.DXVK, "DXVK", false, "For Proton games DXVK is normally supplied by the runner, not copied into the game directory."),
            Make(ManagedComponentKind.ReLimiter, "ReLimiter", files.Any(f => f?.Contains("relimiter", StringComparison.OrdinalIgnoreCase) == true)),
            Make(ManagedComponentKind.DisplayCommander, "Display Commander", files.Any(f => f?.Contains("displaycommander", StringComparison.OrdinalIgnoreCase) == true)),
            Make(ManagedComponentKind.DofFix, "DOF Fix", files.Any(f => f?.Contains("dof", StringComparison.OrdinalIgnoreCase) == true)),
            Make(ManagedComponentKind.RenoDXUpgrade, "RenoDX Upgrade", files.Any(f => f?.Contains("upgrade", StringComparison.OrdinalIgnoreCase) == true && f.Contains("renodx", StringComparison.OrdinalIgnoreCase))),
        };
    }

    private static ManagedComponent Make(ManagedComponentKind kind, string name, bool installed, string? note = null) => new()
    {
        Kind = kind,
        DisplayName = name,
        Availability = installed ? ComponentAvailability.Installed : ComponentAvailability.Available,
        Note = note
    };

    private static IEnumerable<string> SafeEnumerateFiles(string root, int maxDepth)
    {
        if (!Directory.Exists(root)) yield break;
        var rootDepth = root.TrimEnd(Path.DirectorySeparatorChar).Count(c => c == Path.DirectorySeparatorChar);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }
            foreach (var file in files) yield return file;

            var depth = dir.TrimEnd(Path.DirectorySeparatorChar).Count(c => c == Path.DirectorySeparatorChar) - rootDepth;
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    pending.Push(sub);
            }
            catch { }
        }
    }
}
