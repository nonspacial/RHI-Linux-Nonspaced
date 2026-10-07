using System.Buffers;
using System.Text;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Cross-platform Windows graphics API detection. Strong executable/engine signatures are
/// preferred, with conservative config-file fallbacks for engines that resolve D3D/Vulkan
/// dynamically (common in modern games). No Windows registry or native Win32 APIs are used.
/// </summary>
internal static class GraphicsApiDetector
{
    private sealed class Signature
    {
        public Signature(string text, GraphicsApi api)
        {
            Text = text;
            Api = api;
            Ascii = Encoding.ASCII.GetBytes(text);
            Utf16 = Encoding.Unicode.GetBytes(text);
        }

        public string Text { get; }
        public GraphicsApi Api { get; }
        public byte[] Ascii { get; }
        public byte[] Utf16 { get; }
    }

    private static readonly Signature[] StrongSignatures =
    [
        new("d3d12.dll", GraphicsApi.DirectX12),
        new("d3d12createdevice", GraphicsApi.DirectX12),
        new("id3d12device", GraphicsApi.DirectX12),
        new("d3d11.dll", GraphicsApi.DirectX11),
        new("d3d11createdevice", GraphicsApi.DirectX11),
        new("d3d10.dll", GraphicsApi.DirectX10),
        new("d3d10createdevice", GraphicsApi.DirectX10),
        new("d3d9.dll", GraphicsApi.DirectX9),
        new("direct3dcreate9", GraphicsApi.DirectX9),
        new("d3d8.dll", GraphicsApi.DirectX8),
        new("direct3dcreate8", GraphicsApi.DirectX8),
        new("vulkan-1.dll", GraphicsApi.Vulkan),
        new("vkcreateinstance", GraphicsApi.Vulkan),
        new("opengl32.dll", GraphicsApi.OpenGL),
        new("wglcreatecontext", GraphicsApi.OpenGL)
    ];

    private static readonly Signature[] NativeSignatures =
    [
        new("libvulkan.so", GraphicsApi.Vulkan),
        new("vkcreateinstance", GraphicsApi.Vulkan),
        new("libgl.so", GraphicsApi.OpenGL),
        new("libglx.so", GraphicsApi.OpenGL),
        new("glxcreatecontext", GraphicsApi.OpenGL)
    ];

    private static readonly GraphicsApi[] Priority =
    [
        GraphicsApi.DirectX12,
        GraphicsApi.DirectX11,
        GraphicsApi.DirectX10,
        GraphicsApi.DirectX9,
        GraphicsApi.DirectX8,
        GraphicsApi.Vulkan,
        GraphicsApi.OpenGL
    ];

    private static readonly string[] ConfigExtensions =
    [".ini", ".cfg", ".conf", ".json", ".xml", ".toml", ".txt"];

