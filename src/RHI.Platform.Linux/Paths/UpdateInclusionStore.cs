using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// #178 (upstream 2.8.7 <c>ExcludeFromUpdateAll*</c>, <c>GlobalSkip*Updates</c>): which components Update All and the
/// background pass leave alone. Per game (this file, <c>~/.config/rhi-linux/update-inclusion.json</c>: the excluded
/// component names under the game's <see cref="GameRecordIdentity"/> key; the DLSS / Streamline entries stop
/// <see cref="DlssAutoFollowService"/> for that game) and for every game (Settings, read through <c>globalSkips</c>).
/// A component's own Install/Remove is never affected (upstream: exclusion applies to Update All / auto-update only).
/// Names this build does not know are kept on every write. An unreadable file excludes everything until it reads again
/// (it was copied aside and the user is told): updating what the user chose to keep is worse than updating nothing; that
/// state is its own answer (<see cref="UpdateExclusion.Unreadable"/>, <see cref="IsUnreadable"/>) so the dialog, the
/// component rows and the Update All summary say the file cannot be read instead of showing every box ticked. The parsed
/// file is kept while its version (inode, ctime, mtime, length) is unchanged, so per-row questions cost a stat.
/// </summary>
public sealed class UpdateInclusionStore : IGameStateStore
{
    /// <summary>The components Settings can leave out for every game (upstream's six).</summary>
    public static readonly IReadOnlyList<ManagedComponentKind> GlobalComponents =
    [
        ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX, ManagedComponentKind.ReLimiter,
        ManagedComponentKind.DisplayCommander, ManagedComponentKind.OptiScaler, ManagedComponentKind.REFramework
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly JsonFileStore<Dictionary<string, List<string>>> _file;
    private readonly Func<IReadOnlySet<ManagedComponentKind>> _globalSkips;
    private readonly Func<DlssFollowChoice> _follow;
    private readonly object _gate = new();
    // The last parse and the file version it came from (null: not cached, e.g. the file is missing).
    private JsonFileVersion? _parsedVersion;
    private (bool Unreadable, Dictionary<string, List<string>> Map) _parsed;

    /// <param name="globalSkips">The components Settings leaves out for every game (default: none).</param>
    /// <param name="followChoice">Which Follow newest toggles are on (default: neither); the DLSS / Streamline choices
    /// only apply while theirs is.</param>
    public UpdateInclusionStore(string? path = null, Func<IReadOnlySet<ManagedComponentKind>>? globalSkips = null, Func<DlssFollowChoice>? followChoice = null)
    {
        _file = new JsonFileStore<Dictionary<string, List<string>>>(
            path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "update-inclusion.json"), JsonOptions);
        _globalSkips = globalSkips ?? (() => new HashSet<ManagedComponentKind>());
        _follow = followChoice ?? (() => new DlssFollowChoice(false, false));
    }

    public string FilePath => _file.FilePath;

    /// <summary>True while the file is on disk but cannot be read: every component of every game is left out.</summary>
    public bool IsUnreadable
    {
        get
        {
            lock (_gate) return Parsed().Unreadable;
        }
    }

    /// <summary>Whether Follow newest is on for <paramref name="family"/> (DLSS or Streamline); false for anything else.</summary>
    public bool FollowsNewest(ManagedComponentKind family)
    {
        var choice = _follow();
        return family switch
        {
            ManagedComponentKind.Dlss => choice.Dlss,
            ManagedComponentKind.Streamline => choice.Streamline,
            _ => false
        };
    }

    /// <summary>How many times the file was parsed (tests: an unchanged file is parsed once).</summary>
    internal int ParsesForTesting { get; private set; }

    /// <summary>The global choices the Settings dialog saved.</summary>
    public static IReadOnlySet<ManagedComponentKind> GlobalSkipsFrom(LinuxAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var skips = new HashSet<ManagedComponentKind>();
        if (settings.GlobalSkipReShadeUpdates) skips.Add(ManagedComponentKind.ReShade);
        if (settings.GlobalSkipRenoDxUpdates) skips.Add(ManagedComponentKind.RenoDX);
        if (settings.GlobalSkipReLimiterUpdates) skips.Add(ManagedComponentKind.ReLimiter);
        if (settings.GlobalSkipDisplayCommanderUpdates) skips.Add(ManagedComponentKind.DisplayCommander);
        if (settings.GlobalSkipOptiScalerUpdates) skips.Add(ManagedComponentKind.OptiScaler);
        if (settings.GlobalSkipReFrameworkUpdates) skips.Add(ManagedComponentKind.REFramework);
        return skips;
    }

