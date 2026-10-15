using System.Globalization;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// A registry value RHI set in a Wine prefix (#92: the Vulkan implicit-layer registration in <c>system.reg</c>) and what
/// it was before, so removal puts back exactly the earlier state while the value still holds what RHI wrote.
/// </summary>
public sealed class WineRegistryValueRecord
{
    /// <summary><see cref="WineRegistryValueService.SystemHive"/> or <see cref="WineRegistryValueService.UserHive"/>.</summary>
    public string Hive { get; set; } = WineRegistryValueService.SystemHive;
    /// <summary>The key below the hive's root, Windows spelling (<c>Software\Khronos\Vulkan\ImplicitLayers</c>).</summary>
    public string Key { get; set; } = "";
    /// <summary>The value's name, Windows spelling (a json's <c>C:\…</c> path for an implicit layer).</summary>
    public string Name { get; set; } = "";
    /// <summary>The value's right-hand side before RHI set it, as the hive held it (<c>dword:00000001</c>, <c>"text"</c>), or null when absent.</summary>
    public string? PreviousValue { get; set; }
    public bool ChangedByRhi { get; set; }
    /// <summary>True only when RHI had to create the key's section.</summary>
    public bool SectionCreatedByRhi { get; set; }
    /// <summary>The right-hand side RHI wrote (<c>dword:00000000</c>); removal acts only while the value still holds it.</summary>
    public string? WrittenValue { get; set; }
}

/// <summary>
/// Sets and restores single registry values in a prefix's hives through <see cref="WineRegistryEditor"/> (refused while
/// Wine runs in the prefix, locked, backed up, atomic), with the same ownership rules as
/// <see cref="WineDllOverrideService"/>: a value someone changed after RHI set it is kept on removal, and every set or
/// restore made inside a journaled operation is undone by start-up recovery after a crash
/// (<see cref="WineRegistryValueCompensator"/>, <see cref="WineRegistryValueReapplyCompensator"/>).
/// </summary>
public sealed class WineRegistryValueService(WineRegistryEditor registry)
{
    public const string SystemHive = "system";
    public const string UserHive = "user";

    public WineRegistryEditor Registry { get; } = registry;

    /// <summary>A REG_DWORD right-hand side as Wine writes it.</summary>
    public static string Dword(uint value) => "dword:" + value.ToString("x8", CultureInfo.InvariantCulture);

    public string? EditRefusal(string? prefixPath) => Registry.EditRefusal(prefixPath);

    public static bool HasHive(string? prefixPath, string hive)
        => hive == SystemHive ? WineRegistryEditor.HasSystemHive(prefixPath) : WineRegistryEditor.HasUserHive(prefixPath);

    private static string ReadHive(string prefixPath, string hive)
        => hive == SystemHive ? WineRegistryEditor.ReadSystemHive(prefixPath) : WineRegistryEditor.ReadUserHive(prefixPath);

    private T Edit<T>(string prefixPath, string hive, Func<string, (string? Text, T Result)> edit)
        => hive == SystemHive ? Registry.EditSystemHive(prefixPath, edit) : Registry.EditUserHive(prefixPath, edit);

    /// <summary>The record <see cref="Ensure"/> would return, without changing anything (journal step before the write).</summary>
    public WineRegistryValueRecord Plan(string prefixPath, string hive, string key, string name, string rightHandSide, string? rhiNamePrefix = null)
    {
        if (!HasHive(prefixPath, hive))
            throw new FileNotFoundException(MissingHiveMessage(hive), HivePath(prefixPath, hive));
        return PlanFromText(ReadHive(prefixPath, hive), hive, key, name, rightHandSide, rhiNamePrefix);
    }

