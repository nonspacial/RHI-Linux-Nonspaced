namespace RHI.Core.Services;

/// <summary>
/// The Xbox installer app RHI reads read-only (Refs #90): a private app that installs Xbox / Game Pass titles, announces
/// its instances with breadcrumb files, keeps a state folder and marks its payload folders. RHI never names it in its own
/// text: the user sees <see cref="DisplayName"/> (presented as "the app that installed it"). Its name exists only as
/// <see cref="Tag"/>, the spelling that app uses for its own files, folders, program and links, which RHI must match.
/// </summary>
public static class XboxInstallerApp
{
    // Functional only, never shown: the installer app's own name as it spells its files and folders (breadcrumb folder
    // under $XDG_DATA_HOME, state folder, payload markers, desktop entry, program, link scheme, breadcrumb "app" field).
    private const string Tag = "PartingX";

    /// <summary>The owning-app token on cards this app installed (<see cref="Models.XboxPackageInfo.OwningApp"/>); neutral.</summary>
    public const string DisplayName = "Xbox app";

    /// <summary>The app's name as its breadcrumbs' <c>app</c> field, its desktop entry and its start scripts write it.</summary>
    public static string ProductTag => Tag;

    /// <summary>Lower-case spelling used for its folders, program and file names.</summary>
    public static readonly string FileTag = Tag.ToLowerInvariant();

    /// <summary>Its folder under <c>$XDG_DATA_HOME</c> (breadcrumbs in <c>instances/</c>) and <c>$XDG_STATE_HOME</c> (state).</summary>
    public static readonly string FolderName = FileTag;

    /// <summary>The marker in a finished payload folder (JSON: storeId, packageTitle…).</summary>
    public static readonly string OwnedMarker = "." + FileTag + "-owned";

    /// <summary>The marker of an unfinished download.</summary>
    public static readonly string PartialMarker = "." + FileTag + "-partial";

    /// <summary>The Appx package record in a payload folder.</summary>
    public static readonly string AppxRecordFile = "." + FileTag + "-appx.json";

    /// <summary>The run marker the app keeps in a payload folder while the title runs.</summary>
    public static readonly string RunMarker = "." + FileTag + "-run";

    /// <summary>The per-title environment file, beside the title's <c>prefix/</c>.</summary>
    public static readonly string EnvironmentFile = FileTag + ".json";

    /// <summary>Its link scheme (<c>&lt;tag&gt;:</c>), a hand-off to the app.</summary>
    public static readonly string UriScheme = FileTag + ":";
}
