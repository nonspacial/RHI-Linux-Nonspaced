using RHI.Core.Models;

namespace RHI.Platform.Linux.Settings;

/// <summary>
/// Keeps launcher identity separate from the game-local patch target. Provider signatures are only
/// discovery/presentation heuristics; deployment safety is enforced by RequiresExplicitPatchTarget.
/// </summary>
public static class LinuxGameIdentityService
{
    public static readonly IReadOnlyList<string> LaunchProviderOptions =
    [
        "Automático",
        "Battle.net",
        "EA",
        "Epic",
        "GOG",
        "Heroic",
        "Lutris",
        "Rockstar",
        "Steam",
        "Ubisoft",
        "Xbox",
        "Wine",
        "Outro"
    ];

    public static void ApplyDetectedIdentity(DetectedGame game, LinuxGameEditOverride? edit = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        game.LibraryIdentityPath ??= game.InstallPath;
        game.LibraryProvider = InferLibraryProvider(game);
        game.LaunchExecutablePath ??= game.ExecutablePath;
        var automaticPatchCandidate = game.ExecutablePath;

        var inferredLaunchProvider = InferLaunchProvider(game);
        game.LaunchProvider = string.IsNullOrWhiteSpace(edit?.LaunchProvider)
            ? inferredLaunchProvider
            : edit!.LaunchProvider;

        var discoveredSharedLauncher = IsSharedLauncherExecutable(game.LaunchExecutablePath);
        // A detector may already have found no real game folder (e.g. a Lutris wrapper script);
        // that verdict must survive, or the placeholder folder would become the patch target.
        var detectorRequiresTarget = game.RequiresExplicitPatchTarget;
        game.RequiresExplicitPatchTarget = discoveredSharedLauncher || detectorRequiresTarget;
        game.PatchTargetAuthority = "Automatic";

        if (edit?.HasPatchTarget == true)
        {
            game.InstallPath = edit.GameRootPath!;
            game.ExecutablePath = edit.PatchExecutablePath!;
            game.BinaryDirectory = edit.BinaryDirectory!;
            game.RequiresExplicitPatchTarget = true;
            game.PatchTargetAuthority = "Manual";
            return;
        }

        // A manifest/policy may already have replaced the launcher's executable with a distinct, existing
        // game executable. Preserve that authority instead of throwing useful detection away.
        if (discoveredSharedLauncher &&
            !string.IsNullOrWhiteSpace(automaticPatchCandidate) &&
            File.Exists(automaticPatchCandidate) &&
            !PathsEqual(automaticPatchCandidate, game.LaunchExecutablePath))
        {
            game.ExecutablePath = automaticPatchCandidate;
            game.BinaryDirectory ??= Path.GetDirectoryName(automaticPatchCandidate);
            game.PatchTargetAuthority = string.IsNullOrWhiteSpace(game.ManifestLaunchExecutable) ? "Automatic" : "Manifest";
            return;
        }

        // A launcher/client can remain the launch target, but it must never become the game-local
        // deployment authority. Leave the library identity intact and fail closed until Editar jogo
        // supplies a separate executable/root/binary directory.
        if (discoveredSharedLauncher || detectorRequiresTarget)
        {
            game.ExecutablePath = null;
            game.BinaryDirectory = null;
        }
    }

    public static string InferLibraryProvider(DetectedGame game)
    {
        var source = game.Source ?? string.Empty;
        if (source.StartsWith("Heroic", StringComparison.OrdinalIgnoreCase)) return "Heroic";
        if (source.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (source.Contains("Lutris", StringComparison.OrdinalIgnoreCase)) return "Lutris";
        if (source.Contains("Wine", StringComparison.OrdinalIgnoreCase)) return "Wine";
        if (source.Contains("Manual", StringComparison.OrdinalIgnoreCase)) return "RHI";
        return string.IsNullOrWhiteSpace(source) ? "RHI" : source.Trim();
    }

    public static string? InferLaunchProvider(DetectedGame game)
    {
        var executable = game.LaunchExecutablePath ?? game.ExecutablePath;
        var byExecutable = InferProviderFromExecutable(executable);
        if (!string.IsNullOrWhiteSpace(byExecutable)) return byExecutable;

        var source = game.Source ?? string.Empty;
        if (source.Contains("Heroic/Epic", StringComparison.OrdinalIgnoreCase)) return "Epic";
        if (source.Contains("Heroic/GOG", StringComparison.OrdinalIgnoreCase)) return "GOG";
        if (source.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return "Steam";

        var command = game.LaunchCommand ?? string.Empty;
        if (command.Contains("steam://", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (command.Contains("heroic://", StringComparison.OrdinalIgnoreCase) &&
            source.Equals("Heroic", StringComparison.OrdinalIgnoreCase))
            return "Heroic";

        return null;
    }

    public static bool IsSharedLauncherExecutable(string? executablePath)
        => !string.IsNullOrWhiteSpace(InferProviderFromExecutable(executablePath));

    public static string? InferProviderFromExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        var normalized = executablePath.Replace('\\', '/');
        var file = Path.GetFileName(normalized);

        // Keep these signatures deliberately conservative: provider install directories may also
        // contain real games. A shared client is identified primarily by its own executable name,
        // never merely because a game lives below a vendor-named folder.
        if (EqualsAny(file, "Battle.net.exe", "Battle.net Launcher.exe")) return "Battle.net";
        if (EqualsAny(file, "EpicGamesLauncher.exe")) return "Epic";
        if (EqualsAny(file, "EADesktop.exe", "EALauncher.exe", "Origin.exe")) return "EA";
        if (EqualsAny(file, "UbisoftConnect.exe", "upc.exe", "Uplay.exe")) return "Ubisoft";
        if (EqualsAny(file, "GalaxyClient.exe", "GalaxyClientService.exe")) return "GOG";
        if (EqualsAny(file, "RockstarGamesLauncher.exe", "SocialClubHelper.exe") ||
            (file.Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase) &&
             normalized.Contains("rockstar", StringComparison.OrdinalIgnoreCase)))
            return "Rockstar";
        if (EqualsAny(file, "steam.exe")) return "Steam";
        if (EqualsAny(file, "XboxPcApp.exe", "GamingServices.exe")) return "Xbox";

        return null;
    }

    public static string ProviderIconKey(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "battle.net" or "battlenet" => "battlenet",
        "ea" or "origin" => "ea",
        "epic" or "epic games" => "epic",
        "gog" or "gog galaxy" => "gog",
        "heroic" => "heroic",
        "lutris" => "lutris",
        "rockstar" or "rockstar games" => "rockstar",
        "steam" => "steam",
        "ubisoft" or "ubisoft connect" => "ubisoft",
        "xbox" => "xbox",
        "wine" => "wine",
        "rhi" or "manual" => "rhi",
        _ => "rhi"
    };

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase); }
    }

    private static bool EqualsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));
}