    /// <param name="rhiNamePrefix">
    /// Value names that only RHI writes in this key (the Vulkan layers' <c>C:\ProgramData\RHI-Linux\ReShadeVulkan\</c>): a
    /// section that holds nothing else is RHI's, so the value set last in a shared prefix also takes the emptied section
    /// away on removal, whichever game RHI set up first.
    /// </param>
    private static WineRegistryValueRecord PlanFromText(string text, string hive, string key, string name, string rightHandSide, string? rhiNamePrefix)
    {
        var current = WineHiveText.Read(text, Section(key), Quoted(name));
        var already = current is { } read && SameRaw(Raw(read), rightHandSide);
        return new WineRegistryValueRecord
        {
            Hive = hive, Key = key, Name = name,
            PreviousValue = current is { } value ? Raw(value) : null,
            ChangedByRhi = !already,
            SectionCreatedByRhi = !already && (!WineHiveText.HasSection(text, Section(key))
                || (rhiNamePrefix is not null && WineHiveText.HoldsOnlyValuesNamed(text, Section(key), "\"" + WineRegistryEditor.EscapeValueName(rhiNamePrefix)))),
            WrittenValue = already ? null : rightHandSide
        };
    }

    /// <summary>Sets the value (planned from the text read under the lock, so the record matches what was replaced).</summary>
    public WineRegistryValueRecord Ensure(string prefixPath, string hive, string key, string name, string rightHandSide, string? rhiNamePrefix = null)
        => Edit(prefixPath, hive, text =>
        {
            var plan = PlanFromText(text, hive, key, name, rightHandSide, rhiNamePrefix);
            return (plan.ChangedByRhi ? WineHiveText.Set(text, Section(key), Quoted(name), rightHandSide) : null, plan);
        });

    /// <summary>
    /// Journals the undo step in <paramref name="operation"/> (when there is one), then sets the value. Refused before the
    /// step is journaled while Wine runs in the prefix.
    /// </summary>
    public async Task<WineRegistryValueRecord> EnsureAsync(
        DeploymentOperation? operation, string prefixPath, string hive, string key, string name, string rightHandSide,
        CancellationToken cancellationToken = default, string? rhiNamePrefix = null)
    {
        if (EditRefusal(prefixPath) is { } refusal)
            throw new WineRegistryEditRefusedException(refusal, inUse: true);
        if (operation is not null)
            await operation.AddCompensationAsync(WineRegistryValueCompensator.KindName,
                WineRegistryValueCompensator.Describe(prefixPath, Plan(prefixPath, hive, key, name, rightHandSide, rhiNamePrefix)),
                cancellationToken).ConfigureAwait(false);
        return Ensure(prefixPath, hive, key, name, rightHandSide, rhiNamePrefix);
    }

