using System.IO.Compression;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What HSF's row and ⚙ show for a game (#277).</summary>
public sealed record HsfState(bool Installed, string? Version, bool LaunchpadOn, HsfRoute Route, bool RouteSwitchedOff, bool RouteOptedOut, string? IniPath);

/// <summary>
/// #277 (upstream 2.9.3/2.9.4 <c>HsfService</c>, HSF 0.9.8): HSF (Hunter's Sloppy Frames, Capt-Hunter/HSF), a ReShade add-on
/// that adds DLSS frame generation to DirectX 9/11/12 and Vulkan games. One install places the add-on for the game's
/// bitness, HSF's <c>HSF_streamline/</c> folder (Streamline, frame generation files, the 64-bit helper, the licences),
/// <c>HSF.ini</c> beside the add-on (ReShade's AddonPath honoured), <c>HSF.fx</c> in the game's <c>reshade-shaders</c> and,
/// unless switched off, iMMERSE Launchpad for optical flow (<see cref="ILaunchpadStep"/>). DirectX 12 games get HSF's D3D12
/// route in HSF.ini before the first launch (it follows the Rendering API override). An update brings the release's clean
/// HSF.ini with the player's settings carried over. Every change is journaled on HSF's record; files RHI did not place are
/// never overwritten, and only RHI's files (and HSF's own logs) are removed.
/// </summary>
public sealed class HsfDeploymentService
{
    public const string DisplayName = "HSF";
    public const string MetaVersion = "HSF.Version";
    public const string MetaLaunchpad = "HSF.Launchpad";
    public const string MetaRouteOptOut = "HSF.RouteOptOut";
    public const string MetaLaunchpadFiles = "HSF.LaunchpadFiles";
    private const long FileMaxBytes = 64L * 1024 * 1024;

    // Fixed texts (values follow), each with a PT-BR presentation pair.
    public const string AlreadyInstalledMessage = "HSF is already installed for this game.";
    public const string NotInstalledMessage = "HSF is not installed by RHI for this game.";
    public const string NativeLinuxMessage = "HSF is a ReShade add-on for Windows games; it does not apply to a native Linux game.";
    public const string ArchitectureMessage = "Could not tell whether the game is 32-bit or 64-bit, so HSF was not installed.";
    public const string VulkanNeeds64Message = "HSF supports Vulkan in 64-bit games only.";
    public const string NeedsReShadeMessage = "HSF is a ReShade add-on: install ReShade with RHI on this game first.";
    public const string ManualInstallPrefix = "HSF is already in this game folder, but RHI did not install it; nothing was changed. File: ";
    public const string ForeignFilePrefix = "HSF not installed: a file RHI did not write is in the way: ";
    public const string DownloadFailedPrefix = "HSF download failed; nothing was changed: ";
    public const string LayoutUnknownMessage = "This HSF release has a layout RHI does not know yet; nothing was changed.";
    public const string InstalledPrefix = "HSF installed: ";
    public const string RouteSetNote = " DirectX 12 mode is set up in HSF.ini for the first launch.";
    public const string IniKeptNote = " An HSF.ini RHI did not write was already there and was kept as it is.";
    public const string FailedRolledBackPrefix = "HSF failed and was rolled back: ";
    public const string ChangedPrefix = "HSF was left as it is: its files changed since RHI installed them: ";
    public const string RemovedMessage = "HSF removed.";
    public const string KeptFilesPrefix = " Left in place (not RHI's): ";
    public const string CouldNotCheckPrefix = "HSF: the latest release could not be checked: ";
    public const string UpToDatePrefix = "HSF is up to date: ";
    public const string UpdateAvailablePrefix = "HSF update available: ";
    public const string UpdatedPrefix = "HSF updated, with your HSF settings carried over: ";
    public const string LaunchpadOnMessage = "iMMERSE Launchpad is on for HSF in this game.";
    public const string LaunchpadOffMessage = "iMMERSE Launchpad is off for HSF in this game (LumeniteFX or the game's own motion).";
    public const string RouteSavedPrefix = "HSF's DirectX 12 mode is set in HSF.ini: ";
    public const string NoIniMessage = "HSF.ini is not there yet, so nothing was changed.";
    public const string VulkanDxgiWarningPrefix =
        "HSF uses ReShade's Vulkan layer in this game, but a DirectX copy of ReShade (dxgi.dll) is in the game folder and keeps the Vulkan layer out, so HSF will not load. File: ";
    public const string AntiCheatWarning =
        "HSF hooks the game's graphics: do not use it in online games with anti-cheat, which may ban you for it.";

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DeploymentJournal? _journal;
    private readonly ILaunchpadStep _launchpad;
    private readonly string _cacheDir;

