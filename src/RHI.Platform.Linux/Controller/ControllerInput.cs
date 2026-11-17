namespace RHI.Platform.Linux.Controller;

/// <summary>What a controller press does in RHI (upstream 2.9.0–2.9.2's button map).</summary>
public enum PadAction
{
    Up, Down, Left, Right,
    Select,      // A (Cross)
    Back,        // B (Circle)
    Launch,      // Y (Triangle)
    GameMenu,    // X (Square)
    PrevGame,    // LB: previous game, or the previous section in the side panel
    NextGame,    // RB: next game, or the next section in the side panel
    Settings,    // Menu / Start / Options
    Filters,     // View / Back / Share
    PageBack,    // LT: 10 games back (repeats while held)
    PageForward, // RT: 10 games on (repeats while held)
    CycleFilter, // left stick click: All / Installed / Favourites / Hidden
    Home,        // right stick click: the next collection (then all games again)
}

/// <summary>
/// One reading of a controller in XInput's layout (upstream's): button bits, and the sticks with up as positive Y. The
/// SDL reader converts SDL's state (down-positive Y, 0–32767 triggers) to it.
/// </summary>
public readonly record struct PadState(ushort Buttons, short LeftX, short LeftY, short RightX, short RightY)
{
    public static readonly PadState None = new(0, 0, 0, 0, 0);
}

/// <summary>
/// w14/layouts-b (#308, upstream 2.9.0 <c>ControllerPoller</c>, unchanged rules): controller state into actions. Buttons
/// fire once per press; directions (D-pad or left stick) repeat while held after 400 ms, every 80 ms; held triggers
/// repeat after 350 ms, every 110 ms. Anything held when RHI became active (pressed in a game just before) does nothing
/// until released. The right stick scrolls. Pure logic, unit tested.
/// </summary>
public sealed class ControllerPoller
{
    public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    public const ushort Start = 0x0010, BackBtn = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200, LeftTrigger = 0x0400, RightTrigger = 0x0800;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    public const byte TriggerThreshold = 100; // of 255: a resting finger does not press it

    public const short StickThreshold = 16000; // about half way: a light touch does not move
    public const short ScrollDeadZone = 8000;
    public const long RepeatDelayMs = 400, RepeatIntervalMs = 80;
    public const long TriggerRepeatDelayMs = 350, TriggerRepeatIntervalMs = 110;
    public const double ScrollPixelsPerTick = 28;
    private const ushort RepeatingButtons = LeftTrigger | RightTrigger;

    private static readonly (ushort Bit, PadAction Action)[] Presses =
    [
        (A, PadAction.Select), (B, PadAction.Back), (Y, PadAction.Launch), (X, PadAction.GameMenu),
        (LeftShoulder, PadAction.PrevGame), (RightShoulder, PadAction.NextGame),
        (Start, PadAction.Settings), (BackBtn, PadAction.Filters),
        (LeftTrigger, PadAction.PageBack), (RightTrigger, PadAction.PageForward),
        (LeftThumb, PadAction.CycleFilter), (RightThumb, PadAction.Home),
    ];

    private readonly Dictionary<ushort, long> _buttonRepeatAt = [];
    private ushort _prev;
    private ushort _blocked;
    private PadAction? _dir;
    private bool _dirBlocked;
    private long _nextRepeat;

    /// <summary>The buttons with the triggers added as two more (pressed past about 40%).</summary>
    public static ushort WithTriggers(ushort buttons, byte leftTrigger, byte rightTrigger)
    {
        buttons &= unchecked((ushort)~(LeftTrigger | RightTrigger));
        if (leftTrigger >= TriggerThreshold) buttons |= LeftTrigger;
        if (rightTrigger >= TriggerThreshold) buttons |= RightTrigger;
        return buttons;
    }

    /// <summary>The controller's current state is the starting point: what is held now does nothing until released.</summary>
    public void Reset(PadState state)
    {
        _prev = state.Buttons;
        _blocked = state.Buttons;
        _buttonRepeatAt.Clear();
        _dir = Direction(state.Buttons, state.LeftX, state.LeftY);
        _dirBlocked = _dir is not null;
        _nextRepeat = long.MaxValue;
    }

