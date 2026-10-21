using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Settings;

/// <summary>
/// One game's "Apply" of the ReShade Settings values (#206): <see cref="MainIni"/> (the game's ReShade.ini, which may be
/// a link), the planned writes and, index for index, the file each one replaces (<see cref="Targets"/>).
/// </summary>
internal sealed record ReShadeSettingsPlan(string MainIni, IReadOnlyList<IniFileWrite> Writes, IReadOnlyList<string> Targets, string ScreenshotFolder)
{
    /// <summary>Whether the main ReShade.ini is written (its numbered siblings may be written without it).</summary>
    public bool WritesMainIni => Writes.Any(write => write.Path.Equals(MainIni, StringComparison.Ordinal));

    /// <summary>The file a write to the main ReShade.ini replaces (the main file itself when it is not written).</summary>
    public string MainTarget => Writes.Select((write, i) => (write, i)).Where(x => x.write.Path.Equals(MainIni, StringComparison.Ordinal))
        .Select(x => Targets[x.i]).FirstOrDefault() ?? MainIni;

    /// <summary>The screenshot folder the settings name (created at the write phase, as upstream does).</summary>
    public void CreateScreenshotFolder()
    {
        if (!string.IsNullOrWhiteSpace(ScreenshotFolder)) Directory.CreateDirectory(ScreenshotFolder);
    }
}

public static class ReShadeGlobalSettingsApplier
{
    /// <summary>Home: ReShade's own overlay key (upstream writes KeyOverlay at install only when it differs).</summary>
    public const string DefaultOverlayHotkey = "36,0,0,0";

    /// <summary>Print Screen: ReShade's own screenshot key.</summary>
    public const string DefaultScreenshotHotkey = "44,0,0,0";

