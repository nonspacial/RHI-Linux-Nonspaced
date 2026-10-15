using System.IO.Compression;
using System.Text.Json;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection.Launchers;

/// <summary>One game the itch app installed: its itch.io game id, title, folder and (from butler's verdict) its launch target.</summary>
internal sealed record ItchInstall(long? GameId, string Name, string Folder, string? Executable, string? Flavor);

/// <summary>
/// Reads the itch app's own install records (Refs #90), read only: butler's database <c>db/butler.db</c> (tables
/// <c>caves</c>, <c>install_locations</c>, <c>games</c>; the cave's <c>verdict</c> names the launch candidates) and, for
/// every install location plus the default <c>apps</c> folder, the receipt butler writes into each game folder
/// (<c>.itch/receipt.json.gz</c>: game id and title). While the itch app runs, butler keeps recent changes in the
/// database's write-ahead log, which <see cref="SqliteTableReader"/> reads with it; the receipts keep games listed when the
/// database cannot be read at all. Upstream RHI has no itch detector; this follows butler's own layout.
/// </summary>
internal static class ItchLibrary
{
    private const long MaxReceiptBytes = 4L * 1024 * 1024;
    private const long MaxReceiptJsonBytes = 32L * 1024 * 1024;

    /// <param name="userData">itch's user-data folder (<c>~/.config/itch</c>, or <c>AppData/Roaming/itch</c> in a prefix).</param>
    /// <param name="toLinux">Maps a path butler recorded onto the Linux folder it names (null = not mapped).</param>
    public static IReadOnlyList<ItchInstall> Read(string userData, Func<string, string?> toLinux, string home, CancellationToken ct)
    {
        var installs = new List<ItchInstall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var locations = new List<string> { Path.Combine(userData, "apps") };

        var database = Path.Combine(userData, "db", "butler.db");
        if (File.Exists(database))
        {
            // One snapshot of all three tables (with butler's -wal file laid over it while the itch app runs), one warning.
            if (!SqliteTableReader.TryReadTables(database, ["games", "install_locations", "caves"], out var tables, out var error))
                DetectorDiagnostics.Warn($"itch database {database} not read; its games are listed from the receipts in their folders", error);
            var titles = tables["games"]
                .Where(row => Number(row, "id") is not null)
                .GroupBy(row => Number(row, "id")!.Value)
                .ToDictionary(group => group.Key, group => Text(group.First(), "title"));
            var locationPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in tables["install_locations"])
            {
                if (Text(row, "id") is not { } id || Text(row, "path") is not { } recorded || toLinux(recorded) is not { } path) continue;
                locationPaths[id] = path;
                locations.Add(path);
            }
            foreach (var cave in tables["caves"])
            {
                ct.ThrowIfCancellationRequested();
                var folder = Text(cave, "custom_install_folder") is { } custom
                    ? toLinux(custom)
                    : Text(cave, "install_location_id") is { } locationId && locationPaths.TryGetValue(locationId, out var location) &&
                      Text(cave, "install_folder_name") is { } name && IsPlainName(name)
                        ? Path.Combine(location, name)
                        : null;
                if (GameFolder(folder, home) is not { } game || !seen.Add(PathIdentity.Canonicalize(game))) continue;
                var gameId = Number(cave, "game_id");
                var receipt = ReadReceipt(game);
                var (exe, flavor) = Verdict(Text(cave, "verdict"), game);
                installs.Add(new ItchInstall(gameId ?? receipt.GameId,
                    FirstText(gameId is { } known ? titles.GetValueOrDefault(known) : null, receipt.Title) ?? Path.GetFileName(game),
                    game, exe, flavor));
            }
        }

        // Receipts: every game folder in an install location, whatever the database says (or cannot say yet).
        foreach (var location in locations.Distinct(StringComparer.Ordinal))
        {
            string[] folders;
            try { folders = Directory.Exists(location) ? Directory.GetDirectories(location) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DetectorDiagnostics.Warn($"itch install folder {location} unreadable; games in it are not listed", ex);
                continue;
            }
            foreach (var folder in folders.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(ReceiptPath(folder)) || GameFolder(folder, home) is not { } game || !seen.Add(PathIdentity.Canonicalize(game))) continue;
                var receipt = ReadReceipt(game);
                installs.Add(new ItchInstall(receipt.GameId, receipt.Title ?? Path.GetFileName(game), game, null, null));
            }
        }
        return installs;
    }

    private static string ReceiptPath(string folder) => Path.Combine(folder, ".itch", "receipt.json.gz");

    private static string? GameFolder(string? folder, string home)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        folder = Path.TrimEndingDirectorySeparator(folder);
        return GameRootGuard.IsAcceptable(folder, home) ? folder : null;
    }

    private static bool IsPlainName(string name)
        => name is not ("." or "..") && !name.Contains('/') && !name.Contains('\\');

    /// <summary>Game id and title from a folder's butler receipt; nothing when it is missing, too large or unreadable.</summary>
    internal static (long? GameId, string? Title) ReadReceipt(string folder)
    {
        var path = ReceiptPath(folder);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, null);
            if (info.Length > MaxReceiptBytes)
            {
                DetectorDiagnostics.Warn($"itch receipt {path} is larger than {MaxReceiptBytes} bytes; not read");
                return (null, null);
            }
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var bounded = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bounded.Length + read > MaxReceiptJsonBytes)
                    throw new InvalidDataException($"receipt expands past {MaxReceiptJsonBytes} bytes");
                bounded.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(bounded.ToArray());
            if (!document.RootElement.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.Object) return (null, null);
            long? id = game.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.Number && idValue.TryGetInt64(out var number) ? number : null;
            var title = game.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? FirstText(titleValue.GetString()) : null;
            return (id, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            DetectorDiagnostics.Warn($"itch receipt {path} unreadable; the folder name is used", ex);
            return (null, null);
        }
    }

    /// <summary>The first launch candidate butler found in the folder (its path and flavor: windows, linux, script…).</summary>
    private static (string? Executable, string? Flavor) Verdict(string? json, string folder)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return (null, null);
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object ||
                    !candidate.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                    pathValue.GetString() is not { Length: > 0 } relative)
                    continue;
                var flavor = candidate.TryGetProperty("flavor", out var flavorValue) && flavorValue.ValueKind == JsonValueKind.String ? flavorValue.GetString() : null;
                var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Any(part => part is ".." or ".")) continue;
                var exe = Path.Combine([folder, .. parts]);
                // A symlinked subfolder must not lead out of the game's folder either.
                if (File.Exists(exe) && PathIdentity.IsSameOrInside(exe, folder)) return (exe, flavor);
            }
        }
        catch (JsonException ex)
        {
            DetectorDiagnostics.Warn($"itch install record for {folder} has an unreadable verdict; its executable is searched for", ex);
        }
        return (null, null);
    }

    private static string? Text(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static long? Number(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) ? value switch { long number => number, string text when long.TryParse(text, out var parsed) => parsed, _ => null } : null;

    private static string? FirstText(params string?[] values)
        => values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
