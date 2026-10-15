using System.IO.Compression;
using System.Text.Json;
using RHI.Core;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed record ReShadeAddonCatalogItem(
    string Id,
    string DisplayName,
    string Description,
    string? RepositoryUrl,
    bool IsCustom,
    bool ManagedByComponent,
    bool Supports32Bit,
    bool Supports64Bit);

public sealed record ReShadeAddonManagerSnapshot(
    IReadOnlyList<ReShadeAddonCatalogItem> Catalog,
    IReadOnlyList<string> SelectedIds,
    string CustomAddonRoot);

internal sealed record ReShadeAddonDefinition(
    string Id,
    string DisplayName,
    string Description,
    string? DownloadUrl,
    string? DownloadUrl32,
    string? DownloadUrl64,
    string? RepositoryUrl,
    string? EffectInstallPath,
    string? DeployFileName,
    string? ReleaseApiUrl,
    bool IsCustom = false,
    string? CustomPath32 = null,
    string? CustomPath64 = null);

/// <summary>
/// Generic per-game ReShade addon manager for Linux.
/// It consumes crosire's Addons.ini plus upstream RHI manifest overrides and supports local custom addons.
/// First-class RHI components remain protected from duplicate management.
/// </summary>
public sealed partial class ReShadeAddonManagerService : IGameStateStore
{
    public const string AddonsIniUrl = "https://raw.githubusercontent.com/crosire/reshade-shaders/list/Addons.ini";
    private static readonly HashSet<string> ExcludedSections = new(StringComparer.OrdinalIgnoreCase) { "00", "21", "26" };

