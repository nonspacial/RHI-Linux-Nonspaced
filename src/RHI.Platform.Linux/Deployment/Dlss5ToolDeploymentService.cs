using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>Which DLSS5 Tool build: RenoDX's stock add-on, or FoveatedDLSS5 (maximedgoulet), a build of it (#276).</summary>
public enum Dlss5ToolBuild
{
    Stock,
    Foveated
}

/// <summary>One DLSS5 Tool release: where its add-on is and the publisher's digest(s).</summary>
/// <param name="HashUrl">Foveated only: the <c>renodx-dlss5.addon64.sha256</c> published beside the add-on.</param>
public sealed record Dlss5ToolRelease(
    Dlss5ToolBuild Build, string Version, string Tag, string AssetName, string AssetUrl, string? AssetSha256, bool IsArchive, string? HashUrl);

/// <summary>What an install asks for: the build, its version pin (null = Latest) and the NR DLL version (null = Latest for the card).</summary>
public sealed record Dlss5ToolOptions(Dlss5ToolBuild Build, string? Pin, string? NrDllVersion, bool WithBridge = false);

/// <summary>
/// w14/nr-2 (#317): the DLSS5 Tool Neural Rendering method — RenoDX's <c>renodx-dlss5.addon64</c> (RankFTW/rhi-repo,
/// <c>renodx-dlss5-*</c> releases) installed as the method itself, as upstream RHI 2.9.4's "DLSS5 Tool" and "DLSS5 Tool +
/// DX11 Bridge" (<c>Renodx5AddonService</c>, <c>DetailPanelBuilder.NeuralRendering.Install</c>). #276: the Foveated switch
/// installs FoveatedDLSS5 (maximedgoulet) under the same name instead; its own release list, each download checked against
/// the <c>.sha256</c> published beside it.
/// <list type="bullet">
/// <item>The add-on goes in ReShade's add-on folder (the binary folder, or reshade.ini's AddonPath).</item>
/// <item><c>nvngx_dlssnr.dll</c> (the NR DLL list's version) beside the game when the game has none (upstream
/// <c>DeployNrDllIfAbsentAsync</c>); a DLL already there is the game's own and stays untouched and unowned.</item>
/// </list>
/// One journaled operation; the record is the commit point. Removal takes out exactly what the install placed, while it
/// still holds RHI's bytes. The version swap and Update replace only the add-on, in place; a Foveated install follows the
/// Foveated releases, so a stock update never replaces it (and the reverse).
/// </summary>
public sealed partial class Dlss5ToolDeploymentService
{
    public const string DisplayName = "DLSS5 Tool";
    public const string FoveatedDisplayName = "DLSS5 Tool (Foveated)";
    public const string StockReleasesApiUrl = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    public const string FoveatedReleasesApiUrl = "https://api.github.com/repos/maximedgoulet/FoveatedDLSS5/releases?per_page=30";
    public const string FoveatedRepositoryUrl = "https://github.com/maximedgoulet/FoveatedDLSS5";
    public const string StockTagPrefix = "renodx-dlss5-";
    public const string AddonFileName = "renodx-dlss5.addon64";
    public const string HashAssetName = AddonFileName + ".sha256";
    public const string NrDllName = "nvngx_dlssnr.dll";

    // Metadata keys of the record.
    internal const string MetaBuild = "Dlss5Tool.Build"; // "stock" or "foveated"
    internal const string MetaTag = "Dlss5Tool.Tag";
    internal const string MetaPin = "GitHub.ReleaseTagPin";
    internal const string MetaAddonSha256 = "Dlss5Tool.AddonSha256";
    /// <summary>"1" when the DX11 Bridge row installed this DLSS5 Tool with it ("DLSS5 Tool + DX11 Bridge"): the bridge's removal takes it too.</summary>
    internal const string MetaWithBridge = "Dlss5Tool.WithBridge";

