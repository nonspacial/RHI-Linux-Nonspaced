using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Ux;

/// <summary>
/// The support bundle (Refs #192, upstream <c>CopyLogsArchive_Click</c>, <c>CrashReporter.SanitisePath</c>,
/// <c>GameReportEncoder</c>, <c>ExportGameData_Click</c>): RHI's logs as one zip, a game's report as markdown and the
/// library's game data as a zipped JSON. Every text that leaves the machine goes through <see cref="Mask"/>: the home
/// folder becomes <c>~</c>, the user name <c>[user]</c>, Steam user ids and SteamID64s placeholders, token-shaped strings
/// <c>[token]</c>, and the private Xbox installer app's name a neutral placeholder. Files are written whole through
/// <see cref="AtomicFile.WriteNew"/> into RHI's own support folder (<c>$XDG_STATE_HOME/rhi-linux/support</c>), never into a
/// game folder, and never replace an existing file.
/// </summary>
public sealed class DiagnosticsBundle
{
    /// <summary>The newest part of each log file that goes into the zip (the app log rotates at 1 MiB).</summary>
    public const long MaxBytesPerLog = 4L * 1024 * 1024;

    /// <summary>At most this many per-launch logs (newest first) go into the zip.</summary>
    public const int MaxLaunchLogs = 20;

    private readonly string _home;
    private readonly string _userName;
    private readonly Func<DateTime> _clock;

