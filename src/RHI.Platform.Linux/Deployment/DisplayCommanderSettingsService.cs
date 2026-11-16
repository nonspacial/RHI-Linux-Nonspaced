using System.Globalization;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What the Display Commander ⚙ shows for a game (#282): the <c>[DisplayCommander]</c> values, Display Commander's
/// own defaults where a key is absent.</summary>
public sealed record DisplayCommanderSettings(
    double FpsLimit, int FpsLimiterMode, bool InjectReflex, int WindowMode,
    bool ReflexAutoConfigure, bool ReflexEnableSleep, bool ReflexUseMarkers, bool ReflexGenerateMarkers,
    int WinUpGraceSeconds, string IniPath, bool IniExists);

/// <summary>
/// #282 (upstream 2.8.9 <c>DcCogButton_Click</c>): Display Commander's main settings in its ⚙, written to the game's
/// <c>DisplayCommander.ini</c> beside the add-on (ReShade's AddonPath honoured) under <c>[DisplayCommander]</c>, with
/// Display Commander's own key names and defaults (pmnoxx/display-commander <c>settings/main_tab_settings.cpp</c>,
/// <c>advanced_tab_settings.cpp</c>). Each change is one key, journaled on Display Commander's record (a crash before the
/// record is saved puts the file back); only the listed keys and values are written, so nothing else in the file changes.
/// </summary>
public sealed class DisplayCommanderSettingsService(LinuxDeploymentRecordStore records, DeploymentJournal? journal, Func<DetectedGame, string?> writeRefusal)
{
    public const string Section = "DisplayCommander";
    public const string FpsLimitKey = "fps_limit";
    public const string FpsLimiterModeKey = "fps_limiter_mode";
    public const string InjectReflexKey = "inject_reflex";
    public const string WindowModeKey = "WindowMode";
    public const string ReflexAutoConfigureKey = "ReflexAutoConfigure";
    public const string ReflexEnableSleepKey = "ReflexEnableSleep";
    public const string ReflexUseMarkersKey = "ReflexUseMarkers";
    public const string ReflexGenerateMarkersKey = "ReflexGenerateMarkers";
    public const string WinUpGraceSecondsKey = "WinUpGraceSeconds";

    /// <summary>A custom FPS limit's range (upstream: 20–1000, decimals allowed; Display Commander stores a decimal).</summary>
    public const double MinCustomFps = 20, MaxCustomFps = 1000;

    /// <summary>FPS Limiter Mode values 0 and 1, Window Mode values 0–3 (upstream's lists, in its order).</summary>
    public const int FpsLimiterModeCount = 2, WindowModeCount = 4;

    /// <summary>Upstream's Win+Up grace times in seconds (61 = Always); another value in the file is shown as it is.</summary>
    public static readonly IReadOnlyList<int> WinUpGraceChoices = [0, 1, 2, 5, 10, 61];

    public const string NotManagedPrefix = "Display Commander settings need Display Commander installed by RHI: ";
    public const string InvalidValuePrefix = "Display Commander does not take this value; nothing was changed: ";

    private static readonly string[] OnOffKeys =
        [InjectReflexKey, ReflexAutoConfigureKey, ReflexEnableSleepKey, ReflexUseMarkersKey, ReflexGenerateMarkersKey];

    public DisplayCommanderSettings Read(DetectedGame game)
    {
        var path = UserIniService.AddonIniPath(game, ManagedComponentKind.DisplayCommander);
        var lines = File.Exists(path) ? IniLines.Read(path) : [];
        string? Get(string key) => IniLines.Get(lines, Section, key);
        int Int(string key, int fallback) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        bool Bool(string key) => Int(key, 0) != 0;
        var fps = double.TryParse(Get(FpsLimitKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 0;
        return new DisplayCommanderSettings(fps,
            Math.Clamp(Int(FpsLimiterModeKey, 0), 0, FpsLimiterModeCount - 1), Bool(InjectReflexKey),
            Math.Clamp(Int(WindowModeKey, 0), 0, WindowModeCount - 1),
            Bool(ReflexAutoConfigureKey), Bool(ReflexEnableSleepKey), Bool(ReflexUseMarkersKey), Bool(ReflexGenerateMarkersKey),
            Int(WinUpGraceSecondsKey, 1), path, File.Exists(path));
    }

    /// <summary>The value written for <paramref name="key"/> = <paramref name="value"/>, or null when Display Commander
    /// does not take it (unknown key, out of range).</summary>
    public static string? Normalize(string key, string value)
    {
        if (key == FpsLimitKey)
        {
            if (!double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)) return null;
            fps = Math.Round(fps, 3);
            return fps == 0 || fps is >= MinCustomFps and <= MaxCustomFps ? fps.ToString("0.###", CultureInfo.InvariantCulture) : null;
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return null;
        var valid = key switch
        {
            FpsLimiterModeKey => number is >= 0 and < FpsLimiterModeCount,
            WindowModeKey => number is >= 0 and < WindowModeCount,
            WinUpGraceSecondsKey => WinUpGraceChoices.Contains(number),
            _ when OnOffKeys.Contains(key) => number is 0 or 1,
            _ => false
        };
        return valid ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Writes one setting to the game's DisplayCommander.ini (created with the section when absent).</summary>
    public async Task<ComponentOperationResult> SetAsync(DetectedGame game, string key, string value, CancellationToken cancellationToken = default)
    {
        if (Normalize(key, value) is not { } normalized) return ComponentOperationResult.Fail(InvalidValuePrefix + $"{key}={value}");
        if (writeRefusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        // Find hands out a copy; the ledger changes only through the stamped Upsert below.
        var record = records.Find(game, ManagedComponentKind.DisplayCommander);
        if (record is null || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return ComponentOperationResult.Fail(NotManagedPrefix + game.Name);
        var target = UserIniService.AddonIniPath(game, ManagedComponentKind.DisplayCommander);
        var roots = ReShadeGlobalSettingsApplier.GameRoots(game, game.BinaryDirectory);
        string written;
        try
        {
            XboxWriteGate.ThrowIfRefused(target);
            written = IniLines.ResolveTarget(target, roots); // a link is followed only to a same-named file in the game
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ComponentOperationResult.Fail(ex.Message);
        }
        var lines = File.Exists(written) ? IniLines.Read(written) : [];
        if (!IniLines.Set(lines, Section, key, normalized)) return ComponentOperationResult.Ok(target, target); // already so

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(journal, $"DisplayCommander.ini {key}: {game.Name}", game, ManagedComponentKind.DisplayCommander, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await scope.TrackAsync(written, cancellationToken).ConfigureAwait(false);
            IniLines.Write(target, lines, roots);
            await scope.DeclareWrittenContentAsync(written, cancellationToken).ConfigureAwait(false);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("DisplayCommander.ini", false, target); // #204: the record landed
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(target, target); // the changed file; the UI words the outcome
    }
}
