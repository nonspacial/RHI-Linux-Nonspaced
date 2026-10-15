using System.Reflection;
using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>An INI file RHI deploys from a user-editable template (#181).</summary>
public enum UserIniTemplate
{
    /// <summary><c>reshade.ini</c>: merged into a game's ReShade.ini on install and on "Deploy ReShade.ini".</summary>
    ReShade,
    /// <summary><c>reshade.vulkan.ini</c>: the Vulkan-layer install's ReShade.ini (wave 5 #92 uses it).</summary>
    ReShadeVulkan,
    /// <summary><c>reshade.rdr2.ini</c>: used instead of reshade.ini for Red Dead Redemption 2 and Max Payne 3.</summary>
    ReShadeRdr2,
    /// <summary><c>relimiter.ini</c>: a new ReLimiter install's config.</summary>
    ReLimiter,
    /// <summary><c>DisplayCommander.ini</c>: a new Display Commander install's config.</summary>
    DisplayCommander
}

/// <summary>
/// The user's INI templates (#181; upstream 2.8.7 <c>AuxInstallService.EnsureInisDir</c> and the <c>inis</c> folder): one
/// file per template in RHI's INIs folder (<c>~/.config/rhi-linux/inis</c>, beside the OptiScaler templates), named as
/// upstream. A file is seeded from the copy RHI ships the first time it is needed and never overwritten afterwards, so
/// the user's edits are what every later install and "Deploy … .ini" uses. Settings that are "applied to all games"
/// (ReLimiter's, #188) are written into the template too, as upstream does, so later installs inherit them.
/// </summary>
public sealed class UserIniTemplates(string directory)
{
    /// <summary>The largest template RHI reads (a template is a few lines; anything bigger is not one).</summary>
    public const long MaxTemplateBytes = 1024 * 1024;

    /// <summary>Fixed text before the path when a template is too big to be one (localisation key).</summary>
    public const string TooLargePrefix = "INI template ignored: it is larger than 1 MiB: ";

    /// <summary>The INIs folder (created on first seeding).</summary>
    public string Directory { get; } = directory;

    /// <summary>Upstream's file name for a template.</summary>
    public static string FileName(UserIniTemplate template) => template switch
    {
        UserIniTemplate.ReShadeVulkan => "reshade.vulkan.ini",
        UserIniTemplate.ReShadeRdr2 => "reshade.rdr2.ini",
        UserIniTemplate.ReLimiter => "relimiter.ini",
        UserIniTemplate.DisplayCommander => "DisplayCommander.ini",
        _ => "reshade.ini"
    };

    /// <summary>Where the template lives (it may not exist yet).</summary>
    public string PathOf(UserIniTemplate template) => Path.Combine(Directory, FileName(template));

    /// <summary>
    /// The template's path, seeded from RHI's copy when it does not exist yet (never overwritten). Throws when the folder
    /// or file cannot be written.
    /// </summary>
    public string EnsureSeeded(UserIniTemplate template)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(template);
        if (File.Exists(path)) return path;
        try { AtomicFile.WriteNew(path, Shipped(template)); }
        catch (IOException) when (File.Exists(path)) { } // seeded meanwhile by another instance: theirs stays
        return path;
    }

    /// <summary>Seeds every missing template (upstream does this at start-up; RHI when the folder is opened).</summary>
    public IReadOnlyList<string> SeedAll() => Enum.GetValues<UserIniTemplate>().Select(EnsureSeeded).ToList();

    /// <summary>
    /// The template's bytes: the user's file (seeded first when missing). When the folder cannot be written or the file
    /// cannot be read, RHI's shipped copy is used instead and <paramref name="note"/> says why, so an install never fails
    /// over a template.
    /// </summary>
    public byte[] Read(UserIniTemplate template, out string? note)
    {
        note = null;
        try
        {
            var path = EnsureSeeded(template);
            var length = new FileInfo(path).Length;
            if (length > MaxTemplateBytes)
            {
                note = TooLargePrefix + path;
                return Shipped(template);
            }
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = ex.Message;
            return Shipped(template);
        }
    }

    /// <summary>The ReShade.ini template for a game: RDR2 and Max Payne 3 get upstream's dedicated one.</summary>
    public static UserIniTemplate ReShadeTemplateFor(DetectedGame game, bool vulkan = false)
        => IsRdr2Template(game.Name) ? UserIniTemplate.ReShadeRdr2 : vulkan ? UserIniTemplate.ReShadeVulkan : UserIniTemplate.ReShade;

    /// <summary>Upstream <c>AuxInstallService.IsRdr2</c>: the games that use <c>reshade.rdr2.ini</c> (End opens the overlay).</summary>
    public static bool IsRdr2Template(string? gameName)
        => gameName is { Length: > 0 } name
           && (name.Contains("Red Dead Redemption 2", StringComparison.OrdinalIgnoreCase)
               || name.Equals("RDR2", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Max Payne 3", StringComparison.OrdinalIgnoreCase));

    /// <summary>The template RHI ships (upstream 2.8.7's bundled file, without its byte-order mark).</summary>
    internal static byte[] Shipped(UserIniTemplate template)
    {
        var name = "UserIniTemplates." + FileName(template);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("INI template missing from the build: " + name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
