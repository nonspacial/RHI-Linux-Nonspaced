using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>Where the Xbox detector looks (the test seam: no environment is read through it).</summary>
/// <param name="Home">The user's home (<c>~/Games</c> is searched for Xodus installs).</param>
/// <param name="DefaultStateDirs">PartingX's default state folders (<c>$XDG_STATE_HOME/partingx</c>, <c>~/.local/state/partingx</c>).</param>
/// <param name="UserFolders">The "PartingX / Xbox folders" setting.</param>
/// <param name="BreadcrumbDir">Where PartingX instances announce themselves (<see cref="XboxBreadcrumb"/>).</param>
/// <param name="Apps">How the owning apps are opened.</param>
internal sealed record XboxDiscoveryInputs(
    string Home,
    IReadOnlyList<string> DefaultStateDirs,
    IReadOnlyList<string> UserFolders,
    string? BreadcrumbDir,
    OwningAppLocator Apps);

/// <summary>
/// Xbox / Game Pass games (Refs #90; owner: listed straight away). Linux has no official client: installs come from
/// Xodus's on-disk package format first (any folder holding <c>.xodus-streaming.msixvc</c> or a PartingX Appx record
/// plus a package config) and from PartingX's own records second (name, Wine prefix and build, owning instance).
/// Unfinished downloads are hidden. Each card is owned by the app that installed it: Play opens that app (never the
/// game exe, never a synthesised <c>xodus-cli run</c>), and is disabled when RHI cannot find it.
/// </summary>
public sealed class XboxGameDetector : IGameDetector
{
    public const string SourceName = "Xbox";
    public const string PartingXApp = "PartingX";
    public const string XodusApp = "Xodus";

    // ~/Games and user game folders: package folders this many levels down at most.
    private const int XodusScanDepth = 3;
    private const int XodusScanMaxDirectories = 20_000;

    private readonly Func<XboxDiscoveryInputs> _inputs;

    public XboxGameDetector() : this(new XboxFolderStore()) { }

    public XboxGameDetector(XboxFolderStore folders)
        : this(() => new XboxDiscoveryInputs(
            LinuxPaths.Home,
            [Path.Combine(LinuxPaths.StateHome, "partingx"), Path.Combine(LinuxPaths.Home, ".local", "state", "partingx")],
            folders.Folders(),
            Path.Combine(LinuxPaths.DataHome, "partingx", "instances"),
            OwningAppLocator.FromEnvironment(LinuxPaths.Home, LinuxPaths.DataHome)))
    { }

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
        var breadcrumbs = XboxBreadcrumb.ReadAll(inputs.BreadcrumbDir);

        // State folders: the defaults (quiet when PartingX was never set up), breadcrumbs, then the user's list.
        var states = new List<(string Dir, bool Report, XboxBreadcrumb? Crumb)>();
        states.AddRange(inputs.DefaultStateDirs.Select(dir => (dir, false, (XboxBreadcrumb?)null)));
        states.AddRange(breadcrumbs.Select(crumb => (crumb.StateDir, false, (XboxBreadcrumb?)crumb)));
        foreach (var folder in inputs.UserFolders)
        {
            if (!Directory.Exists(folder))
            {
                DetectorDiagnostics.Warn($"Xbox folder {folder} not found; nothing listed from it");
                continue;
            }
            if (StateDirIn(folder) is { } state) states.Add((state, true, null));
            else xodusRoots.Add(folder);
        }