    /// <summary>
    /// Why Update All (or, for <see cref="ManagedComponentKind.Dlss"/> / <see cref="ManagedComponentKind.Streamline"/>,
    /// Follow newest) leaves <paramref name="component"/> of <paramref name="game"/> alone; <see cref="UpdateExclusion.None"/>
    /// when it does not.
    /// </summary>
    public UpdateExclusion ExclusionFor(DetectedGame game, ManagedComponentKind component)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (GlobalComponents.Contains(component) && _globalSkips().Contains(component)) return UpdateExclusion.AllGames;
        lock (_gate)
        {
            var (unreadable, map) = Parsed();
            if (unreadable) return UpdateExclusion.Unreadable;
            return GameRecordIdentity.TryGet(map, game, out var names)
                   && names is not null && names.Any(name => Is(name, component))
                ? UpdateExclusion.ThisGame
                : UpdateExclusion.None;
        }
    }

    /// <summary>
    /// The components left out for this game alone (not the Settings ones). Empty while the file cannot be read: ask
    /// <see cref="IsUnreadable"/> first (then everything is left out).
    /// </summary>
    public IReadOnlySet<ManagedComponentKind> ExcludedFor(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var (_, map) = Parsed();
            var excluded = new HashSet<ManagedComponentKind>();
            if (!GameRecordIdentity.TryGet(map, game, out var names) || names is null) return excluded;
            foreach (var name in names)
                if (Enum.TryParse<ManagedComponentKind>(name, ignoreCase: true, out var kind) && Enum.IsDefined(kind)) excluded.Add(kind);
            return excluded;
        }
    }

    /// <summary>
    /// Saves which of <paramref name="shown"/> are left out for this game (<paramref name="excluded"/>); every other stored
    /// name (a component the dialog did not show, one a newer build wrote) is kept. Throws
    /// <see cref="JsonStoreUnavailableException"/> while the file cannot be read.
    /// </summary>
    public void SetExcluded(DetectedGame game, IEnumerable<ManagedComponentKind> shown, IEnumerable<ManagedComponentKind> excluded)
    {
        ArgumentNullException.ThrowIfNull(game);
        var shownSet = shown.ToHashSet();
        var excludedSet = excluded.Where(shownSet.Contains).ToHashSet();
        lock (_gate)
        {
            var entries = JsonStoreMaps.CaseInsensitive(_file.Load().Value);
            var names = GameRecordIdentity.TryGet(entries, game, out var existing) && existing is not null
                ? existing.Where(name => !shownSet.Any(kind => Is(name, kind))).ToList()
                : [];
            names.AddRange(excludedSet.Order().Select(kind => kind.ToString()));
            if (names.Count == 0) GameRecordIdentity.Remove(entries, game);
            else GameRecordIdentity.Set(entries, game, names);
            _parsedVersion = null;
            _file.Save(entries);
        }
    }

    // The file as last parsed, re-parsed only when its version changed. Call under _gate.
    private (bool Unreadable, Dictionary<string, List<string>> Map) Parsed()
    {
        var version = JsonFileVersion.Of(_file.FilePath);
        if (version is not null && _parsedVersion == version) return _parsed;
        var load = _file.Load();
        ParsesForTesting++;
        _parsed = (load.IsUnreadable, JsonStoreMaps.CaseInsensitive(load.Value));
        _parsedVersion = version;
        return _parsed;
    }

    /// <summary>"Repair after move" (Refs #38): a folder-keyed game's choices follow it to its new key.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
        {
            _parsedVersion = null;
            return _file.Stage(current =>
            {
                var map = JsonStoreMaps.CaseInsensitive(current);
                return move.MoveRecordEntries(map) ? map : null;
            });
        }
    }

    private static bool Is(string? name, ManagedComponentKind kind)
        => string.Equals(name?.Trim(), kind.ToString(), StringComparison.OrdinalIgnoreCase);
}
