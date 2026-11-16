using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>How a Spotlight "What's installed" tile shows a component (upstream's dot colours).</summary>
public enum StatusTone
{
    /// <summary>Not installed: a grey dot.</summary>
    None,
    /// <summary>Installed: green.</summary>
    Installed,
    /// <summary>An update is available: purple (Tabs and Spotlight use purple for updates).</summary>
    Update,
    /// <summary>Not for this game: red.</summary>
    Unsupported,
}

/// <summary>One "What's installed" tile: the component's name, its status and the dot's tone.</summary>
public sealed record SpotlightStatusTile(string Name, string Status, StatusTone Tone);

/// <summary>
/// w14/layouts-b (#305, upstream 2.9.0–2.9.4 Spotlight, mockup D): the Spotlight layout's view-independent rules. The banner
/// keeps roughly the shape of Steam hero art (3840 × 1240) as the window widens, between 340 and 500 px, unless its bottom
/// edge was dragged (200–900, saved; double-click goes back to automatic). Covers are 120 × 180 at size 1 (0.6–2.0, the
/// strip's page-side edge drags them; double-click resets). The covers centre when they all fit. The strip can sit below
/// the page and drop its own background (the strip's ⚙). The "What's installed" tiles copy the Components rows; the
/// frame limiter tile covers ReLimiter and Display Commander. The side panel's keys and titles.
/// </summary>
public sealed class SpotlightPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public const double HeroMinHeight = 340, HeroMaxHeight = 500, HeroDragMin = 200, HeroDragMax = 900;
    public const double HeroArtAspect = 3840.0 / 1240.0;
    public const double BaseTileWidth = 120, BaseCoverHeight = 180, CoverScaleMin = 0.6, CoverScaleMax = 2.0;
    /// <summary>A cover tile's side margins (4 + 4) and the strip's least side padding.</summary>
    public const double TileGap = 8, ShelfMinSide = 20;

    // The side panel's built (non-section) keys, upstream's.
    public const string ScreenshotsKey = "Screenshots";
    public const string AchievementsKey = "Achievements";
    public const string GameSettingsKey = "GameSettings";
    public const string ArtworkPickerKey = "ArtworkPicker";

    private readonly Func<LinuxAppSettings> _settings;
    private readonly Func<Action<LinuxAppSettings>, string?> _save;

    /// <param name="settings">The settings in use.</param>
    /// <param name="save">Saves a change to Spotlight's fields; the localised refusal, or null.</param>
    public SpotlightPresenter(Func<LinuxAppSettings> settings, Func<Action<LinuxAppSettings>, string?> save)
    {
        _settings = settings;
        _save = save;
    }

    /// <summary>Raised after a saved Spotlight choice changed (the window applies it).</summary>
    public event Action? Changed;

    // ---- banner ----------------------------------------------------------------------------------------------------

    /// <summary>The banner's height at <paramref name="width"/>: the dragged height, else the art's shape between 340 and 500.</summary>
    public double HeroHeight(double width)
    {
        var dragged = _settings().SpotlightHeroHeight;
        if (dragged > 0) return Math.Clamp(dragged, HeroDragMin, HeroDragMax);
        if (!double.IsFinite(width) || width <= 0) return HeroMinHeight;
        return Math.Clamp(Math.Round(width / HeroArtAspect), HeroMinHeight, HeroMaxHeight);
    }

    /// <summary>The height a drag of the banner's bottom edge gives (not saved until the drag ends).</summary>
    public static double DraggedHeroHeight(double start, double delta)
        => Math.Round(Math.Clamp(start + (double.IsFinite(delta) ? delta : 0), HeroDragMin, HeroDragMax));

    /// <summary>Saves a dragged banner height (0 = automatic). The refusal, or null.</summary>
    public string? SetHeroHeight(double height)
    {
        var value = height > 0 ? DraggedHeroHeight(height, 0) : 0;
        return Save(s => s.SpotlightHeroHeight = value, _settings().SpotlightHeroHeight == value);
    }

    // ---- covers ----------------------------------------------------------------------------------------------------

    public double CoverScale => Math.Clamp(_settings().SpotlightCoverScale is > 0 and var s ? s : 1, CoverScaleMin, CoverScaleMax);
    public double TileWidth => Math.Round(BaseTileWidth * CoverScale);
    public double CoverHeight => Math.Round(BaseCoverHeight * CoverScale);

    /// <summary>The cover size a drag of the strip's page-side edge gives: into the page makes the covers bigger.</summary>
    public double CoverScaleFromDrag(double startHeight, double delta)
    {
        var height = startHeight + (ShelfAtBottom ? -delta : delta);
        return Math.Round(Math.Clamp(height / BaseCoverHeight, CoverScaleMin, CoverScaleMax), 3);
    }

    /// <summary>Saves a cover size (1 = 120 × 180). The refusal, or null.</summary>
    public string? SetCoverScale(double scale)
    {
        var value = Math.Round(Math.Clamp(double.IsFinite(scale) && scale > 0 ? scale : 1, CoverScaleMin, CoverScaleMax), 3);
        return Save(s => s.SpotlightCoverScale = value, Math.Abs(_settings().SpotlightCoverScale - value) < 0.0005);
    }

    /// <summary>
    /// The strip's side padding: the covers centre when they all fit (whatever the filter or collection); a longer row
    /// starts at the left with the normal 20 px.
    /// </summary>
    public static double ShelfSidePadding(int count, double tileWidth, double stripWidth)
    {
        if (count <= 0 || stripWidth <= 0) return ShelfMinSide;
        var items = count * (tileWidth + TileGap);
        return items + 2 * ShelfMinSide <= stripWidth ? Math.Floor((stripWidth - items) / 2) : ShelfMinSide;
    }

    // ---- the strip's ⚙ (Covers at top / bottom, No background) --------------------------------------------------------

    public bool ShelfAtBottom => _settings().SpotlightShelfAtBottom;
    public bool ShelfPlain => _settings().SpotlightShelfPlain;

    public string? SetShelfAtBottom(bool bottom) => Save(s => s.SpotlightShelfAtBottom = bottom, ShelfAtBottom == bottom);

    public string? SetShelfPlain(bool plain) => Save(s => s.SpotlightShelfPlain = plain, ShelfPlain == plain);

    private string? Save(Action<LinuxAppSettings> change, bool unchanged)
    {
        if (unchanged) return null;
        if (_save(change) is { } refused) return refused;
        change(_settings());
        Changed?.Invoke();
        return null;
    }

    // ---- What's installed ------------------------------------------------------------------------------------------

    /// <summary>
    /// The "What's installed" tiles, in the Components section's order: one per listed component, except that ReLimiter and
    /// Display Commander share one frame limiter tile (the installed one, else "Choose ReLimiter or Display Commander").
    /// </summary>
    public static IReadOnlyList<SpotlightStatusTile> StatusTiles(IEnumerable<ComponentRowViewModel> rows)
    {
        var list = rows.ToList();
        var tiles = new List<SpotlightStatusTile>();
        var limiters = list.Where(r => r.Kind is ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander).ToList();
        var limiterDone = false;
        foreach (var row in list)
        {
            if (row.Kind is ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander)
            {
                if (limiterDone) continue;
                limiterDone = true;
                var installed = limiters.FirstOrDefault(r => IsInstalled(r.Availability));
                if (installed is null && limiters.Count > 1)
                {
                    tiles.Add(new SpotlightStatusTile(T("Spot.FrameLimiter"),
                        UiLocalizationService.Format("Spot.ChooseOne", string.Join(T("Spot.Or"), limiters.Select(r => r.Name))), StatusTone.None));
                    continue;
                }
                tiles.Add(Tile(installed ?? row));
                continue;
            }
            tiles.Add(Tile(row));
        }
        return tiles;
    }

    private static bool IsInstalled(ComponentAvailability availability)
        => availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;

    /// <summary>One component's tile (upstream StatusTile.Update): not installed, its status, or the update.</summary>
    public static SpotlightStatusTile Tile(ComponentRowViewModel row) => row.Availability switch
    {
        ComponentAvailability.Installed => new(row.Name, row.Status, StatusTone.Installed),
        ComponentAvailability.UpdateAvailable => new(row.Name,
            string.IsNullOrWhiteSpace(row.InstalledVersion) ? T("Spot.UpdateAvailable") : UiLocalizationService.Format("Spot.UpdateFrom", row.InstalledVersion),
            StatusTone.Update),
        ComponentAvailability.Unsupported => new(row.Name, row.Status, StatusTone.Unsupported),
        _ => new(row.Name, T("Spot.NotInstalled"), StatusTone.None),
    };

    // ---- the side panel --------------------------------------------------------------------------------------------

    /// <summary>The side panel's title for a key: a section's name, "Components" for the component cards, or a built panel's.</summary>
    public static string PanelTitle(string key) => key switch
    {
        GamePageSections.Overview => GamePageSections.Title(GamePageSections.Components),
        ScreenshotsKey => T("Spot.Screenshots"),
        AchievementsKey => T("Spot.Achievements"),
        GameSettingsKey => T("Spot.GameSettings"),
        ArtworkPickerKey => T("Spot.Artwork"),
        _ => GamePageSections.Title(key),
    };

    /// <summary>The "Tune this game" tiles: one per section after Overview, in the Tabs strip's order (upstream).</summary>
    public static IReadOnlyList<string> TuneSections => GamePageSections.GlanceSections;
}
