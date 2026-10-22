using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    public string CustomAddonRoot => _addonManager.CustomRoot;

    public ComponentOperationResult ImportCustomAddonFile(string sourcePath)
        => _addonManager.ImportCustomAddonFile(sourcePath);

    /// <summary>#195: a dropped or watched RenoDX add-on file installed as the game's RenoDX mod (install guards, update history).</summary>
    public Task<ComponentOperationResult> InstallLocalRenoDxAddonAsync(DetectedGame game, string addonPath, CancellationToken cancellationToken = default)
        => (InstallGuard(game) ?? AddonOffGuard(game, ManagedComponentKind.RenoDX)) is { } guard
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.RenoDX, ComponentUpdateAction.Install, () => _renodx.InstallLocalAddonAsync(game, addonPath, cancellationToken));

    /// <summary>#195: a dropped ReShade add-on that is not RenoDX's, installed into the game.</summary>
    public const string DroppedAddonInstalledPrefix = "Add-on installed for this game; Shaders/Add-ons lists it (untick it there to take it out): ";
    public const string DroppedAddon32BitOn64BitMessage = "This add-on is 32-bit and the game is 64-bit; it needs the add-on's .addon64 file.";
    public const string DroppedAddon64BitOn32BitMessage = "This add-on is 64-bit and the game is 32-bit; it needs the add-on's .addon32 file.";

    /// <summary>w7 follow-up B10: the game has no ReShade with add-on support (PT-BR pair); nothing was copied.</summary>
    public const string DroppedAddonNeedsReShadeMessage =
        "This game has no ReShade with add-on support, so the add-on was not installed and nothing was copied. Install ReShade (not the No Addons build) on this game first.";

    /// <summary>
    /// Why a dropped non-RenoDX add-on cannot go into <paramref name="game"/> — no ReShade in its folder (the Addon
    /// Manager's own test), or RHI's ReShade is the No Addons build — or null. The drop dialog lists only games where this
    /// is null; the install asks it again before anything is copied (re-review smalls 6).
    /// </summary>
    public string? DroppedAddonRefusal(DetectedGame game)
        => string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory)
           || !GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory)
           || ReShadeDeploymentService.LacksAddonSupport(_records.Find(game, ManagedComponentKind.ReShade))
            ? DroppedAddonNeedsReShadeMessage
            : null;

    /// <summary>
    /// #195 (upstream <c>ProcessDroppedAddon</c> for an add-on that is not RenoDX's: copied into the game beside it): the
    /// file is imported into Custom Add-ons (never over a different one) and added to the game's Shaders/Add-ons selection,
    /// whose journaled apply places it, records it and takes it out again when it is unticked. RenoDX is left alone.
    /// </summary>
    public async Task<ComponentOperationResult> InstallDroppedAddonForGameAsync(DetectedGame game, string addonPath, CancellationToken cancellationToken = default)
    {
        if (InstallGuard(game) is { } guard) return guard;
        if (DroppedAddonRefusal(game) is { } noReShade) return ComponentOperationResult.Fail(noReShade); // before the Custom Add-ons copy
        if (!string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) && File.Exists(addonPath)
            && GameArchitecture.Of(game) is var architecture and (PeArchitecture.X86 or PeArchitecture.X64)
            && PeArchitectureDetector.Detect(addonPath) is var addon and (PeArchitecture.X86 or PeArchitecture.X64) && addon != architecture)
            return ComponentOperationResult.Fail(addon == PeArchitecture.X86 ? DroppedAddon32BitOn64BitMessage : DroppedAddon64BitOn32BitMessage);
        var imported = _addonManager.ImportCustomAddonFile(addonPath);
        if (!imported.Success || imported.ChangedFiles is not [var copy, ..]) return imported;
        var id = "custom-" + Path.GetFileNameWithoutExtension(copy);
        // w7 integration (#195 × #179): the add-ons the game gets now plus this one, saved as the game's own list ("Choose for
        // this game") when that differs from what it gets, so a later global edit never takes the add-on out again and the
        // global set itself is never edited. Without the shader-set store the recorded selection is the game's list.
        var selected = EffectiveAddons(game) is { HeldByNoAddons: false } effective
            ? effective.Ids
            : (await _addonManager.GetSnapshotAsync(game, cancellationToken).ConfigureAwait(false)).SelectedIds;
        IReadOnlyList<string> wanted = selected.Contains(id, StringComparer.OrdinalIgnoreCase) ? selected : [.. selected, id];
        if (RecordExplicitAddonChoice(game, wanted) is { } notSaved) return ComponentOperationResult.Fail(notSaved);
        var applied = await ApplyReShadeAddonSelectionAsync(game, wanted, cancellationToken).ConfigureAwait(false);
        var placed = Path.Combine(RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory!), Path.GetFileName(copy));
        return applied.Success ? ComponentOperationResult.Ok(DroppedAddonInstalledPrefix + placed, copy, placed) : applied;
    }

    /// <summary>#195: the game's RenoDX record (the drop dialog names the add-on it replaces).</summary>
    public LinuxDeploymentRecord? GetRenoDxRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.RenoDX);

    /// <summary>w10 follow-up D16: RHI's Luma record of <paramref name="game"/> (what a Luma package from an archive replaces).</summary>
    public LinuxDeploymentRecord? GetLumaRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.LumaFramework);

    /// <summary>#195: the add-on file name the RenoDX catalogue matched for the game this session (its snapshot), else null.</summary>
    public string? RenoDxCatalogueFileName(DetectedGame game)
        => _renodx.TryGetPreparedCompatibility(game, out var mod) && mod?.UrlFor(GameArchitecture.Of(game)) is { } url
           && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : null;

    public async Task<ComponentOperationResult> InstallLocalLumaPackageAsync(
        DetectedGame game,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        if (guard is not null) return guard;
        return await Track(game, ManagedComponentKind.LumaFramework, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.LumaFramework,
            indexedFiles: null,
            () => _lumaFramework.InstallLocalPackageAsync(game, packagePath, cancellationToken),
            cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>#216: where dropped and watched archives get their private folders (RHI's cache; tests point it elsewhere).</summary>
    public string DroppedArchiveRoot { get; set; } = DroppedArchiveExtractor.DefaultRoot;

    /// <summary>#216: a dropped or watched archive copied, listed and checked (<see cref="DroppedArchiveExtractor"/>).</summary>
    public Task<DroppedArchiveInspection> InspectDroppedArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
        => DroppedArchiveExtractor.InspectAsync(archivePath, DroppedArchiveRoot, cancellationToken);

    /// <summary>Fixed start of an add-on RHI could not take out of an archive (PT-BR pair); the reason follows.</summary>
    public const string ArchiveExtractFailedPrefix = "The add-on could not be taken out of the archive, so nothing was installed: ";

    /// <summary>Test seam: stands in for the Luma package install an archive's Luma add-on takes.</summary>
    internal Func<DetectedGame, string, CancellationToken, Task<ComponentOperationResult>>? ArchiveLumaInstallForTesting { get; set; }

    /// <summary>
    /// #216 (upstream <c>ProcessDroppedArchive</c>): installs <paramref name="addon"/> from an inspected archive exactly as
    /// the same file dropped on RHI. A RenoDX add-on → <see cref="InstallLocalRenoDxAddonAsync"/>, another add-on →
    /// <see cref="InstallDroppedAddonForGameAsync"/>, with their refusals (asked again here before anything is extracted);
    /// a Luma add-on → the whole package to <see cref="InstallLocalLumaPackageAsync"/>: RHI's copy of a <c>.zip</c>, or a
    /// <c>.7z</c>/<c>.rar</c> taken out whole (bounded, checked) and zipped again in RHI's private folder
    /// (<see cref="DroppedArchive.PackageAsZipAsync"/>; the Luma install reads a <c>.zip</c>). The user's file is never
    /// read again. Everything is taken out before the install's first game write (Cancel still stops it) and removed
    /// afterwards, whatever happened. The install's own result is returned unchanged (its landed note included).
    /// </summary>
    public async Task<ComponentOperationResult> InstallArchivedAddonAsync(DetectedGame game, DroppedArchive archive, DroppedArchiveAddon addon,
        CancellationToken cancellationToken = default)
    {
        if (addon.Kind == DroppedArchiveAddonKind.Luma)
        {
            if (InstallGuard(game) is { } lumaGuard) return lumaGuard; // before anything is taken out
            string package;
            try
            {
                package = await archive.PackageAsZipAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                RHI.Core.Services.AppLog.Warn("dropped-archive", $"Could not take the Luma package out of {archive.ArchiveName}", ex);
                return ComponentOperationResult.Fail(ArchiveExtractFailedPrefix + ex.Message);
            }
            try
            {
                return await (ArchiveLumaInstallForTesting ?? InstallLocalLumaPackageAsync)(game, package, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!archive.IsZip) DroppedArchiveExtractor.DeleteFolder(Path.GetDirectoryName(package)!);
            }
        }
        var refusal = addon.Kind == DroppedArchiveAddonKind.RenoDx
            ? InstallGuard(game) ?? AddonOffGuard(game, ManagedComponentKind.RenoDX)
            : InstallGuard(game) ?? (DroppedAddonRefusal(game) is { } noReShade ? ComponentOperationResult.Fail(noReShade) : null);
        if (refusal is not null) return refusal;
        string extracted;
        try
        {
            extracted = await archive.ExtractAsync(addon, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("dropped-archive", $"Could not extract {addon.Member} from {archive.ArchiveName}", ex);
            return ComponentOperationResult.Fail(ArchiveExtractFailedPrefix + ex.Message);
        }
        try
        {
            return addon.Kind == DroppedArchiveAddonKind.RenoDx
                ? await InstallLocalRenoDxAddonAsync(game, extracted, cancellationToken).ConfigureAwait(false)
                : await InstallDroppedAddonForGameAsync(game, extracted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DroppedArchiveExtractor.DeleteFolder(Path.GetDirectoryName(extracted)!);
        }
    }
}
