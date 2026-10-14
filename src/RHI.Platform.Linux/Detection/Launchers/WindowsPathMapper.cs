namespace RHI.Platform.Linux.Detection.Launchers;

/// <summary>
/// Maps a Windows path a launcher recorded inside a Wine prefix (<c>C:\Games\X</c>, <c>D:/Ubisoft/Y/</c>) onto the Linux
/// folder it names. <c>C:</c> is the prefix's <c>drive_c</c> (spelt through the prefix path, so a card keeps the folder
/// spelling the prefix scan uses); other letters follow the prefix's <c>dosdevices/&lt;x&gt;:</c> link. Windows paths ignore
/// case, so a component that does not exist as written is matched case-insensitively against its folder's entries.
/// Null when the path is not a drive path below a drive's root, uses <c>..</c>, or its drive is not mapped.
/// </summary>
internal static class WindowsPathMapper
{
    public static string? ToLinux(string prefix, string? windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) return null;
        var text = windowsPath.Trim().Trim('"').Replace('\\', '/');
        if (text.StartsWith("//?/", StringComparison.Ordinal)) text = text[4..];
        if (text.Length < 2 || !char.IsAsciiLetter(text[0]) || text[1] != ':') return null;
        var drive = char.ToLowerInvariant(text[0]);
        var parts = text[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        // A bare drive is never one game's folder, and `..` could climb out of the drive.
        if (parts.Length == 0 || parts.Any(part => part is ".." or ".")) return null;

        var root = DriveRoot(prefix, drive);
        if (root is null) return null;
        var current = root;
        foreach (var part in parts)
        {
            var exact = Path.Combine(current, part);
            if (Directory.Exists(exact) || File.Exists(exact))
            {
                current = exact;
                continue;
            }
            current = MatchIgnoringCase(current, part) ?? exact;
        }
        return current;
    }

    private static string? DriveRoot(string prefix, char drive)
    {
        var link = Path.Combine(prefix, "dosdevices", drive + ":");
        if (drive == 'c')
        {
            // The usual `c: -> ../drive_c`: spell it through the prefix (the folder spelling every other scan uses).
            var driveC = Path.Combine(prefix, "drive_c");
            var target = LinkTarget(link);
            if (target is null || SameDirectory(target, driveC)) return Directory.Exists(driveC) ? driveC : null;
            return Directory.Exists(target) ? target : null;
        }
        var mapped = LinkTarget(link);
        return mapped is not null && Directory.Exists(mapped) ? mapped : null;
    }

    private static string? LinkTarget(string link)
    {
        try
        {
            var info = new FileInfo(link);
            if (info.LinkTarget is not { } target) return null;
            return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(link)!, target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.DetectorDiagnostics.Warn($"Wine drive link {link} unreadable", ex);
            return null;
        }
    }

    private static bool SameDirectory(string a, string b)
        => RHI.Core.Services.PathIdentity.Canonicalize(a) == RHI.Core.Services.PathIdentity.Canonicalize(b);

    private static string? MatchIgnoringCase(string directory, string name)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            return Directory.EnumerateFileSystemEntries(directory)
                .Where(entry => Path.GetFileName(entry).Equals(name, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.DetectorDiagnostics.Warn($"Wine prefix folder {directory} unreadable", ex);
            return null;
        }
    }
}
