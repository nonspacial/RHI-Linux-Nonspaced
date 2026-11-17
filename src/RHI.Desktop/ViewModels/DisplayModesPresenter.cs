using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #311 (upstream 2.9.3 <c>MainViewModel.DisplayModes.cs</c>, 2.9.4 end state): display modes for the UI — switch one
/// game or every game, a game's Custom mode, the modes themselves (Settings › Display &amp; HDR › Display modes), and the
/// Keep step that installs, updates and the Settings "write to all games" buttons run for a game in a mode. Data in
/// <c>display-modes.json</c> (<see cref="DisplayModeFileStore"/>); game files through the journal
/// (<see cref="DisplayModeGameFiles"/>); the launch settings (HDR mode, resolution switch, FPS limit, NVIDIA settings) in
/// the library file, saved like the page's own controls. Switching does nothing while Settings has the feature off.
/// UI thread only (the file work awaits off it).
/// </summary>
public sealed partial class DisplayModesPresenter : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly DisplayModeFileStore _file;
    private readonly bool _unreadable;
    private bool _switching;

    internal DisplayModesPresenter(MainWindowViewModel shell, LinuxManagedComponentService managed, LinuxDeploymentRecordStore records, string configRoot)
    {
        _shell = shell;
        _file = new DisplayModeFileStore(Path.Combine(configRoot, "display-modes.json"));
        Store.Load(_file.Load(out _unreadable));
        GameState = new DisplayModeGameState(Store, _file);
        var files = managed.DisplayModeFiles ?? new DisplayModeGameFiles(records, null, managed.IniWriteRefusal);
        Service = new DisplayModeService(Store, new DisplayModeIo(files)) { GlobalValue = GlobalValue };
        managed.DisplayModeKeeper = KeepForInstall;
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame)) RaiseSelected();
        };
    }

    public DisplayModeStore Store { get; } = new();

    /// <summary>w14 final review M2: display-modes.json as a per-game store that follows a moved or re-keyed game.</summary>
    public DisplayModeGameState GameState { get; }

    /// <summary>After a committed folder move or Repair after move: the session follows the file (the game's new key).</summary>
    internal void FollowCommittedMove()
    {
        GameState.ReloadAfterCommit();
        RaiseChanged();
    }

    /// <summary>
    /// A scan (w14 final review M2): state saved under keys <paramref name="game"/> had before (older builds, merged
    /// duplicates; <see cref="LinuxGameLibraryState.AdoptOlderKeys"/>) moves to its key now. True when anything moved.
    /// </summary>
    internal bool AdoptOlderKeys(DetectedGame game, LinuxGameLibraryState library)
        => GameState.AdoptOlderKeys(Key(game), library.OlderKeysFor(game, Store.GameKeys));

    /// <summary>Saves what a scan adopted; the localised refusal, or null.</summary>
    internal string? SaveAdopted()
    {
        var refused = Save();
        RaiseChanged();
        return refused;
    }
    public DisplayModeService Service { get; }
    public string FilePath => _file.FilePath;

    /// <summary>Raised after modes are added / renamed / deleted / reordered, a value changes or a game is switched.</summary>
    public event Action? Changed;

    /// <summary>Settings › Display &amp; HDR › Display modes › Show on game pages (upstream <c>DisplayModesEnabled</c>).</summary>
    public bool Enabled
    {
        get => Store.Enabled;
        set
        {
            if (Store.Enabled == value) return;
            Store.Enabled = value;
            Save();
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    // ── The selected game (game page and game menu) ───────────────────────────

    /// <summary>The switch shows on the selected game's page: the feature is on and a game is selected.</summary>
    public bool ShowSwitch => Enabled && _shell.SelectedGame is not null;

    /// <summary>The selected game's switch: one entry per mode it can use (<see cref="ModeButton.Active"/> = its mode).</summary>
    public IReadOnlyList<ModeButton> SelectedButtons => _shell.SelectedGame is { } game ? Buttons(game) : [];

    /// <summary>"Display mode: TV" for the selected game (the label beside the switch).</summary>
    public string SelectedModeName => _shell.SelectedGame is { } game ? ActiveModeName(game) : "";

    /// <summary>One mode of a game's switch: clicking the active one turns the mode off (upstream).</summary>
    public sealed record ModeButton(string Id, string Name, bool Active, string Tip)
    {
        /// <summary>The mode to switch to on a click: null (Off) for the active one.</summary>
        public string? Target => Active ? null : Id;
    }

    public IReadOnlyList<ModeButton> Buttons(DetectedGame game)
    {
        var key = Key(game);
        var active = Store.ActiveModeId(key);
        var swapped = Store.ItemsFor(key);
        return Store.ModesFor(key).Select(m =>
        {
            var on = m.Id.Equals(active, StringComparison.OrdinalIgnoreCase);
            var name = DisplayModeText.ModeName(m.Id, m.Name);
            return new ModeButton(m.Id, name, on, DisplayModeText.ModeTip(swapped, name, on));
        }).ToList();
    }

    public static string Key(DetectedGame game) => LinuxGameLibraryStore.GameKey(game);

    /// <summary>Opens the game's display mode dialog (the ⚙ beside the switch; set by the window).</summary>
    public Func<DetectedGame, Task>? OpenGameSettings { get; set; }

    /// <summary>
    /// The game menu's "Display mode" submenu (upstream <c>BuildDisplayModeMenu</c>, under Add to collection): Off, the
    /// modes (✓ = the game's), then Display mode settings…. Null while display modes are off.
    /// </summary>
    public CardMenuItem? MenuFor(DetectedGame game)
    {
        if (!Enabled) return null;
        var active = ActiveModeId(game);
        string Mark(bool on, string label) => on ? "✓ " + label : label;
        var children = new List<CardMenuItem>
        {
            new("display-mode-off", Mark(active is null, T("Dm.MenuOff")), new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => SwitchFromUiAsync(game, null)))
        };
        foreach (var m in Store.ModesFor(Key(game)))
        {
            var id = m.Id;
            children.Add(new("display-mode-" + id, Mark(id.Equals(active, StringComparison.OrdinalIgnoreCase), DisplayModeText.ModeName(id, m.Name)),
                new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => SwitchFromUiAsync(game, id))));
        }
        if (OpenGameSettings is { } open)
            children.Add(new("display-mode-settings", T("Dm.MenuSettings"), new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => open(game)), SeparatorBefore: true));
        return new CardMenuItem("display-mode", T("Dm.Label"), CardMenuItem.OpensSubmenu, Children: children);
    }

    /// <summary>A switch from the page, the game menu or the ⚙: the result goes to the status line.</summary>
    public async Task SwitchFromUiAsync(DetectedGame game, string? modeId) => _shell.StatusText = await SwitchAsync(game, modeId);

    public string? ActiveModeId(DetectedGame game) => Store.ActiveModeId(Key(game));

    public string ActiveModeName(DetectedGame game)
    {
        var id = ActiveModeId(game);
        return DisplayModeText.ModeName(id, Store.NameOf(id));
    }

    // ── Switching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// What the items need to know about <paramref name="game"/>: which launch settings its page offers, and a copy of
    /// them (UI thread).
    /// </summary>
    internal DisplayModeContext Context(DetectedGame game)
    {
        var host = _shell.Runtime.NvidiaHost;
        var selected = ReferenceEquals(game, _shell.SelectedGame);
        IPanelHost panel = _shell;
        return new DisplayModeContext
        {
            Game = game,
            Key = Key(game),
            NvidiaOffered = NvidiaLaunchSettings.Plan(game, null, host, null).Shown,
            FpsLimitOffered = FrameRateLimit.Plan(game, null).Shown,
            // #251: the selected game's page knows whether it can use HDR; for other games RHI has not looked yet.
            HdrOffered = !selected || _shell.Runtime.ShowHdrSection,
            ResolutionOffered = DisplayModeSwitcher.CanEverApply(game, host),
            // w14/layouts-c: launch arguments only where RHI adds them (a command it built, Steam's -applaunch).
            LaunchArgsOffered = _shell.LaunchArguments.RouteFor(game) is RHI.Platform.Linux.Launch.LaunchArgumentsRoute.Direct
                or RHI.Platform.Linux.Launch.LaunchArgumentsRoute.Steam
                || _shell.LaunchArguments.InLauncher(game), // w14/launcher-options (#318): the launcher's copy follows the switch
            Launch = DisplayModeLaunchValues.From(panel.LibraryState, Key(game)),
        };
    }

    /// <summary>Why the game cannot be switched now, or null.</summary>
    internal string? Refusal(DetectedGame game)
    {
        if (!Enabled) return T("Dm.TurnOnFirst");
        // w14 final review m9: a switch records values that could not be saved over the unreadable file.
        if (UnreadableRefusal() is { } unreadable) return unreadable;
        if (_shell.Updates.UpdateAll.IsRunning(game)) return TF("Dm.CloseGameFirst", game.Name);
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) && string.IsNullOrWhiteSpace(game.InstallPath)) return T("Dm.NoFolder");
        IPanelHost panel = _shell;
        if (panel.LibraryWritesPaused()) return _shell.StatusText;
        return null;
    }

    // m9: display-modes.json could not be read (it was set aside): nothing a switch records could be saved.
    private string? UnreadableRefusal()
        => _unreadable && File.Exists(_file.FilePath) ? TF("Dm.UnreadableNoSwitch", _file.FilePath) : null;

    /// <summary>
    /// Switches the game, or with a null <paramref name="modeId"/> turns its mode off. Returns the status line (the
    /// summary, or why it did not switch). w14 final review m10: behind the operation gate, so no install, update or
    /// other operation writes the game's files at the same time.
    /// </summary>
    public async Task<string> SwitchAsync(DetectedGame game, string? modeId, CancellationToken cancellationToken = default)
        => (await SwitchWithResultAsync(game, modeId, cancellationToken)).Line;

    private async Task<(string Line, DisplayModeService.SwitchResult? Result)> SwitchWithResultAsync(DetectedGame game, string? modeId, CancellationToken cancellationToken = default)
    {
        if (Refusal(game) is { } refused) return (refused, null);
        if (_switching) return (T("Dm.Busy"), null);
        IPanelHost panel = _shell;
        using var op = panel.BeginOperation("display-mode", cancellable: false);
        if (op is null) return (T("Vm.AnotherRHIOperationAlreadyRunning"), null);
        _switching = true;
        try
        {
            panel.AdoptRestoredLibraryFile(reloadHdrControls: true);
            var ctx = Context(game);
            var result = modeId is null
                ? await Service.TurnOffAsync(ctx, cancellationToken)
                : await Service.SwitchAsync(ctx, modeId, cancellationToken);
            var notSaved = Finish(ctx);
            var line = DisplayModeText.Summary(result);
            return (notSaved is null ? line : line + " " + notSaved, result);
        }
        finally { _switching = false; }
    }

    /// <summary>
    /// Switches every game that is not hidden or running (upstream: and has a folder); null = turns off every game in a
    /// mode. (switched, running and skipped).
    /// </summary>
    public async Task<(int Switched, int Running, int NotDone)> SwitchAllAsync(string? modeId, IProgress<(string Line, double Percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled || _switching || UnreadableRefusal() is not null) return (0, 0, 0);
        IPanelHost panel = _shell;
        using var op = panel.BeginOperation("display-mode", cancellable: false); // m10
        if (op is null) return (0, 0, 0);
        _switching = true;
        try
        {
            if (panel.LibraryWritesPaused()) return (0, 0, 0);
            panel.AdoptRestoredLibraryFile(reloadHdrControls: true);
            var games = panel.UnattendedGames.Where(g => !string.IsNullOrWhiteSpace(g.BinaryDirectory ?? g.InstallPath)
                                                          && (modeId is not null || Store.IsManaged(Key(g)) || Store.HasRestorePending(Key(g)))).ToList();
            var running = games.Where(g => _shell.Updates.UpdateAll.IsRunning(g)).ToList();
            var targets = games.Except(running).ToList();
            var modeName = modeId is null ? T("Dm.Off") : DisplayModeText.ModeName(modeId, Store.NameOf(modeId));
            var done = 0;
            var launchChanged = new List<DetectedGame>();
            var notDone = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var game = targets[i];
                if (Store.ItemsFor(Key(game)).Count == 0 && modeId is not null && Store.ActiveModeId(Key(game)) == modeId) continue;
                progress?.Report((modeId is null ? TF("Dm.TurningOff", game.Name) : TF("Dm.SwitchingGame", modeName, game.Name), 100.0 * i / Math.Max(1, targets.Count)));
                var ctx = Context(game);
                try
                {
                    var result = modeId is null ? await Service.TurnOffAsync(ctx, cancellationToken) : await Service.SwitchAsync(ctx, modeId, cancellationToken);
                    if (ctx.Launch is { Changed: true } launch)
                    {
                        launch.ApplyTo(panel.LibraryState, ctx.Key);
                        launchChanged.Add(game); // w14/launcher-options (#318)
                    }
                    // M3/m7: a game still in its mode (Off could not put everything back) or not switched is not counted.
                    if (result.Error is null && !result.NotRestored.Any()) done++;
                    else notDone++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    notDone++;
                    RHI.Core.Services.AppLog.Warn("display-modes", $"Could not switch {game.Name}", ex);
                }
            }
            if (panel.PersistLibraryState("Ui.WhatHdrProfile") is null)
                foreach (var game in launchChanged) _shell.LaunchArguments.SyncLauncher(game, keepLink: true); // w14/launcher-options (#318)
            Save();
            ReloadSelected();
            RaiseChanged();
            return (done, running.Count, notDone);
        }
        finally { _switching = false; }
    }

    /// <summary>After a switch: the launch settings it changed go back into the library file; the page reloads.</summary>
    private string? Finish(DisplayModeContext ctx)
    {
        IPanelHost panel = _shell;
        string? refused = null;
        if (ctx.Launch is { Changed: true } launch)
        {
            launch.ApplyTo(panel.LibraryState, ctx.Key);
            refused = panel.PersistLibraryState("Ui.WhatHdrProfile");
            if (refused is null) _shell.LaunchArguments.SyncLauncher(ctx.Game, keepLink: true); // w14/launcher-options (#318)
        }
        refused ??= Save();
        if (ReferenceEquals(ctx.Game, _shell.SelectedGame)) ReloadSelected();
        RaiseChanged();
        return refused;
    }

    private void ReloadSelected()
    {
        if (_shell.SelectedGame is not { } game) return;
        _shell.Runtime.LoadHdrProfile(game);
        _shell.FrameRateLimit.Load();
        _shell.LaunchArguments.Load(); // w14/layouts-c
    }

    // ── A game's Custom mode ──────────────────────────────────────────────────

    public void CreateCustom(DetectedGame game)
    {
        Store.CreateCustom(Key(game));
        Save();
        RaiseChanged();
    }

    /// <summary>
    /// Deletes the game's Custom mode; when the game is in it, its mode is turned off first (normal values written). A
    /// game that could not be turned off keeps its values from before modes as a pending restore (w14 final review M3).
    /// </summary>
    public async Task<string?> DeleteCustomAsync(DetectedGame game)
    {
        string? line = null;
        if (DisplayModeStore.IsCustom(Store.ActiveModeId(Key(game))))
            line = Refusal(game) is null ? await SwitchAsync(game, null) : null;
        Store.DeleteCustom(Key(game));
        Save();
        RaiseChanged();
        return line;
    }

    /// <summary>
    /// Ticks or unticks a setting in the game's Custom mode, or sets its value. While the game is in Custom the result
    /// is written now (unticked = the game's normal value). Returns a problem, or null.
    /// </summary>
    public async Task<string?> SetCustomItemAsync(DetectedGame game, DisplayModeItem item, bool included, string? value)
    {
        var key = Key(game);
        Store.SetInCustom(key, item.Key, included);
        Store.SetCustomValue(key, item.Key, value);
        Save();
        RaiseChanged();
        if (!DisplayModeStore.IsCustom(Store.ActiveModeId(key))) return null;
        if (Refusal(game) is { } refused) return T("Dm.SavedNotWritten") + " " + refused;
        using var op = ((IPanelHost)_shell).BeginOperation("display-mode", cancellable: false); // m10
        if (op is null) return T("Dm.SavedNotWritten") + " " + T("Vm.AnotherRHIOperationAlreadyRunning");
        var ctx = Context(game);
        var problem = Service.ValueFor(ctx, DisplayModeStore.CustomId, item) is { } v ? await Service.ApplyNowAsync(ctx, item, v) : null;
        var notSaved = Finish(ctx);
        return problem is null ? notSaved : UiLocalizationService.Present(problem);
    }

    /// <summary>The game's current values (the per-game dialog's "now …"), read off the UI thread.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> ReadCurrentAsync(DetectedGame game)
    {
        var ctx = Context(game);
        return await Task.Run(() => Service.ReadCurrent(ctx, DisplayModeItems.All));
    }

    /// <summary>Why an item cannot be used for the game now (the per-game dialog greys it), or null.</summary>
    public string? UnavailableReason(DetectedGame game, DisplayModeItem item)
        => Service.Unavailable(Context(game), item) is var reason && reason != DisplayModeUnavailable.None ? DisplayModeText.Reason(reason) : null;

    // ── Modes (Settings card) ─────────────────────────────────────────────────

    public string? AddMode(string name)
    {
        var id = Store.AddMode(name);
        if (id is not null) { Save(); RaiseChanged(); }
        return id;
    }

    public bool RenameMode(string id, string name)
    {
        var ok = Store.RenameMode(id, name);
        if (ok) { Save(); RaiseChanged(); }
        return ok;
    }

    public bool MoveMode(string id, int delta)
    {
        var ok = Store.MoveMode(id, delta);
        if (ok) { Save(); RaiseChanged(); }
        return ok;
    }

    /// <summary>
    /// Deletes a mode. Its games that can be switched are turned off first. The rest (running, not found, or a value that
    /// could not be written back) leave the mode with their values from before modes kept as a pending restore, which Off
    /// writes back later (w14 final review M3). (deleted, turned off, pending).
    /// </summary>
    public async Task<(bool Deleted, int TurnedOff, int Pending)> DeleteModeAsync(string id)
    {
        if (Store.Modes.Count <= 1) return (false, 0, 0);
        var inMode = Store.GamesIn(id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        if (Enabled)
            foreach (var game in ((IPanelHost)_shell).AllGames.Where(g => inMode.Contains(Key(g))).ToList())
                if (Refusal(game) is null && (await SwitchWithResultAsync(game, null)).Result is { TurnedOff: true })
                    done++;
        var ok = Store.DeleteMode(id);
        var pending = ok ? inMode.Count(Store.HasRestorePending) : 0;
        Save();
        RaiseChanged();
        return (ok, done, pending);
    }

    /// <summary>Sets (or with null clears) a mode's value for an item: every game switched to the mode gets it.</summary>
    public void SetModeValue(string modeId, string item, string? value)
    {
        Store.SetModeDefault(modeId, item, value);
        Save();
        RaiseChanged();
    }

    // ── Global values (what Off goes back to) ─────────────────────────────────

    /// <summary>
    /// The global setting a game goes back to when its mode is turned off (or a mode has no value for it), in the item's
    /// value format; null when there is none (the game's value from before modes is used). Follows the Settings "write
    /// to all games" rules (upstream <c>GlobalDisplayModeValue</c>).
    /// </summary>
    internal string? GlobalValue(DisplayModeContext ctx, string itemKey)
    {
        var s = ((IPanelHost)_shell).Settings;
        static string? NotEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
        return itemKey switch
        {
            DisplayModeItemKeys.RdxPeakNits => s.PeakNitsEnabled && s.PeakNits > 0 ? s.PeakNits.ToString(CultureInfo.InvariantCulture) : null,
            // RDR2 / Max Payne 3 keep their own overlay key (the global key is not written to them)
            DisplayModeItemKeys.RsOverlayKey => UserIniTemplates.IsRdr2Template(ctx.GameName) ? null : NotEmpty(s.OverlayHotkey),
            DisplayModeItemKeys.RsScreenshotKey => NotEmpty(s.ScreenshotHotkey),
            DisplayModeItemKeys.UlTargetFps => Math.Max(0, s.ReLimiterTargetFps).ToString(CultureInfo.InvariantCulture),
            DisplayModeItemKeys.UlOsdKey => NotEmpty(s.ReLimiterOsdHotkey),
            DisplayModeItemKeys.DriverFpsCap => DisplayModeItems.FpsGlobal, // the game's own FPS limit goes: Global again
            _ => null,
        };
    }

    // ── Keep (installs, updates, "write to all games") ────────────────────────

    /// <summary>
    /// For a write of global values into <paramref name="game"/> (the Settings "write to all games" buttons): when the game
    /// is in a mode, reads its values for <paramref name="itemKeys"/> now and returns the step that puts them back.
    /// </summary>
    public Func<CancellationToken, Task<int>>? Keep(DetectedGame game, IEnumerable<string> itemKeys)
        => Enabled ? Service.Keep(FileContext(game), itemKeys) : null;

    /// <summary>
    /// Runs <paramref name="write"/> (a "write to all games" over <paramref name="games"/>), then puts back the display
    /// mode values of the games in a mode (upstream: global writes keep a game's display-mode values while it is in a mode).
    /// </summary>
    public async Task<T> KeepingAsync<T>(IEnumerable<DetectedGame> games, IReadOnlyList<string> itemKeys, Func<Task<T>> write)
    {
        var keeps = games.Select(g => Keep(g, itemKeys)).OfType<Func<CancellationToken, Task<int>>>().ToList();
        var result = await write();
        foreach (var keep in keeps)
        {
            try { await keep(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                RHI.Core.Services.AppLog.Warn("display-modes", "Could not put back a game's display mode values", ex);
            }
        }
        return result;
    }

    /// <summary>The managed service's hook (any thread): an install or update of a component that writes these files.</summary>
    private Func<CancellationToken, Task<int>>? KeepForInstall(DetectedGame game, ManagedComponentKind component)
    {
        if (!Enabled) return null;
        IReadOnlyList<string> items = component switch
        {
            ManagedComponentKind.ReShade or ManagedComponentKind.RenoDX => DisplayModeItemKeys.ReShadeIniItems,
            ManagedComponentKind.ReLimiter => DisplayModeItemKeys.ReLimiterItems,
            ManagedComponentKind.DisplayCommander => [DisplayModeItemKeys.DcFpsLimit],
            _ => []
        };
        // An install brings its dependencies (RenoDX installs ReShade; ReLimiter needs ReShade): ReShade.ini items always.
        if (component is ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander) items = [.. items, .. DisplayModeItemKeys.ReShadeIniItems];
        return items.Count == 0 ? null : Service.Keep(FileContext(game), items);
    }

    /// <summary>A context for game-file items only (no launch settings: Keep never writes those).</summary>
    private static DisplayModeContext FileContext(DetectedGame game) => new() { Game = game, Key = Key(game) };

    // ── Saving ────────────────────────────────────────────────────────────────

    /// <summary>Saves display-modes.json; the localised refusal, or null when saved.</summary>
    private string? Save()
    {
        if (_unreadable && File.Exists(_file.FilePath)) return TF("Dm.FileUnreadable", _file.FilePath);
        try
        {
            _file.Save(Store.Snapshot());
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonStoreUnavailableException)
        {
            RHI.Core.Services.AppLog.Warn("display-modes", "Could not save display-modes.json", ex);
            return TF("Dm.NotSaved", UiLocalizationService.Present(ex.Message));
        }
    }

    private void RaiseSelected()
    {
        OnPropertyChanged(nameof(ShowSwitch));
        OnPropertyChanged(nameof(SelectedButtons));
        OnPropertyChanged(nameof(SelectedModeName));
    }

    private void RaiseChanged()
    {
        RaiseSelected();
        Changed?.Invoke();
    }

    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);
}
