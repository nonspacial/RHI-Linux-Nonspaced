using RHI.Core.Models;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// What the mode items need to know about one game. Built on the UI thread (plain values only: the desktop works out
/// which launch settings the game's page offers), used on a background thread.
/// </summary>
public sealed class DisplayModeContext
{
    public required DetectedGame Game { get; init; }

    /// <summary>The game's library key (<c>LinuxGameLibraryStore.GameKey</c>): its display mode state is saved under it.</summary>
    public required string Key { get; init; }

    public string GameName => Game.Name;

    /// <summary>The game page offers NVIDIA settings (#211: an NVIDIA GPU, a Proton game whose launcher takes variables).</summary>
    public bool NvidiaOffered { get; init; }

    /// <summary>The game page offers the FPS limit (#310).</summary>
    public bool FpsLimitOffered { get; init; }

    /// <summary>The game page offers the HDR launch mode (#251: a game that can use HDR, on a host that can run it).</summary>
    public bool HdrOffered { get; init; }

    /// <summary>The game page offers the resolution switch (#191).</summary>
    public bool ResolutionOffered { get; init; }

    /// <summary>
    /// w14/layouts-c: RHI adds launch arguments when it launches this game (a command RHI built, or Steam's <c>-applaunch</c>);
    /// not for a command the user typed or another launcher.
    /// </summary>
    public bool LaunchArgsOffered { get; init; }

    /// <summary>
    /// The game's launch settings, copied from the library state for this switch (null = launch items cannot be read or
    /// written); the desktop puts them back when <see cref="DisplayModeLaunchValues.Changed"/>.
    /// </summary>
    public DisplayModeLaunchValues? Launch { get; init; }
}

/// <summary>Why an item cannot be used for a game now (the desktop words it).</summary>
public enum DisplayModeUnavailable
{
    None,
    /// <summary>No game folder RHI may write to (native Linux game, folder gone, a write refusal).</summary>
    NoGameFolder,
    NoReShadeIni,
    /// <summary>ReShade is not installed by RHI: a copy RHI did not install is left alone.</summary>
    ReShadeNotManaged,
    RenoDxNotManaged,
    ReLimiterNotManaged,
    NoReLimiterIni,
    DisplayCommanderNotManaged,
    NvidiaNotOffered,
    FpsLimitNotOffered,
    HdrNotOffered,
    ResolutionNotOffered,
    /// <summary>w14/layouts-c: RHI does not add launch arguments when it launches this game.</summary>
    LaunchArgsNotOffered
}

/// <summary>Reads and writes the items of one game (game files through the journal, RHI's launch settings in memory).</summary>
public interface IDisplayModeIo
{
    DisplayModeUnavailable Unavailable(DisplayModeContext ctx, DisplayModeItem item);

    /// <summary>The game's current value, or null when it has none / it cannot be read.</summary>
    string? Read(DisplayModeContext ctx, DisplayModeItem item);

    /// <summary>
    /// Writes a value; null on success, else the problem (English, presented by the desktop). A null
    /// <paramref name="value"/> removes it (the game had none: w14 final review m8).
    /// </summary>
    Task<string?> WriteAsync(DisplayModeContext ctx, DisplayModeItem item, string? value, CancellationToken cancellationToken);
}