    /// <summary>
    /// Puts back the earlier value (or removes the value, and its section when RHI created it) while the value still holds
    /// what RHI wrote; a value changed since is kept (<see cref="WineOverrideRestoreOutcome.KeptLaterValue"/>, logged).
    /// </summary>
    public WineOverrideRestoreResult Restore(string prefixPath, WineRegistryValueRecord? record)
    {
        if (record is null || !record.ChangedByRhi) return WineOverrideRestoreResult.NotOwned(record?.Name ?? "");
        if (!HasHive(prefixPath, record.Hive)) return new(WineOverrideRestoreOutcome.NothingToRestore, record.Name, null);
        var result = Edit(prefixPath, record.Hive, text =>
        {
            var current = CurrentIn(text, record);
            if (IsPrevious(current, record))
                return ((string?)null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.AlreadyRestored, record.Name, current));
            if (!SameRaw(current, record.WrittenValue))
                return (null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, record.Name, current));
            var section = Section(record.Key);
            var updated = record.PreviousValue is null
                ? WineHiveText.Remove(text, section, Quoted(record.Name))
                : WineHiveText.Set(text, section, Quoted(record.Name), record.PreviousValue);
            if (record.PreviousValue is null && record.SectionCreatedByRhi)
                updated = WineHiveText.RemoveSectionIfEmpty(updated, section);
            return (updated, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.Restored, record.Name, record.PreviousValue));
        });
        if (result.Outcome == WineOverrideRestoreOutcome.KeptLaterValue)
            AppLog.Warn("wine-registry", $"Registry value not restored because it was changed after RHI set it: {record.Key}\\{record.Name} ({prefixPath}) = '{result.CurrentValue ?? "-"}'");
        return result;
    }

    /// <summary>The inverse of <see cref="Restore"/> for a rollback: sets RHI's value again only while the value is still what the restore left.</summary>
    public WineOverrideRestoreResult Reapply(string prefixPath, WineRegistryValueRecord? record)
    {
        if (record is null || !record.ChangedByRhi || record.WrittenValue is null) return WineOverrideRestoreResult.NotOwned(record?.Name ?? "");
        if (!HasHive(prefixPath, record.Hive)) return new(WineOverrideRestoreOutcome.NothingToRestore, record.Name, null);
        return Edit(prefixPath, record.Hive, text =>
        {
            var current = CurrentIn(text, record);
            if (SameRaw(current, record.WrittenValue))
                return ((string?)null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.AlreadyRestored, record.Name, current));
            if (!IsPrevious(current, record))
                return (null, new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, record.Name, current));
            return (WineHiveText.Set(text, Section(record.Key), Quoted(record.Name), record.WrittenValue),
                new WineOverrideRestoreResult(WineOverrideRestoreOutcome.Restored, record.Name, record.WrittenValue));
        });
    }

    /// <summary>
    /// <see cref="Restore"/> for a journaled removal: while the value still holds RHI's value, journals how to put it back
    /// before restoring. Refused before journaling while Wine runs in the prefix.
    /// </summary>
    public async Task<WineOverrideRestoreResult> RestoreAsync(
        DeploymentOperation? operation, string prefixPath, WineRegistryValueRecord? record, CancellationToken cancellationToken = default)
    {
        if (record is null || !record.ChangedByRhi) return WineOverrideRestoreResult.NotOwned(record?.Name ?? "");
        if (operation is not null && HoldsWrittenValue(prefixPath, record))
        {
            if (EditRefusal(prefixPath) is { } refusal)
                throw new WineRegistryEditRefusedException(refusal, inUse: true);
            await operation.AddCompensationAsync(WineRegistryValueReapplyCompensator.KindName,
                WineRegistryValueReapplyCompensator.Describe(prefixPath, record), cancellationToken).ConfigureAwait(false);
        }
        return Restore(prefixPath, record);
    }

    /// <summary><see cref="Restore"/> for a rollback path: a failure is logged and returned as null.</summary>
    public WineOverrideRestoreResult? TryRestore(string? prefixPath, WineRegistryValueRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(prefixPath)) return null;
        try
        {
            return Restore(prefixPath, record);
        }
        catch (Exception ex)
        {
            AppLog.Warn("wine-registry", $"Rollback could not undo the registry value {record.Key}\\{record.Name} in {prefixPath}", ex);
            return null;
        }
    }

    /// <summary><see cref="Reapply"/> for a rollback path: a failure is logged and returned as null.</summary>
    public WineOverrideRestoreResult? TryReapply(string? prefixPath, WineRegistryValueRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(prefixPath)) return null;
        try
        {
            return Reapply(prefixPath, record);
        }
        catch (Exception ex)
        {
            AppLog.Warn("wine-registry", $"Rollback could not put back the registry value {record.Key}\\{record.Name} in {prefixPath}", ex);
            return null;
        }
    }

    /// <summary>Whether the value still holds what RHI wrote (read only).</summary>
    public static bool HoldsWrittenValue(string? prefixPath, WineRegistryValueRecord record)
        => record.ChangedByRhi && HasHive(prefixPath, record.Hive) && SameRaw(Current(prefixPath!, record), record.WrittenValue);

    /// <summary>The value's right-hand side in the prefix's hive now, or null (read only, no lock).</summary>
    public static string? Current(string prefixPath, WineRegistryValueRecord record) => CurrentIn(ReadHive(prefixPath, record.Hive), record);

    private static string? CurrentIn(string text, WineRegistryValueRecord record)
        => WineHiveText.Read(text, Section(record.Key), Quoted(record.Name)) is { } value ? Raw(value) : null;

    internal static bool IsPrevious(string? current, WineRegistryValueRecord record)
        => current is null || record.PreviousValue is null ? current is null && record.PreviousValue is null : SameRaw(current, record.PreviousValue);

    internal static bool SameRaw(string? a, string? b)
        => a is null || b is null ? a is null && b is null : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether two records name the same value (hive, key and name, as Wine compares them).</summary>
    internal static bool SameValue(WineRegistryValueRecord a, WineRegistryValueRecord b)
        => a.Hive == b.Hive
           && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The value's whole right-hand side: a string re-quoted, any other type as the hive holds it.</summary>
    private static string Raw(WineDllOverrideService.OverrideValue value) => value.Raw ? value.Text : $"\"{value.Text}\"";

    /// <summary>The hive's header for <paramref name="key"/> (each key name escaped, joined by an escaped backslash).</summary>
    internal static string Section(string key)
        => "[" + string.Join(@"\\", key.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(WineRegistryEditor.EscapeKeyName)) + "]";

    internal static string Quoted(string name) => $"\"{WineRegistryEditor.EscapeValueName(name)}\"";

    private static string HivePath(string prefixPath, string hive)
        => hive == SystemHive ? WineRegistryEditor.SystemHivePath(prefixPath) : WineRegistryEditor.UserHivePath(prefixPath);

    private static string MissingHiveMessage(string hive)
        => hive == SystemHive ? WineRegistryEditor.MissingSystemHiveMessage : WineRegistryEditor.MissingHiveMessage;
}

