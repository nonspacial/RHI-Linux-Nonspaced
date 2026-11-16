namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311 (upstream 2.9.3 <c>Services/DisplayModeStore.cs</c>, 2.9.4 end state): the Display modes data, pure and
/// thread-safe. Modes (Monitor, TV, …) hold values per item that every game uses. Each game remembers its active mode and
/// can have a Custom mode of its own (made in its ⚙: the ticked settings and their values), shown only for that game. A
/// game swaps every item a mode has a value for, plus its Custom mode's. A game in no mode is Off (<c>Mode = null</c>):
/// it follows the global settings, and the "write to all games" writers do not keep its values. No file or UI work here:
/// <see cref="DisplayModeService"/> reads and writes the game's settings, <see cref="DisplayModeFileStore"/> saves.
/// </summary>
public sealed class DisplayModeStore
{
    public const int MaxModes = 6;
    public const int MaxNameLength = 24;

    /// <summary>Id of a game's own Custom mode (never a global mode id: those are monitor / tv or "m" + hex).</summary>
    public const string CustomId = "custom";
    public const string CustomName = "Custom";

    private readonly object _lock = new();
    private bool _enabled;
    private List<DisplayModeDefinition> _modes = [];
    private Dictionary<string, GameDisplayModeState> _games = new(StringComparer.OrdinalIgnoreCase);

    public DisplayModeStore() => Load(null);

    /// <summary>Monitor and TV, with no values yet (upstream's defaults).</summary>
    public static DisplayModeData CreateDefaults() => new()
    {
        Modes =
        [
            new DisplayModeDefinition { Id = "monitor", Name = "Monitor" },
            new DisplayModeDefinition { Id = "tv", Name = "TV" },
        ],
    };

