using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.Launchers;

/// <summary>The Windows store launchers whose install records RHI reads inside Wine prefixes (Refs #90).</summary>
internal enum StoreLauncher
{
    EaApp,
    UbisoftConnect,
    BattleNet,
    Rockstar,
    Itch,
    Epic
}

/// <summary>
/// One game a store launcher installed into a Wine prefix: its folder (Linux path), the store's id for it when the
/// record names one, and how that launcher is told to start it (<see cref="LauncherExecutable"/> plus
/// <see cref="PlayArguments"/>; no arguments = the launcher only opens). <see cref="GameExecutable"/> is the game's
/// executable when the store's record names it (else the folder is searched).
/// </summary>
internal sealed record StoreLauncherGame(
    StoreLauncher Store,
    string? StoreId,
    string Name,
    string InstallDirectory,
    string? LauncherExecutable,
    IReadOnlyList<string> PlayArguments,
    string? GameExecutable = null)
{
    public string LauncherName => StoreLauncherRecords.DisplayName(Store);
}

/// <summary>
/// Reads each store launcher's own install records in one Wine prefix (Refs #90), the Linux counterpart of upstream's
/// Windows detectors (GameDetectionService.Platform.cs): Ubisoft's <c>Launcher\Installs\&lt;id&gt;</c> registry keys,
/// Battle.net's <c>product.db</c> (names from its uninstall entries), the EA app's <c>Origin Games</c> keys, publisher keys
/// and <c>__Installer/installerdata.xml</c> files, and Rockstar's uninstall entries and <c>Rockstar Games\&lt;title&gt;</c>
/// keys (its <c>titles.dat</c> is encrypted on current launchers). Launch arguments follow Lutris' store services (the
/// Linux reference): <c>uplay://launch/&lt;id&gt;</c>, <c>--exec="launch &lt;code&gt;"</c>,
/// <c>origin2://game/launch?offerIds=&lt;id&gt;&amp;autoDownload=1</c>, and Rockstar's <c>-launchTitleInFolder &lt;dir&gt;</c>.
/// The Epic Games Launcher's records (classic manifests and the new launcher's install log, #247) are read by
/// <see cref="EpicLauncherRecords"/>.
/// Read only; a record whose folder is missing, refused by <see cref="GameRootGuard"/> or is the prefix itself is skipped.
/// </summary>
internal static class StoreLauncherRecords
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const long MaxInstallerDataBytes = 4L * 1024 * 1024;

    // Upstream's EA publisher keys (method 3); each subkey with an install folder value is one game.
    private static readonly string[] EaPublisherKeys =
        ["EA Games", "Electronic Arts", "Criterion Games", "Respawn", "BioWare", "DICE", "PopCap", "Ghost Games"];

    // Battle.net's own entries in product.db (Lutris' NOT_GAMES plus the app's uid).
    private static readonly string[] BattleNetClientCodes = ["agent", "bna", "battle.net"];

    public static string DisplayName(StoreLauncher store) => store switch
    {
        StoreLauncher.EaApp => "EA app",
        StoreLauncher.UbisoftConnect => "Ubisoft Connect",
        StoreLauncher.BattleNet => "Battle.net",
        StoreLauncher.Rockstar => "Rockstar Games Launcher",
        StoreLauncher.Itch => "itch",
        StoreLauncher.Epic => "Epic Games Launcher",
        _ => store.ToString()
    };

    /// <summary>The provider name <see cref="Settings.LinuxGameIdentityService"/> infers from this store's client exe.</summary>
    public static string ProviderName(StoreLauncher store) => store switch
    {
        StoreLauncher.EaApp => "EA",
        StoreLauncher.UbisoftConnect => "Ubisoft",
        StoreLauncher.BattleNet => "Battle.net",
        StoreLauncher.Rockstar => "Rockstar",
        StoreLauncher.Itch => "itch",
        StoreLauncher.Epic => "Epic",
        _ => store.ToString()
    };

    /// <summary>Every recorded install in <paramref name="prefix"/>, one per folder; each store's failure is a scan warning.</summary>
    public static IReadOnlyList<StoreLauncherGame> Find(string prefix, string home, CancellationToken ct)
    {
        WineRegistryReader registry;
        try { registry = WineRegistryReader.Load(Path.Combine(prefix, "system.reg"), RegistryRoots); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectorDiagnostics.Warn($"Wine prefix {prefix}: registry unreadable; games its store launchers recorded there are found by folder only", ex);
            registry = WineRegistryReader.Empty;
        }

        var games = new List<StoreLauncherGame>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var context = new Context(prefix, home, registry);
        foreach (var (store, read) in new (StoreLauncher, Func<Context, CancellationToken, IEnumerable<StoreLauncherGame>>)[]
                 {
                     (StoreLauncher.UbisoftConnect, Ubisoft),
                     (StoreLauncher.BattleNet, BattleNet),
                     (StoreLauncher.EaApp, Ea),
                     (StoreLauncher.Rockstar, Rockstar),
                     (StoreLauncher.Itch, Itch),
                     // Last: a game another store's record names keeps that store's hand-off, as before #247.
                     (StoreLauncher.Epic, EpicLauncherRecords.Read)
                 })
        {
            try
            {
                foreach (var game in read(context, ct).ToList())
                    if (seen.Add(PathIdentity.Canonicalize(game.InstallDirectory)))
                        games.Add(game);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
            {
                DetectorDiagnostics.Warn($"Wine prefix {prefix}: {DisplayName(store)} install records unreadable; its games are found by folder only", ex);
            }
        }
        return games;
    }

    private static readonly string[] RegistryRoots =
    [
        @"Software\Ubisoft\Launcher", @"Software\Wow6432Node\Ubisoft\Launcher",
        Uninstall, Uninstall32,
        @"Software\Origin Games", @"Software\Wow6432Node\Origin Games",
        .. EaPublisherKeys.SelectMany(key => new[] { @"Software\" + key, @"Software\Wow6432Node\" + key }),
        @"Software\Rockstar Games", @"Software\Wow6432Node\Rockstar Games",
        EpicLauncherRecords.UrlHandlerKey
    ];

    internal sealed record Context(string Prefix, string Home, WineRegistryReader Registry)
    {
        public string DriveC => Path.Combine(Prefix, "drive_c");

        /// <summary>The Linux folder a recorded Windows path names, when it is a real, allowed game folder.</summary>
        public string? GameFolder(string? windowsPath)
        {
            var folder = WindowsPathMapper.ToLinux(Prefix, windowsPath);
            if (folder is null || !System.IO.Directory.Exists(folder)) return null;
            folder = Path.TrimEndingDirectorySeparator(folder);
            var canonical = PathIdentity.Canonicalize(folder);
            // Never the prefix, its drive_c or a drive root: those are whole Windows images, not one game.
            if (canonical == PathIdentity.Canonicalize(Prefix) || canonical == PathIdentity.Canonicalize(DriveC)) return null;
            if (LutrisGameDetector.IsWinePrefixRoot(folder)) return null;
            return GameRootGuard.IsAcceptable(folder, Home) ? folder : null;
        }

        public string? ExistingFile(string? windowsDirectory, params string[] names)
        {
            var directory = WindowsPathMapper.ToLinux(Prefix, windowsDirectory);
            return directory is null ? null : names.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
        }

        public string? DefaultFile(params string[] relativePaths)
            => relativePaths.Select(relative => Path.Combine(DriveC, relative)).FirstOrDefault(File.Exists);

        /// <summary>Values of every direct subkey of a key, in both registry views.</summary>
        public IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> Subkeys(string key)
        {
            foreach (var view in new[] { key, key.Replace(@"Software\", @"Software\Wow6432Node\", StringComparison.Ordinal) }.Distinct())
                foreach (var name in Registry.SubkeyNames(view))
                    yield return (name, Registry.Values(view + "\\" + name));
        }

        public string? Value(string key, string name)
            => Registry.Value(key.Replace(@"Software\", @"Software\Wow6432Node\", StringComparison.Ordinal), name) ?? Registry.Value(key, name);
    }

    // ── Ubisoft Connect ────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<StoreLauncherGame> Ubisoft(Context context, CancellationToken ct)
    {
        var launcher = context.ExistingFile(context.Value(@"Software\Ubisoft\Launcher", "InstallDir"), "UbisoftConnect.exe", "upc.exe")
                       ?? context.DefaultFile(@"Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe",
                           @"Program Files/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe");
        foreach (var (id, values) in context.Subkeys(@"Software\Ubisoft\Launcher\Installs"))
        {
            ct.ThrowIfCancellationRequested();
            if (!values.TryGetValue("InstallDir", out var installDir) || context.GameFolder(installDir) is not { } folder) continue;
            // Ubisoft's keys carry only a numeric id; the folder is the game's title (upstream does the same).
            yield return new StoreLauncherGame(StoreLauncher.UbisoftConnect, id, Path.GetFileName(folder), folder, launcher,
                IsStoreId(id) ? [$"uplay://launch/{id}"] : []);
        }
    }

    // ── Battle.net ─────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<StoreLauncherGame> BattleNet(Context context, CancellationToken ct)
    {
        var products = BattleNetProductDb.Read(Path.Combine(context.DriveC, "ProgramData", "Battle.net", "Agent", "product.db"));
        if (products.Count == 0) yield break;
        var client = products.FirstOrDefault(p => p.ProductCode.Equals("bna", StringComparison.OrdinalIgnoreCase));
        var launcher = context.ExistingFile(client?.InstallPath, "Battle.net.exe", "Battle.net Launcher.exe")
                       ?? context.DefaultFile("Program Files (x86)/Battle.net/Battle.net.exe", "Program Files (x86)/Battle.net/Battle.net Launcher.exe");
        var uninstall = context.Subkeys(Uninstall)
            .Where(entry => entry.Values.TryGetValue("Publisher", out var publisher) && IsBlizzard(publisher))
            .ToList();
        foreach (var product in products)
        {
            ct.ThrowIfCancellationRequested();
            if (!product.Installed || BattleNetClientCodes.Contains(product.ProductCode, StringComparer.OrdinalIgnoreCase) ||
                BattleNetClientCodes.Contains(product.Uid, StringComparer.OrdinalIgnoreCase))
                continue;
            if (context.GameFolder(product.InstallPath) is not { } folder) continue;
            // The uninstall entry Battle.net writes per product names it (`--uid=<uid>` in its uninstall command).
            var name = uninstall
                .Where(entry => entry.Values.TryGetValue("UninstallString", out var command) &&
                                Regex.IsMatch(command, $@"--uid={Regex.Escape(product.Uid)}(\s|""|$)", RegexOptions.IgnoreCase))
                .Select(entry => entry.Values.GetValueOrDefault("DisplayName"))
                .FirstOrDefault(display => !string.IsNullOrWhiteSpace(display))?.Trim();
            var code = product.ProductCode.Length > 0 ? product.ProductCode : product.Uid;
            yield return new StoreLauncherGame(StoreLauncher.BattleNet, code, name ?? Path.GetFileName(folder), folder, launcher,
                IsStoreId(code) ? [$"--exec=launch {code}"] : []);
        }
    }

    private static bool IsBlizzard(string publisher)
        => publisher.Trim() is var p &&
           (p.StartsWith("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Activision", StringComparison.OrdinalIgnoreCase));

    // ── EA app ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<StoreLauncherGame> Ea(Context context, CancellationToken ct)
    {
        var launcher = context.ExistingFile(ParentOf(context.Value(@"Software\Electronic Arts\EA Desktop", "DesktopAppPath")), "EADesktop.exe")
                       ?? context.ExistingFile(context.Value(@"Software\Electronic Arts\EA Desktop", "InstallLocation"), "EADesktop.exe", Path.Combine("EA Desktop", "EADesktop.exe"))
                       ?? context.DefaultFile("Program Files/Electronic Arts/EA Desktop/EA Desktop/EADesktop.exe",
                           "Program Files (x86)/Origin/Origin.exe");
        var found = new List<(string Folder, string? Id, string? Name)>();

        // Origin/EA app keys: HKLM\Software\[Wow6432Node\]Origin Games\<content id> InstallDir + DisplayName.
        foreach (var (id, values) in context.Subkeys(@"Software\Origin Games"))
            if (context.GameFolder(values.GetValueOrDefault("InstallDir")) is { } folder)
                found.Add((folder, id, values.GetValueOrDefault("DisplayName")));
        // Publisher keys: <publisher>\<title> with an install folder value.
        foreach (var publisher in EaPublisherKeys)
            foreach (var (title, values) in context.Subkeys(@"Software\" + publisher))
                if (context.GameFolder(values.GetValueOrDefault("Install Dir") ?? values.GetValueOrDefault("InstallDir") ??
                                       values.GetValueOrDefault("Install Directory")) is { } folder)
                    found.Add((folder, null, title));
        // The EA app's default libraries (Lutris' EAAppGames): one game per folder with installer data.
        foreach (var library in new[] { "Program Files/EA Games", "Program Files (x86)/EA Games" })
        {
            var root = Path.Combine(context.DriveC, library);
            if (!System.IO.Directory.Exists(root)) continue;
            foreach (var folder in System.IO.Directory.GetDirectories(root).Order(StringComparer.Ordinal))
                if (File.Exists(InstallerDataPath(folder)) && context.GameFolder("C:/" + library + "/" + Path.GetFileName(folder)) is { } game)
                    found.Add((game, null, null));
        }

        var launcherFolder = launcher is null ? null : PathIdentity.Canonicalize(Path.GetDirectoryName(launcher)!);
        foreach (var (folder, recordedId, recordedName) in found)
        {
            ct.ThrowIfCancellationRequested();
            // The client's own folder is never a game (a publisher key may point at it).
            if (launcherFolder is not null && PathIdentity.Canonicalize(folder) == launcherFolder) continue;
            var data = ReadInstallerData(folder);
            var id = data.ContentId ?? recordedId;
            var name = FirstText(data.Title, recordedName) ?? Path.GetFileName(folder);
            yield return new StoreLauncherGame(StoreLauncher.EaApp, id, name, folder, launcher,
                id is not null && IsStoreId(id) ? [$"origin2://game/launch?offerIds={id}&autoDownload=1"] : []);
        }
    }

    private static string? ParentOf(string? windowsFile)
    {
        if (string.IsNullOrWhiteSpace(windowsFile)) return null;
        var text = windowsFile.Trim().Trim('"').Replace('\\', '/');
        var slash = text.LastIndexOf('/');
        return slash > 2 ? text[..slash] : null;
    }

    private static string InstallerDataPath(string folder) => Path.Combine(folder, "__Installer", "installerdata.xml");

    /// <summary>The first content id and the (en_US first) title in a game's <c>__Installer/installerdata.xml</c>.</summary>
    internal static (string? ContentId, string? Title) ReadInstallerData(string folder)
    {
        var path = InstallerDataPath(folder);
        var info = new FileInfo(path);
        if (!info.Exists) return (null, null);
        if (info.Length > MaxInstallerDataBytes)
        {
            DetectorDiagnostics.Warn($"EA installer data {path} is larger than {MaxInstallerDataBytes} bytes; not read");
            return (null, null);
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var contentId = document.Descendants().Where(e => e.Name.LocalName == "contentID")
                .Select(e => e.Value.Trim()).FirstOrDefault(value => value.Length > 0);
            var titles = document.Descendants().Where(e => e.Name.LocalName == "gameTitle").ToList();
            var title = titles.FirstOrDefault(e => string.Equals((string?)e.Attribute("locale"), "en_US", StringComparison.OrdinalIgnoreCase))?.Value
                        ?? titles.FirstOrDefault()?.Value
                        ?? document.Descendants().FirstOrDefault(e => e.Name.LocalName == "gameName")?.Value;
            return (contentId, FirstText(title));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            DetectorDiagnostics.Warn($"EA installer data {path} unreadable; the folder name is used", ex);
            return (null, null);
        }
    }

    // ── Rockstar Games Launcher ────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<StoreLauncherGame> Rockstar(Context context, CancellationToken ct)
    {
        var launcherEntry = context.Subkeys(Uninstall)
            .FirstOrDefault(entry => entry.Name.Equals("Rockstar Games Launcher", StringComparison.OrdinalIgnoreCase));
        var launcher = context.ExistingFile(context.Value(@"Software\Rockstar Games\Launcher", "InstallFolder"), "Launcher.exe")
                       ?? context.ExistingFile(launcherEntry.Values?.GetValueOrDefault("InstallLocation"), "Launcher.exe")
                       ?? context.DefaultFile("Program Files/Rockstar Games/Launcher/Launcher.exe", "Program Files (x86)/Rockstar Games/Launcher/Launcher.exe");
        var found = new List<(string Folder, string WindowsPath, string? Name)>();
        foreach (var (key, values) in context.Subkeys(Uninstall))
        {
            if (!values.TryGetValue("Publisher", out var publisher) || !publisher.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)) continue;
            var display = values.GetValueOrDefault("DisplayName")?.Trim();
            // The launcher and its SDK/Social Club are not games (upstream skips the launcher by name).
            if (IsRockstarClient(key) || (display is not null && IsRockstarClient(display))) continue;
            if (values.GetValueOrDefault("InstallLocation") is { } location && context.GameFolder(location) is { } folder)
                found.Add((folder, location.Trim().Trim('"'), display));
        }
        foreach (var (title, values) in context.Subkeys(@"Software\Rockstar Games"))
        {
            if (IsRockstarClient(title)) continue;
            if (values.GetValueOrDefault("InstallFolder") is { } location && context.GameFolder(location) is { } folder)
                found.Add((folder, location.Trim().Trim('"'), title));
        }
        var launcherFolder = launcher is null ? null : PathIdentity.Canonicalize(Path.GetDirectoryName(launcher)!);
        foreach (var (folder, windowsPath, name) in found)
        {
            ct.ThrowIfCancellationRequested();
            if (launcherFolder is not null && PathIdentity.Canonicalize(folder) == launcherFolder) continue;
            yield return new StoreLauncherGame(StoreLauncher.Rockstar, null, FirstText(name) ?? Path.GetFileName(folder), folder, launcher,
                ["-launchTitleInFolder", windowsPath.TrimEnd('\\', '/')]);
        }
    }

    private static bool IsRockstarClient(string name)
        => name.Equals("Launcher", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Social Club", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Rockstar Games SDK", StringComparison.OrdinalIgnoreCase) ||
           (name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) && name.Contains("Rockstar", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Grand", StringComparison.OrdinalIgnoreCase));

    // ── itch ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The itch app for Windows in this prefix: its user data in each Windows user's <c>AppData/Roaming/itch</c>, its
    /// client in <c>AppData/Local/itch</c>. <c>itch://games/&lt;id&gt;</c> opens the game in the app (every itch version
    /// routes it), where the user starts it.
    /// </summary>
    private static IEnumerable<StoreLauncherGame> Itch(Context context, CancellationToken ct)
    {
        var users = Path.Combine(context.DriveC, "users");
        if (!System.IO.Directory.Exists(users)) yield break;
        foreach (var user in System.IO.Directory.GetDirectories(users).Order(StringComparer.Ordinal))
        {
            var userData = Path.Combine(user, "AppData", "Roaming", "itch");
            if (!System.IO.Directory.Exists(userData)) continue;
            var local = Path.Combine(user, "AppData", "Local", "itch");
            var launcher = new[] { Path.Combine(local, "itch.exe") }
                .Concat(System.IO.Directory.Exists(local)
                    ? System.IO.Directory.GetDirectories(local, "app-*").Order(StringComparer.Ordinal).Reverse().Select(app => Path.Combine(app, "itch.exe"))
                    : [])
                .FirstOrDefault(File.Exists);
            foreach (var install in ItchLibrary.Read(userData, recorded => WindowsPathMapper.ToLinux(context.Prefix, recorded), context.Home, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (LutrisGameDetector.IsWinePrefixRoot(install.Folder)) continue;
                yield return new StoreLauncherGame(StoreLauncher.Itch, install.GameId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    install.Name, install.Folder, launcher, install.GameId is { } id ? [$"itch://games/{id}"] : [],
                    // butler's own launch candidate, when it is a Windows program.
                    install.Flavor == "windows" ? install.Executable : null);
            }
        }
    }

    // ── shared ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A store id safe to put in a launch argument (letters, digits, <c>_ . -</c>).</summary>
    internal static bool IsStoreId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static string? FirstText(params string?[] values)
        => values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
