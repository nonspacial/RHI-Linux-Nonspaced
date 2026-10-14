using System.IO.Compression;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Ux;

namespace RHI.Platform.Linux.Network;

/// <summary>A confirmed Nexus download: where it went and what RHI can offer to install from it.</summary>
/// <param name="Folder">The download's own folder under RHI's cache (opened for the user when nothing is installable).</param>
/// <param name="Downloaded">The file as downloaded.</param>
/// <param name="Installable">ReShade add-ons (pulled out of a zip) or a Luma package, for the existing confirmed import/install paths.</param>
public sealed record NexusDownload(string Folder, string Downloaded, string Sha256, IReadOnlyList<string> Installable);

/// <summary>
/// Downloads the file a confirmed nxm:// link names (upstream <c>NexusDownloadService</c> + the NXM half of
/// <c>MainViewModel.Install.Nexus</c>). Nothing touches a game: the file goes to <c>$XDG_CACHE_HOME/rhi-linux/nexus/</c>
/// through <see cref="TrustedDownload"/> under <see cref="DownloadSources.NexusFiles"/>, and RHI then offers what it can
/// install through the paths dropped files already use (each asks again: import a ReShade add-on into Custom Add-ons,
/// install a Luma package into the selected game). Upstream copies a <c>renodx-*.addon64</c> straight into the game;
/// on Linux every add-on found in the archive is offered for import instead (the fork's drop rule: an add-on is only
/// imported after confirmation). Zip entries are read by name only (no folders, so nothing escapes the folder), capped
/// and CRC-checked (<c>ReShadeArchiveExtractor.ExtractEntryAsync</c>).
/// </summary>
public sealed class NexusDownloadService
{
    /// <summary>Largest add-on taken out of a downloaded archive.</summary>
    public const long AddonMaxBytes = TrustedDownloadPolicy.AddonMaxBytes;

    /// <summary>Fixed message (PT-BR presentation pair).</summary>
    public const string NoLinkMessage = "Nexus Mods gave no download link RHI accepts for this file.";

    private static readonly Regex UnsafeName = new("[^A-Za-z0-9._+()\\- ]", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly NexusApiClient _api;
    private readonly string _root;

    /// <param name="cacheRoot">Test seam (default: <c>$XDG_CACHE_HOME/rhi-linux/nexus</c>).</param>
    public NexusDownloadService(HttpClient http, NexusApiClient api, string? cacheRoot = null)
    {
        _http = http;
        _api = api;
        _root = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "nexus");
    }

    public async Task<NexusDownload> DownloadAsync(string apiKey, NxmLink link, NexusModFile? file, CancellationToken cancellationToken = default)
    {
        var links = await _api.GetDownloadLinksAsync(apiKey, link, cancellationToken).ConfigureAwait(false);
        if (links.Count == 0) throw new NexusApiException(NoLinkMessage, null);
        var uri = links[0];

        var folder = Path.Combine(_root, $"{link.GameDomain}-{link.ModId}-{link.FileId}");
        Directory.CreateDirectory(folder);
        var name = SafeFileName(file?.FileName) ?? SafeFileName(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)))
                   ?? $"nexus-{link.GameDomain}-{link.ModId}-{link.FileId}.zip";
        var target = Path.Combine(folder, name);
        string sha;
        using (var download = await TrustedDownload.DownloadToTempAsync(_http, uri.AbsoluteUri, target, DownloadSources.NexusFiles,
                   cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            sha = download.Sha256;
            download.MoveTo(target);
        }
        // Nexus publishes an MD5 in some links only; RHI records the SHA-256 of what it received (#26 rule).
        TrustedDownload.NoteAcceptedWithoutDigest(DownloadSources.NexusFiles, uri.GetLeftPart(UriPartial.Path), sha);
        return new NexusDownload(folder, target, sha, await InstallableAsync(target, folder, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>What can be offered from a downloaded file: itself (an add-on, a Luma package) or the add-ons inside a zip.</summary>
    internal static async Task<IReadOnlyList<string>> InstallableAsync(string downloaded, string folder, CancellationToken cancellationToken)
    {
        switch (LinuxDroppedFileClassifier.Classify(downloaded))
        {
            case LinuxDroppedFileKind.LumaAddon or LinuxDroppedFileKind.ReShadeAddon or LinuxDroppedFileKind.LumaArchive:
                return [downloaded];
            case LinuxDroppedFileKind.ModArchive when downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase):
                break;
            default:
                return [];
        }

        var found = new List<string>();
        var into = Path.Combine(folder, "add-ons");
        try
        {
            using var archive = ZipFile.OpenRead(downloaded);
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0) continue; // a folder
                var kind = LinuxDroppedFileClassifier.Classify(entry.Name);
                if (kind is not (LinuxDroppedFileKind.ReShadeAddon or LinuxDroppedFileKind.LumaAddon)) continue;
                if (SafeFileName(entry.Name) is not { } safe) continue;
                Directory.CreateDirectory(into);
                var destination = Path.Combine(into, safe);
                if (found.Contains(destination, StringComparer.Ordinal)) continue; // the same name twice in different folders: first wins
                FileCleanup.TryDelete(destination);
                if (await ReShadeArchiveExtractor.ExtractEntryAsync(entry, destination, AddonMaxBytes, cancellationToken).ConfigureAwait(false) is { } refused)
                {
                    AppLog.Warn("nexus", $"Skipped an add-on in {Path.GetFileName(downloaded)}: {refused}");
                    continue;
                }
                found.Add(destination);
            }
        }
        catch (InvalidDataException ex)
        {
            AppLog.Warn("nexus", $"{Path.GetFileName(downloaded)} is not a readable zip; nothing is offered from it", ex);
            return [];
        }
        return found;
    }

    /// <summary>A plain file name (no folders, no leading dots, safe characters only), or null.</summary>
    internal static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var leaf = name.Replace('\\', '/').Split('/').Last().Trim();
        leaf = UnsafeName.Replace(leaf, "_").TrimStart('.');
        if (leaf.Length > 120) leaf = leaf[^120..];
        return leaf.Length == 0 || leaf.All(c => c == '_') ? null : leaf;
    }
}
