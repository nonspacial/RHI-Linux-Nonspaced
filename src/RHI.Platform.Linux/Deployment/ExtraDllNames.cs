using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One row of an extra's DLL-name list (#284): the name, the component using it (null = free), and whether it is
/// ASI Loader's Hooked name.</summary>
public sealed record ExtraDllNameOption(string Name, string? BlockedBy, bool IsHooked)
{
    public bool IsFree => BlockedBy is null;
}

/// <summary>
/// #284 (upstream 2.9.0 <c>DetailPanelBuilder.Extras.DllNames</c> / <c>MainViewModel.UalHooked</c>): the DLL-name lists of
/// the extras with a name choice (20/30 FG Unlock, Universal MFG Unlocker, RTX Encore, DLSS Enabler). With ASI Loader
/// installed as X.dll the list offers XHooked.dll right after X.dll (ASI Loader loads it as the real X.dll, so the extra
/// runs alongside it); names another RHI component uses are marked with that component. An extra's proxy file is found
/// through its record (<see cref="ProxyOf"/>), so ASI Loader's install and removal can move it and its record together.
/// </summary>
public static class ExtraDllNames
{
    /// <summary>Fixed text before "Hooked name → ASI Loader name" when a Hooked name is chosen without ASI Loader under the
    /// name it stands in for (PT-BR presentation pair).</summary>
    public const string HookedNeedsUalPrefix =
        "This Hooked name works only while ASI Loader is installed in this game under the name it stands in for; install ASI Loader under that name first, or choose another name. Names: ";

    /// <summary>Fixed text before the name when the game's own DLL already sits under the Hooked name (PT-BR pair).</summary>
    public const string HookedTakenByGamePrefix = "the game's own ";

    // The extras with a name choice, and the record metadata keys that hold the name they were installed under.
    private static readonly IReadOnlyDictionary<ManagedComponentKind, string[]> NameKeys = new Dictionary<ManagedComponentKind, string[]>
    {
        [ManagedComponentKind.Dlssg2030Unlock] = ["FG2030.ProxyName"],
        [ManagedComponentKind.Rtx40MfgStandalone] = ["UniversalMFG.ProxyName", "RTX40MFG.ProxyName"],
        [ManagedComponentKind.RtxEncore] = ["RtxEncore.Name"],
        [ManagedComponentKind.DlssEnablerStandalone] = ["DLSSEnabler.ProxyName"],
    };

    // RTX Encore kept the game's own file under its name: the name must stay (the kept copy goes back over it on removal).
    private const string RtxEncoreKeptKey = "RtxEncore.KeptOriginal";

    /// <summary>The extras whose name follows ASI Loader (#284), in upstream's order.</summary>
    public static IReadOnlyCollection<ManagedComponentKind> Extras => NameKeys.Keys.ToArray();

    /// <summary>The name RHI's ASI Loader is installed under on <paramref name="game"/>, or null.</summary>
    public static string? UalName(LinuxDeploymentRecordStore records, DetectedGame game)
        => records.Find(game, ManagedComponentKind.UltimateAsiLoader)?.Metadata.GetValueOrDefault(UltimateAsiLoaderDeploymentService.MetaProxyName);