    private static readonly HashSet<string> ComponentManagedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "renodx-upgrade",
        "mfgunlock",
        "dlss5-feed",
        "dlss5-dx11-bridge",
        "renodx-dlss5",
        "renodx-dlss-sf",
        "renodx-devkit",
        "renodx-dlssfix",
    };

    private readonly HttpClient _http;
    private readonly UpstreamRhiManifestService _manifest;
    private readonly string _cacheDir;
    private readonly string _addonsIniCache;
    private readonly string _statePath;
    private readonly JsonFileStore<StateDocument> _stateFile;
    private readonly string _customRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DeploymentJournal? _journal;

    /// <summary>
    /// <c>addon-manager.json</c>: the copy of the ledger's Addon Manager records older builds read (#168). Its shape is
    /// the one they write; <see cref="LedgerCopySha256"/> is this build's stamp.
    /// </summary>
    internal sealed class StateDocument
    {
        public int Schema { get; set; } = 1;
        public Dictionary<string, GameState> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>SHA-256 of <see cref="Games"/> (canonical) as this build wrote them; older builds drop it.</summary>
        public string? LedgerCopySha256 { get; set; }
    }

    internal sealed class GameState
    {
        public List<string> SelectedIds { get; set; } = [];
        public List<StateFile> Files { get; set; } = [];
    }

    internal sealed class StateFile
    {
        public string AddonId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    /// <param name="records">The deployment ledger (#168); null (tests) = a ledger beside <paramref name="statePath"/>.</param>
    /// <param name="journal">The deployment journal; null (tests) = in-process rollback only.</param>
    public ReShadeAddonManagerService(
        HttpClient http,
        UpstreamRhiManifestService manifest,
        string? cacheDir = null,
        string? customRoot = null,
        string? statePath = null,
        LinuxDeploymentRecordStore? records = null,
        DeploymentJournal? journal = null)
    {
        _http = http;
        _manifest = manifest;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "addons");
        _addonsIniCache = Path.Combine(_cacheDir, "Addons.ini");
        _customRoot = customRoot ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "Custom", "Addons");
        _statePath = statePath ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "addon-manager.json");
        _stateFile = new JsonFileStore<StateDocument>(_statePath);
        _records = records ?? (statePath is null
            ? new LinuxDeploymentRecordStore()
            : new LinuxDeploymentRecordStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath))!, "deployments.json")));
        _journal = journal;
        Directory.CreateDirectory(_customRoot);
    }

    public string CustomRoot => _customRoot;

    /// <summary>
    /// Imports a standalone addon into the XDG Custom/Addons catalog only. This is deliberately
    /// not a game deployment operation: the user still selects the addon per game in the manager.
    /// Dedicated first-class RHI component payloads are rejected to avoid duplicate ownership.
    /// </summary>
    public ComponentOperationResult ImportCustomAddonFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return ComponentOperationResult.Fail("Addon file does not exist.");

        var extension = Path.GetExtension(sourcePath);
        var expectedArchitecture = extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
            ? PeArchitecture.X86
            : extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase)
                ? PeArchitecture.X64
                : PeArchitecture.Unknown;
        if (expectedArchitecture == PeArchitecture.Unknown)
            return ComponentOperationResult.Fail("Custom addon import accepts only .addon32 or .addon64 files.");
        if (new FileInfo(sourcePath).Length < 1024)
            return ComponentOperationResult.Fail("Addon payload is unexpectedly small.");

        var fileName = Path.GetFileName(sourcePath);
        if (IsReservedCustomFileName(fileName))
            return ComponentOperationResult.Fail($"{fileName} belongs to a dedicated RHI component row and cannot be imported through Custom Addons.");

        var actualArchitecture = PeArchitectureDetector.Detect(sourcePath);
        if (actualArchitecture != expectedArchitecture)
            return ComponentOperationResult.Fail($"Addon architecture mismatch: {fileName} declares {expectedArchitecture} but contains {actualArchitecture}.");

        Directory.CreateDirectory(_customRoot);
        var target = Path.Combine(_customRoot, fileName);
        if (File.Exists(target))
        {
            if (string.Equals(FileHash.Sha256Hex(sourcePath), FileHash.Sha256Hex(target), StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Ok($"{fileName} is already present in Custom Addons.", target);
            return ComponentOperationResult.Fail($"A different Custom Addon already exists with the name {fileName}; RHI will not overwrite it.");
        }

        try
        {
            // Verified, durable copy (#31); never replaces a file that appeared under the name meanwhile.
            AtomicFile.Copy(sourcePath, target, overwrite: false);
            return ComponentOperationResult.Ok($"Imported {fileName} into Custom Addons. Select it in Shaders/Addons for the games that should use it.", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ComponentOperationResult.Fail($"Could not import {fileName}: {ex.Message}");
        }
    }

    internal static bool IsReservedCustomFileName(string fileName)
    {
        var value = fileName.ToLowerInvariant();
        return value.Contains("relimiter") ||
               value.Contains("display_commander") || value.Contains("displaycommander") ||
               value.Contains("mfgunlock") ||
               value.Contains("dlss5-feed") || value.Contains("dlss5-bridge") ||
               value.Contains("renodx-upgrade") ||
               value.Contains("renodx-dlss5") || value.Contains("renodx-dlss-sf") ||
               value.Contains("renodx-dlss.") ||
               value.Contains("renodx-devkit") || value.Contains("renodx-dlssfix");
    }

    public async Task<ReShadeAddonManagerSnapshot> GetSnapshotAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var catalog = await RefreshCatalogAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> selected;
        try
        {
            TryReconcile(out _);
            selected = SelectedIdsOf(_records.Find(game, RecordKind));
        }
        finally
        {
            _gate.Release();
        }
        return new(catalog.Select(ToCatalogItem).ToArray(), selected.ToArray(), _customRoot);
    }

    /// <summary>
    /// The add-on files this manager installed on <paramref name="game"/> (under their own names), for #170; null when its
    /// saved list cannot be read (then nobody can tell which add-ons are the manager's).
    /// </summary>
    public IReadOnlyList<string>? OwnedAddonFiles(DetectedGame game)
    {
        _gate.Wait();
        try
        {
            if (TryReconcile(out _) is ReconcileOutcome.LedgerUnreadable or ReconcileOutcome.CopyUnreadable or null) return null;
            return _records.Find(game, RecordKind)?.Files.Select(file => WithoutOffSuffix(file.Path)).ToArray() ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ReShadeAddonCatalogItem>> RefreshCatalogItemsAsync(
        CancellationToken cancellationToken = default)
        => (await RefreshCatalogAsync(cancellationToken).ConfigureAwait(false))
            .Select(ToCatalogItem).ToArray();

    public async Task<ComponentOperationResult> ApplySelectionAsync(
        DetectedGame game,
        IReadOnlyList<string> selectedIds,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("ReShade addons are only deployed to Windows games running through Wine/Proton.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (!GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Install RHI-managed ReShade with add-on support before using the Addon Manager.");

        var architecture = RenoDxUpgradeDeploymentService.ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail("Could not determine game bitness for ReShade addon deployment.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await RefreshCatalogAsync(cancellationToken).ConfigureAwait(false);
            var byId = catalog.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var normalized = selectedIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var unknown = normalized.Where(id => !byId.ContainsKey(id)).ToArray();
            if (unknown.Length > 0)
                return ComponentOperationResult.Fail($"Unknown ReShade addon id(s): {string.Join(", ", unknown)}");

            var blocked = normalized
                .Select(id => byId[id])
                .Where(IsComponentManaged)
                .Select(x => x.DisplayName)
                .ToArray();
            if (blocked.Length > 0)
                return ComponentOperationResult.Fail($"These addons are managed by dedicated RHI component rows and cannot be duplicated here: {string.Join(", ", blocked)}");

            // Refuse before touching the game: the copy older builds read is user data RHI never writes over while it
            // cannot be read, and the ledger is the commit point (#168).
            _stateFile.EnsureWritable();
            if (_records.CorruptLedgerPath is not null)
                return ComponentOperationResult.Fail($"{ReconcileFailedPrefix}{_records.CorruptLedgerPath}");
            if (TryReconcile(out var reconcileError) is null)
                return ComponentOperationResult.Fail($"{ReconcileFailedPrefix}{reconcileError}");
            var existing = _records.Find(game, RecordKind);
            var oldFiles = existing?.Files.Where(file => !string.IsNullOrWhiteSpace(file.Path)).ToList() ?? [];

            // #170: an add-on turned off for ReShade's No Addons build sits under its name + ReShadeAddonSwitch.OffSuffix.
            foreach (var file in oldFiles)
            {
                foreach (var path in Forms(file.Path))
                {
                    if (!File.Exists(path)) continue;
                    var current = FileHash.Sha256Hex(path);
                    if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        return ComponentOperationResult.Fail($"Managed addon was modified outside RHI and the selection cannot be changed safely: {path}");
                }
            }

            // Prepare all payloads before touching the game.
            var prepared = new List<(ReShadeAddonDefinition Definition, string Source, string DeployName)>();
            foreach (var id in normalized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = byId[id];
                var payload = await PreparePayloadAsync(definition, architecture, cancellationToken).ConfigureAwait(false);
                prepared.Add((definition, payload.Path, payload.DeployName));
            }

            if (normalized.Length == 0 && existing is null)
                return ComponentOperationResult.Ok("RHI-managed generic ReShade addons removed.");

            var deployDir = RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory);
            var oldByPath = oldFiles.Select(file => Path.GetFullPath(WithoutOffSuffix(file.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in prepared)
            {
                var target = Path.GetFullPath(Path.Combine(deployDir, item.DeployName));
                var allowed = Path.GetFullPath(deployDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(allowed, StringComparison.Ordinal))
                    return ComponentOperationResult.Fail($"Addon deploy filename escapes the ReShade addon directory: {item.DeployName}");

                if (File.Exists(target) && !oldByPath.Contains(target))
                    return ComponentOperationResult.Fail($"External addon already exists at {target}. RHI will not overwrite or adopt it.");
            }

            // DEP-09 (#28), #168: one journaled operation whose commit point is the game's Addon Manager record (saved, or
            // taken out of the ledger when the selection is emptied). Every previously owned add-on and every target is
            // tracked with a verified copy before anything changes, new add-ons are placed through verified, durable
            // copies, the older-build copy (addon-manager.json) is written inside the operation before the commit point,
            // and a failure — or a crash, at the next start — puts every file back.
            var removal = normalized.Length == 0;
            var (opened, refusal) = removal
                ? await DeploymentScope.BeginRemovalAsync(_journal, _records, $"ReShade Addon Manager removal: {game.Name}", game, RecordKind, cancellationToken).ConfigureAwait(false)
                : await DeploymentScope.BeginAsync(_journal, $"ReShade Addon Manager: {game.Name}", game, RecordKind, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) return ComponentOperationResult.Fail(refusal);
            await using var scope = opened!;

            // Every payload is prepared: from the first change in the game folder on, Cancel is refused and the
            // selection change runs to completion or full rollback.
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            var changed = new List<string>();
            try
            {
                Directory.CreateDirectory(deployDir);
                foreach (var file in oldFiles)
                    foreach (var path in Forms(file.Path))
                        if (File.Exists(path)) // #170: its turned-off copy goes too
                            await scope.DeleteAsync(path, cancellationToken).ConfigureAwait(false);

                var placed = new List<(string AddonId, string Path, string Sha256)>();
                foreach (var item in prepared)
                {
                    var target = Path.Combine(deployDir, item.DeployName);
                    var hash = await scope.PlaceCopyAsync(item.Source, target, cancellationToken).ConfigureAwait(false);
                    changed.Add(target);
                    placed.Add((item.Definition.Id, target, hash));
                }

                var record = removal ? null : BuildRecord(game, normalized, placed, existing);
                await WriteCopyInsideAsync(scope, game, record).ConfigureAwait(false);
                if (record is null) _records.Remove(game, RecordKind);     // the commit point (removal)
                else _records.Upsert(scope.Stamp(record));               // the commit point (install / change)
            }
            catch (Exception ex)
            {
                var rollback = removal
                    ? await scope.RollbackRemovalAsync(_records, game, existing!, "addon-manager").ConfigureAwait(false)
                    : await scope.RollbackAsync().ConfigureAwait(false);
                if (!rollback.Success)
                    AppLog.Warn("addon-manager", $"Selection rollback did not complete: {string.Join("; ", rollback.Failures)}");
                return ComponentOperationResult.Fail($"ReShade Addon Manager failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
            }
            await scope.CommitAsync().ConfigureAwait(false);

            return ComponentOperationResult.Ok(
                normalized.Length == 0
                    ? "RHI-managed generic ReShade addons removed."
                    : $"Applied {normalized.Length} ReShade addon selection(s).",
                changed.ToArray());
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"ReShade Addon Manager failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// "Repair after move" (Refs #38): moves this game's selection to its current key and re-points
    /// each owned add-on at its new location when the file is there with the recorded hash; a file
    /// that is not keeps its old path (it is then simply not found). Writes nothing.
    /// </summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        _gate.Wait();
        try
        {
            // #168: the ledger's Addon Manager record moves with the other records; this re-points the older-build copy. A
            // copy this build stamped is stamped again; one an older build wrote stays unstamped, so it is still imported.
            return _stateFile.Stage(state =>
            {
                if (state is null) return null;
                state.Games = JsonStoreMaps.CaseInsensitive(state.Games);
                var wasStamped = string.Equals(state.LedgerCopySha256, CopySha256(state.Games), StringComparison.OrdinalIgnoreCase);
                var game = move.Game;
                var entryKey = state.Games.Keys.FirstOrDefault(k => GameRecordIdentity.Matches(k, game))
                               ?? state.Games.Keys.FirstOrDefault(k => move.MovedRecordKey(k) is not null);
                if (entryKey is null) return null;
                var entry = state.Games[entryKey];
                var changed = false;
                var deployDir = string.IsNullOrWhiteSpace(game.BinaryDirectory)
                    ? null
                    : RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory);
                foreach (var file in entry.Files)
                {
                    var targets = new[] { move.MapPath(file.Path), deployDir is null ? null : Path.Combine(deployDir, Path.GetFileName(file.Path)) };
                    foreach (var target in targets.OfType<string>().Distinct(StringComparer.Ordinal))
                    {
                        // #170: a turned-off add-on is found under its .rhi-off name; the copy keeps the add-on's own name.
                        var onDisk = Forms(target).FirstOrDefault(File.Exists);
                        if (string.Equals(target, file.Path, StringComparison.Ordinal) || onDisk is null
                            || !string.Equals(FileHash.Sha256Hex(onDisk), file.Sha256, StringComparison.OrdinalIgnoreCase))
                            continue;
                        file.Path = target;
                        changed = true;
                        break;
                    }
                }
                if (!string.Equals(entryKey, move.NewRecordKey, StringComparison.Ordinal))
                {
                    state.Games.Remove(entryKey);
                    state.Games.TryAdd(move.NewRecordKey, entry);
                    changed = true;
                }
                if (!changed) return null;
                state.LedgerCopySha256 = wasStamped ? CopySha256(state.Games) : null;
                return state;
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static List<ReShadeAddonDefinition> ParseAddonsIni(string content)
    {
        var entries = new List<ReShadeAddonDefinition>();
        if (string.IsNullOrWhiteSpace(content)) return entries;

        string? currentId = null;
        bool commented = false;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (currentId is null || commented || ExcludedSections.Contains(currentId))
                return;
            if (!fields.TryGetValue("PackageName", out var name) || string.IsNullOrWhiteSpace(name))
                return;
            entries.Add(new ReShadeAddonDefinition(
                currentId,
                name.Trim(),
                fields.GetValueOrDefault("PackageDescription") ?? "",
                fields.GetValueOrDefault("DownloadUrl"),
                fields.GetValueOrDefault("DownloadUrl32"),
                fields.GetValueOrDefault("DownloadUrl64"),
                fields.GetValueOrDefault("RepositoryUrl"),
                fields.GetValueOrDefault("EffectInstallPath"),
                fields.GetValueOrDefault("DeployFileName"),
                fields.GetValueOrDefault("ReleaseApiUrl")));
        }

        foreach (var raw in content.Split('\n'))
        {
            var trimmed = raw.Trim().TrimEnd('\r');
            var work = trimmed;
            var isCommentedHeader = false;
            if (work.StartsWith('#') || work.StartsWith(';'))
            {
                isCommentedHeader = true;
                work = work.TrimStart('#', ';').Trim();
            }

            if (work.StartsWith('[') && work.Contains(']'))
            {
                Flush();
                var close = work.IndexOf(']');
                currentId = close > 1 ? work[1..close].Trim() : null;
                commented = isCommentedHeader;
                fields.Clear();
                continue;
            }

            if (currentId is null || string.IsNullOrWhiteSpace(trimmed)) continue;
            var valueLine = trimmed;
            if (commented)
            {
                if (!(valueLine.StartsWith('#') || valueLine.StartsWith(';'))) continue;
                valueLine = valueLine.TrimStart('#', ';').Trim();
            }
            else if (valueLine.StartsWith('#') || valueLine.StartsWith(';'))
            {
                continue;
            }

            var eq = valueLine.IndexOf('=');
            if (eq <= 0) continue;
            fields[valueLine[..eq].Trim()] = valueLine[(eq + 1)..].Trim();
        }

        Flush();
        return entries;
    }

    private async Task<List<ReShadeAddonDefinition>> RefreshCatalogAsync(
        CancellationToken cancellationToken)
    {
        List<ReShadeAddonDefinition> catalog;
        try
        {
            var text = await TrustedDownload.GetStringAsync(_http, AddonsIniUrl, TrustedDownloadPolicy.ReShadeAddons, cancellationToken: cancellationToken).ConfigureAwait(false);
            catalog = ParseAddonsIni(text);
            Directory.CreateDirectory(_cacheDir);
            await AtomicFile.WriteAllTextAsync(_addonsIniCache, text, cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(_addonsIniCache))
                catalog = ParseAddonsIni(await File.ReadAllTextAsync(_addonsIniCache, cancellationToken).ConfigureAwait(false));
            else
                catalog = [];
        }

        try
        {
            var overrides = await _manifest.FetchAddonPacksAsync(cancellationToken).ConfigureAwait(false);
            MergeManifest(catalog, overrides);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Addons.ini + custom addons remain usable offline.
        }

        MergeCustom(catalog);
        return catalog
            .OrderBy(x => IsComponentManaged(x))
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static void MergeManifest(
        List<ReShadeAddonDefinition> catalog,
        IReadOnlyDictionary<string, UpstreamAddonPackDefinition> overrides)
    {
        foreach (var (id, entry) in overrides)
        {
            var index = catalog.FindIndex(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry.Disabled)
            {
                if (index >= 0) catalog.RemoveAt(index);
                continue;
            }

            var replacement = new ReShadeAddonDefinition(
                id,
                entry.PackageName,
                entry.Description ?? "",
                entry.DownloadUrl,
                entry.DownloadUrl32,
                entry.DownloadUrl64,
                entry.RepositoryUrl,
                entry.EffectInstallPath,
                entry.DeployFileName,
                entry.ReleaseApiUrl);

            if (index >= 0)
            {
                var old = catalog[index];
                catalog[index] = replacement with
                {
                    Description = string.IsNullOrWhiteSpace(replacement.Description) ? old.Description : replacement.Description,
                    DownloadUrl = replacement.DownloadUrl ?? old.DownloadUrl,
                    DownloadUrl32 = replacement.DownloadUrl32 ?? old.DownloadUrl32,
                    DownloadUrl64 = replacement.DownloadUrl64 ?? old.DownloadUrl64,
                    RepositoryUrl = replacement.RepositoryUrl ?? old.RepositoryUrl,
                    EffectInstallPath = replacement.EffectInstallPath ?? old.EffectInstallPath,
                    DeployFileName = replacement.DeployFileName ?? old.DeployFileName,
                    ReleaseApiUrl = replacement.ReleaseApiUrl ?? old.ReleaseApiUrl,
                };
            }
            else if (HasDownloadAuthority(replacement))
            {
                catalog.Add(replacement);
            }
        }
    }

    private void MergeCustom(List<ReShadeAddonDefinition> catalog)
    {
        Directory.CreateDirectory(_customRoot);
        var byBase = Directory.EnumerateFiles(_customRoot, "*.addon*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);

        foreach (var group in byBase)
        {
            var path32 = group.FirstOrDefault(x => x.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase));
            var path64 = group.FirstOrDefault(x => x.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase));
            var id = $"custom-{group.Key}";
            catalog.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            catalog.Add(new ReShadeAddonDefinition(
                id,
                group.Key,
                "Custom addon (local file)",
                null, null, null, null, null,
                group.Key,
                null,
                true,
                path32,
                path64));
        }
    }

    private async Task<(string Path, string DeployName)> PreparePayloadAsync(
        ReShadeAddonDefinition definition,
        PeArchitecture architecture,
        CancellationToken cancellationToken)
    {
        var ext = architecture == PeArchitecture.X86 ? ".addon32" : ".addon64";

        if (definition.IsCustom)
        {
            var custom = architecture == PeArchitecture.X86 ? definition.CustomPath32 : definition.CustomPath64;
            if (string.IsNullOrWhiteSpace(custom) || !File.Exists(custom))
                throw new InvalidOperationException($"{definition.DisplayName} does not provide a {ext} custom payload.");
            ValidateAddonPayload(custom, ext, architecture);
            return (custom, EnsureAddonExtension(definition.DeployFileName ?? Path.GetFileNameWithoutExtension(custom), ext));
        }

        var sourceUrl = architecture == PeArchitecture.X86 ? definition.DownloadUrl32 : definition.DownloadUrl64;
        sourceUrl ??= definition.DownloadUrl;

        if (!string.IsNullOrWhiteSpace(definition.ReleaseApiUrl))
            sourceUrl = await ResolveReleaseAssetAsync(definition.ReleaseApiUrl!, ext, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(sourceUrl))
            throw new InvalidOperationException($"{definition.DisplayName} has no {ext} download authority.");

        ValidateDirectUrlArchitecture(sourceUrl, ext);

        Directory.CreateDirectory(_cacheDir);
        var safeId = Sanitize(definition.Id);
        if (IsZipUrl(sourceUrl))
        {
            var zipPath = Path.Combine(_cacheDir, $"{safeId}-{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(sourceUrl, zipPath, cancellationToken).ConfigureAwait(false);
                using var archive = ZipFile.OpenRead(zipPath);
                var entries = archive.Entries
                    .Where(x => !string.IsNullOrEmpty(x.Name) && x.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (entries.Length == 0)
                    throw new InvalidDataException($"{definition.DisplayName} archive contains no {ext} payload.");

                var preferredBase = Path.GetFileNameWithoutExtension(definition.DeployFileName ?? "");
                var entry = entries.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(preferredBase) &&
                    Path.GetFileNameWithoutExtension(x.Name).Equals(preferredBase, StringComparison.OrdinalIgnoreCase))
                    ?? entries[0];

                var cachePath = Path.Combine(_cacheDir, $"{safeId}{ext}");
                if (await ReShadeArchiveExtractor.ExtractEntryAsync(entry, cachePath, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false) is { } refused)
                    throw new InvalidDataException($"{ReShadeArchiveExtractor.ArchiveRefusedPrefix}{refused}");
                ValidateAddonPayload(cachePath, ext, architecture);
                var deploy = EnsureAddonExtension(
                    definition.DeployFileName ?? Path.GetFileNameWithoutExtension(entry.Name),
                    ext);
                return (cachePath, deploy);
            }
            finally
            {
                if (!FileCleanup.TryDelete(zipPath, out var cleanupError))
                    AppLog.Warn("addon-manager", $"Could not remove the downloaded archive {zipPath}", cleanupError);
            }
        }
        else
        {
            var cachePath = Path.Combine(_cacheDir, $"{safeId}{ext}");
            await DownloadAsync(sourceUrl, cachePath, cancellationToken).ConfigureAwait(false);
            ValidateAddonPayload(cachePath, ext, architecture);
            var fromUrl = Path.GetFileName(new Uri(sourceUrl).AbsolutePath);
            var deploy = EnsureAddonExtension(
                definition.DeployFileName ?? Path.GetFileNameWithoutExtension(fromUrl),
                ext);
            return (cachePath, deploy);
        }
    }

    private async Task<string> ResolveReleaseAssetAsync(
        string apiUrl,
        string architectureExtension,
        CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, apiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var releases = root.EnumerateArray().ToArray();
            if (releases.Length == 0)
                throw new InvalidDataException($"GitHub release list is empty: {apiUrl}");
            root = releases[0];
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("assets", out var assets))
            throw new InvalidDataException($"GitHub release has no assets: {apiUrl}");

        string? zip = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (name.EndsWith(architectureExtension, StringComparison.OrdinalIgnoreCase))
                return url!;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                zip ??= url;
        }
        return zip ?? throw new InvalidDataException($"GitHub release has no {architectureExtension} or .zip asset: {apiUrl}");
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        // DEP-06: add-on DLLs only over HTTPS from GitHub / GitHub Pages; checked before the request and after
        // redirects, size-capped and written through a verified, fsynced temp (TrustedDownload).
        using var download = await TrustedDownload.DownloadToTempAsync(
            _http, url, destination, TrustedDownloadPolicy.ReShadeAddons, cancellationToken: cancellationToken).ConfigureAwait(false);
        download.MoveTo(destination);
    }

    internal static void ValidateDirectUrlArchitecture(string url, string expectedExtension)
    {
        string path;
        try { path = new Uri(url).AbsolutePath; }
        catch { path = url; }

        var declares32 = path.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);
        var declares64 = path.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase);
        if (!declares32 && !declares64) return;

        if (declares32 && !expectedExtension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) ||
            declares64 && !expectedExtension.Equals(".addon64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Addon download URL architecture mismatch: expected {expectedExtension}, got {Path.GetFileName(path)}.");
    }

    private static bool IsZipUrl(string url)
    {
        try { return new Uri(url).AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase); }
        catch { return url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase); }
    }

    private static void ValidateAddonPayload(string path, string expectedExtension, PeArchitecture expectedArchitecture)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 1024)
            throw new InvalidDataException($"Addon payload is missing or unexpectedly small: {path}");
        if (!path.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Addon payload architecture extension mismatch: expected {expectedExtension}.");

        var actualArchitecture = PeArchitectureDetector.Detect(path);
        if (actualArchitecture != expectedArchitecture)
            throw new InvalidDataException($"Addon PE architecture mismatch: expected {expectedArchitecture}, got {actualArchitecture} ({path}).");
    }

    private static string EnsureAddonExtension(string name, string extension)
    {
        var baseName = name;
        if (baseName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ||
            baseName.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase))
            baseName = Path.GetFileNameWithoutExtension(baseName);
        baseName = Sanitize(baseName);
        return baseName + extension;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
    }

    private static bool HasDownloadAuthority(ReShadeAddonDefinition definition)
        => definition.IsCustom ||
           !string.IsNullOrWhiteSpace(definition.DownloadUrl) ||
           !string.IsNullOrWhiteSpace(definition.DownloadUrl32) ||
           !string.IsNullOrWhiteSpace(definition.DownloadUrl64) ||
           !string.IsNullOrWhiteSpace(definition.ReleaseApiUrl);

    internal static bool IsComponentManaged(ReShadeAddonDefinition definition)
    {
        if (ComponentManagedIds.Contains(definition.Id)) return true;
        var name = definition.DisplayName;
        return name.Contains("ReLimiter", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Display Commander", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("DLSS5", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ShortFuse", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("RenoDX Upgrade", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("MFG Ada", StringComparison.OrdinalIgnoreCase);
    }

    private static ReShadeAddonCatalogItem ToCatalogItem(ReShadeAddonDefinition definition)
        => new(
            definition.Id,
            definition.DisplayName,
            definition.Description,
            definition.RepositoryUrl,
            definition.IsCustom,
            IsComponentManaged(definition),
            definition.IsCustom ? !string.IsNullOrWhiteSpace(definition.CustomPath32) :
                !string.IsNullOrWhiteSpace(definition.DownloadUrl32) || !string.IsNullOrWhiteSpace(definition.DownloadUrl) || !string.IsNullOrWhiteSpace(definition.ReleaseApiUrl),
            definition.IsCustom ? !string.IsNullOrWhiteSpace(definition.CustomPath64) :
                !string.IsNullOrWhiteSpace(definition.DownloadUrl64) || !string.IsNullOrWhiteSpace(definition.DownloadUrl) || !string.IsNullOrWhiteSpace(definition.ReleaseApiUrl));

    // A recorded add-on path in both its forms: its own name and the No Addons name (#170).
    private static string[] Forms(string path)
    {
        var own = WithoutOffSuffix(path);
        return [own, own + ReShadeAddonSwitch.OffSuffix];
    }

}
