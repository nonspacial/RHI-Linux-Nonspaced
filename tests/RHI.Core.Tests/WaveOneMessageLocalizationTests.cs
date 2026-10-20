using RHI.Core.Services;
using RHI.Core.Deployment;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Integration w1 review (ui-build finding 1): every message the wave-1 lanes added in core/platform code that can reach
/// the status line has a PT-BR presentation. Each sample is the message as the producer builds it (constants where the
/// producer has them), with neutral values (paths, ids, Portuguese exception text), so any English left in the PT-BR
/// output comes from the message itself. Finding 2: the generic fallback names the app log and claims nothing about the
/// operation's state.
/// </summary>
public sealed class WaveOneMessageLocalizationTests
{
    private const string P = "/jogos/Exemplo/addon.dll";
    private const string E = "E/S negada";

    public static TheoryData<string> Messages()
    {
        var data = new TheoryData<string>();
        foreach (var message in All()) data.Add(message);
        return data;
    }

    private static IEnumerable<string> All()
    {
        // Wave 2 trust lane: per-source download policies (#42), host-tool runner (#37), nightly (#27), blob pin (#41).
        foreach (var policy in DownloadSources.All)
            yield return policy.RefusalPrefix + "https://host.invalid/x.zip";
        yield return ExternalProcess.TimeoutPrefix + "7zz (120 s)";
        yield return ExternalArchiveRunner.NoToolPrefix + "pacote.7z";
        yield return ExternalArchiveRunner.SevenZipRequiredPrefix + "pacote.zip";
        yield return $"{ExternalArchiveRunner.FailedPrefix}7zz (2): {E}";
        yield return $"{ReShadeArchiveExtractor.ArchiveRefusedPrefix}{ExternalArchiveRunner.SymlinkRefusal}pacote/mal -> /etc/hostname";
        yield return ReShadeArchiveExtractor.ArchiveRefusedPrefix + ReShadeArchiveExtractor.EmptyArchiveRefusal;
        yield return Dlssg2030UnlockDeploymentService.BlobMismatchPrefix + "version.dll";
        yield return Dlssg2030UnlockDeploymentService.BlobMissingPrefix + "version.dll@0123abc";
        yield return ReShadeDeploymentService.NightlyLookupFailedMessage;
        yield return $"{LumaEngineIniOverrideManager.LinkRefusalPrefix}/prefixo/drive_c/Engine.ini -> /outro/Engine.ini";
        yield return LumaFrameworkDeploymentService.RollbackIncompletePrefix + E;
        yield return $"Universal MFG update failed: {E}{DeploymentMessages.PreviousFileKeptFragment}{P}.rhi-previous-1";
        yield return $"DLSS Enabler Standalone update failed: {E}{DeploymentMessages.PreviousFileKeptFragment}{P}.rhi-previous-1";
        yield return $"RTX 20 FG Unlock legado update failed: {E}{DeploymentMessages.PreviousFileKeptFragment}{P}.anterior-2";
        yield return $"RE Framework update failed: {E}{DeploymentMessages.PreviousFileKeptFragment}{P}.anterior-1";
        // Launch options refused on a store hand-off (wave 2, #46).
        yield return RHI.Platform.Linux.Launch.LinuxGameLauncher.HandoffProfileRefused;
        // AtomicFile / TrustedDownload (core).
        yield return AtomicFile.NoFolderPrefix + P;
        yield return $"{AtomicFile.SizeLimitPrefix}1024{AtomicFile.SizeLimitMiddle}addon.dll";
        yield return AtomicFile.CopyVerifyFailurePrefix + P;
        yield return $"{AtomicFile.ShaMismatchPrefix}aa11{AtomicFile.ShaMismatchMiddle}bb22): addon.dll";
        yield return AtomicFile.SameFilePrefix + P;
        yield return $"{AtomicFile.MoveTargetExistsPrefix}{P} -> {P}.2";
        yield return $"{AtomicFile.MoveFailedPrefix}18): {P} -> {P}.2";
        yield return $"{TrustedDownload.SizeLimitPrefix}100{AtomicFile.SizeLimitMiddle}500 bytes";
        yield return "Could not register this RHI instance in /estado/instancias.";
        // Deployment journal and owned-file rollback notes (core).
        yield return $"Journal entry recovery failed: 0a1b: {E}";
        yield return $"Unreadable journal entry kept at {P}: {E}";
        yield return "No handler is registered for this step; it was left for the next start: wine-override";
        yield return "Changed after the interruption and left as it is: wine-override";
        yield return "Undo deferred to the next start: wine-override";
        yield return $"Could not undo this step: wine-override ({E})";
        yield return $"The journal entry could not be narrowed to the deferred steps ({E}).";
        yield return $"Could not remove {P}";
        yield return $"The journal entry could not be finalised; it is cleaned on the next start: 0a1b ({E})";
        yield return $"Rolled back, but the journal entry could not be removed: 0a1b ({E})";
        yield return $"{P}.bak: not put back; its content was last placed in {P}, which was changed after the interruption.";
        yield return $"{P}: no surviving copy with the pre-operation content (aa11) was found; every copy was kept.";
        yield return $"{P}.bak: the backup disappeared although this operation never changed it; every copy was kept.";
        yield return $"{P}.bak: removed by something else after the interruption (this operation never changed it); nothing to put back.";
        yield return $"{P}: changed after the interruption (neither its earlier content nor what RHI wrote); left as it is. It did not exist before the operation.";
        yield return $"{P}: changed after the interruption (neither its earlier content nor what RHI wrote); left as it is. RHI's copy of its earlier content was moved to {P}.pre-rhi";
        yield return $"{P}.bak: changed after the interruption; left as it is.";
        yield return $"{P}: rollback failed ({E}); every copy was kept.";
        yield return "Wine is running in this prefix and would overwrite the change when it exits, so this Wine override is undone at the next start: d3dcompiler_47 (/prefixo)";
        yield return "This Wine override was changed after the interruption and was left as it is: dxgi (/prefixo) = 'native'";
        // TransactionalFileDeploymentService (DLSS swaps) and ReShade.
        yield return $"Source file does not exist: {P}";
        yield return $"Target directory does not exist: {P}";
        yield return $"No .original backup exists for {P}.";
        yield return $"The deployment journal could not be written ({E}); nothing was changed.";
        yield return $"{E} Rollback was incomplete (Could not remove {P}); the pre-change copies (*{OwnedFileSet.SnapshotMarker}0a1b) were kept next to the affected files.";
        yield return $"ReShade install/update failed: the deployment journal could not be written ({E}). Nothing was changed.";
        // DlssManagerService.
        yield return "DLSS manifest did not answer and no cached copy exists (timeout 30 s).";
        yield return "Downloaded archive does not contain nvngx_dlss.dll.";
        yield return $"The cached DLL is not readable after extraction: {P}";
        yield return "Extracted file is unreadable or unexpectedly small: nvngx_dlss.dll";
        yield return "Manifest entry has a malformed sha256: 310.1.0";
        // RenoDX, RenoDX Upgrade, DOF Fix.
        yield return $"RenoDX install failed: {E} Rollback incomplete: Could not restore {P} ({E}); its previous contents were kept at {P}.rhi-snapshot-1";
        yield return $"RenoDX removal failed: {E} Restore incomplete: Could not restore {P} ({E}) nor keep a copy of its previous contents ({E})";
        yield return $"RenoDX install failed: {E} Control UE changes could not be undone: {E}";
        yield return $"RenoDX removal failed: Could not remove {P}: {E}";
        yield return "RenoDX install failed: Downloaded RenoDX file is not a valid PE add-on (possible HTML/error response).";
        yield return "RenoDX Upgrade install failed: Downloaded RenoDX Upgrade add-on is not a valid PE payload or is unexpectedly small.";
        yield return "RenoDX Upgrade install failed: Downloaded RenoDX Upgrade payload has the wrong architecture (expected 64-bit): 32-bit";
        yield return "Downloaded Universal UE DOF Fix is not a valid PE payload or is unexpectedly small.";
        yield return "Downloaded Universal UE DOF Fix failed the SHA-256 digest published by GitHub.";
        yield return $"DOF Fix update check failed: {E}";
        // Policy, Winetricks route, Repair after move.
        yield return $"Upstream RHI manifest unavailable: {E}";
        yield return "No safe Winetricks route is available for this prefix.";
        yield return D3DCompilerToolInstaller.RuntimeUnknownMessagePrefix + P; // w2-launch re-review, major 1
        yield return D3DCompilerToolInstaller.ToolTimedOutPrefix + "winetricks (600 s)"; // w2-vmsplit re-review, minor 1
        yield return WineOverrideCompensator.UnreadableLedgerPrefix + P + ".corrupt-20261008T120000Z"; // w2-wine re-review, nit 2
        yield return DeploymentRelocation.RepairDonePrefix + "3";
        yield return DeploymentRelocation.RepairMissingAfterMovePrefix + P; // #147, Repair after move without records
        yield return DeploymentRelocation.RepairSettingsDone;
        // Final ui-stores review, findings 2-4: Repair after move's rolled-back commit, the staged-commit lead-in, the
        // desktop opener, the update history and .NET's own file-system errors behind refused saves.
        yield return DeploymentRelocation.RepairNotSavedPrefix + $"Access to the path '{P}.rhi-tmp-0a1b' is denied.";
        yield return DeploymentRelocation.RepairNotSavedPrefix + Platform.Linux.Persistence.StagedFileCommit.StagedTwiceReason;
        yield return Platform.Linux.Persistence.StagedFileCommit.NothingChangedPrefix + E;
        yield return RHI.Platform.Linux.Paths.LinuxDesktopOpener.DirectoryMissingPrefix + "/jogos/Exemplo";
        yield return RHI.Platform.Linux.Paths.LinuxDesktopOpener.UnsupportedLinkPrefix + "ftp://exemplo";
        yield return RHI.Platform.Linux.Paths.LinuxDesktopOpener.NoOpenerMessage;
        yield return $"{ComponentUpdateLogStore.UnreadablePrefix}{P}{ComponentUpdateLogStore.UnreadableSuffix}";
        yield return $"{P} changed while it was being read. Nothing was changed; try again.";
        yield return AtomicFile.VerifyFailurePrefix + P + AtomicFile.TempLeftoverFragment + P + ".rhi-tmp-0a1b";
        yield return $"Access to the path '{P}' is denied.";
        yield return $"Could not find a part of the path '{P}'.";
        yield return $"Could not find file '{P}'.";
        yield return $"No space left on device : '{P}'";
        yield return $"Read-only file system : '{P}'";
        // ReShade setup extractor reasons (after its own prefix).
        foreach (var reason in new[]
                 {
                     "no ZIP archive found in the setup",
                     "the archive holds no ReShade64.dll or ReShade32.dll",
                     "ReShade64.dll: declared size over the limit (5 > 4 bytes)",
                     "the ReShade DLLs together declare more than the limit (9 > 8 bytes)",
                     "ReShade64.dll: holds more bytes than it declares (4)",
                     "ReShade64.dll: holds a different size than it declares (3 != 4)",
                     "ReShade64.dll: CRC-32 does not match the archive (corrupt download)"
                 })
            yield return ReShadeArchiveExtractor.SetupUnreadablePrefix + reason;
        // Downloaded-archive entry refusals (ReShadeArchiveExtractor.ExtractEntryAsync: nightly + Addon Manager zips).
        foreach (var reason in new[]
                 {
                     "ReShade64.dll: declared size over the limit (5 > 4 bytes)",
                     "Exemplo.addon64: holds more bytes than it declares (4)",
                     "Exemplo.addon64: holds a different size than it declares (3 != 4)",
                     "Exemplo.addon64: CRC-32 does not match the archive (corrupt download)",
                     $"{AtomicFile.SizeLimitPrefix}4{AtomicFile.SizeLimitMiddle}Exemplo.addon64"
                 })
            yield return ReShadeArchiveExtractor.ArchiveRefusedPrefix + reason;
        // w3/sync rows
        yield return NeuralRenderingRuntimeOwnership.RefusalFor("OptiScaler F5 DLSSNR");
        yield return OptiScalerPackageProvider.ArchivePathRefusedPrefix + @"..\evil.dll";
        yield return OptiScalerDeploymentService.NightlyPinnedCurrentPrefix + "20261004";
        yield return OptiScalerDeploymentService.NightlyPinnedNotUpdatedPrefix + "20261004";
        yield return OptiScalerDeploymentService.NightlyPinWrongChannelMessage;
        yield return OptiScalerDeploymentService.NightlyBuildInvalidPrefix + "2026-10-04";
        yield return OptiScalerPackageProvider.NightlyBuildMismatchPrefix + "nightly-20261013";
        // w3/journal rows
        yield return $"{LinuxDeploymentRecovery.RemovalUnmarkedPrefix}Access to the path '{P}' is denied.); nothing was changed.";
        yield return WineOverrideReapplyCompensator.DeferredPrefix + "dxgi (/prefixo)";
        yield return REFrameworkDeploymentService.StorageNotFoundPrefix + P + "/_storage_";
        yield return REFrameworkDeploymentService.StorageLinkRefusedPrefix + P + "/_storage_";
        yield return REFrameworkDeploymentService.StorageOutsideGameRefusedPrefix + P + "/_storage_";
        yield return REFrameworkDeploymentService.StorageWineRunningPrefix + P;
        yield return REFrameworkDeploymentService.StorageDeletedPrefix + P + "/_storage_";
        yield return REFrameworkDeploymentService.StoragePartlyDeletedPrefix + P + "/_storage_";
        yield return REFrameworkDeploymentService.StorageUnreadablePrefix + P + "/_storage_";
        yield return ShortFuseDeploymentService.UalKeptPrefix + E;
        yield return $"RE Framework removal failed and was rolled back: {E}";
        // Review w3-journal #4/#8: removal failures and the ShortFuse success lines this lane added or reworded.
        yield return $"{DofFixDeploymentService.RemovalRolledBackPrefix}{E}";
        yield return $"{RenoDxUpgradeDeploymentService.RemovalRolledBackPrefix}{E}";
        yield return $"{ShortFuseDeploymentService.RemovalFailedPrefix}{E}";
        yield return ShortFuseDeploymentService.RemovedMessage;
        yield return $"{ShortFuseDeploymentService.RemovedUalKeptMessage} {ShortFuseDeploymentService.UalKeptPrefix}{E}";
        yield return $"DLSS Enabler Standalone removal failed and was rolled back as far as possible: {E}";
        yield return $"Universal MFG removal failed and was rolled back as far as possible: {E}";
        yield return $"RTX 20 FG Unlock legado removal failed and was rolled back as far as possible: {E}";
        yield return $"Ultimate ASI Loader removal failed and was rolled back as far as possible: {E}";
        yield return $"ShortFuse install failed and was rolled back as far as possible: {E}";
        yield return $"ReLimiter removal failed: {E}";
        yield return $"Protected modified file: {P} no longer matches the RHI-managed hash, so it was not deleted.";
        yield return $"DLSS Enabler Standalone install rolled back: {E}";
        yield return $"Universal MFG update failed and was rolled back as far as possible: {E}";
        yield return $"Ultimate ASI Loader update failed: {E}";
        yield return $"ReShade Addon Manager failed: {E}";
        yield return $"Display Commander install failed: {E}";
        yield return $"RE Framework update rolled back: {E}{DeploymentScope.RollbackIncompletePrefix}{P}: rollback failed ({E}); every copy was kept.); every copy was kept.";
        // w3/scan rows
        yield return RHI.Core.Services.XboxWriteGate.BusyMessage;
        yield return RHI.Core.Services.XboxWriteGate.UnreadablePrefix + "/jogos/xbox/state/app/jobs.json";
        yield return RHI.Core.Services.XboxWriteGate.UnknownInstancePrefix + "/jogos/xbox/payloads/9ABC";
        yield return RHI.Core.Services.XboxWriteGate.EncryptedExecutableMessage;
        yield return RHI.Platform.Linux.Detection.Xbox.InstallerBreadcrumb.NewerSchemaPrefix + "/home/jogador/.local/share/app/instances/a.json";
        yield return RHI.Core.Services.PackageFileProtection.RefusalPrefix + "/jogos/pacotes/9ABC/Jogo.exe";
        // w3/parity rows
        yield return RHI.Platform.Linux.Network.NexusApiClient.UnreachableMessage;
        yield return RHI.Platform.Linux.Network.NexusDownloadService.NoLinkMessage;
        yield return RHI.Platform.Linux.Network.NexusApiClient.NoAccountMessage;
        yield return RHI.Platform.Linux.Network.NexusApiClient.OversizedMessage;
        yield return RHI.Platform.Linux.Network.NexusApiClient.AnsweredPrefix + "503.";
        yield return RHI.Platform.Linux.Settings.ShaderProfileArchive.TooLargeMessage;
        yield return RHI.Platform.Linux.Settings.ShaderProfileArchive.NotAProfileMessage;
        yield return RHI.Platform.Linux.Settings.ShaderProfileArchive.UnreadableProfileMessage;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.NoOwnerMessage;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.OwnerModifiedPrefix + "ShortFuse";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.AlreadyInstalledMessage;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.NotInstalledMessage;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.ForeignFilePrefix + "/jogos/Exemplo/nvngx_dlssnr.ini";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.ScalerModifiedPrefix + "/jogos/Exemplo/nvngx_dlssnr.dll";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.DownloadFailedPrefix + RHI.Platform.Linux.Network.NexusApiClient.UnreachableMessage;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.PackageIncompleteMessage;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.InstalledPrefix + "ShortFuse (v1.0.6).";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.RemovedMessage;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.FailedRolledBackPrefix + "No space left on device : '/jogos'";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.OwnerBlockedPrefix + "DLSS5 Feeder";
        // w4/encore (#158) rows
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NotYetConfirmedNote;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NativeLinuxMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeedsX64Message;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.ApiMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeedsDlssMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.PrefixMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.BinaryFolderMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.AlreadyInstalledMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NotInstalledMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NameLockedMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NameNotOfferedPrefix + "dxgi.dll";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NameHeldPrefix + "dinput8.dll";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.KeptCopyInTheWayPrefix + "/jogos/Exemplo/version.dll.original";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.DownloadFailedPrefix + "No space left on device : '/jogos'";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeuralRenderingUnavailableMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeuralRenderingMismatchMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeuralRenderingNeedsDlssListMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.FailedRolledBackPrefix + "No space left on device : '/jogos'";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.ModifiedPrefix + "/jogos/Exemplo/version.dll";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.MissingPrefix + "/jogos/Exemplo/version.dll";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.InstalledPrefix + "version.dll";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.InstalledPrefix + "version.dll" + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.InstalledWithNeuralRenderingSuffix;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.RemovedMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.RemovedMessage + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.KeptCopyMissingPrefix + "/jogos/Exemplo/version.dll.original";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.UpToDatePrefix + "1.0.0-rc.3";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.UpdatedPrefix + "1.0.0-rc.4";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.UpdateAvailablePrefix + "1.0.0-rc.3 → 1.0.0-rc.4";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.LatestPrefix + "1.0.0-rc.3";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.CheckFailedPrefix + "No space left on device : '/jogos'";
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeuralRenderingOnMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeuralRenderingOffMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NeuralRenderingAlreadyMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.SavedWithNeuralRenderingMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.SavedWithoutNeuralRenderingMessage;
        yield return RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.SavedNamePrefix + "winmm.dll";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.RtxEncoreOwnerMessage;
        yield return RHI.Platform.Linux.Deployment.NeuralRenderingRuntimeOwnership.RefusalFor("RTX Encore");
        // w4/encore review minor 4: the reasons that follow the download/rollback prefixes.
        foreach (var head in new[] { RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.DownloadFailedPrefix, RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.FailedRolledBackPrefix })
        {
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.ReleaseListShapeMessage;
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NoReleaseMessage;
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NoPublishedDigestPrefix + "v1.0.0-rc.4";
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.TagNotAVersionPrefix + "nightly";
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.SumsNoEntryPrefix + "RTX-Encore-v1.0.0-rc.4.zip";
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.SumsMismatchPrefix + "RTX-Encore-v1.0.0-rc.4.zip";
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.PayloadNotAtRootMessage;
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.TooSmallMessage;
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NotX64Message;
            yield return head + RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService.NoVersionResourceMessage;
        }
        // w3/vmsplit rows
        // w3/dllnames rows
        yield return $"Control UE restore incomplete; RenoDX was kept: nvngx_dlss.dll not changed: {ControlUePostInstallService.SnapshotInTheWayPrefix}{P}.rhi-control-previous";
        yield return $"RenoDX installed: Control → {P} · Control UE: nvngx_dlss.dll was changed outside RHI and was left as it is.{ControlUePostInstallService.SnapshotKeptPrefix}{P}.rhi-control-previous";
        yield return $"RenoDX installed: Control → {P} · Control UE: nvngx_dlss.dll{ControlUePostInstallService.UserChangeKeptNote} renderer.ini{ControlUePostInstallService.UserChangeKeptNote}";
        yield return ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + "RenoDX HDR";
        yield return ReShadeDeploymentService.NoAddonsUpdateAvailablePrefix + "6.7.3 → 6.8.0.";
        yield return ReShadeDeploymentService.NoAddonsCurrentPrefix + "6.8.0.";
        yield return ReShadeDeploymentService.NoAddonsLinkMissingMessage;
        yield return ReShadeDeploymentService.NameNotForBitnessPrefix + "ReShade64.dll";
        yield return ReShadeDeploymentService.OtherReShadePresentPrefix + $"{P}/dxgi.dll";
        yield return LinuxManagedComponentService.ExternalReShadeBlocksDependencyPrefix + $"Luma Framework: {P}/dxgi.dll";
        yield return Dx9TranslationReShadeCoordinator.NeedsReShadeAsD3d9Prefix + "dxgi.dll";
        yield return ReShadeDeploymentService.NameHeldByRhiComponentPrefix + "dinput8.dll (RE Framework)";
        yield return "Luma Framework installation rolled back: " + Dx9TranslationReShadeCoordinator.NeedsReShadeAsD3d9Prefix + "dxgi.dll";
        yield return OptiScalerDeploymentService.RemovalFailedPrefix + $"Could not restore {P}/dxgi.dll";
        yield return ReShadeDeploymentService.RemovalFailedPrefix + $"Could not restore {P}/dxgi.dll";
        yield return OptiScalerDeploymentService.ProxyNameNotOfferedPrefix + "d3d9.dll";
        yield return "RenoDX installed: Control → " + P + " · " + ControlUeFrameGenerationService.SetUpPrefix + "20261004."
            + ControlUeFrameGenerationService.StackFailedPrefix + P
            + ControlUeFrameGenerationService.SettingsFailedPrefix + P
            + ControlUeFrameGenerationService.ReShadeNameNote + "d3d12.dll"
            + ControlUeFrameGenerationService.HdrSetPrefix + "1";
        yield return ControlUeFrameGenerationService.NotSetUpPrefix + ControlUeFrameGenerationService.OptiScalerElsewherePrefix + "Stable 0.9.4 / dxgi.dll"
            + ControlUeFrameGenerationService.HdrNotSetPrefix + ControlUeFrameGenerationService.HdrNoIniMessage;
        yield return ControlUeFrameGenerationService.HdrNotSetPrefix + ControlUeFrameGenerationService.HdrNoRenoDxMessage;
        yield return ControlUeFrameGenerationService.HdrNotSetPrefix + ControlUeFrameGenerationService.HdrUserValueKeptMessage;
        yield return P + OptiScalerDeploymentService.OptiPatcherDeployedPrefix + "c3f758abcd2491a950f384efbed579152145bdca";
        yield return P + OptiScalerDeploymentService.OptiPatcherSkippedPrefix + OptiScalerDeploymentService.OptiPatcherForeignMessage;
        yield return P + OptiScalerDeploymentService.OptiPatcherSkippedPrefix + OptiPatcherProvider.ReleaseIncompleteMessage;
        yield return P + OptiScalerDeploymentService.OptiPatcherSkippedPrefix + OptiPatcherProvider.NotWindowsDllMessage;
        yield return OptiScalerDeploymentService.OptiPatcherUpdatePrefix + "c3f758a → 4662cb0";
        yield return P + OptiScalerIniTemplates.NotePrefix + "/home/u/.config/rhi-linux/inis/OptiScaler.amd-dlss.ini";
        yield return P + OptiScalerIniTemplates.UnavailablePrefix + P;
        yield return "OptiScaler install failed: " + OptiScalerDeploymentService.NameHeldByRhiComponentPrefix + "winmm.dll (Ultimate ASI Loader)";
        yield return OptiScalerDeploymentService.OptiScalerAlreadyPresentPrefix + "DXGI.dll";
        yield return OptiScalerDeploymentService.OptiScalerHeldByRhiComponentPrefix + "version.dll (DLSS Enabler)";
        yield return OptiScalerDeploymentService.FolderUnreadablePrefix + P;
        // #170
        yield return "ReShade 6.8.0 [NoAddons] applied as dxgi.dll; no RHI shader packs. Existing ReShade.ini customizations are preserved." + ReShadeAddonSwitch.TurnedOffPrefix + "2.";
        yield return "ReShade 6.8.0 [Stable] applied as dxgi.dll; no RHI shader packs. Existing ReShade.ini customizations are preserved." + ReShadeAddonSwitch.TurnedOnPrefix + "2.";
        yield return ReShadeAddonSwitch.NameTakenPrefix + P;
        yield return ReShadeAddonSwitch.OffNote;
        yield return ReShadeAddonSwitch.HeldPrefix + "ReLimiter";
        yield return "ReShade 6.8.0 [Stable] applied as dxgi.dll; no RHI shader packs. Existing ReShade.ini customizations are preserved." + ReShadeAddonSwitch.ManagerUnreadableNote;
        // #172
        yield return P + LinuxManagedComponentService.OptiScalerSavedNameSkippedPrefix + "winmm.dll (Ultimate ASI Loader)";
        yield return ControlUeFrameGenerationService.NotSetUpPrefix + OptiScalerDeploymentService.OptiScalerAlreadyPresentPrefix + "dxgi.dll"
            + ControlUeFrameGenerationService.HdrSetPrefix + "0";
        // w5/update-policy rows
        // #177 Follow newest DLSS / Streamline (update history notes and per-file failures)
        yield return DlssAutoFollowService.AutomaticDlssNote;
        yield return DlssAutoFollowService.AutomaticStreamlineNote;
        yield return DlssAutoFollowService.RefusedPrefix + DeploymentRelocation.GuardPrefix + "/jogos/Antigo" + DeploymentRelocation.GuardSuffix;
        yield return DlssAutoFollowService.DownloadFailedPrefix + DlssAutoFollowService.NotInManifestPrefix + "DLSS SR (nvngx_dlss.dll) 310.5.2";
        yield return DlssAutoFollowService.SwapFailedMessage;
        // #178 update inclusion
        yield return LinuxUpdateAllService.LeftOutMessage;
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // #181
        yield return UserIniService.NativeGamePrefix + "Celeste";
        yield return UserIniService.NoFolderPrefix + P;
        yield return UserIniService.NotManagedPrefix + "Celeste";
        yield return UserIniService.DeployedPrefix + P;
        yield return UserIniService.DeployedBackupPrefix + P;
        yield return UserIniService.ChangedPrefix + P;
        yield return UserIniService.RestoredPrefix + P;
        yield return UserIniService.NoBackupPrefix + P;
        yield return UserIniService.RestoreChangedPrefix + P;
        yield return UserIniTemplates.TooLargePrefix + P;
        yield return IniLines.LinkRefusalPrefix + P;
        yield return IniLines.LinkRefusalPrefix + P + IniLines.NotPutBackPrefix + P;
        // #180
        yield return ReShadePresetService.NotAPresetPrefix + "notes.ini";
        yield return ReShadePresetService.TooLargePrefix + P;
        yield return ReShadePresetService.NoManagedReShadePrefix + "Celeste";
        yield return ReShadePresetService.NameTakenPrefix + P;
        yield return ReShadePresetService.InstalledPrefix + P;
        yield return ReShadePresetService.RemovedPrefix + P;
        yield return ReShadePresetService.KeptChangedPrefix + P;
        yield return ReShadePresetService.NotInstalledPrefix + "Mine.ini";
        // #189
        yield return LumaSettingsService.NotManagedPrefix + "Celeste";
        yield return LumaSettingsService.NoEngineIniPrefix + "Celeste";
        // #182
        yield return RenoDxPeakNits.NotManagedPrefix + "Celeste";
        yield return RenoDxPeakNits.NoReShadeIniPrefix + "Celeste";
        // w5/shell-launch rows
        yield return RHI.Platform.Linux.Hdr.DisplayModeSwitcher.ToolFailedPrefix + "exit code 1";
        yield return RHI.Platform.Linux.Hdr.DisplayModeSwitcher.NoModePrefix + "2560x1440";
        yield return RHI.Platform.Linux.Hdr.DisplayModeSwitcher.ToolMissingPrefix + "kscreen-doctor";
        yield return RHI.Platform.Linux.Hdr.DisplayModeSwitcher.StateUnsavedPrefix + "/state/display-mode.json";
        yield return RHI.Platform.Linux.Hdr.DisplayModeSwitcher.UnsafeAnswer;
        // w6/crash rows
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.CurrentMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.UpdateAvailablePrefix + "Generic";
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.CouldNotCheckPrefix + RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.ListUnavailableReason;
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.CouldNotCheckPrefix + RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.NoFilesRecordedReason;
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.CouldNotCheckPrefix + RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.NotInListPrefix + "generic.addon64";
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.CouldNotCheckPrefix + RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.NoValidatorPrefix + "Generic";
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.UpdatedPrefix + "2";
        yield return "ReShade Addon Manager is not managed by RHI Linux.";
        yield return ReShadeAddonSwitch.HeldPrefix + RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.DisplayName;
        yield return RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.ReconcileFailedPrefix + P;
        // w5/reshade-core rows
        // #92 ReShade as a Vulkan layer
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.ApiSwitchLayerInstalledMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.ApiSwitchDllInstalledMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.NotVulkanTargetMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.PrefixNotStartedMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.UnknownRunnerMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.DllReShadeInstalledMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.NotOnRequestMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.RowNote;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.RemovedMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.IncompleteRecordMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.RunnerTooOldPrefix + "GE-Proton7-55";
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.GameShipsLoaderPrefix + P;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LayerFolderTakenPrefix + P;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.InstalledPrefix + "6.8.0" + RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderCheckSkippedNote;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.UpdatedPrefix + "6.8.1" + RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderUpdatedNote;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.FailedRolledBackPrefix + RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.ModifiedFilePrefix + P;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.NeedsDllReShadePrefix + "OptiScaler";
        yield return "ReShade source preparation failed: " + RHI.Platform.Linux.Deployment.VulkanRuntimeProvider.LatestUnavailableMessage;
        yield return "ReShade source preparation failed: " + RHI.Platform.Linux.Deployment.VulkanRuntimeProvider.LoaderMissingPrefix + "x86/vulkan-1.dll";
        yield return "ReShade source preparation failed: " + RHI.Platform.Linux.Deployment.VulkanRuntimeProvider.LoaderInvalidPrefix + "x64/vulkan-1.dll";
        yield return RHI.Platform.Linux.Deployment.WineRegistryEditor.MissingSystemHiveMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.UpdatedPrefix + "6.8.1" + RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderRestoredNote;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.Win32PrefixMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.DllOnlyOnVulkanGameMessage;
        yield return RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderMissingNotRestoredPrefix + RHI.Platform.Linux.Deployment.VulkanRuntimeProvider.LatestUnavailableMessage;
        // w7/smalls rows
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.CouldNotCheckPrefix + RHI.Platform.Linux.Network.NexusApiClient.UnreachableMessage; // #199
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.UpToDatePrefix + "v1.0.6";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.UpdateAvailablePrefix + "v1.0.7";
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.UpdatedPrefix + "v1.0.7." + RHI.Platform.Linux.Deployment.NrCostScalerService.IniKeptNote;
        yield return RHI.Platform.Linux.Deployment.NrCostScalerService.NotUpdatedModifiedPrefix + "/jogos/Exemplo/nvngx_dlssnr.dll";
        // w7/reshade rows
        // w7/shader-sets rows
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void WaveOneMessage_HasAPortuguesePresentation_AndStaysAsIsInEnglish(string message)
    {
        var pt = UiLocalizationService.PresentFor("pt-BR", message);

        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt);
        // "no" is also Portuguese ("no instalador"); every other marker is English only.
        var english = UiLocalizationService.EnglishMarkersIn(pt).Where(w => !w.Equals("no", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(english.Length == 0, $"English left ({string.Join(", ", english)}): {pt}");
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void UnlocalizedFallback_NamesTheAppLog_AndClaimsNothingAboutTheOperation(string language)
    {
        var unmapped = language == "pt-BR"
            ? "The component failed because the remote server is not available right now"
            : "O componente falhou porque o servidor remoto não está disponível agora para o jogo";

        var shown = UiLocalizationService.PresentFor(language, unmapped);

        Assert.Equal(UiLocalizationService.UnlocalizedFallbackFor(language), shown);
        Assert.Contains(UiExceptionGuard.Log.FilePath, shown, StringComparison.Ordinal);
        Assert.EndsWith("rhi.log.", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("preserv", shown, StringComparison.OrdinalIgnoreCase);
    }
}
