using System.Text.Json;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>
/// One PartingX instance, read from its state folder (Refs #90). Only the files PartingX's spec fixes are read:
/// <c>settings.json</c> (storage roots), the payload layout and <c>.partingx-owned</c> marker, the per-title
/// <c>partingx.json</c> environment file, runtime bundles, user recipes and (for names only) the catalogue index.
/// Nothing is written and PartingX is never run.
/// </summary>
internal sealed class PartingXInstance
{
    public const string OwnedMarker = ".partingx-owned";
    public const string PartialMarker = ".partingx-partial";
    // PartingX's spec: the environment of the (only) local account.
    private const string AccountId = "default";

    private Dictionary<string, string>? _catalogue;

    private PartingXInstance(string stateDir, string payloadRoot, string? environmentRoot, string? runtimeRoot)
    {
        StateDir = stateDir;
        PayloadRoot = payloadRoot;
        EnvironmentRoot = environmentRoot;
        RuntimeRoot = runtimeRoot;
    }

    public string StateDir { get; }
    public string PayloadRoot { get; }
    public string? EnvironmentRoot { get; }
    public string? RuntimeRoot { get; }
    public string JobsFile => Path.Combine(StateDir, "jobs.json");

    /// <summary>The command that opens this instance of PartingX; null when RHI found none (Play is then disabled).</summary>
    public string? OpenCommand { get; set; }

