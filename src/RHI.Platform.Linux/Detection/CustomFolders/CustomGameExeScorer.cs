using System.Text.RegularExpressions;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.CustomFolders;

/// <summary>One exe inside a candidate folder, with the score the exe picker gave it.</summary>
/// <param name="RelativePath">Relative to the candidate folder, '/'-separated.</param>
public sealed record ScoredExe(string FullPath, string RelativePath, long SizeBytes, int Score, string Reasons);

/// <summary>What the PE reader found in an exe. Fake or unreadable files give (false, false).</summary>
public readonly record struct ExeProbe(bool HasGraphicsImports, bool IsConsole);

/// <summary>Facts about one exe that the score is computed from (no disk access).</summary>
public sealed record ExeFacts(string FileName, string RelativePath, long SizeBytes, bool NextToUnityPlayer, bool HasGraphicsImports,
    bool IsConsole, string FolderName);

/// <summary>Ranked exes for a folder; <see cref="IsAmbiguous"/> when the top two are close ("Check the exe").</summary>
public sealed record ExeRanking(IReadOnlyList<ScoredExe> Exes, bool IsAmbiguous);

/// <summary>
/// #293 (upstream 2.9.0 <c>CustomGameExeScorer</c>, ported as is): picks the game's exe in a folder found by the custom game
/// folders scan. Enumerates exes (bounded depth and count; redistributable, installer and crash-reporter folders skipped,
/// helper exes excluded by name), scores each one (Unreal shipping exe, Binaries folder, Unity player, graphics imports
/// and a name like the folder count for it; launchers, helper-like names, console programs, the Engine folder and tiny
/// files count against it) and ranks them. Linux: paths are '/'-separated and folder names compared ignoring case, as the
/// games are Windows games.
/// </summary>
public static partial class CustomGameExeScorer
{
    public const int MaxDepth = 4;
    public const int MaxDirectories = 1500;
    public const int MaxExes = 300;

    /// <summary>Top two within this many points = ambiguous ("Check the exe").</summary>
    public const int AmbiguityMargin = 15;

