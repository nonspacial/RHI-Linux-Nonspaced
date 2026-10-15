using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>The global peak brightness (#182; upstream Settings "HDR &amp; Peak Brightness").</summary>
/// <param name="Nits">Peak brightness in nits; 0 = not set.</param>
/// <param name="Enabled">Written into RenoDX presets when RenoDX is installed (upstream "Auto-apply peak nits on deploy").</param>
/// <param name="Presets">The <c>[renodx-presetN]</c> numbers it goes into (upstream default 1, 2, 3).</param>
public sealed record PeakNitsSettings(int Nits, bool Enabled, IReadOnlyList<int> Presets)
{
    public static PeakNitsSettings From(LinuxAppSettings settings) => new(settings.PeakNits, settings.PeakNitsEnabled, settings.PeakNitsPresets);

    public bool Applies => Enabled && Nits > 0 && Presets.Count > 0;
}

/// <summary>
/// RenoDX's <c>ToneMapPeakNits</c> (#182; upstream <c>AuxInstallService.ApplyPeakNits</c>, the RenoDX ⚙ "Set Maximum
/// Nits", "Apply to all games"). Every write goes through <see cref="RenoDxIniOverrideManager"/>'s owned-key state,
/// one state per <c>[renodx-presetN]</c> section, kept on the RenoDX record (<see cref="MetadataKey"/>), so removing
/// RenoDX puts back the value from before RHI. An install or update re-applies the global value where the key still
/// holds what RHI wrote (a value changed in RenoDX's overlay is kept); the explicit actions ("Apply to all games",
/// "Set maximum nits") write it whatever is there, as upstream, and keep the pre-RHI value as the restore point.
/// </summary>
public sealed class RenoDxPeakNits(LinuxDeploymentRecordStore records, DeploymentJournal? journal, Func<DetectedGame, string?> writeRefusal)
{
    public const string MetadataKey = "RenoDX.PeakNits";
    public const string Key = "ToneMapPeakNits";
    public const int MaxNits = 10_000;

    public const string NotManagedPrefix = "Peak brightness goes into RenoDX presets; install RenoDX with RHI first: ";
    public const string NoReShadeIniPrefix = "This game has no ReShade.ini yet; start it once with RenoDX so RenoDX writes its presets: ";

    internal static Dictionary<string, RenoDxIniOverrideState> Read(LinuxDeploymentRecord? record)
    {
        if (record?.Metadata.GetValueOrDefault(MetadataKey) is not { Length: > 0 } json) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return new Dictionary<string, RenoDxIniOverrideState>(
                JsonSerializer.Deserialize<Dictionary<string, RenoDxIniOverrideState>>(json) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            AppLog.Warn("renodx-peak-nits", "Stored peak brightness state could not be read; it is treated as absent", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static string Serialize(IReadOnlyDictionary<string, RenoDxIniOverrideState> states) => JsonSerializer.Serialize(states);

    /// <summary>
    /// Writes <paramref name="nits"/> as <c>ToneMapPeakNits</c> into each section (created when missing, as upstream).
    /// <paramref name="force"/>: an explicit user action — write it even where the value changed since RHI's last write;
    /// the entry keeps the value from before RHI's first write as its restore point.
    /// </summary>
    internal static Dictionary<string, RenoDxIniOverrideState> Apply(string binaryDirectory, string? gameRoot, int nits,
        IEnumerable<string> sections, IReadOnlyDictionary<string, RenoDxIniOverrideState> previous, bool force)
    {
        var next = new Dictionary<string, RenoDxIniOverrideState>(previous, StringComparer.OrdinalIgnoreCase);
        var request = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Key] = nits.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var section in sections.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            previous.TryGetValue(section, out var old);
            var result = RenoDxIniOverrideManager.Apply(binaryDirectory, request, force ? null : old, gameRoot: gameRoot, section: section);
            if (result.State is not { } state) continue;
            if (force && old is not null)
            {
                // The first restore point (before RHI ever wrote the key) stays.
                var first = old.Entries.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
                state = state with
                {
                    SectionCreatedByRhi = state.SectionCreatedByRhi || old.SectionCreatedByRhi,
                    Entries = state.Entries.Select(entry => first.TryGetValue(entry.Key, out var o)
                        ? entry with { HadPreviousValue = o.HadPreviousValue, PreviousValue = o.PreviousValue }
                        : entry).ToArray()
                };
            }
            next[section] = state;
        }
        return next;
    }

    /// <summary>Puts back every section's value from before RHI (a value the user changed since stays).</summary>
    internal static void Cleanup(IReadOnlyDictionary<string, RenoDxIniOverrideState> states, string? gameRoot)
    {
        foreach (var state in states.Values) RenoDxIniOverrideManager.Cleanup(state, gameRoot: gameRoot);
    }

    /// <summary>The sections for the global setting: <c>renodx-preset1..3</c> as chosen.</summary>
    public static IEnumerable<string> GlobalSections(IEnumerable<int> presets) => presets.Where(n => n is >= 1 and <= 3).Distinct().Order().Select(n => $"renodx-preset{n}");

    /// <summary>Upstream's per-game "Set Maximum Nits": every <c>[renodx-presetN]</c> already in the file, or preset 1.</summary>
    public static IReadOnlyList<string> GameSections(string iniPath)
    {
        var sections = IniLines.Sections(IniLines.Read(iniPath))
            .Where(name => name.StartsWith("renodx-preset", StringComparison.OrdinalIgnoreCase) && name.Length > "renodx-preset".Length
                           && name["renodx-preset".Length..].All(char.IsAsciiDigit))
            .ToList();
        return sections.Count > 0 ? sections : ["renodx-preset1"];
    }

    /// <summary>Whether the game has a ReShade.ini for RenoDX's presets (upstream shows "Set Maximum Nits" only then).</summary>
    public static bool HasReShadeIni(string binaryDirectory)
        => RenoDxIniOverrideManager.ResolveIniPath(binaryDirectory) is { } ini && File.Exists(ini);

    /// <summary>The value RenoDX will start with: the first preset's <c>ToneMapPeakNits</c>, or null.</summary>
    public static int? CurrentNits(string binaryDirectory)
    {
        if (RenoDxIniOverrideManager.ResolveIniPath(binaryDirectory) is not { } ini || !File.Exists(ini)) return null;
        var lines = IniLines.Read(ini);
        foreach (var section in GameSections(ini))
            if (IniLines.Get(lines, section, Key) is { } value && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nits))
                return (int)nits;
        return null;
    }

    /// <summary>"Apply to all games": the global value into every game whose RenoDX RHI installed (one game per unit).</summary>
    public Task<IniBatchResult> ApplyToAllAsync(IReadOnlyList<DetectedGame> games, PeakNitsSettings settings, CancellationToken cancellationToken = default)
        => UserIniService.RunBatchAsync(
            games.Where(game => records.Find(game, ManagedComponentKind.RenoDX) is not null).ToList(),
            (game, token) => ChangeAsync(game, settings.Nits, _ => GlobalSections(settings.Presets), token),
            "renodx-peak-nits", cancellationToken);

    /// <summary>The RenoDX ⚙ "Set maximum nits" for one game: every preset in its ReShade.ini, or preset 1.</summary>
    public Task<ComponentOperationResult> SetForGameAsync(DetectedGame game, int nits, CancellationToken cancellationToken = default)
        => ChangeAsync(game, nits, GameSections, cancellationToken);

    private async Task<ComponentOperationResult> ChangeAsync(DetectedGame game, int nits, Func<string, IEnumerable<string>> sections, CancellationToken cancellationToken)
    {
        if (nits is < 1 or > MaxNits) throw new ArgumentOutOfRangeException(nameof(nits), nits, "Peak brightness must be 1–10000 nits.");
        if (writeRefusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        // Find hands out a copy; the ledger changes only through the stamped Upsert below.
        var record = records.Find(game, ManagedComponentKind.RenoDX);
        if (record is null || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return ComponentOperationResult.Fail(NotManagedPrefix + game.Name);
        if (RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory) is not { } ini || !File.Exists(ini))
            return ComponentOperationResult.Fail(NoReShadeIniPrefix + game.Name);
        if (XboxWriteGate.Refusal(ini) is { } protectedFile) return ComponentOperationResult.Fail(protectedFile);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(journal, $"RenoDX peak brightness: {game.Name}", game, ManagedComponentKind.RenoDX, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await scope.TrackAsync(ini, cancellationToken).ConfigureAwait(false);
            var states = Apply(game.BinaryDirectory, game.InstallPath, nits, sections(ini), Read(record), force: true);
            await scope.DeclareWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);
            record.Metadata[MetadataKey] = Serialize(states);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(ini, ini);
    }
}
