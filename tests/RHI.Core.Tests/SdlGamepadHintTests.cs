using RHI.Platform.Linux.Controller;

namespace RHI.Core.Tests;

/// <summary>w14 final review nit: SDL leaves the process's signals to .NET (SDL_NO_SIGNAL_HANDLERS set before SDL_Init).</summary>
public sealed class SdlGamepadHintTests
{
    [Fact]
    public void SdlDoesNotTakeTheSignalHandlers()
    {
        Assert.Contains(("SDL_NO_SIGNAL_HANDLERS", "1"), SdlGamepad.Hints);
        Assert.Equal(SdlGamepad.Hints.Count, SdlGamepad.Hints.Select(h => h.Name).Distinct().Count());
    }
}
