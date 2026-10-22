using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    public OptiScalerAdvancedSnapshot GetOptiScalerAdvancedSettings(DetectedGame game)
        => _optiscaler.GetAdvancedSettings(game);

    public ComponentOperationResult SetOptiScalerAdvancedSetting(DetectedGame game, string id, string value)
        => PatchTargetGuard(game) ?? _optiscaler.SetAdvancedSetting(game, id, value);

    public async Task<ComponentOperationResult> SetOptiScalerAdvancedSettingAsync(
        DetectedGame game,
        string id,
        string value,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard ?? await _optiscaler.SetAdvancedSettingAsync(game, id, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The global OptiScaler overlay key (Refs #187, upstream <c>ApplyOsHotkey_Click</c>): sets <c>[Menu] ShortcutKey</c>
    /// in every INI template and in the RHI-managed <c>OptiScaler.ini</c> of every game in <paramref name="games"/>. One game
    /// is one unit (Cancel stops between games); a game whose patch target or Xbox job refuses writes, or whose file
    /// cannot be written, keeps its file and is counted as failed; games without RHI's OptiScaler are not counted.
    /// </summary>
    public OptiScalerMenuKeyResult ApplyOptiScalerMenuKeyToAll(IReadOnlyList<DetectedGame> games, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        var updated = 0;
        var failures = new List<(string Game, string Reason)>();
        var templates = 0;
        try
        {
            if (_optiscaler.IniTemplates?.ApplyMenuKey(key) is { } written)
            {
                templates = written.Updated;
                failures.AddRange(written.Failures); // each template that kept its file, by name
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add((_optiscaler.IniTemplates!.Directory, ex.Message)); // the folder could not be listed
        }
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var unit = new OperationCancellation(cancellationToken);
            if (_optiscaler.GetRecord(game) is null) continue;
            if (PatchTargetGuard(game) is { } refused)
            {
                failures.Add((game.Name, refused.Message));
                continue;
            }
            OperationCancellation.BeginWrites(unit.Token);
            var result = _optiscaler.SetMenuKey(game, key);
            if (result is null) continue;
            if (result.Success) updated++;
            else failures.Add((game.Name, result.Message));
        }
        return new OptiScalerMenuKeyResult(templates, updated, failures);
    }

    public OptiScalerVariant? GetInstalledOptiScalerVariant(DetectedGame game)
    {
        var record = _optiscaler.GetRecord(game);
        return record is null ? null : OptiScalerDeploymentService.GetInstalledVariant(record);
    }

    /// <summary>Nightly installs take the game's saved nightly build pin (#156); null pin = Latest.</summary>
    public Task<ComponentOperationResult> InstallOptiScalerAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
        => InstallOptiScalerAsync(game, variant,
            variant == OptiScalerVariant.Nightly ? _neuralVersionPins.Get(game, ManagedComponentKind.OptiScaler) : null,
            cancellationToken);

    /// <summary>Installs OptiScaler with an explicit nightly build pin (Nightly only; null = Latest). For #136's Control UE FG.</summary>
    public Task<ComponentOperationResult> InstallOptiScalerAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        string? nightlyBuild,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game) ?? LayerGuard(game, ManagedComponentKind.OptiScaler) // w3/parity (#96)
                    ?? VulkanLayerConflict(game, ManagedComponentKind.OptiScaler); // w5/reshade-core (#92)
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Install,
                () => InstallOptiScalerUnderChosenNameAsync(game, variant, nightlyBuild, cancellationToken)); // #172
    }

    /// <summary>
    /// The game's nightly build (#156): the installed record's pin while RHI's OptiScaler is installed, otherwise the saved
    /// preference. Null = Latest. The ⚙ row shows <see cref="GetOptiScalerNightlyChoice"/>, which may differ until Apply (#232).
    /// </summary>
    public string? GetOptiScalerNightlyBuild(DetectedGame game)
    {
        var record = _optiscaler.GetRecord(game);
        if (record is not null)
            return OptiScalerDeploymentService.GetPinnedNightlyBuild(record);
        var saved = _neuralVersionPins.Get(game, ManagedComponentKind.OptiScaler);
        return OptiScalerPackageProvider.IsNightlyBuild(saved) ? saved : null;
    }

    /// <summary>
    /// Saves the nightly build used at the next Nightly install, or by Apply on an installed Nightly (#232); null = Latest.
    /// Throws when the file cannot be written.
    /// </summary>
    public void SetOptiScalerNightlyBuild(DetectedGame game, string? build)
    {
        if (build is not null && !OptiScalerPackageProvider.IsNightlyBuild(build))
            throw new ArgumentException(OptiScalerDeploymentService.NightlyBuildInvalidPrefix + build, nameof(build));
        _neuralVersionPins.Set(game, ManagedComponentKind.OptiScaler, build);
    }

    public Task<IReadOnlyList<string>> ListOptiScalerNightlyBuildsAsync(CancellationToken cancellationToken = default)
        => _optiscaler.ListNightlyBuildsAsync(cancellationToken);

    /// <summary>
    /// The nightly build saved as the game's choice (#156; null = Latest), whatever is installed. While OptiScaler Nightly
    /// is installed it may differ from <see cref="GetInstalledOptiScalerNightlyBuild"/> until Apply switches it (#232).
    /// </summary>
    public string? GetOptiScalerNightlyChoice(DetectedGame game)
    {
        var saved = _neuralVersionPins.Get(game, ManagedComponentKind.OptiScaler);
        return OptiScalerPackageProvider.IsNightlyBuild(saved) ? saved : null;
    }

    /// <summary>What the ⚙ dialog's Nightly build row may do with RHI's installed OptiScaler on the game (#232).</summary>
    public OptiScalerNightlySwitchState GetOptiScalerNightlySwitchState(DetectedGame game)
    {
        var record = _optiscaler.GetRecord(game);
        if (record is null) return OptiScalerNightlySwitchState.NotInstalled;
        if (OptiScalerDeploymentService.GetInstalledVariant(record) != OptiScalerVariant.Nightly) return OptiScalerNightlySwitchState.OtherChannel;
        return OptiScalerDeploymentService.HoldsControlUeFrameGenerationBuild(game, record)
            ? OptiScalerNightlySwitchState.HeldForControlUe
            : OptiScalerNightlySwitchState.Switchable;
    }

    /// <summary>The build RHI's installed OptiScaler Nightly is on (null = Latest, or no Nightly installed).</summary>
    public string? GetInstalledOptiScalerNightlyBuild(DetectedGame game)
        => _optiscaler.GetRecord(game) is { } record ? OptiScalerDeploymentService.GetPinnedNightlyBuild(record) : null;

    /// <summary>
    /// #232 (wizardkof v0.9.25 "Apply selected build now"): switches RHI's installed OptiScaler Nightly to the saved choice
    /// (<see cref="GetOptiScalerNightlyChoice"/>) in place, guarded like an update and recorded in the update history. A
    /// failed or cancelled switch leaves the installed build and puts the choice back on it.
    /// </summary>
    public Task<ComponentOperationResult> ApplyOptiScalerNightlyBuildAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        // Guarded like an update (UpdateManagedAsync), not an install: it replaces the files already there, so an Xbox
        // card whose bitness nothing tells is not refused.
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, ManagedComponentKind.OptiScaler);
        if (guard is not null) return Task.FromResult(guard);
        var build = GetOptiScalerNightlyChoice(game);
        return Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Update,
            () => (NightlySwitchForTesting ?? ((g, b, ct) => _optiscaler.SwitchNightlyBuildAsync(g, b, _neuralVersionPins, ct)))(game, build, cancellationToken));
    }

    /// <summary>Test seam (#232 VM rows): stands in for the switch itself after the guards; null = the real one.</summary>
    internal Func<DetectedGame, string?, CancellationToken, Task<ComponentOperationResult>>? NightlySwitchForTesting { get; set; }

    /// <summary>
    /// w3/dllnames (#136): Control UE's optional frame-generation step after its RenoDX install — OptiScaler Nightly pinned
    /// to <see cref="ControlUeFrameGenerationService.RequiredNightlyBuild"/> as winmm.dll with its FG stack and settings,
    /// and FgSlHdr10 in reshade.ini. Guarded like an OptiScaler install; recorded in the OptiScaler update history.
    /// </summary>
    public Task<ComponentOperationResult> InstallControlUeFrameGenerationAsync(DetectedGame game, bool useHdr, CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game) ?? LayerGuard(game, ManagedComponentKind.OptiScaler);
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Install,
                () => new ControlUeFrameGenerationService(_optiscaler, _records).InstallAsync(game, useHdr, cancellationToken));
    }
}

/// <summary>What the OptiScaler ⚙ Nightly build row may do with the installed OptiScaler (#232).</summary>
public enum OptiScalerNightlySwitchState
{
    /// <summary>RHI has not installed OptiScaler on the game: the choice is used at the next install.</summary>
    NotInstalled,
    /// <summary>RHI's OptiScaler is another channel: no nightly build applies.</summary>
    OtherChannel,
    /// <summary>RHI's OptiScaler Nightly: a chosen build can be applied in place.</summary>
    Switchable,
    /// <summary>Control frame generation's pinned build (#136): it stays on that build.</summary>
    HeldForControlUe
}

/// <summary>What setting the global OptiScaler overlay key changed (Refs #187): templates and games updated, games that kept their file.</summary>
public sealed record OptiScalerMenuKeyResult(int Templates, int Games, IReadOnlyList<(string Game, string Reason)> Failures);
