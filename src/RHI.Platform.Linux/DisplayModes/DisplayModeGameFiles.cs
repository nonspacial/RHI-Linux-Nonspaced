using System.Globalization;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311: the display mode items kept in game files — RenoDX brightness and the ReShade preset / keys in ReShade.ini,
/// ReLimiter's cap and OSD key in relimiter.ini (and RE Framework's <c>_storage_</c> copy), Display Commander's FPS limit.
/// Only files of components RHI installed are written (a copy RHI did not install is left alone, as the INI ⚙ and the
/// "write to all" buttons do). Each write changes only the item's lines, all files of one item at once, journaled on the
/// owning component's record (a crash before the record is saved puts the files back).
/// </summary>
public sealed class DisplayModeGameFiles(LinuxDeploymentRecordStore records, DeploymentJournal? journal, Func<DetectedGame, string?> writeRefusal)
{
    private const string ReLimiterSection = "FrameLimiter";

    public DisplayModeUnavailable Unavailable(DisplayModeContext ctx, DisplayModeItem item)
    {
        var game = ctx.Game;
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory) || writeRefusal(game) is not null)
            return DisplayModeUnavailable.NoGameFolder;
        switch (item.Target)
        {
            case DisplayModeTarget.RenoDxIni:
                if (records.Find(game, ManagedComponentKind.RenoDX) is null) return DisplayModeUnavailable.RenoDxNotManaged;
                return ReShadeIni(game) is null ? DisplayModeUnavailable.NoReShadeIni : DisplayModeUnavailable.None;
            case DisplayModeTarget.ReShadeIni:
                if (records.Find(game, ManagedComponentKind.ReShade) is null) return DisplayModeUnavailable.ReShadeNotManaged;
                return ReShadeIni(game) is null ? DisplayModeUnavailable.NoReShadeIni : DisplayModeUnavailable.None;
            case DisplayModeTarget.ReLimiterIni:
                if (records.Find(game, ManagedComponentKind.ReLimiter) is null) return DisplayModeUnavailable.ReLimiterNotManaged;
                return UserIniService.ReLimiterInis(game).Count == 0 ? DisplayModeUnavailable.NoReLimiterIni : DisplayModeUnavailable.None;
            case DisplayModeTarget.DisplayCommanderIni:
                return records.Find(game, ManagedComponentKind.DisplayCommander) is null ? DisplayModeUnavailable.DisplayCommanderNotManaged : DisplayModeUnavailable.None;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Target, "Not a game-file item.");
        }
    }

    /// <summary>
    /// The game's value, or null when it has none: the key is not in the file, or (Display Commander) there is no file.
    /// w14 final review m8: a missing key reads as "none", not as a value, so Off can remove it again. Throws an
    /// <see cref="IOException"/> when a file cannot be read.
    /// </summary>
    public string? Read(DisplayModeContext ctx, DisplayModeItem item)
    {
        var game = ctx.Game;
        switch (item.Target)
        {
            case DisplayModeTarget.RenoDxIni:
                return ReShadeIni(game) is { } rdx ? RenoDxNitsIni.Read(IniLines.Read(rdx), RenoDxNitsIni.KeyFor(item.Key)) : null;
            case DisplayModeTarget.ReShadeIni:
                if (ReShadeIni(game) is not { } ini) return null;
                var lines = IniLines.Read(ini);
                return item.Key switch
                {
                    DisplayModeItemKeys.RsPreset => IniLines.Get(lines, "GENERAL", "PresetPath"),
                    DisplayModeItemKeys.RsOverlayKey => IniLines.Get(lines, "INPUT", "KeyOverlay"),
                    DisplayModeItemKeys.RsScreenshotKey => IniLines.Get(lines, "INPUT", "KeyScreenshot"),
                    DisplayModeItemKeys.RsGamepadNav => IniLines.Get(lines, "INPUT", "GamepadNavigation") is { } nav ? nav == "1" ? "1" : "0" : null,
                    _ => null
                };
            case DisplayModeTarget.ReLimiterIni:
                // No relimiter.ini = no value (not "off"); a file without target_fps = off (upstream 2.9.3 Beta 3).
                if (UserIniService.ReLimiterInis(game) is not { Count: > 0 } inis) return null;
                var relimiter = IniLines.Read(inis[0]);
                if (item.Key == DisplayModeItemKeys.UlOsdKey) return IniLines.Get(relimiter, ReLimiterSection, "osd_toggle_key");
                return int.TryParse(IniLines.Get(relimiter, ReLimiterSection, "target_fps"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps) && fps > 0
                    ? fps.ToString(CultureInfo.InvariantCulture) : "0";
            case DisplayModeTarget.DisplayCommanderIni:
                var dc = UserIniService.AddonIniPath(game, ManagedComponentKind.DisplayCommander);
                if (!File.Exists(dc)) return null; // a missing ini is no value, not 0
                if (IniLines.Get(IniLines.Read(dc), DisplayCommanderSettingsService.Section, DisplayCommanderSettingsService.FpsLimitKey) is not { } text) return null;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && limit > 0
                    ? Math.Round(limit, 3).ToString("0.###", CultureInfo.InvariantCulture) : "0";
            default:
                return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/>, or with null removes it (w14 final review m8: the game had none before modes — the
    /// key goes, and a Display Commander ini that holds nothing else and no record lists goes too). Null on success, else
    /// the problem. The files are read inside the journal scope (w14 final review m10), so what is written is planned from
    /// what is on disk once the operation holds the game.
    /// </summary>
    public async Task<string?> WriteAsync(DisplayModeContext ctx, DisplayModeItem item, string? value, CancellationToken cancellationToken)
    {
        var game = ctx.Game;
        if (writeRefusal(game) is { } refused) return refused;
        if (item.Target == DisplayModeTarget.DisplayCommanderIni && value is not null
            && DisplayCommanderSettingsService.Normalize(DisplayCommanderSettingsService.FpsLimitKey, value) is null)
            return DisplayCommanderSettingsService.InvalidValuePrefix + value;
        var owner = item.Target switch
        {
            DisplayModeTarget.RenoDxIni => ManagedComponentKind.RenoDX,
            DisplayModeTarget.ReShadeIni => ManagedComponentKind.ReShade,
            DisplayModeTarget.ReLimiterIni => ManagedComponentKind.ReLimiter,
            DisplayModeTarget.DisplayCommanderIni => ManagedComponentKind.DisplayCommander,
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Target, "Not a game-file item.")
        };
        // Find hands out a copy; the ledger changes only through the stamped Upsert below.
        if (records.Find(game, owner) is not { } record) return null;
        try
        {
            // Nothing to change: no journal entry (looked at again inside the scope, which decides).
            if (Plan(game, item, value, record).IsEmpty) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return ex.Message; }

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, $"Display mode {item.Key}: {game.Name}", game, owner, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return refusal;
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            var plan = Plan(game, item, value, record);
            if (plan.IsEmpty)
            {
                await scope.CommitAsync().ConfigureAwait(false);
                return null;
            }
            var targets = plan.Writes.Select(w => AtomicFile.ResolveUserFileTarget(w.Path, w.Policy)).ToList();
            await scope.TrackAllAsync(targets.Concat(plan.Deletes), cancellationToken).ConfigureAwait(false);
            IniLines.WriteAll(plan.Writes);
            foreach (var target in targets) await scope.DeclareWrittenContentAsync(target, cancellationToken).ConfigureAwait(false);
            foreach (var path in plan.Deletes) await scope.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return rollback.Committed ? null : ex.Message + DeploymentScope.RollbackNote(rollback); // #204: committed = it landed
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return null;
    }

    private sealed record WritePlan(List<IniFileWrite> Writes, List<string> Deletes)
    {
        public bool IsEmpty => Writes.Count == 0 && Deletes.Count == 0;
    }

    // The files one write changes, read now. Each keeps its line endings and BOM (w14 final review nit).
    private static WritePlan Plan(DetectedGame game, DisplayModeItem item, string? value, LinuxDeploymentRecord record)
    {
        var bin = game.BinaryDirectory!;
        var plan = new WritePlan([], []);
        switch (item.Target)
        {
            case DisplayModeTarget.RenoDxIni:
            {
                var ini = XboxWriteGate.Checked(ReShadeIni(game) ?? throw new FileNotFoundException("ReShade.ini was not found.", bin));
                var lines = IniLines.Read(ini, out var style);
                var key = RenoDxNitsIni.KeyFor(item.Key);
                if (value is null ? RenoDxNitsIni.Remove(lines, key) : RenoDxNitsIni.Write(lines, key, value))
                    plan.Writes.Add(ReShadeIniManager.PlannedWrite(ini, lines, bin, game.InstallPath) with { Style = style });
                break;
            }
            case DisplayModeTarget.ReShadeIni:
            {
                var main = XboxWriteGate.Checked(ReShadeIni(game) ?? throw new FileNotFoundException("ReShade.ini was not found.", bin));
                // The preset is the main file's; keys go into every ReShade*.ini of the game, as the ReShade ⚙ writes them.
                var files = item.Key == DisplayModeItemKeys.RsPreset ? [main] : new[] { main }.Concat(ReShadeGlobalSettingsApplier.NumberedInis(bin)).ToList();
                var (section, key) = item.Key switch
                {
                    DisplayModeItemKeys.RsPreset => ("GENERAL", "PresetPath"),
                    DisplayModeItemKeys.RsOverlayKey => ("INPUT", "KeyOverlay"),
                    DisplayModeItemKeys.RsScreenshotKey => ("INPUT", "KeyScreenshot"),
                    DisplayModeItemKeys.RsGamepadNav => ("INPUT", "GamepadNavigation"),
                    _ => ("", "")
                };
                if (key.Length == 0) break;
                if (item.Key == DisplayModeItemKeys.RsGamepadNav && value is not null) value = value == "1" ? "1" : "0";
                foreach (var file in files)
                {
                    var lines = IniLines.Read(XboxWriteGate.Checked(file), out var style);
                    var changed = value is null ? IniLines.Remove(lines, section, key) : IniLines.Set(lines, section, key, value);
                    if (changed) plan.Writes.Add(ReShadeIniManager.PlannedWrite(file, lines, bin, game.InstallPath) with { Style = style });
                }
                break;
            }
            case DisplayModeTarget.ReLimiterIni:
            {
                var roots = ReShadeGlobalSettingsApplier.GameRoots(game, bin);
                foreach (var file in UserIniService.ReLimiterInis(game))
                {
                    var lines = IniLines.Read(XboxWriteGate.Checked(file), out var style);
                    // target_fps 0 = off: the key goes, as ReLimiter's "Apply to all games" does.
                    var changed = item.Key == DisplayModeItemKeys.UlOsdKey
                        ? value is null ? IniLines.Remove(lines, ReLimiterSection, "osd_toggle_key") : IniLines.Set(lines, ReLimiterSection, "osd_toggle_key", value)
                        : value is null or "0" ? IniLines.Remove(lines, ReLimiterSection, "target_fps") : IniLines.Set(lines, ReLimiterSection, "target_fps", value);
                    if (changed) plan.Writes.Add(IniLines.PlannedWrite(file, lines, roots) with { Style = style });
                }
                break;
            }
            case DisplayModeTarget.DisplayCommanderIni:
            {
                var file = XboxWriteGate.Checked(UserIniService.AddonIniPath(game, ManagedComponentKind.DisplayCommander));
                var lines = IniLines.Read(file, out var style);
                var roots = ReShadeGlobalSettingsApplier.GameRoots(game, bin);
                if (value is null)
                {
                    if (!IniLines.Remove(lines, DisplayCommanderSettingsService.Section, DisplayCommanderSettingsService.FpsLimitKey)) break;
                    // A file a mode created (no record lists it) that holds nothing else goes with its key.
                    var listed = record.Files.Any(f => PathIdentity.AreSame(f.Path, file));
                    if (!listed && !IniLines.Entries(lines).Any()) plan.Deletes.Add(file);
                    else plan.Writes.Add(IniLines.PlannedWrite(file, lines, roots) with { Style = style });
                    break;
                }
                var fps = DisplayCommanderSettingsService.Normalize(DisplayCommanderSettingsService.FpsLimitKey, value)!;
                if (IniLines.Set(lines, DisplayCommanderSettingsService.Section, DisplayCommanderSettingsService.FpsLimitKey, fps))
                    plan.Writes.Add(IniLines.PlannedWrite(file, lines, roots) with { Style = style });
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Target, "Not a game-file item.");
        }
        return plan;
    }

    private static string? ReShadeIni(DetectedGame game)
        => game.BinaryDirectory is { } bin && RenoDxIniOverrideManager.ResolveIniPath(bin) is { } ini && File.Exists(ini) ? ini : null;
}
