using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// ReShade for Windows Vulkan games under Wine/Proton (#92; spike <c>reviews/w5-92-spike.md</c>). Wine's own
/// <c>vulkan-1.dll</c> loads no Windows layers, so a game gets:
/// <list type="bullet">
/// <item>the Khronos loader (<c>vulkan-1.dll</c> from LunarG's Vulkan runtime) beside its executable, loaded instead of
/// Wine's through a per-executable <c>vulkan-1=native,builtin</c> override (only this game sees the layer);</item>
/// <item>the layer (<c>ReShade64.dll</c>/<c>ReShade32.dll</c> + its json manifest) in its own folder of the prefix's
/// <c>C:\ProgramData\RHI-Linux\ReShadeVulkan\</c>, registered under
/// <c>HKLM\Software[\Wow6432Node]\Khronos\Vulkan\ImplicitLayers</c> in <c>system.reg</c> (the loader never reads HKCU
/// under Wine, and a 32-bit game reads only Wow6432Node). The path inside the prefix keeps the registration valid when
/// the game and its Steam library move;</item>
/// <item><c>ReShade.ini</c> beside the executable (ReShade's layer works from the game's folder: config, shaders, add-ons).</item>
/// </list>
/// DXVK and vkd3d-proton load <c>winevulkan.dll</c> directly, so DirectX games never see the layer. Offered only for a
/// game whose effective API is Vulkan (owner rule: never default a game to Vulkan).
/// </summary>
public static partial class ReShadeVulkanLayer
{
    public const string LayerName = "VK_LAYER_reshade";
    public const string LoaderFileName = "vulkan-1.dll";
    public const string LoaderOverrideName = "vulkan-1";
    /// <summary>Record metadata: which way ReShade was installed (<see cref="PathValue"/> for the layer; absent for the DLL proxy).</summary>
    public const string MetaPath = "ReShade.Path";
    public const string PathValue = "VulkanLayer";
    public const string MetaLoaderVersion = "ReShade.VulkanLoaderVersion";
    public const string DownloadPartLoader = "VulkanLoader";
    internal const string ImplicitLayersKey = @"Software\Khronos\Vulkan\ImplicitLayers";
    internal const string ImplicitLayersKey32 = @"Software\Wow6432Node\Khronos\Vulkan\ImplicitLayers";
    public const int MinimumMajorVersion = 8;
    /// <summary>Component row name on a Vulkan game (the row's Install is the explicit "install ReShade as a Vulkan layer" choice).</summary>
    public const string RowName = "ReShade · Vulkan";

    // Platform messages: fixed text first, values last (PT-BR presentation pairs match the fixed part).
    public const string NotVulkanTargetMessage =
        "ReShade's Vulkan layer is offered only for Windows games that use Vulkan: Vulkan alone found in the game's executable, or the graphics API set to Vulkan in the game's settings.";
    public const string PrefixNotStartedMessage =
        "The game's Wine/Proton prefix is not set up yet. Start the game once and close it, then install ReShade.";
    public const string RunnerTooOldPrefix =
        "ReShade's Vulkan layer needs Proton or Wine 8 or newer. This game's prefix was made by: ";
    public const string UnknownRunnerMessage =
        "RHI cannot tell which Proton or Wine version runs this game, and ReShade's Vulkan layer needs version 8 or newer. Choose a Proton version for the game in its launcher, start the game once, then try again.";
    public const string Win32PrefixMessage =
        "This 32-bit game runs in a 32-bit-only Wine prefix (an older Wine or Lutris prefix), where ReShade's Vulkan layer cannot load, so it is not offered. Proton prefixes and new Wine prefixes are 64-bit.";
    public const string GameShipsLoaderPrefix =
        "This game has its own vulkan-1.dll beside its executable; RHI does not replace it with the Vulkan loader ReShade needs, so nothing was installed. File: ";
    public const string DllReShadeInstalledMessage =
        "ReShade is installed on this game as a DLL. A game gets one ReShade: remove it first, then install the Vulkan layer.";
    public const string NotOnRequestMessage =
        "On a Vulkan game RHI installs ReShade only when you ask for it: install ReShade (Vulkan layer) from its row first.";
    public const string DllOnlyOnVulkanGameMessage =
        "On a Vulkan game ReShade can only be installed as a Vulkan layer, and this component works only with ReShade as a DLL beside the game.";
    public const string NeedsDllReShadePrefix =
        "ReShade is installed on this game as a Vulkan layer, and this component works only with ReShade as a DLL beside the game, so nothing was installed: ";
    public const string LayerFolderTakenPrefix =
        "Something RHI did not install already sits where ReShade's Vulkan layer goes, so nothing was installed: ";
    public const string IncompleteRecordMessage =
        "RHI's record of ReShade's Vulkan layer on this game is incomplete, so the update was stopped; remove ReShade and install it again.";
    public const string ApiSwitchLayerInstalledMessage =
        "ReShade is installed on this game as a Vulkan layer. Remove ReShade first, then change the graphics API.";
    public const string ApiSwitchDllInstalledMessage =
        "ReShade is installed on this game as a DLL, which a Vulkan game does not load. Remove ReShade first, then set the graphics API to Vulkan.";
    public const string RowNote =
        "Vulkan game: ReShade is installed as a Vulkan layer for this game only (the Khronos Vulkan loader beside the executable). Single-player/offline titles only; add-on injection can trigger anti-cheat.";
    public const string InstalledPrefix = "ReShade (Vulkan layer) installed; ReShade.ini beside the game. Version: ";
    public const string UpdatedPrefix = "ReShade (Vulkan layer) updated. Version: ";
    public const string LoaderUpdatedNote = " The Vulkan loader was updated too.";
    public const string LoaderCheckSkippedNote = " LunarG could not be reached, so the Vulkan loader was left as it is.";
    public const string LoaderRestoredNote = " The Vulkan loader beside the game was missing and was put back.";
    public const string LoaderMissingNotRestoredPrefix =
        "The Vulkan loader beside the game is missing and RHI could not fetch it again, so nothing was changed; try again when LunarG can be reached: ";
    public const string RemovedMessage = "ReShade (Vulkan layer) removed: the loader, the layer, its registration and the Wine override are gone.";
    public const string FailedRolledBackPrefix = "ReShade (Vulkan layer) failed and was rolled back; nothing was changed: ";
    public const string ModifiedFilePrefix = "A file of ReShade's Vulkan layer was changed outside RHI, so it was left as it is and nothing was changed: ";

