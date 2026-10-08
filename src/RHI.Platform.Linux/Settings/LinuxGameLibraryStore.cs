using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

public sealed class LinuxManualGameEntry
{
    public string Name { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? PrefixPath { get; set; }
    public string? LaunchCommand { get; set; }
    public string? Runner { get; set; }
    public GraphicsApi? UserGraphicsApiOverride { get; set; }
}

public sealed class LinuxGameEditOverride
{
    /// <summary>Optional UI-only launcher/platform badge override. "Automatic" or empty returns to inference.</summary>
    public string? LaunchProvider { get; set; }

    /// <summary>Confirmed game executable used for runtime detection and game-local patches.</summary>
    public string? PatchExecutablePath { get; set; }

    /// <summary>Confirmed game root used for indexing, manifests and local component discovery.</summary>
    public string? GameRootPath { get; set; }

    /// <summary>Confirmed binary directory used for proxy/add-on deployment.</summary>
    public string? BinaryDirectory { get; set; }

    public LinuxGameEditOverride Normalize()
    {
        LaunchProvider = string.IsNullOrWhiteSpace(LaunchProvider) ||
                         LaunchProvider.Equals("Automatic", StringComparison.OrdinalIgnoreCase) ||
                         LaunchProvider.Equals("Automático", StringComparison.OrdinalIgnoreCase)
            ? null
            : LaunchProvider.Trim();
        PatchExecutablePath = NormalizeOptionalPath(PatchExecutablePath);
        GameRootPath = NormalizeOptionalPath(GameRootPath);
        BinaryDirectory = NormalizeOptionalPath(BinaryDirectory);
        return this;
    }

    public bool HasPatchTarget =>
        !string.IsNullOrWhiteSpace(PatchExecutablePath) &&
        !string.IsNullOrWhiteSpace(GameRootPath) &&
        !string.IsNullOrWhiteSpace(BinaryDirectory);

    private static string? NormalizeOptionalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim())); }
        catch { return value.Trim(); }
    }
}

public sealed class LinuxGameLibraryState
{
    public HashSet<string> FavouriteGames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HiddenGames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LinuxManualGameEntry> ManualGames { get; set; } = [];

    /// <summary>
    /// Per-game user authority for launcher badge presentation and the separate game-local patch target.
    /// Keys use the launcher/library identity, so a patch root change does not orphan the override itself.
    /// </summary>
    public Dictionary<string, LinuxGameEditOverride> GameEdits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-game HDR launch orchestration. This stores only RHI launch preferences, never runner binaries/configuration.</summary>
    public Dictionary<string, LinuxHdrGameProfile> HdrProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string FilterMode { get; set; } = "All Games";

