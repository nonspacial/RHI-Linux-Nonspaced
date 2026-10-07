namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Decides from Steam's own records (not names) whether an installed app is a game and how Steam runs
/// it: <c>appmanifest</c> <c>StateFlags</c>, the app type and platforms in <c>appcache/appinfo.vdf</c>,
/// and the per-app compatibility tool in <c>config/config.vdf</c> <c>CompatToolMapping</c>. When Steam
/// has no record for an app, the old name list and prefix test still apply.
/// </summary>
internal sealed class SteamAppClassifier
{
    internal enum LaunchPlatform { Unknown, Native, Windows }

    // App types Steam installs that are never games (runtimes, Proton, redistributables, SteamVR,
    // controller configs, soundtracks). Game, Application, Demo, Beta and unknown types stay listed.
    private static readonly HashSet<string> NonGameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tool", "Config", "DLC", "Music", "Video", "Series", "Hardware", "Driver", "Plugin"
    };

    private readonly IReadOnlyDictionary<int, SteamAppInfo.Entry> _appInfo;
    private readonly IReadOnlyDictionary<int, string> _compatTools;
    private readonly string? _defaultCompatTool;

    private SteamAppClassifier(IReadOnlyDictionary<int, SteamAppInfo.Entry> appInfo, IReadOnlyDictionary<int, string> compatTools, string? defaultCompatTool)
    {
        _appInfo = appInfo;
        _compatTools = compatTools;
        _defaultCompatTool = defaultCompatTool;
    }

    public static SteamAppClassifier Load(string steamRoot, IEnumerable<string> libraries)
    {
        var appIds = new HashSet<int>();
        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            try
            {
                foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                    if (SteamGameDetector.ExtractAppIdFromAcfFilename(Path.GetFileName(acf)) is int id) appIds.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The detector reports nothing for an unreadable library either.
            }
        }

        var appInfo = SteamAppInfo.Read(Path.Combine(steamRoot, "appcache", "appinfo.vdf"), appIds);
        var (tools, defaultTool) = ReadCompatToolMapping(Path.Combine(steamRoot, "config", "config.vdf"));
        return new SteamAppClassifier(appInfo, tools, defaultTool);
    }

    /// <summary>Bit 4 of <c>StateFlags</c> is "fully installed"; a missing value is not held against the app.</summary>
    public static bool IsFullyInstalled(string? stateFlags)
        => string.IsNullOrWhiteSpace(stateFlags) || !long.TryParse(stateFlags, out var flags) || (flags & 4) != 0;

    public bool IsNotAGame(int? appId, string name, string installDir)
    {
        if (appId is int id && _appInfo.TryGetValue(id, out var entry) && !string.IsNullOrWhiteSpace(entry.Type))
            return NonGameTypes.Contains(entry.Type.Trim());
        return DetectionHelpers.IsKnownSteamTool(name, installDir);
    }

    public LaunchPlatform Platform(int? appId)
    {
        if (appId is not int id) return LaunchPlatform.Unknown;
        if (_compatTools.TryGetValue(id, out var tool))
            return IsLinuxRuntime(tool) ? LaunchPlatform.Native : LaunchPlatform.Windows;
        if (_appInfo.TryGetValue(id, out var entry) && !string.IsNullOrWhiteSpace(entry.OsList))
            return entry.OsList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("linux", StringComparer.OrdinalIgnoreCase)
                ? LaunchPlatform.Native
                : LaunchPlatform.Windows;
        return LaunchPlatform.Unknown;
    }

    /// <summary>The tool Steam will run the app with: its own mapping, else the global default for Windows-only apps.</summary>
    public string? CompatTool(int? appId)
    {
        if (appId is not int id) return null;
        if (_compatTools.TryGetValue(id, out var tool)) return IsLinuxRuntime(tool) ? null : tool;
        return Platform(appId) == LaunchPlatform.Windows ? _defaultCompatTool : null;
    }

    // Steam Linux Runtime mappings run a native build inside a container; they are not Proton.
    private static bool IsLinuxRuntime(string tool)
        => tool.StartsWith("steamlinuxruntime", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Per-app tool names; an empty name or <c>None</c> (the user unticked "force a compatibility tool";
    /// both spellings occur in real files) is no mapping.
    /// Key <c>0</c> is the global default Steam applies to Windows-only apps.
    /// </summary>
    internal static (IReadOnlyDictionary<int, string> Tools, string? DefaultTool) ReadCompatToolMapping(string configVdf)
    {
        var tools = new Dictionary<int, string>();
        string? defaultTool = null;
        if (!File.Exists(configVdf)) return (tools, null);
        SteamKeyValues root;
        try { root = SteamKeyValues.Parse(File.ReadAllText(configVdf)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (tools, null); }

        var mapping = root.Child("InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping");
        if (mapping is null) return (tools, null);
        foreach (var (key, entry) in mapping.Children)
        {
            if (!int.TryParse(key, out var id)) continue;
            var name = entry.Value("name")?.Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (id == 0) defaultTool = name;
            else tools[id] = name;
        }
        return (tools, defaultTool);
    }
}
