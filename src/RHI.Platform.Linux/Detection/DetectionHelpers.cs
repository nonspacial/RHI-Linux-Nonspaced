using System.Text.RegularExpressions;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Detection;

internal static partial class DetectionHelpers
{
    [GeneratedRegex("\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"")]
    private static partial Regex VdfPairRegex();

    public static Dictionary<string, string> ParseFlatVdf(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPairRegex().Matches(text))
            result[m.Groups["key"].Value] = m.Groups["value"].Value.Replace("\\\\", "\\");
        return result;
    }


    /// <summary>
    /// Conservative deny-list for Steam compatibility runtimes and redistributable packages.
    /// These entries have appmanifests just like games but must never appear in the RHI game library.
    /// </summary>
    public static bool IsKnownSteamTool(string name, string installDir)
    {
        var n = name.Trim();
        var d = installDir.Trim();
        if (n.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Equals("Proton Experimental", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Equals("Proton Hotfix", StringComparison.OrdinalIgnoreCase)) return true;
        // Versioned Proton runtimes ("Proton 9.0", "Proton 8.0") ship a Windows image full of
        // .exe files and are picked up as a game otherwise.
        if (n.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) && n.Length > 7 && char.IsDigit(n[7])) return true;
        if (d.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) && d.Length > 7 && char.IsDigit(d[7])) return true;
        if (n.StartsWith("Proton EasyAntiCheat Runtime", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("Proton BattlEye Runtime", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("Steam Controller Configs", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("Steam Input Configs", StringComparison.OrdinalIgnoreCase)) return true;

        if (d.StartsWith("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase)) return true;
        if (d.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase)) return true;
        if (d.Equals("Proton - Experimental", StringComparison.OrdinalIgnoreCase)) return true;
        if (d.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) &&
            (d.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
             d.Contains("BattlEye", StringComparison.OrdinalIgnoreCase) ||
             d.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))) return true;

        return false;
    }

    /// <summary>
    /// Directories installed by Windows/Wine or support software that are not games.
    /// Kept intentionally conservative to avoid hiding legitimate unusual game names.
    /// </summary>
    public static bool IsKnownNonGameWineDirectory(string directoryName)
    {
        var n = directoryName.Trim();
        string[] exact =
        [
            "Common Files",
            "Internet Explorer",
            "Windows Media Player",
            "Windows NT",
            "WindowsPowerShell",
            "Microsoft.NET",
            "Microsoft Update Health Tools",
            "AntiCheatExpert",
            "EasyAntiCheat",
            "EasyAntiCheat_EOS",
            "BattlEye",
            "Epic Online Services",
            // MIDI utility, not a game (DET-11 repro).
            "Bome MIDI Translator",
            "Bome MIDI Translator Pro"
        ];
        return exact.Any(x => n.Equals(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true for prefixes that belong to a launcher already handled by a dedicated detector.
    /// Scanning their drive_c/Program Files again creates fake games from support applications.
    /// </summary>
    public static bool IsLauncherManagedPrefix(string prefixPath)
    {
        var normalized = prefixPath.Replace('\\', '/').TrimEnd('/');
        return normalized.Contains("/heroic/prefixes/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/steamapps/compatdata/", StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindLikelyExecutable(
        string root,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default,
        PeArchitecture? preferredArchitecture = null)
    {
        if (!Directory.Exists(root)) return null;
        var hardIgnore = new[]
        {
            "unins", "crash", "report", "setup", "install", "redist", "vcredist",
            "unitycrashhandler", "easyanticheat", "eac", "beservice", "vc_redist", "busybox",
            // Microsoft GDK packaging stub: it starts the real game exe and is never the game itself.
            "gamelaunchhelper"
        };
        var softPenalty = new[] { "launcher", "bootstrap", "helper", "updater", "config", "diagnostic" };
        var normalizedRoot = NormalizeToken(Path.GetFileName(root));
        indexedFiles ??= RHI.Core.Services.GameFileIndexer.Build(root, 8, cancellationToken);

        var baseDepth = Depth(root);
        var candidates = new List<(string path, long size, int depth, int score, PeArchitecture architecture)>();
        foreach (var file in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(file);
            if (hardIgnore.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }
            var depth = Math.Max(0, Depth(Path.GetDirectoryName(file) ?? root) - baseDepth);
            var normalizedExe = NormalizeToken(Path.GetFileNameWithoutExtension(file));
            var normalizedPath = file.Replace('\\', '/');
            var score = 0;
            if (normalizedPath.Contains("/Binaries/Win64/", StringComparison.OrdinalIgnoreCase)) score += 180;
            else if (normalizedPath.Contains("/Win64/", StringComparison.OrdinalIgnoreCase)) score += 120;
            else if (normalizedPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) score += 45;
            if (normalizedRoot.Length >= 4 &&
                (normalizedExe.Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                 normalizedRoot.Contains(normalizedExe, StringComparison.OrdinalIgnoreCase))) score += 100;
            if (softPenalty.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) score -= 110;
            score -= depth * 7;
            score += (int)Math.Min(80, Math.Log2(Math.Max(1, size / 1024 + 1)) * 5);

            var architecture = preferredArchitecture.HasValue
                ? RHI.Core.Services.PeArchitectureDetector.Detect(file)
                : PeArchitecture.Unknown;
            candidates.Add((file, size, depth, score, architecture));
        }

        IEnumerable<(string path, long size, int depth, int score, PeArchitecture architecture)> ranked = candidates;
        if (preferredArchitecture is PeArchitecture.X86 or PeArchitecture.X64)
        {
            // A manifest bitness override is a selector, not merely a small scoring hint.
            // Fall back to the normal candidates only when no PE of the requested architecture exists.
            var matching = candidates.Where(x => x.architecture == preferredArchitecture.Value).ToArray();
            if (matching.Length > 0) ranked = matching;
        }

        return ranked
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.size)
            .ThenBy(x => x.depth)
            .Select(x => x.path)
            .FirstOrDefault();
    }


    /// <summary>
    /// Finds a native Linux launch target when a Steam depot ships one. This is intentionally
    /// separate from the Windows .exe selector: native depots often contain Windows helper
    /// executables too, and choosing one of those makes renderer detection meaningless.
    /// </summary>
    public static string? FindLikelyNativeExecutable(
        string root,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) return null;
        indexedFiles ??= RHI.Core.Services.GameFileIndexer.Build(root, 8, cancellationToken);

        var normalizedRoot = NormalizeToken(Path.GetFileName(root));
        var baseDepth = Depth(root);
        var candidates = new List<(string path, long size, int depth, int score)>();

        foreach (var file in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LooksLikeNativeLaunchTarget(file)) continue;

            var name = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(file);
            var normalizedName = NormalizeToken(stem);
            var normalizedPath = file.Replace('\\', '/');
            var lower = name.ToLowerInvariant();
            var isElf = IsElf(file);
            var nameMatchesRoot = normalizedRoot.Length >= 4 &&
                (normalizedName.Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                 normalizedRoot.Contains(normalizedName, StringComparison.OrdinalIgnoreCase));
            if (!isElf && !nameMatchesRoot) continue;
            if (lower.Contains("busybox") || lower.Contains("crash") || lower.Contains("report") ||
                lower.Contains("uninstall") || lower.Contains("setup") || lower.Contains("redist") ||
                lower.Contains("server"))
                continue;

            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }
            var depth = Math.Max(0, Depth(Path.GetDirectoryName(file) ?? root) - baseDepth);
            var score = 0;

            if (nameMatchesRoot) score += 180;
            if (depth == 0) score += 60;
            if (isElf) score += 80;
            if (lower.EndsWith(".x86_64") || lower.Contains("x86_64")) score += 55;
            if (lower.EndsWith(".sh")) score += 25;
            if (normalizedPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) score += 20;
            if (lower.Contains("launcher") || lower.Contains("helper") || lower.Contains("bootstrap")) score -= 90;

            score -= depth * 6;
            score += (int)Math.Min(60, Math.Log2(Math.Max(1, size / 1024 + 1)) * 4);
            candidates.Add((file, size, depth, score));
        }

        return candidates
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.size)
            .ThenBy(x => x.depth)
            .Select(x => x.path)
            .FirstOrDefault();
    }

    private static bool LooksLikeNativeLaunchTarget(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        if (ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsElf(path) || HasShebang(path)) return true;
        if (name.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".x86", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            return IsUnixExecutable(path) || name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrEmpty(ext) && IsUnixExecutable(path);
    }

    internal static bool IsNativeExecutable(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && (IsElf(path) || HasShebang(path));

    private static bool IsUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return false;

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }

    private static bool IsElf(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = File.OpenRead(path);
            return stream.Read(magic) == 4 && magic[0] == 0x7f && magic[1] == (byte)'E' &&
                   magic[2] == (byte)'L' && magic[3] == (byte)'F';
        }
        catch { return false; }
    }

    private static bool HasShebang(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[2];
            using var stream = File.OpenRead(path);
            return stream.Read(magic) == 2 && magic[0] == (byte)'#' && magic[1] == (byte)'!';
        }
        catch { return false; }
    }

    public static void ApplyRuntimeMetadata(
        DetectedGame game,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (game.PatchTargetAuthority is "Manual" or "Manifest" &&
            (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath)))
        {
            game.GraphicsApi = GraphicsApi.Unknown;
            game.GraphicsApis = [];
            game.GraphicsApiConfidence = GraphicsApiConfidence.Unknown;
            game.GraphicsApiEvidence = $"O alvo {game.PatchTargetAuthority.ToLowerInvariant()} está configurado, mas o executável não existe no momento. O RHI não substituirá essa autoridade por outro .exe automaticamente.";
            return;
        }

        if (game.RequiresExplicitPatchTarget && !game.HasConfirmedPatchTarget)
        {
            game.BinaryDirectory = null;
            game.GraphicsApi = GraphicsApi.Unknown;
            game.GraphicsApis = [];
            game.GraphicsApiConfidence = GraphicsApiConfidence.Unknown;
            game.GraphicsApiEvidence = "Launcher detectado como alvo de inicialização. Selecione o executável real do jogo em Editar jogo para habilitar a detecção e os patches locais.";
            return;
        }

        var isNative = string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);
        var forcedArchitecture = isNative ? null : game.ForcedArchitecture;
        var currentArchitecture = !string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath)
            ? RHI.Core.Services.PeArchitectureDetector.Detect(game.ExecutablePath)
            : PeArchitecture.Unknown;
        var forcedExecutableMismatch = forcedArchitecture is PeArchitecture.X86 or PeArchitecture.X64
            && currentArchitecture != forcedArchitecture.Value;

        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath) || forcedExecutableMismatch)
        {
            game.ExecutablePath = isNative
                ? FindLikelyNativeExecutable(game.InstallPath, indexedFiles, cancellationToken)
                  ?? FindLikelyExecutable(game.InstallPath, indexedFiles, cancellationToken)
                : FindLikelyExecutable(game.InstallPath, indexedFiles, cancellationToken, forcedArchitecture);
        }

        game.BinaryDirectory = game.ExecutablePath is { Length: > 0 }
            ? Path.GetDirectoryName(game.ExecutablePath)
            : game.InstallPath;

        // Pinned Windows RHI uses re_chunk_000.pak as the shallow signature for RE Engine.
        // Preserve a more specific upstream engine hint when one exists; otherwise local disk
        // evidence is sufficient to unlock the RE Framework row offline.
        if (string.IsNullOrWhiteSpace(game.EngineHint) && indexedFiles is not null &&
            indexedFiles.Any(path => Path.GetFileName(path).Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase)))
        {
            game.EngineHint = "RE Engine";
        }

        // Upstream RHI also detects UE4/UE5 from a game Binaries\Win64/WinGDK folder
        // that is outside Engine and contains a Shipping executable. Linux launchers frequently
        // hand us that binary directory directly (Heroic/Sideload in particular), so preserve
        // this local authority even when the manifest has no per-title engine override.
        if (string.IsNullOrWhiteSpace(game.EngineHint) && indexedFiles is not null &&
            LooksLikeModernUnreal(indexedFiles))
        {
            game.EngineHint = DetectLocalUnrealVersionHint(game.InstallPath, indexedFiles) ?? "Unreal Engine";
        }

        var detection = GraphicsApiDetector.Detect(
            game.InstallPath,
            game.ExecutablePath,
            indexedFiles,
            game.PrefixPath,
            cancellationToken);
        game.GraphicsApi = detection.Primary;
        game.GraphicsApis = detection.Candidates;
        game.GraphicsApiConfidence = detection.Confidence;
        game.GraphicsApiEvidence = detection.Evidence;
    }

    internal static bool LooksLikeModernUnreal(IReadOnlyList<string> indexedFiles)
        => indexedFiles.Any(path =>
        {
            var normalized = path.Replace('\\', '/');
            var fileName = Path.GetFileName(path);
            var inGameWin64 = normalized.Contains("/Binaries/Win64/", StringComparison.OrdinalIgnoreCase) ||
                              normalized.Contains("/Binaries/WinGDK/", StringComparison.OrdinalIgnoreCase);
            var inEngine = normalized.Contains("/Engine/Binaries/", StringComparison.OrdinalIgnoreCase);
            var shippingExe = fileName.EndsWith("Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
                              fileName.Contains("Shipping", StringComparison.OrdinalIgnoreCase) &&
                              fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            return inGameWin64 && !inEngine && shippingExe;
        });

    internal static string? DetectLocalUnrealVersionHint(string installPath, IReadOnlyList<string> indexedFiles)
    {
        // Prefer an indexed Engine/Build/Build.version file when the launcher supplied the root.
        var buildVersion = indexedFiles.FirstOrDefault(path =>
            Path.GetFileName(path).Equals("Build.version", StringComparison.OrdinalIgnoreCase) &&
            path.Replace('\\', '/').Contains("/Engine/Build/", StringComparison.OrdinalIgnoreCase));
        var version = ReadUnrealBuildVersion(buildVersion);
        if (version is not null) return $"Unreal Engine {version}";

        // Heroic can supply .../<Project>/Binaries/Win64 as InstallPath. Walk ancestors just far
        // enough to find the sibling Engine/Build/Build.version used by upstream RHI.
        var current = string.IsNullOrWhiteSpace(installPath) ? null : new DirectoryInfo(installPath);
        for (var depth = 0; current is not null && depth < 5; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "Engine", "Build", "Build.version");
            version = ReadUnrealBuildVersion(candidate);
            if (version is not null) return $"Unreal Engine {version}";
        }

        return null;
    }

    private static string? ReadUnrealBuildVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("MajorVersion", out var majorElement) ||
                !root.TryGetProperty("MinorVersion", out var minorElement)) return null;
            var major = majorElement.GetInt32();
            var minor = minorElement.GetInt32();
            if (major is < 4 or > 6 || minor < 0) return null;
            if (root.TryGetProperty("PatchVersion", out var patchElement))
            {
                var patch = patchElement.GetInt32();
                if (patch > 0) return $"{major}.{minor}.{patch}";
            }
            return $"{major}.{minor}";
        }
        catch
        {
            return null;
        }
    }

    // Kept as a compatibility seam for tests and early port code.
    public static GraphicsApi GuessGraphicsApi(string root)
    {
        var index = RHI.Core.Services.GameFileIndexer.Build(root);
        return GraphicsApiDetector.Detect(root, FindLikelyExecutable(root, index), index).Primary;
    }

    private static string NormalizeToken(string value)
        => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);
}
