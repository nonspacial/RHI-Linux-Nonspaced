using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Control Ultimate Edition's optional frame-generation step (upstream 2.8.1 <c>ded2185</c>/<c>e10099f</c> step 6 and 7,
/// pinned to nightly 20261004 by 2.8.7 <c>049077c</c>; #136). Runs after the RenoDX install the user confirmed with
/// "Install OptiScaler FG = Yes": OptiScaler Nightly pinned to <see cref="RequiredNightlyBuild"/> as <c>winmm.dll</c> (so
/// ReShade keeps <c>dxgi.dll</c>), its Streamline + DLSS Enabler stack (with <c>nvngx_dlssg.dll</c>) and upstream's FG
/// settings in <c>OptiScaler.ini</c>; then "Using HDR?" as <c>FgSlHdr10</c> under <c>[renodx-preset1]</c> in
/// <c>reshade.ini</c>, owned on the RenoDX record (RenoDX removal puts it back; a value the user changed is kept).
/// <para>Each step stands alone, as upstream: a failure is reported and never fails or undoes the RenoDX install.
/// Unlike upstream, OptiScaler stays a normally managed component (Update All reports the pin current; its removal
/// undoes its files and override), and an OptiScaler already installed under another build or name is never replaced.</para>
/// </summary>
public sealed class ControlUeFrameGenerationService
{
    /// <summary>The nightly build newer builds broke FG in Control on (upstream 2.8.7 <c>ControlUeRequiredNightlyBuild</c>).</summary>
    public const string RequiredNightlyBuild = "20261004";
    public const string OptiScalerProxyName = "winmm.dll";
    /// <summary>RenoDX record metadata: owned-key state of <c>FgSlHdr10</c> (a <see cref="RenoDxIniOverrideState"/>).</summary>
    public const string HdrMetadataKey = "ControlUe.FgSlHdr10";
    public const string HdrSection = "renodx-preset1";
    public const string HdrKey = "FgSlHdr10";

    // Fixed text first, values last (PT-BR presentation pairs).
    public const string SetUpPrefix = "Control UE frame generation set up: OptiScaler Nightly pinned as winmm.dll with Streamline, DLSS Enabler and the frame generation settings. Build: ";
    public const string NotSetUpPrefix = "Control UE frame generation was not set up; the RenoDX install is kept: ";
    public const string OptiScalerElsewherePrefix = "OptiScaler is already installed on this game under another build or DLL name, so it was left as it is. Remove OptiScaler, then install RenoDX again with frame generation. Installed: ";
    public const string StackFailedPrefix = " Streamline/DLSS Enabler could not be deployed: ";
    public const string SettingsFailedPrefix = " The frame generation settings could not be written to OptiScaler.ini: ";
    public const string ReShadeNameNote = " ReShade keeps its own DLL name (upstream moves it to dxgi.dll; RHI does not rename an installed ReShade): ";
    public const string HdrSetPrefix = " FgSlHdr10 set in reshade.ini: ";
    public const string HdrNotSetPrefix = " FgSlHdr10 was not set in reshade.ini: ";
    public const string HdrNoIniMessage = "reshade.ini was not found in the game folder.";
    public const string HdrNoRenoDxMessage = "RenoDX is not installed by RHI on this game.";
    public const string HdrUserValueKeptMessage = "the value was changed outside RHI and was kept.";

    /// <summary>Upstream's step 6 INI settings, as advanced-setting ids and values.</summary>
    internal static readonly IReadOnlyList<(string Id, string Value)> FrameGenerationSettings =
    [
        ("Dx12Upscaler", "dlss"),
        ("FrameGenEnabled", "true"),
        ("FGInput", "upscaler"),
        ("FGOutput", "dlssg"),
        ("FGNvngxReplacement", "None"),
        (OptiScalerDeploymentService.ForceReflexSettingId, "2"),
        (OptiScalerDeploymentService.UseGamesReflexMarkersSettingId, "false"),
    ];

    private readonly OptiScalerDeploymentService _optiScaler;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DeploymentJournal? _journal;

    /// <param name="journal">The deployment journal the FgSlHdr10 edit is journaled in; null = the OptiScaler service's
    /// (the app's journal), or in-process rollback only when that has none.</param>
    public ControlUeFrameGenerationService(OptiScalerDeploymentService optiScaler, LinuxDeploymentRecordStore records, DeploymentJournal? journal = null)
    {
        _optiScaler = optiScaler;
        _records = records;
        _journal = journal ?? optiScaler?.Journal;
    }

    /// <summary>
    /// Steps 6 and 7. Success means OptiScaler is on the pinned build as <c>winmm.dll</c> (installed now or already);
    /// the message lists what else happened. Never touches the RenoDX add-on.
    /// </summary>
    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, bool useHdr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var optiScaler = await EnsureOptiScalerAsync(game, cancellationToken).ConfigureAwait(false);
        var notes = optiScaler.Message;
        if (optiScaler.Success)
        {
            notes = SetUpPrefix + RequiredNightlyBuild + ".";
            var record = _optiScaler.GetRecord(game);
            if (record is not null && !_optiScaler.IsManagedFgStackEnabled(record))
            {
                var stack = await _optiScaler.SetAdvancedSettingAsync(game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true", cancellationToken).ConfigureAwait(false);
                if (!stack.Success) notes += StackFailedPrefix + stack.Message;
            }
            var failed = FrameGenerationSettings
                .Select(setting => _optiScaler.SetAdvancedSetting(game, setting.Id, setting.Value))
                .FirstOrDefault(result => !result.Success);
            if (failed is not null) notes += SettingsFailedPrefix + failed.Message;
            if (_records.Find(game, ManagedComponentKind.ReShade) is { } reShade
                && ProxyPayloadLocator.ReShadePayload(reShade) is { } payload
                && !Path.GetFileName(payload.Path).Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase))
                notes += ReShadeNameNote + Path.GetFileName(payload.Path);
        }
        else
        {
            notes = NotSetUpPrefix + notes;
        }

