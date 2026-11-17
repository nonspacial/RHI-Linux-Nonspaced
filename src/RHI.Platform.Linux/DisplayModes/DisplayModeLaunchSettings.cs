using System.Globalization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311: a copy of one game's launch settings that display modes swap — the HDR launch mode (#250/#251), the resolution
/// switch (#191), the FPS limit (#310), the NVIDIA settings (#211) and the launch arguments (w14/layouts-c). Taken from the library state on the UI thread,
/// changed by a switch on a background thread, then put back and saved by the desktop (<see cref="ApplyTo"/>), so the
/// library state is only ever touched on the UI thread. Nothing is written to disk by the switch itself.
/// </summary>
public sealed class DisplayModeLaunchValues
{
    public string HdrMode { get; set; } = LinuxHdrModes.Off;
    public bool ResolutionToggle { get; set; }
    public string? Resolution { get; set; }
    /// <summary>The game's own FPS limit: null = Global, 0 = Off, else 20–1000.</summary>
    public uint? FpsLimit { get; set; }
    public NvidiaGameSettings Nvidia { get; set; } = new();
    /// <summary>w14/layouts-c: the game's launch arguments (null = none).</summary>
    public string? LaunchArgs { get; set; }
    /// <summary>A switch changed something (the desktop saves the library file and reloads the page's controls).</summary>
    public bool Changed { get; set; }

    // What the copy held when it was taken: ApplyTo puts back only what the switch changed since (w14 final review nit).
    private (string Hdr, bool Toggle, string? Resolution, uint? Fps, string Nvidia, string? Args)? _taken;

    private (string Hdr, bool Toggle, string? Resolution, uint? Fps, string Nvidia, string? Args) Now()
        => (HdrMode, ResolutionToggle, Resolution, FpsLimit, System.Text.Json.JsonSerializer.Serialize(Nvidia), LaunchArgs);

    public static DisplayModeLaunchValues From(LinuxGameLibraryState state, string key)
    {
        var profile = state.HdrProfiles.TryGetValue(key, out var saved) ? saved.Normalize() : new LinuxHdrGameProfile();
        var values = new DisplayModeLaunchValues
        {
            HdrMode = profile.Mode,
            ResolutionToggle = profile.ResolutionToggle,
            Resolution = profile.Resolution,
            FpsLimit = state.FpsLimits is { } limits && limits.TryGetValue(key, out var fps) ? fps : null,
            Nvidia = profile.Nvidia?.Copy() ?? new NvidiaGameSettings(),
            LaunchArgs = state.LaunchArguments is { } args && args.TryGetValue(key, out var text) ? text : null,
        };
        values._taken = values.Now();
        return values;
    }

    /// <summary>
    /// Puts these values into <paramref name="state"/> (the profile's other settings stay as they are). Only the settings
    /// the switch changed since the copy was taken: one the user changed on the page while the switch ran stays as the
    /// user set it (w14 final review nit).
    /// </summary>
    public void ApplyTo(LinuxGameLibraryState state, string key)
    {
        var now = Now();
        var taken = _taken;
        bool Changed<T>(Func<(string Hdr, bool Toggle, string? Resolution, uint? Fps, string Nvidia, string? Args), T> part)
            => taken is not { } t || !EqualityComparer<T>.Default.Equals(part(t), part(now));
        var profile = state.HdrProfiles.TryGetValue(key, out var saved) ? saved : new LinuxHdrGameProfile();
        if (Changed(v => v.Hdr)) profile.Mode = HdrMode;
        if (Changed(v => v.Toggle)) profile.ResolutionToggle = ResolutionToggle;
        if (Changed(v => v.Resolution)) profile.Resolution = Resolution;
        if (Changed(v => v.Nvidia))
        {
            var nvidia = Nvidia.Copy().Normalize();
            profile.Nvidia = nvidia.IsDefault() ? null : nvidia;
        }
        profile.Normalize();
        if (profile.IsDefault()) state.HdrProfiles.Remove(key);
        else state.HdrProfiles[key] = profile;
        if (Changed(v => v.Fps))
        {
            var limits = state.FpsLimits ?? new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            if (FpsLimit is { } fps) limits[key] = fps;
            else limits.Remove(key);
            state.FpsLimits = limits.Count == 0 ? null : limits;
        }
        if (Changed(v => v.Args))
        {
            var arguments = state.LaunchArguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (RHI.Platform.Linux.Launch.LaunchArguments.Normalize(LaunchArgs) is { } text) arguments[key] = text;
            else arguments.Remove(key);
            state.LaunchArguments = arguments.Count == 0 ? null : arguments;
        }
    }
}

