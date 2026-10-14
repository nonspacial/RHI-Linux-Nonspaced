using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux/Wine deployment of Universal RTXMFG for RTX 40 and experimental RTX 30.
/// Upstream dashdogy/RTX40MFG-Unlock v1.3+ ships one RTXMFG.dll which is renamed
/// to a supported proxy beside the confirmed Patch Target. RTX 30 is DX12-only and
/// remains explicitly experimental upstream. RHI-Linux validates the official release
/// digest/checksum when available, validates PE x64, applies a per-game Wine
/// native,builtin override, and hash-guards update/removal.
/// </summary>
public sealed class Rtx40MfgStandaloneDeploymentService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/dashdogy/RTX40MFG-Unlock/releases/latest";
    public const string RepositoryUrl = "https://github.com/dashdogy/RTX40MFG-Unlock";
    public const string InnerDllName = "RTXMFG.dll";
    public const string DefaultProxyName = "version.dll";
    public const string GpuGenRtx40 = "RTX 40 Series";
    public const string GpuGenRtx30 = "RTX 30 Series";

    public static readonly IReadOnlyList<string> GpuGenerations = [GpuGenRtx40, GpuGenRtx30];

    // Universal RTXMFG README supported filenames. Keep the complete upstream
    // authority list here even though Bink chaining is deliberately not automated on Linux yet.
    public static readonly IReadOnlyList<string> ProxyNames =
    [
        "version.dll", "dinput8.dll", "winmm.dll", "d3d9.dll", "d3d10.dll",
        "d3d11.dll", "d3d12.dll", "dxgi.dll", "dsound.dll", "wininet.dll",
        "winhttp.dll", "binkw64.dll", "bink2w64.dll", "xinput1_1.dll",
        "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll",
        "xinputuap.dll"
    ];

    // Bink routes require moving the game's original bink DLL to *Hooked.dll and
    // chain-loading it. That migration/rollback path is intentionally deferred to a
    // dedicated compatibility handling rather than guessing the required chain.
    public static readonly IReadOnlyList<string> SelectableProxyNames = ProxyNames
        .Where(name => !IsBinkProxy(name))
        .ToArray();

    private const string MetaGpuGeneration = "UniversalMFG.GpuGeneration";
    private const string MetaProxyName = "UniversalMFG.ProxyName";
    private const string LegacyMetaProxyName = "RTX40MFG.ProxyName";
    private const string MetaTag = "RTX40MFG.Tag";
    private const string MetaAssetName = "RTX40MFG.AssetName";
    private const string MetaAssetSha256 = "RTX40MFG.AssetSha256";
    private const string MetaPayloadSha256 = "RTX40MFG.PayloadSha256";
    private const string MetaExperimental = "RTX40MFG.ProtonExperimental";
    private const string MetaUpstream = "RTX40MFG.Upstream";
    private const int MinimumDllSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly DeploymentJournal? _journal;
    private readonly Rtx40MfgPreferenceStore _preferences;

    /// <summary>The per-game store "Repair after move" carries over (Refs #38).</summary>
    internal Settings.IGameStateStore GameState => _preferences;
    private readonly string _cacheRoot;

    public Rtx40MfgStandaloneDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        Rtx40MfgPreferenceStore? preferences = null,
        string? cacheRoot = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _overrides = overrides;
        _preferences = preferences ?? new Rtx40MfgPreferenceStore();
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "rtx40-mfg-standalone");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone);

    public Rtx40MfgPreferences GetPreferences(DetectedGame game)
    {
        var record = GetRecord(game);
        if (record is not null)
        {
            return new Rtx40MfgPreferences
            {
                GpuGeneration = record.Metadata.GetValueOrDefault(MetaGpuGeneration) ?? GpuGenRtx40,
                ProxyName = record.Metadata.GetValueOrDefault(MetaProxyName)
                            ?? record.Metadata.GetValueOrDefault(LegacyMetaProxyName)
                            ?? DefaultProxyName
            }.Normalize();
        }
        return _preferences.Get(game);
    }

    public ComponentOperationResult SetPreferences(DetectedGame game, string proxyName)
        => SetPreferences(game, GetPreferences(game).GpuGeneration, proxyName);

    public ComponentOperationResult SetPreferences(DetectedGame game, string gpuGeneration, string proxyName)
    {
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("Universal MFG já está instalado. Remova e reinstale para trocar a geração da GPU ou a DLL proxy.");

        var normalizedGpu = NormalizeGpuGeneration(gpuGeneration);
        if (!GpuGenerations.Contains(normalizedGpu, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Geração de GPU não suportada pelo Universal MFG: {gpuGeneration}.");

        var requested = CanonicalProxyOrNull(proxyName);
        if (requested is null)
            return ComponentOperationResult.Fail($"DLL proxy não suportada pelo Universal MFG: {proxyName}.");
        if (IsBinkProxy(requested))
            return ComponentOperationResult.Fail("Os proxies Bink exigem chaining da DLL original (*Hooked.dll). Esse fluxo ainda não é automatizado no RHI-Linux; escolha outro proxy.");

        try { _preferences.Set(game, new Rtx40MfgPreferences { GpuGeneration = normalizedGpu, ProxyName = requested }); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }
        return ComponentOperationResult.Ok($"Universal MFG: {normalizedGpu}, proxy {requested}.");
    }

    public static string NormalizeGpuGeneration(string? value)
        => string.Equals(value?.Trim(), GpuGenRtx30, StringComparison.OrdinalIgnoreCase)
            ? GpuGenRtx30
            : GpuGenRtx40;

    public static string NormalizeProxyName(string? value)
        => CanonicalProxyOrNull(value) ?? DefaultProxyName;

    public static bool IsGameEligible(DetectedGame game)
        => IsGameEligible(game, GpuGenRtx40, null);

    public static bool IsGameEligible(DetectedGame game, string? gpuGeneration)
        => IsGameEligible(game, gpuGeneration, null);

    public static bool IsGameEligible(DetectedGame game, string? gpuGeneration, LinuxDeploymentRecord? managedOptiScaler)
    {
        var gpu = NormalizeGpuGeneration(gpuGeneration);
        var apiEligible = gpu == GpuGenRtx30
            ? game.EffectiveGraphicsApi == GraphicsApi.DirectX12
            : game.EffectiveGraphicsApi is GraphicsApi.DirectX12 or GraphicsApi.Vulkan;

        return !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
               && game.HasConfirmedPatchTarget
               && ResolveArchitecture(game) == PeArchitecture.X64
               && apiEligible
               && !string.IsNullOrWhiteSpace(game.PrefixPath)
               && Directory.Exists(game.PrefixPath)
               && WineRegistryEditor.HasUserHive(game.PrefixPath)
               && ResolveBinaryDirectory(game) is not null
               && HasStreamlineFrameGeneration(game, managedOptiScaler);
    }

    public static string EligibilityReason(DetectedGame game)
        => EligibilityReason(game, GpuGenRtx40, null);

    public static string EligibilityReason(DetectedGame game, string? gpuGeneration)
        => EligibilityReason(game, gpuGeneration, null);

    public static string EligibilityReason(DetectedGame game, string? gpuGeneration, LinuxDeploymentRecord? managedOptiScaler)
    {
        var gpu = NormalizeGpuGeneration(gpuGeneration);
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "Universal MFG é um proxy DLL Windows e não se aplica a jogos Linux nativos.";
        if (!game.HasConfirmedPatchTarget)
            return "Selecione primeiro o executável real do jogo em Editar jogo.";
        if (ResolveArchitecture(game) != PeArchitecture.X64)
            return "Universal MFG exige um executável Windows x64.";
        if (gpu == GpuGenRtx30 && game.EffectiveGraphicsApi != GraphicsApi.DirectX12)
            return "Universal MFG em RTX 30 é experimental e exige DirectX 12; Vulkan não é habilitado para RTX 30 neste caminho.";
        if (gpu == GpuGenRtx40 && game.EffectiveGraphicsApi is not (GraphicsApi.DirectX12 or GraphicsApi.Vulkan))
            return "Universal MFG em RTX 40 usa DirectX 12 como caminho principal e Vulkan experimental; DirectX 11 não é elegível.";
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !WineRegistryEditor.HasUserHive(game.PrefixPath))
            return "Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o Universal MFG.";
        if (ResolveBinaryDirectory(game) is null)
            return "O diretório do executável alvo não pôde ser resolvido.";
        if (!HasStreamlineFrameGeneration(game, managedOptiScaler))
            return "Universal MFG exige Streamline DLSS Frame Generation funcional: integração nativa do jogo (Streamline + nvngx_dlssg.dll) ou a pilha OptiScaler Nightly/DLSS NR/F5 DLSSNR gerenciada pelo RHI com Streamline + DLSS Enabler e FG Output=DLSSG / FGNvngxReplacement=Arturs.";
        return $"Compatível com o caminho experimental Universal MFG: {gpu} + Streamline DLSS Frame Generation + prefixo Wine/Proton inicializado.";
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string? proxyName = null,
        CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("Universal MFG já é gerenciado pelo RHI para este jogo.");

        var preferences = GetPreferences(game);
        var selectedGpu = NormalizeGpuGeneration(preferences.GpuGeneration);
        var managedOptiScaler = _records.Find(game, ManagedComponentKind.OptiScaler);
        if (!IsGameEligible(game, selectedGpu, managedOptiScaler))
            return ComponentOperationResult.Fail(EligibilityReason(game, selectedGpu, managedOptiScaler));
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var conflict = FindFrameGenerationConflict(game);
        if (conflict is not null)
            return ComponentOperationResult.Fail($"{conflict} já está ativo. Remova esse método antes de instalar Universal MFG para manter uma única autoridade de Frame Generation.");

        var requested = proxyName ?? preferences.ProxyName;
        var selectedProxy = CanonicalProxyOrNull(requested);
        if (selectedProxy is null)
            return ComponentOperationResult.Fail($"Proxy não suportado pelo Universal MFG: {requested}.");
        if (IsBinkProxy(selectedProxy))
            return ComponentOperationResult.Fail("Proxy Bink requer preservar/renomear a DLL original para *Hooked.dll. O RHI-Linux ainda não automatiza esse chaining; escolha outro proxy.");

        // The preference file is written last, after the download, the Wine override and the
        // record: refuse here when it is unreadable instead of rolling all of that back (Refs #57).
        try { _preferences.EnsureWritable(); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }

        var binaryDir = ResolveBinaryDirectory(game)!;
        var targetPath = Path.Combine(binaryDir, selectedProxy);
        if (IsRhiManagedPath(game, targetPath))
            return ComponentOperationResult.Fail($"{selectedProxy} já pertence a outro componente gerenciado pelo RHI. Escolha outro proxy no botão ⚙.");
        if (FindExistingProxySlot(binaryDir, selectedProxy) is not null)
            return ComponentOperationResult.Fail($"{selectedProxy} já existe e é externo (inclusive considerando variações de maiúsculas/minúsculas). O RHI não sobrescreve nem adota uma DLL proxy existente; escolha outro nome.");

        Rtx40MfgReleaseInfo release;
        string payloadPath;
        try
        {
            release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            payloadPath = await EnsureCachedPayloadAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Universal MFG download/validation failed: {ex.Message}");
        }

        string? tempPath = null;
        WineOverrideRecord? wineOverride = null;

        // The Wine override is journaled before it is written (#23): a crash before the record is saved
        // leaves an entry start-up recovery rolls back, never an override nothing owns.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "Universal MFG install: " + game.Name, game, ManagedComponentKind.Rtx40MfgStandalone, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var operation = scope.Operation;
        // Downloaded and validated: from the first change on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            // Verified, fsynced copy beside the target (full re-read hash), placed without replacing anything.
            var staged = await AtomicFile.StageCopyAsync(payloadPath, targetPath, null, null, cancellationToken).ConfigureAwait(false);
            tempPath = staged.Path;
            ValidatePayload(tempPath);
            wineOverride = await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath!, selectedProxy, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
            // Tracked and declared first (#23): a crash before the record is saved removes it at the next start.
            await scope.PlaceStagedAsync(staged, targetPath, cancellationToken, overwrite: false).ConfigureAwait(false);

            var payloadHash = staged.Sha256;
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaGpuGeneration] = selectedGpu,
                [MetaProxyName] = selectedProxy,
                [LegacyMetaProxyName] = selectedProxy,
                [MetaTag] = release.Tag,
                [MetaAssetName] = release.AssetName,
                [MetaAssetSha256] = release.AssetSha256 ?? "",
                [MetaPayloadSha256] = payloadHash,
                [MetaExperimental] = "1",
                [MetaUpstream] = RepositoryUrl,
            };
            // Fail-closed source: every payload matched GitHub's digest or the publisher's SHA256SUMS.txt (#26).
            VerifiedDownload.Record(metadata, payloadHash, digestVerified: true);
            // The preference is saved before the commit point (review w3-journal #3): a failure here still rolls back
            // before the record exists, and nothing after the record can throw into the rollback.
            _preferences.Set(game, new Rtx40MfgPreferences { GpuGeneration = selectedGpu, ProxyName = selectedProxy });
            _records.Upsert(LinuxDeploymentRecovery.StampOperation(operation, new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.Rtx40MfgStandalone,
                BinaryDirectory = binaryDir,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = wineOverride,
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = targetPath,
                        ExistedBeforeInstall = false,
                        Sha256 = payloadHash,
                        AllowUserChanges = false
                    }
                ],
                Metadata = metadata
            }));
        }
        catch (Exception ex)
        {
            if (tempPath is not null) FileCleanup.TryDelete(tempPath);
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            _overrides.TryRestore(game.PrefixPath, wineOverride);
            return ComponentOperationResult.Fail($"Universal MFG install rolled back: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record carries the operation id: it is the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(
            $"Universal MFG {release.Version} instalado para {selectedGpu} como {selectedProxy}. Pressione Backspace no jogo para abrir o menu upstream; a compatibilidade sob Wine/Proton deve ser confirmada no jogo.",
            targetPath);
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Universal MFG não é gerenciado pelo RHI para este jogo. Instalações externas permanecem protegidas.");

        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName) ?? record.Metadata.GetValueOrDefault(LegacyMetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Registro do Universal MFG não possui o nome da DLL proxy; remoção bloqueada.");
        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, targetPath));
        if (fileRecord?.Sha256 is not { Length: > 0 })
            return ComponentOperationResult.Fail("Registro do Universal MFG perdeu o hash de ownership; remoção bloqueada.");

        if (File.Exists(targetPath))
        {
            var current = await FileHash.Sha256HexAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"A DLL gerenciada {proxyName} foi modificada fora do RHI. Remoção bloqueada antes de alterar o override Wine.");
        }

        // Journaled (#23): the record is stamped with the removal's id first; a crash before it is removed puts the proxy
        // and the override back as the record still describes them.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "Universal MFG removal: " + game.Name, game, ManagedComponentKind.Rtx40MfgStandalone, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;
        // Cancel is honoured only before the first change; from here the removal runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            await scope.DeleteAsync(targetPath, cancellationToken).ConfigureAwait(false);
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.Rtx40MfgStandalone);
        }
        catch (Exception ex)
        {
            // Files back from the scope's verified copies; RHI's value back only where this removal restored it (a later
            // value stays, #30); the record kept so the restored files stay owned.
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "universal-mfg").ConfigureAwait(false);
            if (overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return ComponentOperationResult.Fail($"Universal MFG removal failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record is gone: the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok("Universal MFG removido e o override Wine anterior foi restaurado."
            + WineDllOverrideService.KeptValueNotes(overrideRestore));
    }

    public async Task<Rtx40MfgUpdateStatus> CheckLatestAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            var record = GetRecord(game);
            var current = record?.Version;
            var hasUpdate = record is not null && !string.Equals(current, latest.Version, StringComparison.OrdinalIgnoreCase);
            return new(true, current, latest.Version, hasUpdate,
                record is null
                    ? $"Último Universal MFG upstream: {latest.Version}."
                    : hasUpdate
                        ? $"Atualização disponível: {current ?? "desconhecida"} → {latest.Version}."
                        : $"Universal MFG {current ?? latest.Version} está atualizado.");
        }
        catch (Exception ex)
        {
            return new(false, GetRecord(game)?.Version, null, false, $"Falha ao verificar Universal MFG: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Universal MFG não é gerenciado pelo RHI para este jogo.");

        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName) ?? record.Metadata.GetValueOrDefault(LegacyMetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Registro do Universal MFG não possui proxy; update bloqueado.");
        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, targetPath));
        if (fileRecord?.Sha256 is not { Length: > 0 })
            return ComponentOperationResult.Fail("Ownership hash do Universal MFG está ausente; update bloqueado.");
        if (!File.Exists(targetPath))
            return ComponentOperationResult.Fail("A DLL gerenciada do Universal MFG não existe mais; update não recriará um slot perdido silenciosamente.");

        var currentHash = await FileHash.Sha256HexAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("A DLL gerenciada do Universal MFG foi modificada externamente; update bloqueado.");

        Rtx40MfgReleaseInfo latest;
        string payloadPath;
        try
        {
            latest = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Ok($"Universal MFG {record.Version} já está atualizado.");
            payloadPath = await EnsureCachedPayloadAsync(latest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Universal MFG update check/download failed: {ex.Message}");
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "Universal MFG update: " + game.Name, game, ManagedComponentKind.Rtx40MfgStandalone, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        string? staged = null;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            var stagedCopy = await AtomicFile.StageCopyAsync(payloadPath, targetPath, null, null, cancellationToken).ConfigureAwait(false);
            staged = stagedCopy.Path;
            ValidatePayload(staged);
            // Tracked with a verified copy of the previous DLL first: a failure (or a crash before the record is saved)
            // puts it back.
            await scope.PlaceStagedAsync(stagedCopy, targetPath, cancellationToken).ConfigureAwait(false);
            var newHash = stagedCopy.Sha256;

            fileRecord.Sha256 = newHash;
            record.Version = latest.Version;
            record.SourceUrl = latest.AssetUrl;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[MetaTag] = latest.Tag;
            record.Metadata[MetaAssetName] = latest.AssetName;
            record.Metadata[MetaAssetSha256] = latest.AssetSha256 ?? "";
            record.Metadata[MetaPayloadSha256] = newHash;
            VerifiedDownload.Record(record.Metadata, newHash, digestVerified: true);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            if (staged is not null) FileCleanup.TryDelete(staged);
            // The previous DLL goes back from the scope's verified copy; a copy that cannot be put back is kept and named.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail($"Universal MFG update failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"Universal MFG atualizado para {latest.Version}; proxy {proxyName} preservado.", targetPath);
    }

    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return new ManagedUpdateCheck(false, false, "Universal MFG is not RHI-managed.");
        var status = await CheckLatestAsync(game, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(true, status.Success && status.HasUpdate, status.Message, status.InstalledVersion, status.LatestVersion,
            CouldNotCheck: !status.Success);
    }

    internal async Task<Rtx40MfgReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, ReleasesApiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("RTX40MFG latest release API returned an unexpected JSON shape.");

        var release = document.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("RTX40MFG latest release is a draft.");
        if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("RTX40MFG latest release is a prerelease; stable-only policy refused it.");

        var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidDataException("RTX40MFG latest release did not publish a tag.");
        var versionText = NormalizePublishedReleaseVersion(tag);

        string? assetName = null;
        string? assetUrl = null;
        string? assetSha = null;
        string? checksumsUrl = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = url;
                    continue;
                }
                if (!name.StartsWith("RTXMFG-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                assetName = name;
                assetUrl = url;
                var digest = asset.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    assetSha = digest["sha256:".Length..].Trim().ToLowerInvariant();
            }
        }

        if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(assetUrl))
            throw new InvalidDataException("RTX40MFG release contains no usable RTXMFG-*.zip asset.");
        if (string.IsNullOrWhiteSpace(assetSha) && string.IsNullOrWhiteSpace(checksumsUrl))
            throw new InvalidDataException("RTX40MFG release exposes no published SHA-256 authority (GitHub digest or SHA256SUMS.txt); deployment refused.");

        return new Rtx40MfgReleaseInfo(versionText, tag, assetName, assetUrl, assetSha, checksumsUrl);
    }

    /// <summary>
    /// GitHub's stable RTXMFG releases occasionally use a suffix such as
    /// <c>v1.3.3-hotfix.2</c>. The suffix is part of the published release identity and must not
    /// be collapsed to a numeric <see cref="Version"/> because doing so would make distinct
    /// hotfixes compare equal. Draft/prerelease policy is enforced from the GitHub release flags;
    /// this helper only validates that the opaque stable tag is safe to persist as a version/cache
    /// segment.
    /// </summary>
    internal static string NormalizePublishedReleaseVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        if (value.Length is < 3 or > 80)
            throw new InvalidDataException($"RTX40MFG release tag '{tag}' is not a supported stable version label.");

        // Require a dotted numeric version core, followed by an optional opaque suffix made only
        // of characters that are safe in cache directory names. GitHub's prerelease boolean,
        // checked by ResolveLatestReleaseAsync, remains the authority for stable-only policy.
        if (!Regex.IsMatch(value, @"^[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException($"RTX40MFG release tag '{tag}' is not a supported stable version label.");
        return value;
    }

    internal async Task<string> EnsureCachedPayloadAsync(Rtx40MfgReleaseInfo release, CancellationToken cancellationToken)
    {
        var versionDir = Path.Combine(_cacheRoot, SanitizePathSegment(release.Version));
        var dllPath = Path.Combine(versionDir, InnerDllName);
        Directory.CreateDirectory(versionDir);
        if (File.Exists(dllPath))
        {
            try
            {
                // Reuse only a DLL stamped at download time for this release asset (#142); a file
                // that reached the cache any other way is downloaded again, never deployed.
                if (!CacheStamp.IsValid(dllPath, release.AssetUrl, release.Version, release.AssetSha256))
                    throw new InvalidDataException("Cached payload has no valid download stamp.");
                ValidatePayload(dllPath);
                return dllPath;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                CacheStamp.Invalidate(dllPath);
                FileCleanup.TryDelete(dllPath);
            }
        }

        // Downloaded under the Universal MFG host policy and checked against GitHub's digest when published; this
        // source is fail-closed: ResolveLatestReleaseAsync refuses a release with neither that digest nor the
        // publisher's SHA256SUMS.txt, which is checked here as well (#26).
        using var download = await TrustedDownload.DownloadToTempAsync(
            _http, release.AssetUrl, Path.Combine(versionDir, release.AssetName), DownloadSources.UniversalMfg,
            release.AssetSha256, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(release.ChecksumsUrl))
        {
            var sums = await TrustedDownload.GetStringAsync(_http, release.ChecksumsUrl, DownloadSources.UniversalMfg, cancellationToken: cancellationToken).ConfigureAwait(false);
            var expected = TryFindChecksum(sums, release.AssetName);
            if (string.IsNullOrWhiteSpace(expected))
                throw new InvalidDataException($"SHA256SUMS.txt does not contain an entry for {release.AssetName}.");
            if (!string.Equals(download.Sha256, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RTX40MFG archive failed the upstream SHA256SUMS.txt verification.");
        }

        using (var zip = ZipFile.OpenRead(download.Path))
        {
            var entries = zip.Entries
                .Where(entry => entry.Name.Equals(InnerDllName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException(entries.Length == 0
                    ? $"{InnerDllName} was not found in the RTX40MFG archive."
                    : $"RTX40MFG archive contains multiple ambiguous {InnerDllName} entries.");

            // Checked extraction (declared-size cap, fixed mode, length + CRC-32, atomic rename); never ExtractToFile (Refs #42).
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entries[0], dllPath, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        try { ValidatePayload(dllPath); }
        catch
        {
            FileCleanup.TryDelete(dllPath);
            throw;
        }
        CacheStamp.Write(dllPath, release.AssetUrl, release.Version, release.AssetSha256);
        return dllPath;
    }

    internal static void ValidatePayload(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("RTX40MFG payload was not downloaded.", path);
        var length = new FileInfo(path).Length;
        if (length < MinimumDllSize)
            throw new InvalidDataException($"RTX40MFG payload is implausibly small ({length} bytes).");
        if (PeArchitectureDetector.Detect(path) != PeArchitecture.X64)
            throw new InvalidDataException("RTX40MFG standalone payload is not a Windows x64 PE DLL.");
    }

    internal static PeArchitecture ResolveArchitecture(DetectedGame game) => GameArchitecture.Of(game);

    internal static string? ResolveBinaryDirectory(DetectedGame game)
    {
        if (!string.IsNullOrWhiteSpace(game.BinaryDirectory) && Directory.Exists(game.BinaryDirectory))
            return Path.GetFullPath(game.BinaryDirectory);
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            return Path.GetDirectoryName(Path.GetFullPath(game.ExecutablePath));
        return null;
    }

    internal static bool HasStreamlineFrameGeneration(DetectedGame game)
        => HasStreamlineFrameGeneration(game, null);

    internal static bool HasStreamlineFrameGeneration(DetectedGame game, LinuxDeploymentRecord? managedOptiScaler)
    {
        var binaryDir = ResolveBinaryDirectory(game);
        if (binaryDir is not null && HasStreamlineFrameGeneration(binaryDir, 4))
            return true;

        if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath)
            && HasStreamlineFrameGeneration(game.InstallPath, 8))
            return true;

        return HasManagedOptiScalerFrameGeneration(managedOptiScaler);
    }

    internal static bool HasManagedOptiScalerFrameGeneration(LinuxDeploymentRecord? record)
    {
        if (record is null || !OptiScalerDeploymentService.HasManagedOptiScalerStreamlineFgFiles(record)) return false;
        var iniPath = Path.Combine(record.BinaryDirectory, "OptiScaler.ini");
        if (!File.Exists(iniPath)) return false;
        var fgOutput = OptiScalerIniEditor.GetValue(record.BinaryDirectory, "FrameGen", "FGOutput");
        var replacement = OptiScalerIniEditor.GetValue(record.BinaryDirectory, "FrameGen", "FGNvngxReplacement");
        return string.Equals(fgOutput, "dlssg", StringComparison.OrdinalIgnoreCase)
               && string.Equals(replacement, "Arturs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStreamlineFrameGeneration(string root, int maxDepth)
    {
        // The RHI-managed OptiScaler stack (OptiScaler/Streamline, incl. nvngx_dlssg.dll since #135)
        // is not the game's own integration; it counts only through HasManagedOptiScalerFrameGeneration.
        var names = GameFileIndexer.Build(root, maxDepth)
            .Where(path => !DlssManagerService.IsUnderOptiScalerFolder(root, path))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasDlssg = names.Contains("nvngx_dlssg.dll");
        var hasStreamline = names.Contains("sl.interposer.dll") || names.Contains("sl.common.dll") || names.Contains("sl.dlss_g.dll");
        return hasDlssg && hasStreamline;
    }

    internal static string? TryFindChecksum(string text, string assetName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(assetName)) return null;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var hash = parts[0].Trim();
            var fileName = parts[^1].Trim().TrimStart('*');
            if (!fileName.Equals(assetName, StringComparison.OrdinalIgnoreCase)) continue;
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) return hash.ToLowerInvariant();
        }
        return null;
    }

    private string? FindFrameGenerationConflict(DetectedGame game)
    {
        if (_records.Find(game, ManagedComponentKind.MfgAdaUnlock) is not null) return "MFG Ada Unlock";
        if (_records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null) return "RTX 20 FG Unlock legado";
        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null) return "ShortFuse";
        return null;
    }

    private bool IsRhiManagedPath(DetectedGame game, string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var record in _records.FindAll(game))
        {
            if (record.Component == ManagedComponentKind.Rtx40MfgStandalone) continue;
            if (record.Files.Any(file => ProxySlotsEqual(file.Path, full))) return true;
        }
        return false;
    }

    private static string? FindExistingProxySlot(string binaryDirectory, string proxyName)
    {
        try
        {
            return Directory.EnumerateFiles(binaryDirectory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(proxyName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            var exact = Path.Combine(binaryDirectory, proxyName);
            return File.Exists(exact) ? exact : null;
        }
    }

    private static bool ProxySlotsEqual(string left, string right)
    {
        try
        {
            var leftFull = Path.GetFullPath(left);
            var rightFull = Path.GetFullPath(right);
            return string.Equals(Path.GetDirectoryName(leftFull), Path.GetDirectoryName(rightFull), StringComparison.Ordinal)
                   && string.Equals(Path.GetFileName(leftFull), Path.GetFileName(rightFull), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? CanonicalProxyOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultProxyName;
        var name = Path.GetFileName(value.Trim());
        return ProxyNames.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBinkProxy(string value)
        => value.Equals("binkw64.dll", StringComparison.OrdinalIgnoreCase)
           || value.Equals("bink2w64.dll", StringComparison.OrdinalIgnoreCase);

    private static string SanitizePathSegment(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

    private static bool PathsEqual(string left, string right)
    {
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal); }
        catch { return string.Equals(left, right, StringComparison.Ordinal); }
    }
}

public sealed record Rtx40MfgReleaseInfo(
    string Version,
    string Tag,
    string AssetName,
    string AssetUrl,
    string? AssetSha256,
    string? ChecksumsUrl);

public sealed record Rtx40MfgUpdateStatus(
    bool Success,
    string? InstalledVersion,
    string? LatestVersion,
    bool HasUpdate,
    string Message);