    // User-facing lines (PT-BR in the UI's presentation table).
    public const string NativeLinuxMessage = "DLSS5 Tool is a Windows ReShade add-on; it does not apply to native Linux games.";
    public const string BinaryFolderMessage = "The game's executable folder is not known yet; confirm the game's executable in Edit game before installing DLSS5 Tool.";
    public const string ArchitectureMessage = "DLSS5 Tool needs a 64-bit game.";
    public const string NeedsDlssMessage = "DLSS5 Tool needs a game with native DLSS. For games without DLSS use DLSS5 Feeder or GITC Uplift.";
    public const string NeedsReShadeMessage = "DLSS5 Tool is a ReShade add-on: install ReShade (with add-on support) first.";
    public const string AlreadyInstalledMessage = "DLSS5 Tool is already installed by RHI for this game.";
    public const string NotInstalledMessage = "DLSS5 Tool is not installed by RHI for this game.";
    public const string ExternalPrefix = "A RenoDX DLSS add-on is already in this game but was not installed by RHI; RHI does not overwrite or remove it: ";
    public const string DownloadFailedPrefix = "DLSS5 Tool download failed; nothing was changed: ";
    public const string FailedRolledBackPrefix = "DLSS5 Tool failed and was rolled back: ";
    public const string ModifiedPrefix = "DLSS5 Tool not changed: a file changed since RHI installed it (left as it is): ";
    public const string InstalledPrefix = "DLSS5 Tool installed: ";
    public const string RemovedMessage = "DLSS5 Tool removed; ReShade and the game's own files were left as they were.";
    public const string NoStockReleaseMessage = "No renodx-dlss5 release with the DLSS5 Tool add-on was found.";
    public const string NoFoveatedReleaseMessage = "No FoveatedDLSS5 release with renodx-dlss5.addon64 and its .sha256 was found.";
    public const string HashMissingPrefix = "FoveatedDLSS5's .sha256 does not name a SHA-256 for ";
    public const string HashMismatchPrefix = "FoveatedDLSS5's add-on does not match the SHA-256 published with it: ";
    public const string MissingFilePrefix = "The DLSS5 Tool release does not hold ";
    public const string NotPePrefix = "Not a 64-bit Windows add-on in the DLSS5 Tool release: ";
    public const string PinUnavailablePrefix = "DLSS5 Tool version not published any more, so Latest was used: ";
    public const string NrDllPlacedNote = " NVIDIA's Neural Rendering file (nvngx_dlssnr.dll) was put beside the game.";
    public const string GameNrDllKeptNote = " The game's own nvngx_dlssnr.dll was kept.";
    public const string NoNrDllNote = " NVIDIA's Neural Rendering file could not be fetched; the add-on needs nvngx_dlssnr.dll beside the game.";
    public const string FoveatedNote = " Foveated build: turn on foveation and set the zones in the add-on's settings in the ReShade overlay (Add-ons > DLSS 5 Neural Rendering).";
    public const string AvailableNote = "DLSS5 Tool (RenoDX): Neural Rendering on the game's own DLSS. For DX12 games with native DLSS; with the DX11 Bridge for DX11 and Vulkan games. RHI installs the add-on and, when the game has none, nvngx_dlssnr.dll. For the newest DLSS use the DLSS section.";
    public const string SwitchMethodNote = " Installing it removes the Neural Rendering method RHI installed here first.";
    public const string FeederOwnsNote = "renodx-dlss5.addon64 here is DLSS5 Feeder's neural consumer (part of the Feeder install).";

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DlssManagerService? _dlss;
    private readonly DeploymentJournal? _journal;
    private readonly string _cacheRoot;

