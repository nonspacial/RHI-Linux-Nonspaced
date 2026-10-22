using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

// w11/nightly-switch (#232; upstream RHI 2.8.7 keeps the build dropdown live while Nightly is installed, wizardkof
// v0.9.25.0-beta.1 "Apply selected build now"): an installed OptiScaler Nightly changes its build in place through
// OptiScaler's own update transaction (UpdateCoreAsync).
public sealed partial class OptiScalerDeploymentService
{
    // Fixed text first, values last (PT-BR presentation pairs).
    /// <summary>The switch landed on a pinned build; the build follows.</summary>
    public const string NightlySwitchedPrefix =
        "OptiScaler Nightly switched in place; OptiScaler.ini, its DLL name and the Wine override were kept. Build: ";
    /// <summary>The switch landed on Latest (auto-update); the version follows.</summary>
    public const string NightlySwitchedToLatestPrefix =
        "OptiScaler Nightly switched in place to Latest (auto-update); OptiScaler.ini, its DLL name and the Wine override were kept. Version: ";
    /// <summary>Nothing was switched: the installed build, the record and the saved choice are as before; the reason follows.</summary>
    public const string NightlySwitchFailedPrefix =
        "OptiScaler Nightly was not switched; the installed build stays and the saved choice is back on it: ";
    /// <summary>
    /// Nothing was switched or changed, but the saved choice could not be put back on the installed build before the switch
    /// (its file cannot be written), so it still names the chosen build; the reason follows.
    /// </summary>
    public const string NightlySwitchChoiceNotResetPrefix =
        "OptiScaler Nightly was not switched and nothing was changed, but the saved choice could not be put back on the installed build: ";
    /// <summary>The chosen build could not be had (offline, no such build, a refused archive); the reason follows.</summary>
    public const string NightlyBuildUnavailablePrefix = "The chosen OptiScaler Nightly build could not be downloaded: ";
    public const string NightlySwitchNotNightlyMessage =
        "Only an installed OptiScaler Nightly can change its build in place; nothing was changed.";
    /// <summary>The installed build is already the chosen one; the build (or Latest's version) follows.</summary>
    public const string NightlySwitchSameBuildPrefix = "OptiScaler Nightly is already on the chosen build; nothing was changed. Build: ";
    /// <summary>Control frame generation's pinned build (#136) is never switched away from; the build follows.</summary>
    public const string NightlySwitchControlUePrefix =
        "This OptiScaler Nightly was installed for Control's frame generation, which needs this build, so it was not switched; remove OptiScaler to choose another build. Build: ";

    /// <summary>
    /// True while <paramref name="record"/> is the OptiScaler Control Ultimate Edition's frame-generation step installed
    /// (#136: Nightly pinned to <see cref="ControlUeFrameGenerationService.RequiredNightlyBuild"/> as
    /// <see cref="ControlUeFrameGenerationService.OptiScalerProxyName"/>, on that game): newer builds break its frame
    /// generation, so the build is never switched in place (#232 decision).
    /// </summary>
    public static bool HoldsControlUeFrameGenerationBuild(DetectedGame game, LinuxDeploymentRecord record)
        => ControlUePostInstallService.IsControlUltimateEdition(game)
           && GetPinnedNightlyBuild(record) == ControlUeFrameGenerationService.RequiredNightlyBuild
           && GetProxyName(record).Equals(ControlUeFrameGenerationService.OptiScalerProxyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// #232: switches RHI's installed OptiScaler Nightly on <paramref name="game"/> to <paramref name="build"/> (a nightly
    /// date; null = Latest, which auto-updates) in place, through OptiScaler's update transaction: the same modified-file
    /// refusal, journal and rollback, the same DLL name, OptiScaler.ini and OptiPatcher/Streamline stack handling. The
    /// record's pin and the saved choice in <paramref name="choices"/> (when given) end up equal to the build really
    /// installed: the choice is put back on the installed build before anything is downloaded, then written with the
    /// stamped record (tracked + declared, before the commit point) — so a refusal, a failure, a Cancel or a crash at any
    /// point leaves the installed build, its record and the choice agreeing. The caller checks the Xbox write gate.
    /// </summary>
    public async Task<ComponentOperationResult> SwitchNightlyBuildAsync(
        DetectedGame game,
        string? build,
        NeuralAddonVersionPreferenceStore? choices = null,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null || GetInstalledVariant(record) != OptiScalerVariant.Nightly)
            return ComponentOperationResult.Fail(NightlySwitchNotNightlyMessage);
        if (build is not null && !OptiScalerPackageProvider.IsNightlyBuild(build))
            return ComponentOperationResult.Fail(NightlyBuildInvalidPrefix + build);
        var installed = GetPinnedNightlyBuild(record);
        if (HoldsControlUeFrameGenerationBuild(game, record))
            return ComponentOperationResult.Fail(NightlySwitchControlUePrefix + installed);
        if (string.Equals(build, installed, StringComparison.Ordinal))
            return ComponentOperationResult.Ok(NightlySwitchSameBuildPrefix + (installed ?? record.Version));

        // A failed or cancelled switch leaves the choice on the installed build (#232): it goes there now, before any
        // download, so the journal's copy of the file (taken inside the operation) is that state too.
        try { choices?.Set(game, ManagedComponentKind.OptiScaler, installed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ComponentOperationResult.Fail(NightlySwitchChoiceNotResetPrefix + ex.Message);
        }

        var result = await UpdateCoreAsync(game, record, new NightlySwitch(build, choices, game), cancellationToken).ConfigureAwait(false);
        return result.Success ? result : ComponentOperationResult.Fail(NightlySwitchFailedPrefix + result.Message);
    }

    /// <summary>What the update transaction does differently for an in-place Nightly switch (#232).</summary>
    private sealed class NightlySwitch(string? build, NeuralAddonVersionPreferenceStore? choices, DetectedGame game)
    {
        public Task<OptiScalerPackage> FetchAsync(IOptiScalerPackageProvider packages, CancellationToken cancellationToken)
            => build is null
                ? packages.GetLatestAsync(OptiScalerVariant.Nightly, cancellationToken)
                : packages.GetNightlyBuildAsync(build, cancellationToken);

        /// <summary>The record's pin follows the build; the saved choice is written tracked and declared in <paramref name="scope"/>.</summary>
        public async Task BeforeCommitAsync(LinuxDeploymentRecord record, DeploymentScope scope, CancellationToken cancellationToken)
        {
            if (build is null) record.Metadata.Remove(NightlyBuildMetadataKey);
            else record.Metadata[NightlyBuildMetadataKey] = build;
            if (choices?.StageSet(game, ManagedComponentKind.OptiScaler, build) is not { } choice) return;
            await scope.TrackAsync(choice.Path, cancellationToken).ConfigureAwait(false);
            await scope.DeclareWriteAsync(choice.Path, FileHash.Sha256HexOf(choice.After), cancellationToken).ConfigureAwait(false);
            AtomicFile.WriteStoreFile(choice.Path, choice.After);
        }

        public string SuccessMessage(OptiScalerPackage package)
            => build is null ? NightlySwitchedToLatestPrefix + package.Version : NightlySwitchedPrefix + build;
    }
}
