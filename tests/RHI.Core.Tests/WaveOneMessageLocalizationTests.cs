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
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        yield return RHI.Platform.Linux.Network.NexusApiClient.UnreachableMessage;
        yield return RHI.Platform.Linux.Network.NexusDownloadService.NoLinkMessage;
        yield return RHI.Platform.Linux.Network.NexusApiClient.NoAccountMessage;
        yield return RHI.Platform.Linux.Network.NexusApiClient.OversizedMessage;
        yield return RHI.Platform.Linux.Network.NexusApiClient.AnsweredPrefix + "503.";
        yield return RHI.Platform.Linux.Settings.ShaderProfileArchive.TooLargeMessage;
        yield return RHI.Platform.Linux.Settings.ShaderProfileArchive.NotAProfileMessage;
        yield return RHI.Platform.Linux.Settings.ShaderProfileArchive.UnreadableProfileMessage;
        // w3/vmsplit rows
        // w3/dllnames rows
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
