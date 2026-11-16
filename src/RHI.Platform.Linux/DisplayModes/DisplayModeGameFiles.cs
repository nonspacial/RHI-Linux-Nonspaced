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
                    DisplayModeItemKeys.RsGamepadNav => IniLines.Get(lines, "INPUT", "GamepadNavigation") == "1" ? "1" : "0",
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
                return double.TryParse(IniLines.Get(IniLines.Read(dc), DisplayCommanderSettingsService.Section, DisplayCommanderSettingsService.FpsLimitKey),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && limit > 0
                    ? Math.Round(limit, 3).ToString("0.###", CultureInfo.InvariantCulture) : "0";
            default:
                return null;
        }
    }

    public async Task<string?> WriteAsync(DisplayModeContext ctx, DisplayModeItem item, string value, CancellationToken cancellationToken)
    {
        var game = ctx.Game;
        if (writeRefusal(game) is { } refused) return refused;
        var bin = game.BinaryDirectory!;
        var writes = new List<IniFileWrite>();
        ManagedComponentKind owner;
        try
        {
            switch (item.Target)
            {
                case DisplayModeTarget.RenoDxIni:
                {
                    owner = ManagedComponentKind.RenoDX;
                    var ini = XboxWriteGate.Checked(ReShadeIni(game) ?? throw new FileNotFoundException("ReShade.ini was not found.", bin));
                    var lines = IniLines.Read(ini);
                    if (RenoDxNitsIni.Write(lines, RenoDxNitsIni.KeyFor(item.Key), value)) writes.Add(ReShadeIniManager.PlannedWrite(ini, lines, bin, game.InstallPath));
                    break;
                }
                case DisplayModeTarget.ReShadeIni:
                {
                    owner = ManagedComponentKind.ReShade;
                    var main = XboxWriteGate.Checked(ReShadeIni(game) ?? throw new FileNotFoundException("ReShade.ini was not found.", bin));
                    // The preset is the main file's; keys go into every ReShade*.ini of the game, as the ReShade ⚙ writes them.
                    var files = item.Key == DisplayModeItemKeys.RsPreset ? [main] : new[] { main }.Concat(ReShadeGlobalSettingsApplier.NumberedInis(bin)).ToList();
                    foreach (var file in files)
                    {
                        var lines = IniLines.Read(XboxWriteGate.Checked(file));
                        var changed = item.Key switch
                        {
                            DisplayModeItemKeys.RsPreset => IniLines.Set(lines, "GENERAL", "PresetPath", value),
                            DisplayModeItemKeys.RsOverlayKey => IniLines.Set(lines, "INPUT", "KeyOverlay", value),
                            DisplayModeItemKeys.RsScreenshotKey => IniLines.Set(lines, "INPUT", "KeyScreenshot", value),
                            DisplayModeItemKeys.RsGamepadNav => IniLines.Set(lines, "INPUT", "GamepadNavigation", value == "1" ? "1" : "0"),
                            _ => false
                        };
                        if (changed) writes.Add(ReShadeIniManager.PlannedWrite(file, lines, bin, game.InstallPath));
                    }
                    break;
                }
                case DisplayModeTarget.ReLimiterIni:
                {
                    owner = ManagedComponentKind.ReLimiter;
                    var roots = ReShadeGlobalSettingsApplier.GameRoots(game, bin);
                    foreach (var file in UserIniService.ReLimiterInis(game))
                    {
                        var lines = IniLines.Read(XboxWriteGate.Checked(file));
                        // target_fps 0 = off: the key goes, as ReLimiter's "Apply to all games" does.
                        var changed = item.Key == DisplayModeItemKeys.UlOsdKey
                            ? IniLines.Set(lines, ReLimiterSection, "osd_toggle_key", value)
                            : value == "0" ? IniLines.Remove(lines, ReLimiterSection, "target_fps") : IniLines.Set(lines, ReLimiterSection, "target_fps", value);
                        if (changed) writes.Add(IniLines.PlannedWrite(file, lines, roots));
                    }
                    break;
                }
                case DisplayModeTarget.DisplayCommanderIni:
                {
                    owner = ManagedComponentKind.DisplayCommander;
                    if (DisplayCommanderSettingsService.Normalize(DisplayCommanderSettingsService.FpsLimitKey, value) is not { } fps)
                        return DisplayCommanderSettingsService.InvalidValuePrefix + value;
                    var file = XboxWriteGate.Checked(UserIniService.AddonIniPath(game, ManagedComponentKind.DisplayCommander));
                    var lines = IniLines.Read(file);
                    if (IniLines.Set(lines, DisplayCommanderSettingsService.Section, DisplayCommanderSettingsService.FpsLimitKey, fps))
                        writes.Add(IniLines.PlannedWrite(file, lines, ReShadeGlobalSettingsApplier.GameRoots(game, bin)));
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(item), item.Target, "Not a game-file item.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ex.Message;
        }
        return writes.Count == 0 ? null : await WriteJournaledAsync(game, owner, item.Key, writes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> WriteJournaledAsync(DetectedGame game, ManagedComponentKind owner, string itemKey, IReadOnlyList<IniFileWrite> writes, CancellationToken cancellationToken)
    {
        // Find hands out a copy; the ledger changes only through the stamped Upsert below.
        if (records.Find(game, owner) is not { } record) return null;
        List<string> targets;
        try { targets = writes.Select(w => AtomicFile.ResolveUserFileTarget(w.Path, w.Policy)).ToList(); } // refused before anything is written
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return ex.Message; }

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, $"Display mode {itemKey}: {game.Name}", game, owner, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return refusal;
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await scope.TrackAllAsync(targets, cancellationToken).ConfigureAwait(false);
            IniLines.WriteAll(writes);
            foreach (var target in targets) await scope.DeclareWrittenContentAsync(target, cancellationToken).ConfigureAwait(false);
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

    private static string? ReShadeIni(DetectedGame game)
        => game.BinaryDirectory is { } bin && RenoDxIniOverrideManager.ResolveIniPath(bin) is { } ini && File.Exists(ini) ? ini : null;
}
