using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

/// <summary>A game's choice for its shaders or its add-ons (#179, upstream per-game Global / Select / Off).</summary>
public enum ShaderAddonMode
{
    /// <summary>The global set (Settings → Shaders/Add-ons).</summary>
    Global,
    /// <summary>The game's own list.</summary>
    Select,
    /// <summary>None: RHI's shader packs (or Addon Manager add-ons) are taken out of the game.</summary>
    Off,
}

/// <summary>What the global shader set means (#179, upstream Settings \"Shaders\": Off / RHI Managed / Custom).</summary>
public enum GlobalShaderMode
{
    /// <summary>The global pack set.</summary>
    Managed,
    /// <summary>No shaders for games that follow the global set.</summary>
    Off,
    /// <summary>Only the local custom shader folder.</summary>
    Custom,
}

/// <summary>A game's shader and add-on choice as resolved now (adopted from the ledger when the game has no entry yet).</summary>
public sealed record ShaderAddonGameChoice(
    ShaderAddonMode ShaderMode,
    IReadOnlyList<string> ShaderPackIds,
    ShaderAddonMode AddonMode,
    IReadOnlyList<string> AddonIds,
    bool Saved);

/// <summary>
/// #179 (upstream 2.8.7 <c>SelectedShaderPacks</c>, <c>GlobalShadersOff</c>/<c>UseCustomShaders</c>,
/// <c>EnabledGlobalAddons</c>, <c>PerGameShaderMode</c>/<c>PerGameAddonMode</c> and the per-game selections): the global
/// shader set, its mode and the global add-ons, and per game a Global / Select / Off choice for shaders and for add-ons
/// with the game's own lists. <c>~/.config/rhi-linux/shader-addon-sets.json</c>, keyed like the ledger
/// (<see cref="GameRecordIdentity"/>).
/// <para>Migration (owner decision): a game this store has no entry for takes its choice from the ledger — the packs on
/// its RHI ReShade record become <b>Select</b> with exactly those packs, the Addon Manager record's selection becomes
/// <b>Select</b> with exactly those add-ons, anything else is <b>Global</b>. <see cref="AdoptFromLedger"/> saves that for
/// every such game at start-up; until saved the same answer is computed on every read. Older builds never read this file:
/// they keep using the records as before.</para>
/// <para>An unreadable file is copied aside and never saved over; callers then fall back to what the records say.</para>
/// </summary>
public sealed class GlobalShaderAddonStore : IGameStateStore
{
    /// <summary>The upstream global set on a fresh install is Lilium; on Linux it starts empty (see UPSTREAM.md #179).</summary>
    public static readonly IReadOnlyList<string> DefaultGlobalShaderPackIds = [];

    internal const string ReShadeShaderPacksKey = "ReShade.ShaderPacks";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly JsonFileStore<Document> _file;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly object _gate = new();

    internal sealed class Document
    {
        public int Schema { get; set; } = 1;
        public string GlobalShaderMode { get; set; } = nameof(Settings.GlobalShaderMode.Managed);
        /// <summary>Null: never edited (the default set).</summary>
        public List<string>? GlobalShaderPackIds { get; set; }
        public List<string> GlobalAddonIds { get; set; } = [];
        public Dictionary<string, GameEntry> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class GameEntry
    {
        public string ShaderMode { get; set; } = nameof(ShaderAddonMode.Global);
        public List<string> ShaderPackIds { get; set; } = [];
        public string AddonMode { get; set; } = nameof(ShaderAddonMode.Global);
        public List<string> AddonIds { get; set; } = [];
    }

    public GlobalShaderAddonStore(LinuxDeploymentRecordStore records, string? path = null)
    {
        _records = records;
        FilePath = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "shader-addon-sets.json");
        _file = new JsonFileStore<Document>(FilePath, JsonOptions);
    }

    public string FilePath { get; }

    /// <summary>True when the file exists but cannot be read (it was copied aside; nothing is saved over it).</summary>
    public bool IsUnreadable => _file.Load().IsUnreadable;

    // ── Global ─────────────────────────────────────────────────────────────