    public static GraphicsApiDetection Detect(
        string gameRoot,
        string? executablePath,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
        => Detect(gameRoot, executablePath, indexedFiles, null, cancellationToken);

    public static GraphicsApiDetection Detect(
        string gameRoot,
        string? executablePath,
        IReadOnlyList<string>? indexedFiles,
        string? prefixPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        indexedFiles ??= [];

        // 0) Runtime evidence is stronger than static capability strings. Unity Player.log,
        // when present inside a Wine/Proton prefix, records the renderer that was actually used.
        var runtime = DetectFromRuntimeLogs(gameRoot, executablePath, prefixPath, cancellationToken);
        if (runtime.Primary != GraphicsApi.Unknown) return runtime;

        var hasExecutable = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);

        // Unity boot.config records the renderer selected by the player build and is stronger
        // evidence than broad capability/import strings in UnityPlayer.dll or the game executable.
        // Older Unity builds may omit boot.config; the presence of a *_Data directory still
        // means the Windows player defaults to D3D11.
        if (hasExecutable && !DetectionHelpers.IsNativeExecutable(executablePath))
        {
            var unityBoot = DetectFromUnityBootConfig(gameRoot);
            if (unityBoot.Primary != GraphicsApi.Unknown) return unityBoot;
        }

        if (hasExecutable && DetectionHelpers.IsNativeExecutable(executablePath))
        {
            var native = DetectNative(gameRoot, executablePath!, indexedFiles, cancellationToken);
            if (native.Primary != GraphicsApi.Unknown || native.Candidates.Count > 0) return native;
        }

        // 1) D3D12 Agility SDK is authoritative runtime evidence. Games can import D3D11
        // capability strings while actually loading an app-local D3D12Core.dll. The runtime
        // is commonly beside the executable, but some titles keep it in a child D3D12/ folder.
        // Upstream RHI performs this check ahead of PE scanning; Linux mirrors that ordering.
        var binaryDir = !string.IsNullOrWhiteSpace(executablePath)
            ? Path.GetDirectoryName(executablePath)
            : null;
        var directFolders = new[] { binaryDir, gameRoot }
            .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>();

        var direct = new HashSet<GraphicsApi>();
        var evidence = new List<string>();
        foreach (var folder in directFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddMarker(folder, "D3D12Core.dll", GraphicsApi.DirectX12, direct, evidence);
            AddMarker(folder, "vulkan-1.dll", GraphicsApi.Vulkan, direct, evidence);
        }

        // The selected-game file index is already bounded and reused by detection. Consume it
        // before PE scanning so app-local Agility layouts such as Win64/D3D12/D3D12Core.dll
        // retain the same strong authority as a marker placed directly beside the executable.
        if (!direct.Contains(GraphicsApi.DirectX12))
        {
            var agilityMarker = indexedFiles
                .Where(path => Path.GetFileName(path).Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .OrderBy(path => BinaryAffinity(path, binaryDir, gameRoot))
                .ThenBy(path => RelativeDepth(gameRoot, Path.GetDirectoryName(path) ?? gameRoot))
                .FirstOrDefault();

            // Keep the overload without a prebuilt index useful as well, but bound the fallback
            // walk to the executable directory and two child levels.
            if (agilityMarker is null && !string.IsNullOrWhiteSpace(binaryDir) && Directory.Exists(binaryDir))
            {
                var nestedMarkers = new List<string>();
                CollectNamedFiles(binaryDir, "D3D12Core.dll", 2, nestedMarkers, 4, cancellationToken);
                agilityMarker = nestedMarkers
                    .OrderBy(path => BinaryAffinity(path, binaryDir, gameRoot))
                    .ThenBy(path => RelativeDepth(gameRoot, Path.GetDirectoryName(path) ?? gameRoot))
                    .FirstOrDefault();
            }

            if (agilityMarker is not null)
            {
                direct.Add(GraphicsApi.DirectX12);
                evidence.Add($"D3D12Core.dll app-local runtime marker: {EvidenceRelativePath(gameRoot, agilityMarker)}");
            }
        }

        if (direct.Count > 0)
        {
            var ordered = Order(direct);
            return new GraphicsApiDetection(
                ordered[0], ordered, GraphicsApiConfidence.High,
                string.Join("; ", evidence));
        }

        // 2) Quick selected-executable pass. Most ordinary PE imports are found near the
        // front, so do not scan a huge game binary in full before checking explicit config.
        if (hasExecutable)
        {
            var quickExe = ScanBinary(executablePath!, 32L * 1024 * 1024, cancellationToken);
            if (quickExe.Count > 0)
            {
                var ordered = Order(quickExe);
                return new GraphicsApiDetection(
                    ordered[0], ordered, GraphicsApiConfidence.High,
                    $"Executable signature: {string.Join(", ", ordered.Select(ApiDllName))}");
            }
        }

        // 3) Engines such as RE Engine can LoadLibrary the renderer rather than statically
        // importing it. A selected config key is better evidence than an expensive blind scan.
        var config = DetectFromConfigs(gameRoot, executablePath, indexedFiles, cancellationToken);
        if (config.Primary != GraphicsApi.Unknown) return config;

        // 4) If no explicit config exists, perform a deeper executable scan. This remains
        // background/cancellable and covers large binaries whose renderer strings occur late.
        if (hasExecutable)
        {
            var deepExe = ScanBinary(executablePath!, 512L * 1024 * 1024, cancellationToken);
            if (deepExe.Count > 0)
            {
                var ordered = Order(deepExe);
                return new GraphicsApiDetection(
                    ordered[0], ordered, GraphicsApiConfidence.High,
                    $"Executable deep signature: {string.Join(", ", ordered.Select(ApiDllName))}");
            }
        }

        // 5) Unity keeps renderer implementation in UnityPlayer.dll and records broad
        // capabilities there. Treat one unique renderer as medium evidence, but retain an
        // ambiguous candidate list instead of inventing a primary API when several coexist.
        var unity = DetectFromUnityFiles(indexedFiles, cancellationToken);
        if (unity.Primary != GraphicsApi.Unknown || unity.Candidates.Count > 0) return unity;

        // 6) Last fallback: inspect a few likely engine binaries from the already-built
        // file index. This avoids another recursive directory walk and stays bounded.
        var candidates = indexedFiles
            .Where(f => (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) &&
                        !IsKnownInjectionOrProxyBinary(f) &&
                        !string.Equals(f, executablePath, StringComparison.Ordinal))
            .OrderBy(f => BinaryAffinity(f, binaryDir, gameRoot))
            .ThenByDescending(SafeLength)
            .Take(8)
            .ToArray();

        var nearby = new HashSet<GraphicsApi>();
        foreach (var binary in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var api in ScanBinary(binary, 24L * 1024 * 1024, cancellationToken))
                nearby.Add(api);
        }

        if (nearby.Count == 0) return GraphicsApiDetection.Unknown;
        var nearbyOrdered = Order(nearby);
        if (nearbyOrdered.Count >= 3)
        {
            return new GraphicsApiDetection(
                GraphicsApi.Unknown, nearbyOrdered, GraphicsApiConfidence.Low,
                $"Ambiguous nearby engine signatures: {string.Join(", ", nearbyOrdered.Select(ApiDllName))}");
        }
        return new GraphicsApiDetection(
            nearbyOrdered[0], nearbyOrdered, GraphicsApiConfidence.Medium,
            $"Nearby engine binary signature: {string.Join(", ", nearbyOrdered.Select(ApiDllName))}");
    }