    /// <summary>
    /// The rows of <paramref name="kind"/>'s DLL-name list on <paramref name="game"/>: <paramref name="baseNames"/> with
    /// ASI Loader's Hooked name after ASI Loader's name, each marked with the RHI component that uses it (the extra's own
    /// <paramref name="current"/> name is never marked). A Hooked name the game's own DLL already sits under (ASI Loader
    /// chained it there) is marked as the game's.
    /// </summary>
    public static IReadOnlyList<ExtraDllNameOption> Options(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind kind,
        IEnumerable<string> baseNames, string? current, Func<string, bool>? noHooked = null)
    {
        var ual = UalName(records, game);
        var owners = Owners(records, game, kind);
        if (!string.IsNullOrEmpty(current)) owners.Remove(current);
        var directory = BinaryDirectory(game);
        return HookedDllNames.WithHooked(baseNames, ual, noHooked).Select(name =>
        {
            var hooked = HookedDllNames.IsHookedFor(name, ual);
            var blockedBy = owners.TryGetValue(name, out var owner) ? owner : null;
            if (blockedBy is null && hooked && !string.Equals(name, current, StringComparison.OrdinalIgnoreCase)
                && directory is not null && ReShadeDeploymentService.FindFileCaseInsensitive(directory, name) is not null)
                blockedBy = HookedTakenByGamePrefix + ual;
            return new ExtraDllNameOption(name, blockedBy, hooked);
        }).ToArray();
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be installed now because it is a Hooked name without ASI Loader under its base
    /// name (#284), or null. Other names are the extra's own business.
    /// </summary>
    public static string? HookedRefusal(LinuxDeploymentRecordStore records, DetectedGame game, string name)
    {
        if (!HookedDllNames.TryGetBase(name, out var baseName)) return null;
        return string.Equals(UalName(records, game), baseName, StringComparison.OrdinalIgnoreCase)
            ? null
            : HookedNeedsUalPrefix + $"{name} → {baseName}";
    }

    /// <summary>Every DLL name another RHI component's file sits under in the game's binary folder, with that component.</summary>
    public static Dictionary<string, string> Owners(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind except)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (BinaryDirectory(game) is not { } directory) return owners;
        foreach (var record in records.FindAll(game).Where(record => record.Component != except))
            foreach (var file in record.Files.Where(file => !string.IsNullOrWhiteSpace(file.Path)))
            {
                var full = Path.GetFullPath(file.Path);
                if (string.Equals(Path.GetDirectoryName(full), directory, StringComparison.Ordinal)
                    && full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    owners.TryAdd(Path.GetFileName(full), LinuxManagedComponentService.DisplayDependencyName(record.Component));
            }
        return owners;
    }

    /// <summary>The proxy file of an extra's record (the file its recorded name points at), or null for another record.</summary>
    internal static LinuxDeployedFile? ProxyOf(LinuxDeploymentRecord record)
    {
        if (!NameKeys.TryGetValue(record.Component, out var keys)) return null;
        var name = keys.Select(key => record.Metadata.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (name is null) return null;
        return record.Files.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file.Path)
                                                   && Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(Path.GetFullPath(Path.GetDirectoryName(file.Path)!), Path.GetFullPath(record.BinaryDirectory), StringComparison.Ordinal));
    }

    /// <summary>
    /// The extra of <paramref name="game"/> whose proxy is <paramref name="path"/> and may move to its Hooked name (RHI
    /// created it, and RTX Encore keeps no game file under it), or null.
    /// </summary>
    internal static LinuxDeploymentRecord? MovableExtraAt(LinuxDeploymentRecordStore records, DetectedGame game, string path)
    {
        var full = Path.GetFullPath(path);
        return records.FindAll(game).FirstOrDefault(record => ProxyOf(record) is { ExistedBeforeInstall: false } proxy
                                                             && Path.GetFullPath(proxy.Path).Equals(full, StringComparison.Ordinal)
                                                             && !record.Metadata.ContainsKey(RtxEncoreKeptKey));
    }

    /// <summary>A copy of an extra's record whose proxy is now <paramref name="newPath"/> (file and recorded name).</summary>
    internal static LinuxDeploymentRecord Moved(LinuxDeploymentRecord record, string newPath, WineOverrideRecord? wineOverride)
    {
        var copy = LinuxDeploymentRecordStore.Clone(record);
        var proxy = ProxyOf(copy)!;
        proxy.Path = newPath;
        foreach (var key in NameKeys[copy.Component].Where(copy.Metadata.ContainsKey))
            copy.Metadata[key] = Path.GetFileName(newPath);
        copy.WineOverride = wineOverride;
        return copy;
    }

    private static string? BinaryDirectory(DetectedGame game)
        => string.IsNullOrWhiteSpace(game.BinaryDirectory) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.BinaryDirectory));
}
