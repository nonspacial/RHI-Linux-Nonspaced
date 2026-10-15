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
