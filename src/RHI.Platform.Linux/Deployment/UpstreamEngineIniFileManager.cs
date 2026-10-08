using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

internal sealed record UpstreamEngineIniOwnedEntry(
    string Section,
    string Key,
    string AppliedValue,
    bool HadPreviousValue,
    string? PreviousValue);

internal sealed record UpstreamEngineIniFileState(
    string IniPath,
    bool FileCreatedByRhi,
    string TemplateName,
    string TemplateSha256,
    IReadOnlyList<UpstreamEngineIniOwnedEntry> Entries);

internal sealed record UpstreamEngineIniApplyResult(
    UpstreamEngineIniFileState? State,
    int AppliedCount,
    int PreservedUserEdits,
    string? SourceUrl);

/// <summary>
/// Linux/Wine implementation of upstream RHI <c>engineIniFiles</c>.
/// Templates are fetched from RankFTW/rhi-repo, parsed as INI key/value authority and merged
/// transactionally into the resolved Unreal Engine.ini. RHI owns only keys it actually changes:
/// uninstall/update restores a pre-existing value when it is still equal to the value RHI wrote,
/// while user edits after deployment relinquish ownership and are preserved.
/// </summary>
internal sealed class UpstreamEngineIniFileManager
{
    internal const string MetadataKey = "Upstream.EngineIniFile";
    internal const string BaseUrl = "https://raw.githubusercontent.com/RankFTW/rhi-repo/main/engine-files/";
    private const int MaxTemplateBytes = 256 * 1024;

    private readonly HttpClient _http;
    private readonly string _cacheRoot;