/// <summary>
/// Journal step that undoes a registry value RHI set (<see cref="WineRegistryValueService.EnsureAsync"/>): only while the
/// value still holds what RHI wrote; deferred while a wineserver runs for the prefix (it would write the hive back from
/// memory). A step whose value is at its earlier state was never written or is already undone, so it is done at once.
/// A record of the same game saved after the step that holds the same value now depends on it: the step is then left
/// alone (the user installed again while this undo waited).
/// </summary>
public sealed class WineRegistryValueCompensator(
    WineRegistryValueService values, Func<string, bool>? isPrefixInUse = null, LinuxDeploymentRecordStore? records = null) : IDeploymentCompensator
{
    public const string KindName = "wine-registry-value";
    private readonly Func<string, bool> _isPrefixInUse = isPrefixInUse ?? values.Registry.IsPrefixInUse;

    public string Kind => KindName;

    public static string Describe(string prefixPath, WineRegistryValueRecord record, DateTime? journaledUtc = null)
        => JsonSerializer.Serialize(new Payload { PrefixPath = prefixPath, Record = record, JournaledUtc = journaledUtc ?? DateTime.UtcNow });

    public CompensationResult Compensate(string data)
    {
        var payload = JsonSerializer.Deserialize<Payload>(data) ?? throw new InvalidDataException("Empty registry value journal step.");
        if (payload.Record is not { } record || string.IsNullOrWhiteSpace(payload.PrefixPath))
            throw new InvalidDataException("Incomplete registry value journal step.");
        var prefix = payload.PrefixPath;
        if (!record.ChangedByRhi || !Directory.Exists(prefix) || !WineRegistryValueService.HasHive(prefix, record.Hive))
            return CompensationResult.Done;
        var current = WineRegistryValueService.Current(prefix, record);
        if (WineRegistryValueService.IsPrevious(current, record)) return CompensationResult.Done;
        if (!WineRegistryValueService.SameRaw(current, record.WrittenValue)) return KeptLaterValue(record, prefix, current);
        if (SupersededByLaterRecord(payload, record)) return CompensationResult.Done;
        if (_isPrefixInUse(prefix)) return DeferredWhileWineRuns(record, prefix);
        WineOverrideRestoreResult result;
        try
        {
            result = values.Restore(prefix, record);
        }
        catch (WineRegistryEditRefusedException ex) when (ex.PrefixInUse)
        {
            return DeferredWhileWineRuns(record, prefix);
        }
        return result.Outcome == WineOverrideRestoreOutcome.KeptLaterValue
            ? KeptLaterValue(record, prefix, result.CurrentValue)
            : CompensationResult.Done;
    }

    private bool SupersededByLaterRecord(Payload payload, WineRegistryValueRecord step)
    {
        if (records is null || payload.JournaledUtc is not { } journaled) return false;
        if (records.CorruptLedgerPath is { } corrupt)
            throw new IOException(WineOverrideCompensator.UnreadableLedgerPrefix + corrupt);
        var later = records.All()
            .Where(record => Utc(record.SavedAtUtc ?? record.InstalledAtUtc) >= Utc(journaled))
            .Select(record => (Record: record, Value: record.RegistryValues.FirstOrDefault(value => WineRegistryValueService.SameValue(value, step))))
            .Where(claim => claim.Value is not null)
            .OrderBy(claim => claim.Record.InstalledAtUtc)
            .ToList();
        if (later.Count == 0) return false;
        if (later.All(claim => !claim.Value!.ChangedByRhi))
        {
            // The later install found RHI's value already there: it takes over this step's ownership, so its removal
            // restores the value the key had before either install (as WineOverrideCompensator does for overrides).
            var (record, adopted) = later[0];
            adopted!.ChangedByRhi = true;
            adopted.PreviousValue = step.PreviousValue;
            adopted.SectionCreatedByRhi = step.SectionCreatedByRhi;
            adopted.WrittenValue = step.WrittenValue;
            try
            {
                records.Upsert(record);
            }
            catch (InvalidOperationException ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }
        return true;
    }

    private static DateTime Utc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Local => time.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _ => time
    };

    internal static CompensationResult KeptLaterValue(WineRegistryValueRecord record, string prefix, string? current)
        => new(CompensationStatus.KeptLaterChange,
            $"This registry value was changed after the interruption and was left as it is: {record.Key}\\{record.Name} ({prefix}) = '{current ?? "-"}'");

    internal static CompensationResult DeferredWhileWineRuns(WineRegistryValueRecord record, string prefix)
        => new(CompensationStatus.Deferred,
            $"Wine is running in this prefix and would overwrite the change when it exits, so this registry value is undone at the next start: {record.Key}\\{record.Name} ({prefix})");

    private sealed class Payload
    {
        public string PrefixPath { get; set; } = "";
        public WineRegistryValueRecord? Record { get; set; }
        public DateTime? JournaledUtc { get; set; }
    }
}

