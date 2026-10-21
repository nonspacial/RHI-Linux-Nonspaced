using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A key RenoDX's Engine.ini step added (<see cref="RenoDxEngineIni"/>); <paramref name="Group"/> = "lut" or "hdr".</summary>
internal sealed record RenoDxEngineIniOwnedKey(string Group, string Section, string Key, string AppliedValue);

/// <summary>
/// What RenoDX's Engine.ini step owns in one Engine.ini, kept on RenoDX's record: the keys it added (and still holds),
/// whether it created the file, and the lock it added (upstream makes Engine.ini read-only after adding its keys).
/// </summary>
/// <param name="ModeBefore">The file's Unix mode before RHI's first key went in (null: RHI created the file).</param>
/// <param name="LockedByRhi">RHI made the file read-only; it is unlocked again when RHI's last key comes out.</param>
internal sealed record RenoDxEngineIniState(
    string IniPath, bool FileCreatedByRhi, int? ModeBefore, bool LockedByRhi, IReadOnlyList<RenoDxEngineIniOwnedKey> Entries);

/// <summary>
/// #217 (and #218's HDR keys): RenoDX's own Engine.ini keys for Unreal Engine games (upstream
/// <c>AuxInstallService.ApplyEngineIniLutSetting</c> / <c>RemoveEngineIniLutSetting</c>): <c>r.LUT.UpdateEveryFrame=1</c>
/// under <c>[/Script/Engine.RendererSettings]</c>, so the game recomputes its colour-grading LUT every frame for RenoDX.
/// Written at RenoDX's install, update and local drop, and by the RenoDX ⚙ (On / Off), inside RenoDX's journaled
/// operation. Ownership per key: a key the file already has (any section, as upstream checks) is the user's and is never
/// changed or owned; a key RHI added comes out on removal or Off only while it still holds RHI's value. As upstream,
/// Engine.ini is made read-only once RHI's key is in (so the game cannot rewrite it on launch); the earlier lock state is
/// recorded and put back when RHI's last key comes out. Every write keeps a user's own lock
/// (<see cref="LumaEngineIniOverrideManager.WriteUserEngineIni"/>).
/// </summary>
internal static class RenoDxEngineIni
{
    /// <summary>Record metadata: the owned state (<see cref="RenoDxEngineIniState"/>).</summary>
    public const string StateMetadataKey = "RenoDX.EngineIniKeys";

    /// <summary>Record metadata: "off" when the user turned the LUT key off in the RenoDX ⚙ (absent = on, upstream's default).</summary>
    public const string LutChoiceMetadataKey = "RenoDX.EngineIniLut";

    public const string LutGroup = "lut";
    public const string Off = "off";

    public static readonly IReadOnlyList<(string Section, string Key, string Value)> LutKeys =
        [("/Script/Engine.RendererSettings", "r.LUT.UpdateEveryFrame", "1")];

    private const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode DefaultMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>An Unreal Engine game (the manifest's engine hint, as upstream's <c>EngineHint.Contains("Unreal")</c>).</summary>
    public static bool IsUnreal(DetectedGame game) => game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The game's Engine.ini (the one resolver every Engine.ini writer uses), or null when no location is proven.</summary>
    public static string? ResolvePath(DetectedGame game) => UpstreamEngineIniFileManager.ResolveTargetPath(game);

    /// <summary>
    /// Whether the LUT key applies to <paramref name="game"/> at all (and the ⚙ row shows): an Unreal game the manifest
    /// does not exclude (<c>ueExtendedCompatibility[game].lut</c> false), with no custom Engine.ini file (upstream's
    /// <c>engineIniFiles</c>, which then manages Engine.ini) and an Engine.ini location RHI can find.
    /// </summary>
    public static bool LutApplies(DetectedGame game)
        => IsUnreal(game) && game.UeExtendedLutByManifest != false && string.IsNullOrWhiteSpace(game.EngineIniFileOverride)
           && ResolvePath(game) is not null;

