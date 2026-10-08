using System.Globalization;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Conservative editor for Wine's DLL override registry. RHI uses per-application
/// AppDefaults whenever the executable name is known, which is essential for shared
/// Heroic/Lutris prefixes: enabling dxgi for one game must not affect every game in the prefix.
/// Every change goes through <see cref="WineRegistryEditor"/> (refused while Wine runs in the prefix, locked,
/// backed up, atomic). <see cref="Restore"/> undoes only RHI's own value (#30); a value set later is kept.
/// </summary>
public sealed class WineDllOverrideService
{
    internal const string GlobalSectionName = @"[Software\\Wine\\DllOverrides]";

    /// <summary>The only value RHI writes.</summary>
    public const string RhiValue = "native,builtin";

    /// <summary>Fixed text of the note a removal adds when it kept a later value; "dll = 'value'" follows.</summary>
    public const string KeptUserValueNotePrefix = " RHI left this Wine override as it is, because it was changed after RHI set it: ";

    public WineDllOverrideService(WineRegistryEditor? registry = null) => Registry = registry ?? new WineRegistryEditor();

    /// <summary>The prefix registry writer every override change goes through.</summary>
    public WineRegistryEditor Registry { get; }

    /// <summary>Why a registry change in <paramref name="prefixPath"/> would be refused right now (Wine runs there), or null.</summary>
    public string? EditRefusal(string? prefixPath) => Registry.EditRefusal(prefixPath);

    public WineOverrideRecord EnsureNativeBuiltin(string prefixPath, string dllName, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        applicationName = NormalizeApplicationName(applicationName);
        // Planned from the text read under the lock, so the record matches what was really replaced.
        return Registry.EditUserHive(prefixPath, text =>
        {
            var plan = Plan(text, dllName, applicationName);
            return (plan.ChangedByRhi ? SetOverride(text, dllName, RhiValue, applicationName) : null, plan);
        });
    }

    /// <summary>
    /// Journals the undo step in <paramref name="operation"/> (when there is one), then sets the override: a crash
    /// between this write and the caller's record leaves a journal entry that start-up recovery rolls back (#23).
    /// Refused before the step is journaled while Wine runs in the prefix (the editor would refuse the write), so a
    /// game started during a download never leaves an undo step for an override RHI did not write.
    /// </summary>
    public async Task<WineOverrideRecord> EnsureNativeBuiltinAsync(
        DeploymentOperation? operation, string prefixPath, string dllName, string? applicationName = null, CancellationToken cancellationToken = default)
    {
        if (EditRefusal(prefixPath) is { } refusal)
            throw new WineRegistryEditRefusedException(refusal, inUse: true);
        if (operation is not null)
            await operation.AddCompensationAsync(WineOverrideCompensator.KindName,
                WineOverrideCompensator.Describe(prefixPath, PlanNativeBuiltin(prefixPath, dllName, applicationName),
                    LinuxDeploymentRecovery.GameKeyOfCommitProbe(operation.CommitProbe), DateTime.UtcNow),
                cancellationToken).ConfigureAwait(false);
        return EnsureNativeBuiltin(prefixPath, dllName, applicationName);
    }

    /// <summary>
    /// The record <see cref="EnsureNativeBuiltin"/> would return, without changing the registry, so a
    /// journal can hold the exact undo step before the override is written. <see cref="Restore"/>
    /// with this record is safe whether or not the override was then written.
    /// </summary>
    public WineOverrideRecord PlanNativeBuiltin(string prefixPath, string dllName, string? applicationName = null)
    {
        if (!WineRegistryEditor.HasUserHive(prefixPath))
            throw new FileNotFoundException(WineRegistryEditor.MissingHiveMessage, WineRegistryEditor.UserHivePath(prefixPath));
        return Plan(WineRegistryEditor.ReadUserHive(prefixPath), NormalizeDllName(dllName), NormalizeApplicationName(applicationName));
    }