    /// <summary>Replaces everything with saved data (null or no modes = the defaults; games are kept).</summary>
    public void Load(DisplayModeData? data)
    {
        data ??= CreateDefaults();
        lock (_lock)
        {
            _enabled = data.Enabled;
            _modes = [];
            foreach (var m in data.Modes ?? [])
            {
                if (m is null || string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name) || IsCustom(m.Id)) continue;
                if (_modes.Any(x => x.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase))) continue;
                if (_modes.Count >= MaxModes) break;
                var name = m.Name.Trim();
                if (name.Length > MaxNameLength) name = name[..MaxNameLength];
                _modes.Add(new DisplayModeDefinition { Id = m.Id, Name = name, Values = CopyValues(m.Values) });
            }
            if (_modes.Count == 0) _modes = CreateDefaults().Modes;
            _games = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, st) in data.Games ?? [])
            {
                if (string.IsNullOrWhiteSpace(key) || st is null) continue;
                var c = Copy(st);
                foreach (var mode in c.Values.Keys.Where(m => !IsCustom(m)).ToList()) c.Values.Remove(mode);
                if (!c.Custom) { c.Items = null; c.Values.Clear(); }
                if (IsCustom(c.Mode) && !c.Custom) ClearActive(c);
                if (c.Mode is not null && !IsCustom(c.Mode) && Find(c.Mode) is null) ClearActive(c); // a mode that is gone
                if (!IsEmpty(c)) _games[key] = c;
            }
        }
    }

    /// <summary>A deep copy of everything, for saving.</summary>
    public DisplayModeData Snapshot()
    {
        lock (_lock)
        {
            var d = new DisplayModeData
            {
                Enabled = _enabled,
                Modes = _modes.Select(m => new DisplayModeDefinition { Id = m.Id, Name = m.Name, Values = CopyValues(m.Values) }).ToList(),
            };
            foreach (var (k, st) in _games)
                if (!IsEmpty(st)) d.Games[k] = Copy(st);
            return d;
        }
    }

    /// <summary>Settings › Display modes › Show on game pages. Off hides the switch; games keep what they have now.</summary>
    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set { lock (_lock) _enabled = value; }
    }

    // ── Modes ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<DisplayModeDefinition> Modes
    {
        get { lock (_lock) return _modes.Select(m => new DisplayModeDefinition { Id = m.Id, Name = m.Name, Values = CopyValues(m.Values) }).ToList(); }
    }

    public string? NameOf(string? modeId)
    {
        if (IsCustom(modeId)) return CustomName;
        lock (_lock) return Find(modeId)?.Name;
    }

    public static bool IsCustom(string? modeId) => modeId is not null && modeId.Equals(CustomId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The modes this game can switch to: the global ones, then its Custom mode if it has one.</summary>
    public IReadOnlyList<DisplayModeDefinition> ModesFor(string gameKey)
    {
        var list = Modes.ToList();
        if (HasCustom(gameKey)) list.Add(new DisplayModeDefinition { Id = CustomId, Name = CustomName });
        return list;
    }

    /// <summary>Null when the name can be used for a mode (other than <paramref name="exceptId"/>), else why not.</summary>
    public DisplayModeNameProblem? ValidateName(string? name, string? exceptId = null)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) return DisplayModeNameProblem.Empty;
        if (n.Length > MaxNameLength) return DisplayModeNameProblem.TooLong;
        if (n.Equals(CustomName, StringComparison.OrdinalIgnoreCase)) return DisplayModeNameProblem.Reserved;
        lock (_lock)
            if (_modes.Any(m => m.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && !m.Id.Equals(exceptId ?? "", StringComparison.OrdinalIgnoreCase)))
                return DisplayModeNameProblem.Taken;
        return null;
    }

    /// <summary>Adds a mode; its id, or null when the name is invalid or there are already <see cref="MaxModes"/>.</summary>
    public string? AddMode(string name)
    {
        if (ValidateName(name) is not null) return null;
        lock (_lock)
        {
            if (_modes.Count >= MaxModes) return null;
            string id;
            do id = "m" + Guid.NewGuid().ToString("N")[..8];
            while (_modes.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
            _modes.Add(new DisplayModeDefinition { Id = id, Name = name.Trim() });
            return id;
        }
    }

    public bool RenameMode(string id, string name)
    {
        if (ValidateName(name, id) is not null) return false;
        lock (_lock)
        {
            var m = Find(id);
            if (m is null) return false;
            m.Name = name.Trim();
            return true;
        }
    }

    /// <summary>Deletes a mode (at least one stays). Games still in it are set to Off.</summary>
    public bool DeleteMode(string id)
    {
        lock (_lock)
        {
            if (_modes.Count <= 1) return false;
            var m = Find(id);
            if (m is null) return false;
            _modes.Remove(m);
            foreach (var st in _games.Values)
            {
                st.Values.Remove(m.Id);
                if (st.Mode is not null && st.Mode.Equals(m.Id, StringComparison.OrdinalIgnoreCase)) ClearActive(st);
            }
            foreach (var k in _games.Keys.ToList()) Prune(k);
            return true;
        }
    }

    public bool MoveMode(string id, int delta)
    {
        lock (_lock)
        {
            var m = Find(id);
            if (m is null) return false;
            int i = _modes.IndexOf(m), j = i + delta;
            if (j < 0 || j >= _modes.Count || j == i) return false;
            _modes.RemoveAt(i);
            _modes.Insert(j, m);
            return true;
        }
    }

    public string? ModeDefault(string modeId, string item)
    {
        lock (_lock) return Find(modeId)?.Values.TryGetValue(item, out var v) == true ? v : null;
    }

    /// <summary>Sets (or with null clears) a mode's value for an item.</summary>
    public void SetModeDefault(string modeId, string item, string? value)
    {
        lock (_lock)
        {
            var m = Find(modeId);
            if (m is null) return;
            if (value is null) m.Values.Remove(item);
            else m.Values[item] = value;
        }
    }

    /// <summary>Items any mode has a value for. Every game swaps these.</summary>
    public IReadOnlyList<string> ModeValueItems
    {
        get { lock (_lock) return ModeValueItemsLocked(); }
    }

    private List<string> ModeValueItemsLocked()
        => _modes.SelectMany(m => m.Values.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ── Games ─────────────────────────────────────────────────────────────────

    /// <summary>The game's active mode id, or null when it is Off.</summary>
    public string? ActiveModeId(string gameKey)
    {
        lock (_lock)
        {
            if (!_games.TryGetValue(gameKey, out var st) || st.Mode is null) return null;
            if (IsCustom(st.Mode)) return st.Custom ? CustomId : null;
            return Find(st.Mode)?.Id;
        }
    }

    /// <summary>True while the game is in a mode: its swapped values are RHI-managed (global writers keep them).</summary>
    public bool IsManaged(string gameKey) => ActiveModeId(gameKey) is not null;

    /// <summary>Items the game swaps: everything a mode has a value for, plus what its Custom mode changes.</summary>
    public IReadOnlyList<string> ItemsFor(string gameKey)
    {
        lock (_lock)
        {
            var list = ModeValueItemsLocked();
            foreach (var k in CustomItemsLocked(gameKey))
                if (!list.Contains(k, StringComparer.OrdinalIgnoreCase)) list.Add(k);
            return list;
        }
    }

    public bool Swaps(string gameKey, string item) => ItemsFor(gameKey).Contains(item, StringComparer.OrdinalIgnoreCase);

    public bool HasCustom(string gameKey)
    {
        lock (_lock) return _games.TryGetValue(gameKey, out var st) && st.Custom;
    }

    /// <summary>Makes the game's Custom mode (empty: it changes nothing until settings are ticked).</summary>
    public void CreateCustom(string gameKey)
    {
        lock (_lock) GetOrAdd(gameKey).Custom = true;
    }

    /// <summary>Removes the game's Custom mode. If the game was in it, it is set to Off (write its normal values first).</summary>
    public void DeleteCustom(string gameKey)
    {
        lock (_lock)
        {
            if (!_games.TryGetValue(gameKey, out var st)) return;
            st.Custom = false;
            st.Items = null;
            st.Values.Remove(CustomId);
            if (IsCustom(st.Mode)) ClearActive(st);
            Prune(gameKey);
        }
    }

    public IReadOnlyList<string> CustomItems(string gameKey)
    {
        lock (_lock) return CustomItemsLocked(gameKey);
    }

    private List<string> CustomItemsLocked(string gameKey)
        => _games.TryGetValue(gameKey, out var st) && st.Custom && st.Items is not null ? st.Items.ToList() : [];

    public bool InCustom(string gameKey, string item) => CustomItems(gameKey).Contains(item, StringComparer.OrdinalIgnoreCase);

    /// <summary>Ticks or unticks a setting in the game's Custom mode (its value is kept while unticked).</summary>
    public void SetInCustom(string gameKey, string item, bool on)
    {
        lock (_lock)
        {
            if (!_games.TryGetValue(gameKey, out var st) || !st.Custom) return;
            st.Items ??= [];
            st.Items.RemoveAll(k => k.Equals(item, StringComparison.OrdinalIgnoreCase));
            if (on) st.Items.Add(item);
        }
    }

    public string? CustomValue(string gameKey, string item)
    {
        lock (_lock)
            return _games.TryGetValue(gameKey, out var st) && st.Values.TryGetValue(CustomId, out var vals) && vals.TryGetValue(item, out var v) ? v : null;
    }

    /// <summary>Sets (or with null clears) the Custom mode's value for a setting.</summary>
    public void SetCustomValue(string gameKey, string item, string? value)
    {
        lock (_lock)
        {
            if (!_games.TryGetValue(gameKey, out var st) || !st.Custom) return;
            if (value is null)
            {
                if (st.Values.TryGetValue(CustomId, out var vals)) vals.Remove(item);
                return;
            }
            if (!st.Values.TryGetValue(CustomId, out var dict)) st.Values[CustomId] = dict = new(StringComparer.OrdinalIgnoreCase);
            dict[item] = value;
        }
    }

    /// <summary>
    /// What the game gets for an item in a mode: in its Custom mode the value it set (ticked settings only), in a global
    /// mode that mode's value. Null = none (the game's normal value).
    /// </summary>
    public string? Effective(string gameKey, string modeId, string item)
        => IsCustom(modeId)
            ? InCustom(gameKey, item) ? CustomValue(gameKey, item) : null
            : ModeDefault(modeId, item);

    /// <summary>Effective value for the game's active mode, or null when it is Off or does not swap this item.</summary>
    public string? ActiveValue(string gameKey, string item)
        => Swaps(gameKey, item) && ActiveModeId(gameKey) is { } mode ? Effective(gameKey, mode, item) : null;

    /// <summary>Marks the game as being in <paramref name="modeId"/> (a global mode, or its own Custom mode).</summary>
    public void SetActive(string gameKey, string modeId)
    {
        lock (_lock)
        {
            var custom = IsCustom(modeId);
            if (custom ? !(_games.TryGetValue(gameKey, out var cs) && cs.Custom) : Find(modeId) is null) return;
            GetOrAdd(gameKey).Mode = custom ? CustomId : Find(modeId)!.Id;
        }
    }

    /// <summary>Whether the game's value for <paramref name="item"/> was recorded when it entered its active mode.</summary>
    public bool TryGetEntry(string gameKey, string item, out string? value)
    {
        lock (_lock)
        {
            value = null;
            return _games.TryGetValue(gameKey, out var st) && st.Entry is not null && st.Entry.TryGetValue(item, out value);
        }
    }

    public void SetEntries(string gameKey, IDictionary<string, string?> entries)
    {
        lock (_lock) GetOrAdd(gameKey).Entry = new Dictionary<string, string?>(entries, StringComparer.OrdinalIgnoreCase);
    }

    public void SetEntry(string gameKey, string item, string? value)
    {
        lock (_lock)
        {
            var st = GetOrAdd(gameKey);
            st.Entry ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            st.Entry[item] = value;
        }
    }

    /// <summary>The game's value from before it entered a mode (null = none recorded).</summary>
    public string? OriginalValue(string gameKey, string item)
    {
        lock (_lock)
            return _games.TryGetValue(gameKey, out var st) && st.Original is not null && st.Original.TryGetValue(item, out var v) ? v : null;
    }

    public void SetOriginal(string gameKey, string item, string? value)
    {
        lock (_lock)
        {
            var st = GetOrAdd(gameKey);
            if (value is null)
            {
                st.Original?.Remove(item);
                if (st.Original is { Count: 0 }) st.Original = null;
                Prune(gameKey);
                return;
            }
            st.Original ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            st.Original[item] = value;
        }
    }

    /// <summary>Turns the game's mode off: no active mode, and the entry / before-modes records are dropped.</summary>
    public void SetOff(string gameKey)
    {
        lock (_lock)
        {
            if (!_games.TryGetValue(gameKey, out var st)) return;
            ClearActive(st);
            Prune(gameKey);
        }
    }

    /// <summary>Keys of the games in <paramref name="modeId"/>.</summary>
    public IReadOnlyList<string> GamesIn(string modeId)
    {
        lock (_lock)
            return _games.Where(kv => kv.Value.Mode is not null && kv.Value.Mode.Equals(modeId, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
    }

    /// <summary>Forgets everything about one game.</summary>
    public bool RemoveGame(string gameKey)
    {
        lock (_lock) return _games.Remove(gameKey);
    }

    /// <summary>The game's state moved to a new library key (a moved game folder): its state follows. True when moved.</summary>
    public bool MoveGame(string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey) || oldKey.Equals(newKey, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_lock)
        {
            if (!_games.TryGetValue(oldKey, out var st) || _games.ContainsKey(newKey)) return false;
            _games.Remove(oldKey);
            _games[newKey] = st;
            return true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DisplayModeDefinition? Find(string? id)
        => id is null ? null : _modes.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private GameDisplayModeState GetOrAdd(string key)
    {
        if (!_games.TryGetValue(key, out var st)) _games[key] = st = new GameDisplayModeState();
        return st;
    }

    private static void ClearActive(GameDisplayModeState st)
    {
        st.Mode = null;
        st.Entry = null;
        st.Original = null;
    }

    private void Prune(string key)
    {
        if (_games.TryGetValue(key, out var st) && IsEmpty(st)) _games.Remove(key);
    }

    private static bool IsEmpty(GameDisplayModeState st)
        => st.Mode is null && !st.Custom && st.Items is null && st.Values.Count == 0 && st.Entry is null && st.Original is null;

    private static Dictionary<string, string> CopyValues(Dictionary<string, string>? v)
        => v is null ? new(StringComparer.OrdinalIgnoreCase) : new(v.Where(kv => kv.Key is not null && kv.Value is not null), StringComparer.OrdinalIgnoreCase);

    private static GameDisplayModeState Copy(GameDisplayModeState st)
    {
        var c = new GameDisplayModeState
        {
            Mode = st.Mode,
            Custom = st.Custom,
            Items = st.Items?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList(),
            Entry = st.Entry is null ? null : new Dictionary<string, string?>(st.Entry, StringComparer.OrdinalIgnoreCase),
            Original = st.Original is null ? null : CopyValues(st.Original),
        };
        foreach (var (mode, vals) in st.Values ?? [])
            if (mode is not null && vals is not null) c.Values[mode] = CopyValues(vals);
        return c;
    }
}

/// <summary>Why a mode name cannot be used (the desktop words it).</summary>
public enum DisplayModeNameProblem { Empty, TooLong, Reserved, Taken }
