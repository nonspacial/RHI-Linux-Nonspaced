using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #289 (upstream 2.9.3 <c>UpdateHistory.Record</c>): the one hook a change made outside the managed component path
/// (Install / Update / Remove, which <see cref="ComponentUpdateRecorder"/> records by diffing the ledger) writes to
/// Update History with. Keyed by component: <see cref="NameFor(ManagedComponentKind)"/> gives the history name, so a new
/// component needs only a name line there (or in <see cref="LinuxManagedComponentService.DisplayDependencyName"/>).
/// An update is recorded only when the version really changed (upstream <c>ShouldRecord</c>); a failure always is.
/// Recording never throws: history is informational.
/// </summary>
public sealed class ComponentHistory(ComponentUpdateLogStore log, Func<DateTime>? utcNow = null)
{
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Upstream <c>ShouldRecord</c>: both versions known, the new one not "unknown", and different.</summary>
    public static bool ShouldRecordUpdate(string? from, string? to)
    {
        var o = from?.Trim();
        var n = to?.Trim();
        if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(n)) return false;
        if (string.Equals(n, "unknown", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(o, n, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name Update History shows for a component.</summary>
    public static string NameFor(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.Dlss => "DLSS",
        ManagedComponentKind.Streamline => "Streamline",
        _ => LinuxManagedComponentService.DisplayDependencyName(kind)
    };

    /// <summary>The name of one DLSS file family, as Follow newest names it.</summary>
    public static string DlssName(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => "DLSS SR (nvngx_dlss.dll)",
        DlssComponentKind.RayReconstruction => "DLSS RR (nvngx_dlssd.dll)",
        DlssComponentKind.FrameGeneration => "DLSS FG (nvngx_dlssg.dll)",
        DlssComponentKind.NeuralRendering => "DLSS NR (nvngx_dlssnr.dll)",
        _ => kind.ToString()
    };

    public const string StreamlineName = "Streamline (sl.*.dll)";

    /// <summary>
    /// Records one change of <paramref name="component"/> in <paramref name="game"/>. An <see cref="ComponentUpdateAction.Update"/>
    /// that succeeded is recorded only when <see cref="ShouldRecordUpdate"/> says the version changed; installs, removals
    /// and failures always. <paramref name="name"/> defaults to <see cref="NameFor(ManagedComponentKind)"/>. True when written.
    /// </summary>
    public bool Record(DetectedGame game, ManagedComponentKind component, ComponentUpdateAction action, string? from, string? to,
        bool success = true, string? message = null, string? name = null)
    {
        if (success && action == ComponentUpdateAction.Update && !ShouldRecordUpdate(from, to)) return false;
        try
        {
            log.Record(new ComponentUpdateLogEntry
            {
                TimestampUtc = _utcNow(),
                GameName = game.Name,
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                Component = component,
                ComponentName = name ?? NameFor(component),
                Action = action,
                FromVersion = from?.Trim(),
                ToVersion = to?.Trim(),
                Success = success,
                Message = message
            });
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("update-history", $"The {name ?? NameFor(component)} change for {game.Name} was not recorded in the update history.", ex);
            return false;
        }
    }

    /// <summary>The file version of a DLSS DLL (or the first readable of several), for the "from" side of a swap.</summary>
    public static string? DllVersion(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (DlssAutoFollowService.PeFileVersion(path) is { } version) return version;
        return null;
    }

    /// <summary>The Streamline version of the first folder whose interposer or common DLL reads.</summary>
    public static string? StreamlineVersion(IEnumerable<string> folders)
        => DllVersion(folders.SelectMany(folder => new[] { Path.Combine(folder, "sl.interposer.dll"), Path.Combine(folder, "sl.common.dll") }));
}
