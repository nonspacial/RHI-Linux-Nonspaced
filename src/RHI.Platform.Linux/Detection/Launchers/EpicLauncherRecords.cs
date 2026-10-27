using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.Launchers;

/// <summary>
/// The Epic Games Launcher's own install records inside one Wine prefix (#247), the Linux counterpart of upstream's
/// <c>GameDetectionService.Epic.cs</c> (c1076d3): the classic launcher's <c>ProgramData/Epic/EpicGamesLauncher/Data/
/// Manifests/*.item</c> files (DisplayName, InstallLocation, AppName) and the new launcher's (<c>com.epicgames.eglv2</c>)
/// per-user append log <c>AppData/Local/com.epicgames.eglv2/installations.jsonl</c> (a header line, then one entry per
/// change; the last entry per installationId wins and only <c>complete: true</c> counts). The log has no names: they come
/// from the per-user uninstall entries <c>HKCU\…\Uninstall\EpicGames-&lt;artifactId&gt;</c> (user.reg), then from the
/// classic manifest a migrated game keeps in <c>import-scan/v1-items/&lt;installationId&gt;.item</c>, then the folder name.
/// Uninstall entries the log does not mention are listed too, as upstream does. The game starts through the launcher's
/// own URI, as upstream's Epic launch does: <c>com.epicgames.launcher://apps/&lt;AppName&gt;?action=launch&amp;silent=true</c>
/// (AppName = the new launcher's artifactId). Read only and bounded: regular files only, opened without blocking, capped.
/// </summary>
internal static class EpicLauncherRecords
{
    /// <summary>A classic manifest is a few KiB; anything past this is not one.</summary>
    internal const long MaxManifestBytes = 4L * 1024 * 1024;

    /// <summary>The new launcher's append log grows by one short line per install change.</summary>
    internal const long MaxInstallLogBytes = 32L * 1024 * 1024;

    internal const string UriScheme = "com.epicgames.launcher:";
    private const string UninstallPrefix = "EpicGames-";
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string UrlHandlerKey = @"Software\Classes\com.epicgames.launcher\shell\open\command";

    private static readonly string[] DefaultLauncherFiles =
    [
        "Program Files (x86)/Epic Games/Launcher/Portal/Binaries/Win64/EpicGamesLauncher.exe",
        "Program Files (x86)/Epic Games/Launcher/Portal/Binaries/Win32/EpicGamesLauncher.exe",
        "Program Files/Epic Games/Launcher/Portal/Binaries/Win64/EpicGamesLauncher.exe",
        "Program Files/Epic Games/Launcher/Portal/Binaries/Win32/EpicGamesLauncher.exe"
    ];

    /// <summary>One entry of the new launcher's install log, as its last line for that installationId left it.</summary>
    internal sealed record Installation(string InstallationId, bool Complete, string? Dir, string? Namespace, string? ArtifactId);