    /// <summary>The global shader mode (Managed when unreadable).</summary>
    public GlobalShaderMode ShaderMode
    {
        get
        {
            lock (_gate) return ParseGlobal(LoadOrNew().GlobalShaderMode);
        }
    }

    /// <summary>The global shader pack set.</summary>
    public IReadOnlyList<string> GlobalShaderPackIds
    {
        get
        {
            lock (_gate) return (IReadOnlyList<string>?)LoadOrNew().GlobalShaderPackIds?.ToArray() ?? DefaultGlobalShaderPackIds;
        }
    }

    /// <summary>The global add-ons.</summary>
    public IReadOnlyList<string> GlobalAddonIds
    {
        get
        {
            lock (_gate) return LoadOrNew().GlobalAddonIds.ToArray();
        }
    }

    /// <summary>Saves the global shader mode. Throws <see cref="IOException"/> when it cannot be saved.</summary>
    public void SetShaderMode(GlobalShaderMode mode) => Update(doc => doc.GlobalShaderMode = mode.ToString());

    /// <summary>Saves the global shader set. Throws <see cref="IOException"/> when it cannot be saved.</summary>
    public void SetGlobalShaderPackIds(IEnumerable<string> ids) => Update(doc => doc.GlobalShaderPackIds = Normalize(ids));

    /// <summary>Saves the global add-ons. Throws <see cref="IOException"/> when it cannot be saved.</summary>
    public void SetGlobalAddonIds(IEnumerable<string> ids) => Update(doc => doc.GlobalAddonIds = Normalize(ids));

    // ── Per game ───────────────────────────────────────────────────────────