    public LinuxGameLibraryState Normalize()
    {
        FavouriteGames = new HashSet<string>(FavouriteGames ?? [], StringComparer.OrdinalIgnoreCase);
        HiddenGames = new HashSet<string>(HiddenGames ?? [], StringComparer.OrdinalIgnoreCase);
        ManualGames ??= [];
        GameEdits = JsonStoreMaps.CaseInsensitive(GameEdits);
        foreach (var key in GameEdits.Keys.ToArray())
        {
            var normalized = (GameEdits[key] ?? new LinuxGameEditOverride()).Normalize();
            if (!normalized.HasPatchTarget && string.IsNullOrWhiteSpace(normalized.LaunchProvider))
                GameEdits.Remove(key);
            else
                GameEdits[key] = normalized;
        }
        HdrProfiles = JsonStoreMaps.CaseInsensitive(HdrProfiles);
        foreach (var key in HdrProfiles.Keys.ToArray()) HdrProfiles[key] = (HdrProfiles[key] ?? new LinuxHdrGameProfile()).Normalize();
        ManualGames = ManualGames
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.InstallPath))
            .Select(entry =>
            {
                entry.Name = entry.Name.Trim();
                entry.InstallPath = NormalizePath(entry.InstallPath);
                entry.ExecutablePath = NormalizeOptionalPath(entry.ExecutablePath);
                entry.PrefixPath = NormalizeOptionalPath(entry.PrefixPath);
                entry.LaunchCommand = string.IsNullOrWhiteSpace(entry.LaunchCommand) ? null : entry.LaunchCommand.Trim();
                entry.Runner = string.IsNullOrWhiteSpace(entry.Runner) ? "Manual Windows" : entry.Runner.Trim();
                return entry;
            })
            .GroupBy(entry => ManualIdentity(entry), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        FilterMode = LinuxGameLibraryStore.NormalizeFilterMode(FilterMode);
        return this;
    }

    private static string? NormalizeOptionalPath(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizePath(value);

    /// <summary>
    /// Removes the manual entries behind a library card. A card can be a manual entry merged with a
    /// launcher entry for the same folder spelt differently (symlink, trailing separator) or moved by a
    /// manifest install-path override, so every path the card stands for is compared by filesystem
    /// identity (Refs #55). Returns how many entries were removed.
    /// </summary>
    public int RemoveManualEntriesFor(RHI.Core.Models.DetectedGame game)
    {
        var paths = new[] { game.InstallPath, game.ManifestInstallRoot, game.LibraryIdentityPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => RHI.Core.Services.PathIdentity.Canonicalize(path!))
            .ToHashSet(StringComparer.Ordinal);
        return ManualGames.RemoveAll(entry => paths.Contains(RHI.Core.Services.PathIdentity.Canonicalize(entry.InstallPath)));
    }

    /// <summary>
    /// Moves library state saved under keys this card had before onto its current key (Refs #55, #147):
    /// <list type="bullet">
    /// <item>the key builds before #147 wrote for it (source, AppID and lower-cased folder);</item>
    /// <item>for a Steam game, that older key at the same <c>steamapps/common/…</c> folder of another library (the
    /// library was moved before the key changed) — same source and AppID, so it is this game;</item>
    /// <item>the keys, old and current, of duplicates merged into the card (another launcher's entry, a symlink
    /// spelling of its folder);</item>
    /// <item>for a folder-keyed game, a key of the same source naming its folder in another spelling today
    /// (<see cref="LinuxGameLibraryStore.SameFolderKeys"/>), and the symlink-resolved key earlier builds of this
    /// branch wrote (<see cref="LinuxGameLibraryStore.ResolvedFolderKey(RHI.Core.Models.DetectedGame)"/>).</item>
    /// </list>
    /// Favourite and hidden are carried over; a game edit or HDR profile moves when the card has none of its own (an
    /// existing one is never overwritten, and the older entry then stays where it is). Keys no detected card claims are
    /// left as they are, so a game that is not found today (unmounted drive) keeps its state. Returns whether anything moved.
    /// </summary>
    public bool AdoptOlderKeys(RHI.Core.Models.DetectedGame game)
    {
        var key = LinuxGameLibraryStore.GameKey(game);
        var changed = false;
        foreach (var old in OlderKeys(game))
        {
            if (string.Equals(old, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (FavouriteGames.Remove(old)) { FavouriteGames.Add(key); changed = true; }
            if (HiddenGames.Remove(old)) { HiddenGames.Add(key); changed = true; }
            changed |= Move(GameEdits, old, key);
            changed |= Move(HdrProfiles, old, key);
        }
        return changed;
    }

    /// <summary>The game's edit under its current key, else under a key an older build wrote for this folder.</summary>
    public bool TryGetEdit(RHI.Core.Models.DetectedGame game, out LinuxGameEditOverride edit)
    {
        if (GameEdits.TryGetValue(LinuxGameLibraryStore.GameKey(game), out edit!)) return true;
        if (GameEdits.TryGetValue(LinuxGameLibraryStore.LegacyGameKey(game), out edit!)) return true;
        return LinuxGameLibraryStore.ResolvedFolderKey(game) is { } resolved && GameEdits.TryGetValue(resolved, out edit!);
    }

    private string[] OlderKeys(RHI.Core.Models.DetectedGame game)
    {
        var keys = new List<string> { LinuxGameLibraryStore.LegacyGameKey(game) };
        if (LinuxGameLibraryStore.ResolvedFolderKey(game) is { } resolved) keys.Add(resolved);
        foreach (var alias in game.MergedAliases)
        {
            keys.Add(LinuxGameLibraryStore.GameKey(alias));
            keys.Add(LinuxGameLibraryStore.LegacyGameKey(alias));
            if (LinuxGameLibraryStore.ResolvedFolderKey(alias) is { } aliasResolved) keys.Add(aliasResolved);
        }
        keys.AddRange(LinuxGameLibraryStore.SameFolderKeys(game, FavouriteGames.Concat(HiddenGames).Concat(GameEdits.Keys).Concat(HdrProfiles.Keys)).ToArray());
        if (game.SteamAppId is int appId && LinuxGameLibraryStore.SteamLibraryTail(LinuxGameLibraryStore.IdentityPath(game)) is { } tail)
        {
            var prefix = LinuxGameLibraryStore.LegacyKeyPrefix(game.Source, appId);
            keys.AddRange(FavouriteGames.Concat(HiddenGames).Concat(GameEdits.Keys).Concat(HdrProfiles.Keys)
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(LinuxGameLibraryStore.SteamLibraryTail(k[prefix.Length..]), tail, StringComparison.OrdinalIgnoreCase)));
        }
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool Move<T>(Dictionary<string, T> entries, string from, string to)
    {
        if (entries.ContainsKey(to) || !entries.Remove(from, out var value)) return false;
        entries[to] = value;
        return true;
    }

    private static string NormalizePath(string value)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim())); }
        catch { return value.Trim(); }
    }

    private static string ManualIdentity(LinuxManualGameEntry entry)
        => $"{entry.Name}|{entry.InstallPath}|{entry.ExecutablePath}";
}

