using System.Collections.Concurrent;
using System.Text.Json;
using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// The one gate in front of every RHI change to an Xbox / Game Pass title (Refs #90). Two rules:
/// <list type="bullet">
/// <item>Job in progress: while the installer app has a job queued or running for the title (install, update, repair, Prepare,
/// launch) RHI changes nothing of it, game folder or prefix: the installer app re-fetches package files under RHI's feet,
/// Prepare rewrites the prefix, a running game holds its files open. Read from the installer app's <c>jobs.json</c>
/// <c>queue</c> (finished jobs move to <c>history</c>). A job list that cannot be read, and an installer-app title whose
/// instance RHI does not know (so it has no job list to read), are refused the same way: RHI cannot tell.</item>
/// <item>Protected files (<see cref="PackageFileProtection"/>): the encrypted exes, the msixvc, the package config and
/// manifest, the installer app's markers and the DXVK/VKD3D/NVAPI DLLs the installer app places in a title prefix.</item>
/// </list>
/// Where it is checked: every entry point that changes a game asks <see cref="Refusal(DetectedGame)"/> before anything
/// else (managed components, DLSS/Streamline swap and restore, DLSS defaults and batch, ReShade global settings,
/// Repair after move), so the user sees one plain refusal before any download. The shared write primitives check
/// paths: a path taken into a deployment (<c>OwnedFileSet.TrackAsync</c>, the transactional file service) passes
/// <see cref="ThrowIfRefused"/> (both rules), and every placing/replacing/renaming/deleting primitive
/// (<c>AtomicFile</c>, <c>FileCleanup.TryDelete</c>) passes <see cref="PackageFileProtection.ThrowIfProtected"/>.
/// The job rule is not re-checked by the low-level primitives on purpose: a rollback must always be able to put back
/// what the operation changed, even if the installer app queued a job meanwhile.
/// Protection is decided from disk on every call (an exe without <c>MZ</c>, markers found by walking up), so a file whose
/// status flips between track and rollback (a package marker appearing mid-operation) can refuse its rollback; that
/// rollback then keeps every copy and reports it, it never loses data (scan re-review nit 5).
/// </summary>
public static class XboxWriteGate
{
    public const string InstallerApp = XboxInstallerApp.DisplayName;

    /// <summary>The refusal while the installer app has a job for the game (the job itself is written to the log).</summary>
    public const string BusyMessage =
        "The app that installed this game is working on it (installing, updating, preparing or running it); RHI changes nothing until it has finished.";

    /// <summary>Fixed start of the refusal when the job list cannot be read; its path follows.</summary>
    public const string UnreadablePrefix =
        "The job list of the app that installed this game could not be read, so RHI cannot tell whether that app is changing it. File: ";

    /// <summary>Fixed start of the refusal for an installer-app title whose instance RHI does not know; the title's folder follows.</summary>
    public const string UnknownInstancePrefix =
        "This game was installed by a copy of the Xbox app RHI does not know, so it cannot tell whether that app is changing it. Nothing was changed. Start that app once, or add its folder in Settings → Xbox / Game Pass folders. Folder: ";

    /// <summary>
    /// The install refusal on an Xbox card whose exe RHI cannot read (encrypted on disk) and whose package declares no
    /// architecture either: the existing "32-bit or 64-bit" refusal, with the reason (#167 reads a declared one).
    /// </summary>
    public const string EncryptedExecutableMessage =
        "Could not determine whether the game executable is 32-bit or 64-bit: this Xbox / Game Pass package keeps it encrypted on disk and does not declare its architecture, so RHI cannot install mods on it.";

    // An installer-app title folder whose instance is unknown is recognised by these files (the installer app's own layout).
    private static readonly string OwnedMarker = XboxInstallerApp.OwnedMarker;
    private static readonly string EnvironmentFile = XboxInstallerApp.EnvironmentFile;
    private const int MaxWalkUp = 12;
    private const long MaxJobsBytes = 16 * 1024 * 1024;

    private sealed record Title(string? StoreId, string OwningApp, string? JobsFile, string Folder);

