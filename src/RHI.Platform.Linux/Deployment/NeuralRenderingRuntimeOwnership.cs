using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// The <c>nvngx_dlssnr.dll</c> sharing rule (#157): one place for every component that puts the DLSS Neural Rendering
/// runtime into a game folder. Parity's NR Cost Scaler (#96) and later RTX Encore (#158) adopt it.
/// <list type="number">
/// <item><b>One owner.</b> Per game, at most one RHI component owns <c>nvngx_dlssnr.dll</c>: ShortFuse, DLSS5 Feeder
/// (any Feeder record: Full deploys the runtime, and an add-on-only record is treated the same, as before), OptiScaler
/// DLSS NR or OptiScaler F5 DLSSNR, or RTX Encore while its record lists the file (#158: only with its "Neural Rendering
/// file" choice; without it RTX Encore uses whatever runtime is there and is no owner). OptiScaler Stable and Nightly never
/// deploy it and are no owners.</item>
/// <item><b>Refused, not shared.</b> Installing an owner while another owner is recorded is refused before any download,
/// file or registry change, and the refusal names the current owner (<see cref="RefusalFor"/>). There is no
/// co-ownership ledger: the user removes one owner first.</item>
/// <item><b>The owner's record decides.</b> Only the owner's own record (path and hash) governs updating, swapping the NR
/// version of, or removing the file; a file that no longer matches that hash is protected as modified.</item>
/// <item><b>Layers are not owners.</b> A component that wraps the owner's runtime (the NR Cost Scaler: it moves
/// <c>nvngx_dlssnr.dll</c> to <c>nvngx_dlssnr_real.dll</c> and puts its proxy in its place) needs an owner, refuses
/// when the owner's file no longer matches the owner's recorded hash, keeps the moved file under its own record, and
/// must be removed before the owner updates, swaps or removes the runtime (the owner refuses while a layer is
/// recorded). Removing the layer restores the owner's file byte for byte, so the owner's hash check passes again.</item>
/// </list>
/// </summary>
public static class NeuralRenderingRuntimeOwnership
{
    public const string DllName = "nvngx_dlssnr.dll";

    /// <summary>Refusal and row note when another component owns the runtime; the owner's name follows.</summary>
    public const string OwnedByPrefix = "nvngx_dlssnr.dll already belongs to another RHI component for this game; remove it first: ";

    /// <summary>OptiScaler variants that deploy <c>nvngx_dlssnr.dll</c> (and its forwarder): DLSS NR and F5 DLSSNR.</summary>
    public static bool OptiScalerVariantOwnsRuntime(OptiScalerVariant variant)
        => variant is OptiScalerVariant.DlssNr or OptiScalerVariant.F5;

    /// <summary>\"OptiScaler DLSS NR\" / \"OptiScaler F5 DLSSNR\" when <paramref name="optiScaler"/> owns the runtime, else null.</summary>
    public static string? OptiScalerOwner(LinuxDeploymentRecord? optiScaler)
        => optiScaler is not null && OptiScalerVariantOwnsRuntime(OptiScalerDeploymentService.GetInstalledVariant(optiScaler))
            ? "OptiScaler " + OptiScalerDeploymentService.VariantLabel(OptiScalerDeploymentService.GetInstalledVariant(optiScaler))
            : null;

    public static string? OptiScalerOwner(LinuxDeploymentRecordStore records, DetectedGame game)
        => OptiScalerOwner(records.Find(game, ManagedComponentKind.OptiScaler));

    /// <summary>
    /// The component (other than <paramref name="requester"/>) that owns <c>nvngx_dlssnr.dll</c> for the game, by display
    /// name, or null when none does. A component that would deploy the runtime calls this before its first download.
    /// </summary>
    public static string? OwnerOtherThan(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind requester)
        => OwnerRecordOtherThan(records, game, requester)?.Name;

