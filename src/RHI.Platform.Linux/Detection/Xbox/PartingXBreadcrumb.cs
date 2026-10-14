using System.Text.Json;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>
/// A PartingX instance announcing itself (Refs #90; PartingX #217/#218, spec <c>reviews/w3-90-partingx-entrypoints.md</c>):
/// every <c>${XDG_DATA_HOME:-~/.local/share}/partingx/instances/*.json</c>, one per instance, kept after PartingX exits.
/// Schema 1 fields read here: <c>schema</c>, <c>app</c>, <c>updatedAt</c>, <c>stateDir</c>, <c>settingsFile</c>,
/// <c>jobsFile</c>, <c>payloadRoots[]</c>, <c>environmentRoot</c>, <c>runtimeRoot</c>, <c>launch{argv[],env{}}</c> and
/// <c>entryPoints{play[],show[]}</c>; unknown fields are ignored. A file of a newer schema is skipped with a scan
/// warning (its meaning may have changed); a missing, garbled or foreign file is skipped with a log line. When several
/// files name one state folder, the newest <c>updatedAt</c> wins.
/// </summary>
internal sealed record PartingXBreadcrumb(
    string File,
    DateTimeOffset? UpdatedAt,
    string StateDir,
    string? SettingsFile,
    string? JobsFile,
    IReadOnlyList<string> PayloadRoots,
    string? EnvironmentRoot,
    string? RuntimeRoot,
    IReadOnlyList<string>? LaunchArgv,
    IReadOnlyDictionary<string, string> LaunchEnvironment,
    IReadOnlyList<string>? PlayEntry,
    IReadOnlyList<string>? ShowEntry)
{
    public const int SupportedSchema = 1;
    public const string App = "PartingX";

    /// <summary>The argument an entry point carries for the title's store id; replaced in whole arguments only.</summary>
    public const string StoreIdPlaceholder = "{storeId}";

    /// <summary>Fixed start of the scan warning for a breadcrumb of a newer schema; the file follows.</summary>
    public const string NewerSchemaPrefix =
        "A PartingX instance file uses a newer format than this RHI reads (update RHI); that instance is not read from it. File: ";

    private const long MaxBytes = 64 * 1024;

    /// <summary>
    /// <c>launch.argv</c> followed by <paramref name="entry"/> with <see cref="StoreIdPlaceholder"/> replaced by
    /// <paramref name="storeId"/>; null when the breadcrumb has no launch or no such entry point.
    /// </summary>
    public IReadOnlyList<string>? Command(IReadOnlyList<string>? entry, string storeId)
        => LaunchArgv is null || entry is null
            ? null
            : [.. LaunchArgv, .. entry.Select(argument => argument == StoreIdPlaceholder ? storeId : argument)];

    /// <summary>Every usable breadcrumb in <paramref name="directory"/>, one per state folder (newest <c>updatedAt</c>).</summary>
    /// <param name="reportNewerSchema">
    /// Whether a newer-schema file is a scan warning of the calling detector: the Xbox detector's (it lists the games);
    /// the Wine detector only borrows the folders, so it stays quiet rather than report the same file twice.
    /// </param>
    public static IReadOnlyList<PartingXBreadcrumb> ReadAll(string? directory, bool reportNewerSchema = true)
    {
        var crumbs = new List<PartingXBreadcrumb>();
        if (directory is null || !Directory.Exists(directory)) return crumbs;
        string[] files;
        try { files = Directory.GetFiles(directory, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("scan", $"PartingX instance list {directory} unreadable", ex);
            return crumbs;
        }
        foreach (var file in files.Order(StringComparer.Ordinal))
            if (Read(file, reportNewerSchema) is { } crumb) crumbs.Add(crumb);
        return crumbs
            .GroupBy(crumb => PathIdentity.Canonicalize(crumb.StateDir), StringComparer.Ordinal)
            // Newest updatedAt wins; a file without one counts as oldest; equal times: the last file name.
            .Select(group => group.OrderBy(crumb => crumb.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(crumb => crumb.File, StringComparer.Ordinal)
                .Last())
            .ToList();
    }

    private static PartingXBreadcrumb? Read(string file, bool reportNewerSchema)
    {
        try
        {
            if (new FileInfo(file).Length > MaxBytes) throw new IOException($"{file} is larger than {MaxBytes} bytes");
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schemaValue) || !schemaValue.TryGetInt32(out var schema) || schema < 1)
            {
                AppLog.Warn("scan", $"PartingX instance file {file} has no schema; skipped");
                return null;
            }
            if (schema > SupportedSchema)
            {
                AppLog.Warn("scan", $"PartingX instance file {file}: schema {schema}, this RHI reads {SupportedSchema}");
                if (reportNewerSchema) DetectorDiagnostics.Warn(NewerSchemaPrefix + file);
                return null;
            }
            if (PartingXInstance.String(root, "app") is { } app && !string.Equals(app, App, StringComparison.Ordinal))
            {
                AppLog.Warn("scan", $"Instance file {file} belongs to {app}, not PartingX; skipped");
                return null;
            }
            if (Rooted(PartingXInstance.String(root, "stateDir")) is not { } stateDir)
            {
                AppLog.Warn("scan", $"PartingX instance file {file} names no absolute stateDir; skipped");
                return null;
            }
            DateTimeOffset? updatedAt = PartingXInstance.String(root, "updatedAt") is { } stamp &&
                                        DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                                            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            var launch = root.TryGetProperty("launch", out var launchValue) && launchValue.ValueKind == JsonValueKind.Object ? launchValue : default;
            var argv = Strings(launch, "argv");
            if (argv is { Count: 0 } || (argv is not null && !Path.IsPathRooted(argv[0]))) argv = null;
            var entryPoints = root.TryGetProperty("entryPoints", out var entries) && entries.ValueKind == JsonValueKind.Object ? entries : default;
            return new PartingXBreadcrumb(
                file,
                updatedAt,
                stateDir,
                Rooted(PartingXInstance.String(root, "settingsFile")),
                Rooted(PartingXInstance.String(root, "jobsFile")),
                (Strings(root, "payloadRoots") ?? []).Where(Path.IsPathRooted).Distinct(StringComparer.Ordinal).ToArray(),
                Rooted(PartingXInstance.String(root, "environmentRoot")),
                Rooted(PartingXInstance.String(root, "runtimeRoot")),
                argv,
                Environment(launch),
                Strings(entryPoints, "play"),
                Strings(entryPoints, "show"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("scan", $"PartingX instance file {file} unreadable", ex);
            return null;
        }
    }

    private static string? Rooted(string? path) => path is not null && Path.IsPathRooted(path) ? path : null;

    // A string array whose every element is a string; null when absent or anything else.
    private static IReadOnlyList<string>? Strings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            items.Add(item.GetString()!);
        }
        return items;
    }

    // launch.env: string values under names a process environment can carry (no '=' or NUL); anything else is dropped.
    private static IReadOnlyDictionary<string, string> Environment(JsonElement launch)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (launch.ValueKind != JsonValueKind.Object || !launch.TryGetProperty("env", out var value) || value.ValueKind != JsonValueKind.Object)
            return env;
        foreach (var pair in value.EnumerateObject())
            if (pair.Value.ValueKind == JsonValueKind.String && pair.Name.Length > 0 && pair.Name.IndexOfAny(['=', '\0']) < 0 &&
                !pair.Value.GetString()!.Contains('\0'))
                env[pair.Name] = pair.Value.GetString()!;
        return env;
    }
}