    /// <summary>Whether a record is a Vulkan-layer ReShade.</summary>
    public static bool IsLayerRecord(LinuxDeploymentRecord? record)
        => record is not null && record.Component == ManagedComponentKind.ReShade
           && string.Equals(record.Metadata.GetValueOrDefault(MetaPath), PathValue, StringComparison.Ordinal);

    /// <summary>
    /// The game's effective graphics API is Vulkan by evidence the owner accepts: the user's own API override, or (without
    /// one) Vulkan alone at High confidence. A multi-renderer exe never qualifies on detection alone (#53: DirectX first).
    /// </summary>
    public static bool IsVulkanTarget(DetectedGame game)
        => game.HasExplicitGraphicsApiOverride
            ? game.UserGraphicsApiOverride == GraphicsApi.Vulkan
            : game.EffectiveGraphicsApi == GraphicsApi.Vulkan
              && game.GraphicsApiConfidence == GraphicsApiConfidence.High
              && game.GraphicsApis.All(api => api == GraphicsApi.Vulkan);

    /// <summary>Why the layer cannot be offered to <paramref name="game"/> now (plain reason), or null.</summary>
    public static string? OfferRefusal(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return NotVulkanTargetMessage;
        if (!IsVulkanTarget(game)) return NotVulkanTargetMessage;
        return PrefixRefusal(game);
    }

