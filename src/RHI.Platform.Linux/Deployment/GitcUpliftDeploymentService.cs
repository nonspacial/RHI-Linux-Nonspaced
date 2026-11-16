using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One GITC Uplift release: its zip and the publisher's digests.</summary>
public sealed record GitcUpliftRelease(string Version, string Tag, string AssetName, string AssetUrl, string? AssetSha256, string? SumsUrl);

/// <summary>A release's files, checked and cached (<c>~/.cache/rhi-linux/gitc-uplift/&lt;version&gt;/</c>).</summary>
public sealed record GitcUpliftPayload(GitcUpliftRelease Release, string Directory, string ZipSha256, bool DigestVerified)
{
    public string File(string name) => System.IO.Path.Combine(Directory, name);
}

/// <summary>What an install asks for: the version pin (null = Latest), the NR DLL version (null = Latest for the card),
/// iMMERSE Launchpad on or off, and the NR on/off key (a Windows virtual-key code; 0 = leave the game's own).</summary>
public sealed record GitcUpliftOptions(string? Pin, string? NrDllVersion, bool Launchpad, int EnableKey);

/// <summary>
/// w14/nr (#274): GITC Uplift (ghostinthecamera/GITC-Uplift), a ReShade add-on that runs NVIDIA's DLSS Neural Rendering on
/// the picture ReShade sees, as a Neural Rendering method (upstream RHI 2.9.2–2.9.3 <c>UpliftService</c>: <c>93d0fcb</c>,
/// <c>18088a3</c>, <c>5f409de</c>, <c>7748d10</c>, <c>71af41e</c>; end state 2.9.4). Every game file goes through one
/// journaled <see cref="DeploymentScope"/> and the record is the commit point:
/// <list type="bullet">
/// <item><c>gitc-uplift.addon64</c> (or <c>.addon32</c>) and <c>gitc-uplift-helper64.exe</c> in ReShade's add-on folder (the
/// binary folder, or reshade.ini's AddonPath); the NR DLL in its <c>GITC-Uplift</c> subfolder (a copy the user put there
/// is kept and not owned); <c>Uplift.fx</c> / <c>UpliftMask.fx</c> in RHI's shader tree, added to ReShade.ini's search
/// paths.</item>
/// <item>OpenGL: the <c>Uplift</c> technique switched on first in the preset, so NR runs on ReShade's resolved copy of a
/// multisampled back buffer (upstream's OpenGL MSAA set-up). iMMERSE Launchpad (<see cref="ImmerseLaunchpadService"/>) on
/// above it when the game's choice is on (default: games without DLSS).</item>
/// <item>The NR on/off key (<c>[Uplift] EnableKey</c> in ReShade.ini) when one is set in Settings.</item>
/// </list>
/// Removal takes out exactly what the install put in: files still holding RHI's bytes, techniques RHI switched on, the key
/// RHI wrote, the search paths it added. Add-on and helper always come from one release and change together (Uplift 2.0
/// refuses a mismatched pair); never while the game runs.
/// </summary>
public sealed partial class GitcUpliftDeploymentService
{
    public const string DisplayName = "GITC Uplift";
    public const string ReleasesApiUrl = "https://api.github.com/repos/ghostinthecamera/GITC-Uplift/releases?per_page=30";
    public const string RepositoryUrl = "https://github.com/ghostinthecamera/GITC-Uplift";
    public const string GuideUrl = "https://github.com/ghostinthecamera/GITC-Uplift/wiki/Guide";
    public const string Addon64 = "gitc-uplift.addon64";
    public const string Addon32 = "gitc-uplift.addon32";
    public const string HelperExe = "gitc-uplift-helper64.exe";
    public const string DllFolder = "GITC-Uplift";
    public const string NrDllName = "nvngx_dlssnr.dll";
    public const string TechniqueToken = "Uplift@Uplift.fx";
    public const string TreeName = "GITC-Uplift";
    public const string EffectSearchPath = @".\reshade-shaders\RHI\GITC-Uplift\Shaders\**";
    public const string IniSection = "Uplift";
    public const string EnableKeyKey = "EnableKey";
    public static readonly string[] ShaderFiles = ["Uplift.fx", "UpliftMask.fx"];
    /// <summary>The release zip's entries RHI takes (by full name; every other entry is left in the zip).</summary>
    internal static readonly (string Entry, string Name)[] PayloadEntries =
    [
        ("Addons/" + Addon64, Addon64), ("Addons/" + Addon32, Addon32), ("Addons/" + HelperExe, HelperExe),
        ("Shaders/Uplift.fx", "Uplift.fx"), ("Shaders/UpliftMask.fx", "UpliftMask.fx")
    ];

