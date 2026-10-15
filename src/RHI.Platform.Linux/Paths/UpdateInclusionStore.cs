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
/// (it was copied aside and the user is told): updating what the user chose to keep is worse than updating nothing.
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
    private readonly object _gate = new();

    /// <param name="globalSkips">The components Settings leaves out for every game (default: none).</param>
    public UpdateInclusionStore(string? path = null, Func<IReadOnlySet<ManagedComponentKind>>? globalSkips = null)
    {
        _file = new JsonFileStore<Dictionary<string, List<string>>>(
            path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "update-inclusion.json"), JsonOptions);
        _globalSkips = globalSkips ?? (() => new HashSet<ManagedComponentKind>());
    }

    public string FilePath => _file.FilePath;

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
            var load = _file.Load();
            if (load.IsUnreadable) return UpdateExclusion.ThisGame;
            return GameRecordIdentity.TryGet(JsonStoreMaps.CaseInsensitive(load.Value), game, out var names)
                   && names is not null && names.Any(name => Is(name, component))
                ? UpdateExclusion.ThisGame
                : UpdateExclusion.None;
        }
    }

    /// <summary>The components left out for this game alone (not the Settings ones).</summary>
    public IReadOnlySet<ManagedComponentKind> ExcludedFor(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var load = _file.Load();
            var excluded = new HashSet<ManagedComponentKind>();
            if (!GameRecordIdentity.TryGet(JsonStoreMaps.CaseInsensitive(load.Value), game, out var names) || names is null) return excluded;
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
            _file.Save(entries);
        }
    }

    /// <summary>"Repair after move" (Refs #38): a folder-keyed game's choices follow it to its new key.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
            return _file.Stage(current =>
            {
                var map = JsonStoreMaps.CaseInsensitive(current);
                return move.MoveRecordEntries(map) ? map : null;
            });
    }

    private static bool Is(string? name, ManagedComponentKind kind)
        => string.Equals(name?.Trim(), kind.ToString(), StringComparison.OrdinalIgnoreCase);
}