    /// <summary>
    /// True when a result came only from PE import/string scanning (steps 2, 4 and 6 above), which
    /// legacy D3D shims in NW.js/Electron runtimes can fool. Logs and config files are not included.
    /// </summary>
    internal static bool IsBinarySignatureEvidence(string? evidence)
        => evidence is not null
           && (evidence.StartsWith("Executable signature:", StringComparison.Ordinal)
               || evidence.StartsWith("Executable deep signature:", StringComparison.Ordinal)
               || evidence.StartsWith("Nearby engine binary signature:", StringComparison.Ordinal));

    private static GraphicsApiDetection DetectFromConfigs(
        string gameRoot,
        string? executablePath,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken)
    {
        var binaryDir = !string.IsNullOrWhiteSpace(executablePath) ? Path.GetDirectoryName(executablePath) : null;
        var candidates = indexedFiles
            .Where(IsLikelyConfigFile)
            .OrderBy(f => ConfigAffinity(f, binaryDir, gameRoot))
            .ThenBy(f => SafeLength(f))
            .Take(80);

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SafeLength(file) is <= 0 or > 2 * 1024 * 1024) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            foreach (var raw in lines.Take(6000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = NormalizeConfigLine(raw);
                if (line.Length == 0 || !LooksLikeRendererSetting(line)) continue;

                var found = APIsInConfigLine(line);
                if (found.Count != 1) continue; // ambiguous option lists are not evidence.
                var api = found[0];
                return new GraphicsApiDetection(
                    api, [api], GraphicsApiConfidence.Medium,
                    $"Config selection: {Path.GetFileName(file)} ({ConfigEvidenceToken(api)})");
            }
        }

