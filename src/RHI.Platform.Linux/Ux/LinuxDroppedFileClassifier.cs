namespace RHI.Platform.Linux.Ux;

public enum LinuxDroppedFileKind
{
    Unsupported = 0,
    WindowsGameExecutable,
    LumaAddon,
    ReShadeAddon,
    LumaArchive,
    ModArchive,
}

public static class LinuxDroppedFileClassifier
{
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
