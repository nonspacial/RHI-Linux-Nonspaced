using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// Conservative local component detection. A caller may provide the shared selected-game
/// file index so component detection does not recursively walk the game tree again.
/// </summary>
public static class ComponentProbe
{
    public static IReadOnlyList<ManagedComponent> Probe(
        DetectedGame game,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (!game.HasConfirmedPatchTarget && game.RequiresExplicitPatchTarget)
            indexedFiles ??= Array.Empty<string>();
        else
            indexedFiles ??= GameFileIndexer.Build(game.InstallPath, 8, cancellationToken);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(Path.GetFileName(path));
        }

        var binaryDirectory = string.IsNullOrWhiteSpace(game.BinaryDirectory)
            ? null
            : Path.GetFullPath(game.BinaryDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var dlssg2030Installed = files.Contains("dlssg_sm86.ini") &&
            new[] { "version.dll", "winmm.dll", "dinput8.dll", "winhttp.dll", "dxgi.dll" }.Any(files.Contains);

        var reShadeInstalled = indexedFiles.Any(path =>
        {
            if (binaryDirectory is not null)
            {
                var parent = Path.GetDirectoryName(path);
                if (parent is null || !Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(binaryDirectory, StringComparison.Ordinal))
                    return false;
            }
            var name = Path.GetFileName(path);
            if (name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ReShade64.asi", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase))
                return BinarySignatureInspector.LooksLikeReShade(path);
            return false;
        });

        // RHI's own OptiScaler FG stack (OptiScaler/Streamline/sl.*) is not the game's Streamline: the DLSS panel and
        // Universal MFG already ignore the OptiScaler/ folder, and MFG Ada Unlock needs the game's own integration.
        var stackRoot = !string.IsNullOrWhiteSpace(game.InstallPath) ? game.InstallPath : game.BinaryDirectory;
        var ownStreamline = indexedFiles.Any(path =>
        {
            var name = Path.GetFileName(path);
            return (name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("sl.common.dll", StringComparison.OrdinalIgnoreCase)) &&
                   (string.IsNullOrWhiteSpace(stackRoot) || !DlssManagerService.IsUnderOptiScalerFolder(stackRoot, path));
        });

        var result = new[]
        {
            Make(ManagedComponentKind.ReShade, "ReShade", reShadeInstalled),
            Make(ManagedComponentKind.RenoDX, "RenoDX HDR", indexedFiles.Any(path => IsPrimaryRenoDxAddonFileName(Path.GetFileName(path)))),
            Make(ManagedComponentKind.RenoDXUpgrade, "RenoDX Upgrade", indexedFiles.Any(path =>
                IsRenoDxUpgradeAddonFileName(Path.GetFileName(path)))),
            Make(ManagedComponentKind.OptiScaler, "OptiScaler", IsOptiScalerInstalled(indexedFiles, binaryDirectory)),
            Make(ManagedComponentKind.LumaFramework, "Luma Framework", files.Any(IsLumaFrameworkAddonFileName)),
            Make(ManagedComponentKind.REFramework, "RE Framework", IsReFrameworkInstalled(indexedFiles, binaryDirectory)),
            Make(ManagedComponentKind.Dlss, "DLSS", files.Contains("nvngx_dlss.dll")),
            Make(ManagedComponentKind.Streamline, "Streamline", ownStreamline),
            Make(ManagedComponentKind.ReLimiter, "ReLimiter", files.Any(IsReLimiterAddonFileName)),
            Make(ManagedComponentKind.DisplayCommander, "Display Commander", files.Any(IsDisplayCommanderAddonFileName)),
            Make(ManagedComponentKind.MfgAdaUnlock, "MFG Ada Unlock", files.Any(IsMfgAdaUnlockAddonFileName)),
            Make(ManagedComponentKind.Dlssg2030Unlock, "RTX 20 FG Unlock (Legacy Experimental)", dlssg2030Installed),
            // Standalone DLSS Enabler uses a generic proxy filename, so a bare version/dxgi/etc.
            // is not sufficient evidence. RHI ownership records upgrade this row to Installed later.
            Make(ManagedComponentKind.DlssEnablerStandalone, "DLSS Enabler (Standalone)", false),
            // Universal RTXMFG is also a generic proxy DLL; ownership records are the safe
            // authority for Installed state. A bare version/dxgi/etc. must never be adopted.
            Make(ManagedComponentKind.Rtx40MfgStandalone, "Universal MFG (Experimental)", false),
            Make(ManagedComponentKind.Dlss5Feeder, "DLSS5 Feeder", files.Any(IsDlss5FeederAddonFileName)),
            Make(ManagedComponentKind.Dlss5Dx11Bridge, "DLSS5 DX11 Bridge", files.Any(IsDlss5BridgeAddonFileName)),
            Make(ManagedComponentKind.ShortFuse, "ShortFuse / DLSS Frame Generation", files.Contains("renodx-dlss.addon64") || files.Contains("zzz_renodx-dlss.addon64")),
            Make(ManagedComponentKind.DofFix, "Universal UE DOF Fix", indexedFiles.Any(path =>
                Path.GetFileName(path).Equals("renodx-universal_ue-dof-fix.addon64", StringComparison.OrdinalIgnoreCase) &&
                (binaryDirectory is null || string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(path) ?? "").TrimEnd(Path.DirectorySeparatorChar),
                    binaryDirectory,
                    StringComparison.Ordinal)))),
        };