    /// <summary>The actions for this reading; <paramref name="scroll"/> is the right stick's scroll in pixels (down positive).</summary>
    public List<PadAction> Update(PadState state, long nowMs, out double scroll)
    {
        var buttons = state.Buttons;
        var actions = new List<PadAction>();
        _blocked &= buttons; // released buttons work again
        foreach (var (bit, action) in Presses)
        {
            bool down = (buttons & bit) != 0, wasDown = (_prev & bit) != 0;
            if (!down || (_blocked & bit) != 0) continue;
            if (!wasDown)
            {
                actions.Add(action);
                if ((bit & RepeatingButtons) != 0) _buttonRepeatAt[bit] = nowMs + TriggerRepeatDelayMs;
            }
            else if ((bit & RepeatingButtons) != 0 && _buttonRepeatAt.TryGetValue(bit, out var at) && nowMs >= at)
            {
                actions.Add(action);
                _buttonRepeatAt[bit] = nowMs + TriggerRepeatIntervalMs;
            }
        }
        _prev = buttons;

        var dir = Direction(buttons, state.LeftX, state.LeftY);
        if (dir is null)
        {
            _dir = null;
            _dirBlocked = false;
        }
        else if (dir != _dir)
        {
            _dir = dir; // a new direction moves straight away
            _dirBlocked = false;
            actions.Add(dir.Value);
            _nextRepeat = nowMs + RepeatDelayMs;
        }
        else if (!_dirBlocked && nowMs >= _nextRepeat)
        {
            actions.Add(dir.Value);
            _nextRepeat = nowMs + RepeatIntervalMs;
        }
        scroll = Math.Abs((int)state.RightY) > ScrollDeadZone ? -state.RightY / 32767.0 * ScrollPixelsPerTick : 0;
        return actions;
    }

    /// <summary>The D-pad wins over the stick; on the stick the larger axis wins (positive Y is up).</summary>
    public static PadAction? Direction(ushort buttons, short lx, short ly)
    {
        if ((buttons & DPadUp) != 0) return PadAction.Up;
        if ((buttons & DPadDown) != 0) return PadAction.Down;
        if ((buttons & DPadLeft) != 0) return PadAction.Left;
        if ((buttons & DPadRight) != 0) return PadAction.Right;
        int ax = Math.Abs((int)lx), ay = Math.Abs((int)ly);
        if (Math.Max(ax, ay) < StickThreshold) return null;
        if (ay >= ax) return ly > 0 ? PadAction.Up : PadAction.Down;
        return lx > 0 ? PadAction.Right : PadAction.Left;
    }

    /// <summary>
    /// SDL's reading in XInput's layout: SDL's stick Y grows downwards (flipped, -32768 kept in range) and its triggers run
    /// 0–32767 (scaled to 0–255 before the press threshold).
    /// </summary>
    public static PadState FromSdl(ushort buttons, short lx, short ly, short rx, short ry, short leftTrigger, short rightTrigger)
        => new(WithTriggers(buttons, ToByte(leftTrigger), ToByte(rightTrigger)), lx, Flip(ly), rx, Flip(ry));

    private static short Flip(short v) => v == short.MinValue ? short.MaxValue : (short)-v;

    private static byte ToByte(short trigger) => (byte)Math.Clamp(trigger / 128, 0, 255);

    /// <summary>The collection after <paramref name="active"/> (right stick click): the first, the next, then none (all games).</summary>
    public static string? NextCollection(IReadOnlyList<string> collections, string? active)
    {
        if (collections.Count == 0) return null;
        if (active is null) return collections[0];
        var i = collections.ToList().FindIndex(c => string.Equals(c, active, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? collections[0] : i + 1 < collections.Count ? collections[i + 1] : null;
    }

    /// <summary>An index moved by <paramref name="step"/> and clamped to the list (LB / RB, LT / RT); -1 for an empty list.</summary>
    public static int Step(int index, int step, int count) => count == 0 ? -1 : Math.Clamp(index < 0 ? 0 : index + step, 0, count - 1);
}
