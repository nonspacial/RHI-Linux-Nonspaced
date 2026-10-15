using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection.Launchers;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Detection;

public sealed partial class SteamGameDetector
{
    /// <summary>
    /// Source of a non-Steam shortcut's card (like Heroic/Sideload): Steam starts it, but it is no Steam app, so nothing
    /// that keys on Source "Steam" (an upstream AppID override, the hand-off of a card without a command) applies.
    /// </summary>
    public const string ShortcutSource = "Steam/Non-Steam";

    /// <summary>
    /// Non-Steam games a Steam user added (#176, Refs #90), from <c>shortcuts.vdf</c>. A shortcut to a Windows game's
    /// executable is a card of its own (folder = its start folder, else the program's folder; never a library or home
    /// folder), and Play hands <c>steam://rungameid/&lt;id&gt;</c> to Steam, so Steam starts it with the tool and options the
    /// user set there. A shortcut to a store client (EA app, Ubisoft Connect, Battle.net, Rockstar, itch) is not a game:
    /// the games that client recorded in the shortcut's own Proton prefix get cards, moddable in their real folders, and
    /// Play starts the client through Steam (the user starts the game there); another store's games in that prefix are
    /// listed without a launch command, as Heroic's are. Native programs, scripts, other launchers' commands and
    /// installers (setup, uninstall…) are not listed.
    /// </summary>
    internal static IReadOnlyList<DetectedGame> ShortcutGames(
        string steamRoot, IReadOnlyList<string> libraries, SteamAppClassifier classifier, bool hostSteam, CancellationToken ct)
    {
        var games = new List<DetectedGame>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shortcut in SteamShortcuts.ReadAll(steamRoot))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Only an absolute program path: a relative one would be looked up in RHI's own working folder (w6 review 5).
                if (!Path.IsPathRooted(shortcut.Exe) || !shortcut.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(shortcut.Exe)) continue;
                if (DetectionHelpers.IsSupportExecutableName(Path.GetFileNameWithoutExtension(shortcut.Exe))) continue;
                var command = ShortcutLaunchCommandFor(shortcut.GameId, steamRoot, hostSteam);
                var tool = classifier.ShortcutCompatTool(shortcut.AppId);
                var id = shortcut.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var prefix = libraries.Select(library => Path.Combine(library, "steamapps", "compatdata", id, "pfx")).FirstOrDefault(Directory.Exists);
                var runner = prefix is not null ? "Proton" : "Windows/Proton (prefix not created)";

                if (LinuxGameIdentityService.InferProviderFromExecutable(shortcut.Exe) is { } provider)
                {
                    if (prefix is null) continue;
                    foreach (var record in StoreLauncherRecords.Find(prefix, LinuxPaths.Home, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!seen.Add(PathIdentity.Canonicalize(record.InstallDirectory))) continue;
                        var exe = record.GameExecutable ?? PrefixGameFolders.FindExecutable(new PrefixGameFolders.Entry(record.InstallDirectory, null, null), ct);
                        if (exe is null) continue;
                        // Only this store's own client can start its games; the user picks the game there.
                        var ownClient = StoreLauncherRecords.ProviderName(record.Store) == provider;
                        games.Add(new DetectedGame
                        {
                            Name = record.Name,
                            InstallPath = record.InstallDirectory,
                            Source = ShortcutSource,
                            PrefixPath = prefix,
                            ExecutablePath = exe,
                            LaunchExecutablePath = ownClient ? shortcut.Exe : exe,
                            Runner = runner,
                            CompatibilityTool = tool,
                            LaunchCommand = ownClient ? command : null,
                            ParentLauncherName = ownClient ? record.LauncherName : null
                        });
                    }
                    continue;
                }

                if (ShortcutGameFolder(shortcut) is not { } folder || !seen.Add(PathIdentity.Canonicalize(folder))) continue;
                games.Add(new DetectedGame
                {
                    Name = shortcut.Name,
                    InstallPath = folder,
                    Source = ShortcutSource,
                    PrefixPath = prefix,
                    ExecutablePath = shortcut.Exe,
                    LaunchExecutablePath = shortcut.Exe,
                    Runner = runner,
                    CompatibilityTool = tool,
                    LaunchCommand = command
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DetectorDiagnostics.Warn($"Steam non-Steam shortcut \"{shortcut.Name}\" skipped", ex);
            }
        }
        return games;
    }

    /// <summary>The shortcut's start folder when it holds the program and is a real game folder, else the program's folder.</summary>
    private static string? ShortcutGameFolder(SteamShortcut shortcut)
    {
        var exeFolder = Path.GetDirectoryName(Path.GetFullPath(shortcut.Exe));
        if (!string.IsNullOrWhiteSpace(shortcut.StartDir) && Path.IsPathRooted(shortcut.StartDir))
        {
            var start = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shortcut.StartDir));
            if (exeFolder is not null && PathIdentity.IsSameOrInside(exeFolder, start) && GameRootGuard.IsAcceptable(start)) return start;
        }
        return exeFolder is not null && GameRootGuard.IsAcceptable(exeFolder) ? exeFolder : null;
    }

    /// <summary>
    /// How Steam is asked to start a non-Steam shortcut: <c>steam://rungameid/&lt;game id&gt;</c> through the Flatpak client,
    /// the host <c>steam</c> binary, or the URL handler, as <see cref="LaunchCommandFor"/> chooses for Steam apps.
    /// </summary>
    internal static string ShortcutLaunchCommandFor(ulong gameId, string steamRoot, bool hostSteamOnPath)
    {
        var url = $"steam://rungameid/{gameId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        static bool IsFlatpak(string root)
            => (root.Replace('\\', '/').TrimEnd('/') + "/").Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal);
        if (IsFlatpak(steamRoot) || IsFlatpak(PathIdentity.Canonicalize(steamRoot)))
            return $"flatpak run com.valvesoftware.Steam {url}";
        return hostSteamOnPath ? $"steam {url}" : $"xdg-open {url}";
    }
}