        // The detail's credit line lists the recorded add-on files; it keeps only those still found here.
        foreach (var component in result)
        {
            if (component.Kind == ManagedComponentKind.RenoDX)
                component.DetectedAddonFiles = files.Where(IsPrimaryRenoDxAddonFileName).ToArray();
            else if (component.Kind == ManagedComponentKind.LumaFramework)
                component.DetectedAddonFiles = files.Where(IsLumaFrameworkAddonFileName).ToArray();
        }

        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var component in result)
            {
                component.Availability = ComponentAvailability.Unsupported;
                component.Note = "Native Linux target: the current RHI Linux integration deploys Windows DLL components only to Wine/Proton games.";
            }
        }

        return result;
    }

    private static bool IsPrimaryRenoDxAddonFileName(string fileName)
    {
        if (!fileName.StartsWith("renodx", StringComparison.OrdinalIgnoreCase) ||
            (!fileName.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) &&
             !fileName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)))
            return false;
        return !fileName.Contains("dlss", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains("upgrade", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains("mfgunlock", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains("mfg_unlock", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Equals("renodx-universal_ue-dof-fix.addon64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRenoDxUpgradeAddonFileName(string fileName)
        => fileName.Contains("renodx", StringComparison.OrdinalIgnoreCase) &&
           fileName.Contains("upgrade", StringComparison.OrdinalIgnoreCase) &&
           (fileName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase));

    private static bool IsLumaFrameworkAddonFileName(string fileName)
        => IsReShadeAddonFileName(fileName, "luma");

    private static bool IsReLimiterAddonFileName(string fileName)
        => IsReShadeAddonFileName(fileName, "relimiter", "ultra_limiter");

    private static bool IsDisplayCommanderAddonFileName(string fileName)
        => IsReShadeAddonFileName(fileName, "display_commander", "displaycommander");

    private static bool IsMfgAdaUnlockAddonFileName(string fileName)
        => IsReShadeAddonFileName(fileName, "mfgunlock", "mfg_unlock");

    private static bool IsDlss5FeederAddonFileName(string fileName)
        => IsReShadeAddonFileName(fileName, "dlss5-feed", "dlss5_feeder");

    private static bool IsDlss5BridgeAddonFileName(string fileName)
        => IsReShadeAddonFileName(fileName, "dlss5-bridge", "dlss5-dx11-bridge");

    private static bool IsReShadeAddonFileName(string fileName, params string[] familyMarkers)
    {
        if (!fileName.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase))
            return false;

        return familyMarkers.Any(marker => fileName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReFrameworkInstalled(IReadOnlyList<string> indexedFiles, string? binaryDirectory)
    {
        if (binaryDirectory is null) return false;
        var hasDinput8 = indexedFiles.Any(path =>
        {
            var parent = Path.GetDirectoryName(path);
            return parent is not null
                   && Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(binaryDirectory, StringComparison.Ordinal)
                   && Path.GetFileName(path).Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase);
        });
        if (!hasDinput8) return false;

        // External REFramework creates a reframework directory/config/log footprint after first launch.
        // A bare dinput8.dll is intentionally not enough evidence because many unrelated ASI/mod loaders use it.
        return indexedFiles.Any(path =>
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(binaryDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
            var relative = Path.GetRelativePath(binaryDirectory, full);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return first.Equals("reframework", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsOptiScalerInstalled(IReadOnlyList<string> indexedFiles, string? binaryDirectory)
    {
        if (binaryDirectory is null) return false;
        foreach (var path in indexedFiles)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(binaryDirectory, StringComparison.Ordinal))
                continue;
            var name = Path.GetFileName(path);
            if (name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) && BinarySignatureInspector.LooksLikeOptiScaler(path))
                return true;
            if (name is not null && new[] { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll" }
                .Any(proxy => name.Equals(proxy, StringComparison.OrdinalIgnoreCase)) && BinarySignatureInspector.LooksLikeOptiScaler(path))
                return true;
        }
        return false;
    }

    private static ManagedComponent Make(ManagedComponentKind kind, string name, bool installed, string? note = null) => new()
    {
        Kind = kind,
        DisplayName = name,
        Availability = installed ? ComponentAvailability.Installed : ComponentAvailability.Available,
        Note = note
    };
}