    public static IEnumerable<StoreLauncherGame> Read(StoreLauncherRecords.Context context, CancellationToken ct)
    {
        var manifests = Path.Combine(context.DriveC, "ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var dataFolders = UserFolders(context)
            .Select(user => Path.Combine(user, "AppData", "Local", "com.epicgames.eglv2"))
            .Where(Directory.Exists)
            .ToList();
        // Nothing of either launcher here: user.reg is not read at all.
        if (!Directory.Exists(manifests) && dataFolders.Count == 0) yield break;

        var user = LoadUserRegistry(context.Prefix);
        var launcher = Launcher(context, user);
        var found = new List<(string Folder, string Name, string? AppName)>();

        // Classic launcher: one .item file per install (a DLC's file shares its game's folder: the game's own comes first).
        if (Directory.Exists(manifests))
        {
            var items = Directory.EnumerateFiles(manifests, "*.item", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Order(StringComparer.Ordinal)
                .Select(file => (File: file, Manifest: ReadManifest(file)))
                .Where(item => item.Manifest is not null)
                .OrderBy(item => item.Manifest!.Value.IsAddOn ? 1 : 0)
                .ToList();
            foreach (var (_, manifest) in items)
            {
                ct.ThrowIfCancellationRequested();
                var (name, location, appName, _) = manifest!.Value;
                if (context.GameFolder(location) is { } folder)
                    found.Add((folder, FirstText(name) ?? Path.GetFileName(folder), appName));
            }
        }

        // New launcher: the install log, names from the uninstall entries it writes.
        var uninstall = UninstallEntries(user);
        var listedArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataFolder in dataFolders)
        {
            var log = Path.Combine(dataFolder, "installations.jsonl");
            if (!File.Exists(log)) continue;
            foreach (var install in ReadInstallLog(log))
            {
                ct.ThrowIfCancellationRequested();
                if (install.ArtifactId is { } artifact) listedArtifacts.Add(artifact);
                if (!install.Complete || context.GameFolder(install.Dir) is not { } folder) continue;
                var name = (install.ArtifactId is { } id && uninstall.TryGetValue(id, out var entry) ? entry.Name : null)
                           ?? MigratedManifestName(dataFolder, install.InstallationId)
                           ?? Path.GetFileName(folder);
                found.Add((folder, name, install.ArtifactId));
            }
        }
        // Uninstall entries the log does not mention (upstream: in case the log's format changes).
        if (dataFolders.Count > 0)
            foreach (var (artifact, entry) in uninstall)
                if (!listedArtifacts.Contains(artifact) && context.GameFolder(entry.Location) is { } folder)
                    found.Add((folder, entry.Name, artifact));

        var launcherFolder = launcher is null ? null : PathIdentity.Canonicalize(Path.GetDirectoryName(launcher)!);
        foreach (var (folder, name, appName) in found)
        {
            ct.ThrowIfCancellationRequested();
            // The launcher's own folder is never one of its games.
            if (launcherFolder is not null && PathIdentity.IsSameOrInside(launcherFolder, PathIdentity.Canonicalize(folder))) continue;
            var plain = appName is not null && StoreLauncherRecords.IsStoreId(appName);
            yield return new StoreLauncherGame(StoreLauncher.Epic, plain ? appName : null, name, folder, launcher,
                plain ? [$"{UriScheme}//apps/{appName}?action=launch&silent=true"] : []);
        }
    }

    private static IEnumerable<string> UserFolders(StoreLauncherRecords.Context context)
    {
        var users = Path.Combine(context.DriveC, "users");
        return Directory.Exists(users) ? Directory.GetDirectories(users).Order(StringComparer.Ordinal) : [];
    }

    private static WineRegistryReader LoadUserRegistry(string prefix)
    {
        try { return WineRegistryReader.Load(Path.Combine(prefix, "user.reg"), [Uninstall, UrlHandlerKey]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectorDiagnostics.Warn($"Wine prefix {prefix}: user registry unreadable; Epic Games Launcher games are named by their folders", ex);
            return WineRegistryReader.Empty;
        }
    }

    /// <summary>
    /// The launcher the <c>com.epicgames.launcher:</c> URL handler starts (per user first, then the machine's), else the
    /// classic launcher's default places.
    /// </summary>
    private static string? Launcher(StoreLauncherRecords.Context context, WineRegistryReader user)
    {
        foreach (var command in new[] { user.Value(UrlHandlerKey, ""), context.Registry.Value(UrlHandlerKey, "") })
            if (HandlerProgram(command) is { } program && WindowsPathMapper.ToLinux(context.Prefix, program) is { } file && File.Exists(file))
                return file;
        return context.DefaultFile(DefaultLauncherFiles);
    }

    /// <summary>The program of a URL handler command: <c>"C:\…\EpicGamesLauncher.exe" %1</c> or unquoted up to <c>.exe</c>.</summary>
    internal static string? HandlerProgram(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }

    /// <summary>artifactId → name and recorded folder, from <c>HKCU\…\Uninstall\EpicGames-&lt;artifactId&gt;</c>.</summary>
    private static Dictionary<string, (string Name, string? Location)> UninstallEntries(WineRegistryReader user)
    {
        var entries = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in user.SubkeyNames(Uninstall))
        {
            if (!key.StartsWith(UninstallPrefix, StringComparison.OrdinalIgnoreCase) || key.Length == UninstallPrefix.Length) continue;
            var values = user.Values(Uninstall + "\\" + key);
            if (FirstText(values.GetValueOrDefault("DisplayName")) is not { } name) continue;
            entries[key[UninstallPrefix.Length..]] = (name, values.GetValueOrDefault("InstallLocation"));
        }
        return entries;
    }

    /// <summary>
    /// The latest entry per installationId of the new launcher's install log. A line that is not JSON (a half-written
    /// append) is skipped, with one warning for the file; the header and lines without an installationId are no entries.
    /// </summary>
    internal static IReadOnlyList<Installation> ReadInstallLog(string path)
    {
        if (ReadBounded(path, MaxInstallLogBytes, "Epic Games Launcher install log") is not { } bytes) return [];
        var latest = new Dictionary<string, Installation>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        var span = bytes.AsSpan();
        if (span.StartsWith("\uFEFF"u8)) span = span[3..];
        while (!span.IsEmpty)
        {
            var newline = span.IndexOf((byte)'\n');
            var line = newline < 0 ? span : span[..newline];
            span = newline < 0 ? default : span[(newline + 1)..];
            if (line.Trim(" \t\r"u8).IsEmpty) continue;
            try
            {
                using var document = JsonDocument.Parse(line.ToArray());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Text(root, "installationId") is not { } id) continue;
                latest[id] = new Installation(id,
                    root.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.True,
                    Text(root, "dir"), Text(root, "namespace"), Text(root, "artifactId"));
            }
            catch (JsonException) { unreadable++; }
        }
        if (unreadable > 0)
            DetectorDiagnostics.Warn($"Epic Games Launcher install log {path}: unreadable lines skipped: {unreadable}");
        return [.. latest.Values];
    }