    private static WineOverrideRecord Plan(string text, string dllName, string? applicationName)
    {
        var sectionExistedBefore = FindSection(SplitLines(text), SectionFor(applicationName)).Start >= 0;
        var previous = TryReadOverride(text, dllName, applicationName);
        var alreadyNative = string.Equals(previous, RhiValue, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(previous, "native", StringComparison.OrdinalIgnoreCase);
        return new WineOverrideRecord
        {
            DllName = dllName, PreviousValue = previous, ChangedByRhi = !alreadyNative,
            SectionCreatedByRhi = !alreadyNative && !sectionExistedBefore, ApplicationName = applicationName,
            WrittenValue = alreadyNative ? null : RhiValue
        };
    }

    /// <summary>
    /// Puts back the value the override had before RHI set it, but only while it still holds the value RHI wrote
    /// (#30): a value the user or another tool set since is kept (<see cref="WineOverrideRestoreOutcome.KeptLaterValue"/>,
    /// logged). Idempotent: an override already back at its earlier value is left alone.
    /// </summary>
    public WineOverrideRestoreResult Restore(string prefixPath, WineOverrideRecord? record)
    {
        if (record is null || !record.ChangedByRhi) return WineOverrideRestoreResult.NotOwned(record?.DllName ?? "");
        if (!WineRegistryEditor.HasUserHive(prefixPath)) return new(WineOverrideRestoreOutcome.NothingToRestore, record.DllName, null);
        var result = Registry.EditUserHive(prefixPath, text =>
        {
            var current = TryReadOverride(text, record.DllName, record.ApplicationName);
            if (SameValue(current, record.PreviousValue))
                return ((string?)null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.AlreadyRestored, record.DllName, current));
            if (!SameValue(current, WrittenBy(record)))
                return (null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, record.DllName, current));

            var updated = record.PreviousValue is null
                ? RemoveOverride(text, record.DllName, record.ApplicationName)
                : SetOverride(text, record.DllName, record.PreviousValue, record.ApplicationName);
            // Only remove a now-empty section when this install explicitly recorded that
            // RHI created it. Older deployment records deserialize this flag as false,
            // which intentionally favors leaving an empty section over deleting a key that
            // may have existed before RHI learned to track section ownership.
            if (record.PreviousValue is null && record.SectionCreatedByRhi)
                updated = RemoveSectionIfEmpty(updated, record.ApplicationName);
            return (updated, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.Restored, record.DllName, record.PreviousValue));
        });
        if (result.Outcome == WineOverrideRestoreOutcome.KeptLaterValue)
            AppLog.Warn("wine-registry", $"Wine override not restored because it was changed after RHI set it: {record.DllName} ({prefixPath}) = '{result.CurrentValue ?? "-"}'");
        return result;
    }

    /// <summary>
    /// The inverse of <see cref="Restore"/> for a rollback that must put RHI's value back (a removal that failed half
    /// way): sets it again only while the override is still at the value <see cref="Restore"/> left; a value someone
    /// set in between is kept.
    /// </summary>
    public WineOverrideRestoreResult Reapply(string prefixPath, WineOverrideRecord? record)
    {
        if (record is null || !record.ChangedByRhi) return WineOverrideRestoreResult.NotOwned(record?.DllName ?? "");
        if (!WineRegistryEditor.HasUserHive(prefixPath)) return new(WineOverrideRestoreOutcome.NothingToRestore, record.DllName, null);
        return Registry.EditUserHive(prefixPath, text =>
        {
            var current = TryReadOverride(text, record.DllName, record.ApplicationName);
            var written = WrittenBy(record);
            if (SameValue(current, written))
                return ((string?)null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.AlreadyRestored, record.DllName, current));
            if (!SameValue(current, record.PreviousValue))
                return (null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, record.DllName, current));
            return (SetOverride(text, record.DllName, written, record.ApplicationName),
                new WineOverrideRestoreResult(WineOverrideRestoreOutcome.Restored, record.DllName, written));
        });
    }

    /// <summary>
    /// <see cref="Restore"/> for a rollback path: a failure (Wine started meanwhile, lock busy, I/O) is logged and
    /// returned as null instead of hiding behind an empty catch; a journaled operation's own rollback retries it,
    /// and start-up recovery after that.
    /// </summary>
    public WineOverrideRestoreResult? TryRestore(string? prefixPath, WineOverrideRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(prefixPath)) return null;
        try
        {
            return Restore(prefixPath, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("wine-registry", $"Rollback could not undo the Wine override {record.DllName} in {prefixPath}", ex);
            return null;
        }
    }

    /// <summary><see cref="Reapply"/> for a rollback path; failures are logged and returned as null.</summary>
    public WineOverrideRestoreResult? TryReapply(string? prefixPath, WineOverrideRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(prefixPath)) return null;
        try
        {
            return Reapply(prefixPath, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("wine-registry", $"Rollback could not put back the Wine override {record.DllName} in {prefixPath}", ex);
            return null;
        }
    }

    /// <summary>Whether the override still holds the value RHI wrote for <paramref name="record"/> (read only).</summary>
    public static bool HoldsWrittenValue(string? prefixPath, WineOverrideRecord record)
        => record.ChangedByRhi && WineRegistryEditor.HasUserHive(prefixPath)
           && SameValue(TryReadOverride(WineRegistryEditor.ReadUserHive(prefixPath!), record.DllName, record.ApplicationName), WrittenBy(record));

    /// <summary>The note a removal appends for every override it kept (empty when none was kept).</summary>
    public static string KeptValueNotes(params WineOverrideRestoreResult?[] results)
        => string.Concat(results.Where(result => result?.Outcome == WineOverrideRestoreOutcome.KeptLaterValue)
            .Select(result => $"{KeptUserValueNotePrefix}{result!.DllName} = '{result.CurrentValue ?? "-"}'."));

    /// <summary>The override's current value in the prefix's hive (read only, no lock).</summary>
    internal static string? CurrentValue(string prefixPath, WineOverrideRecord record)
        => TryReadOverride(WineRegistryEditor.ReadUserHive(prefixPath), record.DllName, record.ApplicationName);

    internal static string WrittenBy(WineOverrideRecord record) => record.WrittenValue ?? RhiValue;

    /// <summary>Whether two records name the same override (DLL and AppDefaults application, as Wine compares keys).</summary>
    internal static bool SameOverride(WineOverrideRecord a, WineOverrideRecord b)
        => string.Equals(NormalizeDllName(a.DllName), NormalizeDllName(b.DllName), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeApplicationName(a.ApplicationName), NormalizeApplicationName(b.ApplicationName), StringComparison.OrdinalIgnoreCase);

    internal static bool SameValue(string? a, string? b)
        => a is null || b is null ? a is null && b is null : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string? TryReadOverride(string text, string dllName, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        var sectionName = SectionFor(NormalizeApplicationName(applicationName));
        var lines = SplitLines(text);
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return null;
        var prefix = $"\"{dllName}\"=";
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rhs = line[(line.IndexOf('=') + 1)..].Trim();
            if (rhs.Length >= 2 && rhs[0] == '"' && rhs[^1] == '"') return rhs[1..^1];
            return rhs;
        }
        return null;
    }

    internal static string SetOverride(string text, string dllName, string value, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        applicationName = NormalizeApplicationName(applicationName);
        var sectionName = SectionFor(applicationName);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        var entry = $"\"{dllName}\"=\"{value}\"";
        if (start < 0)
        {
            // A hive without a final newline (Wine always writes one) gets none after the new section either, so
            // RemoveSectionIfEmpty can give back exactly the bytes it had.
            var finalNewline = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r');
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fileTime = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L) * 10_000_000L;
            lines.Add($"{sectionName} {now}");
            lines.Add($"#time={fileTime.ToString("x", CultureInfo.InvariantCulture)}");
            lines.Add(entry);
            return string.Join(newline, lines) + (finalNewline ? newline : "");
        }

        var prefix = $"\"{dllName}\"=";
        for (var i = start + 1; i < end; i++)
        {
            if (!lines[i].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveRange(i + 1, ContinuationLines(lines, i, end));
            lines[i] = entry;
            return string.Join(newline, lines);
        }
        // After the section's last value, not after the blank separator line (or the empty string that follows a
        // final newline), so the hive keeps its layout and its final newline.
        var at = end;
        while (at > start + 1 && lines[at - 1].Length == 0) at--;
        lines.Insert(at, entry);
        return string.Join(newline, lines);
    }

    internal static string RemoveOverride(string text, string dllName, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        var sectionName = SectionFor(NormalizeApplicationName(applicationName));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return text;
        var prefix = $"\"{dllName}\"=";
        for (var i = start + 1; i < end; i++)
        {
            if (!lines[i].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveRange(i, 1 + ContinuationLines(lines, i, end));
            break;
        }
        return string.Join(newline, lines);
    }


    internal static string RemoveSectionIfEmpty(string text, string? applicationName = null)
    {
        applicationName = NormalizeApplicationName(applicationName);
        var sectionName = SectionFor(applicationName);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return text;

        // Be deliberately conservative: comments (#time), blank lines and the section
        // header itself are metadata. Any other content means somebody added data to
        // the key while RHI was installed, so the section must survive uninstall.
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            return text;
        }

        var removeEnd = end;
        // Consume the separator blank line after the section when present, but never
        // touch the next registry section. This avoids leaving extra blank blocks.
        while (removeEnd < lines.Count && lines[removeEnd].Length == 0) removeEnd++;
        // The last section of a hive without a final newline (as SetOverride leaves one it created there): its
        // separator blank line goes too, so the hive ends where it ended before the section was added.
        var lastWithoutFinalNewline = removeEnd == lines.Count && lines[^1].Length != 0;
        if (lastWithoutFinalNewline && start > 0 && lines[start - 1].Length == 0) start--;
        lines.RemoveRange(start, removeEnd - start);

        // Avoid creating a trailing stack of empty lines after section removal.
        while (lines.Count > 1 && lines[^1].Length == 0 && lines[^2].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return string.Join(newline, lines);
    }

    /// <summary>
    /// How many lines after <paramref name="valueLine"/> continue its value: Wine wraps long hex values with a trailing
    /// backslash (a quoted string value always ends with its closing quote).
    /// </summary>
    private static int ContinuationLines(IReadOnlyList<string> lines, int valueLine, int sectionEnd)
    {
        var count = 0;
        for (var i = valueLine; i + 1 < sectionEnd && lines[i].TrimEnd().EndsWith('\\'); i++) count++;
        return count;
    }

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string sectionName)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (start < 0)
            {
                if (line.StartsWith(sectionName, StringComparison.OrdinalIgnoreCase)) start = i;
                continue;
            }
            if (line.StartsWith("[", StringComparison.Ordinal)) return (start, i);
        }
        return start < 0 ? (-1, -1) : (start, lines.Count);
    }

    private static string SectionFor(string? applicationName)
        => string.IsNullOrWhiteSpace(applicationName)
            ? GlobalSectionName
            : $@"[Software\\Wine\\AppDefaults\\{WineRegistryEditor.EscapeKeyName(applicationName)}\\DllOverrides]";

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string NormalizeDllName(string name) => Path.GetFileNameWithoutExtension(name.Trim()).ToLowerInvariant();
    private static string? NormalizeApplicationName(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name.Trim());
}

public enum WineOverrideRestoreOutcome
{
    /// <summary>RHI did not change this override (or there is no record): nothing to do.</summary>
    NotOwned,

    /// <summary>The prefix has no user.reg any more (deleted or reset): nothing to do.</summary>
    NothingToRestore,

    /// <summary>The override already held the target value; nothing was written.</summary>
    AlreadyRestored,

    /// <summary>The target value was written.</summary>
    Restored,

    /// <summary>The override holds a value neither RHI nor the earlier state had: someone changed it, so it was kept.</summary>
    KeptLaterValue
}

/// <summary>What <see cref="WineDllOverrideService.Restore"/> (or <see cref="WineDllOverrideService.Reapply"/>) did; <see cref="CurrentValue"/> is the override's value afterwards.</summary>
public sealed record WineOverrideRestoreResult(WineOverrideRestoreOutcome Outcome, string DllName, string? CurrentValue)
{
    public static WineOverrideRestoreResult NotOwned(string dllName) => new(WineOverrideRestoreOutcome.NotOwned, dllName, null);
}
