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
    /// <summary>"<old> → <new>" follows, then the kept-settings count, then the install's own line.</summary>
    public const string SwitchedPrefix = "OptiScaler switched to another version: ";
    public const string SwitchKeptSuffix = " OptiScaler.ini settings kept: ";
    /// <summary>w14 final review m1: the new version cannot be installed, found before anything was removed; the reason follows.</summary>
    public const string SwitchNotStartedPrefix = "OptiScaler was not switched, nothing changed: ";
    /// <summary>w14 final review m1: the installed version could not be fetched again (so it could not come back); the reason follows.</summary>
    public const string SwitchPreviousUnavailablePrefix = "OptiScaler was not switched, nothing changed: the installed version could not be prepared to come back if the switch failed: ";
    /// <summary>w14 final review m2: ANR FG was put back in the new version's custom folder.</summary>
    public const string SwitchAnrFgCarriedNote = " ANR FG was put back for the new version.";
    /// <summary>w14 final review m2: ANR FG could not come with the switch; its reason follows, then <see cref="SwitchAnrFgClearedNote"/>.</summary>
    public const string SwitchAnrFgDroppedPrefix = " ANR FG was removed with the old version and could not be put back: ";
    public const string SwitchAnrFgClearedNote = " FG Nvngx Replacement was set to None in OptiScaler.ini, so OptiScaler does not look for it.";

    /// <summary>
    /// Switches RHI's installed OptiScaler on <paramref name="game"/> to <paramref name="target"/> (its release pin, if one
    /// is saved, as an install takes it). Before anything is removed (w14 final review m1) the new version is checked
    /// (nvngx_dlssnr.dll's owner) and downloaded, and the installed version is downloaded again by its exact release, so a
    /// failed install puts exactly that version back. The previous OptiScaler.ini is read first and its values carried
    /// into the new INI; a copy stays in RHI's data folder. ANR FG, whose files are on OptiScaler's record, is put back
    /// for the new version, or its provider is cleared from the INI (m2). Every step is a recorded remove or install,
    /// each journaled on its own.
    /// </summary>
    public async Task<ComponentOperationResult> SwitchOptiScalerVersionAsync(DetectedGame game, OptiScalerVariant target, CancellationToken cancellationToken = default)
    {
        var record = _optiscaler.GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(SwitchNotInstalledMessage);
        var current = OptiScalerDeploymentService.GetInstalledVariant(record);
        if (current == target) return ComponentOperationResult.Ok(SwitchSameVersionPrefix + OptiScalerVersions.Label(target));
        if (!OptiScalerVersions.IsKnown(target)) return ComponentOperationResult.Fail(OptiScalerPackageProvider.UnknownVersionPrefix + target.Id);

        // m1: nothing is removed until both versions are in hand.
        var (prepared, refused) = target == OptiScalerVariant.Nightly
            ? await _optiscaler.PrepareInstallAsync(game, target, GetOptiScalerNightlyChoice(game), releaseTag: null, cancellationToken).ConfigureAwait(false)
            : await _optiscaler.PrepareInstallAsync(game, target, nightlyBuild: null, GetOptiScalerReleasePin(game, target), cancellationToken).ConfigureAwait(false);
        if (prepared is null) return ComponentOperationResult.Fail(SwitchNotStartedPrefix + refused);
        var (previous, unavailable) = await _optiscaler.PrepareExactInstallAsync(record, cancellationToken).ConfigureAwait(false);
        if (previous is null) return ComponentOperationResult.Fail(SwitchPreviousUnavailablePrefix + unavailable);
        var hadAnrFg = record.Metadata.ContainsKey(OptiScalerDeploymentService.AnrFgVersionMetadataKey);

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

        var removed = await RemoveOptiScalerKeepingIniChoicesAsync(game, cancellationToken).ConfigureAwait(false);
        if (!removed.Success) return ComponentOperationResult.Fail(SwitchRemoveFailedPrefix + removed.Message);
        // From here the switch runs to the end: a half-switched game would have no OptiScaler at all.
        var installed = await InstallOptiScalerPinnedAsync(game, target, prepared.NightlyBuild, prepared.ReleaseTag, CancellationToken.None, prepared).ConfigureAwait(false);
        if (!installed.Success)
        {
            var back = await InstallOptiScalerPinnedAsync(game, current, previous.NightlyBuild, previous.ReleaseTag, CancellationToken.None, previous).ConfigureAwait(false);
            var anrBack = "";
            if (back.Success)
            {
                anrBack = hadAnrFg ? await PutAnrFgBackAsync(game) : "";
                if (previousIni is not null) TryCarryOver(previousIni, iniPath);
                if (anrBack.StartsWith(SwitchAnrFgDroppedPrefix, StringComparison.Ordinal)) anrBack += ClearAnrFgProvider(iniPath);
            }
            return ComponentOperationResult.Fail(SwitchInstallFailedPrefix + installed.Message
                + (back.Success ? SwitchRestoredNote + anrBack : SwitchNotRestoredPrefix + back.Message));
        }
        var anr = hadAnrFg ? await PutAnrFgBackAsync(game) : "";
        var kept = previousIni is null ? 0 : TryCarryOver(previousIni, iniPath);
        if (anr.StartsWith(SwitchAnrFgDroppedPrefix, StringComparison.Ordinal)) anr += ClearAnrFgProvider(iniPath);
        return new ComponentOperationResult(true, SwitchedPrefix + $"{OptiScalerVersions.Label(current)} → {OptiScalerVersions.Label(target)}." + SwitchKeptSuffix + kept
            + " " + installed.Message + anr, // nit: the install's own notes (INI template, OptiPatcher, a skipped name) stay
            installed.ChangedFiles);
    }

    /// <summary>m2: ANR FG into the reinstalled OptiScaler's custom folder; the note to add.</summary>
    private async Task<string> PutAnrFgBackAsync(DetectedGame game)
    {
        var result = await _optiscaler.EnsureAnrFgAsync(game, CancellationToken.None).ConfigureAwait(false);
        return result.Success ? SwitchAnrFgCarriedNote : SwitchAnrFgDroppedPrefix + result.Message;
    }

    /// <summary>
    /// m2: an OptiScaler.ini that still names ANR FG as its FG Nvngx Replacement after ANR FG could not come back is set
    /// to None (OptiScaler's own value for the real DLSSG); the note to add, or "" when it named something else.
    /// </summary>
    private static string ClearAnrFgProvider(string iniPath)
    {
        try
        {
            if (!File.Exists(iniPath)
                || !string.Equals(OptiScalerIniEditor.GetValueInFile(iniPath, "FrameGen", "FGNvngxReplacement"), AnrFgProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
                return "";
            OptiScalerIniEditor.SetValuesInFile(iniPath, [("FrameGen", "FGNvngxReplacement", "None")]);
            return SwitchAnrFgClearedNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("optiscaler", "OptiScaler.ini still names ANR FG after a version switch and could not be changed", ex);
            return "";
        }
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