/// <summary>Reads and writes the launch-setting items on a game's <see cref="DisplayModeLaunchValues"/> (value formats in <see cref="DisplayModeItems"/>).</summary>
public static class DisplayModeLaunchSettings
{
    public static DisplayModeUnavailable Unavailable(DisplayModeContext ctx, DisplayModeItem item) => item.Key switch
    {
        DisplayModeItemKeys.Hdr => ctx.HdrOffered ? DisplayModeUnavailable.None : DisplayModeUnavailable.HdrNotOffered,
        DisplayModeItemKeys.Resolution => ctx.ResolutionOffered ? DisplayModeUnavailable.None : DisplayModeUnavailable.ResolutionNotOffered,
        DisplayModeItemKeys.DriverFpsCap => ctx.FpsLimitOffered ? DisplayModeUnavailable.None : DisplayModeUnavailable.FpsLimitNotOffered,
        DisplayModeItemKeys.LaunchArgs => ctx.LaunchArgsOffered ? DisplayModeUnavailable.None : DisplayModeUnavailable.LaunchArgsNotOffered,
        _ => ctx.NvidiaOffered ? DisplayModeUnavailable.None : DisplayModeUnavailable.NvidiaNotOffered
    };

    public static string? Read(DisplayModeLaunchValues v, string itemKey)
    {
        var n = v.Nvidia;
        return itemKey switch
        {
            DisplayModeItemKeys.Hdr => LinuxHdrModes.Normalize(v.HdrMode),
            // The exact state ("Off:", "Off:2560x1440", "On:", "3840x2160"), so turning a mode off puts the size back too.
            DisplayModeItemKeys.Resolution => !v.ResolutionToggle
                ? DisplayModeItems.ResolutionOff + ":" + v.Resolution
                : v.Resolution ?? DisplayModeItems.ResolutionOn + ":",
            DisplayModeItemKeys.DriverFpsCap => v.FpsLimit is { } fps ? fps.ToString(CultureInfo.InvariantCulture) : DisplayModeItems.FpsGlobal,
            DisplayModeItemKeys.MfgMode => NvidiaFrameGenerationModes.Normalize(n.FrameGenerationMode) switch
            {
                NvidiaFrameGenerationModes.Fixed => DisplayModeItems.FrameGenerationFixedPrefix + n.FrameCount.ToString(CultureInfo.InvariantCulture),
                NvidiaFrameGenerationModes.Dynamic => DisplayModeItems.FrameGenerationDynamic,
                _ => DisplayModeItems.FrameGenerationOff
            },
            DisplayModeItemKeys.DlssSrPreset => n.SuperResolutionPreset.ToString(CultureInfo.InvariantCulture),
            DisplayModeItemKeys.DlssRrPreset => n.RayReconstructionPreset.ToString(CultureInfo.InvariantCulture),
            DisplayModeItemKeys.DlssSrScale => n.SuperResolutionRenderScale.ToString(CultureInfo.InvariantCulture),
            DisplayModeItemKeys.DlssRrScale => n.RayReconstructionRenderScale.ToString(CultureInfo.InvariantCulture),
            // "" = no arguments: a value of its own, so "before modes" remembers it and Off puts it back.
            DisplayModeItemKeys.LaunchArgs => v.LaunchArgs ?? "",
            _ => null
        };
    }

    /// <summary>Sets an item; null on success, else why the value was not taken.</summary>
    public static string? Write(DisplayModeLaunchValues v, string itemKey, string value)
    {
        var before = (v.HdrMode, v.ResolutionToggle, v.Resolution, v.FpsLimit, Nvidia: System.Text.Json.JsonSerializer.Serialize(v.Nvidia), v.LaunchArgs);
        var problem = Set(v, itemKey, value);
        if (problem is null)
            v.Changed |= before != (v.HdrMode, v.ResolutionToggle, v.Resolution, v.FpsLimit, System.Text.Json.JsonSerializer.Serialize(v.Nvidia), v.LaunchArgs);
        return problem;
    }

