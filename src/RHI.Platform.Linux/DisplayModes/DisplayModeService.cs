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

    /// <summary>What happened to one item.</summary>
    public sealed record ItemResult(DisplayModeItem Item, bool Written, string? Value, DisplayModeUnavailable Unavailable, string? Problem);

    /// <summary>Result of one switch (<see cref="ModeId"/> null = turned off).</summary>
    public sealed record SwitchResult(string GameName, string? ModeId, string ModeName, IReadOnlyList<ItemResult> Items)
    {
        public bool TurnedOff => ModeId is null;
        public IEnumerable<ItemResult> Written => Items.Where(i => i.Written);
        /// <summary>Items that had a value to write and could not take it.</summary>
        public IEnumerable<ItemResult> Failed => Items.Where(i => !i.Written && i.Problem is not null);
        /// <summary>True when one of <paramref name="targets"/> was written (the page reloads those controls).</summary>
        public bool WroteTo(DisplayModeTarget target) => Written.Any(i => i.Item.Target == target);
    }

    /// <summary>
    /// Switches the game to <paramref name="targetModeId"/>. Switching to the mode it is already in writes that mode's
    /// values again (nothing is remembered, since nothing is being left).
    /// </summary>
    public async Task<SwitchResult> SwitchAsync(DisplayModeContext ctx, string targetModeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = ctx.Key;
            var from = store.ActiveModeId(key); // null = Off
            var leaving = from is null || !from.Equals(targetModeId, StringComparison.OrdinalIgnoreCase);
            var results = new List<ItemResult>();
            var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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
                if (leaving && current is not null) RememberLeaving(key, from, item, current);

                // The mode's value, else the game's normal value (a mode only needs what differs).
                // A value the game already has is not written again (upstream re-writes it; the file ends the same).
                var value = store.Effective(key, targetModeId, item.Key) ?? NormalValue(ctx, item);
                if (value is null || value.Equals(current, StringComparison.Ordinal))
                {
                    entries[item.Key] = current; // nothing to write
                    results.Add(new ItemResult(item, false, null, DisplayModeUnavailable.None, null));
                    continue;
                }
                var error = await io.WriteAsync(ctx, item, value, cancellationToken).ConfigureAwait(false);
                entries[item.Key] = error is null ? io.Read(ctx, item) ?? value : current;
                results.Add(new ItemResult(item, error is null, value, DisplayModeUnavailable.None, error));
            }
            store.SetActive(key, targetModeId);
            store.SetEntries(key, entries);
            return new SwitchResult(ctx.GameName, targetModeId, store.NameOf(targetModeId) ?? targetModeId, results);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Turns the game's mode off: writes its normal values (the global setting, else what it had before it entered a
    /// mode). Already off = nothing to do.
    /// </summary>
    public async Task<SwitchResult> TurnOffAsync(DisplayModeContext ctx, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = ctx.Key;
            var from = store.ActiveModeId(key);
            var results = new List<ItemResult>();
            if (from is null) return new SwitchResult(ctx.GameName, null, "Off", results);
            foreach (var item in Items(key))
            {
                var reason = io.Unavailable(ctx, item);
                if (reason != DisplayModeUnavailable.None)
                {
                    results.Add(new ItemResult(item, false, null, reason, null));
                    continue;
                }
                var current = io.Read(ctx, item);
                if (current is not null) RememberLeaving(key, from, item, current);
                var value = NormalValue(ctx, item);
                if (value is null || value.Equals(current, StringComparison.Ordinal))
                {
                    results.Add(new ItemResult(item, false, null, DisplayModeUnavailable.None, null)); // no normal value, or it has it
                    continue;
                }
                var error = await io.WriteAsync(ctx, item, value, cancellationToken).ConfigureAwait(false);
                results.Add(new ItemResult(item, error is null, value, DisplayModeUnavailable.None, error));
            }
            store.SetOff(key);
            return new SwitchResult(ctx.GameName, null, "Off", results);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Before a new value is written: coming from Off, the current value is the game's value from before modes. Leaving a
    /// mode, a value is remembered only for an item that was not recorded on entry (it started being swapped while in the
    /// mode): that is the game's normal value. A value it still has from the mode it is leaving is not that mode's to keep.
    /// </summary>
    private void RememberLeaving(string key, string? from, DisplayModeItem item, string current)
    {
        if (from is null)
        {
            store.SetOriginal(key, item.Key, current);
            return;
        }
        if (!store.TryGetEntry(key, item.Key, out _) && store.OriginalValue(key, item.Key) is null)
            store.SetOriginal(key, item.Key, current);
    }

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
            if (value.Equals(io.Read(ctx, item), StringComparison.Ordinal)) return null;
            var error = await io.WriteAsync(ctx, item, value, cancellationToken).ConfigureAwait(false);
            if (error is null && store.IsManaged(ctx.Key)) store.SetEntry(ctx.Key, item.Key, io.Read(ctx, item) ?? value);
            return error;
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