    public DiagnosticsBundle(string logDirectory, string outputDirectory, string home, string userName, Func<DateTime>? clock = null)
    {
        LogDirectory = logDirectory;
        OutputDirectory = outputDirectory;
        _home = Path.TrimEndingDirectorySeparator(home ?? "");
        _userName = userName ?? "";
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>The current user's log folder and RHI's support folder.</summary>
    public static DiagnosticsBundle ForCurrentUser()
        => new(LinuxPaths.LogDirectory, Path.Combine(LinuxPaths.StateHome, "rhi-linux", "support"), LinuxPaths.Home, Environment.UserName);

    public string LogDirectory { get; }

    /// <summary>Where the zips and reports are written (RHI's own state folder).</summary>
    public string OutputDirectory { get; }

    private static readonly Regex SteamUserData = new(@"(userdata[/\\])\d+(?=[/\\]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SteamId64 = new(@"\b7656119\d{10}\b", RegexOptions.Compiled);
    private static readonly Regex GitHubToken = new(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b", RegexOptions.Compiled);
    private static readonly Regex InstallerName = new(Regex.Escape(XboxInstallerApp.FileTag), RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The placeholder that stands for the private Xbox installer app's name in generated text.</summary>
    public const string InstallerPlaceholder = "[xbox-installer]";

    /// <summary>
    /// Masks personal details in <paramref name="text"/> (upstream masks the Windows profile folder, Steam user ids and
    /// Xbox package versions; on Linux: <c>$HOME</c>, the user name, Steam user ids and SteamID64s, plus token-shaped
    /// strings and the private installer app's name).
    /// </summary>
    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var masked = text;
        if (_home.Length > 1)
            masked = Regex.Replace(masked, Regex.Escape(_home) + @"(?![\w.-])", "~"); // the folder itself or anything below it
        if (_userName.Length >= 2)
            masked = Regex.Replace(masked, @"(?<![\w.-])" + Regex.Escape(_userName) + @"(?![\w.-])", "[user]");
        masked = SteamUserData.Replace(masked, "$1[userid]");
        masked = SteamId64.Replace(masked, "[steamid]");
        masked = GitHubToken.Replace(masked, "[token]");
        masked = InstallerName.Replace(masked, InstallerPlaceholder);
        return masked;
    }

    /// <summary>
    /// Zips every log in <see cref="LogDirectory"/> (the app log and its rotations, the recovery log, the newest
    /// per-launch logs), masked, into a new file in <see cref="OutputDirectory"/>. Null when there is no log to zip.
    /// </summary>
    public string? CreateLogsArchive()
    {
        var logs = LogFiles().ToArray();
        if (logs.Length == 0) return null;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, entryName) in logs)
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(ClampZipTime(File.GetLastWriteTime(path)));
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Mask(ReadTail(path, MaxBytesPerLog)));
            }
        }
        return WriteOutput($"RHI-Linux-logs-{Stamp()}.zip", buffer.ToArray());
    }

    /// <summary>Saves a masked game report (<paramref name="markdown"/>) as a new <c>.md</c> file and returns its path.</summary>
    public string SaveReport(string gameName, string markdown)
        => WriteOutput($"RHI-Linux-report-{SafeFileName(gameName)}-{Stamp()}.md", new UTF8Encoding(false).GetBytes(Mask(markdown)));

    /// <summary>
    /// Writes the library's game data (upstream's community-database export, schema "1") as a zipped JSON and returns the
    /// zip's path. Paths are relative to the game folder (upstream records the folder name and the exe below it).
    /// </summary>
    public string ExportGameData(IReadOnlyList<GameDataRow> games, string rhiVersion)
    {
        var export = new
        {
            version = "1",
            rhi_version = rhiVersion,
            platform = "linux",
            exported_at = _clock().ToUniversalTime().ToString("o"),
            game_count = games.Count,
            games
        };
        var json = Mask(JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }));
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("rhi_game_data.json", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(ClampZipTime(_clock()));
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(json);
        }
        return WriteOutput($"RHI-Linux-game-data-{Stamp()}.zip", buffer.ToArray());
    }

    private IEnumerable<(string Path, string EntryName)> LogFiles()
    {
        if (!Directory.Exists(LogDirectory)) yield break;
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(file => Path.GetFileName(file).Contains(".log", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
            if (IsRegularFile(file)) yield return (file, Path.GetFileName(file));
        var launch = Path.Combine(LogDirectory, "launch");
        if (!Directory.Exists(launch) || new DirectoryInfo(launch).LinkTarget is not null) yield break;
        foreach (var file in Directory.EnumerateFiles(launch, "*.log", SearchOption.TopDirectoryOnly)
                     .Where(IsRegularFile)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(MaxLaunchLogs))
            yield return (file, "launch/" + Path.GetFileName(file));
    }

    // Symlinks are not followed: a link in the log folder must not pull another file into the bundle.
    private static bool IsRegularFile(string path)
    {
        try { return new FileInfo(path) is { Exists: true, LinkTarget: null }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string ReadTail(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var skipped = Math.Max(0, length - maxBytes);
        stream.Seek(skipped, SeekOrigin.Begin);
        var bytes = new byte[length - skipped];
        stream.ReadExactly(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        return skipped == 0 ? text : $"[… first {skipped} bytes left out …]\n" + text;
    }

    private string WriteOutput(string fileName, byte[] bytes)
    {
        AtomicFile.CreateDirectoryDurable(OutputDirectory);
        var path = Path.Combine(OutputDirectory, fileName);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(OutputDirectory, $"{Path.GetFileNameWithoutExtension(fileName)}-{n}{Path.GetExtension(fileName)}");
        AtomicFile.WriteNew(path, bytes, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private string Stamp() => _clock().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime ClampZipTime(DateTime time) => time.Year < 1980 ? new DateTime(1980, 1, 1) : time;

    /// <summary>A name usable in a file name: invalid characters and spaces become '_' (upstream <c>SanitizeFileName</c>).</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "").Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray()).Trim('_');
        return safe.Length == 0 ? "game" : safe.Length > 60 ? safe[..60] : safe;
    }
}

/// <summary>One game in the game-data export (upstream <c>BuildGameDataJson</c> fields that exist on Linux).</summary>
public sealed record GameDataRow(
    string Name,
    string Store,
    string? InstallSubpath,
    string? ExeRelative,
    string? Api,
    IReadOnlyList<string> AllApis,
    int? Bitness,
    string? Engine,
    int? SteamAppid,
    string? EpicAppName,
    string? HeroicAppName,
    string? PcgwUrl,
    string? EngineIniProjectOverride);