    public static string ResolveScreenshotPath(LinuxAppSettings settings, DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(settings.ScreenshotPath)) return "";
        return settings.PerGameScreenshotFolders
            ? Path.Combine(settings.ScreenshotPath, SanitizeDirectoryName(game.Name))
            : settings.ScreenshotPath;
    }

    /// <summary>
    /// What a ReShade install (and "Deploy ReShade.ini") writes into the game's ReShade.ini besides RHI's shader paths
    /// (#181; upstream 2.8.7 <c>MergeRsIni</c>): the user's template (<c>reshade.rdr2.ini</c> for RDR2 / Max Payne 3, whose
    /// Vulkan depth settings are left out of a DirectX install), the screenshot folder, and the two hotkeys and the effect
    /// list style when they differ from ReShade's own defaults (so the template's choice stands otherwise, as upstream).
    /// For the Vulkan layer install (#92) pass <paramref name="vulkan"/>: the Vulkan template, depth settings kept.
    /// </summary>
    /// <param name="templates">Null: no template (RHI's built-in baseline for a new file, nothing merged into an existing one).</param>
    public static ReShadeIniDefaults InstallDefaults(LinuxAppSettings settings, DetectedGame game, UserIniTemplates? templates, bool vulkan = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(game);
        var template = UserIniTemplates.ReShadeTemplateFor(game, vulkan);
        var overlay = LinuxAppSettings.NormalizeHotkey(settings.OverlayHotkey, DefaultOverlayHotkey);
        var screenshot = LinuxAppSettings.NormalizeHotkey(settings.ScreenshotHotkey, DefaultScreenshotHotkey);
        return new ReShadeIniDefaults
        {
            Template = templates?.Read(template, out _),
            SkipPreprocessorDefinitions = !vulkan && template == UserIniTemplate.ReShadeRdr2,
            ScreenshotSavePath = ScreenshotSavePath(settings, game),
            OverlayHotkey = overlay == DefaultOverlayHotkey ? null : overlay,
            ScreenshotHotkey = screenshot == DefaultScreenshotHotkey ? null : screenshot,
            VariableListUseTabs = settings.ReShadeVariableListUseTabs ? null : false
        };
    }

    /// <summary>
    /// "Apply to selected game" / "Apply to all games" (upstream <c>ApplyScreenshotSettingsToIni</c>): the screenshot
    /// folder (when set), both hotkeys and the effect list style go into the game's ReShade.ini and every numbered
    /// <c>ReShade&lt;N&gt;.ini</c> beside it (one per extra swap chain). RDR2 / Max Payne 3 keep their template's End key
    /// (upstream skips the overlay key there). A ReShade.ini is created when the game has none. Returns the main file.
    /// </summary>
    public static string ApplyToGame(LinuxAppSettings settings, DetectedGame game)
    {
        var plan = PlanForGame(settings, game);
        plan.CreateScreenshotFolder();
        IniLines.WriteAll(plan.Writes);
        return plan.MainIni;
    }

    /// <summary>
    /// What <see cref="ApplyToGame"/> writes into one game, worked out without writing anything (#206: the journaled
    /// apply tracks every target first): the main file, the planned writes (only files whose lines change) and the file
    /// each write replaces under the one ReShade.ini link rule (a shared <c>~/.config/ReShade</c> file, never the link).
    /// Throws, before any write, for a missing folder, an Xbox title RHI must not change, or a link the rule refuses.
    /// </summary>
    internal static ReShadeSettingsPlan PlanForGame(LinuxAppSettings settings, DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(game);
        var root = game.BinaryDirectory ?? game.InstallPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Selected game binary directory is unavailable.");

        // Refs #90: nothing of an Xbox title the installer app is working on (or cannot be checked) is changed.
        var mainIni = RHI.Core.Services.XboxWriteGate.Checked(FindCaseInsensitive(root, "ReShade.ini") ?? Path.Combine(root, "ReShade.ini")); // w3/scan hook
        var defaults = SettingsFor(settings, game);

        // Every file of the game is planned first and written whole or not at all (review finding 6), under the one
        // ReShade.ini link rule (review finding 8).
        var writes = new List<IniFileWrite>();
        foreach (var iniPath in new[] { mainIni }.Concat(NumberedInis(root)))
        {
            var lines = IniLines.Read(RHI.Core.Services.XboxWriteGate.Checked(iniPath));
            var before = File.Exists(iniPath) ? lines.ToList() : null;
            ReShadeIniManager.ApplySettings(lines, defaults);
            if (before is null || !lines.SequenceEqual(before, StringComparer.Ordinal))
                writes.Add(ReShadeIniManager.PlannedWrite(iniPath, lines, root, game.InstallPath));
        }
        var targets = writes.Select(write => AtomicFile.ResolveUserFileTarget(write.Path, write.Policy)).ToList();
        return new ReShadeSettingsPlan(mainIni, writes, targets, ResolveScreenshotPath(settings, game));
    }

    /// <summary>The Settings values "Apply to all games" writes (upstream <c>ApplyScreenshotSettingsToIni</c>).</summary>
    internal static ReShadeIniDefaults SettingsFor(LinuxAppSettings settings, DetectedGame game) => new()
    {
        ScreenshotSavePath = ScreenshotSavePath(settings, game),
        OverlayHotkey = UserIniTemplates.IsRdr2Template(game.Name) ? null : LinuxAppSettings.NormalizeHotkey(settings.OverlayHotkey, DefaultOverlayHotkey),
        ScreenshotHotkey = LinuxAppSettings.NormalizeHotkey(settings.ScreenshotHotkey, DefaultScreenshotHotkey),
        VariableListUseTabs = settings.ReShadeVariableListUseTabs
    };

    /// <summary><c>ReShade2.ini</c>, <c>ReShade3.ini</c> … beside the main file (any case), never <c>ReShadePreset.ini</c>.</summary>
    internal static IEnumerable<string> NumberedInis(string directory)
        => Directory.EnumerateFiles(directory, "*.ini", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                return stem.Length > "reshade".Length
                       && stem.StartsWith("reshade", StringComparison.OrdinalIgnoreCase)
                       && stem["reshade".Length..].All(char.IsAsciiDigit);
            })
            .Order(StringComparer.Ordinal);

    /// <summary>Folders a symlinked ReShade.ini may point into: the game's binary folder, its install folder.</summary>
    internal static IReadOnlyList<string> GameRoots(DetectedGame game, string binaryDirectory)
        => new[] { binaryDirectory, game.InstallPath }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList()!;

    private static string? ScreenshotSavePath(LinuxAppSettings settings, DetectedGame game)
    {
        var path = ResolveScreenshotPath(settings, game);
        return string.IsNullOrWhiteSpace(path) ? null : ToWindowsStylePath(path);
    }

    internal static void SetValue(List<string> lines, string section, string key, string value)
        => IniLines.Set(lines, section, key, value);

    internal static string ToWindowsStylePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return Path.IsPathRooted(path) && path.StartsWith("/", StringComparison.Ordinal)
            ? "Z:" + normalized
            : normalized;
    }

    private static string SanitizeDirectoryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(name.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Game" : result;
    }

    private static string? FindCaseInsensitive(string directory, string fileName)
        => Directory.EnumerateFiles(directory)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
}