    /// <summary>A classic manifest's name, install folder, AppName and whether it is a DLC (it names another main game).</summary>
    internal static (string? Name, string? Location, string? AppName, bool IsAddOn)? ReadManifest(string path)
    {
        if (ReadBounded(path, MaxManifestBytes, "Epic Games Launcher manifest") is not { } bytes) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var appName = Text(root, "AppName");
            var main = Text(root, "MainGameAppName");
            return (Text(root, "DisplayName"), Text(root, "InstallLocation"), appName,
                main is not null && appName is not null && !main.Equals(appName, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException ex)
        {
            DetectorDiagnostics.Warn($"Epic Games Launcher manifest {path} unreadable; not read", ex);
            return null;
        }
    }

    /// <summary>The DisplayName of the classic manifest a migrated game keeps in the new launcher's data folder.</summary>
    private static string? MigratedManifestName(string dataFolder, string installationId)
    {
        // The id names a file: only a plain name (letters, digits, `_ . -`), never a path.
        if (!StoreLauncherRecords.IsStoreId(installationId)) return null;
        var item = Path.Combine(dataFolder, "import-scan", "v1-items", installationId + ".item");
        return File.Exists(item) ? FirstText(ReadManifest(item)?.Name) : null;
    }

    /// <summary>
    /// The file's bytes, or null with a scan warning: never anything but a regular file (a FIFO would block the scan, a
    /// device never ends; opened without blocking and judged on the open file), never more than <paramref name="cap"/>.
    /// </summary>
    private static byte[]? ReadBounded(string path, long cap, string what)
    {
        try
        {
            using var handle = FileKind.OpenRegularFileForReading(path);
            if (handle is null)
            {
                DetectorDiagnostics.Warn($"{what} {path} is not a regular file or cannot be opened; not read");
                return null;
            }
            var length = RandomAccess.GetLength(handle);
            if (length > cap)
            {
                DetectorDiagnostics.Warn($"{what} {path} is larger than {cap} bytes; not read");
                return null;
            }
            // Bounded by the length of the open file: what the launcher appends meanwhile is read on the next scan.
            var bytes = new byte[length];
            var filled = 0;
            while (filled < bytes.Length && RandomAccess.Read(handle, bytes.AsSpan(filled), filled) is var read and > 0) filled += read;
            return filled == bytes.Length ? bytes : bytes[..filled];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectorDiagnostics.Warn($"{what} {path} unreadable; not read", ex);
            return null;
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? FirstText(value.GetString()) : null;

    private static string? FirstText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
