using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

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

    internal UserIniService Service => new(new UserIniTemplates(TemplatesFolder), records, managed.IniWriteRefusal);

    /// <summary>The components whose row gets the INI ⚙ while they are installed.</summary>
    public static bool HasIniSettings(ManagedComponent component)
        => component.Kind is ManagedComponentKind.ReShade or ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander
           && component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;

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
    public int DeployCandidates(ManagedComponentKind kind) => vm.AllLibraryGames.Count(game => records.Find(game, kind) is not null);

    /// <summary>"Deploy X.ini" for the selected game.</summary>
    public async Task<string> DeployForSelectedAsync(ManagedComponentKind kind)
    {
        if (vm.SelectedGame is not { } game) return Report(UiLocalizationService.Text("Vm.SelectGameFirst"));
        using var op = vm.Operations.TryBegin("ini-deploy", cancellable: false);
        if (op is null) return Report(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"));
        var settings = vm.Settings.GetLinuxSettingsSnapshot().Settings;
        var service = Service;
        try
        {
            var result = await Task.Run(() => kind == ManagedComponentKind.ReShade
                ? service.DeployReShadeIni(game, settings, op.Token)
                : service.DeployAddonIni(game, kind, op.Token));
            return Report(UiLocalizationService.Present(result.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Report(UiLocalizationService.Format("Ini.DeployFailed", IniFileName(kind), UiLocalizationService.Present(ex.Message)));
        }
    }

    /// <summary>Mass INI deploy of <paramref name="kind"/>'s INI to every game where RHI manages it.</summary>
    public Task<string> DeployToAllAsync(ManagedComponentKind kind, LinuxAppSettings settings)
        => RunBatchAsync("ini-deploy-all", IniFileName(kind),
            (service, games, token) => service.DeployToAllAsync(games, kind, settings, token));

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