    // Metadata keys of the record.
    internal const string MetaTag = "Uplift.Tag";
    internal const string MetaPin = "GitHub.ReleaseTagPin";
    internal const string MetaTechnique = "Uplift.Technique"; // "1" RHI switched it on, "user" it was on, "" not set
    internal const string MetaLaunchpad = "Uplift.Launchpad"; // "1" RHI switched it on, "user" it was on, "off"
    internal const string MetaPresetPath = "Uplift.PresetPath";
    internal const string MetaPresetCreated = "Uplift.PresetCreated";
    internal const string MetaKeyWritten = "Uplift.EnableKey";
    internal const string MetaKeyPrevious = "Uplift.EnableKeyPrevious"; // the value before, "" none (the key was missing)
    internal const string MetaKeyPreviousExists = "Uplift.EnableKeyPreviousExists";
    internal const string MetaIniPath = "Uplift.ReShadeIniPath";
    internal const string MetaIniCreated = "Uplift.ReShadeIniCreated";
    internal const string MetaIniHash = "Uplift.ReShadeIniHash";
    internal const string MetaIniEffectAdded = "Uplift.EffectPathsAdded";
    internal const string MetaIniTextureAdded = "Uplift.TexturePathsAdded";
    internal const string MetaIniEffectKeyCreated = "Uplift.EffectKeyCreated";
    internal const string MetaIniTextureKeyCreated = "Uplift.TextureKeyCreated";

    // User-facing lines (PT-BR in the UI's presentation table).
    public const string NativeLinuxMessage = "GITC Uplift is a Windows ReShade add-on; it does not apply to native Linux games.";
    public const string BinaryFolderMessage = "The game's executable folder is not known yet; confirm the game's executable in Edit game before installing GITC Uplift.";
    public const string ArchitectureMessage = "GITC Uplift needs a 32-bit or 64-bit Windows game executable.";
    public const string NeedsReShadeMessage = "GITC Uplift is a ReShade add-on: install ReShade (with add-on support) first.";
    public const string AlreadyInstalledMessage = "GITC Uplift is already installed by RHI for this game.";
    public const string NotInstalledMessage = "GITC Uplift is not installed by RHI for this game.";
    public const string ExternalPrefix = "GITC Uplift is already in this game but was not installed by RHI; RHI does not overwrite it: ";
    public const string DownloadFailedPrefix = "GITC Uplift download failed; nothing was changed: ";
    public const string FailedRolledBackPrefix = "GITC Uplift failed and was rolled back: ";
    public const string ModifiedPrefix = "GITC Uplift not changed: a file changed since RHI installed it (left as it is): ";
    public const string RunningMessage = "GITC Uplift's add-on and helper must change together, and the game is running: close the game, then try again.";
    public const string InstalledPrefix = "GITC Uplift installed: ";
    public const string InstalledHowTo = " In the game, open ReShade (Home), go to the GITC Uplift tab and tick Enable (NR starts off every time the game starts).";
    public const string RemovedMessage = "GITC Uplift removed; ReShade and the game's own files were left as they were.";
    public const string NoReleaseMessage = "No GITC Uplift release with a GITC-Uplift-*.zip was found.";
    public const string KeptPrefix = " Kept (changed since RHI put them there): ";
    public const string SumsNoEntryPrefix = "GITC Uplift's SHA256SUMS.txt has no line for: ";
    public const string SumsMismatchPrefix = "A GITC Uplift file does not match its SHA256SUMS.txt: ";
    public const string MissingFilePrefix = "The GITC Uplift release zip does not hold ";
    public const string NotPePrefix = "Not a Windows program of the expected kind in the GITC Uplift release: ";
    public const string PinUnavailablePrefix = "GITC Uplift version not published any more, so Latest was used: ";
    public const string NoNrDllNote = " NVIDIA's Neural Rendering file could not be fetched; put nvngx_dlssnr.dll in the GITC-Uplift folder yourself.";
    public const string UserNrDllKeptNote = " The nvngx_dlssnr.dll already in the GITC-Uplift folder was kept (not RHI's).";
    public const string AvailableNote = "GITC Uplift by ghostinthecamera: Neural Rendering on almost any game on an RTX card (DirectX 9–12, Vulkan, OpenGL; 32 and 64-bit), for single-player games. RHI installs the add-on, its 64-bit helper, the NR DLL (in its GITC-Uplift folder) and its effects; in the game open ReShade (Home), GITC Uplift tab, tick Enable.";
    public const string SwitchMethodNote = " Installing it removes the Neural Rendering method RHI installed here first.";
    public const string PresetUnmappedNote = " The game's ReShade preset is named by a Windows drive path, so its techniques were not changed.";

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DlssManagerService? _dlss;
    private readonly ImmerseLaunchpadService _launchpad;
    private readonly DeploymentJournal? _journal;
    private readonly IGameRunningProbe _running;
    private readonly string _cacheRoot;

