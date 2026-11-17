namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311 (upstream 2.9.3 <c>Services/DisplayModeService.cs</c>, 2.9.4 end state): switches a game between display modes,
/// and turns its mode off. For every item the game swaps: coming from Off, remember the game's current value as its
/// value from before modes; then write its value for the new mode (the mode's value, else its normal value: the global
/// setting, else its value from before modes). Turning off writes the normal values. One switch at a time: a game's
/// files must not be written by two switches at once.
/// </summary>
public sealed class DisplayModeService(DisplayModeStore store, IDisplayModeIo io)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DisplayModeStore Store => store;

    /// <summary>
    /// The global setting for an item (game, item key) → value, or null when there is none (most items). Set by the
    /// desktop from Settings (peak brightness, ReShade keys, ReLimiter cap / OSD key, the global FPS limit).
    /// </summary>
    public Func<DisplayModeContext, string, string?>? GlobalValue { get; set; }

    /// <summary>
    /// What happened to one item. <paramref name="NotRestored"/> (Off): it has a value from before modes that could not
    /// be written back (its file could not be written, or its folder cannot be written now).
    /// </summary>
    public sealed record ItemResult(DisplayModeItem Item, bool Written, string? Value, DisplayModeUnavailable Unavailable, string? Problem, bool NotRestored = false);

    /// <summary>
    /// Result of one switch (<see cref="ModeId"/> null = in no mode). w14 final review M3/m7: a switch stopped by a file
    /// it could not read has <see cref="Error"/> (what it had written was put back, nothing was recorded, the game is in
    /// the mode it was in); an Off that could not write every value back stays in its mode (<see cref="NotRestored"/>).
    /// </summary>
    public sealed record SwitchResult(string GameName, string? ModeId, string ModeName, IReadOnlyList<ItemResult> Items)
    {
        /// <summary>The file read that stopped the switch (everything it wrote was put back), or null.</summary>
        public string? Error { get; init; }
        /// <summary>Not everything that stopped the switch could be put back (the files it names keep the new value).</summary>
        public string? RollbackProblem { get; init; }
        /// <summary>The switch asked for Off.</summary>
        public bool OffRequested { get; init; }
        public bool TurnedOff => OffRequested && ModeId is null && Error is null && !NotRestored.Any();
        /// <summary>Off kept the game in its mode: these values from before modes were not written back (try Off again).</summary>
        public IEnumerable<ItemResult> NotRestored => Items.Where(i => i.NotRestored);
        public IEnumerable<ItemResult> Written => Items.Where(i => i.Written);
        /// <summary>Items that had a value to write and could not take it.</summary>
        public IEnumerable<ItemResult> Failed => Items.Where(i => !i.Written && i.Problem is not null);
        /// <summary>True when one of <paramref name="targets"/> was written (the page reloads those controls).</summary>
        public bool WroteTo(DisplayModeTarget target) => Error is null && Written.Any(i => i.Item.Target == target);
    }

    // What a game goes back to for an item: Has = false: nothing (no global setting, nothing recorded); Value null = it had
    // none (remove it).
    private readonly record struct Normal(bool Has, string? Value)
    {
        public static readonly Normal None = new(false, null);
    }

    /// <summary>
    /// Switches the game to <paramref name="targetModeId"/>. Switching to the mode it is already in writes that mode's
    /// values again (nothing is remembered, since nothing is being left). A file that cannot be read stops the switch:
    /// what it wrote is put back and nothing is recorded (w14 final review m7).
    /// </summary>
    public async Task<SwitchResult> SwitchAsync(DisplayModeContext ctx, string targetModeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = ctx.Key;
            var from = store.ActiveModeId(key); // null = Off
            var pending = store.HasRestorePending(key);
            var leaving = from is null || !from.Equals(targetModeId, StringComparison.OrdinalIgnoreCase);
            var results = new List<ItemResult>();
            var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var originals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var written = new List<(DisplayModeItem Item, string? Before)>();
            try
            {
                foreach (var item in Items(key))
                {
                    var reason = io.Unavailable(ctx, item);
                    if (reason != DisplayModeUnavailable.None)
                    {
                        entries[item.Key] = null; // not readable in this mode: nothing to remember when leaving it
                        results.Add(new ItemResult(item, false, null, reason, null));
                        continue;
                    }
                    var current = io.Read(ctx, item);
                    if (leaving) RememberLeaving(originals, key, from is null && !pending, item, current);

                    // The mode's value, else the game's normal value (a mode only needs what differs).
                    // A value the game already has is not written again (upstream re-writes it; the file ends the same).
                    var value = store.Effective(key, targetModeId, item.Key) is { } modeValue ? new Normal(true, modeValue) : NormalFor(ctx, item, originals);
                    if (!value.Has || string.Equals(value.Value, current, StringComparison.Ordinal))
                    {
                        entries[item.Key] = current; // nothing to write
                        results.Add(new ItemResult(item, false, null, DisplayModeUnavailable.None, null));
                        continue;
                    }
                    var error = await io.WriteAsync(ctx, item, value.Value, cancellationToken).ConfigureAwait(false);
                    if (error is null) written.Add((item, current));
                    entries[item.Key] = error is null ? io.Read(ctx, item) ?? value.Value : current;
                    results.Add(new ItemResult(item, error is null, value.Value, DisplayModeUnavailable.None, error));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return await RolledBackAsync(ctx, from, results, written, ex).ConfigureAwait(false);
            }
            foreach (var (item, value) in originals) store.RecordOriginal(key, item, value);
            store.SetActive(key, targetModeId);
            store.SetEntries(key, entries);
            return new SwitchResult(ctx.GameName, targetModeId, store.NameOf(targetModeId) ?? targetModeId, results);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Turns the game's mode off: writes its normal values (the global setting, else what it had before it entered a
    /// mode; a key or file it did not have goes again). Already off = nothing to do. w14 final review M3: the game is set
    /// to Off only once every value from before modes is back; else it stays in its mode with those values kept, and
    /// Off can be tried again. A file that cannot be read stops it, as a switch (m7).
    /// </summary>
    public async Task<SwitchResult> TurnOffAsync(DisplayModeContext ctx, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = ctx.Key;
            var from = store.ActiveModeId(key);
            var pending = store.HasRestorePending(key);
            var results = new List<ItemResult>();
            if (from is null && !pending) return new SwitchResult(ctx.GameName, null, "Off", results) { OffRequested = true };
            var originals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var written = new List<(DisplayModeItem Item, string? Before)>();
            // A pending restore is a mode left without a switch: the items listed are the ones it recorded.
            var items = pending ? Items(key).Concat(RecordedItems(key)).Distinct().ToList() : Items(key).ToList();
            try
            {
                foreach (var item in items)
                {
                    var reason = io.Unavailable(ctx, item);
                    if (reason != DisplayModeUnavailable.None)
                    {
                        // A folder RHI cannot write to now still has the values to put back; a component that is gone took
                        // its file with it.
                        var stuck = reason == DisplayModeUnavailable.NoGameFolder && store.TryGetOriginal(key, item.Key, out _);
                        results.Add(new ItemResult(item, false, null, reason, null, NotRestored: stuck));
                        continue;
                    }
                    var current = io.Read(ctx, item);
                    RememberLeaving(originals, key, fromOff: false, item, current);
                    var value = NormalFor(ctx, item, originals);
                    if (!value.Has || string.Equals(value.Value, current, StringComparison.Ordinal))
                    {
                        results.Add(new ItemResult(item, false, null, DisplayModeUnavailable.None, null)); // no normal value, or it has it
                        continue;
                    }
                    var error = await io.WriteAsync(ctx, item, value.Value, cancellationToken).ConfigureAwait(false);
                    if (error is null) written.Add((item, current));
                    results.Add(new ItemResult(item, error is null, value.Value, DisplayModeUnavailable.None, error, NotRestored: error is not null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var failed = await RolledBackAsync(ctx, from, results, written, ex).ConfigureAwait(false);
                return failed with { OffRequested = true };
            }
            foreach (var (item, value) in originals) store.RecordOriginal(key, item, value);
            var off = results.All(r => !r.NotRestored);
            if (off) store.SetOff(key);
            return new SwitchResult(ctx.GameName, off ? null : from, off ? "Off" : store.NameOf(from) ?? from ?? "Off", results) { OffRequested = true };
        }
        finally { _gate.Release(); }
    }

    // m7: a read failed part-way. What this pass wrote goes back (newest first, each through its own journaled write),
    // nothing is recorded, and the game stays in the mode it was in.
    private async Task<SwitchResult> RolledBackAsync(DisplayModeContext ctx, string? from, List<ItemResult> results,
        List<(DisplayModeItem Item, string? Before)> written, Exception ex)
    {
        var notPutBack = new List<string>();
        for (var i = written.Count - 1; i >= 0; i--)
        {
            var (item, before) = written[i];
            string? problem;
            try { problem = await io.WriteAsync(ctx, item, before, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception putBack) when (putBack is IOException or UnauthorizedAccessException) { problem = putBack.Message; }
            if (problem is not null) notPutBack.Add(item.Key + ": " + problem);
        }
        RHI.Core.Services.AppLog.Warn("display-modes", $"Switching {ctx.GameName} stopped: a file could not be read", ex);
        var items = results.Select(r => r with { Written = false }).ToList();
        return new SwitchResult(ctx.GameName, from, store.NameOf(from) ?? from ?? "Off", items)
        {
            Error = ex.Message,
            RollbackProblem = notPutBack.Count == 0 ? null : string.Join("; ", notPutBack)
        };
    }

    /// <summary>
    /// Before a new value is written: coming from Off, the current value is the game's value from before modes (none when
    /// it has none: m8). Leaving a mode, a value is remembered only for an item that was not recorded on entry (it started
    /// being swapped while in the mode): that is the game's normal value. A value it still has from the mode it is leaving
    /// is not that mode's to keep. Recorded in <paramref name="originals"/> and saved once the switch is through.
    /// </summary>
    private void RememberLeaving(Dictionary<string, string?> originals, string key, bool fromOff, DisplayModeItem item, string? current)
    {
        if (fromOff)
        {
            originals[item.Key] = current;
            return;
        }
        if (!store.TryGetEntry(key, item.Key, out _) && !store.TryGetOriginal(key, item.Key, out _) && current is not null)
            originals[item.Key] = current;
    }

    private Normal NormalFor(DisplayModeContext ctx, DisplayModeItem item, IReadOnlyDictionary<string, string?> originals)
    {
        if (GlobalValue?.Invoke(ctx, item.Key) is { } global) return new Normal(true, global);
        if (originals.TryGetValue(item.Key, out var pending)) return new Normal(true, pending);
        return store.TryGetOriginal(ctx.Key, item.Key, out var original) ? new Normal(true, original) : Normal.None;
    }

    private IEnumerable<DisplayModeItem> RecordedItems(string key)
        => DisplayModeItems.All.Where(item => store.TryGetOriginal(key, item.Key, out _));

    /// <summary>The game's normal value for an item: the global setting, else its value from before modes. Null = none.</summary>
    public string? NormalValue(DisplayModeContext ctx, DisplayModeItem item)
        => GlobalValue?.Invoke(ctx, item.Key) ?? store.OriginalValue(ctx.Key, item.Key);

    /// <summary>What the game gets for an item in a mode: the mode's value, else its normal value.</summary>
    public string? ValueFor(DisplayModeContext ctx, string modeId, DisplayModeItem item)
        => store.Effective(ctx.Key, modeId, item.Key) ?? NormalValue(ctx, item);

    /// <summary>Writes one value now (an edit to the game's Custom value while it is in Custom). Null = done, else the problem.</summary>
    public async Task<string?> ApplyNowAsync(DisplayModeContext ctx, DisplayModeItem item, string value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (io.Unavailable(ctx, item) != DisplayModeUnavailable.None) return null; // the dialog already says why
            try
            {
                if (value.Equals(io.Read(ctx, item), StringComparison.Ordinal)) return null;
                var error = await io.WriteAsync(ctx, item, value, cancellationToken).ConfigureAwait(false);
                if (error is null && store.IsManaged(ctx.Key)) store.SetEntry(ctx.Key, item.Key, io.Read(ctx, item) ?? value);
                return error;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; } // m7: a file that cannot be read
        }
        finally { _gate.Release(); }
    }

    /// <summary>The game's current values (for the per-game dialog). Null = no value / not available.</summary>
    public Dictionary<string, string?> ReadCurrent(DisplayModeContext ctx, IEnumerable<DisplayModeItem> items)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            result[item.Key] = io.Unavailable(ctx, item) == DisplayModeUnavailable.None ? io.Read(ctx, item) : null;
        return result;
    }

    public DisplayModeUnavailable Unavailable(DisplayModeContext ctx, DisplayModeItem item) => io.Unavailable(ctx, item);

    /// <summary>
    /// For writers that push one value to many games (ReShade / RenoDX / ReLimiter / Display Commander installs and
    /// updates, the Settings "write to all games" buttons; upstream <c>Keep</c>): when the game is in a mode and swaps
    /// any of <paramref name="itemKeys"/>, reads those values now and returns an action that writes them back afterwards
    /// (or the mode's value when the file had none then, e.g. a fresh ini). Null = nothing to keep.
    /// </summary>
    public Func<CancellationToken, Task<int>>? Keep(DisplayModeContext ctx, IEnumerable<string> itemKeys)
    {
        var mode = store.ActiveModeId(ctx.Key);
        if (mode is null) return null;
        var items = itemKeys.Where(k => store.Swaps(ctx.Key, k)).Select(DisplayModeItems.Get).OfType<DisplayModeItem>()
            .Where(i => i.Target != DisplayModeTarget.LaunchSettings).Distinct().ToList();
        if (items.Count == 0) return null;
        var captured = items.Select(i => (Item: i, Value: io.Unavailable(ctx, i) == DisplayModeUnavailable.None ? io.Read(ctx, i) : null)).ToList();
        return async cancellationToken =>
        {
            var written = 0;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var (item, value) in captured)
                {
                    if (io.Unavailable(ctx, item) != DisplayModeUnavailable.None) continue;
                    var v = value ?? ValueFor(ctx, mode, item);
                    if (v is null || v.Equals(io.Read(ctx, item), StringComparison.Ordinal)) continue;
                    if (await io.WriteAsync(ctx, item, v, cancellationToken).ConfigureAwait(false) is not null) continue;
                    written++;
                    // A fresh ini (e.g. ReLimiter just installed) now holds the mode's value: that is what it entered with.
                    if (value is null) store.SetEntry(ctx.Key, item.Key, v);
                }
            }
            finally { _gate.Release(); }
            return written;
        };
    }

    private IEnumerable<DisplayModeItem> Items(string key)
        => store.ItemsFor(key).Select(DisplayModeItems.Get).OfType<DisplayModeItem>();
}
