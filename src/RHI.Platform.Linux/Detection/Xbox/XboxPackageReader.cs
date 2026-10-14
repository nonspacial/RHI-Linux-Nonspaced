using System.Xml;
using System.Xml.Linq;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>
/// What an Xbox / Game Pass package folder says about itself (Refs #90): <c>MicrosoftGame.config</c> (GDK, any
/// spelling, configVersion 0 or 1) and <c>appxmanifest.xml</c>. Read-only; names are matched case-insensitively and
/// the executable is returned in its on-disk spelling (a manifest may write <c>blooddungeon.exe</c> for
/// <c>BloodDungeon.exe</c>).
/// <c>Architecture</c> (the package's <c>ProcessorArchitecture</c>) and <c>ExecutableEncrypted</c> are read
/// but not applied to the card yet: taking the bitness of an encrypted exe from the package is #167.
/// </summary>
internal sealed record XboxPackage(
    string Root,
    string? ConfigPath,
    string? ManifestPath,
    string? StoreId,
    string? DisplayName,
    string? ExecutableName,
    string? Executable,
    PeArchitecture? Architecture,
    bool ExecutableEncrypted,
    bool ExecutableIsLauncher,
    string? Problem)
{
    /// <summary>True when the package names an executable that exists on disk (PartingX's own verify rule, plus the config).</summary>
    public bool HasExecutable => Executable is not null;
}

internal static class XboxPackageReader
{
    public const string ConfigFileName = "MicrosoftGame.config";
    public const string ManifestFileName = "appxmanifest.xml";
    public const string StreamingFileName = ".xodus-streaming.msixvc";
    public const string StreamingTempFileName = ".xodus-streaming-tmp.msixvc";
    public const string AppxRecordFileName = ".partingx-appx.json";

    // Package XML files are a few KiB; anything far larger is not one.
    private const long MaxXmlBytes = 4 * 1024 * 1024;

    /// <summary>True when <paramref name="dir"/> holds a finished Xodus/PartingX package (msixvc or Appx record) and a config or manifest.</summary>
    public static bool LooksLikePackage(string dir)
        => (File.Exists(Path.Combine(dir, StreamingFileName)) || File.Exists(Path.Combine(dir, AppxRecordFileName))) &&
           (FindChild(dir, ConfigFileName) is not null || FindChild(dir, ManifestFileName) is not null);

    /// <param name="executableOverride">An executable the owning app was told to run instead (a PartingX user recipe).</param>
    public static XboxPackage? Read(string root, string? executableOverride = null)
    {
        var configPath = FindChild(root, ConfigFileName);
        var manifestPath = FindChild(root, ManifestFileName);
        if (configPath is null && manifestPath is null) return null;

        string? problem = null;
        var config = Load(configPath, ref problem);
        var manifest = Load(manifestPath, ref problem);

        var storeId = Text(config, "StoreId");
        var displayName = Usable(Attribute(config, "ShellVisuals", "DefaultDisplayName"))
                          ?? Usable(Text(manifest, "DisplayName"));
        var configExecutables = config?.Descendants().Where(e => e.Name.LocalName == "Executable").ToList() ?? [];
        var configExecutable = configExecutables
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("TargetDeviceFamily"), "PC", StringComparison.OrdinalIgnoreCase))
            ?? configExecutables.FirstOrDefault();
        var named = Usable(executableOverride)
                    ?? Usable((string?)configExecutable?.Attribute("Name"))
                    ?? Usable(Attribute(manifest, "Application", "Executable"));

        var executable = named is null ? null : ResolveOnDisk(root, named);
        var isLauncher = false;
        if (executable is not null && Path.GetFileName(executable).Equals("launcher.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Desktop App Converter shim: launcher.cfg line 1 names the real executable.
            var target = DacTarget(root);
            if (target is not null) executable = target;
            else isLauncher = true;
        }
        if (executable is not null && !isLauncher)
            isLauncher = DetectionHelpers.NameWords(Path.GetFileNameWithoutExtension(executable)).Any(word => word is "launcher" or "menu");
        if (named is not null && executable is null)
            problem ??= $"executable {named} not found";
        else if (named is null)
            problem ??= "no executable named";

        var architecture = Architecture(Attribute(manifest, "Identity", "ProcessorArchitecture"))
                           ?? Architecture(Text(config, "ProcessorArchitecture"));
        return new XboxPackage(root, configPath, manifestPath, Usable(storeId), displayName, named, executable, architecture,
            executable is not null && !StartsWithMz(executable), isLauncher, problem);
    }

    /// <summary>The entry of <paramref name="dir"/> whose name equals <paramref name="name"/> ignoring case; the exact spelling wins, case twins are ambiguous (null).</summary>
    internal static string? FindChild(string dir, string name)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("scan", $"Xbox package folder {dir} unreadable", ex);
            return null;
        }
        var matches = entries.Where(e => Path.GetFileName(e).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.FirstOrDefault(e => Path.GetFileName(e) == name) ?? (matches.Length == 1 ? matches[0] : null);
    }

    /// <summary>
    /// A package-relative path in its on-disk spelling, one component at a time; null for rooted paths, <c>..</c>, a
    /// missing component, a symlinked folder on the way, or case twins.
    /// </summary>
    internal static string? ResolveOnDisk(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part is "." ) continue;
            if (part == "..") return null;
            var next = FindChild(current, part);
            if (next is null) return null;
            var isLast = i == parts.Length - 1;
            if (!isLast && (!Directory.Exists(next) || IsLink(next))) return null;
            current = next;
        }
        return File.Exists(current) ? current : null;
    }

    private static string? DacTarget(string root)
    {
        var cfg = FindChild(root, "launcher.cfg");
        if (cfg is null) return null;
        try
        {
            var first = File.ReadLines(cfg).Select(line => line.Trim().TrimStart('\uFEFF')).FirstOrDefault(line => line.Length > 0);
            return first is null ? null : ResolveOnDisk(root, first);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("scan", $"Xbox launcher.cfg {cfg} unreadable", ex);
            return null;
        }
    }

    private static XElement? Load(string? path, ref string? problem)
    {
        if (path is null) return null;
        try
        {
            if (new FileInfo(path).Length > MaxXmlBytes)
            {
                problem ??= $"{Path.GetFileName(path)} too large";
                return null;
            }
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader).Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            problem ??= $"{Path.GetFileName(path)} unreadable: {ex.Message}";
            return null;
        }
    }

    private static string? Text(XElement? root, string element)
        => root?.Descendants().FirstOrDefault(e => e.Name.LocalName == element)?.Value.Trim();

    private static string? Attribute(XElement? root, string element, string attribute)
        => (string?)root?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == element)?.Attribute(attribute);

    // Localised resource references (ms-resource:…) are not names.
    private static string? Usable(string? value)
        => string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    private static PeArchitecture? Architecture(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "x86" => PeArchitecture.X86,
        "x64" => PeArchitecture.X64,
        "arm64" => PeArchitecture.Arm64,
        _ => null
    };

    private static bool StartsWithMz(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[2];
            using var stream = File.OpenRead(path);
            return stream.Read(magic) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("scan", $"Xbox executable {path} unreadable", ex);
            return false;
        }
    }

    internal static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("scan", $"{path} not followed (attributes unreadable)", ex);
            return true;
        }
    }
}
