namespace RHI.Core.Services;

/// <summary>
/// The file names RTX Encore (SilyNoMeta/rtx-encore, #158) can be installed under: its INSTALLATION.md list, in its order
/// (upstream RHI 2.8.6 <c>RtxEncoreService.KnownProxyNames</c>), plus the <c>.asi</c> plugin. A file under one of them is
/// RTX Encore only when its version resource says so (<see cref="BinarySignatureInspector.LooksLikeRtxEncore"/>).
/// </summary>
public static class RtxEncoreNames
{
    public const string AsiName = "rtx-encore.asi";

    /// <summary>Folders beside the game's executable where an ASI loader reads plugins (RTX Encore's own docs).</summary>
    public static readonly IReadOnlyList<string> AsiFolders = ["plugins", "scripts"];

    public static readonly IReadOnlyList<string> All =
    [
        "version.dll", "dinput8.dll", "winmm.dll", "dxgi.dll",
        "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll",
        "dsound.dll", "wininet.dll", "winhttp.dll",
        "binkw64.dll", "bink2w64.dll",
        "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll", "xinputuap.dll",
        AsiName,
    ];

    /// <summary>The offered spelling of <paramref name="name"/> (case-insensitive), or null when RTX Encore does not use it.</summary>
    public static string? Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return All.FirstOrDefault(n => n.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bink names need the game's own Bink DLL renamed <c>*Hooked.dll</c>: not automated on Linux (as Universal MFG).</summary>
    public static bool IsBink(string name)
        => name.Equals("binkw64.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("bink2w64.dll", StringComparison.OrdinalIgnoreCase);

    public static bool IsAsi(string name) => name.Equals(AsiName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first RTX Encore found for a game whose executable sits in <paramref name="binaryDirectory"/>: any of its names
    /// in that folder, or its <c>.asi</c> there or in an ASI loader's plugin folder, identified by version resource.
    /// </summary>
    public static string? FindOnDisk(string? binaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(binaryDirectory) || !Directory.Exists(binaryDirectory)) return null;
        foreach (var candidate in Candidates(binaryDirectory))
            if (File.Exists(candidate) && BinarySignatureInspector.LooksLikeRtxEncore(candidate)) return candidate;
        return null;
    }

    private static IEnumerable<string> Candidates(string binaryDirectory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(binaryDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
            if (Canonical(Path.GetFileName(file)) is not null) yield return file;
        foreach (var folder in AsiFolders)
        {
            var dir = Path.Combine(binaryDirectory, folder);
            if (!Directory.Exists(dir)) continue;
            string[] inFolder;
            try { inFolder = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in inFolder)
                if (IsAsi(Path.GetFileName(file))) yield return file;
        }
    }
}