    public HsfDeploymentService(HttpClient http, LinuxDeploymentRecordStore records, DeploymentJournal? journal, ILaunchpadStep launchpad, string? cacheDir = null)
    {
        _http = http;
        _records = records;
        _journal = journal;
        _launchpad = launchpad;
        _cacheDir = cacheDir ?? Path.Combine(Paths.LinuxPaths.CacheHome, "rhi-linux", "hsf");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.Hsf);

    /// <summary>Why HSF cannot be installed on <paramref name="game"/> at all, or null.</summary>
    public string? EligibilityRefusal(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return NativeLinuxMessage;
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory)) return ArchitectureMessage;
        var architecture = GameArchitecture.Of(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64)) return ArchitectureMessage;
        if (IsVulkan(game) && architecture == PeArchitecture.X86) return VulkanNeeds64Message;
        if (_records.Find(game, ManagedComponentKind.ReShade) is null) return NeedsReShadeMessage;
        return null;
    }

    /// <summary>
    /// Vulkan games use ReShade's Vulkan layer; a DirectX ReShade (dxgi.dll) in the game folder keeps the layer out and
    /// HSF with it (upstream's warning). The warning text, or null.
    /// </summary>
    public static string? VulkanDxgiWarning(DetectedGame game)
    {
        if (!IsVulkan(game) || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return null;
        return ReShadeDeploymentService.FindFileCaseInsensitive(game.BinaryDirectory, "dxgi.dll") is { } dxgi && BinarySignatureInspector.LooksLikeReShade(dxgi)
            ? VulkanDxgiWarningPrefix + dxgi
            : null;
    }

    private static bool IsVulkan(DetectedGame game) => game.EffectiveGraphicsApi == GraphicsApi.Vulkan;

    public HsfState Read(DetectedGame game)
    {
        var record = GetRecord(game);
        if (record is null || string.IsNullOrWhiteSpace(game.BinaryDirectory)) return new HsfState(false, null, true, HsfRoute.None, false, false, null);
        var ini = IniPath(game);
        var (route, off) = ini is not null && File.Exists(ini) ? HsfPackage.ReadRoute(IniLines.Read(ini)) : (HsfRoute.None, false);
        return new HsfState(true, record.Metadata.GetValueOrDefault(MetaVersion) ?? record.Version,
            record.Metadata.GetValueOrDefault(MetaLaunchpad) != "off", route, off, record.Metadata.ContainsKey(MetaRouteOptOut), ini);
    }

    private static string? IniPath(DetectedGame game)
        => string.IsNullOrWhiteSpace(game.BinaryDirectory) ? null : Path.Combine(AddonPathFolder.Resolve(game.BinaryDirectory, game.PrefixPath).Folder, HsfPackage.IniName);

    // ── install ───────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, bool launchpadOn = true, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(AlreadyInstalledMessage);
        if (EligibilityRefusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        var binaryDir = game.BinaryDirectory!;
        var is32 = GameArchitecture.Of(game) == PeArchitecture.X86;
        var folder = AddonPathFolder.Resolve(binaryDir, game.PrefixPath);
        if (folder.Refusal is not null) return ComponentOperationResult.Fail(folder.Refusal);
        var addonDir = folder.Folder;
        foreach (var addon in new[] { HsfPackage.Addon64, HsfPackage.Addon32 })
            if (ReShadeDeploymentService.FindFileCaseInsensitive(addonDir, addon) is { } manual) return ComponentOperationResult.Fail(ManualInstallPrefix + manual);
        var shaderTarget = Path.Combine(binaryDir, "reshade-shaders", "Shaders", HsfPackage.ShaderFx);
        foreach (var inTheWay in new[] { Path.Combine(addonDir, HsfPackage.StreamlineFolder), shaderTarget })
            if (File.Exists(inTheWay) || Directory.Exists(inTheWay)) return ComponentOperationResult.Fail(ForeignFilePrefix + inTheWay);

        // Everything from the network first: nothing in the game changes before the package is staged and checked.
        Staged staged;
        IReadOnlyList<LaunchpadFile> launchpadFiles = [];
        try
        {
            staged = await StageAsync(cancellationToken).ConfigureAwait(false);
            if (launchpadOn) launchpadFiles = await _launchpad.StageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsDownloadFailure(ex))
        {
            AppLog.Warn("hsf", "The HSF download failed", ex);
            return new ComponentOperationResult(false, DownloadFailedPrefix + ex.Message, SourceUnavailable: ex is HttpRequestException or TaskCanceledException);
        }

        var iniPath = Path.Combine(addonDir, HsfPackage.IniName);
        var iniIsUsers = File.Exists(iniPath);
        var route = HsfPackage.PickRoute(game.EffectiveGraphicsApi == GraphicsApi.DirectX12, is32, HasGameStreamline(game, addonDir), null);
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(_journal, "HSF install: " + game.Name, game, ManagedComponentKind.Hsf, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var created = new List<string>();
        var routeSet = false;
        try
        {
            var files = new List<LinuxDeployedFile>();
            foreach (var (relative, source) in PackageFiles(staged, is32))
                files.Add(await PlaceAsync(scope, source, Path.Combine(addonDir, relative.Replace('/', Path.DirectorySeparatorChar)), created).ConfigureAwait(false));
            files.Add(await PlaceAsync(scope, Path.Combine(staged.Folder, HsfPackage.ShaderFx), shaderTarget, created).ConfigureAwait(false));
            var placedLaunchpad = await PlaceLaunchpadAsync(scope, binaryDir, launchpadFiles, files, created).ConfigureAwait(false);
            if (!iniIsUsers)
            {
                var lines = IniLines.FromBytes(await File.ReadAllBytesAsync(Path.Combine(staged.Folder, HsfPackage.IniName), CancellationToken.None).ConfigureAwait(false));
                routeSet = HsfPackage.PresetRoute(lines, route);
                var sha = await scope.PlaceBytesAsync(iniPath, System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"), CancellationToken.None).ConfigureAwait(false);
                // HSF writes its settings into it: removed with HSF whatever it says then (as upstream).
                files.Add(new LinuxDeployedFile { Path = iniPath, Sha256 = sha, ExistedBeforeInstall = false, AllowUserChanges = true });
            }
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaVersion] = staged.Release.Version,
                [MetaLaunchpad] = launchpadOn ? "on" : "off",
                [MetaLaunchpadFiles] = string.Join("\n", placedLaunchpad),
            };
            VerifiedDownload.Record(metadata, staged.ArchiveSha256, staged.DigestVerified);
            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.Hsf,
                BinaryDirectory = binaryDir,
                SourceUrl = staged.Release.Url,
                Version = staged.Release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = files,
                Metadata = metadata
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName);
            if (rollback.RolledBack) AddonPathFolder.RemoveCreated(created);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        finally
        {
            DeleteStaging(staged.Folder);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(InstalledPrefix + staged.Release.Version + "." + (routeSet ? RouteSetNote : "") + (iniIsUsers ? IniKeptNote : "")
                                           + (VulkanDxgiWarning(game) is { } warning ? " " + warning : ""), iniPath);
    }

    // ── update ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return new ManagedUpdateCheck(false, false, NotInstalledMessage);
        HsfRelease latest;
        try
        {
            latest = await LatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsDownloadFailure(ex))
        {
            return new ManagedUpdateCheck(true, false, CouldNotCheckPrefix + ex.Message, record.Version, CouldNotCheck: true);
        }
        var update = !string.Equals(latest.Version, record.Version, StringComparison.OrdinalIgnoreCase);
        return new ManagedUpdateCheck(true, update, (update ? UpdateAvailablePrefix + latest.Version : UpToDatePrefix + record.Version), record.Version, latest.Version);
    }

    /// <summary>
    /// Brings RHI's HSF to the newest release: every file RHI placed is replaced (one that changed since refuses the update
    /// before anything changes), files the new release no longer has go, and HSF.ini becomes the release's clean one with
    /// the player's settings carried over (HSF's guide), when RHI wrote it.
    /// </summary>
    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (Changed(record) is { Count: > 0 } changed) return ComponentOperationResult.Fail(ChangedPrefix + string.Join(", ", changed));
        var binaryDir = record.BinaryDirectory;
        var addonFile = record.Files.FirstOrDefault(f => Path.GetFileName(f.Path) is HsfPackage.Addon64 or HsfPackage.Addon32);
        if (addonFile is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        var addonDir = Path.GetDirectoryName(addonFile.Path)!;
        var is32 = Path.GetFileName(addonFile.Path) == HsfPackage.Addon32;
        Staged staged;
        try
        {
            staged = await StageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsDownloadFailure(ex))
        {
            return new ComponentOperationResult(false, DownloadFailedPrefix + ex.Message, SourceUnavailable: ex is HttpRequestException or TaskCanceledException);
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(_journal, "HSF update: " + game.Name, game, ManagedComponentKind.Hsf, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var created = new List<string>();
        var updated = LinuxDeploymentRecordStore.Clone(record);
        try
        {
            var iniPath = Path.Combine(addonDir, HsfPackage.IniName);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var files = new List<LinuxDeployedFile>();
            var targets = PackageFiles(staged, is32).Select(p => (p.Source, Target: Path.Combine(addonDir, p.Relative.Replace('/', Path.DirectorySeparatorChar))))
                .Append((Path.Combine(staged.Folder, HsfPackage.ShaderFx), Path.Combine(binaryDir, "reshade-shaders", "Shaders", HsfPackage.ShaderFx)));
            foreach (var (source, target) in targets)
            {
                var ours = record.Files.Any(f => f.Path == target);
                if (!ours && (File.Exists(target) || Directory.Exists(target))) throw new IOException(ForeignFilePrefix + target);
                await AddonPathFolder.CreateJournaledAsync(Path.GetDirectoryName(target)!, scope.Operation, created, CancellationToken.None).ConfigureAwait(false);
                var sha = await scope.PlaceCopyAsync(source, target, CancellationToken.None).ConfigureAwait(false);
                files.Add(new LinuxDeployedFile { Path = target, Sha256 = sha, ExistedBeforeInstall = false });
                keep.Add(target);
            }
            // Launchpad's files stay as they are; HSF.ini becomes the clean one with the settings kept.
            foreach (var file in record.Files.Where(f => !keep.Contains(f.Path)))
            {
                if (file.Path == iniPath)
                {
                    var merged = HsfPackage.MergeIni(await File.ReadAllTextAsync(Path.Combine(staged.Folder, HsfPackage.IniName), CancellationToken.None).ConfigureAwait(false),
                        File.Exists(iniPath) ? await File.ReadAllTextAsync(iniPath, CancellationToken.None).ConfigureAwait(false) : null);
                    var sha = await scope.PlaceBytesAsync(iniPath, System.Text.Encoding.UTF8.GetBytes(merged), CancellationToken.None).ConfigureAwait(false);
                    files.Add(new LinuxDeployedFile { Path = iniPath, Sha256 = sha, AllowUserChanges = true });
                }
                else if (IsLaunchpadFile(record, file.Path)) files.Add(file);
                else await scope.DeleteAsync(file.Path, CancellationToken.None).ConfigureAwait(false); // the new release no longer has it
            }
            updated.Files = files;
            updated.Version = staged.Release.Version;
            updated.SourceUrl = staged.Release.Url;
            updated.Metadata[MetaVersion] = staged.Release.Version;
            VerifiedDownload.Record(updated.Metadata, staged.ArchiveSha256, staged.DigestVerified);
            await KeepLaunchpadFirstInScopeAsync(scope, game, updated).ConfigureAwait(false);
            _records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName);
            if (rollback.RolledBack) AddonPathFolder.RemoveCreated(created);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        finally
        {
            DeleteStaging(staged.Folder);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(UpdatedPrefix + staged.Release.Version + ".", updated.Files.Select(f => f.Path).ToArray());
    }

    // ── remove ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes HSF as its guide describes, with RHI Linux's file rules: RHI's files (add-on, HSF_streamline, HSF.ini, HSF.fx,
    /// Launchpad) and the logs HSF writes beside its add-on and in HSF_streamline go; anything else (HSF's camera profiles,
    /// a mods folder) stays and is named. A file RHI placed that changed since refuses the removal.
    /// </summary>
    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (Changed(record) is { Count: > 0 } changed) return ComponentOperationResult.Fail(ChangedPrefix + string.Join(", ", changed));
        var addonFile = record.Files.FirstOrDefault(f => Path.GetFileName(f.Path) is HsfPackage.Addon64 or HsfPackage.Addon32);
        var addonDir = addonFile is null ? record.BinaryDirectory : Path.GetDirectoryName(addonFile.Path)!;
        var streamline = Path.Combine(addonDir, HsfPackage.StreamlineFolder);
        var logs = HsfPackage.HsfLogs.Select(name => Path.Combine(addonDir, name))
            .Concat(Directory.Exists(streamline) ? Directory.EnumerateFiles(streamline, "*.log", SearchOption.TopDirectoryOnly) : [])
            .Where(File.Exists).ToArray();

        var (opened, refusal) = await DeploymentScope.BeginRemovalAsync(_journal, _records, "HSF removal: " + game.Name, game, ManagedComponentKind.Hsf, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        _ = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            foreach (var path in record.Files.Select(f => f.Path).Concat(logs)) await scope.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.Hsf); // the commit point
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "hsf").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName, removal: true);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        // Folders RHI's files were in go when nothing else is left in them; anything else stays and is named.
        foreach (var folder in record.Files.Select(f => Path.GetDirectoryName(f.Path)!).Append(streamline).Distinct().OrderByDescending(p => p.Length))
            if (folder.StartsWith(streamline, StringComparison.Ordinal) || folder.Contains(Path.Combine("reshade-shaders", "Shaders", "iMMERSE"), StringComparison.Ordinal)
                || folder.Contains(Path.Combine("reshade-shaders", "Textures", "iMMERSE"), StringComparison.Ordinal))
                FileCleanup.TryDeleteEmptyDirectory(folder);
        var kept = Directory.Exists(streamline) ? SafeFileEnumeration.Entries(streamline, ignoreInaccessible: true).Select(e => e.FullName).ToList() : [];
        var profiles = Path.Combine(addonDir, "HSF_profiles");
        if (Directory.Exists(profiles)) kept.Add(profiles);
        return ComponentOperationResult.Ok(RemovedMessage + (kept.Count > 0 ? KeptFilesPrefix + string.Join(", ", kept) : ""), record.Files.Select(f => f.Path).ToArray());
    }

    // ── ⚙: Launchpad and the D3D12 route ──────────────────────────────────────────────────────────────────────────

    /// <summary>Turns iMMERSE Launchpad on (its files placed where free) or off (RHI's Launchpad files removed) for HSF.</summary>
    public async Task<ComponentOperationResult> SetLaunchpadAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        IReadOnlyList<LaunchpadFile> staged = [];
        if (on)
        {
            try { staged = await _launchpad.StageAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsDownloadFailure(ex))
            {
                return new ComponentOperationResult(false, DownloadFailedPrefix + ex.Message, SourceUnavailable: ex is HttpRequestException or TaskCanceledException);
            }
        }
        return await ChangeAsync(game, record, "HSF Launchpad", async (scope, updated, created) =>
        {
            var current = LaunchpadPaths(updated);
            if (on)
            {
                var placed = await PlaceLaunchpadAsync(scope, updated.BinaryDirectory, staged, updated.Files, created).ConfigureAwait(false);
                updated.Metadata[MetaLaunchpadFiles] = string.Join("\n", current.Concat(placed).Distinct(StringComparer.Ordinal));
            }
            else
            {
                foreach (var path in current) await scope.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
                updated.Files.RemoveAll(f => current.Contains(f.Path));
                updated.Metadata[MetaLaunchpadFiles] = "";
            }
            updated.Metadata[MetaLaunchpad] = on ? "on" : "off";
            return on ? LaunchpadOnMessage : LaunchpadOffMessage;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets HSF's D3D12 route in HSF.ini now (the ⚙'s choice; None = HSF decides, remembered so RHI never pre-sets it).</summary>
    public async Task<ComponentOperationResult> SetRouteAsync(DetectedGame game, HsfRoute route, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (IniPath(game) is not { } ini || !File.Exists(ini)) return ComponentOperationResult.Fail(NoIniMessage);
        return await ChangeAsync(game, record, "HSF DirectX 12 mode", async (scope, updated, _) =>
        {
            var roots = ReShadeGlobalSettingsApplier.GameRoots(game, game.BinaryDirectory!);
            var written = IniLines.ResolveTarget(ini, roots);
            var lines = IniLines.Read(written);
            if (HsfPackage.SetRoute(lines, route))
            {
                await scope.TrackAsync(written, CancellationToken.None).ConfigureAwait(false);
                IniLines.Write(ini, lines, roots);
                await scope.DeclareWrittenContentAsync(written, CancellationToken.None).ConfigureAwait(false);
            }
            if (route == HsfRoute.None) updated.Metadata[MetaRouteOptOut] = "1";
            else updated.Metadata.Remove(MetaRouteOptOut);
            return RouteSavedPrefix + route;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves iMMERSE Launchpad to the top of the game's ReShade preset where HSF listed it lower down (only the order changes).</summary>
    public async Task<ComponentOperationResult> KeepLaunchpadFirstAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        return await ChangeAsync(game, record, "HSF Launchpad order", async (scope, updated, _) =>
            await KeepLaunchpadFirstInScopeAsync(scope, game, updated).ConfigureAwait(false) ? "moved" : "", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> KeepLaunchpadFirstInScopeAsync(DeploymentScope scope, DetectedGame game, LinuxDeploymentRecord record)
    {
        if (record.Metadata.GetValueOrDefault(MetaLaunchpad) == "off" || PresetPath(game) is not { } preset || !File.Exists(preset)) return false;
        var (text, changed) = _launchpad.KeepFirst(await File.ReadAllTextAsync(preset, CancellationToken.None).ConfigureAwait(false));
        if (!changed) return false;
        var roots = ReShadeGlobalSettingsApplier.GameRoots(game, game.BinaryDirectory!);
        var written = IniLines.ResolveTarget(preset, roots);
        await scope.TrackAsync(written, CancellationToken.None).ConfigureAwait(false);
        IniLines.WriteBytes(preset, System.Text.Encoding.UTF8.GetBytes(text), roots);
        await scope.DeclareWrittenContentAsync(written, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    // The game's ReShade preset: reshade.ini's [GENERAL] PresetPath (relative to the game folder), else ReShadePreset.ini.
    private static string? PresetPath(DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory)) return null;
        var ini = ReShadeDeploymentService.FindFileCaseInsensitive(game.BinaryDirectory, "ReShade.ini");
        var configured = ini is null ? null : IniLines.Get(IniLines.Read(ini), "GENERAL", "PresetPath");
        if (string.IsNullOrWhiteSpace(configured) || configured.Contains(':')) return Path.Combine(game.BinaryDirectory, "ReShadePreset.ini");
        var relative = configured.Trim().Trim('"').Replace('\\', '/').TrimStart('.', '/');
        if (relative.Split('/').Contains("..")) return null;
        return Path.Combine(game.BinaryDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<ComponentOperationResult> ChangeAsync(DetectedGame game, LinuxDeploymentRecord record, string what,
        Func<DeploymentScope, LinuxDeploymentRecord, List<string>, Task<string>> change, CancellationToken cancellationToken)
    {
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(_journal, $"{what}: {game.Name}", game, ManagedComponentKind.Hsf, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        _ = OperationCancellation.BeginWrites(cancellationToken);
        var created = new List<string>();
        var updated = LinuxDeploymentRecordStore.Clone(record);
        string message;
        try
        {
            message = await change(scope, updated, created).ConfigureAwait(false);
            _records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName);
            if (rollback.RolledBack) AddonPathFolder.RemoveCreated(created);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(message);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<LinuxDeployedFile> PlaceAsync(DeploymentScope scope, string source, string target, List<string> created)
    {
        await AddonPathFolder.CreateJournaledAsync(Path.GetDirectoryName(target)!, scope.Operation, created, CancellationToken.None).ConfigureAwait(false);
        var sha = await scope.PlaceCopyAsync(source, target, CancellationToken.None, overwrite: false).ConfigureAwait(false);
        return new LinuxDeployedFile { Path = target, Sha256 = sha, ExistedBeforeInstall = false };
    }

    // Launchpad's files where none is there yet (a user's own iMMERSE pack keeps its copies); the paths placed.
    private static async Task<List<string>> PlaceLaunchpadAsync(DeploymentScope scope, string binaryDir, IReadOnlyList<LaunchpadFile> launchpad,
        List<LinuxDeployedFile> files, List<string> created)
    {
        var placed = new List<string>();
        foreach (var file in launchpad)
        {
            var target = Path.Combine(binaryDir, "reshade-shaders", file.Relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) || files.Any(f => f.Path == target)) continue;
            files.Add(await PlaceAsync(scope, file.Source, target, created).ConfigureAwait(false));
            placed.Add(target);
        }
        return placed;
    }

    private static HashSet<string> LaunchpadPaths(LinuxDeploymentRecord record)
        => (record.Metadata.GetValueOrDefault(MetaLaunchpadFiles) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static bool IsLaunchpadFile(LinuxDeploymentRecord record, string path) => LaunchpadPaths(record).Contains(path);

    // The package's files a game gets (relative to the add-on folder): its add-on and everything in HSF_streamline/.
    private static IEnumerable<(string Relative, string Source)> PackageFiles(Staged staged, bool is32Bit)
    {
        yield return (HsfPackage.AddonFor(is32Bit), Path.Combine(staged.Folder, HsfPackage.AddonFor(is32Bit)));
        var streamline = Path.Combine(staged.Folder, HsfPackage.StreamlineFolder);
        foreach (var file in SafeFileEnumeration.Files(streamline).Order(StringComparer.Ordinal))
            yield return (HsfPackage.StreamlineFolder + "/" + Path.GetRelativePath(streamline, file).Replace(Path.DirectorySeparatorChar, '/'), file);
    }

    // RHI's files (other than HSF.ini, which holds HSF's settings) that no longer hold what RHI placed.
    private static List<string> Changed(LinuxDeploymentRecord record)
        => record.Files.Where(f => !f.AllowUserChanges && File.Exists(f.Path) && f.Sha256 is { Length: > 0 } sha && !FileHash.HexEquals(FileHash.Sha256Hex(f.Path), sha))
            .Select(f => f.Path).ToList();

    /// <summary>
    /// The game ships its own Streamline: an <c>sl.interposer.dll</c> under its install folder outside HSF's own folder.
    /// Null when the folder cannot be read (no guess: HSF picks by itself).
    /// </summary>
    internal static bool? HasGameStreamline(DetectedGame game, string addonDir)
    {
        var root = !string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath) ? game.InstallPath : game.BinaryDirectory;
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var own = Path.Combine(addonDir, HsfPackage.StreamlineFolder) + Path.DirectorySeparatorChar;
            // Never through a directory link (a Wine prefix's z: → /, #64).
            return SafeFileEnumeration.Files(root, "*", ignoreInaccessible: true)
                .Any(path => Path.GetFileName(path).Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(own, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A staging folder is RHI's own scratch copy under its cache: removed whole (a failure is only logged).
    private static void DeleteStaging(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("hsf", $"Could not remove the staging folder {folder}", ex); }
    }

    private static bool IsDownloadFailure(Exception ex)
        => ex is HttpRequestException or IOException or InvalidDataException or JsonException or UntrustedDownloadException or TaskCanceledException;

    private sealed record Staged(string Folder, HsfRelease Release, string ArchiveSha256, bool DigestVerified);

    private async Task<HsfRelease> LatestReleaseAsync(CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, HsfPackage.ReleasesApiUrl, DownloadSources.GitHubApi, accept: "application/vnd.github+json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return HsfPackage.ParseNewestRelease(json) ?? throw new InvalidDataException(LayoutUnknownMessage);
    }

    /// <summary>The newest release's zip through the stamped cache (GitHub's digest checked), its files staged fresh.</summary>
    private async Task<Staged> StageAsync(CancellationToken cancellationToken)
    {
        var release = await LatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var entry = Path.Combine(_cacheDir, release.Version, string.Concat(release.AssetName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')));
        var verified = await TrustedDownload.EnsureVerifiedAsync(_http, release.Url, entry, DownloadSources.Hsf, release.Version, release.Digest,
            validate: ReShadeArchiveExtractor.RequireReadableZip, cancellationToken: cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(_cacheDir, "staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var zip = ZipFile.OpenRead(verified.Path))
            {
                foreach (var zipEntry in zip.Entries)
                {
                    if (HsfPackage.StagedPathFor(zipEntry.FullName) is not { } relative) continue;
                    var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target)) continue; // the first of a name wins
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(zipEntry, target, FileMaxBytes, cancellationToken).ConfigureAwait(false);
                }
            }
            if (HsfPackage.RequiredFiles.Any(file => !File.Exists(Path.Combine(staging, file.Replace('/', Path.DirectorySeparatorChar)))))
                throw new InvalidDataException(LayoutUnknownMessage);
        }
        catch
        {
            DeleteStaging(staging);
            throw;
        }
        return new Staged(staging, release, verified.Sha256, verified.DigestVerified);
    }
}