    /// <summary>The user's choice on <paramref name="record"/>: false only after the ⚙ turned it off.</summary>
    public static bool LutChosen(LinuxDeploymentRecord? record)
        => !string.Equals(record?.Metadata.GetValueOrDefault(LutChoiceMetadataKey), Off, StringComparison.OrdinalIgnoreCase);

    public static RenoDxEngineIniState? Read(LinuxDeploymentRecord? record)
    {
        if (record?.Metadata.GetValueOrDefault(StateMetadataKey) is not { Length: > 0 } json) return null;
        try { return JsonSerializer.Deserialize<RenoDxEngineIniState>(json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("renodx-engine-ini", "Stored Engine.ini key state could not be read; it is treated as absent", ex);
            return null;
        }
    }

    public static void Write(LinuxDeploymentRecord record, RenoDxEngineIniState? state)
    {
        if (state is null) record.Metadata.Remove(StateMetadataKey);
        else record.Metadata[StateMetadataKey] = JsonSerializer.Serialize(state);
    }

    /// <summary>Whether the LUT key is in effect for the ⚙: RHI owns it, or the file already holds it with the value 1.</summary>
    public static bool LutIsOn(DetectedGame game, LinuxDeploymentRecord? record)
    {
        if (Read(record)?.Entries.Any(entry => entry.Group == LutGroup) == true) return true;
        if (!LutChosen(record) || ResolvePath(game) is not { } path || !File.Exists(path)) return false;
        var lines = File.ReadAllLines(path);
        return LutKeys.All(item => FindKeyAnywhere(lines, item.Key) is var index and >= 0 && Value(lines[index]) == item.Value);
    }

    /// <summary>
    /// Changes the file at <paramref name="iniPath"/> from <paramref name="state"/>: each key of the <paramref name="on"/>
    /// groups the file lacks is added and owned; each owned key of the <paramref name="off"/> groups is taken out while it
    /// still holds RHI's value (a value the user changed stays and is no longer owned); a key the file already had is left
    /// alone, and owned keys of other groups stay. Locks the file once RHI owns a key in it, and puts the earlier lock state
    /// back when RHI owns none. Returns the state to keep (null: RHI owns nothing there).
    /// </summary>
    public static RenoDxEngineIniState? Apply(string iniPath, IReadOnlyDictionary<string, IReadOnlyList<(string Section, string Key, string Value)>> on,
        IReadOnlySet<string> off, RenoDxEngineIniState? state)
    {
        if (state is not null && !PathsEqual(state.IniPath, iniPath))
        {
            Cleanup(state); // the game's Engine.ini moved (another prefix user, a new config folder): RHI's old keys go
            state = null;
        }
        var existed = File.Exists(iniPath);
        var target = existed ? LumaEngineIniOverrideManager.ResolveWriteTarget(iniPath) : iniPath;
        var lines = existed ? File.ReadAllLines(iniPath).ToList() : [];
        var owned = new List<RenoDxEngineIniOwnedKey>();
        var changed = false;
        foreach (var entry in state?.Entries ?? [])
        {
            var index = FindKeyInSection(lines, entry.Section, entry.Key);
            if (index < 0 || Value(lines[index]) != entry.AppliedValue) continue; // the user's now
            if (!off.Contains(entry.Group)) { owned.Add(entry); continue; }
            lines.RemoveAt(index);
            RemoveSectionIfEmpty(lines, entry.Section);
            changed = true;
        }
        foreach (var (group, keys) in on)
        {
            foreach (var (section, key, value) in keys)
            {
                if (owned.Any(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                if (FindKeyAnywhere(lines, key) >= 0) continue; // upstream: present anywhere = there already; never the user's value
                InsertIntoSection(lines, section, $"{key}={value}");
                owned.Add(new RenoDxEngineIniOwnedKey(group, section, key, value));
                changed = true;
            }
        }

        var createdByRhi = state?.FileCreatedByRhi ?? (!existed && owned.Count > 0);
        var modeBefore = state is not null ? state.ModeBefore : existed && !OperatingSystem.IsWindows() ? (int)File.GetUnixFileMode(target) : null;
        if (owned.Count == 0)
        {
            if (changed) WriteOrDelete(iniPath, lines, createdByRhi);
            if (state?.LockedByRhi == true && File.Exists(iniPath) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(LumaEngineIniOverrideManager.ResolveWriteTarget(iniPath), modeBefore is { } mode ? (UnixFileMode)mode : DefaultMode);
            return null;
        }
        if (changed) LumaEngineIniOverrideManager.WriteUserEngineIni(iniPath, lines);
        var lockedByRhi = state?.LockedByRhi ?? false;
        if (!OperatingSystem.IsWindows())
        {
            var written = LumaEngineIniOverrideManager.ResolveWriteTarget(iniPath);
            var current = File.GetUnixFileMode(written);
            if ((current & WriteBits) != 0)
            {
                // Upstream: read-only so the game cannot rewrite it on launch. Only a file that was writable before RHI's
                // first key is unlocked again later; a user's own lock stays theirs.
                File.SetUnixFileMode(written, current & ~WriteBits);
                lockedByRhi = lockedByRhi || modeBefore is null || ((UnixFileMode)modeBefore.Value & WriteBits) != 0;
            }
        }
        return new RenoDxEngineIniState(iniPath, createdByRhi, modeBefore, lockedByRhi, owned);
    }

    /// <summary>Takes RHI's keys out (each only while it still holds RHI's value) and puts the earlier lock state back.</summary>
    public static void Cleanup(RenoDxEngineIniState? state)
    {
        if (state is null || !File.Exists(state.IniPath)) return;
        Apply(state.IniPath, new Dictionary<string, IReadOnlyList<(string, string, string)>>(),
            state.Entries.Select(entry => entry.Group).ToHashSet(StringComparer.Ordinal), state);
    }

    private static void WriteOrDelete(string iniPath, List<string> lines, bool createdByRhi)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        if (createdByRhi && lines.Count == 0 && new FileInfo(iniPath).LinkTarget is null)
        {
            File.Delete(iniPath);
            return;
        }
        LumaEngineIniOverrideManager.WriteUserEngineIni(iniPath, lines);
    }

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal);

    private static (string? Key, string? Value) Parse(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('[')) return (null, null);
        var equals = trimmed.IndexOf('=');
        return equals <= 0 ? (null, null) : (trimmed[..equals].Trim(), trimmed[(equals + 1)..].Trim());
    }

    private static string Value(string line) => Parse(line).Value ?? "";

    private static int FindKeyAnywhere(IReadOnlyList<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
            if (Parse(lines[i]).Key is { } found && found.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var header = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
                if (lines[j].TrimStart().StartsWith('[')) { end = j; break; }
            return (i, end);
        }
        return (-1, -1);
    }

    private static int FindKeyInSection(IReadOnlyList<string> lines, string section, string key)
    {
        var (start, end) = FindSection(lines, section);
        for (var i = start + 1; start >= 0 && i < end; i++)
            if (Parse(lines[i]).Key is { } found && found.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static void InsertIntoSection(List<string> lines, string section, string line)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(line);
            return;
        }
        var insert = end;
        while (insert > start + 1 && string.IsNullOrWhiteSpace(lines[insert - 1])) insert--;
        lines.Insert(insert, line);
    }

    private static void RemoveSectionIfEmpty(List<string> lines, string section)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0) return;
        for (var i = start + 1; i < end; i++)
            if (lines[i].Trim().Length > 0) return; // any other line (a comment too) keeps the section
        lines.RemoveRange(start, end - start);
        if (start > 0 && start <= lines.Count && string.IsNullOrWhiteSpace(lines[start - 1])
            && (start == lines.Count || lines[start].TrimStart().StartsWith('[')))
            lines.RemoveAt(start - 1); // the blank line RHI put before the section it added
    }
}
