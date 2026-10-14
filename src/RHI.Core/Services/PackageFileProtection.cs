namespace RHI.Core.Services;

/// <summary>
/// Files of an Xbox / Game Pass package RHI must never write (Refs #90): the encrypted game exes (patched bytes no longer
/// decrypt, so the game fails to start), the package's <c>.xodus-streaming*.msixvc</c>, <c>MicrosoftGame.config</c>,
/// <c>appxmanifest.xml</c> and PartingX's markers (they carry the licence and the Xbox identity), and the
/// DXVK / VKD3D-Proton / DXVK-NVAPI DLLs PartingX places in a title prefix's <c>system32</c>/<c>syswow64</c> (PartingX
/// marks the prefix stale and puts its own back). Decided from the files on disk, so it needs no library state:
/// a package folder is one holding <c>.xodus-streaming.msixvc</c>, <c>.partingx-owned</c> or <c>.partingx-appx.json</c>.
/// </summary>
public static class PackageFileProtection
{
    /// <summary>Fixed start of the refusal; the file's path follows.</summary>
    public const string RefusalPrefix =
        "RHI never changes this file of an Xbox package: the game would no longer start, or PartingX/Xodus would put it back. File: ";

    private static readonly string[] PackageMarkers = [".xodus-streaming.msixvc", ".partingx-owned", ".partingx-appx.json"];

    private static readonly string[] PackageRootFiles =
    [
        ".xodus-streaming.msixvc", ".xodus-streaming-tmp.msixvc", ".partingx-owned", ".partingx-appx.json", ".partingx-partial",
        ".partingx-run", "MicrosoftGame.config", "appxmanifest.xml"
    ];

    private static readonly string[] PrefixRuntimeDlls =
        ["dxgi.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "d3d12.dll", "d3d12core.dll", "nvapi.dll", "nvapi64.dll", "nvofapi64.dll"];

    // Package folders hold their exes a few levels down (UE: <Project>/Binaries/WinGDK).
    private const int MaxPackageDepth = 10;

    /// <summary>The refusal for writing, replacing, renaming or deleting <paramref name="path"/>; null when RHI may.</summary>
    public static string? Refusal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        var dir = Path.GetDirectoryName(full);
        if (dir is null) return null;

        if (IsPrefixRuntimeDll(dir, name)) return RefusalPrefix + full;

        var root = PackageRoot(dir);
        if (root is null) return null;
        if (string.Equals(root, dir, StringComparison.Ordinal) &&
            PackageRootFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            return RefusalPrefix + full;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsEncrypted(full))
            return RefusalPrefix + full;
        return null;
    }

    /// <summary><paramref name="path"/> itself, after <see cref="ThrowIfProtected"/> (for an argument checked before anything else runs).</summary>
    public static string Checked(string path)
    {
        ThrowIfProtected(path);
        return path;
    }

    /// <summary>Throws <see cref="ProtectedPackageFileException"/> for a protected file.</summary>
    public static void ThrowIfProtected(string path)
    {
        if (Refusal(path) is { } refusal) throw new ProtectedPackageFileException(refusal);
    }

    private static string? PackageRoot(string dir)
    {
        var current = dir;
        for (var level = 0; current is not null && level <= MaxPackageDepth; level++, current = Path.GetDirectoryName(current))
            if (PackageMarkers.Any(marker => File.Exists(Path.Combine(current, marker))))
                return current;
        return null;
    }

    // <title env>/prefix/drive_c/windows/system32|syswow64/<dll>, where <title env> holds partingx.json.
    private static bool IsPrefixRuntimeDll(string dir, string name)
    {
        if (!PrefixRuntimeDlls.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        var system = Path.GetFileName(dir);
        if (!system.Equals("system32", StringComparison.OrdinalIgnoreCase) && !system.Equals("syswow64", StringComparison.OrdinalIgnoreCase))
            return false;
        var windows = Path.GetDirectoryName(dir);
        var driveC = windows is null ? null : Path.GetDirectoryName(windows);
        var prefix = driveC is null ? null : Path.GetDirectoryName(driveC);
        var environment = prefix is null ? null : Path.GetDirectoryName(prefix);
        return environment is not null &&
               Path.GetFileName(windows!).Equals("windows", StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(driveC!).Equals("drive_c", StringComparison.Ordinal) &&
               File.Exists(Path.Combine(environment, "partingx.json"));
    }

    // An existing exe without the MZ header: the package keeps it encrypted (an absent file is not protected).
    private static bool IsEncrypted(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Span<byte> magic = stackalloc byte[2];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Read(magic) < 2 || magic[0] != (byte)'M' || magic[1] != (byte)'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unknown: refuse rather than risk the game's own exe.
            AppLog.Warn("deploy", $"Xbox package exe {path} unreadable; treated as protected", ex);
            return true;
        }
    }
}

/// <summary>A deployment tried to change a protected Xbox package file (<see cref="PackageFileProtection"/>).</summary>
public sealed class ProtectedPackageFileException(string message) : IOException(message);
