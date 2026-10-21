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
    /// The nightly build shown for the game (#156): the installed record's pin while OptiScaler Nightly is installed (its
    /// choice is fixed until removal, as upstream), otherwise the saved preference. Null = Latest.
    /// </summary>
    public string? GetOptiScalerNightlyBuild(DetectedGame game)
    {
        var record = _optiscaler.GetRecord(game);
        if (record is not null)
            return OptiScalerDeploymentService.GetPinnedNightlyBuild(record);
        var saved = _neuralVersionPins.Get(game, ManagedComponentKind.OptiScaler);
        return OptiScalerPackageProvider.IsNightlyBuild(saved) ? saved : null;
    }

    /// <summary>Saves the nightly build used at the next Nightly install (null = Latest). Throws when the file cannot be written.</summary>
    public void SetOptiScalerNightlyBuild(DetectedGame game, string? build)
    {
        if (build is not null && !OptiScalerPackageProvider.IsNightlyBuild(build))
            throw new ArgumentException(OptiScalerDeploymentService.NightlyBuildInvalidPrefix + build, nameof(build));
        _neuralVersionPins.Set(game, ManagedComponentKind.OptiScaler, build);
    }

    public Task<IReadOnlyList<string>> ListOptiScalerNightlyBuildsAsync(CancellationToken cancellationToken = default)
        => _optiscaler.ListNightlyBuildsAsync(cancellationToken);

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

/// <summary>What setting the global OptiScaler overlay key changed (Refs #187): templates and games updated, games that kept their file.</summary>
public sealed record OptiScalerMenuKeyResult(int Templates, int Games, IReadOnlyList<(string Game, string Reason)> Failures);
