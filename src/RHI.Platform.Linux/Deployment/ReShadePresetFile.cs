using System.Text;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w14/nr (#274, #275, #277): the ReShade preset a game uses and RHI's one way to read and rewrite it. ReShade reads
/// <c>[GENERAL] PresetPath</c> from ReShade.ini relative to its own folder (the game's binary folder), and
/// <c>ReShadePreset.ini</c> there when the key is missing (upstream <c>UpliftService.PresetPath</c>). Writes are atomic,
/// keep a user's read-only lock and follow a link only as far as ReShade.ini's rule allows (the game's folders and
/// <c>~/.config/ReShade</c>); callers journal the file <see cref="WriteTarget"/> names before writing.
/// </summary>
public static class ReShadePresetFile
{
    public const string DefaultName = "ReShadePreset.ini";

    /// <summary>The preset file ReShade uses in <paramref name="binaryDirectory"/>, or null when PresetPath names a Windows drive path RHI cannot map.</summary>
    public static string? Resolve(string binaryDirectory)
    {
        string? value = null;
        var ini = ReShadeIniManager.ResolveIniPath(binaryDirectory);
        try
        {
            if (File.Exists(ini)) value = ReShadePresetText.GetKey(File.ReadAllText(ini), "GENERAL", "PresetPath");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("reshade-preset", $"ReShade.ini could not be read for its PresetPath: {ini}", ex);
        }
        return ResolveValue(binaryDirectory, value);
    }

    /// <summary>A PresetPath value (null/empty = ReShade's default) as a full Linux path; null for a Windows drive path.</summary>
    public static string? ResolveValue(string binaryDirectory, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) value = DefaultName;
        value = value.Trim().Trim('"');
        if (Regex.IsMatch(value, @"^[A-Za-z]:", RegexOptions.CultureInvariant) || value.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var relative = value.Replace('\\', '/');
        var full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(binaryDirectory, relative));
        // ReShade on Windows does not care about case: use the file that is there under any case.
        var directory = Path.GetDirectoryName(full);
        if (directory is not null && !File.Exists(full) && ReShadeDeploymentService.FindFileCaseInsensitive(directory, Path.GetFileName(full)) is { } existing)
            return existing;
        return full;
    }

    /// <summary>The preset's text, or null when there is no such file.</summary>
    public static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>The file a write to <paramref name="path"/> replaces (a link's allowed target, or the path itself); throws the write's refusal.</summary>
    public static string WriteTarget(string path, string binaryDirectory, string? gameRoot)
        => AtomicFile.ResolveUserFileTarget(path, ReShadeIniManager.LinkPolicy(path, binaryDirectory, gameRoot));

    /// <summary>Writes the preset (UTF-8 without BOM, atomically) under ReShade.ini's link rule.</summary>
    public static void Write(string path, string text, string binaryDirectory, string? gameRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.ReplaceUserFile(path, new UTF8Encoding(false).GetBytes(text), ReShadeIniManager.LinkPolicy(path, binaryDirectory, gameRoot),
            readOnly: ReadOnlyUserFile.WriteKeepingLock);
    }
}
