using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>Where the Xbox detector looks (the test seam: no environment is read through it).</summary>
/// <param name="Home">The user's home (<c>~/Games</c> is searched for Xodus installs).</param>
/// <param name="DefaultStateDirs">the installer app's default state folders (<c>$XDG_STATE_HOME/{app}</c>, <c>~/.local/state/{app}</c>).</param>
/// <param name="UserFolders">The "Xbox / Game Pass folders" setting.</param>
/// <param name="BreadcrumbDir">Where installer-app instances announce themselves (<see cref="InstallerBreadcrumb"/>).</param>
/// <param name="Apps">How the owning apps are opened.</param>
internal sealed record XboxDiscoveryInputs(
    string Home,
    IReadOnlyList<string> DefaultStateDirs,
    IReadOnlyList<string> UserFolders,
    string? BreadcrumbDir,
    OwningAppLocator Apps);

/// <summary>
/// Xbox / Game Pass games (Refs #90; owner: listed straight away). Linux has no official client: installs come from
/// Xodus's on-disk package format first (any folder holding <c>.xodus-streaming.msixvc</c> or an installer-app Appx record
/// plus a package config) and from the installer app's own records second (name, Wine prefix and build, owning instance).
/// Unfinished downloads are hidden. Each card is owned by the app that installed it: Play runs the installer app's own Play entry
/// point from its breadcrumb (<c>--play &lt;storeId&gt;</c>: licensing, saves, cloud) or, without one, opens the owning
/// app; never the game exe, never a synthesised <c>xodus-cli run</c>; disabled when RHI finds neither.
/// </summary>
public sealed class XboxGameDetector : IGameDetector
{
    public const string SourceName = "Xbox";
    public const string InstallerApp = XboxInstallerApp.DisplayName;
    public const string XodusApp = "Xodus";

    // ~/Games and user game folders: package folders this many levels down at most.
    private const int XodusScanDepth = 3;
    private const int XodusScanMaxDirectories = 20_000;

    private readonly Func<XboxDiscoveryInputs> _inputs;

    public XboxGameDetector() : this(new XboxFolderStore()) { }

    public XboxGameDetector(XboxFolderStore folders)
        : this(() => new XboxDiscoveryInputs(
            LinuxPaths.Home,
            [Path.Combine(LinuxPaths.StateHome, XboxInstallerApp.FolderName), Path.Combine(LinuxPaths.Home, ".local", "state", XboxInstallerApp.FolderName)],
            folders.Folders(),
            Path.Combine(LinuxPaths.DataHome, XboxInstallerApp.FolderName, "instances"),
            OwningAppLocator.FromEnvironment(LinuxPaths.Home, LinuxPaths.DataHome)))
        => FolderStore = folders;

    /// <summary>The "Xbox / Game Pass folders" setting this detector reads (the app passes its one shared store).</summary>
    internal XboxFolderStore? FolderStore { get; }

    internal XboxGameDetector(Func<XboxDiscoveryInputs> inputs) => _inputs = inputs;

