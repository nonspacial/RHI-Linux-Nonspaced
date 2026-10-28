using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>Finds standalone Wine prefixes not already represented by Steam/Heroic/Lutris/umu.</summary>
public sealed class WinePrefixDetector : IGameDetector
{
    private readonly string _home;
    private readonly string? _dataHome;
    private readonly Func<IEnumerable<string>> _lutrisConfigRoots;
    private readonly Func<IEnumerable<string>> _installerAppStateFolders;

    public WinePrefixDetector() : this(LinuxPaths.Home, LinuxPaths.LutrisConfigRoots, LinuxPaths.DataHome) { }

    /// <param name="xboxFolders">The "Xbox / Game Pass folders" setting (w3/scan): prefixes of those instances are the installer app's.</param>
    public WinePrefixDetector(Xbox.XboxFolderStore xboxFolders)
        : this(LinuxPaths.Home, LinuxPaths.LutrisConfigRoots, LinuxPaths.DataHome, xboxFolders.Folders)
        => XboxFolderStore = xboxFolders;

    /// <summary>The "Xbox / Game Pass folders" setting this detector reads (the app passes its one shared store).</summary>
    internal Xbox.XboxFolderStore? XboxFolderStore { get; }

    internal WinePrefixDetector(string home, IEnumerable<string> lutrisConfigRoots, string? dataHome = null, IEnumerable<string>? xboxFolders = null)
        : this(home, () => lutrisConfigRoots, dataHome, () => xboxFolders ?? []) { }

    private WinePrefixDetector(string home, Func<IEnumerable<string>> lutrisConfigRoots, string? dataHome, Func<IEnumerable<string>>? xboxFolders = null)
    {
        _home = home;
        _dataHome = dataHome;
        _lutrisConfigRoots = lutrisConfigRoots;
        _installerAppStateFolders = xboxFolders ?? (() => []);
    }