    // Folders (canonical) of the Xbox titles the last scans found: payload folder and title environment.
    private static readonly ConcurrentDictionary<string, Title> Registered = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the folders of an Xbox title the scan found (its package folder, its installer-app title environment), so
    /// a write to a path inside them is checked against the title's job list (<see cref="ThrowIfRefused"/>).
    /// </summary>
    public static void Register(XboxPackageInfo package, params string?[] folders)
    {
        ArgumentNullException.ThrowIfNull(package);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) continue;
            var canonical = PathIdentity.Canonicalize(folder);
            Registered[canonical] = new Title(package.StoreId, package.OwningApp, package.JobsFile, canonical);
        }
    }

    /// <summary>The refusal for changing <paramref name="game"/> now (job in progress or cannot tell); null when nothing stands in the way.</summary>
    public static string? Refusal(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.XboxPackage is { } package)
            return JobRefusal(package.StoreId, package.OwningApp, package.JobsFile, game.InstallPath);
        // A card that is not the Xbox card itself (a manual entry inside an installer-app folder) is judged by its folders.
        foreach (var folder in new[] { game.InstallPath, game.BinaryDirectory, game.PrefixPath })
            if (PathJobRefusal(folder) is { } refusal) return refusal;
        return null;
    }

    /// <summary>
    /// <see cref="Refusal(DetectedGame)"/>, then the install-only refusal of an Xbox card whose bitness nothing tells:
    /// the exe cannot be read and the package declares no architecture (<see cref="GameArchitecture"/>, #167), so no
    /// installer can tell 32 from 64 bits. Removal never asks this.
    /// </summary>
    public static string? InstallRefusal(DetectedGame game)
        => Refusal(game) ?? (HasUnreadableExecutable(game) ? EncryptedExecutableMessage : null);

    /// <summary>The refusal for changing the file at <paramref name="path"/> now (protected file, or its title is busy); null when RHI may.</summary>
    public static string? Refusal(string path)
        => PackageFileProtection.Refusal(path) ?? PathJobRefusal(path);

    /// <summary><paramref name="path"/> itself, after <see cref="ThrowIfRefused"/> (for an argument checked before anything else runs).</summary>
    public static string Checked(string path)
    {
        ThrowIfRefused(path);
        return path;
    }

    /// <summary>Throws <see cref="ProtectedPackageFileException"/> or <see cref="XboxTitleBusyException"/>.</summary>
    public static void ThrowIfRefused(string path)
    {
        PackageFileProtection.ThrowIfProtected(path);
        if (PathJobRefusal(path) is { } busy) throw new XboxTitleBusyException(busy);
    }

    /// <summary>The first refusal among <paramref name="paths"/> (a batch is refused whole, before anything changes).</summary>
    public static string? Refusal(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (Refusal(path) is { } refusal) return refusal;
        return null;
    }

    private static bool HasUnreadableExecutable(DetectedGame game)
        => game.XboxPackage is not null &&
           !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
           File.Exists(game.ExecutablePath) &&
           GameArchitecture.Of(game) == PeArchitecture.Unknown;

    private static string? PathJobRefusal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        var full = PathIdentity.Canonicalize(path);
        Title? owner = null;
        foreach (var title in Registered.Values)
            if (IsSameOrInside(full, title.Folder) && (owner is null || title.Folder.Length > owner.Folder.Length))
                owner = title;
        if (owner is not null) return JobRefusal(owner.StoreId, owner.OwningApp, owner.JobsFile, owner.Folder);

        // An installer-app folder no scan registered: the installer app owns it, but which instance (and job list) is unknown.
        var current = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        for (var level = 0; current is not null && level <= MaxWalkUp; level++, current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, OwnedMarker)) ||
                (File.Exists(Path.Combine(current, EnvironmentFile)) && Directory.Exists(Path.Combine(current, "prefix"))))
                return UnknownInstancePrefix + current;
        }
        return null;
    }

    private static string? JobRefusal(string? storeId, string owningApp, string? jobsFile, string? folder)
    {
        if (!string.Equals(owningApp, InstallerApp, StringComparison.Ordinal)) return null;
        if (jobsFile is null || storeId is null) return UnknownInstancePrefix + folder;
        if (!File.Exists(jobsFile)) return null; // The installer app has never queued a job
        try
        {
            if (new FileInfo(jobsFile).Length > MaxJobsBytes) throw new IOException($"{jobsFile} is larger than {MaxJobsBytes} bytes");
            using var doc = JsonDocument.Parse(File.ReadAllText(jobsFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var job in queue.EnumerateArray())
            {
                if (!string.Equals(Text(job, "titleId"), storeId, StringComparison.OrdinalIgnoreCase)) continue;
                AppLog.Warn("deploy", $"Xbox app job {Text(job, "kind")} ({Text(job, "state")}) for {storeId}: RHI changes refused");
                return BusyMessage;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("deploy", $"Xbox app job list {jobsFile} unreadable", ex);
            return UnreadablePrefix + jobsFile;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSameOrInside(string candidate, string root)
        => candidate.Equals(root, StringComparison.Ordinal) ||
           candidate.StartsWith(root.EndsWith('/') ? root : root + "/", StringComparison.Ordinal);
}

/// <summary>A deployment tried to change a file of an Xbox title the installer app is working on, or cannot be checked (<see cref="XboxWriteGate"/>).</summary>
public sealed class XboxTitleBusyException(string message) : IOException(message);
