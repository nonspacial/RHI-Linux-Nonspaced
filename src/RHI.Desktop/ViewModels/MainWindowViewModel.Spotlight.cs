using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Artwork;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

// w14/layouts-b (#305): upstream 2.9.0–2.9.4's Spotlight layout. Its sizes and places (SpotlightPresenter), the artwork
// services (Steam art, the user's own cover and banner, SteamGridDB), the game menu's Artwork submenu, the Screenshots
// panel's listing and the Trash it deletes to. Artwork is only asked for while Spotlight shows (the view asks).
public partial class MainWindowViewModel
{
    private SpotlightPresenter? _spotlight;
    private PcgwCentralDataService? _spotlightPcgw;

    /// <summary>The settings in use, for Settings' save to keep Spotlight's fields as they are.</summary>
    internal LinuxAppSettings SpotlightSettings => _settings;

    /// <summary>#305: Spotlight's banner and cover sizes, the strip's place and background, the side panel's titles.</summary>
    public SpotlightPresenter Spotlight => _spotlight ??= new SpotlightPresenter(() => _settings, SaveLayoutSettings);

    /// <summary>Steam artwork and the user's own art; null in a build without a download client (the tiles stay plain).</summary>
    public SteamArtworkService? Artwork { get; private set; }

    /// <summary>SteamGridDB's covers and banners (Spotlight › Artwork); null without a download client.</summary>
    public SteamGridDbService? SteamGridDb { get; private set; }

    private SteamGridDbKeyPresenter? _steamGridDbKey;

    /// <summary>Settings › Accounts &amp; Data › SteamGridDB API key; null without the service.</summary>
    public SteamGridDbKeyPresenter? SteamGridDbKey => SteamGridDb is { } sgdb ? _steamGridDbKey ??= new SteamGridDbKeyPresenter(sgdb) : null;

    /// <summary>The desktop's Trash (Screenshots › Delete).</summary>
    public FreedesktopTrash Trash { get; set; } = new();

    /// <summary>The window's file picker and side panel for the game menu's Artwork entries (set by the window).</summary>
    public Func<DetectedGame, ArtworkKind, Task>? PickCustomArt { get; set; }
    public Action<DetectedGame, ArtworkKind>? BrowseArt { get; set; }

    /// <summary>Raised after a game's custom cover or banner changed (the window reloads that art).</summary>
    public event Action<DetectedGame, ArtworkKind>? ArtworkChanged;

    /// <summary>Wires the artwork services (AppComposition). Without them Spotlight shows names and a plain banner.</summary>
    public void UseSpotlightServices(HttpClient? http, PcgwCentralDataService? pcgw, SteamArtworkService? artwork = null, SteamGridDbService? steamGridDb = null,
        RHI.Platform.Linux.Achievements.SteamAchievementService? achievements = null)
    {
        _spotlightPcgw = pcgw;
        Artwork = artwork ?? (http is null ? null : new SteamArtworkService(http));
        SteamGridDb = steamGridDb ?? (http is null ? null : new SteamGridDbService(http, new SecretFileStore(SteamGridDbService.DefaultKeyPath, "steamgriddb")));
        _achievements = new AchievementsPresenter(() => _settings, SaveLayoutSettings, achievements ?? new RHI.Platform.Linux.Achievements.SteamAchievementService(http: http));
    }

    private AchievementsPresenter? _achievements;

    /// <summary>#307: Settings › Achievements and Spotlight's achievements (Steam's local files).</summary>
    public AchievementsPresenter Achievements => _achievements ??= new AchievementsPresenter(() => _settings, SaveLayoutSettings, null);

    /// <summary>Starts reading PCGamingWiki's data, which names the Steam AppID of games from other stores (Spotlight only).</summary>
    public void PrefetchArtworkNames() => _spotlightPcgw?.Prefetch();

    /// <summary>The Steam AppID whose artwork a game uses, or null.</summary>
    public int? ArtworkAppId(DetectedGame game) => SteamArtworkService.ResolveAppId(game, _spotlightPcgw?.Current);

    /// <summary>The key a game's own art is saved under (its library key, so it survives a rename).</summary>
    public static string ArtKey(DetectedGame game) => LinuxGameLibraryStore.GameKey(game);

