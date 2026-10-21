using System.Text;
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
/// <param name="CreatedSections">The section headers RHI added (and whether it put a blank line before each): only these
/// come out again, and only once empty, so removal leaves a header or blank line the file already had (null: none).</param>
internal sealed record RenoDxEngineIniState(
    string IniPath, bool FileCreatedByRhi, int? ModeBefore, bool LockedByRhi, IReadOnlyList<RenoDxEngineIniOwnedKey> Entries,
    IReadOnlyList<RenoDxEngineIniSection>? CreatedSections = null);

/// <summary>A section header RenoDX's Engine.ini step added, and whether it added the blank line before it.</summary>
internal sealed record RenoDxEngineIniSection(string Section, bool BlankLineAdded);

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

    // ── #218: UE-Extended's Engine.ini HDR keys (upstream ApplyEngineIniHdrSettings; its LUT line is the LUT group's) ──

    /// <summary>Record metadata: "off" when the user turned the HDR keys off in the RenoDX ⚙ (absent = on, upstream's default).</summary>
    public const string HdrChoiceMetadataKey = "RenoDX.EngineIniHdr";

    /// <summary>Record metadata: "1" when the last install found the HDR keys apply to the game (UE-Extended, the database's
    /// <c>Method: ini</c> or the manifest / engine rule); the ⚙ shows its HDR row only then.</summary>
    public const string HdrAppliesMetadataKey = "RenoDX.EngineIniHdrApplies";

    public const string HdrGroup = "hdr";

    public static readonly IReadOnlyList<(string Section, string Key, string Value)> HdrKeys =
    [
        ("SystemSettings", "r.AllowHDR", "1"),
        ("SystemSettings", "r.HDR.EnableHDROutput", "1"),
        ("SystemSettings", "r.HDR.Display.OutputDevice", "3"),
        ("SystemSettings", "r.HDR.Display.ColorGamut", "2"),
        ("SystemSettings", "r.HDR.UI.CompositeMode", "1")
    ];

    /// <summary>An Unreal Engine 4 game (upstream's <c>EngineHint.Contains("Unreal Engine 4")</c>: the SDR path by default).</summary>
    public static bool IsUnreal4(DetectedGame game) => game.EngineHint?.Contains("Unreal Engine 4", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether UE-Extended's Engine.ini HDR keys apply (upstream's install): a game with no custom Engine.ini file and an
    /// Engine.ini location, where the UE-Extended database entry says <c>Method: ini</c>, or — without an entry — the
    /// manifest's <c>ueExtendedCompatibility[game].hdr</c>, else every game but Unreal Engine 4.
    /// </summary>
    public static bool HdrApplies(DetectedGame game, RenoDxUnrealDbEntry? database)
        => string.IsNullOrWhiteSpace(game.EngineIniFileOverride) && ResolvePath(game) is not null
           && (database is not null ? database.DeployEngineIniHdr : game.UeExtendedHdrByManifest ?? !IsUnreal4(game));

    public static bool HdrChosen(LinuxDeploymentRecord? record)
        => !string.Equals(record?.Metadata.GetValueOrDefault(HdrChoiceMetadataKey), Off, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the HDR keys are in effect for the ⚙: RHI owns them, or the file already holds all of them as RHI would.</summary>
    public static bool HdrIsOn(DetectedGame game, LinuxDeploymentRecord? record) => GroupIsOn(game, record, HdrGroup, HdrKeys, HdrChosen(record));

    /// <summary>The <c>[renodx]</c> keys upstream's <c>ApplyRenoDxNativeHdrSettings</c> fills in for UE-Extended (missing ones only).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> NativeHdrKeys(bool usesSdrPath) =>
    [
        new("DumpLUTShaders", "0"), new("ForceBorderless", "1"), new("FxUpgradeRender", "1"), new("PreventFullscreen", "1"),
        new("SettingsMode", "2"), new("Set_Path", usesSdrPath ? "1" : "0"),
        new("Upgrade_B10G10R10A2_UNORM", ""), new("Upgrade_B8G8R8A8_TYPELESS", ""), new("Upgrade_B8G8R8A8_UNORM", ""),
        new("Upgrade_B8G8R8A8_UNORM_SRGB", ""), new("Upgrade_CopyDestinations", ""), new("Upgrade_R10G10B10A2_TYPELESS", ""),
        new("Upgrade_R10G10B10A2_UNORM", ""), new("Upgrade_R11G11B10_FLOAT", ""), new("Upgrade_R16G16B16A16_TYPELESS", ""),
        new("Upgrade_R8G8B8A8_SNORM", ""), new("Upgrade_R8G8B8A8_TYPELESS", ""), new("Upgrade_R8G8B8A8_UNORM", ""),
        new("Upgrade_R8G8B8A8_UNORM_SRGB", ""), new("Upgrade_SwapChainCompatibility", ""), new("Upgrade_UseSCRGB", "")
    ];

    /// <summary>Record metadata: RHI's native-HDR <c>[renodx]</c> keys (a <see cref="RenoDxIniOverrideState"/>).</summary>
    public const string NativeHdrMetadataKey = "RenoDX.NativeHdrIni";

    private static bool GroupIsOn(DetectedGame game, LinuxDeploymentRecord? record, string group,
        IReadOnlyList<(string Section, string Key, string Value)> keys, bool chosen)
    {
        if (Read(record)?.Entries.Any(entry => entry.Group == group) == true) return true;
        if (!chosen || ResolvePath(game) is not { } path || !File.Exists(path)) return false;
        try
        {
            var lines = IniText.Parse(File.ReadAllBytes(path)).Lines;
            return keys.All(item => FindKeyAnywhere(lines, item.Key) is var index and >= 0 && Value(lines[index].Text) == item.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The ⚙ still opens: an Engine.ini RHI cannot read counts as Off (and its row is hidden, see CannotEdit).
            AppLog.Warn("renodx-engine-ini", $"Could not read {path}; its RenoDX setting is shown as Off", ex);
            return false;
        }
    }

    /// <summary>
    /// Whether the ⚙ row of <paramref name="group"/> can do anything: RHI owns its keys (Off takes them out), or the game's
    /// Engine.ini can be edited. An Engine.ini RHI cannot read or follow hides the row (logged) instead of offering a switch
    /// that can only fail.
    /// </summary>
    public static bool RowUsable(DetectedGame game, LinuxDeploymentRecord? record, string group)
    {
        if (Read(record)?.Entries.Any(entry => entry.Group == group) == true) return true;
        if (ResolvePath(game) is not { } path || CannotEdit(path) is not { } refusal) return true;
        AppLog.Warn("renodx-engine-ini", $"RenoDX's Engine.ini setting is hidden for {game.Name}: {refusal}");
        return false;
    }

    /// <summary>
    /// Why RenoDX's Engine.ini step cannot edit <paramref name="iniPath"/> (null: it can): a link to a file outside the game's
    /// prefix or project folder, a file RHI cannot read, or one that is not text. Checked before the file is tracked, so an
    /// install, update or drop goes on without the key and says why (upstream logs the failure and carries on).
    /// </summary>
    public static string? CannotEdit(string iniPath)
    {
        try
        {
            var target = LumaEngineIniOverrideManager.ResolveWriteTarget(iniPath);
            if (File.Exists(target)) IniText.Parse(File.ReadAllBytes(target));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ex.Message;
        }
    }

    private const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode DefaultMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>An Unreal Engine game (the manifest's engine hint, as upstream's <c>EngineHint.Contains("Unreal")</c>).</summary>
    public static bool IsUnreal(DetectedGame game) => game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The game's Engine.ini (the one resolver every Engine.ini writer uses), or null when no location is proven — and only
    /// where its folder exists: a manifest project override names a folder the game creates on its first launch, and RHI
    /// creates no Engine.ini folder of its own (the key goes in at the next update after that launch; the ⚙ row is hidden).
    /// </summary>
    public static string? ResolvePath(DetectedGame game)
        => UpstreamEngineIniFileManager.ResolveTargetPath(game) is { } path && Directory.Exists(Path.GetDirectoryName(path)) ? path : null;

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
    public static bool LutIsOn(DetectedGame game, LinuxDeploymentRecord? record) => GroupIsOn(game, record, LutGroup, LutKeys, LutChosen(record));

    /// <summary>
    /// Changes the file at <paramref name="iniPath"/> from <paramref name="state"/>: each key of the <paramref name="on"/>
    /// groups the file lacks is added and owned; each owned key of the <paramref name="off"/> groups is taken out while it
    /// still holds RHI's value (a value the user changed stays and is no longer owned); a key the file already had is left
    /// alone, and owned keys of other groups stay. Locks the file once RHI owns a key in it, and puts the earlier lock state
    /// back when RHI owns none. Byte for byte: only RHI's lines move (a section header and the blank line before it only
    /// where RHI added them); the file's line endings, encoding and a missing final newline stay. Returns the state to keep
    /// (null: RHI owns nothing there).
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
        var text = existed ? IniText.Parse(File.ReadAllBytes(iniPath)) : IniText.Empty();
        var lines = text.Lines;
        var owned = new List<RenoDxEngineIniOwnedKey>();
        var created = (state?.CreatedSections ?? []).ToList();
        var changed = false;
        foreach (var entry in state?.Entries ?? [])
        {
            var index = FindKeyInSection(lines, entry.Section, entry.Key);
            if (index < 0 || Value(lines[index].Text) != entry.AppliedValue) continue; // the user's now
            if (!off.Contains(entry.Group)) { owned.Add(entry); continue; }
            text.RemoveAt(index);
            RemoveCreatedSectionIfEmpty(text, entry.Section, created);
            changed = true;
        }
        foreach (var (group, keys) in on)
        {
            foreach (var (section, key, value) in keys)
            {
                if (owned.Any(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                if (FindKeyAnywhere(lines, key) >= 0) continue; // upstream: present anywhere = there already; never the user's value
                if (FindSection(lines, section).Start < 0)
                {
                    created.RemoveAll(item => item.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
                    created.Add(new RenoDxEngineIniSection(section, text.AppendSection(section)));
                }
                InsertIntoSection(text, section, $"{key}={value}");
                owned.Add(new RenoDxEngineIniOwnedKey(group, section, key, value));
                changed = true;
            }
        }

        var createdByRhi = state?.FileCreatedByRhi ?? (!existed && owned.Count > 0);
        var modeBefore = state is not null ? state.ModeBefore : existed && !OperatingSystem.IsWindows() ? (int)File.GetUnixFileMode(target) : null;
        if (owned.Count == 0)
        {
            if (changed) WriteOrDelete(iniPath, text, createdByRhi);
            if (state?.LockedByRhi == true && File.Exists(iniPath) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(LumaEngineIniOverrideManager.ResolveWriteTarget(iniPath), modeBefore is { } mode ? (UnixFileMode)mode : DefaultMode);
            return null;
        }
        if (changed) LumaEngineIniOverrideManager.WriteUserEngineIniBytes(iniPath, text.ToBytes());
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
        return new RenoDxEngineIniState(iniPath, createdByRhi, modeBefore, lockedByRhi, owned, created.Count > 0 ? created : null);
    }

    /// <summary>
    /// <see cref="Apply"/> for an install, update or drop, where the key is optional: a failure that left every file it
    /// touches as it was (a folder RHI cannot write to, a file that turned unreadable) keeps <paramref name="state"/> and
    /// returns why in <paramref name="failure"/> (logged), so RenoDX itself still installs; a failure after a file changed
    /// is thrown, and the operation's rollback puts everything back.
    /// </summary>
    public static RenoDxEngineIniState? ApplyOptional(string iniPath, IReadOnlyDictionary<string, IReadOnlyList<(string Section, string Key, string Value)>> on,
        IReadOnlySet<string> off, RenoDxEngineIniState? state, out string? failure)
    {
        failure = null;
        var paths = new[] { iniPath, state?.IniPath }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var before = paths.Select(BytesOrNull).ToArray();
        try
        {
            return Apply(iniPath, on, off, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            for (var i = 0; i < paths.Length; i++)
            {
                var now = BytesOrNull(paths[i]);
                if ((now is null) != (before[i] is null) || (now is not null && !now.AsSpan().SequenceEqual(before[i]))) throw;
            }
            AppLog.Warn("renodx-engine-ini", $"RenoDX's Engine.ini line was not written to {iniPath}; RenoDX itself goes on", ex);
            failure = ex.Message;
            return state;
        }
    }

    private static byte[]? BytesOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; } // unreadable before and after = unchanged
    }

    /// <summary>Takes RHI's keys out (each only while it still holds RHI's value) and puts the earlier lock state back.</summary>
    public static void Cleanup(RenoDxEngineIniState? state)
    {
        if (state is null || !File.Exists(state.IniPath)) return;
        Apply(state.IniPath, new Dictionary<string, IReadOnlyList<(string, string, string)>>(),
            state.Entries.Select(entry => entry.Group).ToHashSet(StringComparer.Ordinal), state);
    }

    private static void WriteOrDelete(string iniPath, IniText text, bool createdByRhi)
    {
        if (createdByRhi && text.Lines.Count == 0 && new FileInfo(iniPath).LinkTarget is null)
        {
            File.Delete(iniPath);
            return;
        }
        LumaEngineIniOverrideManager.WriteUserEngineIniBytes(iniPath, text.ToBytes());
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

    private static int FindKeyAnywhere(IReadOnlyList<IniLine> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
            if (Parse(lines[i].Text).Key is { } found && found.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static (int Start, int End) FindSection(IReadOnlyList<IniLine> lines, string section)
    {
        var header = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Text.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
                if (lines[j].Text.TrimStart().StartsWith('[')) { end = j; break; }
            return (i, end);
        }
        return (-1, -1);
    }

    private static int FindKeyInSection(IReadOnlyList<IniLine> lines, string section, string key)
    {
        var (start, end) = FindSection(lines, section);
        for (var i = start + 1; start >= 0 && i < end; i++)
            if (Parse(lines[i].Text).Key is { } found && found.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static void InsertIntoSection(IniText text, string section, string line)
    {
        var (start, end) = FindSection(text.Lines, section);
        var insert = end;
        while (insert > start + 1 && string.IsNullOrWhiteSpace(text.Lines[insert - 1].Text)) insert--;
        text.Insert(insert, line);
    }

    /// <summary>A section header RHI added comes out once nothing but blank lines is left under it, with the blank line
    /// RHI put before it; a header the file already had always stays.</summary>
    private static void RemoveCreatedSectionIfEmpty(IniText text, string section, List<RenoDxEngineIniSection> created)
    {
        var mine = created.FindIndex(item => item.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (mine < 0) return;
        var (start, end) = FindSection(text.Lines, section);
        if (start < 0) return;
        for (var i = start + 1; i < end; i++)
            if (text.Lines[i].Text.Trim().Length > 0) return; // any other line (a comment too) keeps the section
        text.RemoveAt(start);
        if (created[mine].BlankLineAdded && start > 0 && string.IsNullOrWhiteSpace(text.Lines[start - 1].Text))
            text.RemoveAt(start - 1);
        created.RemoveAt(mine);
    }

    /// <summary>One line of Engine.ini: its text and the line ending it had ("" for a last line without one).</summary>
    private sealed record IniLine(string Text, string Eol)
    {
        public string Eol { get; set; } = Eol;
    }

    /// <summary>
    /// Engine.ini as lines that keep their own endings, with its encoding (UTF-8, or UTF-8/UTF-16 with a byte order mark,
    /// as Unreal writes a file with non-ASCII text), so writing it back gives the same bytes for every line RHI did not touch.
    /// </summary>
    private sealed class IniText
    {
        private Encoding _encoding = new UTF8Encoding(false, true);
        private byte[] _preamble = [];
        private string _newLine = "\n";
        public List<IniLine> Lines { get; } = [];

        public static IniText Empty() => new();

        public static IniText Parse(byte[] bytes)
        {
            var text = new IniText();
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) (text._encoding, text._preamble) = (new UTF8Encoding(false, true), [0xEF, 0xBB, 0xBF]);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) (text._encoding, text._preamble) = (new UnicodeEncoding(false, false, true), [0xFF, 0xFE]);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) (text._encoding, text._preamble) = (new UnicodeEncoding(true, false, true), [0xFE, 0xFF]);
            string content;
            try { content = text._encoding.GetString(bytes, text._preamble.Length, bytes.Length - text._preamble.Length); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException(RenoDxDeploymentService.EngineIniNotTextMessage, ex); }
            var first = true;
            for (var start = 0; start < content.Length;)
            {
                var newline = content.IndexOf('\n', start);
                if (newline < 0) { text.Lines.Add(new IniLine(content[start..], "")); break; }
                var crlf = newline > start && content[newline - 1] == '\r';
                var eol = crlf ? "\r\n" : "\n";
                text.Lines.Add(new IniLine(content[start..(crlf ? newline - 1 : newline)], eol));
                if (first) { text._newLine = eol; first = false; }
                start = newline + 1;
            }
            return text;
        }

        public byte[] ToBytes()
        {
            var builder = new StringBuilder();
            foreach (var line in Lines) builder.Append(line.Text).Append(line.Eol);
            return [.. _preamble, .. _encoding.GetBytes(builder.ToString())];
        }

        /// <summary>Inserts a line in the file's own line ending; at the end of a file without a final newline, the new
        /// line takes over that (so taking it out again gives the same bytes).</summary>
        public void Insert(int index, string line)
        {
            if (index == Lines.Count && index > 0 && Lines[index - 1].Eol.Length == 0)
            {
                Lines[index - 1].Eol = _newLine;
                Lines.Add(new IniLine(line, ""));
                return;
            }
            Lines.Insert(index, new IniLine(line, _newLine));
        }

        /// <summary>Takes a line out; a last line without a final newline hands that back to the line before it.</summary>
        public void RemoveAt(int index)
        {
            var removed = Lines[index];
            Lines.RemoveAt(index);
            if (removed.Eol.Length == 0 && index == Lines.Count && index > 0) Lines[index - 1].Eol = "";
        }

        /// <summary>Adds <c>[section]</c> at the end, after a blank line when the file's last line is not blank; returns
        /// whether that blank line was added.</summary>
        public bool AppendSection(string section)
        {
            var blank = Lines.Count > 0 && !string.IsNullOrWhiteSpace(Lines[^1].Text);
            if (blank) Insert(Lines.Count, "");
            Insert(Lines.Count, $"[{section}]");
            return blank;
        }
    }
}