    public string Name => SourceName;

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => DetectIn(_inputs(), cancellationToken), cancellationToken);

    internal static IReadOnlyList<DetectedGame> DetectIn(XboxDiscoveryInputs inputs, CancellationToken ct)
    {
        var games = new List<DetectedGame>();
        var seenFolders = new HashSet<string>(StringComparer.Ordinal);
        var seenStates = new HashSet<string>(StringComparer.Ordinal);
        var payloadRoots = new HashSet<string>(StringComparer.Ordinal);
        var xodusRoots = new List<string> { Path.Combine(inputs.Home, "Games") };
        var defaults = inputs.DefaultStateDirs.Select(PathIdentity.Canonicalize).ToHashSet(StringComparer.Ordinal);
        // The installer app's breadcrumbs are the primary source (one per instance, newest per state folder).
        var breadcrumbs = InstallerBreadcrumb.ReadAll(inputs.BreadcrumbDir)
            .ToDictionary(crumb => PathIdentity.Canonicalize(crumb.StateDir), StringComparer.Ordinal);

        // State folders: breadcrumbs, the defaults (quiet when the installer app was never set up), then the user's list.
        var states = new List<(string Dir, bool Report)>();
        states.AddRange(breadcrumbs.Values.Select(crumb => (crumb.StateDir, false)));
        states.AddRange(inputs.DefaultStateDirs.Select(dir => (dir, false)));
        foreach (var folder in inputs.UserFolders)
        {
            if (!Directory.Exists(folder))
            {
                DetectorDiagnostics.Warn($"Xbox folder {folder} not found; nothing listed from it");
                continue;
            }
            if (StateDirIn(folder) is { } state) states.Add((state, true));
            else xodusRoots.Add(folder);
        }

        foreach (var (dir, report) in states)
        {
            ct.ThrowIfCancellationRequested();
            var canonical = PathIdentity.Canonicalize(dir);
            if (!seenStates.Add(canonical)) continue;
            // The breadcrumb names the instance's folders itself; settings.json only stands in for an instance without one.
            var instance = breadcrumbs.TryGetValue(canonical, out var crumb)
                ? XboxInstallerInstance.FromBreadcrumb(crumb)
                : Directory.Exists(dir) ? XboxInstallerInstance.Load(dir, report) : null;
            if (instance is null) continue;
            instance.FallbackOpenCommand = inputs.Apps.InstallerApp(dir, defaults.Contains(canonical));
            foreach (var payloadRoot in instance.PayloadRoots)
            {
                xodusRoots.Add(payloadRoot);
                payloadRoots.Add(PathIdentity.Canonicalize(payloadRoot));
            }
            var installs = instance.Installs(ct, out var partials);
            if (partials > 0)
                AppLog.Warn("scan", $"Xbox app {dir}: {partials} unfinished download(s) not listed until they finish");
            foreach (var install in installs)
            {
                ct.ThrowIfCancellationRequested();
                if (!seenFolders.Add(PathIdentity.Canonicalize(install.Directory))) continue;
                games.Add(FromInstallerApp(instance, install));
            }
        }

        string? xodusCommand = null;
        var xodusLooked = false;
        foreach (var root in xodusRoots.Distinct(StringComparer.Ordinal))
            foreach (var dir in PackageFolders(root, ct))
            {
                if (!seenFolders.Add(PathIdentity.Canonicalize(dir))) continue;
                // A known installer-app payload folder decides on its own: what it did not list is unfinished or not its own.
                if (payloadRoots.Contains(PathIdentity.Canonicalize(Path.GetDirectoryName(dir) ?? dir))) continue;
                if (File.Exists(Path.Combine(dir, XboxInstallerInstance.PartialMarker))) continue;
                if (!xodusLooked)
                {
                    xodusCommand = inputs.Apps.Xodus();
                    xodusLooked = true;
                }
                if (FromFolder(dir, xodusCommand) is { } game) games.Add(game);
            }
        return games;
    }

    /// <summary>The installer app's state folder a user-listed folder stands for (the state folder, its state home or the instance folder).</summary>
    private static string? StateDirIn(string folder)
        => new[] { folder, Path.Combine(folder, XboxInstallerApp.FolderName), Path.Combine(folder, "state", XboxInstallerApp.FolderName) }
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "settings.json")));

    /// <summary>
    /// The installed product's name, except where it is only the package's own name with "Standard Edition" added (the
    /// store's name for the plain game, "Forza Horizon 6 Standard Edition" for the "Forza Horizon 6" package): then the
    /// package's shorter name, which is also the one mod lists use. An edition that differs (Control Ultimate Edition over
    /// the "Control Standard Edition" package) keeps the product's name (#175).
    /// </summary>
    internal static string? CardName(string? productTitle, string? packageTitle)
    {
        if (productTitle is null) return packageTitle;
        if (packageTitle is null) return productTitle;
        var plain = System.Text.RegularExpressions.Regex.Replace(productTitle, @"\s*(?:[-–:]\s*)?Standard Edition\s*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return string.Equals(plain.Trim(), packageTitle.Trim(), StringComparison.OrdinalIgnoreCase) ? packageTitle : productTitle;
    }

    private static DetectedGame FromInstallerApp(XboxInstallerInstance instance, XboxInstallerInstance.Install install)
    {
        var package = XboxPackageReader.Read(install.Directory, instance.RecipeExecutable(install.StoreId));
        var verified = package is { HasExecutable: true } &&
                       (File.Exists(Path.Combine(install.Directory, XboxPackageReader.StreamingFileName)) ||
                        File.Exists(Path.Combine(install.Directory, XboxPackageReader.AppxRecordFileName)));
        if (!verified)
            DetectorDiagnostics.Warn($"Xbox app title {install.StoreId} ({install.Directory}): {package?.Problem ?? "no package config"}; listed without a modding target");
        var environment = instance.Environment(install.StoreId);
        // The product the user installed names the card (#175): the marker and package name a shared base package.
        var name = CardName(instance.InstalledProductTitle(install.StoreId), install.Title) ?? package?.DisplayName ?? install.StoreId;
        var game = Card(name, install.Directory, verified ? package : null, Package(instance, install.StoreId));
        game.PrefixPath = environment.Prefix;
        game.WineBinaryPath = environment.Wine;
        game.WineServerPath = environment.WineServer;
        game.CompatibilityTool = environment.RuntimeName;
        game.CompatibilityLayerType = environment.Prefix is null ? null : "wine";
        game.Runner = environment.RuntimeName is null ? InstallerApp : $"{InstallerApp} · {environment.RuntimeName}";
        // Writes into the payload folder or the title environment are checked against this instance's job list.
        XboxWriteGate.Register(game.XboxPackage!, install.Directory, environment.Prefix is null ? null : Path.GetDirectoryName(environment.Prefix));
        return game;
    }

    private static DetectedGame? FromFolder(string dir, string? xodusCommand)
    {
        var package = XboxPackageReader.Read(dir);
        if (package is null) return null;
        // An installer-app-owned folder whose instance RHI does not know: the installer app still owns it (open it from the installer app).
        var ownedByInstallerApp = File.Exists(Path.Combine(dir, XboxInstallerInstance.OwnedMarker));
        if (!package.HasExecutable)
            DetectorDiagnostics.Warn($"Xbox package {dir}: {package.Problem}; listed without a modding target");
        var name = package.DisplayName ?? Path.GetFileName(dir);
        var game = Card(name, dir, package.HasExecutable ? package : null,
            new XboxPackageInfo(package.StoreId, ownedByInstallerApp ? InstallerApp : XodusApp, ownedByInstallerApp ? null : xodusCommand, JobsFile: null));
        game.Runner = ownedByInstallerApp ? InstallerApp : XodusApp;
        XboxWriteGate.Register(game.XboxPackage!, dir);
        return game;
    }

    /// <summary>
    /// The card's package record: Play and Show through the breadcrumb's entry points (an argument vector, the installer app's own
    /// launch path; spec items 2–3) when its launcher is there, else the instance's fallback opener (spec item 4).
    /// </summary>
    private static XboxPackageInfo Package(XboxInstallerInstance instance, string storeId)
    {
        var crumb = instance.Breadcrumb;
        var play = crumb?.Command(crumb.PlayEntry, storeId);
        if (crumb is not null && play is not null && OwningAppLocator.IsExecutableFile(play[0]))
            return new XboxPackageInfo(storeId, InstallerApp, OwningAppLocator.Render(play, crumb.LaunchEnvironment), instance.JobsFile,
                play, crumb.Command(crumb.ShowEntry, storeId), crumb.LaunchEnvironment);
        if (crumb?.LaunchArgv is { } argv && !OwningAppLocator.IsExecutableFile(argv[0]))
            AppLog.Warn("scan", $"Xbox app launcher {argv[0]} ({crumb.File}) is not an executable file; Play falls back to opening the installer app");
        return new XboxPackageInfo(storeId, InstallerApp, instance.FallbackOpenCommand, instance.JobsFile);
    }

    private static DetectedGame Card(string name, string dir, XboxPackage? package, XboxPackageInfo info)
    {
        var executable = package?.Executable;
        var openCommand = info.OpenCommand;
        return new DetectedGame
        {
            Name = name,
            InstallPath = dir,
            Source = SourceName,
            ExecutablePath = executable,
            LaunchExecutablePath = executable,
            // A launcher (Fallout 4's, a menu shim) is no patch target until the user picks the game's exe in Edit game.
            RequiresExplicitPatchTarget = package is null || package.ExecutableIsLauncher,
            LaunchCommand = openCommand,
            // The installer app's Play entry point starts the game itself; only an opener leaves the user to start it in the app.
            ParentLauncherName = openCommand is null || info.PlayArgv is not null ? null : info.OwningApp,
            // The exe is encrypted on disk, so its bitness comes from the package (GameArchitecture, #167); the exe's own
            // header still wins when it can be read (a plain Appx).
            XboxPackage = info with { Architecture = package?.Architecture }
        };
    }

    /// <summary>
    /// Package folders under <paramref name="root"/> (itself included), at most <see cref="XodusScanDepth"/> levels
    /// down: symlinked folders are not followed, Wine prefixes and installer-app environment folders are not entered, and
    /// a package folder's own subfolders are not searched.
    /// </summary>
    private static IEnumerable<string> PackageFolders(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Queue<(string Dir, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Dequeue();
            if (++visited > XodusScanMaxDirectories)
            {
                DetectorDiagnostics.Warn($"Xbox folder search stopped in {root} after {XodusScanMaxDirectories} folders");
                yield break;
            }
            if (XboxPackageReader.LooksLikePackage(dir))
            {
                yield return dir;
                continue;
            }
            if (depth >= XodusScanDepth || IsWinePrefix(dir) || XboxInstallerInstance.IsEnvironmentRoot(dir)) continue;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("scan", $"Xbox folder search skipped {dir}", ex);
                continue;
            }
            foreach (var child in children.Order(StringComparer.Ordinal))
                if (!XboxPackageReader.IsLink(child)) pending.Enqueue((child, depth + 1));
        }
    }

    private static bool IsWinePrefix(string dir)
        => Directory.Exists(Path.Combine(dir, "drive_c")) || Directory.Exists(Path.Combine(dir, "dosdevices"));
}