/// <summary>
/// Journal step of a removal that restores a registry value (<see cref="WineRegistryValueService.RestoreAsync"/>): when the
/// removal is rolled back after a crash, RHI's value is put back while the value is still what the restore left.
/// </summary>
public sealed class WineRegistryValueReapplyCompensator(WineRegistryValueService values, Func<string, bool>? isPrefixInUse = null) : IDeploymentCompensator
{
    public const string KindName = "wine-registry-value-reapply";
    private readonly Func<string, bool> _isPrefixInUse = isPrefixInUse ?? values.Registry.IsPrefixInUse;

    public string Kind => KindName;

    public static string Describe(string prefixPath, WineRegistryValueRecord record)
        => JsonSerializer.Serialize(new Payload { PrefixPath = prefixPath, Record = record });

    public CompensationResult Compensate(string data)
    {
        var payload = JsonSerializer.Deserialize<Payload>(data) ?? throw new InvalidDataException("Empty registry value journal step.");
        if (payload.Record is not { } record || string.IsNullOrWhiteSpace(payload.PrefixPath))
            throw new InvalidDataException("Incomplete registry value journal step.");
        var prefix = payload.PrefixPath;
        if (!record.ChangedByRhi || !Directory.Exists(prefix) || !WineRegistryValueService.HasHive(prefix, record.Hive))
            return CompensationResult.Done;
        var current = WineRegistryValueService.Current(prefix, record);
        if (WineRegistryValueService.SameRaw(current, record.WrittenValue)) return CompensationResult.Done;
        if (!WineRegistryValueService.IsPrevious(current, record)) return WineRegistryValueCompensator.KeptLaterValue(record, prefix, current);
        if (_isPrefixInUse(prefix)) return WineRegistryValueCompensator.DeferredWhileWineRuns(record, prefix);
        WineOverrideRestoreResult result;
        try
        {
            result = values.Reapply(prefix, record);
        }
        catch (WineRegistryEditRefusedException ex) when (ex.PrefixInUse)
        {
            return WineRegistryValueCompensator.DeferredWhileWineRuns(record, prefix);
        }
        return result.Outcome == WineOverrideRestoreOutcome.KeptLaterValue
            ? WineRegistryValueCompensator.KeptLaterValue(record, prefix, result.CurrentValue)
            : CompensationResult.Done;
    }

    private sealed class Payload
    {
        public string PrefixPath { get; set; } = "";
        public WineRegistryValueRecord? Record { get; set; }
    }
}