    /// <summary>
    /// The game's choice: its saved entry, else what the ledger says (migration rule, see the class summary). Null when the
    /// file cannot be read (callers then keep what the records say).
    /// </summary>
    public ShaderAddonGameChoice? ChoiceFor(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var load = _file.Load();
            if (load.IsUnreadable) return null;
            var games = JsonStoreMaps.CaseInsensitive(load.Value?.Games);
            return GameRecordIdentity.TryGet(games, game, out var entry) && entry is not null
                ? ToChoice(entry, saved: true)
                : ToChoice(Adopted(LinuxDeploymentRecordStore.GameKey(game), _records.FindAll(game)), saved: false);
        }
    }

    /// <summary>
    /// Saves the game's choice as it resolves now when it has no entry yet, so a record an install writes later is never
    /// taken for an older build's selection. False when it could not be saved (logged).
    /// </summary>
    public bool Pin(DetectedGame game)
    {
        try
        {
            if (ChoiceFor(game) is not { } choice) return false;
            if (choice.Saved) return true;
            Change(game, _ => { });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shader-sets", $"Could not save the shader and add-on choice of {game.Name}", ex);
            return false;
        }
    }

    /// <summary>Sets the game's shader mode; <paramref name="packIds"/> (when given) becomes its Select list. Throws <see cref="IOException"/>.</summary>
    public void SetShaderMode(DetectedGame game, ShaderAddonMode mode, IEnumerable<string>? packIds = null)
        => Change(game, entry =>
        {
            entry.ShaderMode = mode.ToString();
            if (packIds is not null) entry.ShaderPackIds = Normalize(packIds);
        });

    /// <summary>Sets the game's add-on mode; <paramref name="addonIds"/> (when given) becomes its Select list. Throws <see cref="IOException"/>.</summary>
    public void SetAddonMode(DetectedGame game, ShaderAddonMode mode, IEnumerable<string>? addonIds = null)
        => Change(game, entry =>
        {
            entry.AddonMode = mode.ToString();
            if (addonIds is not null) entry.AddonIds = Normalize(addonIds);
        });

    /// <summary>
    /// Migration: saves an entry for every game the ledger has a ReShade or Addon Manager record for and this file has no
    /// entry for, with the ledger's packs/add-ons as Select. Returns the keys adopted (empty when nothing changed); throws
    /// <see cref="IOException"/> when the file cannot be read or written.
    /// </summary>
    public IReadOnlyList<string> AdoptFromLedger()
    {
        lock (_gate)
        {
            var load = _file.Load();
            if (load.IsUnreadable) throw new JsonStoreUnavailableException(Path.GetFullPath(FilePath), load.QuarantinePath);
            var doc = load.Value ?? new Document();
            doc.Games = JsonStoreMaps.CaseInsensitive(doc.Games);
            var all = _records.All();
            var adopted = new List<string>();
            foreach (var key in all.Where(r => r.Component is ManagedComponentKind.ReShade or ManagedComponentKind.ReShadeAddonManager)
                         .Select(r => r.GameKey).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.Ordinal))
            {
                if (doc.Games.ContainsKey(key)) continue;
                doc.Games[key] = Adopted(key, all.Where(r => string.Equals(r.GameKey, key, StringComparison.OrdinalIgnoreCase)).ToArray());
                adopted.Add(key);
            }
            if (adopted.Count > 0) _file.Save(doc);
            return adopted;
        }
    }

    /// <summary>"Repair after move": a folder-keyed game's entry follows it to its new key.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
            return _file.Stage(doc =>
            {
                if (doc is null) return null;
                doc.Games = JsonStoreMaps.CaseInsensitive(doc.Games);
                return move.MoveRecordEntries(doc.Games) ? doc : null;
            });
    }

    // ── Pure rules ─────────────────────────────────────────────────────────

    /// <summary>The migration rule for one game key: Select with the records' lists where a record has one, else Global.</summary>
    internal static GameEntry Adopted(string key, IReadOnlyList<LinuxDeploymentRecord> records)
    {
        var entry = new GameEntry();
        if (records.FirstOrDefault(r => r.Component == ManagedComponentKind.ReShade) is { } reshade)
        {
            entry.ShaderMode = nameof(ShaderAddonMode.Select);
            entry.ShaderPackIds = ReadList(reshade.Metadata.GetValueOrDefault(ReShadeShaderPacksKey));
        }
        if (records.FirstOrDefault(r => r.Component == ManagedComponentKind.ReShadeAddonManager) is { } manager)
        {
            entry.AddonMode = nameof(ShaderAddonMode.Select);
            entry.AddonIds = ReShadeAddonManagerService.SelectedIdsOf(manager).ToList();
        }
        return entry;
    }

    internal static ShaderAddonMode ParseMode(string? value)
        => Enum.TryParse<ShaderAddonMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : ShaderAddonMode.Global;

    internal static GlobalShaderMode ParseGlobal(string? value)
        => Enum.TryParse<GlobalShaderMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : GlobalShaderMode.Managed;

    private static ShaderAddonGameChoice ToChoice(GameEntry entry, bool saved)
        => new(ParseMode(entry.ShaderMode), (entry.ShaderPackIds ?? []).ToArray(), ParseMode(entry.AddonMode), (entry.AddonIds ?? []).ToArray(), saved);

    private static List<string> Normalize(IEnumerable<string> ids)
        => ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> ReadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return Normalize(JsonSerializer.Deserialize<string[]>(json) ?? []); }
        catch (JsonException) { return []; }
    }

    private Document LoadOrNew() => _file.Load().Value ?? new Document();

    private void Update(Action<Document> change)
    {
        lock (_gate)
        {
            var load = _file.Load();
            if (load.IsUnreadable) throw new JsonStoreUnavailableException(Path.GetFullPath(FilePath), load.QuarantinePath);
            var doc = load.Value ?? new Document();
            doc.Games = JsonStoreMaps.CaseInsensitive(doc.Games);
            change(doc);
            _file.Save(doc);
        }
    }

    // Edits the game's entry (adopted from the ledger first when it has none) and saves.
    private void Change(DetectedGame game, Action<GameEntry> change)
        => Update(doc =>
        {
            if (!GameRecordIdentity.TryGet(doc.Games, game, out var entry) || entry is null)
                entry = Adopted(LinuxDeploymentRecordStore.GameKey(game), _records.FindAll(game));
            entry.ShaderPackIds ??= [];
            entry.AddonIds ??= [];
            change(entry);
            GameRecordIdentity.Set(doc.Games, game, entry);
        });
}
