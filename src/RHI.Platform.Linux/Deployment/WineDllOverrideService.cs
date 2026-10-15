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
        var sectionExistedBefore = WineHiveText.HasSection(text, SectionFor(applicationName));
        var read = ReadOverride(text, dllName, applicationName);
        var previous = read?.Text;
        var alreadyNative = read is { Raw: false } &&
                            (string.Equals(previous, RhiValue, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(previous, "native", StringComparison.OrdinalIgnoreCase));
        return new WineOverrideRecord
        {
            DllName = dllName, PreviousValue = previous, PreviousValueIsRaw = read?.Raw == true, ChangedByRhi = !alreadyNative,
            SectionCreatedByRhi = !alreadyNative && !sectionExistedBefore, ApplicationName = applicationName,
            WrittenValue = alreadyNative ? null : RhiValue
        };
    }

    /// <summary>
    /// <see cref="Restore"/> for a journaled removal: while the override still holds RHI's value, journals how to put it
    /// back (<see cref="WineOverrideReapplyCompensator"/>) before restoring, so a crash before the removal's record is
    /// gone is rolled back with the override as the record describes it. Refused before journaling while Wine runs in
    /// the prefix (call <see cref="EditRefusal"/> before the removal's first change). Without an operation it is
    /// <see cref="Restore"/>.
    /// </summary>
    public async Task<WineOverrideRestoreResult> RestoreAsync(
        DeploymentOperation? operation, string prefixPath, WineOverrideRecord? record, CancellationToken cancellationToken = default)
    {
        if (record is null || !record.ChangedByRhi) return WineOverrideRestoreResult.NotOwned(record?.DllName ?? "");
        if (operation is not null && HoldsWrittenValue(prefixPath, record))
        {
            if (EditRefusal(prefixPath) is { } refusal)
                throw new WineRegistryEditRefusedException(refusal, inUse: true);
            await operation.AddCompensationAsync(WineOverrideReapplyCompensator.KindName,
                WineOverrideReapplyCompensator.Describe(prefixPath, record), cancellationToken).ConfigureAwait(false);
        }
        return Restore(prefixPath, record);
    }

    /// <summary>
    /// <see cref="Reapply"/> for a journaled operation: while the override is at the value a restore left (so the reapply
    /// will act), journals how to undo it (<see cref="WineOverrideCompensator"/>, as for a fresh set) before writing, so
    /// a crash before the operation's commit point takes RHI's value out again. Refused before journaling while Wine
    /// runs in the prefix. Without an operation it is <see cref="Reapply"/>.
    /// </summary>
    public async Task<WineOverrideRestoreResult> ReapplyAsync(
        DeploymentOperation? operation, string prefixPath, WineOverrideRecord? record, CancellationToken cancellationToken = default)
    {
        if (record is null || !record.ChangedByRhi) return WineOverrideRestoreResult.NotOwned(record?.DllName ?? "");
        if (operation is not null && WineRegistryEditor.HasUserHive(prefixPath) && IsPreviousValue(CurrentValue(prefixPath, record), record))
        {
            if (EditRefusal(prefixPath) is { } refusal)
                throw new WineRegistryEditRefusedException(refusal, inUse: true);
            await operation.AddCompensationAsync(WineOverrideCompensator.KindName,
                WineOverrideCompensator.Describe(prefixPath, record), cancellationToken).ConfigureAwait(false);
        }
        return Reapply(prefixPath, record);
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
            var current = ReadOverride(text, record.DllName, record.ApplicationName);
            if (IsPreviousValue(current, record))
                return ((string?)null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.AlreadyRestored, record.DllName, current?.Text));
            if (!SameValue(current, WrittenBy(record)))
                return (null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, record.DllName, current?.Text));

            // A non-string earlier value (hex(2):…, dword:…) goes back as it was, type and continuation lines included.
            var updated = record.PreviousValue is null
                ? RemoveOverride(text, record.DllName, record.ApplicationName)
                : SetOverride(text, record.DllName, record.PreviousValue, record.ApplicationName, raw: record.PreviousValueIsRaw);
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
            var current = ReadOverride(text, record.DllName, record.ApplicationName);
            var written = WrittenBy(record);
            if (SameValue(current, written))
                return ((string?)null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.AlreadyRestored, record.DllName, current?.Text));
            if (!IsPreviousValue(current, record))
                return (null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, record.DllName, current?.Text));
            return (SetOverride(text, record.DllName, written, record.ApplicationName),
                new WineOverrideRestoreResult(WineOverrideRestoreOutcome.Restored, record.DllName, written));
        });
    }

    /// <summary>
    /// <see cref="Restore"/> for a rollback path: any failure (Wine started meanwhile, lock busy, I/O, or anything
    /// unexpected) is logged and returned as null instead of escaping the rollback or hiding behind an empty catch, so
    /// the rollback's remaining steps still run; a journaled operation's own rollback retries it, and start-up recovery
    /// after that.
    /// </summary>
    public WineOverrideRestoreResult? TryRestore(string? prefixPath, WineOverrideRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(prefixPath)) return null;
        try
        {
            return Restore(prefixPath, record);
        }
        catch (Exception ex)
        {
            AppLog.Warn("wine-registry", $"Rollback could not undo the Wine override {record.DllName} in {prefixPath}", ex);
            return null;
        }
    }

    /// <summary><see cref="Reapply"/> for a rollback path; any failure is logged and returned as null.</summary>
    public WineOverrideRestoreResult? TryReapply(string? prefixPath, WineOverrideRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(prefixPath)) return null;
        try
        {
            return Reapply(prefixPath, record);
        }
        catch (Exception ex)
        {
            AppLog.Warn("wine-registry", $"Rollback could not put back the Wine override {record.DllName} in {prefixPath}", ex);
            return null;
        }
    }

    /// <summary>Whether the override still holds the value RHI wrote for <paramref name="record"/> (read only).</summary>
    public static bool HoldsWrittenValue(string? prefixPath, WineOverrideRecord record)
        => record.ChangedByRhi && WineRegistryEditor.HasUserHive(prefixPath)
           && SameValue(ReadOverride(WineRegistryEditor.ReadUserHive(prefixPath!), record.DllName, record.ApplicationName), WrittenBy(record));

    /// <summary>The note a removal appends for every override it kept (empty when none was kept).</summary>
    public static string KeptValueNotes(params WineOverrideRestoreResult?[] results)
        => string.Concat(results.Where(result => result?.Outcome == WineOverrideRestoreOutcome.KeptLaterValue)
            .Select(result => $"{KeptUserValueNotePrefix}{result!.DllName} = '{result.CurrentValue ?? "-"}'."));

    /// <summary>The override's current value in the prefix's hive (read only, no lock).</summary>
    internal static OverrideValue? CurrentValue(string prefixPath, WineOverrideRecord record)
        => ReadOverride(WineRegistryEditor.ReadUserHive(prefixPath), record.DllName, record.ApplicationName);

    /// <summary>
    /// An override's value as the hive holds it: a string value unquoted (<see cref="Raw"/> false), any other type
    /// (<c>hex(2):…</c>, <c>dword:…</c>) as its raw right-hand side with its continuation lines (joined with a line feed).
    /// </summary>
    internal readonly record struct OverrideValue(string Text, bool Raw);

    /// <summary>Whether <paramref name="current"/> is the value the override had before RHI set it.</summary>
    internal static bool IsPreviousValue(OverrideValue? current, WineOverrideRecord record)
    {
        if (current is null || record.PreviousValue is null) return current is null && record.PreviousValue is null;
        if (record.PreviousValueIsRaw) return current.Value.Raw && string.Equals(current.Value.Text, record.PreviousValue, StringComparison.Ordinal);
        // A string earlier value, or one an older build recorded without its type (its first line only).
        var text = current.Value.Raw ? current.Value.Text.Split('\n')[0] : current.Value.Text;
        return SameValue(text, record.PreviousValue);
    }

    /// <summary>Whether <paramref name="current"/> is the string value <paramref name="value"/> (a typed value never is).</summary>
    internal static bool SameValue(OverrideValue? current, string value)
        => current is { Raw: false } read && SameValue(read.Text, value);

    internal static string WrittenBy(WineOverrideRecord record) => record.WrittenValue ?? RhiValue;

    /// <summary>Whether two records name the same override (DLL and AppDefaults application, as Wine compares keys).</summary>
    internal static bool SameOverride(WineOverrideRecord a, WineOverrideRecord b)
        => string.Equals(NormalizeDllName(a.DllName), NormalizeDllName(b.DllName), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeApplicationName(a.ApplicationName), NormalizeApplicationName(b.ApplicationName), StringComparison.OrdinalIgnoreCase);

    internal static bool SameValue(string? a, string? b)
        => a is null || b is null ? a is null && b is null : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string? TryReadOverride(string text, string dllName, string? applicationName = null)
        => ReadOverride(text, dllName, applicationName)?.Text;

    internal static OverrideValue? ReadOverride(string text, string dllName, string? applicationName = null)
        => WineHiveText.Read(text, SectionFor(NormalizeApplicationName(applicationName)), Quoted(dllName));

    /// <param name="raw">Write <paramref name="value"/> as a raw right-hand side (<see cref="OverrideValue.Raw"/>), not as a string.</param>
    internal static string SetOverride(string text, string dllName, string value, string? applicationName = null, bool raw = false)
        => WineHiveText.Set(text, SectionFor(NormalizeApplicationName(applicationName)), Quoted(dllName), raw ? value : $"\"{value}\"");

    internal static string RemoveOverride(string text, string dllName, string? applicationName = null)
        => WineHiveText.Remove(text, SectionFor(NormalizeApplicationName(applicationName)), Quoted(dllName));

    internal static string RemoveSectionIfEmpty(string text, string? applicationName = null)
        => WineHiveText.RemoveSectionIfEmpty(text, SectionFor(NormalizeApplicationName(applicationName)));

    private static string Quoted(string dllName) => $"\"{NormalizeDllName(dllName)}\"";

    private static string SectionFor(string? applicationName)
        => string.IsNullOrWhiteSpace(applicationName)
            ? GlobalSectionName
            : $@"[Software\\Wine\\AppDefaults\\{WineRegistryEditor.EscapeKeyName(applicationName)}\\DllOverrides]";

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
