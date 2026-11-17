using System.Text.Json.Serialization;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311 (upstream 2.9.3 <c>Models/DisplayMode.cs</c>): one display mode, e.g. "TV", with its value per item key
/// (<see cref="DisplayModeItemKeys"/>). Every game switched to the mode gets these values.
/// </summary>
public sealed class DisplayModeDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Item key → value (the item's value format, see <see cref="DisplayModeItems"/>).</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One game's display mode state, keyed by the game's library key (<c>LinuxGameLibraryStore.GameKey</c>; upstream keys
/// "GameName|Store").
/// </summary>
public sealed class GameDisplayModeState
{
    /// <summary>Active mode id. Null = Off: the game is in no mode and follows the global settings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    /// <summary>True once a Custom mode was made for this game in its ⚙ (only this game shows it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Custom { get; set; }

    /// <summary>Settings the game's Custom mode changes (ticked in its ⚙). Null = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Items { get; set; }

    /// <summary>Mode id → item key → value. Only the Custom mode (<see cref="DisplayModeStore.CustomId"/>) is used.</summary>
    public Dictionary<string, Dictionary<string, string>> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Item key → the value the game had right after it entered its active mode (null = could not be read then). Null
    /// dictionary = never switched.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Entry { get; set; }

    /// <summary>
    /// Item key → the game's value before it entered a mode from Off. Turning the mode off writes these back for items
    /// with no global setting, and a mode with no value of its own uses them too. Null dictionary = none recorded; a null
    /// value (w14 final review m8) = the key or file did not exist then, and Off removes it again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Original { get; set; }

    /// <summary>
    /// w14 final review M3: the game left its mode (the mode or its Custom mode was deleted) before its values from before
    /// modes could be written back (it was running, not found, or a file could not be written). <see cref="Original"/> is
    /// kept; Off writes it back, and a switch to a mode keeps it as the game's values from before modes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RestorePending { get; set; }
}

/// <summary>
/// Everything saved for Display modes, in <c>~/.config/rhi-linux/display-modes.json</c> (upstream: the "DisplayModes"
/// entry of settings.json, and its on/off switch in Settings).
/// </summary>
public sealed class DisplayModeData
{
    /// <summary>Settings › Display &amp; HDR › Display modes › Show on game pages (upstream <c>DisplayModesEnabled</c>; off by default).</summary>
    public bool Enabled { get; set; }

    public List<DisplayModeDefinition> Modes { get; set; } = [];

    public Dictionary<string, GameDisplayModeState> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
