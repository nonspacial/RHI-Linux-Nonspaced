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

    public SdlGamepad()
    {
        try
        {
            _sdl = Sdl.GetApi();
            // RHI decides when to listen (its window is active); SDL must not drop events while another window has focus.
            _sdl.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS4", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS5", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_STEAM", "1");
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
        ushort buttons = 0;
        void Bit(GameControllerButton button, ushort bit)
        {
            if (sdl.GameControllerGetButton(_pad, button) != 0) buttons |= bit;
        }
        Bit(GameControllerButton.DpadUp, ControllerPoller.DPadUp);
        Bit(GameControllerButton.DpadDown, ControllerPoller.DPadDown);
        Bit(GameControllerButton.DpadLeft, ControllerPoller.DPadLeft);
        Bit(GameControllerButton.DpadRight, ControllerPoller.DPadRight);
        Bit(GameControllerButton.Start, ControllerPoller.Start);
        Bit(GameControllerButton.Back, ControllerPoller.BackBtn);
        Bit(GameControllerButton.Leftstick, ControllerPoller.LeftThumb);
        Bit(GameControllerButton.Rightstick, ControllerPoller.RightThumb);
        Bit(GameControllerButton.Leftshoulder, ControllerPoller.LeftShoulder);
        Bit(GameControllerButton.Rightshoulder, ControllerPoller.RightShoulder);
        Bit(GameControllerButton.A, ControllerPoller.A);
        Bit(GameControllerButton.B, ControllerPoller.B);
        Bit(GameControllerButton.X, ControllerPoller.X);
        Bit(GameControllerButton.Y, ControllerPoller.Y);
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