    /// <summary>
    /// The instance whose state folder is <paramref name="stateDir"/>; null when it has no readable <c>settings.json</c>
    /// with a payload root. <paramref name="reportProblems"/>: the folder was named by the user or a breadcrumb, so a
    /// missing or unreadable file is a scan warning (the default state folder without settings is just PartingX never set up).
    /// </summary>
    public static PartingXInstance? Load(string stateDir, bool reportProblems)
    {
        var settings = Path.Combine(stateDir, "settings.json");
        if (!File.Exists(settings))
        {
            if (reportProblems) DetectorDiagnostics.Warn($"PartingX folder {stateDir} has no settings.json; its games are not listed");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(ReadSmall(settings));
            var root = doc.RootElement;
            var payload = String(root, "payloadRoot");
            if (payload is null || !Path.IsPathRooted(payload))
            {
                DetectorDiagnostics.Warn($"PartingX settings {settings} name no payload folder; its games are not listed");
                return null;
            }
            return new PartingXInstance(stateDir, payload, Rooted(String(root, "environmentRoot")), Rooted(String(root, "runtimeRoot")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DetectorDiagnostics.Warn($"PartingX settings {settings} unreadable; its games are not listed", ex);
            return null;
        }
    }

    /// <summary>One installed title: PartingX's own rule (a real folder whose marker parses and names the folder's store id).</summary>
    public sealed record Install(string Directory, string StoreId, string? Title);

    /// <summary>Installed titles; unfinished downloads (no marker, or a partial marker) are counted, not returned.</summary>
    public IReadOnlyList<Install> Installs(CancellationToken ct, out int partials)
    {
        partials = 0;
        var installs = new List<Install>();
        string[] dirs;
        try { dirs = Directory.Exists(PayloadRoot) ? Directory.GetDirectories(PayloadRoot) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectorDiagnostics.Warn($"PartingX payload folder {PayloadRoot} unreadable; its games are not listed", ex);
            return installs;
        }
        foreach (var dir in dirs.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            if (!IsStoreId(id) || XboxPackageReader.IsLink(dir)) continue;
            var marker = Path.Combine(dir, OwnedMarker);
            if (!File.Exists(marker) || File.Exists(Path.Combine(dir, PartialMarker)))
            {
                if (File.Exists(Path.Combine(dir, PartialMarker)) || File.Exists(Path.Combine(dir, XboxPackageReader.StreamingTempFileName)))
                    partials++;
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(ReadSmall(marker));
                if (!string.Equals(String(doc.RootElement, "storeId"), id, StringComparison.Ordinal))
                {
                    DetectorDiagnostics.Warn($"PartingX folder {dir}: its marker names another title; not listed");
                    continue;
                }
                installs.Add(new Install(dir, id, String(doc.RootElement, "packageTitle")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                DetectorDiagnostics.Warn($"PartingX folder {dir}: marker unreadable; not listed", ex);
            }
        }
        return installs;
    }

    /// <summary>The catalogue title (names only; the catalogue is a cache PartingX refreshes, so it is never relied on for anything else).</summary>
    public string? CatalogueTitle(string storeId)
    {
        if (_catalogue is null)
        {
            _catalogue = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = Path.Combine(StateDir, "catalogue", "index.json");
            try
            {
                if (File.Exists(index))
                {
                    using var doc = JsonDocument.Parse(ReadSmall(index, 64 * 1024 * 1024));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array)
                        foreach (var title in titles.EnumerateArray())
                            if (String(title, "productId") is { } id && String(title, "title") is { } name)
                                _catalogue.TryAdd(id, name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warn("scan", $"PartingX catalogue {index} unreadable; names come from the packages", ex);
            }
        }
        return _catalogue.GetValueOrDefault(storeId);
    }

    /// <summary>The executable a user recipe tells PartingX to run instead of the package's own.</summary>
    public string? RecipeExecutable(string storeId)
    {
        var recipe = Path.Combine(StateDir, "recipes", storeId + ".json");
        if (!File.Exists(recipe)) return null;
        try
        {
            using var doc = JsonDocument.Parse(ReadSmall(recipe));
            return String(doc.RootElement, "executable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DetectorDiagnostics.Warn($"PartingX recipe {recipe} unreadable; the package's own executable is used", ex);
            return null;
        }
    }

    /// <summary>The title's Wine environment: its prefix, and the Wine build PartingX selected for it.</summary>
    public sealed record TitleEnvironment(string? Prefix, string? RuntimeName, string? Wine, string? WineServer);

    public TitleEnvironment Environment(string storeId)
    {
        if (EnvironmentRoot is null) return new(null, null, null, null);
        var dir = Path.Combine(EnvironmentRoot, AccountId, storeId);
        var prefix = Path.Combine(dir, "prefix");
        var hasPrefix = Directory.Exists(Path.Combine(prefix, "drive_c"));
        string? runtime = null, wine = null, wineServer = null;
        var file = Path.Combine(dir, "partingx.json");
        if (File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadSmall(file));
                runtime = String(doc.RootElement, "selectedRuntime");
                if (runtime is not null && RuntimeRoot is not null && IsSafeName(runtime))
                {
                    var bin = Path.Combine(RuntimeRoot, runtime, "bin");
                    wine = ExistingFile(Path.Combine(bin, "wine"));
                    wineServer = ExistingFile(Path.Combine(bin, "wineserver"));
                }
                else if (doc.RootElement.TryGetProperty("external", out var external) && external.ValueKind == JsonValueKind.Object &&
                         Rooted(String(external, "winePath")) is { } winePath)
                {
                    runtime = "external";
                    wine = ExistingFile(winePath);
                    wineServer = wine is null ? null : ExistingFile(Path.Combine(Path.GetDirectoryName(wine)!, "wineserver"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                DetectorDiagnostics.Warn($"PartingX environment {file} unreadable; the game is listed without its Wine build", ex);
            }
        }
        return new(hasPrefix ? prefix : null, runtime, wine, wineServer);
    }

    /// <summary>
    /// True for PartingX's environment root layout (<c>default/</c> plus <c>.templates/</c>): every Wine prefix under it
    /// belongs to PartingX, so the Wine-prefix detector never lists one (Refs #90).
    /// </summary>
    internal static bool IsEnvironmentRoot(string dir)
        => Directory.Exists(Path.Combine(dir, AccountId)) && Directory.Exists(Path.Combine(dir, ".templates"));

    // 1–32 ASCII letters and digits (PartingX's store id rule).
    internal static bool IsStoreId(string value) => value.Length is > 0 and <= 32 && value.All(char.IsAsciiLetterOrDigit);

    private static bool IsSafeName(string value)
        => value.Length > 0 && value != "." && value != ".." && value.IndexOfAny(['/', '\\']) < 0;

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;

    private static string? Rooted(string? path) => path is not null && Path.IsPathRooted(path) ? path : null;

    internal static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>Reads a small state file whole (a corrupt multi-GB file must not be loaded).</summary>
    internal static string ReadSmall(string path, long cap = 4 * 1024 * 1024)
    {
        if (new FileInfo(path).Length > cap) throw new IOException($"{path} is larger than {cap} bytes");
        return File.ReadAllText(path);
    }
}