    public UpstreamEngineIniFileManager(HttpClient http, string? cacheRoot = null)
    {
        _http = http;
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "engine-files");
    }

    public async Task<UpstreamEngineIniApplyResult> ApplyAsync(
        DetectedGame game,
        string templateName,
        UpstreamEngineIniFileState? previousState = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var safeName = ValidateTemplateName(templateName);
        var template = await FetchTemplateAsync(safeName, cancellationToken).ConfigureAwait(false);
        return ApplyTemplate(game, safeName, template.Text, template.Sha256, template.SourceUrl, previousState);
    }

    internal static UpstreamEngineIniApplyResult ApplyTemplate(
        DetectedGame game,
        string templateName,
        string templateText,
        string templateSha256,
        string? sourceUrl,
        UpstreamEngineIniFileState? previousState = null)
    {
        var requested = ParseTemplate(templateText);
        if (requested.Count == 0)
            throw new InvalidDataException($"Upstream Engine.ini template '{templateName}' contains no deployable INI keys.");

        var iniPath = ResolveTargetPath(game)
                      ?? throw new InvalidOperationException(
                          $"Upstream declares Engine.ini template '{templateName}', but no safe Unreal Engine config path could be resolved inside this Wine/Proton prefix.");

        if (previousState is not null && !PathsEqual(previousState.IniPath, iniPath))
        {
            Cleanup(previousState);
            previousState = null;
        }

        var existedBefore = File.Exists(iniPath);
        var lines = existedBefore ? File.ReadAllLines(iniPath).ToList() : new List<string>();
        var owned = new Dictionary<string, UpstreamEngineIniOwnedEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previousState?.Entries ?? Array.Empty<UpstreamEngineIniOwnedEntry>())
            owned[Identity(item.Section, item.Key)] = item;

        var requestedByIdentity = requested
            .GroupBy(item => Identity(item.Section, item.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var preserveUserEdit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var appliedCount = 0;
        var preservedUserEdits = 0;

        // Reconcile prior RHI-owned keys first. The first pre-RHI value remains the restoration
        // authority across updates; if the current value no longer matches what RHI wrote, the
        // user or another tool owns it now and RHI stops touching that key.
        foreach (var old in owned.Values.ToArray())
        {
            var identity = Identity(old.Section, old.Key);
            var index = FindKeyInSection(lines, old.Section, old.Key);
            if (index < 0)
            {
                owned.Remove(identity);
                preserveUserEdit.Add(identity);
                preservedUserEdits++;
                continue;
            }

            var current = GetValue(lines[index]);
            if (!string.Equals(current, old.AppliedValue, StringComparison.Ordinal))
            {
                owned.Remove(identity);
                preserveUserEdit.Add(identity);
                preservedUserEdits++;
                continue;
            }

            if (!requestedByIdentity.TryGetValue(identity, out var next))
            {
                if (old.HadPreviousValue)
                    lines[index] = $"{old.Key}={old.PreviousValue ?? string.Empty}";
                else
                {
                    lines.RemoveAt(index);
                    RemoveSectionIfEmpty(lines, old.Section);
                }
                owned.Remove(identity);
                changed = true;
                continue;
            }

            if (!string.Equals(current, next.Value, StringComparison.Ordinal))
            {
                lines[index] = $"{next.Key}={next.Value}";
                owned[identity] = old with
                {
                    Section = next.Section,
                    Key = next.Key,
                    AppliedValue = next.Value
                };
                changed = true;
                appliedCount++;
            }
        }

        foreach (var item in requestedByIdentity.Values)
        {
            var identity = Identity(item.Section, item.Key);
            if (owned.ContainsKey(identity) || preserveUserEdit.Contains(identity)) continue;

            var index = FindKeyInSection(lines, item.Section, item.Key);
            if (index >= 0)
            {
                var previous = GetValue(lines[index]);
                if (string.Equals(previous, item.Value, StringComparison.Ordinal))
                    continue; // Pre-existing matching value: do not claim ownership.

                lines[index] = $"{item.Key}={item.Value}";
                owned[identity] = new UpstreamEngineIniOwnedEntry(
                    item.Section, item.Key, item.Value, true, previous);
                changed = true;
                appliedCount++;
                continue;
            }

            InsertIntoSection(lines, item.Section, $"{item.Key}={item.Value}");
            owned[identity] = new UpstreamEngineIniOwnedEntry(
                item.Section, item.Key, item.Value, false, null);
            changed = true;
            appliedCount++;
        }

        if (changed || (!existedBefore && owned.Count > 0))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iniPath)
                                      ?? throw new InvalidOperationException("Resolved Engine.ini parent directory is invalid."));
            AtomicFile.ReplaceUserFileLines(iniPath, lines);
        }

        if (owned.Count == 0)
            return new UpstreamEngineIniApplyResult(null, appliedCount, preservedUserEdits, sourceUrl);

        return new UpstreamEngineIniApplyResult(
            new UpstreamEngineIniFileState(
                iniPath,
                previousState?.FileCreatedByRhi == true || !existedBefore,
                templateName,
                templateSha256,
                owned.Values
                    .OrderBy(value => value.Section, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
            appliedCount,
            preservedUserEdits,
            sourceUrl);
    }

    public static void Cleanup(UpstreamEngineIniFileState? state)
    {
        if (state is null || !File.Exists(state.IniPath)) return;
        var lines = File.ReadAllLines(state.IniPath).ToList();
        var changed = false;

        foreach (var item in state.Entries)
        {
            var index = FindKeyInSection(lines, item.Section, item.Key);
            if (index < 0) continue;
            if (!string.Equals(GetValue(lines[index]), item.AppliedValue, StringComparison.Ordinal))
                continue; // External/user edit wins.

            if (item.HadPreviousValue)
                lines[index] = $"{item.Key}={item.PreviousValue ?? string.Empty}";
            else
            {
                lines.RemoveAt(index);
                RemoveSectionIfEmpty(lines, item.Section);
            }
            changed = true;
        }

        if (!changed) return;
        TrimTrailingBlankLines(lines);
        if (state.FileCreatedByRhi && lines.All(string.IsNullOrWhiteSpace))
        {
            File.Delete(state.IniPath);
            return;
        }
        AtomicFile.ReplaceUserFileLines(state.IniPath, lines);
    }

    public static string Serialize(UpstreamEngineIniFileState state)
        => JsonSerializer.Serialize(state);

    public static UpstreamEngineIniFileState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<UpstreamEngineIniFileState>(json); }
        catch { return null; }
    }

    internal async Task<(string Text, string Sha256, string SourceUrl)> FetchTemplateAsync(
        string templateName,
        CancellationToken cancellationToken = default)
    {
        var safeName = ValidateTemplateName(templateName);
        Directory.CreateDirectory(_cacheRoot);
        var cachePath = Path.Combine(_cacheRoot, safeName);
        var sourceUrl = BaseUrl + Uri.EscapeDataString(safeName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            ValidateTemplateBytes(bytes, safeName);
            var text = DecodeTemplate(bytes);
            _ = ParseTemplate(text); // Structural validation before cache persistence.
            await PersistCacheAsync(cachePath, bytes, cancellationToken).ConfigureAwait(false);
            return (text, FileHash.Sha256HexOf(bytes), sourceUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!File.Exists(cachePath))
                throw new TimeoutException($"Timed out fetching upstream Engine.ini template '{safeName}'.");
        }
        catch when (File.Exists(cachePath))
        {
            // Last-known-good cache fallback below.
        }

        if (!File.Exists(cachePath))
            throw new InvalidDataException($"Upstream Engine.ini template '{safeName}' is unavailable and no last-known-good cache exists.");

        var cached = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
        ValidateTemplateBytes(cached, safeName);
        var cachedText = DecodeTemplate(cached);
        _ = ParseTemplate(cachedText);
        return (cachedText, FileHash.Sha256HexOf(cached), sourceUrl);
    }

    internal static IReadOnlyList<(string Section, string Key, string Value)> ParseTemplate(string text)
    {
        var result = new List<(string Section, string Key, string Value)>();
        string? section = null;
        using var reader = new StringReader(text ?? string.Empty);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Length == 0)
                    throw new InvalidDataException("Engine.ini template contains an empty section name.");
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || string.IsNullOrWhiteSpace(section))
                throw new InvalidDataException($"Engine.ini template contains an unsupported line: {raw}");
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0)
                throw new InvalidDataException("Engine.ini template contains an empty key.");
            result.Add((section!, key, value));
        }
        return result;
    }

    private static string ValidateTemplateName(string value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length == 0 || Path.IsPathRooted(name) || name != Path.GetFileName(name) ||
            name.Contains("..", StringComparison.Ordinal) || !name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe upstream Engine.ini template filename: '{value}'.");
        return name;
    }

    private static void ValidateTemplateBytes(byte[] bytes, string name)
    {
        if (bytes.Length == 0 || bytes.Length > MaxTemplateBytes)
            throw new InvalidDataException($"Engine.ini template '{name}' has an invalid size ({bytes.Length} bytes).");
        if (bytes.Contains((byte)0))
            throw new InvalidDataException($"Engine.ini template '{name}' contains NUL bytes.");
    }

    private static string DecodeTemplate(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Engine.ini template is not valid UTF-8.", ex); }
    }

    private static async Task PersistCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    internal static string? ResolveTargetPath(DetectedGame game)
    {
        var resolved = LumaEngineIniOverrideManager.ResolveEngineIniPath(game);
        return resolved is null ? null : ResolveExistingCaseInsensitive(resolved);
    }

    private static string ResolveExistingCaseInsensitive(string path)
    {
        if (File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return path;
        var name = Path.GetFileName(path);
        try
        {
            return Directory.EnumerateFiles(directory)
                       .FirstOrDefault(candidate => Path.GetFileName(candidate).Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? path;
        }
        catch { return path; }
    }

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string Identity(string section, string key) => section.Trim() + "\0" + key.Trim();

    private static int FindKeyInSection(IReadOnlyList<string> lines, string section, string key)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0) return -1;
        for (var i = start + 1; i < end; i++)
        {
            var parsed = ParseKey(lines[i]);
            if (parsed.Key is not null && parsed.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static (string? Key, string? Value) ParseKey(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('[')) return (null, null);
        var equals = trimmed.IndexOf('=');
        if (equals <= 0) return (null, null);
        return (trimmed[..equals].Trim(), trimmed[(equals + 1)..].Trim());
    }

    private static string GetValue(string line) => ParseKey(line).Value ?? string.Empty;

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var target = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    end = j;
                    break;
                }
            }
            return (i, end);
        }
        return (-1, -1);
    }

    private static void InsertIntoSection(List<string> lines, string section, string value)
    {
        var (_, end) = FindSection(lines, section);
        if (end < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add(value);
            return;
        }
        lines.Insert(end, value);
    }

    private static void RemoveSectionIfEmpty(List<string> lines, string section)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0) return;
        var hasContent = false;
        for (var i = start + 1; i < end; i++)
        {
            var text = lines[i].Trim();
            if (text.Length > 0 && !text.StartsWith(';') && !text.StartsWith('#'))
            {
                hasContent = true;
                break;
            }
        }
        if (!hasContent)
        {
            lines.RemoveRange(start, end - start);
            TrimTrailingBlankLines(lines);
        }
    }

    private static void TrimTrailingBlankLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
    }
}
