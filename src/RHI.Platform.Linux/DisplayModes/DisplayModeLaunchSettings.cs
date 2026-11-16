using System.Globalization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311: a copy of one game's launch settings that display modes swap — the HDR launch mode (#250/#251), the resolution
/// switch (#191), the FPS limit (#310) and the NVIDIA settings (#211). Taken from the library state on the UI thread,
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
    /// <summary>A switch changed something (the desktop saves the library file and reloads the page's controls).</summary>
    public bool Changed { get; set; }

    public static DisplayModeLaunchValues From(LinuxGameLibraryState state, string key)
    {
        var profile = state.HdrProfiles.TryGetValue(key, out var saved) ? saved.Normalize() : new LinuxHdrGameProfile();
        return new DisplayModeLaunchValues
        {
            HdrMode = profile.Mode,
            ResolutionToggle = profile.ResolutionToggle,
            Resolution = profile.Resolution,
            FpsLimit = state.FpsLimits is { } limits && limits.TryGetValue(key, out var fps) ? fps : null,
            Nvidia = profile.Nvidia?.Copy() ?? new NvidiaGameSettings(),
        };
    }

    /// <summary>Puts these values into <paramref name="state"/> (the profile's other settings stay as they are).</summary>
    public void ApplyTo(LinuxGameLibraryState state, string key)
    {
        var profile = state.HdrProfiles.TryGetValue(key, out var saved) ? saved : new LinuxHdrGameProfile();
        profile.Mode = HdrMode;
        profile.ResolutionToggle = ResolutionToggle;
        profile.Resolution = Resolution;
        var nvidia = Nvidia.Copy().Normalize();
        profile.Nvidia = nvidia.IsDefault() ? null : nvidia;
        profile.Normalize();
        if (profile.IsDefault()) state.HdrProfiles.Remove(key);
        else state.HdrProfiles[key] = profile;
        var limits = state.FpsLimits ?? new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (FpsLimit is { } fps) limits[key] = fps;
        else limits.Remove(key);
        state.FpsLimits = limits.Count == 0 ? null : limits;
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
        _ => ctx.NvidiaOffered ? DisplayModeUnavailable.None : DisplayModeUnavailable.NvidiaNotOffered
    };

    public static string? Read(DisplayModeLaunchValues v, string itemKey)
    {
        var n = v.Nvidia;
        return itemKey switch
        {
            DisplayModeItemKeys.Hdr => LinuxHdrModes.Normalize(v.HdrMode),
            // Off keeps the chosen size ("Off:2560x1440"), so turning a mode off puts both back.
            DisplayModeItemKeys.Resolution => !v.ResolutionToggle
                ? v.Resolution is { } kept ? DisplayModeItems.ResolutionOff + ":" + kept : DisplayModeItems.ResolutionOff
                : v.Resolution ?? DisplayModeItems.ResolutionOn,
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
            _ => null
        };
    }

    /// <summary>Sets an item; null on success, else why the value was not taken.</summary>
    public static string? Write(DisplayModeLaunchValues v, string itemKey, string value)
    {
        var before = (v.HdrMode, v.ResolutionToggle, v.Resolution, v.FpsLimit, Nvidia: System.Text.Json.JsonSerializer.Serialize(v.Nvidia));
        var problem = Set(v, itemKey, value);
        if (problem is null)
            v.Changed |= before != (v.HdrMode, v.ResolutionToggle, v.Resolution, v.FpsLimit, System.Text.Json.JsonSerializer.Serialize(v.Nvidia));
        return problem;
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
                if (value == DisplayModeItems.ResolutionOff) v.ResolutionToggle = false;
                else if (value.StartsWith(DisplayModeItems.ResolutionOff + ":", StringComparison.Ordinal)
                         && DisplayResolution.Parse(value[(DisplayModeItems.ResolutionOff.Length + 1)..]) is { } keptSize)
                {
                    v.ResolutionToggle = false;
                    v.Resolution = keptSize.ToString();
                }
                else if (value == DisplayModeItems.ResolutionOn) v.ResolutionToggle = true;
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

    public Task<string?> WriteAsync(DisplayModeContext ctx, DisplayModeItem item, string value, CancellationToken cancellationToken)
        => item.Target == DisplayModeTarget.LaunchSettings
            ? Task.FromResult(DisplayModeLaunchSettings.Write(Launch(ctx), item.Key, value))
            : files.WriteAsync(ctx, item, value, cancellationToken);

    private static DisplayModeLaunchValues Launch(DisplayModeContext ctx)
        => ctx.Launch ?? throw new InvalidOperationException("The game's launch settings were not taken for this switch.");
}
