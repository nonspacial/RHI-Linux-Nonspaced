using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// ReShade add-ons and the No Addons channel (#170, owner decision 2026-10-15): switching a game's ReShade to the No
/// Addons build turns its installed add-ons off — each add-on file RHI installed (component add-ons and the Addon
/// Manager's) is renamed in place to <c>&lt;name&gt;.rhi-off</c>, which ReShade never loads; nothing is deleted. Switching
/// back to a channel with add-on support turns exactly those back on (only RHI writes the suffix). Both directions run
/// inside the ReShade install's journaled operation and change the add-on records' file paths in the same ledger write
/// as ReShade's record, so a crash leaves either both or neither. Removing an add-on stays the user's own action (its
/// record names the renamed file, so removal deletes that). An add-on that is off is not updated (held, like an NR
/// method under NR Cost Scaler).
/// </summary>
public static class ReShadeAddonSwitch
{
    /// <summary>Appended to an add-on file RHI turned off; ReShade loads only <c>.addon</c>, <c>.addon32</c> and <c>.addon64</c>.</summary>
    public const string OffSuffix = ".rhi-off";

    /// <summary>Fixed text before the count of add-ons turned off (PT-BR presentation pair).</summary>
    public const string TurnedOffPrefix =
        " ReShade add-ons turned off because No Addons cannot load them (kept in the game folder; they come back on when ReShade goes back to Stable or Nightly): ";

    /// <summary>Fixed text before the count of add-ons turned back on (PT-BR presentation pair).</summary>
    public const string TurnedOnPrefix = " ReShade add-ons turned back on: ";

    /// <summary>Fixed text before the path when the other name of an add-on file is taken (PT-BR presentation pair).</summary>
    public const string NameTakenPrefix =
        "ReShade was not changed: an add-on cannot be turned off or back on because a file already has the name it would get. Move that file away first: ";

    /// <summary>Fixed text before the add-on's name when an install or update of an add-on that is off is refused (PT-BR pair).</summary>
    public const string HeldPrefix =
        "This add-on is turned off for ReShade's No Addons build; install ReShade with add-on support (Stable or Nightly) on this game first: ";

    /// <summary>
    /// Appended to a ReShade switch to or from No Addons while the Addon Manager's saved list cannot be read: its add-ons
    /// were left as they were (PT-BR presentation pair).
    /// </summary>
    public const string ManagerUnreadableNote =
        " Addon Manager add-ons were not checked because its saved list cannot be read, so they were left as they were; apply ReShade again once that list reads.";

    /// <summary>Component row note of an add-on that is off (PT-BR presentation pair).</summary>
    public const string OffNote =
        "Turned off for ReShade's No Addons build: the add-on stays in the game folder and comes back on when RHI installs ReShade with add-on support (Stable or Nightly) on this game.";

    /// <summary>One file rename; <see cref="Owner"/> is the add-on component (<see cref="ManagedComponentKind.ReShadeAddonManager"/>
    /// for the Addon Manager's record), null for a manager add-on known only from the hook's list.</summary>
    public sealed record Move(string From, string To, ManagedComponentKind? Owner);

    /// <summary>What switching to a build with (or without) add-on support changes; empty when nothing is affected.</summary>
    public sealed record Plan(bool TurningOff, IReadOnlyList<Move> Moves, IReadOnlyList<LinuxDeploymentRecord> Records)
    {
        public static readonly Plan None = new(false, [], []);
    }

