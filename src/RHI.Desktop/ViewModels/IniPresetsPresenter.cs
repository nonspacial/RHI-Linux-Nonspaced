using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>The three "Apply to all games" buttons (review finding 5).</summary>
public enum IniApplyAll { ReShadeSettings, ReLimiterSettings, PeakNits }

/// <summary>
/// w5/ini-presets: the INI side of the shell (#181): "Deploy … .ini" in the ReShade / ReLimiter / Display Commander ⚙,
/// the INI templates folder, the mass deploy and "Apply to all games" rows in Settings. Every action holds the busy gate
/// (a batch is cancellable between games) and reports one line on the status bar; the logic lives in
/// <see cref="UserIniService"/>.
/// </summary>
public sealed class IniPresetsPresenter(MainWindowViewModel vm, LinuxManagedComponentService managed, LinuxDeploymentRecordStore records, string configRoot)
{
    /// <summary>The INIs folder (shared with the OptiScaler templates).</summary>
    public string TemplatesFolder => Path.Combine(configRoot, "inis");

    // The app's journaled service (AppComposition); tests and older wiring get an unjournaled one over the same folder.
    internal UserIniService Service => managed.UserIni ?? new(new UserIniTemplates(TemplatesFolder), records, managed.IniWriteRefusal);

    /// <summary>
    /// The components whose row gets the INI ⚙ while RHI manages them: a copy the user installed (shown as installed,
    /// not managed) is theirs, and the ⚙ is hidden there (review finding 1).
    /// </summary>
    public static bool HasIniSettings(ManagedComponent component)
        => component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable
           && component.ManagedByRhi
           && (component.Kind is ManagedComponentKind.ReShade or ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander
               // #189: Luma's ⚙ (HDR on first boot, TAA) is upstream's, for Unreal Engine games whose Luma RHI installed.
               || component.Kind == ManagedComponentKind.LumaFramework && component.UnrealEngine);

    /// <summary>The file "Deploy" writes for a component.</summary>
    public static string IniFileName(ManagedComponentKind kind)
        => kind == ManagedComponentKind.ReShade ? "ReShade.ini" : UserIniService.AddonIniName(kind) ?? "";

    /// <summary>Seeds every missing template; the localised error line, or null when the folder is ready.</summary>
    public string? PrepareTemplatesFolder()
    {
        try
        {
            new UserIniTemplates(TemplatesFolder).SeedAll();
            new OptiScalerIniTemplates(TemplatesFolder, () => new(OptiScalerGpuType.Nvidia, true)).SeedAll();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return UiLocalizationService.Present(ex.Message);
        }
    }

    /// <summary>The saved settings (a copy).</summary>
    public LinuxAppSettings SavedSettings() => vm.Settings.GetLinuxSettingsSnapshot().Settings;

    /// <summary>Saves settings through the Settings view model (the one save path); returns its status line.</summary>
    public string Save(LinuxAppSettings settings) => vm.Settings.SaveLinuxSettings(settings);

    /// <summary>How many library games a mass deploy of <paramref name="kind"/>'s INI would write (RHI manages the component).</summary>
    public int DeployCandidates(ManagedComponentKind kind) => Service.DeployTargets(vm.AllLibraryGames.ToList(), kind).Count;

    /// <summary>
    /// How many of those hold a relimiter.ini / DisplayCommander.ini that changed since RHI wrote it (the mass deploy's
    /// question names them: each is kept as a backup beside it; review finding 2).
    /// </summary>
    public int DeployChangedCount(ManagedComponentKind kind) => Service.ChangedAddonInis(vm.AllLibraryGames.ToList(), kind);

    /// <summary>The mass deploy's question: games it writes, and how many of them hold an add-on INI that changed since RHI wrote it.</summary>
    /// <param name="Error">Set (and on the status bar) when a game's file could not be read; nothing is asked then.</param>
    public sealed record DeployQuestion(int Count, int Changed, string? Error);