    public GitcUpliftDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        DlssManagerService? dlss,
        ImmerseLaunchpadService launchpad,
        string? cacheRoot = null,
        DeploymentJournal? journal = null,
        IGameRunningProbe? running = null)
    {
        _http = http;
        _records = records;
        _dlss = dlss;
        _launchpad = launchpad;
        _journal = journal;
        _running = running ?? new LinuxGameRunningProbe();
        _cacheRoot = cacheRoot ?? Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.CacheHome, "rhi-linux", "gitc-uplift");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.GitcUplift);

    public ImmerseLaunchpadService Launchpad => _launchpad;

    /// <summary>iMMERSE Launchpad's default for a game (upstream: on in games without DLSS, which have no motion vectors).</summary>
    public static bool LaunchpadDefault(bool gameHasDlss) => !gameHasDlss;

    /// <summary>Why GITC Uplift cannot go in this game (null when it can). Uplift supports DX9–12, Vulkan and OpenGL, 32 and 64-bit.</summary>
    public static string? IneligibleReason(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return NativeLinuxMessage;
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory)) return BinaryFolderMessage;
        return GameArchitecture.Of(game) is PeArchitecture.X64 or PeArchitecture.X86 ? null : ArchitectureMessage;
    }

    // ── Releases ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The published releases, newest first: not drafts or pre-releases (upstream lists only full releases), each with its
    /// <c>GITC-Uplift-*.zip</c>. A release with neither GitHub's digest nor a SHA256SUMS.txt is left out (fail-closed: the
    /// publisher ships both).
    /// </summary>
    internal static IReadOnlyList<GitcUpliftRelease> ParseReleases(string json)
    {
        var list = new List<GitcUpliftRelease>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException(NoReleaseMessage);
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
                var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag) || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                string? name = null, url = null, sha = null, sums = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var n = asset.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                    var u = asset.TryGetProperty("browser_download_url", out var uu) ? uu.GetString() : null;
                    if (string.IsNullOrWhiteSpace(n) || string.IsNullOrWhiteSpace(u)) continue;
                    if (n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) { sums = u; continue; }
                    if (name is not null || !n.StartsWith("GITC-Uplift", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    name = n;
                    url = u;
                    var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest["sha256:".Length..].Trim().ToLowerInvariant();
                }
                if (name is null || (sha is null && sums is null)) continue;
                if (NormalizeVersion(tag) is not { } version) continue;
                list.Add(new GitcUpliftRelease(version, tag, name, url!, sha, sums));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(NoReleaseMessage, ex);
        }
        return list;
    }

    /// <summary>The tag as a version (leading v dropped), or null when it is not one. Versions become folder names: safe characters only.</summary>
    internal static string? NormalizeVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return Regex.IsMatch(value, @"^[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]{0,40})?$", RegexOptions.CultureInvariant) ? value : null;
    }

    private async Task<IReadOnlyList<GitcUpliftRelease>> FetchReleasesAsync(CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, ReleasesApiUrl, DownloadSources.GitHubApi, cancellationToken: cancellationToken).ConfigureAwait(false);
        var releases = ParseReleases(json);
        return releases.Count == 0 ? throw new InvalidDataException(NoReleaseMessage) : releases;
    }

    /// <summary>The version picker's list (newest first).</summary>
    public async Task<IReadOnlyList<GitHubReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
        => (await FetchReleasesAsync(cancellationToken).ConfigureAwait(false)).Select(r => new GitHubReleaseVersion(r.Tag, r.Version, false)).ToList();

    /// <summary>The release for a pin (null = the newest). A pin no longer published falls back to the newest, as upstream (the note says so).</summary>
    internal async Task<(GitcUpliftRelease Release, string? Note)> ResolveAsync(string? pin, CancellationToken cancellationToken)
    {
        var releases = await FetchReleasesAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pin)) return (releases[0], null);
        var wanted = NormalizeVersion(pin) ?? pin;
        var found = releases.FirstOrDefault(r => r.Version.Equals(wanted, StringComparison.OrdinalIgnoreCase) || r.Tag.Equals(pin, StringComparison.OrdinalIgnoreCase));
        return found is not null ? (found, null) : (releases[0], PinUnavailablePrefix + pin);
    }

    /// <summary>
    /// The release's files in the cache, downloaded and checked when needed: the zip against GitHub's digest and the
    /// release's SHA256SUMS.txt; each file taken out against its own SHA256SUMS line; the add-ons and helper must be Windows
    /// programs of their bitness. Cached files are reused only while their download stamp matches (#142).
    /// </summary>
    internal async Task<GitcUpliftPayload> EnsureCachedAsync(GitcUpliftRelease release, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_cacheRoot, release.Version);
        Directory.CreateDirectory(directory);
        var stampOf = Path.Combine(directory, "release.zip.sha256");
        if (PayloadEntries.All(p => CacheStamp.IsValid(Path.Combine(directory, p.Name), release.AssetUrl, release.Version, release.AssetSha256)) && File.Exists(stampOf))
            return new GitcUpliftPayload(release, directory, File.ReadAllText(stampOf).Trim(), true);
        foreach (var (_, name) in PayloadEntries)
        {
            CacheStamp.Invalidate(Path.Combine(directory, name));
            FileCleanup.TryDelete(Path.Combine(directory, name));
        }

        using var download = await TrustedDownload.DownloadToTempAsync(_http, release.AssetUrl, Path.Combine(directory, release.AssetName),
            DownloadSources.GitcUplift, release.AssetSha256, cancellationToken: cancellationToken).ConfigureAwait(false);
        string? sums = null;
        if (!string.IsNullOrWhiteSpace(release.SumsUrl))
        {
            sums = await TrustedDownload.GetStringAsync(_http, release.SumsUrl, DownloadSources.GitcUplift, cancellationToken: cancellationToken).ConfigureAwait(false);
            var expected = Rtx40MfgStandaloneDeploymentService.TryFindChecksum(sums, release.AssetName)
                           ?? throw new InvalidDataException(SumsNoEntryPrefix + release.AssetName);
            if (!FileHash.HexEquals(download.Sha256, expected)) throw new InvalidDataException(SumsMismatchPrefix + release.AssetName);
        }

        using (var zip = ZipFile.OpenRead(download.Path))
        {
            foreach (var (entryName, name) in PayloadEntries)
            {
                var entry = zip.Entries.SingleOrDefault(e => e.FullName.Replace('\\', '/').Equals(entryName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException(MissingFilePrefix + entryName);
                var target = Path.Combine(directory, name);
                await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, target, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
                if (sums is not null)
                {
                    var expected = Rtx40MfgStandaloneDeploymentService.TryFindChecksum(sums, entryName)
                                   ?? throw new InvalidDataException(SumsNoEntryPrefix + entryName);
                    if (!FileHash.HexEquals(FileHash.Sha256Hex(target), expected)) throw new InvalidDataException(SumsMismatchPrefix + entryName);
                }
                var expectedArchitecture = name == Addon32 ? PeArchitecture.X86 : name.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) ? (PeArchitecture?)null : PeArchitecture.X64;
                if (expectedArchitecture is { } architecture && PeArchitectureDetector.Detect(target) != architecture)
                    throw new InvalidDataException(NotPePrefix + entryName);
            }
        }
        foreach (var (_, name) in PayloadEntries)
            CacheStamp.Write(Path.Combine(directory, name), release.AssetUrl, release.Version, release.AssetSha256);
        AtomicFile.WriteAllText(stampOf, download.Sha256);
        return new GitcUpliftPayload(release, directory, download.Sha256, release.AssetSha256 is not null || release.SumsUrl is not null);
    }

    // ── Install ───────────────────────────────────────────────────────────────

    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, GitcUpliftOptions options, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(AlreadyInstalledMessage);
        if (IneligibleReason(game) is { } ineligible) return ComponentOperationResult.Fail(ineligible);
        var binaryDir = Path.GetFullPath(game.BinaryDirectory!);
        if (!GitHubReShadeAddonDeploymentService.HasReShade(binaryDir) && !VulkanReShadeInUse(game))
            return ComponentOperationResult.Fail(NeedsReShadeMessage);
        var addonFolder = AddonPathFolder.Resolve(binaryDir, game.PrefixPath);
        if (addonFolder.Refusal is not null) return ComponentOperationResult.Fail(addonFolder.Refusal);
        var addonDir = addonFolder.Folder;
        var x86 = GameArchitecture.Of(game) == PeArchitecture.X86;
        var addonName = x86 ? Addon32 : Addon64;
        foreach (var target in new[] { Path.Combine(addonDir, addonName), Path.Combine(addonDir, HelperExe) }
                     .Concat(ShaderFiles.Select(f => Path.Combine(TreeRoot(binaryDir), "Shaders", f))))
            if (ReShadeDeploymentService.FindFileCaseInsensitive(Path.GetDirectoryName(target)!, Path.GetFileName(target)) is { } existing)
                return ComponentOperationResult.Fail(ExternalPrefix + existing);

        // The network half: every download and check before the first change.
        GitcUpliftPayload payload;
        string? pinNote;
        (string Path, string Version)? nr = null;
        LaunchpadStage? launchpad = null;
        var stageRoot = Path.Combine(_cacheRoot, $"stage-{Guid.NewGuid():N}");
        try
        {
            GitcUpliftRelease release;
            (release, pinNote) = await ResolveAsync(options.Pin, cancellationToken).ConfigureAwait(false);
            payload = await EnsureCachedAsync(release, cancellationToken).ConfigureAwait(false);
            if (_dlss is not null)
            {
                try { nr = await _dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, options.NrDllVersion, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("gitc-uplift", "The Neural Rendering DLL could not be fetched", ex); }
            }
            if (options.Launchpad) launchpad = await _launchpad.StageAsync(stageRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            DeleteStage(stageRoot);
            return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
        }

        var (opened, refusal) = await DeploymentScope.BeginAsync(_journal, "GITC Uplift install: " + game.Name, game, ManagedComponentKind.GitcUplift, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            DeleteStage(stageRoot);
            return ComponentOperationResult.Fail(refusal);
        }
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var created = new List<string>();
        var notes = new StringBuilder();
        // The highest folder each tree adds (reshade-shaders itself on a game without one): removal prunes those when empty.
        var foldersAdded = new[] { Path.Combine(addonDir, DllFolder), TreeRoot(binaryDir), ImmerseLaunchpadService.TreeRoot(binaryDir) }
            .Select(folder => FirstMissingFolder(folder, binaryDir)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        try
        {
            var files = new List<LinuxDeployedFile>();
            async Task Place(string source, string target)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var sha = await scope.PlaceCopyAsync(source, target, CancellationToken.None, overwrite: false).ConfigureAwait(false);
                Dlss5FeederFullDeploymentService.NormalizeReadableFile(target);
                files.Add(new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = sha });
            }

            await AddonPathFolder.CreateJournaledAsync(addonDir, scope.Operation, created, CancellationToken.None).ConfigureAwait(false);
            await Place(payload.File(addonName), Path.Combine(addonDir, addonName)).ConfigureAwait(false);
            await Place(payload.File(HelperExe), Path.Combine(addonDir, HelperExe)).ConfigureAwait(false);
            var dllTarget = Path.Combine(addonDir, DllFolder, NrDllName);
            var userDll = ReShadeDeploymentService.FindFileCaseInsensitive(Path.Combine(addonDir, DllFolder), NrDllName) is not null;
            if (nr is { } nrDll && !userDll) await Place(nrDll.Path, dllTarget).ConfigureAwait(false);
            else if (userDll) notes.Append(UserNrDllKeptNote);
            else notes.Append(NoNrDllNote);
            foreach (var shader in ShaderFiles)
                await Place(payload.File(shader), Path.Combine(TreeRoot(binaryDir), "Shaders", shader)).ConfigureAwait(false);
            if (launchpad is not null)
                files.AddRange(await ImmerseLaunchpadService.PlaceAsync(scope, launchpad, binaryDir, _records, game, ManagedComponentKind.GitcUplift).ConfigureAwait(false));

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaTag] = payload.Release.Tag,
                [MetaPin] = options.Pin is null || pinNote is not null ? "" : payload.Release.Version,
                [NeuralRenderingDllSwap.MetaDllVersion] = nr is { } placed && !userDll ? placed.Version : ""
            };
            VerifiedDownload.Record(metadata, payload.ZipSha256, payload.DigestVerified);
            if (launchpad is not null) VerifiedDownload.Record(metadata, launchpad.ArchiveSha256, false, ImmerseLaunchpadService.DownloadPart);

            // ReShade.ini: search paths and the NR on/off key, tracked before and declared after (a rollback puts it back).
            var iniPath = ReShadeIniManager.ResolveIniPath(binaryDir);
            var iniTarget = ReShadeIniManager.ResolveWriteTarget(iniPath, binaryDir, game.InstallPath);
            await scope.TrackAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
            var iniResult = ReShadeIniManager.Apply(binaryDir,
                launchpad is null ? [EffectSearchPath] : [EffectSearchPath, ImmerseLaunchpadService.EffectSearchPath],
                launchpad is null ? [] : [ImmerseLaunchpadService.TextureSearchPath], gameRoot: game.InstallPath);
            if (options.EnableKey is > 0 and <= 255) WriteKeyInto(iniResult.Path, binaryDir, game.InstallPath, options.EnableKey, metadata, firstWrite: true);
            await scope.DeclareWrittenContentAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
            RecordIni(metadata, iniResult);

            // The preset: Uplift's technique for OpenGL (MSAA), iMMERSE Launchpad above it when on.
            var opengl = game.EffectiveGraphicsApi == GraphicsApi.OpenGL;
            metadata[MetaLaunchpad] = launchpad is null ? "off" : "";
            if (opengl || launchpad is not null)
            {
                if (await EditPresetAsync(scope, game, binaryDir, metadata, files, (text, m) =>
                    {
                        var (withUplift, upliftOn, _) = ReShadePresetText.AddTechnique(text, TechniqueToken);
                        m[MetaTechnique] = upliftOn ? "user" : "1";
                        if (launchpad is null) return withUplift;
                        var (withLaunchpad, launchpadOn, _) = ImmerseLaunchpadService.Enable(withUplift, TechniqueToken);
                        m[MetaLaunchpad] = launchpadOn ? "user" : "1";
                        return ImmerseLaunchpadService.KeepFirst(withLaunchpad).Text;
                    }).ConfigureAwait(false) is { } presetNote)
                    notes.Append(presetNote);
            }

            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.GitcUplift,
                BinaryDirectory = binaryDir,
                SourceUrl = payload.Release.AssetUrl,
                Version = payload.Release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = files,
                DirectoriesCreatedByRhi = [.. foldersAdded, .. created],
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
        finally
        {
            DeleteStage(stageRoot);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(InstalledPrefix + payload.Release.Version + "." + (pinNote is null ? "" : " " + pinNote + ".") + notes + InstalledHowTo);
    }

    /// <summary>The game's RHI shader tree for Uplift's effects.</summary>
    public static string TreeRoot(string binaryDirectory) => Path.Combine(binaryDirectory, "reshade-shaders", "RHI", TreeName);

    /// <summary>The highest folder from <paramref name="folder"/> up to (not including) <paramref name="stopAt"/> that does not exist yet; null when it exists.</summary>
    internal static string? FirstMissingFolder(string folder, string stopAt)
    {
        string? missing = null;
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
             !string.IsNullOrEmpty(current) && !string.Equals(current, stop, StringComparison.Ordinal) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current)!)
            missing = current;
        return missing;
    }

    private bool VulkanReShadeInUse(DetectedGame game)
        => game.EffectiveGraphicsApi == GraphicsApi.Vulkan && _records.Find(game, ManagedComponentKind.ReShade) is not null;

    private static void RecordIni(Dictionary<string, string> metadata, ReShadeIniApplyResult iniResult)
    {
        metadata[MetaIniPath] = iniResult.Path;
        metadata[MetaIniCreated] = iniResult.CreatedByRhi ? "1" : "0";
        metadata[MetaIniEffectKeyCreated] = iniResult.EffectKeyCreated ? "1" : "0";
        metadata[MetaIniTextureKeyCreated] = iniResult.TextureKeyCreated ? "1" : "0";
        metadata[MetaIniEffectAdded] = JsonSerializer.Serialize(iniResult.AddedEffectPaths);
        metadata[MetaIniTextureAdded] = JsonSerializer.Serialize(iniResult.AddedTexturePaths);
        metadata[MetaIniHash] = File.Exists(iniResult.Path) ? FileHash.Sha256Hex(iniResult.Path) : "";
    }

    /// <summary>
    /// Edits the game's preset through <paramref name="scope"/> (tracked, written under ReShade.ini's link rule, declared): a
    /// preset that is not there yet is created and becomes one of the record's files. Null when done, else the line to add
    /// to the result (the preset is named by a Windows drive path and was left alone).
    /// </summary>
    private static async Task<string?> EditPresetAsync(
        DeploymentScope scope, DetectedGame game, string binaryDir, Dictionary<string, string> metadata, List<LinuxDeployedFile>? files,
        Func<string, Dictionary<string, string>, string> edit)
    {
        var preset = metadata.TryGetValue(MetaPresetPath, out var known) && !string.IsNullOrWhiteSpace(known) ? known : ReShadePresetFile.Resolve(binaryDir);
        if (preset is null) return PresetUnmappedNote;
        metadata[MetaPresetPath] = preset;
        var existing = ReShadePresetFile.Read(preset);
        var text = edit(existing ?? "", metadata);
        if (existing is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preset)!);
            var sha = await scope.PlaceBytesAsync(preset, new UTF8Encoding(false).GetBytes(text), CancellationToken.None).ConfigureAwait(false);
            metadata[MetaPresetCreated] = "1";
            files?.Add(new LinuxDeployedFile { Path = preset, ExistedBeforeInstall = false, Sha256 = sha, AllowUserChanges = true });
            return null;
        }
        if (string.Equals(text, existing, StringComparison.Ordinal)) return null;
        var target = ReShadePresetFile.WriteTarget(preset, binaryDir, game.InstallPath);
        await scope.TrackAsync(target, CancellationToken.None).ConfigureAwait(false);
        ReShadePresetFile.Write(preset, text, binaryDir, game.InstallPath);
        await scope.DeclareWrittenContentAsync(target, CancellationToken.None).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Writes <c>[Uplift] EnableKey</c> into ReShade.ini (decimal virtual-key code, as Uplift reads it). The first write
    /// RHI makes remembers the value that was there, so removal can put it back.
    /// </summary>
    private static void WriteKeyInto(string iniPath, string binaryDir, string? gameRoot, int vk, Dictionary<string, string> metadata, bool firstWrite)
    {
        var text = File.Exists(iniPath) ? File.ReadAllText(iniPath) : "";
        if (firstWrite || !metadata.ContainsKey(MetaKeyPreviousExists))
        {
            var previous = ReShadePresetText.GetKey(text, IniSection, EnableKeyKey);
            metadata[MetaKeyPreviousExists] = previous is null ? "0" : "1";
            metadata[MetaKeyPrevious] = previous ?? "";
        }
        var value = vk.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ReShadePresetFile.Write(iniPath, ReShadePresetText.SetKey(text, IniSection, EnableKeyKey, value), binaryDir, gameRoot);
        metadata[MetaKeyWritten] = value;
    }

    /// <summary>Removes a staging folder in RHI's cache (its own temporary files); a failure is logged.</summary>
    private static void DeleteStage(string stageRoot)
    {
        try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("gitc-uplift", $"Staging folder could not be removed: {stageRoot}", ex);
        }
    }

    private static IReadOnlyList<string> ReadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
