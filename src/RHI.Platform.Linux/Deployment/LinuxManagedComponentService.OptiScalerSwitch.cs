using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Version switch of an installed OptiScaler (upstream: a preset saved on a version switches the game to it; owner rule
/// 2026-11-16 for #273): OptiScaler is removed and installed again as the other version, and the user's OptiScaler.ini
/// values are put back into the new INI for every key the new build has.
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    public const string SwitchNotInstalledMessage = "RHI has not installed OptiScaler on this game, so there is no version to switch.";
    /// <summary>The version label follows.</summary>
    public const string SwitchSameVersionPrefix = "OptiScaler is already this version: ";
    /// <summary>The removal's message follows; nothing changed.</summary>
    public const string SwitchRemoveFailedPrefix = "OptiScaler could not be removed to switch versions, so nothing changed: ";
    /// <summary>The install's message follows; then whether the previous version came back.</summary>
    public const string SwitchInstallFailedPrefix = "The other OptiScaler version could not be installed: ";
    public const string SwitchRestoredNote = " The previous version was installed again with your OptiScaler.ini settings.";
    /// <summary>The reinstall's message follows: the game has no RHI OptiScaler now.</summary>
    public const string SwitchNotRestoredPrefix = " The previous version could not be installed again either: ";
    /// <summary>"<old> → <new>" follows, then the kept-settings count.</summary>
    public const string SwitchedPrefix = "OptiScaler switched to another version: ";
    public const string SwitchKeptSuffix = " OptiScaler.ini settings kept: ";

    /// <summary>
    /// Switches RHI's installed OptiScaler on <paramref name="game"/> to <paramref name="target"/> (its release pin, if one
    /// is saved, as an install takes it). The previous OptiScaler.ini is read first and its values carried into the new
    /// INI; a copy stays in RHI's data folder. When the new version cannot be installed the previous one is installed again
    /// with those settings. Every step is a recorded remove or install, each journaled on its own.
    /// </summary>
    public async Task<ComponentOperationResult> SwitchOptiScalerVersionAsync(DetectedGame game, OptiScalerVariant target, CancellationToken cancellationToken = default)
    {
        var record = _optiscaler.GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(SwitchNotInstalledMessage);
        var current = OptiScalerDeploymentService.GetInstalledVariant(record);
        if (current == target) return ComponentOperationResult.Ok(SwitchSameVersionPrefix + OptiScalerVersions.Label(target));
        if (!OptiScalerVersions.IsKnown(target)) return ComponentOperationResult.Fail(OptiScalerPackageProvider.UnknownVersionPrefix + target.Id);
        var iniPath = Path.Combine(Path.GetFullPath(record.BinaryDirectory), "OptiScaler.ini");
        string? previousIni = null;
        try
        {
            if (File.Exists(iniPath))
            {
                previousIni = await File.ReadAllTextAsync(iniPath, cancellationToken).ConfigureAwait(false);
                await _optiscaler.BackUpIniAsync(game, iniPath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ComponentOperationResult.Fail(SwitchRemoveFailedPrefix + ex.Message);
        }

        var removed = await RemoveAsync(game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (!removed.Success) return ComponentOperationResult.Fail(SwitchRemoveFailedPrefix + removed.Message);
        // From here the switch runs to the end: a half-switched game would have no OptiScaler at all.
        var installed = await InstallOptiScalerAsync(game, target, CancellationToken.None).ConfigureAwait(false);
        if (!installed.Success)
        {
            var back = await InstallOptiScalerAsync(game, current, CancellationToken.None).ConfigureAwait(false);
            if (back.Success && previousIni is not null) TryCarryOver(previousIni, iniPath);
            return ComponentOperationResult.Fail(SwitchInstallFailedPrefix + installed.Message
                + (back.Success ? SwitchRestoredNote : SwitchNotRestoredPrefix + back.Message));
        }
        var kept = previousIni is null ? 0 : TryCarryOver(previousIni, iniPath);
        return new ComponentOperationResult(true, SwitchedPrefix + $"{OptiScalerVersions.Label(current)} → {OptiScalerVersions.Label(target)}." + SwitchKeptSuffix + kept,
            installed.ChangedFiles);
    }

    /// <summary>The previous INI's values into the new one; how many changed (0 when the file could not be written, logged).</summary>
    private static int TryCarryOver(string previousIni, string iniPath)
    {
        try
        {
            return File.Exists(iniPath) ? OptiScalerIniEditor.CarryOver(previousIni, iniPath).Count : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("optiscaler", "OptiScaler.ini settings were not carried over after a version switch (a copy is in RHI's data folder)", ex);
            return 0;
        }
    }
}