    /// <summary>
    /// <see cref="DeployCandidates"/> and <see cref="DeployChangedCount"/> for the mass deploy's question, read and hashed
    /// off the UI thread (every managed game's add-on INI is read). An IO error becomes a status line, never an
    /// unhandled exception (ini-presets re-review nit 2).
    /// </summary>
    public async Task<DeployQuestion> DeployQuestionAsync(ManagedComponentKind kind)
    {
        var games = vm.AllLibraryGames.ToList();
        var service = Service;
        try
        {
            return await Task.Run(() =>
            {
                var targets = service.DeployTargets(games, kind);
                return new DeployQuestion(targets.Count, targets.Count == 0 ? 0 : service.ChangedAddonInis(targets, kind), null);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeployQuestion(0, 0, Report(UiLocalizationService.Format("Ini.DeployFailed", IniFileName(kind), UiLocalizationService.Present(ex.Message))));
        }
    }

    /// <summary>
    /// Whether "Deploy X.ini" for the selected game must ask first: the game's relimiter.ini / DisplayCommander.ini changed
    /// since RHI wrote it, or RHI never wrote it (review finding 2). ReShade.ini keeps its other lines and never asks.
    /// </summary>
    public bool DeployNeedsConfirmation(ManagedComponentKind kind)
    {
        if (vm.SelectedGame is not { } game || UserIniService.AddonIniName(kind) is null) return false;
        try { return Service.InspectAddonIni(game, kind) == UserIniService.AddonIniState.Changed; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// "Deploy X.ini" for the selected game. <paramref name="replaceChanged"/>: the user confirmed replacing an add-on INI
    /// that changed since RHI wrote it (a backup is kept beside it); without it such a file is left as it is.
    /// </summary>
    public async Task<string> DeployForSelectedAsync(ManagedComponentKind kind, bool replaceChanged = false)
    {
        if (vm.SelectedGame is not { } game) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        using var op = vm.Operations.TryBegin("ini-deploy", cancellable: false);
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        var settings = vm.Settings.GetLinuxSettingsSnapshot().Settings;
        var service = Service;
        try
        {
            var result = await Task.Run(() => kind == ManagedComponentKind.ReShade
                ? Task.FromResult(service.DeployReShadeIni(game, settings, op.Token))
                : service.DeployAddonIniAsync(game, kind, replaceChanged, op.Token));
            return Report(UiLocalizationService.Present(result.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Report(UiLocalizationService.Format("Ini.DeployFailed", IniFileName(kind), UiLocalizationService.Present(ex.Message)));
        }
    }

    /// <summary>The backup a Deploy kept of the selected game's add-on INI (still there, unchanged), or null.</summary>
    public string? AddonIniBackup(ManagedComponentKind kind)
    {
        if (vm.SelectedGame is not { } game || UserIniService.AddonIniName(kind) is null) return null;
        try { return Service.AddonIniBackup(game, kind); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Puts the backup a Deploy kept back in place of the selected game's add-on INI.</summary>
    public async Task<string> RestoreAddonIniAsync(ManagedComponentKind kind)
    {
        if (vm.SelectedGame is not { } game) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        using var op = vm.Operations.TryBegin("ini-restore", cancellable: false);
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        var service = Service;
        try
        {
            var result = await Task.Run(() => service.RestoreAddonIniAsync(game, kind, op.Token));
            return Report(UiLocalizationService.Present(result.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Report(UiLocalizationService.Format("Ini.RestoreFailed", IniFileName(kind), UiLocalizationService.Present(ex.Message)));
        }
    }

    /// <summary>
    /// Mass INI deploy of <paramref name="kind"/>'s INI to every game where RHI manages it. <paramref name="replaceChanged"/>:
    /// the inline question (which named the changed files) was confirmed; each such file is kept as a backup.
    /// </summary>
    public Task<string> DeployToAllAsync(ManagedComponentKind kind, LinuxAppSettings settings, bool replaceChanged = false)
        => RunBatchAsync("ini-deploy-all", IniFileName(kind),
            (service, games, token) => service.DeployToAllAsync(games, kind, settings, replaceChanged, token));

    /// <summary>"Apply to all games": screenshot folder, hotkeys, effect list style into every ReShade.ini.</summary>
    public Task<string> ApplyReShadeSettingsToAllAsync(LinuxAppSettings settings)
        => RunBatchAsync("reshade-settings-all", UiLocalizationService.Text("Ini.ReShadeSettingsWhat"),
            (service, games, token) => service.ApplyReShadeSettingsToAllAsync(games, settings, token));

    /// <summary>
    /// ReLimiter "Apply to all games" (#188): the four globals into every relimiter.ini, then into the user's template so
    /// later installs inherit them (a template that cannot be written is said on the same line).
    /// </summary>
    public async Task<string> ApplyReLimiterSettingsToAllAsync(LinuxAppSettings settings)
    {
        var globals = ReLimiterGlobals.From(settings);
        var (line, ran) = await RunBatchCoreAsync("relimiter-settings-all", UiLocalizationService.Text("Ini.ReLimiterSettingsWhat"),
            (service, games, token) => service.ApplyReLimiterSettingsToAllAsync(games, globals, token));
        if (!ran) return line;
        try
        {
            Service.ApplyReLimiterSettingsToTemplate(globals);
            return line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Report(line + " " + UiLocalizationService.Format("Ini.TemplateNotUpdated", UiLocalizationService.Present(ex.Message)));
        }
    }

    private async Task<string> RunBatchAsync(string kind, string what,
        Func<UserIniService, IReadOnlyList<DetectedGame>, CancellationToken, Task<IniBatchResult>> run)
        => (await RunBatchCoreAsync(kind, what, run)).Line;

    // Ran = the batch went through (not refused by the busy gate, not cancelled).
    private async Task<(string Line, bool Ran)> RunBatchCoreAsync(string kind, string what,
        Func<UserIniService, IReadOnlyList<DetectedGame>, CancellationToken, Task<IniBatchResult>> run)
    {
        using var op = vm.Operations.TryBegin(kind);
        if (op is null) return (Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning")), false);
        try
        {
            var result = await run(Service, vm.AllLibraryGames.ToList(), op.Token);
            return (Report(BatchSummary(what, result)), true);
        }
        catch (OperationCanceledException)
        {
            return (Report(MainWindowViewModel.OperationCancelledText()), false);
        }
    }

    // ---- #180 ReShade presets -----------------------------------------------------------------------

    /// <summary>The user's presets folder (upstream: <c>inis/reshade-presets</c>).</summary>
    public string PresetsFolder => ReShadePresetService.PresetsFolder(TemplatesFolder);

    internal ReShadePresetService Presets => managed.ReShadePresets ?? new ReShadePresetService(records, null, managed.IniWriteRefusal);

    /// <summary>The presets in the folder (full paths, by name).</summary>
    public IReadOnlyList<string> AvailablePresets() => ReShadePresetService.ListPresets(PresetsFolder);

    /// <summary>The presets RHI installed in the selected game.</summary>
    public IReadOnlyList<InstalledReShadePreset> InstalledPresets()
        => vm.SelectedGame is { } game ? Presets.Installed(game) : [];

    /// <summary>Creates the presets folder; the localised error line, or null when it is ready.</summary>
    public string? PreparePresetsFolder()
    {
        try
        {
            Directory.CreateDirectory(PresetsFolder);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UiLocalizationService.Present(ex.Message);
        }
    }

    /// <summary>
    /// A dropped preset (#180; upstream <c>DragDropHandler.Preset</c>): checked first, then <paramref name="ask"/> offers the
    /// games whose ReShade RHI manages. Only a confirmed choice keeps it in the presets folder (never over another preset
    /// there: <see cref="ReShadePresetService.StorePreset"/>) and installs it; Cancel (null) stores nothing (review
    /// finding 3). With no such game the drop is only kept in the folder. Returns the status line, or null after Cancel.
    /// </summary>
    public async Task<string?> OfferDroppedPresetAsync(string source,
        Func<IReadOnlyList<DetectedGame>, string, Task<(DetectedGame Game, bool WithShaderPacks)?>> ask)
    {
        try { ReShadePresetService.ReadPreset(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Report(UiLocalizationService.Present(ex.Message));
        }
        var targets = PresetTargets();
        if (targets.Count == 0)
        {
            var (kept, notKept) = StorePreset(source);
            return kept is null ? notKept : Report(UiLocalizationService.Format("Ini.PresetStoredNoGames", Path.GetFileName(kept)));
        }
        if (await ask(targets, Path.GetFileName(source)) is not { } chosen) return null;
        var (stored, refusal) = StorePreset(source);
        if (stored is null) return refusal;
        var line = await InstallPresetAsync(stored, chosen.WithShaderPacks, chosen.Game);
        return Path.GetFileName(stored).Equals(Path.GetFileName(source), StringComparison.OrdinalIgnoreCase)
            ? line
            : Report(UiLocalizationService.Format("Ini.PresetStoredAs", Path.GetFileName(source), Path.GetFileName(stored)) + " " + line);
    }

    /// <summary>A dropped preset is checked and kept in the presets folder; returns its new path, or the refusal line.</summary>
    public (string? Path, string? Refusal) StorePreset(string source)
    {
        try { return (ReShadePresetService.StorePreset(source, PresetsFolder), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, Report(UiLocalizationService.Present(ex.Message)));
        }
    }

    /// <summary>Whether presets apply to the selected game (RHI manages its ReShade); the ⚙ hides them otherwise.</summary>
    public bool PresetsApply() => vm.SelectedGame is { } game && records.Find(game, ManagedComponentKind.ReShade) is not null;

    /// <summary>Library games a preset can go into (RHI manages their ReShade).</summary>
    public IReadOnlyList<DetectedGame> PresetTargets()
        => vm.AllLibraryGames.Where(game => records.Find(game, ManagedComponentKind.ReShade) is not null)
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Installs a preset into <paramref name="game"/> (the selected game when null) and, when asked, ticks the shader
    /// packs its effects need and applies them through the Shaders/Add-ons path (keeping the game's add-on selection).
    /// </summary>
    public async Task<string> InstallPresetAsync(string presetPath, bool withShaderPacks, DetectedGame? game = null)
    {
        game ??= vm.SelectedGame;
        if (game is null) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        ComponentOperationResult result;
        using (var op = vm.Operations.TryBegin("reshade-preset", cancellable: false))
        {
            if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
            result = await Presets.InstallAsync(game, presetPath, op.Token);
        }
        var line = UiLocalizationService.Present(result.Message);
        if (!result.Success || !withShaderPacks) return Report(line);

        var (packs, unresolved, error) = await ResolvePacksAsync(presetPath);
        if (error is not null) return Report(line + " " + error);
        var tail = unresolved.Count > 0 ? " " + UiLocalizationService.Format("Ini.PresetUnresolved", string.Join(", ", unresolved)) : "";
        if (packs.Count == 0) return Report(line + " " + UiLocalizationService.Text("Ini.PresetNoPacks") + tail);
        var current = managed.GetReShadeOptions(game).ShaderPackIds;
        if (packs.All(pack => current.Contains(pack, StringComparer.OrdinalIgnoreCase)))
            return Report(line + " " + UiLocalizationService.Format("Ini.PresetPacksAlready", string.Join(", ", packs)) + tail);
        var shaderIds = current.Union(packs, StringComparer.OrdinalIgnoreCase).ToList();
        ComponentOperationResult applied;
        if (ReferenceEquals(game, vm.SelectedGame))
        {
            var addons = (await vm.GetShaderAddonManagerSnapshotAsync())?.SelectedAddonIds ?? [];
            applied = await vm.ApplyShaderAddonManagerAsync(shaderIds, addons);
        }
        else
        {
            using var op = vm.Operations.TryBegin("reshade-preset-packs");
            if (op is null) return Report(line + " " + UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
            var options = managed.GetReShadeOptions(game);
            options.ShaderPackIds = shaderIds;
            applied = await managed.ApplyReShadeAsync(game, options, op.Token);
        }
        return Report(line + " " + (applied.Success
            ? UiLocalizationService.Format("Ini.PresetPacksApplied", string.Join(", ", packs))
            : UiLocalizationService.Format("Ini.PresetPacksFailed", UiLocalizationService.Present(applied.Message))) + tail);
    }

    /// <summary>Removes a preset RHI installed in the selected game.</summary>
    public async Task<string> RemovePresetAsync(string fileName)
    {
        if (vm.SelectedGame is not { } game) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        using var op = vm.Operations.TryBegin("reshade-preset", cancellable: false);
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        var result = await Presets.RemoveAsync(game, fileName, op.Token);
        return Report(UiLocalizationService.Present(result.Message));
    }

    /// <summary>
    /// Mass preset install (upstream <c>MassPresetInstall</c>): the preset into every game whose ReShade RHI manages, and
    /// the shader packs it needs into each when asked. One game per unit, Cancel between games.
    /// </summary>
    public async Task<string> InstallPresetToAllAsync(string presetPath, bool withShaderPacks)
    {
        IReadOnlyList<string> packs = [];
        if (withShaderPacks)
        {
            var (resolved, _, error) = await ResolvePacksAsync(presetPath);
            if (error is not null) return Report(error);
            packs = resolved;
        }
        var targets = PresetTargets();
        var what = Path.GetFileName(presetPath);
        using var op = vm.Operations.TryBegin("reshade-preset-all");
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        int updated = 0, refused = 0, failed = 0;
        try
        {
            foreach (var game in targets)
            {
                op.Token.ThrowIfCancellationRequested();
                using var unit = new RHI.Core.Deployment.OperationCancellation(op.Token);
                var result = await Presets.InstallAsync(game, presetPath, unit.Token);
                if (result.Success && packs.Count > 0)
                {
                    var options = managed.GetReShadeOptions(game);
                    if (!packs.All(pack => options.ShaderPackIds.Contains(pack, StringComparer.OrdinalIgnoreCase)))
                    {
                        options.ShaderPackIds = options.ShaderPackIds.Union(packs, StringComparer.OrdinalIgnoreCase).ToList();
                        using var packUnit = new RHI.Core.Deployment.OperationCancellation(op.Token);
                        result = await managed.ApplyReShadeAsync(game, options, packUnit.Token);
                        if (!result.Success) { failed++; RHI.Core.Services.AppLog.Warn("reshade-preset", $"{game.Name}: {result.Message}"); continue; }
                    }
                }
                if (result.Success) updated++;
                else { refused++; RHI.Core.Services.AppLog.Warn("reshade-preset", $"{game.Name}: {result.Message}"); }
            }
        }
        catch (OperationCanceledException)
        {
            return Report(MainWindowViewModel.OperationCancelledText());
        }
        return Report(BatchSummary(what, new IniBatchResult(updated, refused, failed)));
    }

    // The packs whose files hold the preset's effects (every pack's file list is read; a pack not downloaded yet is fetched).
    private async Task<(IReadOnlyList<string> Packs, IReadOnlyList<string> Unresolved, string? Error)> ResolvePacksAsync(string presetPath)
    {
        using var op = vm.Operations.TryBegin("reshade-preset-resolve");
        if (op is null) return ([], [], UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        try
        {
            var effects = ReShadePresetService.RequiredEffects(ReShadePresetService.ReadPreset(presetPath));
            var packFiles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in await managed.RefreshReShadeShaderPackCatalogAsync(op.Token))
            {
                op.Token.ThrowIfCancellationRequested();
                try
                {
                    var files = await managed.GetReShadeShaderPackFilesAsync(pack.Id, op.Token);
                    packFiles[pack.Id] = files.Where(file => !file.IsTexture).Select(file => file.DisplayPath).ToList();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or RHI.Core.Deployment.UntrustedDownloadException)
                {
                    RHI.Core.Services.AppLog.Warn("reshade-preset", $"shader pack {pack.Id} not read: {ex.Message}");
                }
            }
            var (packs, unresolved) = ReShadePresetService.Resolve(effects, packFiles);
            return (packs, unresolved, null);
        }
        catch (OperationCanceledException)
        {
            return ([], [], MainWindowViewModel.OperationCancelledText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ([], [], UiLocalizationService.Present(ex.Message));
        }
    }

    // ---- #189 Luma ⚙ -------------------------------------------------------------------------------

    internal LumaSettingsService Luma => managed.LumaSettings ?? new LumaSettingsService(records, null, managed.IniWriteRefusal);

    /// <summary>The selected game's Luma settings as its files hold them.</summary>
    public LumaSettingsSnapshot? LumaSettings() => vm.SelectedGame is { } game ? Luma.Read(game) : null;

    /// <summary>Sets HDR on first boot for the selected game (Default / Off / On).</summary>
    public async Task<string> SetLumaHdrAsync(LumaHdrMode mode)
    {
        if (vm.SelectedGame is not { } game) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        using var op = vm.Operations.TryBegin("luma-settings", cancellable: false);
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        var result = await Luma.SetHdrAsync(game, mode, op.Token);
        return Report(result.Success
            ? UiLocalizationService.Format("Ini.LumaHdrSet", OptionLists.LumaHdrMode.Display(mode))
            : UiLocalizationService.Present(result.Message));
    }

    /// <summary>Turns Luma's TAA keys on or off in the selected game's Engine.ini.</summary>
    public async Task<string> SetLumaTaaAsync(bool on)
    {
        if (vm.SelectedGame is not { } game) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        using var op = vm.Operations.TryBegin("luma-settings", cancellable: false);
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        var result = await Luma.SetTaaAsync(game, on, op.Token);
        if (!result.Success) return Report(UiLocalizationService.Present(result.Message));
        // Keys the user had before RHI are never removed: say so when TAA still reads as on.
        var stillOn = !on && Luma.Read(game).TaaOn;
        return Report(UiLocalizationService.Text(on ? "Ini.LumaTaaOn" : stillOn ? "Ini.LumaTaaOffKept" : "Ini.LumaTaaOff"));
    }

    // ---- #182 peak brightness and the RenoDX ⚙ -------------------------------------------------------

    internal RenoDxPeakNits PeakNits => managed.RenoDxPeakNits ?? new RenoDxPeakNits(records, null, managed.IniWriteRefusal);

    /// <summary>Connected monitors with a known HDR peak (EDID, or the value set in KDE's display settings).</summary>
    public IReadOnlyList<RHI.Platform.Linux.Hdr.DisplayPeak> DisplayPeaks() => RHI.Platform.Linux.Hdr.DisplayPeakBrightness.Read();

    /// <summary>"Apply to all games": the global peak brightness into every game whose RenoDX RHI installed.</summary>
    public Task<string> ApplyPeakNitsToAllAsync(LinuxAppSettings settings)
    {
        var peak = PeakNitsSettings.From(settings);
        if (!peak.Applies) return Task.FromResult(Report(UiLocalizationService.Text("Ini.PeakNotSet")));
        return RunBatchAsync("renodx-peak-nits-all", UiLocalizationService.Format("Ini.PeakWhat", peak.Nits),
            (_, games, token) => PeakNits.ApplyToAllAsync(games, peak, token));
    }

    /// <summary>Whether the selected game has a ReShade.ini for RenoDX's presets (upstream shows "Set Maximum Nits" only then).</summary>
    public bool GameHasReShadeIni()
        => vm.SelectedGame?.BinaryDirectory is { } bin && RenoDxPeakNits.HasReShadeIni(bin);

    /// <summary>The selected game's RenoDX peak brightness as its ReShade.ini holds it.</summary>
    public int? GameNits() => vm.SelectedGame?.BinaryDirectory is { } bin ? RenoDxPeakNits.CurrentNits(bin) : null;

    /// <summary>The RenoDX ⚙ "Set maximum nits" for the selected game.</summary>
    public async Task<string> SetGameNitsAsync(int nits)
    {
        if (vm.SelectedGame is not { } game) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        if (nits is < 1 or > RenoDxPeakNits.MaxNits) return Report(UiLocalizationService.Text("Ini.PeakInvalid"));
        using var op = vm.Operations.TryBegin("renodx-peak-nits", cancellable: false);
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        var result = await PeakNits.SetForGameAsync(game, nits, op.Token);
        return Report(result.Success ? UiLocalizationService.Format("Ini.PeakGameSet", nits) : UiLocalizationService.Present(result.Message));
    }

    /// <summary>Whether the RenoDX ⚙ offers the UE-Extended switch for the selected game.</summary>
    public bool OffersUeExtended() => vm.SelectedGame is { } game && RenoDxDeploymentService.OffersUeExtendedSwitch(game, records.Find(game, ManagedComponentKind.RenoDX));

    /// <summary>Whether the selected game's RenoDX is the UE-Extended build.</summary>
    public bool UeExtendedInstalled() => vm.SelectedGame is { } game && RenoDxDeploymentService.IsUeExtendedInstalled(records.Find(game, ManagedComponentKind.RenoDX));

    /// <summary>
    /// The UE-Extended switch (upstream <c>ToggleUeExtended</c>): RenoDX is reinstalled through the normal update path with
    /// the choice, which is saved only by that update's own stamped record (review finding 9): nothing is saved outside
    /// the operation, and a failed reinstall leaves the earlier choice as it was.
    /// </summary>
    public async Task<string> SetUeExtendedAsync(bool on)
    {
        var (_, shown) = await vm.RunComponentUpdateAsync((game, token) =>
            records.Find(game, ManagedComponentKind.RenoDX) is null
                ? Task.FromResult(ComponentOperationResult.Fail(RenoDxPeakNits.NotManagedPrefix + game.Name))
                : managed.UpdateRenoDxUeExtendedAsync(game, vm.Session.CachedFiles(game) ?? [], on, token));
        return shown;
    }

    /// <summary>
    /// The inline question an "Apply to all games" button asks first (review finding 5), with the number of games it
    /// writes into; null when it writes into none (or peak brightness is not set): it then runs at once and says so.
    /// </summary>
    public string? ApplyToAllQuestion(IniApplyAll what, LinuxAppSettings settings)
    {
        var games = vm.AllLibraryGames.ToList();
        var (label, count) = what switch
        {
            IniApplyAll.ReShadeSettings => (UiLocalizationService.Text("Ini.ReShadeSettingsWhat"), Service.ReShadeSettingsTargets(games).Count),
            IniApplyAll.ReLimiterSettings => (UiLocalizationService.Text("Ini.ReLimiterSettingsWhat"), Service.ReLimiterSettingsTargets(games).Count),
            _ => PeakNitsSettings.From(settings) is { Applies: true } peak
                ? (UiLocalizationService.Format("Ini.PeakWhat", peak.Nits), PeakNits.Targets(games).Count)
                : ("", 0)
        };
        return count == 0 ? null : UiLocalizationService.Format("Ini.ApplyAllAsk", label, count);
    }

    /// <summary>The status line for a batch, from its counts.</summary>
    public static string BatchSummary(string what, IniBatchResult result)
        => result.Total == 0 ? UiLocalizationService.Format("Ini.BatchNone", what)
            : result.Refused + result.Failed == 0 ? UiLocalizationService.Format("Ini.BatchDone", what, result.Updated)
            : UiLocalizationService.Format("Ini.BatchPartial", what, result.Updated, result.Refused, result.Failed);

    private string Report(string line)
    {
        vm.StatusText = line;
        return line;
    }
}