    /// <summary>True for a ReShade add-on file name, on or off.</summary>
    public static bool IsAddonFile(string path)
    {
        var name = path.EndsWith(OffSuffix, StringComparison.Ordinal) ? path[..^OffSuffix.Length] : path;
        var extension = Path.GetExtension(name);
        return extension.Equals(".addon", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when RHI turned this add-on component's files off for No Addons.</summary>
    public static bool IsOff(LinuxDeploymentRecord? record)
        => record is not null && LinuxComponentDependencyPolicy.IsReShadeAddon(record.Component)
           && record.Files.Any(file => file.Path.EndsWith(OffSuffix, StringComparison.Ordinal) && IsAddonFile(file.Path));

    /// <summary>
    /// True when <paramref name="directory"/> holds an add-on file RHI turned off (only RHI writes the suffix): after
    /// ReShade was removed while No Addons had them off, these are Addon Manager add-ons (component add-ons keep ReShade
    /// from being removed).
    /// </summary>
    public static bool HasOffAddons(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        try
        {
            return Directory.EnumerateFiles(directory, "*" + OffSuffix)
                .Any(path => path.EndsWith(OffSuffix, StringComparison.Ordinal) && IsAddonFile(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("reshade-addons", $"Could not look for add-ons turned off in {directory}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The renames and record changes for ReShade becoming a build with (<paramref name="addonSupport"/>) or without add-on
    /// support, or a refusal naming a file already at a target name. Reads only; changes nothing.
    /// </summary>
    public static (Plan Plan, string? Refusal) PlanFor(
        IReadOnlyList<LinuxDeploymentRecord> gameRecords, IReadOnlyList<string> addonManagerFiles, bool addonSupport)
    {
        var turningOff = !addonSupport;
        var moves = new List<Move>();
        var changed = new List<LinuxDeploymentRecord>();
        // #168: the Addon Manager's record is renamed like a component add-on's (its add-ons are no component, so they
        // never keep ReShade from being removed).
        var addonRecords = gameRecords.Where(r => LinuxComponentDependencyPolicy.IsReShadeAddon(r.Component)
                                                  || r.Component == ManagedComponentKind.ReShadeAddonManager).ToList();
        foreach (var record in addonRecords)
        {
            LinuxDeploymentRecord? copy = null;
            foreach (var (file, index) in record.Files.Select((file, index) => (file, index)))
            {
                if (string.IsNullOrWhiteSpace(file.Path) || !IsAddonFile(file.Path)) continue;
                var isOff = file.Path.EndsWith(OffSuffix, StringComparison.Ordinal);
                string? newPath = null;
                if (turningOff && !isOff && File.Exists(file.Path)) newPath = file.Path + OffSuffix;
                // Back on: the record goes back to the add-on's own name even when the file was deleted meanwhile (then it
                // is simply missing, as after any manual delete); only an existing file is renamed.
                else if (!turningOff && isOff) newPath = file.Path[..^OffSuffix.Length];
                if (newPath is null) continue;
                if (File.Exists(file.Path)) moves.Add(new Move(file.Path, newPath, record.Component));
                copy ??= LinuxDeploymentRecordStore.Clone(record);
                copy.Files[index].Path = newPath;
            }
            if (copy is not null) changed.Add(copy);
        }
        var recorded = addonRecords.SelectMany(r => r.Files).Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.EndsWith(OffSuffix, StringComparison.Ordinal) ? path[..^OffSuffix.Length] : path)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in addonManagerFiles.Where(p => !string.IsNullOrWhiteSpace(p) && IsAddonFile(p) && !recorded.Contains(p)))
        {
            if (turningOff && File.Exists(path)) moves.Add(new Move(path, path + OffSuffix, null));
            else if (!turningOff && File.Exists(path + OffSuffix)) moves.Add(new Move(path + OffSuffix, path, null));
        }
        if (moves.FirstOrDefault(move => File.Exists(move.To) || Directory.Exists(move.To)) is { } blocked)
            return (Plan.None, NameTakenPrefix + blocked.To);
        return (new Plan(turningOff, moves, changed), null);
    }

    /// <summary>
    /// Renames every planned file (each tracked by <paramref name="operation"/> first, the renamed content declared), in
    /// order; returns the moves done so far through <paramref name="done"/> so a failure can be undone.
    /// </summary>
    public static async Task ApplyAsync(Plan plan, DeploymentOperation? operation, List<Move> done)
    {
        foreach (var move in plan.Moves)
        {
            if (operation is not null)
            {
                await operation.TrackFileAsync(move.From, CancellationToken.None, includeBackup: false).ConfigureAwait(false);
                await operation.TrackFileAsync(move.To, CancellationToken.None, includeBackup: false).ConfigureAwait(false);
                await operation.RecordWriteAsync(move.To, await FileHash.Sha256HexAsync(move.From, CancellationToken.None).ConfigureAwait(false),
                    CancellationToken.None).ConfigureAwait(false);
            }
            AtomicFile.Rename(move.From, move.To);
            done.Add(move);
        }
    }

    /// <summary>Puts renamed files back after a failure before the commit point (best effort, logged; never throws).</summary>
    public static void Undo(IReadOnlyList<Move> done)
    {
        foreach (var move in done.Reverse())
        {
            try
            {
                if (File.Exists(move.To) && !File.Exists(move.From)) AtomicFile.Rename(move.To, move.From);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("reshade-addons", $"Could not put the add-on back at {move.From}; it is still at {move.To}", ex);
            }
        }
    }

    /// <summary>How many add-ons a plan turns off or on (a component counts once, whatever its number of files; each Addon
    /// Manager add-on counts on its own).</summary>
    public static int AddonCount(Plan plan)
        => plan.Moves.Select(move => move.Owner is { } owner && owner != ManagedComponentKind.ReShadeAddonManager ? owner.ToString() : move.From)
            .Distinct(StringComparer.Ordinal).Count();

    /// <summary>The status-line sentence for what was done (empty when nothing changed).</summary>
    public static string Summary(Plan plan)
        => plan.Moves.Count == 0 ? "" : $"{(plan.TurningOff ? TurnedOffPrefix : TurnedOnPrefix)}{AddonCount(plan)}.";
}