/// <summary>
/// XDG-native persisted library state. This deliberately stays separate from settings.json:
/// settings are global preferences, while this file owns library membership and per-game visibility state.
/// </summary>
public sealed class LinuxGameLibraryStore : IGameStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly JsonFileStore<LinuxGameLibraryState> _file;

    public string LibraryPath { get; }

    public LinuxGameLibraryStore(string? libraryPath = null)
    {
        LibraryPath = libraryPath ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "game-library.json");
        // Normalising is part of reading: content it cannot handle (e.g. a null entry) is unreadable.
        _file = new JsonFileStore<LinuxGameLibraryState>(LibraryPath, JsonOptions,
            parse: text => JsonSerializer.Deserialize<LinuxGameLibraryState>(text, JsonOptions)?.Normalize());
    }

    /// <summary>
    /// Never throws: a missing or unreadable library yields an empty state. An unreadable file is
    /// copied aside and <see cref="Save"/> refuses to replace it until it reads again.
    /// </summary>
    public LinuxGameLibraryState Load() => Load(out _);

    /// <summary>As <see cref="Load()"/>; <paramref name="unreadable"/> says the state is an empty stand-in for a file
    /// that could not be read (saves are refused until it reads again).</summary>
    public LinuxGameLibraryState Load(out bool unreadable)
    {
        var load = _file.Load();
        unreadable = load.IsUnreadable;
        return load.Value ?? new LinuxGameLibraryState();
    }

    /// <summary>A deep copy (JSON round trip): callers mutate it and adopt it only once it is saved.</summary>
    public static LinuxGameLibraryState Copy(LinuxGameLibraryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return (JsonSerializer.Deserialize<LinuxGameLibraryState>(JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), JsonOptions)
                ?? new LinuxGameLibraryState()).Normalize();
    }

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> while an unreadable library file is on disk.</summary>
    public void Save(LinuxGameLibraryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Normalize();
        _file.Save(state);
    }

    /// <summary>
    /// "Repair after move" (Refs #38, #147): favourite, hidden, edits (their paths rebased onto the new
    /// folder) and HDR settings stored under the game's old library key follow it; an edit under a key that
    /// did not change (Steam, Heroic) has its paths rebased in place.
    /// </summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        return _file.Stage(state =>
        {
            if (state is null) return null;
            var changed = move.MoveLibraryFlag(state.FavouriteGames);
            changed |= move.MoveLibraryFlag(state.HiddenGames);
            changed |= move.MoveLibraryEntries(state.GameEdits, edit => Rebase(move, edit));
            // A store-keyed game (Steam, Heroic) keeps its key when it moves, so its edit is not moved above: its
            // patch target still names the old folder and is re-pointed in place (Refs #147).
            if (state.GameEdits.TryGetValue(move.NewLibraryKey, out var current) && Rebase(move, current) is var rebased
                && !SamePaths(current, rebased))
            {
                state.GameEdits[move.NewLibraryKey] = rebased;
                changed = true;
            }
            changed |= move.MoveLibraryEntries(state.HdrProfiles);
            return changed ? state.Normalize() : null;
        });
    }

    /// <summary>
    /// The game's confirmed patch target (Edit game: root, binaries folder, executable) when its root no longer exists —
    /// the game moved and its edit did not follow. "Repair after move" maps these onto the game's current folder even
    /// when RHI installed nothing there (Refs #147). Empty when there is nothing to re-point or the library is unreadable.
    /// </summary>
    public IReadOnlyList<string> MovedPaths(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var state = Load(out var unreadable);
        if (unreadable || !state.TryGetEdit(game, out var edit) || !edit.HasPatchTarget || Directory.Exists(edit.GameRootPath)) return [];
        return [edit.GameRootPath!, edit.BinaryDirectory!, edit.PatchExecutablePath!];
    }

    private static LinuxGameEditOverride Rebase(GameStateMove move, LinuxGameEditOverride edit) => new()
    {
        LaunchProvider = edit.LaunchProvider,
        PatchExecutablePath = MapOptional(move, edit.PatchExecutablePath),
        GameRootPath = MapOptional(move, edit.GameRootPath),
        BinaryDirectory = MapOptional(move, edit.BinaryDirectory)
    };

    private static bool SamePaths(LinuxGameEditOverride a, LinuxGameEditOverride b)
        => a.PatchExecutablePath == b.PatchExecutablePath && a.GameRootPath == b.GameRootPath && a.BinaryDirectory == b.BinaryDirectory;

    private static string? MapOptional(GameStateMove move, string? path) => path is null ? null : move.MapPath(path);

    /// <summary>
    /// The key a game's library state (favourite, hidden, edit, HDR profile) is saved under. It survives moving the game
    /// to another library or drive (Refs #147): a Steam game is keyed by its AppID and a Heroic game by its app id —
    /// plus the child's name for a manifest split game, whose children share one AppID — and any other game by its
    /// source and its library folder as the launcher spells it (symlinks not resolved, so re-pointing a symlinked games
    /// folder keeps the key). Keys older builds wrote (<see cref="LegacyGameKey(DetectedGame)"/>) are adopted by
    /// <see cref="LinuxGameLibraryState.AdoptOlderKeys"/>.
    /// <para>Known limits (kept, documented): a split child's key carries the remote manifest's child name, so an upstream
    /// rename of that child leaves its state under the old name (kept, not adopted: the old key does not record the child's
    /// sub-folder). The same split child in two Steam libraries gives two cards with one key, so they share favourite,
    /// hidden, edit and HDR profile (rare: Steam installs an app in one library). The same AppID in two libraries is one
    /// card: favourite and hidden of both are united, an edit or HDR profile comes from the card's own older key first
    /// and the other stays under its own key (kept, detached). A stored key in another spelling of the game's folder is
    /// adopted by where that spelling points today (<c>SameFolderKeys</c>): if a symlink in an old spelling is re-pointed
    /// at another disk holding a different game in a same-named folder of the same source, and the first game's launcher
    /// spelling changed to the direct path before RHI saw it, the second game inherits the first game's state (three
    /// coincidences; a key written as-is through that link has the same exposure).</para>
    /// </summary>
    public static string GameKey(DetectedGame game) => GameKey(game, IdentityPath(game));

    /// <summary>The key <paramref name="game"/> would have with <paramref name="identityPath"/> as its library folder.</summary>
    public static string GameKey(DetectedGame game, string identityPath)
        => StoreKey(game.SteamAppId, game.HeroicAppName, game.IsManifestSplitChild ? game.Name : null)
           ?? FolderKey(game.Source, identityPath);

    /// <summary>The key a card stored under before it was merged into another (see <see cref="DetectedGame.MergedAliases"/>).</summary>
    public static string GameKey(LibraryEntryAlias alias)
        => StoreKey(alias.SteamAppId, alias.HeroicAppName, null) ?? FolderKey(alias.Source, alias.IdentityPath);

    /// <summary>
    /// A key unique to one card at one folder (source, AppID, folder) for in-session caches (the runtime probe, the
    /// selection kept across a refresh), which must not survive a move. Never for saved state: use <see cref="GameKey(DetectedGame)"/>.
    /// The folder is compared as Linux does, case-sensitively: <c>Game</c> and <c>game</c> are two folders, so two cards
    /// never share one cached analysis (the pre-#147 key lower-cased it; w2 merge review, nit 4).
    /// </summary>
    public static string SessionKey(DetectedGame game)
    {
        var identityPath = IdentityPath(game);
        string path;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(identityPath)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = (identityPath ?? "").Trim();
        }
        var app = game.SteamAppId is int id ? $"steam:{id}" : "nosteam";
        return $"{Source(game.Source)}|{app}|{path}";
    }

    /// <summary>The key builds before #147 saved library state under: source, Steam AppID and lower-cased folder.</summary>
    public static string LegacyGameKey(DetectedGame game) => LegacyGameKey(game.Source, IdentityPath(game), game.SteamAppId);

    /// <summary>The pre-#147 key of a merged duplicate.</summary>
    public static string LegacyGameKey(LibraryEntryAlias alias) => LegacyGameKey(alias.Source, alias.IdentityPath, alias.SteamAppId);

    /// <summary>The pre-#147 key <paramref name="identityPath"/> gives a game of this source and AppID.</summary>
    public static string LegacyGameKey(string? sourceName, string identityPath, int? steamAppId)
    {
        string path;
        try { path = Path.GetFullPath(identityPath).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = (identityPath ?? "").Trim().ToLowerInvariant();
        }
        var app = steamAppId is int id ? $"steam:{id}" : "nosteam";
        return $"{Source(sourceName)}|{app}|{path}";
    }

    /// <summary>The start of every pre-#147 key of a Steam game with this source and AppID (the folder follows).</summary>
    internal static string LegacyKeyPrefix(string? sourceName, int steamAppId) => $"{Source(sourceName)}|steam:{steamAppId}|";

    /// <summary>
    /// The part of a Steam game folder below <c>steamapps/common/</c> (<c>installdir</c>, plus a split child's
    /// sub-folder), lower-cased; the same in every Steam library. Null for a folder outside a Steam library.
    /// </summary>
    internal static string? SteamLibraryTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        const string marker = "/steamapps/common/";
        var at = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var tail = normalized[(at + marker.Length)..].ToLowerInvariant();
        return tail.Length == 0 ? null : tail;
    }

    internal static string IdentityPath(DetectedGame game)
        => string.IsNullOrWhiteSpace(game.LibraryIdentityPath) ? game.InstallPath : game.LibraryIdentityPath;

    private static string? StoreKey(int? steamAppId, string? heroicAppName, string? splitChildName)
    {
        var split = string.IsNullOrWhiteSpace(splitChildName) ? "" : "|split:" + splitChildName.Trim();
        if (steamAppId is int id) return $"steam:{id}{split}";
        return string.IsNullOrWhiteSpace(heroicAppName) ? null : $"heroic:{heroicAppName.Trim()}{split}";
    }

    /// <summary>
    /// A folder-keyed game's key uses the folder as the launcher spells it (absolute, no trailing separator), never with
    /// symlinks resolved: re-pointing a symlinked games folder (<c>~/Games -&gt; /media/disk/Games</c>, moved with
    /// <c>mv</c> + <c>ln -sfn</c>) must not change the key. Two spellings of one folder are one card in the scanner, and the
    /// duplicate's key is adopted through <see cref="DetectedGame.MergedAliases"/>.
    /// </summary>
    private static string FolderKey(string? sourceName, string? identityPath)
        => $"folder:{Source(sourceName)}|{AsWritten(identityPath)}";

    /// <summary>
    /// The key builds of this branch wrote for a folder-keyed game before keys kept the folder as written: the folder's
    /// <see cref="PathIdentity"/> (symlinks resolved). Null for a store-keyed game (Steam, Heroic) or when it equals the
    /// current key. Adopted by <see cref="LinuxGameLibraryState.AdoptOlderKeys"/> while the symlink still points where it did.
    /// </summary>
    internal static string? ResolvedFolderKey(DetectedGame game) => ResolvedFolderKey(game, IdentityPath(game));

    /// <summary>As <see cref="ResolvedFolderKey(DetectedGame)"/> with <paramref name="identityPath"/> as the game's library folder.</summary>
    internal static string? ResolvedFolderKey(DetectedGame game, string identityPath)
        => StoreKey(game.SteamAppId, game.HeroicAppName, game.IsManifestSplitChild ? game.Name : null) is null
            ? ResolvedFolderKey(game.Source, identityPath)
            : null;

    /// <summary>The resolved-form key of a merged duplicate (see <see cref="ResolvedFolderKey(DetectedGame)"/>).</summary>
    internal static string? ResolvedFolderKey(LibraryEntryAlias alias)
        => StoreKey(alias.SteamAppId, alias.HeroicAppName, null) is null ? ResolvedFolderKey(alias.Source, alias.IdentityPath) : null;

    private static string? ResolvedFolderKey(string? sourceName, string? identityPath)
    {
        var resolved = $"folder:{Source(sourceName)}|{PathIdentity.Canonicalize(identityPath ?? "")}";
        return string.Equals(resolved, FolderKey(sourceName, identityPath), StringComparison.Ordinal) ? null : resolved;
    }

    /// <summary>
    /// Stored folder keys of <paramref name="game"/>'s source that name its folder in another spelling today (the launcher
    /// now spells the path through a symlink, or no longer does, or the merged duplicate whose spelling the card used is
    /// gone): same source, same last path component, and the same <see cref="PathIdentity"/> now. Empty for a store-keyed game.
    /// </summary>
    internal static IEnumerable<string> SameFolderKeys(DetectedGame game, IEnumerable<string> stored)
    {
        if (StoreKey(game.SteamAppId, game.HeroicAppName, game.IsManifestSplitChild ? game.Name : null) is not null) yield break;
        var identity = IdentityPath(game);
        var own = FolderKey(game.Source, identity);
        var prefix = $"folder:{Source(game.Source)}|";
        var leaf = Path.GetFileName(AsWritten(identity));
        string? canonical = null;
        foreach (var key in stored)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(key, own, StringComparison.OrdinalIgnoreCase)) continue;
            var path = key[prefix.Length..];
            if (!string.Equals(Path.GetFileName(path), leaf, StringComparison.Ordinal)) continue; // cheap filter before any readlink
            canonical ??= PathIdentity.Canonicalize(identity);
            if (string.Equals(PathIdentity.Canonicalize(path), canonical, StringComparison.Ordinal)) yield return key;
        }
    }

    private static string AsWritten(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = path.Trim(); }
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length == 0 && full.Length > 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
    }

    private static string Source(string? sourceName) => (sourceName ?? "").Trim().ToLowerInvariant();

    public static string NormalizeFilterMode(string? value) => value?.Trim() switch
    {
        "Favourites" => "Favourites",
        "Installed" => "Installed",
        "Unreal" => "Unreal",
        "Unity" => "Unity",
        "Other" => "Other",
        "RenoDX" => "RenoDX",
        "Luma" => "Luma",
        "Hidden" => "Hidden",
        _ => "All Games"
    };
}