        // Step 7 runs whatever step 6 did, as upstream.
        notes += await SetHdrKeyAsync(game, useHdr).ConfigureAwait(false);
        return optiScaler.Success ? ComponentOperationResult.Ok(notes) : ComponentOperationResult.Fail(notes);
    }

    private async Task<ComponentOperationResult> EnsureOptiScalerAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        if (_optiScaler.GetRecord(game) is { } existing)
        {
            var pinned = OptiScalerDeploymentService.GetPinnedNightlyBuild(existing);
            var name = OptiScalerDeploymentService.GetProxyName(existing);
            if (pinned == RequiredNightlyBuild && name.Equals(OptiScalerProxyName, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Ok("");
            var variant = OptiScalerDeploymentService.VariantLabel(OptiScalerDeploymentService.GetInstalledVariant(existing));
            return ComponentOperationResult.Fail(OptiScalerElsewherePrefix + $"{variant} {pinned ?? existing.Version} / {name}");
        }
        return await _optiScaler.InstallAsync(game, OptiScalerVariant.Nightly, RequiredNightlyBuild, OptiScalerProxyName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>FgSlHdr10</c> (1 with HDR, else 0) under <c>[renodx-preset1]</c> through the owned-key state kept on the
    /// RenoDX record, so RenoDX removal restores it; a value the user changed since is kept. Returns the note.
    /// <para>Journaled (review nit 5): reshade.ini (the file a link resolves to) is tracked with a verified copy before
    /// the edit and the edit declared; the commit point is the RenoDX record saved with the key's ownership (stamped). A
    /// failure, or a crash before that save, puts reshade.ini back, so the key is never left without an owner. Runs after
    /// RenoDX's own install committed, as its own unit; never cancelled.</para>
    /// </summary>
    internal async Task<string> SetHdrKeyAsync(DetectedGame game, bool useHdr)
    {
        var renoDx = _records.Find(game, ManagedComponentKind.RenoDX);
        if (renoDx is null || string.IsNullOrWhiteSpace(game.BinaryDirectory))
            return HdrNotSetPrefix + HdrNoRenoDxMessage;
        if (RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory) is not { } iniPath)
            return HdrNotSetPrefix + HdrNoIniMessage;
        var previous = renoDx.Metadata.TryGetValue(HdrMetadataKey, out var json) ? RenoDxIniOverrideManager.Deserialize(json) : null;
        var value = useHdr ? "1" : "0";

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"Control UE FgSlHdr10: {game.Name}", game, ManagedComponentKind.RenoDX, CancellationToken.None).ConfigureAwait(false);
        if (journalRefusal is not null) return HdrNotSetPrefix + journalRefusal;
        await using var scope = opened!;
        RenoDxIniOverrideApplyResult applied;
        try
        {
            // The INI writer follows a link to its target (UserFileLinkPolicy), so the target is what changes. Resolved by
            // the writer's own rule before anything is copied (w3 final review, finding 1): a link the policy refuses is
            // refused here, so no rollback copy is ever written beside a target outside the game's folders, and the file
            // tracked is exactly the one the writer replaces.
            var written = AtomicFile.ResolveUserFileTarget(iniPath, RenoDxIniOverrideManager.LinkPolicy(game.BinaryDirectory, game.InstallPath));
            await scope.TrackAsync(written, CancellationToken.None).ConfigureAwait(false);
            applied = RenoDxIniOverrideManager.Apply(game.BinaryDirectory, new Dictionary<string, string> { [HdrKey] = value },
                previous, gameRoot: game.InstallPath, section: HdrSection);
            if (applied.State is null)
            {
                await scope.RollbackAsync().ConfigureAwait(false);
                return HdrNotSetPrefix + HdrNoIniMessage;
            }
            await scope.DeclareWrittenContentAsync(written, CancellationToken.None).ConfigureAwait(false);
            renoDx.Metadata[HdrMetadataKey] = RenoDxIniOverrideManager.Serialize(applied.State);
            // The commit point: nothing owns the key until this save lands; a failure before it puts reshade.ini back.
            _records.Upsert(scope.Stamp(renoDx));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (!rollback.Success)
                AppLog.Warn("control-ue", $"FgSlHdr10 rollback did not complete: {string.Join("; ", rollback.Failures)}");
            return HdrNotSetPrefix + ex.Message + DeploymentScope.RollbackNote(rollback);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return applied.AppliedCount > 0 ? HdrSetPrefix + value : HdrNotSetPrefix + HdrUserValueKeptMessage;
    }
}
