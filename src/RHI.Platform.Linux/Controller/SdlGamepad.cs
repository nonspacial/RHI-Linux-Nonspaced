using RHI.Core.Services;
using Silk.NET.SDL;

namespace RHI.Platform.Linux.Controller;

/// <summary>
/// w14/layouts-b (#308): game controllers through SDL2's game controller API (Silk.NET.SDL, which carries its own SDL2).
/// Upstream reads XInput, and PlayStation and Steam Controllers over HID itself; SDL covers all three on Linux: Xbox-style
/// pads (xpad / xone), DualShock 4 / DualSense / Edge over USB or Bluetooth (HIDAPI driver, kernel driver otherwise) and
/// the Steam Controller (its HIDAPI driver, or the virtual pad Steam Input makes). SDL is asked for joystick events in the
/// background, because RHI itself only acts on them while its window is the active one. The first attached controller is
/// read; a new one is found when it is plugged in (looked for once a second). Nothing here throws: without a usable SDL
/// the reader reports <see cref="Available"/> false and the reason is logged once.
/// </summary>
public sealed unsafe class SdlGamepad : IDisposable
{
    private readonly Sdl? _sdl;
    private GameController* _pad;
    private long _nextScanMs;

    /// <summary>
    /// The hints set before <c>SDL_Init</c>. SDL must not drop events while another window has focus (RHI decides when to
    /// listen: its window is active). w14 final review nit: SDL_NO_SIGNAL_HANDLERS, so SDL's event subsystem leaves SIGINT
    /// and SIGTERM to .NET (SDL would turn them into a quit event nobody reads, and Ctrl+C or a session logout would not
    /// stop RHI).
    /// </summary>
    internal static readonly IReadOnlyList<(string Name, string Value)> Hints =
    [
        ("SDL_NO_SIGNAL_HANDLERS", "1"),
        ("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1"),
        ("SDL_JOYSTICK_HIDAPI", "1"),
        ("SDL_JOYSTICK_HIDAPI_PS4", "1"),
        ("SDL_JOYSTICK_HIDAPI_PS5", "1"),
        ("SDL_JOYSTICK_HIDAPI_STEAM", "1"),
    ];

    /// <summary>
    /// SDL's buttons read, in XInput's layout (upstream's 14). Nothing else is read: an Xbox Elite Series 2's back paddles
    /// (SDL's Paddle1–4, seen as extra buttons; upstream ignores them too), the Share / Misc1 button, the Guide button and a
    /// PlayStation touchpad click do nothing in RHI. A paddle the controller itself maps to A, B, … (an Elite profile) arrives
    /// as that button and works as it.
    /// </summary>
    internal static readonly IReadOnlyList<(GameControllerButton Button, ushort Bit)> ButtonMap =
    [
        (GameControllerButton.DpadUp, ControllerPoller.DPadUp),
        (GameControllerButton.DpadDown, ControllerPoller.DPadDown),
        (GameControllerButton.DpadLeft, ControllerPoller.DPadLeft),
        (GameControllerButton.DpadRight, ControllerPoller.DPadRight),
        (GameControllerButton.Start, ControllerPoller.Start),
        (GameControllerButton.Back, ControllerPoller.BackBtn),
        (GameControllerButton.Leftstick, ControllerPoller.LeftThumb),
        (GameControllerButton.Rightstick, ControllerPoller.RightThumb),
        (GameControllerButton.Leftshoulder, ControllerPoller.LeftShoulder),
        (GameControllerButton.Rightshoulder, ControllerPoller.RightShoulder),
        (GameControllerButton.A, ControllerPoller.A),
        (GameControllerButton.B, ControllerPoller.B),
        (GameControllerButton.X, ControllerPoller.X),
        (GameControllerButton.Y, ControllerPoller.Y),
    ];

    /// <summary>The button bits (XInput's layout) from SDL's pressed buttons (<see cref="ButtonMap"/> only).</summary>
    internal static ushort ButtonsFrom(Func<GameControllerButton, bool> isDown)
    {
        ushort buttons = 0;
        foreach (var (button, bit) in ButtonMap)
            if (isDown(button)) buttons |= bit;
        return buttons;
    }

    public SdlGamepad()
    {
        try
        {
            _sdl = Sdl.GetApi();
            foreach (var (name, value) in Hints) _sdl.SetHint(name, value);
            if (_sdl.Init(Sdl.InitGamecontroller) != 0)
            {
                AppLog.Warn("controller", "SDL could not start its game controller support: " + _sdl.GetErrorS());
                _sdl = null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or FileNotFoundException or InvalidOperationException)
        {
            AppLog.Warn("controller", "SDL2 is not available; controller support is off", ex);
            _sdl = null;
        }
    }

    /// <summary>Whether SDL started (a controller may still be missing).</summary>
    public bool Available => _sdl is not null;

    /// <summary>The connected controller's name, or null.</summary>
    public string? Name => _sdl is not null && _pad is not null ? _sdl.GameControllerNameS(_pad) : null;

    /// <summary>The controller's state now (<see cref="PadState.None"/> without one); false when no controller is attached.</summary>
    public bool TryRead(long nowMs, out PadState state)
    {
        state = PadState.None;
        if (_sdl is not { } sdl) return false;
        sdl.GameControllerUpdate();
        if (_pad is not null && sdl.GameControllerGetAttached(_pad) != SdlBool.True)
        {
            sdl.GameControllerClose(_pad);
            _pad = null;
        }
        if (_pad is null)
        {
            if (nowMs < _nextScanMs) return false;
            _nextScanMs = nowMs + 1000;
            for (var i = 0; i < sdl.NumJoysticks() && _pad is null; i++)
                if (sdl.IsGameController(i) == SdlBool.True) _pad = sdl.GameControllerOpen(i);
            if (_pad is null) return false;
            AppLog.Warn("controller", "Controller connected: " + Name);
        }
        var buttons = ButtonsFrom(button => sdl.GameControllerGetButton(_pad, button) != 0);
        short Axis(GameControllerAxis axis) => sdl.GameControllerGetAxis(_pad, axis);
        state = ControllerPoller.FromSdl(buttons, Axis(GameControllerAxis.Leftx), Axis(GameControllerAxis.Lefty), Axis(GameControllerAxis.Rightx),
            Axis(GameControllerAxis.Righty), Axis(GameControllerAxis.Triggerleft), Axis(GameControllerAxis.Triggerright));
        return true;
    }

    public void Dispose()
    {
        if (_sdl is null) return;
        if (_pad is not null) _sdl.GameControllerClose(_pad);
        _pad = null;
        _sdl.QuitSubSystem(Sdl.InitGamecontroller);
    }
}