    /// <summary>
    /// The owner (other than <paramref name="requester"/>) by display name, with its record, or null. Layers (the NR Cost
    /// Scaler) ask this with their own kind to find the record whose runtime they wrap.
    /// </summary>
    public static (string Name, LinuxDeploymentRecord Record)? OwnerRecordOtherThan(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind requester)
    {
        if (requester != ManagedComponentKind.OptiScaler && records.Find(game, ManagedComponentKind.OptiScaler) is { } optiScalerRecord
            && OptiScalerOwner(optiScalerRecord) is { } optiScaler) return (optiScaler, optiScalerRecord);
        if (requester != ManagedComponentKind.Dlss5Feeder && records.Find(game, ManagedComponentKind.Dlss5Feeder) is { } feeder) return ("DLSS5 Feeder", feeder);
        if (requester != ManagedComponentKind.ShortFuse && records.Find(game, ManagedComponentKind.ShortFuse) is { } shortFuse) return ("ShortFuse", shortFuse);
        if (requester != ManagedComponentKind.RtxEncore && records.Find(game, ManagedComponentKind.RtxEncore) is { } encore
            && ListsRuntime(encore)) return (RtxEncoreOwnerName, encore);
        return null;
    }

    /// <summary>
    /// Whether <paramref name="component"/>'s record for the game owns the runtime under rule 1: ShortFuse and DLSS5 Feeder
    /// records always do, OptiScaler only with a variant that deploys it (DLSS NR, F5 DLSSNR).
    /// </summary>
    public static bool ComponentOwnsRuntime(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind component)
        => component switch
        {
            ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder => true,
            ManagedComponentKind.OptiScaler => OptiScalerOwner(records, game) is not null,
            ManagedComponentKind.RtxEncore => records.Find(game, ManagedComponentKind.RtxEncore) is { } encore && ListsRuntime(encore),
            _ => false
        };

    /// <summary>
    /// Every record of the game that owns <c>nvngx_dlssnr.dll</c> under rule 1 (normally at most one): OptiScaler DLSS NR /
    /// F5 DLSSNR, DLSS5 Feeder, ShortFuse, RTX Encore with its Neural Rendering file. Their files are never direct DLSS
    /// swap/restore targets (<see cref="LinuxDlssDefaultsService.DetectTargets"/>), so the DLSS panel, Quick Apply, Batch
    /// Deploy and Restore can never replace an owner's runtime nor consume the <c>.original</c> it puts back on removal.
    /// </summary>
    public static IReadOnlyList<LinuxDeploymentRecord> OwnerRecords(LinuxDeploymentRecordStore records, DetectedGame game)
        => OwnerKinds
            .Where(kind => ComponentOwnsRuntime(records, game, kind))
            .Select(kind => records.Find(game, kind))
            .OfType<LinuxDeploymentRecord>()
            .ToArray();

    private static readonly ManagedComponentKind[] OwnerKinds =
        [ManagedComponentKind.OptiScaler, ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.ShortFuse, ManagedComponentKind.RtxEncore];

    public static string RefusalFor(string owner) => OwnedByPrefix + owner;

    /// <summary>RTX Encore's name as an owner (#158).</summary>
    public const string RtxEncoreOwnerName = "RTX Encore";

    /// <summary>RTX Encore when its record owns the runtime (its Neural Rendering file is installed), else null.</summary>
    public static string? RtxEncoreOwner(LinuxDeploymentRecordStore records, DetectedGame game)
        => ComponentOwnsRuntime(records, game, ManagedComponentKind.RtxEncore) ? RtxEncoreOwnerName : null;

    /// <summary>Whether <paramref name="record"/> lists <c>nvngx_dlssnr.dll</c> among its files.</summary>
    public static bool ListsRuntime(LinuxDeploymentRecord record)
        => record.Files.Any(file => Path.GetFileName(file.Path).Equals(DllName, StringComparison.OrdinalIgnoreCase));
}
