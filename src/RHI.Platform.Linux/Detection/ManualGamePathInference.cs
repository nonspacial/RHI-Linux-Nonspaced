using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Detection;

/// <summary>Linux-safe path/name inference for Windows executables dropped into RHI.</summary>
public static partial class ManualGamePathInference
{
    public static string InferInstallRoot(string executablePath)
    {
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new ArgumentException("Executable path has no parent directory.", nameof(executablePath));
        var dirName = Path.GetFileName(exeDir);
        var parent = Path.GetDirectoryName(exeDir);
        var parentName = parent is null ? "" : Path.GetFileName(parent);

        if (parent is not null && parentName.Equals("Binaries", StringComparison.OrdinalIgnoreCase) &&
            (dirName.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
             dirName.Equals("Win32", StringComparison.OrdinalIgnoreCase) ||
             dirName.Equals("WinGDK", StringComparison.OrdinalIgnoreCase)))
        {
            var project = Path.GetDirectoryName(parent);
            if (project is null) return exeDir;
            // Unreal ships <Game>/<Project>/Binaries/Win64 beside <Game>/Engine, where nvngx_dlss.dll and
            // Streamline usually live: the game root is <Game> so those are indexed too. An exe under
            // Engine/Binaries itself also belongs to <Game>.
            var game = Path.GetDirectoryName(project);
            var projectIsEngine = Path.GetFileName(project).Equals("Engine", StringComparison.OrdinalIgnoreCase);
            if (game is not null &&
                (projectIsEngine || Directory.Exists(Path.Combine(game, "Engine"))) &&
                GameRootGuard.IsAcceptable(game))
                return game;
            return project;
        }

        var current = exeDir;
        for (var level = 0; level < 3 && current is not null; level++)
        {
            if (LooksLikeGameRoot(current) && GameRootGuard.IsAcceptable(current)) return current;
            current = Path.GetDirectoryName(current);
        }
        return exeDir;
    }

    public static string InferGameName(string executablePath, string installRoot)
    {
        var exeName = Path.GetFileNameWithoutExtension(executablePath);
        var rootName = Path.GetFileName(installRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? exeName;
        var cleanedExe = ShippingSuffixRegex().Replace(exeName, "").Trim('-', '_', ' ');
        if (rootName.Contains(' ') || rootName.Contains('-')) return Clean(rootName);
        return string.IsNullOrWhiteSpace(cleanedExe) ? Clean(rootName) : Clean(cleanedExe);
    }

    internal static bool LooksLikeGameRoot(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, "steam_appid.txt")) ||
                   File.Exists(Path.Combine(directory, "steam_api64.dll")) ||
                   File.Exists(Path.Combine(directory, "steam_api.dll")) ||
                   Directory.Exists(Path.Combine(directory, ".egstore")) ||
                   File.Exists(Path.Combine(directory, "goglog.ini")) ||
                   File.Exists(Path.Combine(directory, "MicrosoftGame.config")) ||
                   File.Exists(Path.Combine(directory, ".build.info")) ||
                   File.Exists(Path.Combine(directory, "UnityPlayer.dll")) ||
                   Directory.Exists(Path.Combine(directory, "Engine")) ||
                   Directory.Exists(Path.Combine(directory, "Binaries"));
        }
        catch
        {
            return false;
        }
    }

    private static string Clean(string value)
    {
        value = value.Replace('_', ' ').Trim();
        value = Regex.Replace(value, @"\s+", " ");
        return value;
    }

    [GeneratedRegex(@"(?i)(-Win64-Shipping|-Win32-Shipping|-WinGDK-Shipping|-Shipping)$")]
    private static partial Regex ShippingSuffixRegex();
}