    public Dlss5ToolDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        DlssManagerService? dlss,
        string? cacheRoot = null,
        DeploymentJournal? journal = null)
    {
        _http = http;
        _records = records;
        _dlss = dlss;
        _journal = journal;
        _cacheRoot = cacheRoot ?? Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.CacheHome, "rhi-linux", "dlss5-tool");
    }

    internal DeploymentJournal? Journal => _journal;

    public LinuxDeploymentRecord? GetRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.Dlss5Tool);

    /// <summary>The build the record says is installed (stock for a record without one).</summary>
    public static Dlss5ToolBuild BuildOf(LinuxDeploymentRecord record)
        => record.Metadata.GetValueOrDefault(MetaBuild) == "foveated" ? Dlss5ToolBuild.Foveated : Dlss5ToolBuild.Stock;

    /// <summary>The installed build, or null when RHI did not install DLSS5 Tool here.</summary>
    public Dlss5ToolBuild? InstalledBuild(DetectedGame game) => GetRecord(game) is { } record ? BuildOf(record) : null;

    /// <summary>"DLSS5 Tool" or "DLSS5 Tool (Foveated)".</summary>
    public static string NameOf(Dlss5ToolBuild build) => build == Dlss5ToolBuild.Foveated ? FoveatedDisplayName : DisplayName;

    /// <summary>
    /// Why DLSS5 Tool cannot go in this game (null when it can): upstream offers it for 64-bit games with native DLSS
    /// (DX12 alone; DX11 and Vulkan with the bridge).
    /// </summary>
    public static string? IneligibleReason(DetectedGame game, bool hasNativeDlss)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return NativeLinuxMessage;
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory)) return BinaryFolderMessage;
        if (GameArchitecture.Of(game) != PeArchitecture.X64) return ArchitectureMessage;
        return hasNativeDlss ? null : NeedsDlssMessage;
    }

    // ── Releases ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The stock releases in RankFTW/rhi-repo, newest first (rc10 above rc5, a stable release above its rcs; upstream's
    /// order): <c>renodx-dlss5-&lt;version&gt;</c> tags with <c>renodx-dlss5.addon64</c> or a <c>renodx-dlss5*.zip</c>.
    /// ShortFuse's <c>renodx-dlss-SF-</c> releases share the repository and are left out.
    /// </summary>
    internal static IReadOnlyList<Dlss5ToolRelease> ParseStockReleases(string json)
    {
        var list = new List<Dlss5ToolRelease>();
        foreach (var (tag, assets) in Releases(json, NoStockReleaseMessage, includePreReleases: true))
        {
            if (!tag.StartsWith(StockTagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var version = SafeVersion(tag[StockTagPrefix.Length..]);
            if (version is null) continue;
            var direct = assets.FirstOrDefault(a => a.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase));
            var archive = assets.FirstOrDefault(a => a.Name.StartsWith("renodx-dlss5", StringComparison.OrdinalIgnoreCase)
                                                     && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            var chosen = direct ?? archive;
            if (chosen is null) continue;
            list.Add(new Dlss5ToolRelease(Dlss5ToolBuild.Stock, version, tag, chosen.Name, chosen.Url, chosen.Sha256, direct is null, null));
        }
        return Newest(list);
    }

    /// <summary>
    /// The FoveatedDLSS5 releases, newest first. Every one so far is a pre-release, so pre-releases count (drafts do not).
    /// A release without both <c>renodx-dlss5.addon64</c> and its <c>.sha256</c> is left out (fail-closed: the hash is
    /// what every download is checked against).
    /// </summary>
    internal static IReadOnlyList<Dlss5ToolRelease> ParseFoveatedReleases(string json)
    {
        var list = new List<Dlss5ToolRelease>();
        foreach (var (tag, assets) in Releases(json, NoFoveatedReleaseMessage, includePreReleases: true))
        {
            var addon = assets.FirstOrDefault(a => a.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase));
            var hash = assets.FirstOrDefault(a => a.Name.Equals(HashAssetName, StringComparison.OrdinalIgnoreCase));
            if (addon is null || hash is null) continue;
            var value = tag.Trim();
            if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
            if (SafeVersion(value) is not { } version) continue;
            list.Add(new Dlss5ToolRelease(Dlss5ToolBuild.Foveated, version, tag, addon.Name, addon.Url, addon.Sha256, false, hash.Url));
        }
        return Newest(list);
    }

    private sealed record Asset(string Name, string Url, string? Sha256);

    private static IEnumerable<(string Tag, IReadOnlyList<Asset> Assets)> Releases(string json, string emptyMessage, bool includePreReleases)
    {
        var result = new List<(string, IReadOnlyList<Asset>)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException(emptyMessage);
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (!includePreReleases && release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
                var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag) || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                var list = new List<Asset>();
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest["sha256:".Length..].Trim().ToLowerInvariant()
                        : null;
                    list.Add(new Asset(name, url, sha));
                }
                result.Add((tag, list));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(emptyMessage, ex);
        }
        return result;
    }

    // Versions become folder names: safe characters only (e.g. "10.8.0", "8.5.0-rc10", "0.4.0").
    private static string? SafeVersion(string value)
        => Regex.IsMatch(value, @"^[0-9]+(?:\.[0-9]+){0,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]{0,40})?$", RegexOptions.CultureInvariant) ? value : null;

    private static IReadOnlyList<Dlss5ToolRelease> Newest(IEnumerable<Dlss5ToolRelease> releases)
        => releases
            .Select(r => (Release: r, Key: ReleaseVersionOrdering.Key(r.Version)))
            .OrderByDescending(x => x.Key.Base)
            .ThenByDescending(x => x.Key.PreRelease)
            .Select(x => x.Release)
            .ToArray();

    private async Task<IReadOnlyList<Dlss5ToolRelease>> FetchReleasesAsync(Dlss5ToolBuild build, CancellationToken cancellationToken)
    {
        var url = build == Dlss5ToolBuild.Foveated ? FoveatedReleasesApiUrl : StockReleasesApiUrl;
        var json = await TrustedDownload.GetStringAsync(_http, url, DownloadSources.GitHubApi, cancellationToken: cancellationToken).ConfigureAwait(false);
        var releases = build == Dlss5ToolBuild.Foveated ? ParseFoveatedReleases(json) : ParseStockReleases(json);
        return releases.Count == 0
            ? throw new InvalidDataException(build == Dlss5ToolBuild.Foveated ? NoFoveatedReleaseMessage : NoStockReleaseMessage)
            : releases;
    }

    /// <summary>The version picker's list for a build (newest first; Foveated: its own releases, upstream's switched list).</summary>
    public async Task<IReadOnlyList<GitHubReleaseVersion>> ListVersionsAsync(Dlss5ToolBuild build, CancellationToken cancellationToken = default)
        => (await FetchReleasesAsync(build, cancellationToken).ConfigureAwait(false))
            .Select(r => new GitHubReleaseVersion(r.Tag, r.Version, build == Dlss5ToolBuild.Foveated)).ToList();

    /// <summary>The release for a pin (null = the newest). A pin no longer published falls back to the newest (the note says so).</summary>
    internal async Task<(Dlss5ToolRelease Release, string? Note)> ResolveAsync(Dlss5ToolBuild build, string? pin, CancellationToken cancellationToken)
    {
        var releases = await FetchReleasesAsync(build, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pin)) return (releases[0], null);
        var found = releases.FirstOrDefault(r => r.Version.Equals(pin, StringComparison.OrdinalIgnoreCase) || r.Tag.Equals(pin, StringComparison.OrdinalIgnoreCase));
        return found is not null ? (found, null) : (releases[0], PinUnavailablePrefix + pin);
    }

    /// <summary>
    /// The release's add-on in the cache (<c>&lt;cache&gt;/&lt;stock|foveated&gt;/&lt;version&gt;/renodx-dlss5.addon64</c>),
    /// downloaded and checked when needed. Stock: GitHub's digest (fail-open without one, as every RHI-repository payload).
    /// Foveated: the published <c>.sha256</c> first, then the add-on against it (and against GitHub's digest when there is
    /// one); fail-closed. Either way the add-on must be a 64-bit PE. Reused only while its download stamp matches (#142).
    /// </summary>
    internal async Task<(string Path, string Sha256, bool DigestVerified)> EnsureCachedAsync(Dlss5ToolRelease release, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_cacheRoot, release.Build == Dlss5ToolBuild.Foveated ? "foveated" : "stock", release.Version);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, AddonFileName);
        string? expected = release.AssetSha256;
        if (release.Build == Dlss5ToolBuild.Foveated)
        {
            // The .sha256 is read on every resolve: the cache entry is bound to the hash the publisher states now.
            var text = await TrustedDownload.GetStringAsync(_http, release.HashUrl!, DownloadSources.FoveatedDlss5, cancellationToken: cancellationToken).ConfigureAwait(false);
            var published = ParseSha256File(text) ?? throw new InvalidDataException(HashMissingPrefix + release.Tag);
            if (expected is not null && !FileHash.HexEquals(expected, published))
                throw new InvalidDataException(HashMismatchPrefix + release.Tag);
            expected = published;
        }
        if (CacheStamp.TryValidate(target, release.AssetUrl, release.Version, expected, out var cachedSha))
            return (target, cachedSha, expected is not null);
        CacheStamp.Invalidate(target);
        FileCleanup.TryDelete(target);

        var policy = release.Build == Dlss5ToolBuild.Foveated ? DownloadSources.FoveatedDlss5 : DownloadSources.RhiRepository;
        using var download = await TrustedDownload.DownloadToTempAsync(_http, release.AssetUrl,
            Path.Combine(directory, release.IsArchive ? Path.GetFileName(release.AssetName) : AddonFileName),
            policy, expected, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (expected is not null && !FileHash.HexEquals(download.Sha256, expected))
            throw new InvalidDataException(HashMismatchPrefix + release.Tag); // the download already refuses this; kept explicit
        if (release.IsArchive)
        {
            using var zip = ZipFile.OpenRead(download.Path);
            var entries = zip.Entries.Where(e => e.Name.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1) throw new InvalidDataException(MissingFilePrefix + AddonFileName);
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entries[0], target, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            download.MoveTo(target);
        }
        if (PeArchitectureDetector.Detect(target) != PeArchitecture.X64)
        {
            FileCleanup.TryDelete(target);
            throw new InvalidDataException(NotPePrefix + release.AssetName);
        }
        var sha = CacheStamp.Write(target, release.AssetUrl, release.Version, expected);
        if (expected is null) TrustedDownload.NoteAcceptedWithoutDigest(policy, release.AssetUrl, download.Sha256);
        return (target, sha, expected is not null);
    }

    /// <summary>The SHA-256 a <c>.sha256</c> file states (<c>&lt;hex&gt;  &lt;name&gt;</c>, or the hex alone); null when it states none.</summary>
    internal static string? ParseSha256File(string text)
    {
        var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && Regex.IsMatch(first, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant) ? first.ToLowerInvariant() : null;
    }

    // ── Install ───────────────────────────────────────────────────────────────

    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, Dlss5ToolOptions options, bool hasNativeDlss, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(AlreadyInstalledMessage);
        if (IneligibleReason(game, hasNativeDlss) is { } ineligible) return ComponentOperationResult.Fail(ineligible);
        var binaryDir = Path.GetFullPath(game.BinaryDirectory!);
        if (!GitHubReShadeAddonDeploymentService.HasReShade(binaryDir) && !VulkanReShadeInUse(game))
            return ComponentOperationResult.Fail(NeedsReShadeMessage);
        var addonFolder = AddonPathFolder.Resolve(binaryDir, game.PrefixPath);
        if (addonFolder.Refusal is not null) return ComponentOperationResult.Fail(addonFolder.Refusal);
        var addonDir = addonFolder.Folder;
        // Upstream deletes the other RenoDX DLSS add-on (mutual exclusivity); Linux never deletes a file RHI did not place:
        // one that is there without RHI's record refuses the install (a managed one was removed by the method switch).
        foreach (var name in new[] { AddonFileName, ShortFuseDeploymentService.AddonFileName, ShortFuseDeploymentService.ZzzAddonFileName })
            if (ReShadeDeploymentService.FindFileCaseInsensitive(addonDir, name) is { } existing)
                return ComponentOperationResult.Fail(ExternalPrefix + existing);

        // The network half: every download and check before the first change.
        (string Path, string Sha256, bool DigestVerified) addon;
        Dlss5ToolRelease release;
        string? pinNote;
        (string Path, string Version)? nr = null;
        var gameNrDll = ReShadeDeploymentService.FindFileCaseInsensitive(binaryDir, NrDllName) is not null;
        try
        {
            (release, pinNote) = await ResolveAsync(options.Build, options.Pin, cancellationToken).ConfigureAwait(false);
            addon = await EnsureCachedAsync(release, cancellationToken).ConfigureAwait(false);
            if (!gameNrDll && _dlss is not null)
            {
                try { nr = await _dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, options.NrDllVersion, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("dlss5-tool", "The Neural Rendering DLL could not be fetched", ex); }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
        }

        var (opened, refusal) = await DeploymentScope.BeginAsync(_journal, "DLSS5 Tool install: " + game.Name, game, ManagedComponentKind.Dlss5Tool, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var created = new List<string>();
        var note = gameNrDll ? GameNrDllKeptNote : nr is null ? NoNrDllNote : NrDllPlacedNote;
        try
        {
            var files = new List<LinuxDeployedFile>();
            await AddonPathFolder.CreateJournaledAsync(addonDir, scope.Operation, created, CancellationToken.None).ConfigureAwait(false);
            var addonTarget = Path.Combine(addonDir, AddonFileName);
            var addonSha = await scope.PlaceCopyAsync(addon.Path, addonTarget, CancellationToken.None, overwrite: false).ConfigureAwait(false);
            files.Add(new LinuxDeployedFile { Path = addonTarget, ExistedBeforeInstall = false, Sha256 = addonSha });
            if (nr is { } nrDll)
            {
                var dllTarget = Path.Combine(binaryDir, NrDllName);
                var dllSha = await scope.PlaceCopyAsync(nrDll.Path, dllTarget, CancellationToken.None, overwrite: false).ConfigureAwait(false);
                files.Add(new LinuxDeployedFile { Path = dllTarget, ExistedBeforeInstall = false, Sha256 = dllSha });
            }
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaBuild] = options.Build == Dlss5ToolBuild.Foveated ? "foveated" : "stock",
                [MetaTag] = release.Tag,
                [MetaPin] = options.Pin is null || pinNote is not null ? "" : release.Version,
                [MetaAddonSha256] = addonSha,
                [MetaWithBridge] = options.WithBridge ? "1" : "",
                [NeuralRenderingDllSwap.MetaDllVersion] = nr is { } placed ? placed.Version : ""
            };
            VerifiedDownload.Record(metadata, addonSha, addon.DigestVerified);
            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.Dlss5Tool,
                BinaryDirectory = binaryDir,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = files,
                DirectoriesCreatedByRhi = [.. created],
                Metadata = metadata
            }));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName);
            if (rollback.RolledBack) AddonPathFolder.RemoveCreated(created);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(InstalledPrefix + NameOf(options.Build) + " " + release.Version + "."
                                           + (pinNote is null ? "" : " " + pinNote + ".") + note
                                           + (options.Build == Dlss5ToolBuild.Foveated ? FoveatedNote : ""));
    }

    private bool VulkanReShadeInUse(DetectedGame game)
        => game.EffectiveGraphicsApi == GraphicsApi.Vulkan && _records.Find(game, ManagedComponentKind.ReShade) is not null;
}