    /// <summary>The game menu's Artwork submenu (Spotlight only, upstream): set, browse, or go back to Steam's art.</summary>
    internal CardMenuItem? ArtworkMenuFor(DetectedGame game)
    {
        if (Layout.Layout != MainLayout.Spotlight) return null;
        var key = ArtKey(game);
        var children = new List<CardMenuItem>
        {
            new("art-set-cover", T("Art.SetCover"), new AsyncRelayCommand(() => PickCustomArt?.Invoke(game, ArtworkKind.Cover) ?? Task.CompletedTask)),
            new("art-set-banner", T("Art.SetBanner"), new AsyncRelayCommand(() => PickCustomArt?.Invoke(game, ArtworkKind.Hero) ?? Task.CompletedTask)),
            new("art-browse-covers", T("Art.BrowseCovers"), new RelayCommand(() => BrowseArt?.Invoke(game, ArtworkKind.Cover)), SeparatorBefore: true),
            new("art-browse-banners", T("Art.BrowseBanners"), new RelayCommand(() => BrowseArt?.Invoke(game, ArtworkKind.Hero))),
        };
        var customCover = Artwork?.TryGetCustom(key, ArtworkKind.Cover) is not null;
        var customHero = Artwork?.TryGetCustom(key, ArtworkKind.Hero) is not null;
        if (customCover) children.Add(new("art-steam-cover", T("Art.UseSteamCover"), new RelayCommand(() => ResetCustomArt(game, ArtworkKind.Cover)), SeparatorBefore: true));
        if (customHero) children.Add(new("art-steam-banner", T("Art.UseSteamBanner"), new RelayCommand(() => ResetCustomArt(game, ArtworkKind.Hero)), SeparatorBefore: !customCover));
        return new CardMenuItem("artwork", T("Art.Menu"), CardMenuItem.OpensSubmenu, Children: children);
    }

    /// <summary>Copies a picked image in as the game's cover or banner; the status line says what happened.</summary>
    public bool SetCustomArt(DetectedGame game, ArtworkKind kind, string file)
    {
        if (Artwork is null) return false;
        if (Artwork.SetCustom(ArtKey(game), kind, file, out var error) is null)
        {
            StatusText = UiLocalizationService.Format("Art.NotSaved", game.DisplayName(), UiLocalizationService.Present(error ?? ""));
            return false;
        }
        ArtworkChanged?.Invoke(game, kind);
        return true;
    }

    /// <summary>Use Steam cover / banner: the game's own art is removed, Steam's (or the plain tile) shows again.</summary>
    public void ResetCustomArt(DetectedGame game, ArtworkKind kind)
    {
        Artwork?.ClearCustom(ArtKey(game), kind);
        ArtworkChanged?.Invoke(game, kind);
    }

    /// <summary>The selected game's screenshots (Screenshots panel): the folder and the files, newest first.</summary>
    public ScreenshotListing FindScreenshots(DetectedGame game)
        => ScreenshotLibrary.Find(game, ReShadeGlobalSettingsApplier.ResolveScreenshotPath(_settings, game), _settings.PerGameScreenshotFolders);

    /// <summary>Screenshots › Set as banner: the screenshot becomes the game's custom banner (Use Steam banner undoes it).</summary>
    public void SetBannerFromScreenshot(DetectedGame game, string file)
    {
        if (SetCustomArt(game, ArtworkKind.Hero, file)) StatusText = UiLocalizationService.Format("Shots.BannerSet", game.DisplayName());
    }

    /// <summary>Screenshots › Delete: the file goes to the Trash (never deleted outright). True when it went.</summary>
    public bool TrashScreenshot(string file)
    {
        if (Trash.Trash(file, out var error) is not null)
        {
            StatusText = UiLocalizationService.Format("Shots.Trashed", Path.GetFileName(file));
            return true;
        }
        StatusText = UiLocalizationService.Present(error ?? FreedesktopTrash.NotTrashedPrefix);
        return false;
    }

    /// <summary>#308: Settings › General › Controller support (on by default), saved at once. The refusal, or null.</summary>
    public string? SetControllerSupport(bool on)
    {
        if (_settings.ControllerSupport == on) return null;
        if (SaveLayoutSettings(s => s.ControllerSupport = on) is { } refused) return refused;
        _settings.ControllerSupport = on;
        ControllerSettingChanged?.Invoke();
        return null;
    }

    /// <summary>#308: Controller support changed (the window starts or stops reading the controller).</summary>
    public event Action? ControllerSettingChanged;

    /// <summary>#308: a game started from RHI (its launch went ahead).</summary>
    public event Action<DetectedGame>? GameLaunched;

    /// <summary>#308: whether the game runs now (the running probe Update All uses).</summary>
    public bool IsGameRunning(DetectedGame game) => Updates.UpdateAll.IsRunning(game);

    /// <summary>Whether a game has an update RHI knows of (the cover's purple outline and Update tag).</summary>
    public bool GameHasUpdate(DetectedGame game) => TopBar.HasUpdate(LinuxGameLibraryStore.GameKey(game));
}
