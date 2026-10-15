using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection.Launchers;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Games the native itch app (or its Flatpak) installed (Refs #90), from its own records (<see cref="ItchLibrary"/>), read
/// only. Play opens the game in the itch app (<c>itch://games/&lt;id&gt;</c>), where the user starts it; a folder whose
/// itch.io game id is unknown is listed without a launch command. Games the Windows itch app installed inside a Wine
/// prefix are listed by the prefix's own detector (<see cref="StoreLauncherRecords"/>).
/// </summary>
public sealed class ItchGameDetector : IGameDetector
{
    private const string NativeRunner = "Native Linux";

    private readonly string _home;
    private readonly Func<IEnumerable<string>> _userDataFolders;
    private readonly Func<string, string?> _toLinux;

    public ItchGameDetector() : this(LinuxPaths.Home, () => UserDataFolders(LinuxPaths.Home, LinuxPaths.ConfigHome), null) { }

    /// <summary>
    /// Test seam: explicit itch user-data folders (each holding <c>db/butler.db</c> and <c>apps/</c>), and how the paths
    /// butler recorded map to folders (default: absolute paths as they are).
    /// </summary>
    internal ItchGameDetector(string home, IEnumerable<string> userDataFolders, Func<string, string?>? toLinux = null)
        : this(home, () => userDataFolders, toLinux) { }

    private ItchGameDetector(string home, Func<IEnumerable<string>> userDataFolders, Func<string, string?>? toLinux)
    {
        _home = home;
        _userDataFolders = userDataFolders;
        _toLinux = toLinux ?? (recorded => Path.IsPathRooted(recorded) ? recorded : null);
    }

    public string Name => "itch";

    public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedGame>>(() => Detect(cancellationToken), cancellationToken);

    /// <summary>The native app's user data (<c>$XDG_CONFIG_HOME/itch</c>) and the Flatpak's.</summary>
    internal static IEnumerable<string> UserDataFolders(string home, string configHome)
    {
        yield return Path.Combine(configHome, "itch");
        yield return Path.Combine(home, ".config", "itch");
        yield return Path.Combine(home, ".var", "app", "io.itch.itch", "config", "itch");
    }

    private IReadOnlyList<DetectedGame> Detect(CancellationToken ct)
    {
        var games = new List<DetectedGame>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var userData in _userDataFolders().Where(Directory.Exists).DistinctBy(PathIdentity.Canonicalize))
        {
            var flatpak = userData.Replace('\\', '/').Contains("/.var/app/io.itch.itch/", StringComparison.Ordinal);
            IReadOnlyList<ItchInstall> installs;
            try { installs = ItchLibrary.Read(userData, _toLinux, _home, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DetectorDiagnostics.Warn($"itch library {userData} unreadable; its games are not listed", ex);
                continue;
            }
            foreach (var install in installs)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(PathIdentity.Canonicalize(install.Folder))) continue;
                var (exe, native) = Executable(install, ct);
                if (exe is null) continue;
                games.Add(new DetectedGame
                {
                    Name = install.Name,
                    InstallPath = install.Folder,
                    Source = "itch",
                    ExecutablePath = exe,
                    LaunchExecutablePath = exe,
                    Runner = native ? NativeRunner : null,
                    LaunchCommand = install.GameId is { } id ? OpenCommand(id, flatpak) : null,
                    // Play opens the game's page in the itch app; the user starts it there.
                    ParentLauncherName = install.GameId is null ? null : "itch"
                });
            }
        }
        return games;
    }

    /// <summary>
    /// The game's executable: butler's launch candidate when it exists, else the likeliest Windows executable, else the
    /// likeliest native one. Native means a Linux build (no Wine prefix).
    /// </summary>
    private static (string? Exe, bool Native) Executable(ItchInstall install, CancellationToken ct)
    {
        // HTML, macOS, Java and installer candidates are not programs RHI mods: the folder is searched instead.
        if (install.Executable is { } recorded && install.Flavor is "windows" or "linux" or "script")
            return (recorded, install.Flavor != "windows");
        try
        {
            if (DetectionHelpers.FindLikelyExecutable(install.Folder, cancellationToken: ct) is { } windows) return (windows, false);
            return (DetectionHelpers.FindLikelyNativeExecutable(install.Folder, cancellationToken: ct), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectorDiagnostics.Warn($"itch game folder {install.Folder} skipped", ex);
            return (null, false);
        }
    }

    private static string OpenCommand(long gameId, bool flatpak)
        => flatpak
            ? $"flatpak run io.itch.itch 'itch://games/{gameId}'"
            : $"xdg-open 'itch://games/{gameId}'";
}
