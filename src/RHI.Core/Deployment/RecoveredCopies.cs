namespace RHI.Core.Deployment;

/// <summary>
/// The same-file-system place for copies RHI cannot prove redundant but must not delete:
/// <c>&lt;file's folder&gt;/.rhi-recovered/&lt;yyyyMMdd-HHmmss&gt;/</c>. Moving there is a rename inside one
/// folder tree (so it never becomes an unflushed cross-device copy), made durable with directory
/// fsyncs. Recursive walks over game folders must not treat this folder's content as game files;
/// copies are stored under decorated names so DLL-name lookups never match them.
/// </summary>
public static class RecoveredCopies
{
    public const string FolderName = ".rhi-recovered";

    /// <summary>Suffix given to a pre-operation copy recovery set aside (e.g. <c>ReShade.ini.pre-rhi</c>).</summary>
    public const string PreOperationSuffix = ".pre-rhi";

    /// <summary>
    /// Moves <paramref name="path"/> to <c>&lt;its folder&gt;/.rhi-recovered/&lt;timestamp&gt;/&lt;fileName&gt;</c>
    /// (default: its own name), adding <c>-2</c>, <c>-3</c>… if that name is taken. Returns the new path.
    /// </summary>
    public static string MoveAside(string path, DateTime nowUtc, string? fileName = null)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.Combine(Path.GetDirectoryName(full)!, FolderName, nowUtc.ToString("yyyyMMdd-HHmmss"));
        AtomicFile.CreateDirectoryDurable(folder);
        var name = fileName ?? Path.GetFileName(full);
        var destination = Path.Combine(folder, name);
        for (var n = 2; File.Exists(destination) || Directory.Exists(destination); n++)
            destination = Path.Combine(folder, $"{name}-{n}");
        AtomicFile.MoveDurable(full, destination);
        return destination;
    }
}