    private static (bool On, string? Size)? ExactResolution(string value)
    {
        foreach (var (prefix, on) in new[] { (DisplayModeItems.ResolutionOff + ":", false), (DisplayModeItems.ResolutionOn + ":", true) })
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var size = value[prefix.Length..];
            if (size.Length == 0) return (on, null);
            return DisplayResolution.Parse(size) is { } parsed ? (on, parsed.ToString()) : null;
        }
        return null;
    }

    private static string? Set(DisplayModeLaunchValues v, string itemKey, string value)
    {
        const string NotTaken = "This value is not one RHI can set: ";
        var n = v.Nvidia;
        switch (itemKey)
        {
            case DisplayModeItemKeys.Hdr:
                if (LinuxHdrModes.Normalize(value) != value) return NotTaken + value;
                v.HdrMode = value;
                return null;
            case DisplayModeItemKeys.Resolution:
                // A mode's Off / On leave the game's size as it is; a game's own exact state ("Off:", "Off:WxH", "On:") sets it too.
                if (value == DisplayModeItems.ResolutionOff) v.ResolutionToggle = false;
                else if (value == DisplayModeItems.ResolutionOn) v.ResolutionToggle = true;
                else if (ExactResolution(value) is { } exact) (v.ResolutionToggle, v.Resolution) = exact;
                else if (DisplayResolution.Parse(value) is { } size) { v.ResolutionToggle = true; v.Resolution = size.ToString(); }
                else return NotTaken + value;
                return null;
            case DisplayModeItemKeys.DriverFpsCap:
                if (value == DisplayModeItems.FpsGlobal) v.FpsLimit = null;
                else if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var fps)
                         && (fps == FrameRateLimit.Off || FrameRateLimit.Valid(fps) is not null)) v.FpsLimit = fps;
                else return NotTaken + value;
                return null;
            case DisplayModeItemKeys.MfgMode:
                if (value == DisplayModeItems.FrameGenerationDynamic)
                {
                    n.FrameGenerationMode = NvidiaFrameGenerationModes.Dynamic;
                    n.FrameCount = 0;
                }
                else if (value.StartsWith(DisplayModeItems.FrameGenerationFixedPrefix, StringComparison.Ordinal)
                         && uint.TryParse(value[DisplayModeItems.FrameGenerationFixedPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                         && count is >= 1 and <= NvidiaGameSettings.MaxFrameCount)
                {
                    n.FrameGenerationMode = NvidiaFrameGenerationModes.Fixed;
                    n.FrameCount = count;
                }
                else if (value == DisplayModeItems.FrameGenerationOff)
                {
                    n.FrameGenerationMode = null;
                    n.FrameCount = 0;
                }
                else return NotTaken + value;
                n.Normalize();
                return null;
            case DisplayModeItemKeys.DlssSrPreset:
            case DisplayModeItemKeys.DlssRrPreset:
                if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var preset) || NvidiaGameSettings.NormalizePreset(preset) != preset)
                    return NotTaken + value;
                if (itemKey == DisplayModeItemKeys.DlssSrPreset) n.SuperResolutionPreset = preset;
                else n.RayReconstructionPreset = preset;
                return null;
            case DisplayModeItemKeys.DlssSrScale:
            case DisplayModeItemKeys.DlssRrScale:
                if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) || (scale != 0 && NvidiaGameSettings.Scale(scale) != scale))
                    return NotTaken + value;
                if (itemKey == DisplayModeItemKeys.DlssSrScale) n.SuperResolutionRenderScale = scale;
                else n.RayReconstructionRenderScale = scale;
                return null;
            case DisplayModeItemKeys.LaunchArgs:
                if (value.Trim().Length == 0) { v.LaunchArgs = null; return null; }
                if (RHI.Platform.Linux.Launch.LaunchArguments.Normalize(value) is not { } args) return NotTaken + value;
                v.LaunchArgs = args;
                return null;
            default:
                return NotTaken + itemKey;
        }
    }
}

/// <summary>The items of one game: game files through <see cref="DisplayModeGameFiles"/>, launch settings on the context's copy.</summary>
public sealed class DisplayModeIo(DisplayModeGameFiles files) : IDisplayModeIo
{
    public DisplayModeUnavailable Unavailable(DisplayModeContext ctx, DisplayModeItem item)
        => item.Target == DisplayModeTarget.LaunchSettings ? DisplayModeLaunchSettings.Unavailable(ctx, item) : files.Unavailable(ctx, item);

    public string? Read(DisplayModeContext ctx, DisplayModeItem item)
        => item.Target == DisplayModeTarget.LaunchSettings ? DisplayModeLaunchSettings.Read(Launch(ctx), item.Key) : files.Read(ctx, item);

    public Task<string?> WriteAsync(DisplayModeContext ctx, DisplayModeItem item, string? value, CancellationToken cancellationToken)
        => item.Target == DisplayModeTarget.LaunchSettings
            // A launch setting always has a value (Read never answers none), so there is nothing to remove.
            ? Task.FromResult(value is null ? null : DisplayModeLaunchSettings.Write(Launch(ctx), item.Key, value))
            : files.WriteAsync(ctx, item, value, cancellationToken);

    private static DisplayModeLaunchValues Launch(DisplayModeContext ctx)
        => ctx.Launch ?? throw new InvalidOperationException("The game's launch settings were not taken for this switch.");
}
