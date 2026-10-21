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
            // Any name ReShade can be installed under (#120), judged by ProxyDllNames' rules.
            return ProxyDllNames.IsReShadeOnDisk(path);
        });

        // #92: ReShade as a Vulkan layer leaves the Khronos loader and ReShade.ini beside the executable and its DLL in the
        // game's own RHI folder of the prefix. All three are the footprint: a DirectX game that ships its own vulkan-1.dll
        // beside a ReShade.ini kept from an earlier ReShade is not ReShade.
        reShadeInstalled = reShadeInstalled || HasVulkanLayerFootprint(game, indexedFiles, binaryDirectory);

        // RTX Encore (#158) under any of its names beside the executable, or its .asi in an ASI loader's folder; its version
        // resource decides (its body mentions ReShade and OptiScaler).
        var rtxEncoreInstalled = binaryDirectory is not null && indexedFiles.Any(path => IsRtxEncoreCandidate(path, binaryDirectory)
            && BinarySignatureInspector.LooksLikeRtxEncore(path));

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
            // Generic proxy names as well, but identified by its own version resource; RHI's record marks it managed.
            Make(ManagedComponentKind.RtxEncore, RtxEncoreDisplayName, rtxEncoreInstalled),
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

        // RTX Encore works through the game's own DLSS (#158): the same rule its install applies.
        result.Single(component => component.Kind == ManagedComponentKind.RtxEncore).GameShipsDlss =
            DlssManagerService.ShipsOwnDlss(stackRoot, indexedFiles);

        // w7/dxvk-hdr (#200): the DX9 HDR row is shown only where it can apply (a Windows DirectX 9 game outside Xbox/Game
        // Pass) or where its d3d9.dll is already beside the executable. Everywhere else it is Unsupported (hidden), so
        // RHI's record can still bring it back as Installed with Remove (the record outlives a deleted d3d9.dll or an
        // API change; review major 1).
        var dxvkHdrInstalled = HasDxvkHdrFootprint(indexedFiles, binaryDirectory);
        var dxvkHdrRow = Make(ManagedComponentKind.DxvkHdr, DxvkHdrDisplayName, dxvkHdrInstalled);
        if (!dxvkHdrInstalled && !IsDxvkHdrCandidate(game)) dxvkHdrRow.Availability = ComponentAvailability.Unsupported;
        result = [.. result, dxvkHdrRow];

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

    private static bool HasVulkanLayerFootprint(DetectedGame game, IReadOnlyList<string> indexedFiles, string? binaryDirectory)
    {
        if (binaryDirectory is null) return false;
        bool Beside(string name) => indexedFiles.Any(path =>
            Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(path) is { } parent
            && Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(binaryDirectory, StringComparison.Ordinal));
        return Beside("vulkan-1.dll") && Beside("ReShade.ini") && ReShadeVulkanLayerFolder.HasManifest(game);
    }

    /// <summary>The RTX Encore row's name (upstream 2.8.6 "RTX Encore (Beta)": every release so far is a prerelease).</summary>
    public const string RtxEncoreDisplayName = "RTX Encore (Beta)";

    /// <summary>#200: the DX9 HDR row's name (plain: no format or colour-space names; provenance lives in the tooltip).</summary>
    public const string DxvkHdrDisplayName = "DX9 HDR";

    /// <summary>
    /// #200: a string only EndlesslyFlowering's DXVK HDR build carries (its swap-chain colour-space upgrade setting, in
    /// both bitnesses); Proton's and upstream DXVK's d3d9.dll do not, so a plain DXVK is never taken for DX9 HDR.
    /// </summary>
    public const string DxvkHdrMarker = "d3d9.upgradeSwapChainColorSpaceTo";

    /// <summary>
    /// #200: where DX9 HDR can apply: a Windows game (not native Linux, not an Xbox / Game Pass install) whose effective
    /// graphics API is DirectX 9 (detected, or the user's / manifest's override). DirectX 8 is left out: upstream places
    /// only d3d9.dll, which a DirectX 8 game does not load itself. A game the RHI manifest lists as an XNA game (#201) is
    /// left out too: XNA's graphics assembly loads d3d9.dll only from the .NET assembly folder of the prefix, never from
    /// the game folder, so DX9 HDR's d3d9.dll there would never be loaded.
    /// </summary>
    public static bool IsDxvkHdrCandidate(DetectedGame game)
        => !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
           && game.XboxPackage is null
           && game.EffectiveGraphicsApi == GraphicsApi.DirectX9
           && string.IsNullOrWhiteSpace(game.ManifestXnaGacFolder); // w7 integration (#200 × #201)

    /// <summary>#200: a d3d9.dll beside the executable that carries <see cref="DxvkHdrMarker"/>.</summary>
    private static bool HasDxvkHdrFootprint(IReadOnlyList<string> indexedFiles, string? binaryDirectory)
    {
        if (binaryDirectory is null) return false;
        return indexedFiles.Any(path =>
            Path.GetFileName(path).Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(path) is { } parent
            && Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(binaryDirectory, StringComparison.Ordinal)
            && IsDxvkHdrBuild(path));
    }

    /// <summary>#200: whether <paramref name="path"/> is EndlesslyFlowering's DXVK HDR build (by <see cref="DxvkHdrMarker"/>).</summary>
    public static bool IsDxvkHdrBuild(string path)
    {
        try
        {
            return File.Exists(path) && BinaryStringSearch.ContainsAny(path, DxvkHdrMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRtxEncoreCandidate(string path, string binaryDirectory)
    {
        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || RtxEncoreNames.Canonical(name) is null) return false;
        var folder = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        if (folder.Equals(binaryDirectory, StringComparison.Ordinal)) return true;
        return RtxEncoreNames.IsAsi(name)
               && RtxEncoreNames.AsiFolders.Any(sub => folder.Equals(Path.Combine(binaryDirectory, sub), StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Names an OptiScaler proxy is recognised under: upstream's <c>SupportedDllNames</c>, the same list the Linux
    /// OptiScaler service installs under (a contract test keeps the two equal).
    /// </summary>
    internal static readonly string[] OptiScalerProxyNames =
        ["dxgi.dll", "winmm.dll", "d3d11.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll"];

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
            if (name is not null && OptiScalerProxyNames
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
