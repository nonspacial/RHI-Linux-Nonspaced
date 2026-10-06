using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// In-place swap of the nvngx_dlssnr.dll owned by an orchestrated Neural Rendering method
/// (ShortFuse, DLSS5 Feeder). Mirrors upstream RHI 2.7.6 "NR DLL version swap while installed":
/// only the NR DLL changes and the owning record keeps tracking it, so removal still restores
/// whatever was there before the method was installed.
/// </summary>
internal static class NeuralRenderingDllSwap
{
    public const string DllName = "nvngx_dlssnr.dll";
    public const string MetaDllVersion = "NeuralRendering.DllVersion";

    public static async Task<ComponentOperationResult> SwapAsync(
        LinuxDeploymentRecordStore records,
        DlssManagerService dlss,
        DetectedGame game,
        ManagedComponentKind owner,
        string displayName,
        string? version,
        CancellationToken cancellationToken)
    {
        var record = records.Find(game, owner);
        if (record is null)
            return ComponentOperationResult.Fail($"{displayName} is not managed by RHI Linux.");
        var file = ManagedFileSwap.FindFile(record, DllName);
        if (file is null)
            return ComponentOperationResult.Fail($"{displayName} does not own {DllName} for this game; nothing was swapped.");

        (string Path, string Version)? cached;
        try
        {
            cached = await dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"{DllName} download failed; nothing was changed: {ex.Message}");
        }
        if (cached is not { } source)
            return ComponentOperationResult.Fail("The DLSS manifest does not provide a Neural Rendering runtime.");

        // A pin that is no longer published falls back to the newest entry; report what was used.
        var fallback = !string.IsNullOrWhiteSpace(version) &&
                       !string.Equals(version, source.Version, StringComparison.OrdinalIgnoreCase)
            ? $"{DllName} {version} is no longer published; using {source.Version}. "
            : "";
        try
        {
            var changed = await ManagedFileSwap.ReplaceAsync(record, [(file.Path, source.Path)], updated =>
            {
                updated.Metadata[MetaDllVersion] = source.Version;
                records.Upsert(updated);
            }, cancellationToken).ConfigureAwait(false);
            return ComponentOperationResult.Ok(fallback + (changed == 0
                ? $"{displayName}: {DllName} already matches {source.Version}."
                : $"{displayName}: {DllName} swapped in place to {source.Version}. Nothing else was changed."), file.Path);
        }
        catch (ManagedFileRestoreException ex)
        {
            return ComponentOperationResult.Fail($"{displayName}: {DllName} swap failed and the previous DLL could not be restored: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"{displayName}: {DllName} swap failed; the previous DLL was kept: {ex.Message}");
        }
    }
}
