namespace RHI.Platform.Linux.Ux;

public enum LinuxDroppedFileKind
{
    Unsupported = 0,
    WindowsGameExecutable,
    LumaAddon,
    ReShadeAddon,
    LumaArchive,
    ModArchive,
    /// <summary>#180: a <c>.ini</c> offered as a ReShade preset (its Techniques line is checked when it is used).</summary>
    ReShadePreset,
}

/// <summary>#195: what RHI offers for a dropped or watched ReShade add-on (not a Luma one).</summary>
public enum DroppedAddonTarget
{
    /// <summary>Upstream offers no game install for it: the fork's Custom Add-ons import (#130).</summary>
    CustomAddonsImport = 0,
    /// <summary>A RenoDX game add-on: installed as the game's RenoDX mod.</summary>
    RenoDxInstall,
    /// <summary>Another ReShade add-on dropped on RHI: installed into the game through its Shaders/Addons selection.</summary>
    GameAddonInstall,
}

public static class LinuxDroppedFileClassifier
{
    /// <summary>
    /// #195, upstream 2.8.7's sets exactly. Dropped (<c>DragDropHandler</c>): every <c>.addon64</c>, <c>.addon32</c> and
    /// <c>.addon</c> is offered for a game except <c>renodx-dlss5*</c> and <c>renodx-dlss.*</c>; a RenoDX game add-on
    /// becomes the game's RenoDX mod, any other add-on goes in beside it (a name one of RHI's component rows installs, or a
    /// plain <c>.addon</c> that is not RenoDX's, keeps the Custom Add-ons import, which says why). Watched
    /// (<c>AddonFileWatcher</c>): only a RenoDX game add-on ending in <c>.addon64</c> or <c>.addon32</c> is offered.
    /// </summary>
    public static DroppedAddonTarget AddonTarget(string? path, bool watched)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return DroppedAddonTarget.CustomAddonsImport;
        var extension = Path.GetExtension(name);
        var withBitness = extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase) || extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase);
        if (!withBitness && (watched || !extension.Equals(".addon", StringComparison.OrdinalIgnoreCase))) return DroppedAddonTarget.CustomAddonsImport;
        if (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.IsDroppedRenoDxAddon(name)) return DroppedAddonTarget.RenoDxInstall;
        if (watched) return DroppedAddonTarget.CustomAddonsImport;
        // Upstream's drop skips renodx-dlss5* and renodx-dlss.*: both are among the names RHI's own rows install.
        return withBitness && !RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.IsReservedCustomFileName(name)
                           && !name.StartsWith("renodx-universal_ue", StringComparison.OrdinalIgnoreCase) // DoF Fix's row
            ? DroppedAddonTarget.GameAddonInstall
            : DroppedAddonTarget.CustomAddonsImport;
    }

    /// <summary>
    /// #216, upstream 2.8.7 <c>ProcessDroppedArchive</c>'s candidates: an <c>.addon64</c>, <c>.addon32</c> or <c>.addon</c>
    /// inside a dropped or watched archive whose name starts <c>renodx-</c> or holds "luma". A Luma add-on takes the
    /// Luma package install; a <c>renodx-</c> one takes the dropped file's own path (<see cref="AddonTarget"/>, dropped
    /// set): the game's RenoDX mod, or another add-on beside it. Names a dropped file would only import into Custom
    /// Add-ons (and every other file) are not offered: null.
    /// </summary>
    public static RHI.Platform.Linux.Deployment.DroppedArchiveAddonKind? ArchiveAddonKind(string? fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(name)) return null;
        if (IsLikelyLumaAddon(name)) return RHI.Platform.Linux.Deployment.DroppedArchiveAddonKind.Luma;
        if (!name.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase)) return null;
        return AddonTarget(name, watched: false) switch
        {
            DroppedAddonTarget.RenoDxInstall => RHI.Platform.Linux.Deployment.DroppedArchiveAddonKind.RenoDx,
            DroppedAddonTarget.GameAddonInstall => RHI.Platform.Linux.Deployment.DroppedArchiveAddonKind.GameAddon,
            _ => null,
        };
    }

    public static LinuxDroppedFileKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return LinuxDroppedFileKind.Unsupported;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return LinuxDroppedFileKind.WindowsGameExecutable;
        if (extension.Equals(".addon", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase))
            return IsLikelyLumaAddon(path) ? LinuxDroppedFileKind.LumaAddon : LinuxDroppedFileKind.ReShadeAddon;
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && IsLikelyLumaPackage(path))
            return LinuxDroppedFileKind.LumaArchive;
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            return LinuxDroppedFileKind.ModArchive;
        if (extension.Equals(".ini", StringComparison.OrdinalIgnoreCase))
            return LinuxDroppedFileKind.ReShadePreset;
        return LinuxDroppedFileKind.Unsupported;
    }

    public static bool IsLikelyLumaAddon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".addon", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase))
            return false;
        return Path.GetFileNameWithoutExtension(path).Contains("luma", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikelyLumaPackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        return (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || IsLikelyLumaAddon(path))
               && Path.GetFileNameWithoutExtension(path).Contains("luma", StringComparison.OrdinalIgnoreCase);
    }
}