    /// <summary>The prefix part of <see cref="OfferRefusal"/>: initialised (both hives) and run by Proton/Wine 8+.</summary>
    public static string? PrefixRefusal(DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath)
            || !WineRegistryEditor.HasSystemHive(game.PrefixPath) || !WineRegistryEditor.HasUserHive(game.PrefixPath))
            return PrefixNotStartedMessage;
        // Review nit 7: a 32-bit game reads the layer from Wow6432Node, which a win32-only prefix does not have.
        if (GameArchitecture.Of(game) == PeArchitecture.X86 && IsWin32OnlyPrefix(game.PrefixPath)) return Win32PrefixMessage;
        string? marker;
        try
        {
            var runtime = WinePrefixDetector.PrefixRuntime.Read(game.PrefixPath);
            marker = runtime.IsProton ? runtime.Version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("reshade-vulkan", $"Could not read the runtime markers of {game.PrefixPath}", ex);
            marker = null;
        }
        return RunnerRefusal(marker, game.CompatibilityTool);
    }

    /// <summary>Whether the prefix's <c>system.reg</c> header says <c>#arch=win32</c> (read from the first lines only).</summary>
    internal static bool IsWin32OnlyPrefix(string prefixPath)
    {
        try
        {
            using var reader = new StreamReader(WineRegistryEditor.SystemHivePath(prefixPath), System.Text.Encoding.Latin1);
            for (var i = 0; i < 16 && reader.ReadLine() is { } line; i++)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#arch=", StringComparison.OrdinalIgnoreCase))
                    return trimmed["#arch=".Length..].Trim().Equals("win32", StringComparison.OrdinalIgnoreCase);
                if (trimmed.StartsWith('[')) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("reshade-vulkan", $"Could not read the architecture of {prefixPath}", ex);
        }
        return false;
    }

    /// <summary>
    /// The runner rule: the prefix's Proton marker (<c>version</c>/<c>config_info</c>: <c>GE-Proton10-18</c>,
    /// <c>9.0-203</c>, <c>10.1000-200</c>) first, else the launcher's runner name (<c>wine-ge-8-26</c>,
    /// <c>lutris-GE-Proton8-26-x86_64</c>, <c>wine-11.19</c>); major version 8 or newer, unknown refused.
    /// </summary>
    internal static string? RunnerRefusal(string? prefixMarker, string? compatibilityTool)
    {
        foreach (var name in new[] { prefixMarker, compatibilityTool })
        {
            if (MajorVersion(name) is not { } major) continue;
            return major >= MinimumMajorVersion ? null : RunnerTooOldPrefix + name!.Trim();
        }
        return UnknownRunnerMessage;
    }

    /// <summary>The major version in a Proton/Wine runner name, or null when it names none.</summary>
    internal static int? MajorVersion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = NamedVersion().Match(name);
        if (!match.Success) match = LeadingVersion().Match(name.Trim());
        return match.Success && int.TryParse(match.Groups["major"].Value, out var major) && major is > 0 and < 1000 ? major : null;
    }

    [GeneratedRegex(@"(?:proton|wine)(?:[-_ ]?ge)?[-_ ]?(?<major>\d{1,3})(?:[.\-_]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedVersion();

    [GeneratedRegex(@"^(?:experimental[-_])?(?<major>\d{1,3})\.\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersion();

    /// <summary>The game's folder for the layer inside its prefix, and the same folder as Windows sees it (<c>C:\…</c>).</summary>
    public static (string LinuxPath, string WindowsPath) LayerFolder(DetectedGame game) => ReShadeVulkanLayerFolder.For(game);

    /// <summary>The prefix folders every game's layer shares (<c>ProgramData\RHI-Linux\ReShadeVulkan</c> and <c>ProgramData\RHI-Linux</c>).</summary>
    public static IReadOnlyList<string> SharedFolders(string prefixPath)
    {
        var root = ReShadeVulkanLayerFolder.Root(prefixPath);
        return [root, Path.GetDirectoryName(root)!];
    }

    public static string LayerDllName(bool is32) => is32 ? "ReShade32.dll" : "ReShade64.dll";
    public static string ManifestName(bool is32) => ReShadeVulkanLayerFolder.ManifestName(is32);
    internal static string RegistryKey(bool is32) => is32 ? ImplicitLayersKey32 : ImplicitLayersKey;

    /// <summary>The layer manifest (upstream's <c>ReShade64.json</c>; the 32-bit one names <c>ReShade32.dll</c>).</summary>
    public static byte[] Manifest(bool is32)
    {
        var json = new
        {
            file_format_version = "1.0.0",
            layer = new
            {
                name = LayerName,
                type = "GLOBAL",
                library_path = @".\" + LayerDllName(is32),
                api_version = "1.3.268",
                implementation_version = "1",
                description = $"crosire's ReShade post-processing injector for {(is32 ? 32 : 64)}-bit",
                device_extensions = new[]
                {
                    new { name = "VK_EXT_tooling_info", spec_version = "1", entrypoints = new[] { "vkGetPhysicalDeviceToolPropertiesEXT" } }
                },
                disable_environment = new Dictionary<string, string> { ["DISABLE_VK_LAYER_reshade_1"] = "1" }
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(json, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The loader beside the executable when the game ships its own (not RHI's): the foreign-file refusal.</summary>
    internal static string? ForeignLoader(string binaryDirectory, LinuxDeploymentRecord? layerRecord)
    {
        var path = ReShadeDeploymentService.FindFileCaseInsensitive(binaryDirectory, LoaderFileName);
        if (path is null) return null;
        var owned = layerRecord?.Files.Any(file => string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(path), StringComparison.Ordinal)) == true;
        return owned ? null : path;
    }
}

/// <summary>
/// The Khronos Vulkan loader from LunarG's Vulkan runtime components (#92): the newest version from
/// <c>vulkan.lunarg.com/sdk/latest.json</c>, the zip from <c>sdk.lunarg.com</c> under
/// <see cref="DownloadSources.LunarGVulkanRuntime"/> (LunarG publishes no digest: HTTPS + host policy, SHA-256 recorded),
/// cached through <see cref="CacheStamp"/>, the x64/x86 <c>vulkan-1.dll</c> extracted bounded and bound to the zip.
/// </summary>
public sealed class VulkanRuntimeProvider(HttpClient http, string cacheDir)
{
    public const string LatestUrl = "https://vulkan.lunarg.com/sdk/latest.json";
    /// <summary>Largest loader DLL accepted from the zip (real ones are under 2 MB).</summary>
    public const long LoaderMaxBytes = 16L * 1024 * 1024;
    public const string LatestUnavailableMessage = "Could not find the current Vulkan loader version on LunarG's site.";
    public const string LoaderMissingPrefix = "The LunarG Vulkan runtime download does not contain the Vulkan loader for this game: ";
    public const string LoaderInvalidPrefix = "The downloaded Vulkan loader is not a valid Vulkan loader for this game: ";

    /// <summary>The versioned runtime zip (LunarG names it <c>VulkanRT-X64-&lt;v&gt;-Components.zip</c> since 1.4).</summary>
    public static string RuntimeUrl(string version) => $"https://sdk.lunarg.com/sdk/download/{version}/windows/VulkanRT-X64-{version}-Components.zip";

    /// <summary>The newest Windows runtime version LunarG lists.</summary>
    public async Task<string> LatestVersionAsync(CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await TrustedDownload.GetStringAsync(http, LatestUrl, DownloadSources.LunarGVulkanRuntime, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(LatestUnavailableMessage, ex);
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var version = document.RootElement.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.String ? windows.GetString() : null;
            if (version is null || !Regex.IsMatch(version, @"^\d{1,3}\.\d{1,3}\.\d{1,4}(\.\d{1,4})?$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException(LatestUnavailableMessage);
            return version;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(LatestUnavailableMessage, ex);
        }
    }

    /// <summary>The loader for <paramref name="architecture"/> from <paramref name="version"/>'s runtime (cached), with the zip's download facts.</summary>
    public async Task<(string LoaderPath, VerifiedDownload Download)> EnsureLoaderAsync(string version, PeArchitecture architecture, CancellationToken cancellationToken)
    {
        var url = RuntimeUrl(version);
        var folder = Path.Combine(cacheDir, version);
        var zip = Path.Combine(folder, "VulkanRT-Components.zip");
        var download = await TrustedDownload.EnsureVerifiedAsync(http, url, zip, DownloadSources.LunarGVulkanRuntime, version,
            validate: ReShadeArchiveExtractor.RequireReadableZip, cancellationToken: cancellationToken).ConfigureAwait(false);
        var arch = architecture == PeArchitecture.X86 ? "x86" : "x64";
        var loader = Path.Combine(folder, arch, ReShadeVulkanLayer.LoaderFileName);
        if (CacheStamp.IsValidBoundTo(loader, url, version, download.Sha256)) return (loader, download);

        CacheStamp.Invalidate(loader);
        Directory.CreateDirectory(Path.GetDirectoryName(loader)!);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith($"/{arch}/{ReShadeVulkanLayer.LoaderFileName}", StringComparison.OrdinalIgnoreCase));
            if (entry is null) throw new InvalidDataException(LoaderMissingPrefix + $"{arch}/{ReShadeVulkanLayer.LoaderFileName}");
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, loader, LoaderMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        if (!IsLoader(loader, architecture))
        {
            FileCleanup.TryDelete(loader);
            throw new InvalidDataException(LoaderInvalidPrefix + $"{arch}/{ReShadeVulkanLayer.LoaderFileName}");
        }
        CacheStamp.WriteBoundTo(loader, url, version, download.Sha256);
        return (loader, download);
    }

    /// <summary>A PE of the game's bitness whose version resource says it is the Vulkan loader.</summary>
    internal static bool IsLoader(string path, PeArchitecture architecture)
        => PeArchitectureDetector.Detect(path) == architecture
           && PeVersionReader.Read(path) is { FileDescription: { } description }
           && description.Contains("Vulkan Loader", StringComparison.OrdinalIgnoreCase);

    /// <summary>The version a runtime folder name or record gives, compared numerically (null when unreadable).</summary>
    internal static int Compare(string? candidate, string? installed)
        => Version.TryParse(candidate, out var a) && Version.TryParse(installed, out var b) ? a.CompareTo(b) : 0;
}