        foreach (var (dir, report, listedCrumb) in states)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir) || !seenStates.Add(PathIdentity.Canonicalize(dir))) continue;
            var instance = PartingXInstance.Load(dir, report);
            if (instance is null) continue;
            // A breadcrumb for the same state folder supplies the launcher even when the folder came from elsewhere.
            var crumb = listedCrumb ?? breadcrumbs.FirstOrDefault(c => PathIdentity.Canonicalize(c.StateDir) == PathIdentity.Canonicalize(dir));
            instance.OpenCommand = inputs.Apps.PartingX(dir, defaults.Contains(PathIdentity.Canonicalize(dir)), crumb);
            xodusRoots.Add(instance.PayloadRoot);
            payloadRoots.Add(PathIdentity.Canonicalize(instance.PayloadRoot));
            var installs = instance.Installs(ct, out var partials);
            if (partials > 0)
                AppLog.Warn("scan", $"PartingX {dir}: {partials} unfinished download(s) not listed until they finish");
            foreach (var install in installs)
            {
                ct.ThrowIfCancellationRequested();
                if (!seenFolders.Add(PathIdentity.Canonicalize(install.Directory))) continue;
                games.Add(FromPartingX(instance, install));
            }
        }

        string? xodusCommand = null;
        var xodusLooked = false;
        foreach (var root in xodusRoots.Distinct(StringComparer.Ordinal))
            foreach (var dir in PackageFolders(root, ct))
            {
                if (!seenFolders.Add(PathIdentity.Canonicalize(dir))) continue;
                // A known PartingX payload folder decides on its own: what it did not list is unfinished or not its own.
                if (payloadRoots.Contains(PathIdentity.Canonicalize(Path.GetDirectoryName(dir) ?? dir))) continue;
                if (File.Exists(Path.Combine(dir, PartingXInstance.PartialMarker))) continue;
                if (!xodusLooked)
                {
                    xodusCommand = inputs.Apps.Xodus();
                    xodusLooked = true;
                }
                if (FromFolder(dir, xodusCommand) is { } game) games.Add(game);
            }
        return games;
    }

    /// <summary>The PartingX state folder a user-listed folder stands for (the state folder, its state home or the instance folder).</summary>
    private static string? StateDirIn(string folder)
        => new[] { folder, Path.Combine(folder, "partingx"), Path.Combine(folder, "state", "partingx") }
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "settings.json")));

    private static DetectedGame FromPartingX(PartingXInstance instance, PartingXInstance.Install install)
    {
        var package = XboxPackageReader.Read(install.Directory, instance.RecipeExecutable(install.StoreId));
        var verified = package is { HasExecutable: true } &&
                       (File.Exists(Path.Combine(install.Directory, XboxPackageReader.StreamingFileName)) ||
                        File.Exists(Path.Combine(install.Directory, XboxPackageReader.AppxRecordFileName)));
        if (!verified)
            DetectorDiagnostics.Warn($"PartingX title {install.StoreId} ({install.Directory}): {package?.Problem ?? "no package config"}; listed without a modding target");
        var environment = instance.Environment(install.StoreId);
        var name = install.Title ?? instance.CatalogueTitle(install.StoreId) ?? package?.DisplayName ?? install.StoreId;
        var game = Card(name, install.Directory, verified ? package : null, PartingXApp, install.StoreId, instance.OpenCommand, instance.JobsFile);
        game.PrefixPath = environment.Prefix;
        game.WineBinaryPath = environment.Wine;
        game.WineServerPath = environment.WineServer;
        game.CompatibilityTool = environment.RuntimeName;
        game.CompatibilityLayerType = environment.Prefix is null ? null : "wine";
        game.Runner = environment.RuntimeName is null ? PartingXApp : $"{PartingXApp} · {environment.RuntimeName}";
        // Writes into the payload folder or the title environment are checked against this instance's job list.
        XboxWriteGate.Register(game.XboxPackage!, install.Directory, environment.Prefix is null ? null : Path.GetDirectoryName(environment.Prefix));
        return game;
    }

    private static DetectedGame? FromFolder(string dir, string? xodusCommand)
    {
        var package = XboxPackageReader.Read(dir);
        if (package is null) return null;
        // A PartingX-owned folder whose instance RHI does not know: PartingX still owns it (open it from PartingX).
        var ownedByPartingX = File.Exists(Path.Combine(dir, PartingXInstance.OwnedMarker));
        if (!package.HasExecutable)
            DetectorDiagnostics.Warn($"Xbox package {dir}: {package.Problem}; listed without a modding target");
        var name = package.DisplayName ?? Path.GetFileName(dir);
        var game = Card(name, dir, package.HasExecutable ? package : null, ownedByPartingX ? PartingXApp : XodusApp,
            package.StoreId, ownedByPartingX ? null : xodusCommand, jobsFile: null);
        game.Runner = ownedByPartingX ? PartingXApp : XodusApp;
        XboxWriteGate.Register(game.XboxPackage!, dir);
        return game;
    }

    private static DetectedGame Card(string name, string dir, XboxPackage? package, string owningApp, string? storeId, string? openCommand, string? jobsFile)
    {
        var executable = package?.Executable;
        return new DetectedGame
        {
            Name = name,
            InstallPath = dir,
            Source = SourceName,
            ExecutablePath = executable,
            LaunchExecutablePath = executable,
            // A launcher (Fallout 4's, a menu shim) is no patch target until the user picks the game's exe in Edit game.
            RequiresExplicitPatchTarget = package is null || package.ExecutableIsLauncher,
            // The exe is encrypted on disk, so its bitness is unknown: the card never claims one, and installs refuse with
            // the plain reason (XboxWriteGate.InstallRefusal) until #167 reads it from the package.
            LaunchCommand = openCommand,
            ParentLauncherName = openCommand is null ? null : owningApp,
            XboxPackage = new XboxPackageInfo(storeId, owningApp, openCommand, jobsFile)
        };
    }

    /// <summary>
    /// Package folders under <paramref name="root"/> (itself included), at most <see cref="XodusScanDepth"/> levels
    /// down: symlinked folders are not followed, Wine prefixes and PartingX environment folders are not entered, and
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
            if (depth >= XodusScanDepth || IsWinePrefix(dir) || PartingXInstance.IsEnvironmentRoot(dir)) continue;
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