    /// <summary>Folders that never hold the game's exe (installers, redistributables, anti-cheat, tools).</summary>
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "redist", "_redist", "__redist", "redists", "_commonredist", "commonredist", "redistributables", "redistributable",
        "directx", "directx9", "dxsetup", "vcredist", "vc_redist", "support", "_support", "__support",
        "__installer", "_installer", "installer", "installers", "install", "setup", "prerequisites", "prereqs", "prereq",
        "easyanticheat", "battleye", "physx", "dotnet", "dotnetfx", "netfx", "thirdparty", "crashreporter", "crashpad",
        "monobleedingedge", "uninstall", "_uninstall", "__macosx", "dlc_installers",
    };

    /// <summary>Exe names that are usually not the game itself, but can be (penalised, not excluded).</summary>
    private static readonly string[] UnlikelyWords =
    [
        "server", "editor", "config", "settings", "benchmark", "crash", "report", "helper", "updater",
        "dedicated", "modmanager", "tool", "patcher", "sdk", "console", "debug",
    ];

    private static readonly string[] GraphicsDlls =
        ["d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "vulkan-1.dll", "opengl32.dll"];

    /// <summary>Helper / installer exes that are never the game (matched on the name without ".exe").</summary>
    [GeneratedRegex(
        @"^(unins\d*|uninst.*|uninstall.*|setup.*|.*_setup|vc_?redist.*|vcredist.*|dxsetup|dxwebsetup|directx.*|" +
        @"unitycrashhandler.*|crashreportclient.*|crashreporter.*|crashpad_handler|crashsender.*|crashhandler.*|bugsplat.*|" +
        @"easyanticheat.*|eac_?launcher|eacsetup|beservice.*|battleye.*|be_?launcher|" +
        @"ue4prereqsetup.*|ueprereqsetup.*|ue\dprereqsetup.*|dotnetfx.*|ndp\d.*|netfx.*|.*redist.*|" +
        @"quicksfv|7z|7za|7zg|7zfm|7zr|unrar|rar|winrar|unarc|isdone|oalinst|physx.*|" +
        @"epiconlineservices.*|epicwebhelper|ubisoftconnectinstaller|upc|uplaywebcore|socialclub.*|" +
        @"steamerrorreporter.*|steamservice|steam_?install.*|cefsharp\.browsersubprocess|cefprocess|qtwebengineprocess|" +
        @"zfgamebrowser|ffmpeg|ffprobe|python\w*|wermgr|dxdiag|installer.*|install|touchup|cleanup|dowser|" +
        @"gogalaxy.*|galaxyclient.*|vulkan.*runtime.*|vulkanrt.*|openal.*|xnafx.*|xnafx\d+_redist)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedName();

    /// <summary>Trailing tags stripped from exe names before comparing them with the folder name.</summary>
    [GeneratedRegex(@"([_\-\s.]?(shipping|win64|wingdk|win32|x64|x86|dx\d+|d3d\d+|vulkan|vk|steam|gog|epic|egs|retail|64bit|32bit|64|32))+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExeTagStrip();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotAlphanumeric();

    /// <summary>True for folder names the scan never enters (hidden, installers, redistributables…).</summary>
    public static bool IsSkippedFolderName(string name)
        => !string.IsNullOrEmpty(name) && (name.StartsWith('.') || name.StartsWith('$') || SkipFolders.Contains(name));

    /// <summary>True for helper / installer exe file names (with or without ".exe").</summary>
    public static bool IsExcludedExeName(string fileName)
    {
        var stem = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        return ExcludedName().IsMatch(stem);
    }

    /// <summary>Exe files of the folder itself (any case of ".exe"), helper exes left out.</summary>
    internal static IEnumerable<string> TopLevelGameExes(string folder)
        => Directory.EnumerateFiles(folder).Where(f => IsExe(f) && !IsExcludedExeName(Path.GetFileName(f)));

    private static bool IsExe(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Exe files under <paramref name="root"/>, up to <see cref="MaxDepth"/> folders down, skipping redistributable,
    /// installer and bonus folders and excluded helper exes. Symlinked folders are not followed (loops, other drives).
    /// Bounded so a huge folder cannot hang the scan.
    /// </summary>
    public static List<string> EnumerateExes(string root, CancellationToken ct = default)
    {
        var result = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var dirs = 0;
        while (queue.Count > 0 && dirs < MaxDirectories && result.Count < MaxExes)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            dirs++;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (!IsExe(file) || IsExcludedExeName(Path.GetFileName(file))) continue;
                    result.Add(file);
                    if (result.Count >= MaxExes) break;
                }
                if (depth >= MaxDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (IsSkippedFolderName(Path.GetFileName(sub))) continue;
                    try
                    {
                        if (new DirectoryInfo(sub).LinkTarget is not null) continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("custom-folders", $"Could not read {dir}", ex);
            }
        }
        return result;
    }

    /// <summary>Lower-case letters and digits only.</summary>
    public static string Norm(string s) => NotAlphanumeric().Replace(s.ToLowerInvariant(), "");

    /// <summary>Scores one exe. Higher = more likely the game. Returns the score and a short list of reasons (log text).</summary>
    public static (int Score, string Reasons) Score(ExeFacts f)
    {
        var score = 0;
        var why = new List<string>();
        var stem = Path.GetFileNameWithoutExtension(f.FileName);
        var parts = f.RelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var depth = parts.Length - 1;

        var inBinaries = parts.Length >= 3
            && parts[^3].Equals("Binaries", StringComparison.OrdinalIgnoreCase)
            && (parts[^2].Equals("Win64", StringComparison.OrdinalIgnoreCase)
                || parts[^2].Equals("WinGDK", StringComparison.OrdinalIgnoreCase)
                || parts[^2].Equals("Win32", StringComparison.OrdinalIgnoreCase));
        var inEngineFolder = parts.Length > 1 && parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase);

        if (stem.EndsWith("Shipping", StringComparison.OrdinalIgnoreCase)) { score += 100; why.Add("Unreal shipping exe"); }
        if (inBinaries && !inEngineFolder) { score += 60; why.Add("Binaries folder"); }
        if (f.NextToUnityPlayer) { score += 80; why.Add("Unity player"); }
        if (f.HasGraphicsImports) { score += 40; why.Add("uses a graphics API"); }

        var exeNorm = Norm(ExeTagStrip().Replace(stem, ""));
        var folderNorm = Norm(f.FolderName);
        if (exeNorm.Length > 0 && folderNorm.Length > 0)
        {
            if (exeNorm == folderNorm) { score += 40; why.Add("name matches folder"); }
            else if (exeNorm.Length >= 3 && folderNorm.Length >= 3 && (folderNorm.Contains(exeNorm) || exeNorm.Contains(folderNorm)))
            { score += 20; why.Add("name like folder"); }
        }

        var stemNorm = Norm(stem);
        if (stemNorm.Contains("launcher")) { score -= 60; why.Add("launcher"); }
        else if (UnlikelyWords.Any(stemNorm.Contains)) { score -= 30; why.Add("helper-like name"); }
        if (f.IsConsole) { score -= 50; why.Add("console program"); }
        if (inEngineFolder) { score -= 40; why.Add("inside Engine folder"); }
        if (f.SizeBytes < 150 * 1024) { score -= 30; why.Add("very small"); }

        // Deeper exes are less likely, except the known engine layouts.
        if (!inBinaries && !f.NextToUnityPlayer && depth > 0) score -= 4 * depth;

        // File size: tie-breaker only (0..10).
        var mb = f.SizeBytes / (1024.0 * 1024.0);
        score += (int)Math.Min(10, Math.Round(Math.Log2(1 + mb) * 1.5));
        return (score, string.Join(", ", why));
    }

    /// <summary>Reads what the scorer needs from a real exe; non-PE files give (false, false).</summary>
    public static ExeProbe ProbeExe(string path)
    {
        var imports = PeImportReader.Read(path);
        var graphics = imports is not null && imports.Regular.Concat(imports.Delay)
            .Any(dll => GraphicsDlls.Contains(dll, StringComparer.OrdinalIgnoreCase));
        return new ExeProbe(graphics, IsConsoleSubsystem(path));
    }

    /// <summary>True when the PE optional header says IMAGE_SUBSYSTEM_WINDOWS_CUI. False for anything unreadable.</summary>
    public static bool IsConsoleSubsystem(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dos = new byte[64];
            if (fs.Read(dos, 0, 64) < 64 || dos[0] != 'M' || dos[1] != 'Z') return false;
            var peOffset = BitConverter.ToInt32(dos, 0x3C);
            if (peOffset <= 0 || peOffset > fs.Length - 100) return false;
            fs.Seek(peOffset, SeekOrigin.Begin);
            var header = new byte[24 + 70];
            if (fs.Read(header, 0, header.Length) < header.Length) return false;
            if (header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0) return false;
            // The subsystem sits at offset 68 of the optional header in both PE32 and PE32+.
            return BitConverter.ToUInt16(header, 24 + 68) == 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Ranks the exes in <paramref name="root"/>, best first. <paramref name="folderName"/> is the cleaned game name the exe
    /// names are compared with (default: the folder's own name); <paramref name="probe"/> reads PE details (default
    /// <see cref="ProbeExe"/>).
    /// </summary>
    public static ExeRanking Rank(string root, string? folderName = null, Func<string, ExeProbe>? probe = null, CancellationToken ct = default)
    {
        probe ??= ProbeExe;
        var name = folderName ?? Path.GetFileName(root.TrimEnd('/'));
        var list = new List<ScoredExe>();
        foreach (var exe in EnumerateExes(root, ct))
        {
            ct.ThrowIfCancellationRequested();
            long size = 0;
            try { size = new FileInfo(exe).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* scored as very small */ }
            var dir = Path.GetDirectoryName(exe) ?? root;
            var unity = HasFileIgnoringCase(dir, "UnityPlayer.dll") || HasFileIgnoringCase(dir, "GameAssembly.dll");
            var p = probe(exe);
            var rel = Path.GetRelativePath(root, exe).Replace('\\', '/');
            var (score, reasons) = Score(new ExeFacts(Path.GetFileName(exe), rel, size, unity, p.HasGraphicsImports, p.IsConsole, name));
            list.Add(new ScoredExe(exe, rel, size, score, reasons));
        }
        var ranked = list
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.SizeBytes)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ambiguous = ranked.Count >= 2 && ranked[0].Score - ranked[1].Score < AmbiguityMargin;
        return new ExeRanking(ranked, ambiguous);
    }

    internal static bool HasFileIgnoringCase(string dir, string name)
    {
        try
        {
            return File.Exists(Path.Combine(dir, name))
                   || Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