    public string Name => "Wine";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    private IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var roots = new[]
        {
            Path.Combine(_home, ".wine"),
            Path.Combine(_home, "Games"),
            Path.Combine(_home, ".local", "share", "bottles", "bottles")
        };
        // The installer app's title prefixes (Refs #90): every environment root its breadcrumbs, default state folders and the
        // user's installer-app folders name.
        var installerApp = Xbox.XboxInstallerInstance.KnownEnvironmentRoots(
            Path.Combine(_dataHome ?? Path.Combine(_home, ".local", "share"), XboxInstallerApp.FolderName, "instances"),
            _installerAppStateFolders().Append(Path.Combine(_home, ".local", "state", XboxInstallerApp.FolderName)));
        var owned = LauncherOwnedPrefixes.Load(_home, _lutrisConfigRoots(), _dataHome, installerApp);
        var games = new List<DetectedGame>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var prefix in FindPrefixes(root, 3))
            {
                ct.ThrowIfCancellationRequested();

                // Steam, Heroic, Lutris and umu already have authoritative entries for these prefixes.
                // Walking them again duplicates games and turns support applications into fake entries.
                if (DetectionHelpers.IsLauncherManagedPrefix(prefix) || owned.Owns(prefix))
                    continue;

                // Without its runtime markers RHI cannot tell a Proton prefix from a Wine one, and host wine would run
                // wineboot over a Proton prefix. The games stay listed (and moddable) without a launch command, like a
                // Proton prefix whose Wine is gone; the warning names the prefix (Refs #144, w2-launch review major 2).
                PrefixRuntime? runtime;
                Exception? unreadable = null;
                try { runtime = PrefixRuntime.Read(prefix); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable = ex;
                    runtime = null;
                }
                var listedBefore = games.Count;
                var records = Launchers.StoreLauncherRecords.Find(prefix, _home, ct)
                    .ToDictionary(record => PathIdentity.Canonicalize(record.InstallDirectory), StringComparer.Ordinal);
                var entries = PrefixGameFolders.Find(Path.Combine(prefix, "drive_c")).ToList();
                // Games a store launcher recorded outside Program Files and drive_c/Games (another drive, a custom
                // library folder) are listed too, unless they sit inside a folder listed already.
                var entryFolders = entries.Select(entry => PathIdentity.Canonicalize(entry.Directory)).ToHashSet(StringComparer.Ordinal);
                entries.AddRange(records
                    .Where(pair => !entryFolders.Contains(pair.Key) &&
                                   !entryFolders.Any(folder => pair.Key.StartsWith(folder + "/", StringComparison.Ordinal)))
                    .Select(pair => new PrefixGameFolders.Entry(pair.Value.InstallDirectory, null, null)));
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var appDir = entry.Directory;
                    records.TryGetValue(PathIdentity.Canonicalize(appDir), out var record);
                    string? exe;
                    try { exe = record?.GameExecutable ?? PrefixGameFolders.FindExecutable(entry, ct); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        DetectorDiagnostics.Warn($"Wine prefix folder {appDir} skipped", ex);
                        continue;
                    }
                    if (exe is null) continue;
                    // A store launcher's game starts through that launcher (its own launch argument), like Lutris' store
                    // services do; without the launcher's exe the game exe is started directly, as upstream does.
                    var launcherExe = record?.LauncherExecutable;
                    games.Add(new DetectedGame
                    {
                        Name = record?.Name ?? Path.GetFileName(appDir),
                        InstallPath = appDir,
                        Source = "Wine",
                        PrefixPath = prefix,
                        ExecutablePath = exe,
                        LaunchExecutablePath = launcherExe ?? exe,
                        Runner = runtime is null ? null : runtime.IsProton ? "Proton" : "Wine",
                        CompatibilityTool = runtime?.Version,
                        // Unknown runtime: every Wine-running path (Winetricks, the D3DCompiler step) refuses it.
                        CompatibilityLayerType = runtime is null
                            ? D3DCompilerToolInstaller.UnknownRuntimeLayer
                            : runtime.IsProton ? "proton" : null,
                        WineBinaryPath = runtime?.WineBinary,
                        WineServerPath = runtime?.WineServer,
                        LaunchCommand = runtime is null ? null
                            : launcherExe is null ? BuildLaunchCommand(prefix, exe, runtime, [])
                            : BuildLaunchCommand(prefix, launcherExe, runtime, record!.PlayArguments),
                        // With no launch argument the launcher only opens: the user starts the game there.
                        ParentLauncherName = launcherExe is not null && record!.PlayArguments.Count == 0 ? record.LauncherName : null
                    });
                }
                // w12 follow-up (merge review nit 1): the games stay listed, so the scan says how many and that Play needs a
                // command set in Edit game (its own line, never "skipped"); with none found it is only logged.
                if (unreadable is not null)
                {
                    var listed = games.Count - listedBefore;
                    var message = $"Wine prefix {prefix}: runtime markers unreadable; {listed} game(s) listed without a launch command";
                    if (listed > 0) DetectorDiagnostics.ListedWithoutLaunchCommand(listed, message, unreadable);
                    else AppLog.Warn("scan", message, unreadable);
                }
            }
        }
        return games;
    }

    /// <summary>
    /// A Proton-built prefix is only started with that Proton's own Wine: host `wine` runs
    /// wineboot over it and can break it. Without that Wine the entry is listed but not launchable.
    /// </summary>
    private static string? BuildLaunchCommand(string prefix, string exe, PrefixRuntime runtime, IReadOnlyList<string> arguments)
    {
        if (runtime.IsProton && runtime.WineBinary is null) return null;
        var command = $"WINEPREFIX={Shell(prefix)} {(runtime.WineBinary is null ? "wine" : Shell(runtime.WineBinary))} {Shell(exe)}";
        return arguments.Count == 0 ? command : command + " " + string.Join(' ', arguments.Select(Shell));
    }

    private static IEnumerable<string> FindPrefixes(string root, int maxDepth)
    {
        if (IsPrefix(root))
        {
            yield return root;
            yield break;
        }
        var startDepth = root.Count(c => c == Path.DirectorySeparatorChar);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (IsPrefix(dir))
            {
                yield return dir;
                continue;
            }
            var depth = dir.Count(c => c == Path.DirectorySeparatorChar) - startDepth;
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // A symlinked folder is followed only when it is itself a prefix (a prefix kept on
                    // another disk); any other link could lead anywhere, / included.
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0 && !IsPrefix(sub)) continue;
                    pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder is skipped.
                DetectorDiagnostics.Warn($"Wine prefix search skipped {dir}", ex);
            }
        }
    }

    private static bool IsPrefix(string dir)
        => Directory.Exists(Path.Combine(dir, "drive_c")) && File.Exists(Path.Combine(dir, "system.reg"));

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Runtime that built a prefix. Proton/umu write `config_info` (line 1: version, line 2: the
    /// dist's share/fonts/) and `version`; plain Wine writes neither.
    /// </summary>
    internal sealed record PrefixRuntime(bool IsProton, string? Version, string? WineBinary, string? WineServer)
    {
        public static PrefixRuntime Read(string prefix) => Read(prefix, FindOnPath);

        /// <param name="onPath">Resolves a program name on PATH (host Wine), injectable for tests.</param>
        internal static PrefixRuntime Read(string prefix, Func<string, string?> onPath)
        {
            // Steam-style layout keeps the markers beside `pfx/`.
            var dirs = Path.GetFileName(prefix) == "pfx" ? new[] { prefix, Path.GetDirectoryName(prefix)! } : [prefix];
            foreach (var dir in dirs)
            {
                var configInfo = ReadLines(Path.Combine(dir, "config_info"));
                var version = ReadLines(Path.Combine(dir, "version"));
                if (configInfo is null && version is null) continue;

                var name = configInfo?.FirstOrDefault()?.Trim() ?? version!.FirstOrDefault()?.Trim();
                string? wine = null, server = null;
                var fonts = configInfo?.ElementAtOrDefault(1)?.Trim();
                if (!string.IsNullOrEmpty(fonts) && Path.IsPathRooted(fonts))
                {
                    var bin = Path.GetFullPath(Path.Combine(fonts.TrimEnd('/'), "..", "..", "bin"));
                    if (IsProtonDistBin(bin))
                    {
                        wine = new[] { "wine", "wine64" }.Select(n => Path.Combine(bin, n)).FirstOrDefault(File.Exists);
                        // Never the host's Wine, whatever the path says: it would run wineboot over the prefix.
                        if (wine is not null && IsHostWine(wine, onPath)) wine = null;
                    }
                    var serverPath = Path.Combine(bin, "wineserver");
                    server = wine is not null && File.Exists(serverPath) ? serverPath : null;
                }
                return new PrefixRuntime(true, string.IsNullOrEmpty(name) ? null : name, wine, server);
            }
            return new PrefixRuntime(false, null, null, null);
        }

        /// <summary>
        /// A Proton build keeps its Wine in <c>&lt;dist&gt;/files/bin</c> (or <c>dist/bin</c> in older builds)
        /// beside a <c>proton</c> script or <c>version</c> file. Anything else (a stale or hand-edited
        /// config_info pointing at /usr/share/fonts, say) is not that Proton's Wine.
        /// </summary>
        internal static bool IsProtonDistBin(string bin)
        {
            var files = Path.GetDirectoryName(bin.TrimEnd('/'));
            if (files is null || Path.GetFileName(bin.TrimEnd('/')) != "bin") return false;
            if (Path.GetFileName(files) is not ("files" or "dist")) return false;
            var dist = Path.GetDirectoryName(files);
            return dist is not null &&
                   (File.Exists(Path.Combine(dist, "proton")) || File.Exists(Path.Combine(dist, "version")));
        }

        private static bool IsHostWine(string wine, Func<string, string?> onPath)
        {
            var canonical = PathIdentity.Canonicalize(wine);
            return new[] { "wine", "wine64" }
                .Select(onPath)
                .Any(host => host is not null && PathIdentity.Canonicalize(host) == canonical);
        }

        private static string? FindOnPath(string program)
            => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir, program))
                .FirstOrDefault(File.Exists);

        /// <summary>Null when the file does not exist; an unreadable one throws (the caller lists the prefix's games without a launch command).</summary>
        private static string[]? ReadLines(string path)
            => File.Exists(path) ? File.ReadAllLines(path) : null;
    }
}