        return GraphicsApiDetection.Unknown;
    }

    private static bool IsLikelyConfigFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (!ConfigExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(path);
        return name.Contains("config", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("graphic", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("render", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("setting", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Engine.ini", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeConfigLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '\'') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool LooksLikeRendererSetting(string line)
    {
        string[] keys =
        [
            "capability", "targetplatform", "graphicsapi", "renderapi", "renderer", "renderingapi",
            "defaultgraphicsrhi", "defaultrhi", "preferredrhi", "backend"
        ];
        return keys.Any(k => line.Contains(k + "=", StringComparison.Ordinal) ||
                             line.Contains(k + ":", StringComparison.Ordinal));
    }

    private static IReadOnlyList<GraphicsApi> APIsInConfigLine(string line)
    {
        var result = new List<GraphicsApi>(2);
        if (ContainsAny(line, "directx12", "directx_12", "dx12", "d3d12", "defaultgraphicsrhi_dx12")) result.Add(GraphicsApi.DirectX12);
        if (ContainsAny(line, "directx11", "directx_11", "dx11", "d3d11", "defaultgraphicsrhi_dx11")) result.Add(GraphicsApi.DirectX11);
        if (ContainsAny(line, "directx10", "dx10", "d3d10")) result.Add(GraphicsApi.DirectX10);
        if (ContainsAny(line, "directx9", "dx9", "d3d9")) result.Add(GraphicsApi.DirectX9);
        if (ContainsAny(line, "directx8", "dx8", "d3d8")) result.Add(GraphicsApi.DirectX8);
        if (line.Contains("vulkan", StringComparison.Ordinal)) result.Add(GraphicsApi.Vulkan);
        if (ContainsAny(line, "opengl", "ogl")) result.Add(GraphicsApi.OpenGL);
        return result.Distinct().ToArray();
    }

    private static bool ContainsAny(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static HashSet<GraphicsApi> ScanBinary(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
        => ScanBinary(path, maxBytes, StrongSignatures, cancellationToken);

    private static HashSet<GraphicsApi> ScanBinary(
        string path,
        long maxBytes,
        IReadOnlyList<Signature> signatures,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<GraphicsApi>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024 + 256);
            try
            {
                var carry = 0;
                long scanned = 0;
                while (scanned < maxBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(1024 * 1024, maxBytes - scanned);
                    if (toRead <= 0) break;
                    var read = stream.Read(buffer, carry, toRead);
                    if (read <= 0) break;
                    scanned += read;
                    var length = carry + read;
                    var span = buffer.AsSpan(0, length);

                    // Lowercase ASCII once per chunk, then use the runtime's optimized Span.IndexOf
                    // for every signature. Older Linux code used a byte-by-byte nested loop per signature.
                    LowerAsciiInPlace(span);
                    foreach (var signature in signatures)
                    {
                        if (span.IndexOf(signature.Ascii) >= 0 || span.IndexOf(signature.Utf16) >= 0)
                            found.Add(signature.Api);
                    }

                    carry = Math.Min(192, length);
                    span[(length - carry)..].CopyTo(buffer);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return found;
    }

    private static GraphicsApiDetection DetectNative(
        string gameRoot,
        string executablePath,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken)
    {
        var found = ScanBinary(executablePath, 256L * 1024 * 1024, NativeSignatures, cancellationToken);
        if (found.Count == 1)
        {
            var api = found.Single();
            return new GraphicsApiDetection(
                api, [api], GraphicsApiConfidence.High,
                $"Native executable signature: {ApiNativeToken(api)}");
        }
        if (found.Count > 1)
        {
            var ordered = Order(found);
            return new GraphicsApiDetection(
                GraphicsApi.Unknown, ordered, GraphicsApiConfidence.Medium,
                $"Native executable exposes multiple renderer backends: {string.Join(", ", ordered.Select(ApiNativeToken))}");
        }

        var fna = indexedFiles.Any(f =>
            Path.GetFileName(f).Equals("FNA.dll", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(f).Contains("FNA3D", StringComparison.OrdinalIgnoreCase));
        if (fna)
        {
            return new GraphicsApiDetection(
                GraphicsApi.Unknown, [GraphicsApi.Vulkan, GraphicsApi.OpenGL], GraphicsApiConfidence.Medium,
                "Native FNA/FNA3D game: active backend is selected at runtime (Vulkan or OpenGL)");
        }

        // A native helper script can launch an ELF deeper in the tree. Reuse the already-built
        // index and inspect a small set of ELF candidates without another recursive walk.
        var nativeCandidates = indexedFiles
            .Where(f => !string.Equals(f, executablePath, StringComparison.Ordinal) && IsElfFile(f))
            .OrderBy(f => BinaryAffinity(f, Path.GetDirectoryName(executablePath), gameRoot))
            .ThenByDescending(SafeLength)
            .Take(6);
        foreach (var binary in nativeCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var api in ScanBinary(binary, 64L * 1024 * 1024, NativeSignatures, cancellationToken))
                found.Add(api);
        }
        if (found.Count == 1)
        {
            var api = found.Single();
            return new GraphicsApiDetection(
                api, [api], GraphicsApiConfidence.Medium,
                $"Native engine binary signature: {ApiNativeToken(api)}");
        }
        if (found.Count > 1)
        {
            var ordered = Order(found);
            return new GraphicsApiDetection(
                GraphicsApi.Unknown, ordered, GraphicsApiConfidence.Medium,
                $"Native engine supports multiple renderer backends: {string.Join(", ", ordered.Select(ApiNativeToken))}");
        }
        return GraphicsApiDetection.Unknown;
    }

    private static GraphicsApiDetection DetectFromRuntimeLogs(
        string gameRoot,
        string? executablePath,
        string? prefixPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prefixPath) || !Directory.Exists(prefixPath))
            return GraphicsApiDetection.Unknown;

        var driveC = Path.Combine(prefixPath, "drive_c");
        if (!Directory.Exists(driveC)) return GraphicsApiDetection.Unknown;

        var candidates = new List<string>();
        try
        {
            foreach (var users in Directory.EnumerateDirectories(Path.Combine(driveC, "users")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localLow = Path.Combine(users, "AppData", "LocalLow");
                if (!Directory.Exists(localLow)) continue;
                CollectNamedFiles(localLow, "Player.log", 5, candidates, 12, cancellationToken);
                CollectNamedFiles(localLow, "Player-prev.log", 5, candidates, 12, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        var isIsolatedSteamPrefix = prefixPath.Replace('\\', '/').Contains("/steamapps/compatdata/", StringComparison.OrdinalIgnoreCase);
        var rootToken = NormalizeLooseToken(Path.GetFileName(gameRoot));
        var exeToken = NormalizeLooseToken(Path.GetFileNameWithoutExtension(executablePath ?? ""));
        var orderedLogs = candidates
            .Where(log => isIsolatedSteamPrefix || RuntimeLogMatchesGame(log, rootToken, exeToken))
            .OrderByDescending(SafeLastWriteUtc);

        foreach (var log in orderedLogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> lines;
            try { lines = ReadTailLines(log, 8 * 1024 * 1024); }
            catch { continue; }

            foreach (var raw in lines.Reverse().Take(12000))
            {
                var line = raw.ToLowerInvariant();
                if (ContainsAny(line, "direct3d 12", "direct3d12", "d3d12 device", "d3d12:"))
                    return RuntimeLogResult(GraphicsApi.DirectX12, log);
                if (ContainsAny(line, "direct3d 11", "direct3d11", "d3d11 device", "d3d11:"))
                    return RuntimeLogResult(GraphicsApi.DirectX11, log);
                if (line.Contains("vulkan api", StringComparison.Ordinal) || line.Contains("gfxdevice: vulkan", StringComparison.Ordinal))
                    return RuntimeLogResult(GraphicsApi.Vulkan, log);
                if (line.Contains("opengl", StringComparison.Ordinal) &&
                    (line.Contains("version", StringComparison.Ordinal) || line.Contains("gfxdevice", StringComparison.Ordinal)))
                    return RuntimeLogResult(GraphicsApi.OpenGL, log);
            }
        }
        return GraphicsApiDetection.Unknown;
    }

    /// <summary>
    /// A shared prefix can hold several games' Unity logs. Attribution uses only the product folders
    /// below <c>AppData/LocalLow/&lt;Company&gt;/</c>: the prefix directory and user name are not game
    /// identity (a launcher whose prefix is named after it matched every log inside it), and the company
    /// folder alone is not either (a "Ubisoft" launcher card must not inherit a Ubisoft game's log) (Refs #52).
    /// </summary>
    internal static bool RuntimeLogMatchesGame(string log, string rootToken, string exeToken)
    {
        var normalized = log.Replace('\\', '/');
        const string marker = "/AppData/LocalLow/";
        var at = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var folders = normalized[(at + marker.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToArray();
        var productFolders = folders.Length > 1 ? folders.Skip(1) : folders;
        foreach (var folder in productFolders)
        {
            var token = NormalizeLooseToken(folder);
            if ((rootToken.Length >= 5 && token.Contains(rootToken, StringComparison.Ordinal)) ||
                (exeToken.Length >= 5 && token.Contains(exeToken, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static string NormalizeLooseToken(string value)
        => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static GraphicsApiDetection RuntimeLogResult(GraphicsApi api, string log)
        => new(api, [api], GraphicsApiConfidence.High,
            $"Runtime renderer log: {Path.GetFileName(log)} ({ConfigEvidenceToken(api)})");

    internal static GraphicsApiDetection DetectFromUnityBootConfig(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return GraphicsApiDetection.Unknown;
        try
        {
            foreach (var dataDir in Directory.EnumerateDirectories(gameRoot, "*_Data", SearchOption.TopDirectoryOnly))
            {
                var bootConfig = Path.Combine(dataDir, "boot.config");
                if (!File.Exists(bootConfig))
                {
                    return new GraphicsApiDetection(
                        GraphicsApi.DirectX11, [GraphicsApi.DirectX11], GraphicsApiConfidence.High,
                        $"Unity data folder without boot.config: {Path.GetFileName(dataDir)}; Windows player defaults to DX11");
                }

                foreach (var raw in File.ReadLines(bootConfig))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("gfx-device-type=", StringComparison.OrdinalIgnoreCase)) continue;
                    var rawValue = line["gfx-device-type=".Length..].Trim();
                    if (!int.TryParse(rawValue, out var value)) continue;
                    var api = value switch
                    {
                        2 => GraphicsApi.DirectX9,
                        17 => GraphicsApi.DirectX11,
                        18 => GraphicsApi.DirectX12,
                        21 => GraphicsApi.Vulkan,
                        4 => GraphicsApi.OpenGL,
                        _ => GraphicsApi.DirectX11
                    };
                    return new GraphicsApiDetection(
                        api, [api], GraphicsApiConfidence.High,
                        $"Unity boot.config gfx-device-type={value} ({ConfigEvidenceToken(api)})");
                }

                return new GraphicsApiDetection(
                    GraphicsApi.DirectX11, [GraphicsApi.DirectX11], GraphicsApiConfidence.High,
                    "Unity boot.config has no gfx-device-type; Windows player defaults to DX11");
            }
        }
        catch
        {
            // Static probing remains available if Unity metadata is unreadable.
        }
        return GraphicsApiDetection.Unknown;
    }

    private static GraphicsApiDetection DetectFromUnityFiles(
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken)
    {
        var unityFiles = indexedFiles
            .Where(f => Path.GetFileName(f).Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => Path.GetFileName(f).Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToArray();
        if (unityFiles.Length == 0) return GraphicsApiDetection.Unknown;

        var found = new HashSet<GraphicsApi>();
        foreach (var file in unityFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var api in ScanBinary(file, 192L * 1024 * 1024, cancellationToken))
                found.Add(api);
        }

        var ordered = Order(found);
        if (ordered.Count == 1)
        {
            return new GraphicsApiDetection(
                ordered[0], ordered, GraphicsApiConfidence.Medium,
                $"Unity player data signature: {ApiDllName(ordered[0])}");
        }
        if (ordered.Count > 1)
        {
            return new GraphicsApiDetection(
                GraphicsApi.Unknown, ordered, GraphicsApiConfidence.Low,
                $"Unity runtime exposes multiple graphics backends: {string.Join(", ", ordered.Select(ApiDllName))}");
        }
        return GraphicsApiDetection.Unknown;
    }

    private static void CollectNamedFiles(
        string root,
        string fileName,
        int maxDepth,
        ICollection<string> result,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var baseDepth = RelativeDepth(root, root);
        var stack = new Stack<(string path, int depth)>();
        stack.Push((root, baseDepth));
        while (stack.Count > 0 && result.Count < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) result.Add(candidate);
                if (depth >= maxDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    stack.Push((sub, depth + 1));
            }
            catch { }
        }
    }

    private static bool IsElfFile(string path)
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

    private static IReadOnlyList<string> ReadTailLines(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes)
            stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        if (stream.Position > 0) _ = reader.ReadLine(); // discard a potentially partial first line
        return reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static void LowerAsciiInPlace(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b is >= (byte)'A' and <= (byte)'Z') data[i] = (byte)(b + 32);
        }
    }

    private static bool IsKnownInjectionOrProxyBinary(string path)
    {
        var name = Path.GetFileName(path);
        string[] exact =
        [
            "dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "dinput8.dll",
            "winmm.dll", "version.dll", "ReShade64.dll", "OptiScaler.dll"
        ];
        if (exact.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase))) return true;
        return name.Contains("reshade", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("optiscaler", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("renodx", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("sl.", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddMarker(
        string folder,
        string fileName,
        GraphicsApi api,
        ISet<GraphicsApi> found,
        ICollection<string> evidence)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return;
        found.Add(api);
        evidence.Add($"{fileName} beside game binary");
    }

    private static string EvidenceRelativePath(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch { return path; }
    }

    private static IReadOnlyList<GraphicsApi> Order(IEnumerable<GraphicsApi> apis)
    {
        var set = apis.ToHashSet();
        return Priority.Where(set.Contains).ToArray();
    }

    private static int BinaryAffinity(string path, string? binaryDir, string gameRoot)
    {
        var dir = Path.GetDirectoryName(path) ?? gameRoot;
        if (!string.IsNullOrWhiteSpace(binaryDir))
        {
            try
            {
                if (Path.GetFullPath(dir) == Path.GetFullPath(binaryDir)) return 0;
            }
            catch { }
        }
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/Binaries/Win64/", StringComparison.OrdinalIgnoreCase)) return 1;
        if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) return 2;
        return 10 + RelativeDepth(gameRoot, dir);
    }

    private static int ConfigAffinity(string path, string? binaryDir, string gameRoot)
    {
        var dir = Path.GetDirectoryName(path) ?? gameRoot;
        try
        {
            if (!string.IsNullOrWhiteSpace(binaryDir) && Path.GetFullPath(dir) == Path.GetFullPath(binaryDir)) return 0;
            if (Path.GetFullPath(dir) == Path.GetFullPath(gameRoot)) return 1;
        }
        catch { }
        var name = Path.GetFileName(path);
        if (name.Equals("Engine.ini", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase)) return 2;
        return 10 + RelativeDepth(gameRoot, dir);
    }

    private static int RelativeDepth(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel == ".") return 0;
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
        }
        catch { return 1000; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static string ApiNativeToken(GraphicsApi api) => api switch
    {
        GraphicsApi.Vulkan => "libvulkan/vkCreateInstance",
        GraphicsApi.OpenGL => "libGL/GLX",
        _ => ApiDllName(api)
    };

    private static string ApiDllName(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => "d3d12.dll",
        GraphicsApi.DirectX11 => "d3d11.dll",
        GraphicsApi.DirectX10 => "d3d10.dll",
        GraphicsApi.DirectX9 => "d3d9.dll",
        GraphicsApi.DirectX8 => "d3d8.dll",
        GraphicsApi.Vulkan => "vulkan-1.dll",
        GraphicsApi.OpenGL => "opengl32.dll",
        _ => "unknown"
    };

    private static string ConfigEvidenceToken(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => "DX12/D3D12",
        GraphicsApi.DirectX11 => "DX11/D3D11",
        GraphicsApi.DirectX10 => "DX10/D3D10",
        GraphicsApi.DirectX9 => "DX9/D3D9",
        GraphicsApi.DirectX8 => "DX8/D3D8",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => "Unknown"
    };
}
