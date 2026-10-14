using System.Security.Cryptography;
using System.Text;
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
/// Legacy experimental Linux/Wine deployment for sdli1995/dlssg_for_sm86.
/// New RTX 30 installs are retired in favor of Universal MFG; this service remains
/// available for RTX 20 / SM75 and for safe removal/update of historical managed records.
/// The payload is a Windows x64 proxy DLL plus dlssg_sm86.ini from the repository tree (no releases).
/// As upstream RHI 2.8.5 (Dlssg20_30Service), the version is the newest commit that touched version.dll,
/// resolved at install/update time — it is not pinned to a fixed commit, so an update follows upstream's
/// pushes (#41). Unlike upstream (which reads <c>main</c>), both files are then fetched at that exact commit
/// SHA, so the bytes are immutable for the recorded version, and each must match the git blob id GitHub
/// reports for that path at that commit before it is cached (stamped) and deployed. PE/INI validation,
/// per-game Wine native,builtin override and hash guards as for the other proxies.
/// </summary>
public sealed class Dlssg2030UnlockDeploymentService
{
    public const string RepoOwner = "sdli1995";
    public const string RepoName = "dlssg_for_sm86";
    public const string RepositoryUrl = "https://github.com/sdli1995/dlssg_for_sm86";
    public const string CommitsApiUrl = "https://api.github.com/repos/sdli1995/dlssg_for_sm86/commits?path=version.dll&per_page=1";

    /// <summary>GitHub contents API for one file at one commit (its <c>sha</c> is the git blob id of the bytes).</summary>
    internal static string ContentsApiUrl(string commitSha, string fileName)
        => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/contents/{fileName}?ref={commitSha}";
    public const string IniFileName = "dlssg_sm86.ini";
    public const string DefaultProxyName = "version.dll";
    public const string GpuGenRtx30 = "RTX 30 Series";
    public const string GpuGenRtx20 = "RTX 20 Series";

    public static readonly IReadOnlyList<string> ProxyNames =
        ["version.dll", "winmm.dll", "dinput8.dll", "winhttp.dll", "dxgi.dll"];

    public static readonly IReadOnlyList<string> GpuGenerations = [GpuGenRtx20];

    private const string MetaProxyName = "FG2030.ProxyName";
    private const string MetaGpuGeneration = "FG2030.GpuGeneration";
    private const string MetaRouter = "FG2030.Router";
    private const string MetaCommitSha = "FG2030.CommitSha";
    private const string MetaSourceDllSha = "FG2030.SourceDllSha256";
    private const string MetaSourceIniSha = "FG2030.SourceIniSha256";
    private const string MetaExperimental = "FG2030.Experimental";
    private const int MinimumDllSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly DeploymentJournal? _journal;
    private readonly Dlssg2030PreferenceStore _preferences;

    /// <summary>The per-game store "Repair after move" carries over (Refs #38).</summary>
    internal Settings.IGameStateStore GameState => _preferences;
    private readonly string _cacheRoot;

    public Dlssg2030UnlockDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        Dlssg2030PreferenceStore? preferences = null,
        string? cacheRoot = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _overrides = overrides;
        _preferences = preferences ?? new Dlssg2030PreferenceStore();
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "dlssg2030");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.Dlssg2030Unlock);

    public Dlssg2030Preferences GetPreferences(DetectedGame game)
    {
        var record = GetRecord(game);
        if (record is not null)
        {
            return new Dlssg2030Preferences
            {
                GpuGeneration = record.Metadata.GetValueOrDefault(MetaGpuGeneration) ?? GpuGenRtx30,
                ProxyName = record.Metadata.GetValueOrDefault(MetaProxyName) ?? DefaultProxyName
            }.Normalize();
        }
        return _preferences.Get(game);
    }

    public ComponentOperationResult SetPreferences(DetectedGame game, string gpuGeneration, string proxyName)
    {
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está instalado. Remova e reinstale para trocar a DLL proxy.");

        if (string.Equals(gpuGeneration?.Trim(), GpuGenRtx30, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("RTX 30 agora usa Universal MFG. Abra as configurações do Universal MFG e selecione RTX 30 Series.");

        var normalizedGpu = NormalizeGpuGeneration(gpuGeneration);
        var normalizedProxy = NormalizeProxyName(proxyName);
        if (!GpuGenerations.Contains(normalizedGpu, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Geração de GPU inválida: {gpuGeneration}.");
        if (!ProxyNames.Contains(normalizedProxy, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Nome de proxy não suportado pelo RTX 20 FG Unlock legado: {proxyName}.");

        try
        {
            _preferences.Set(game, new Dlssg2030Preferences
            {
                GpuGeneration = normalizedGpu,
                ProxyName = normalizedProxy
            });
        }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }
        return ComponentOperationResult.Ok($"RTX 20 FG Unlock legado: {normalizedGpu}, proxy {normalizedProxy}.");
    }

    public static string NormalizeGpuGeneration(string? value)
        => string.Equals(value?.Trim(), GpuGenRtx30, StringComparison.OrdinalIgnoreCase)
            ? GpuGenRtx30
            : GpuGenRtx20;

    public static string NormalizeProxyName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? DefaultProxyName : Path.GetFileName(value.Trim());
        return ProxyNames.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? DefaultProxyName;
    }

    public static string RouterFor(string? gpuGeneration)
        => string.Equals(NormalizeGpuGeneration(gpuGeneration), GpuGenRtx20, StringComparison.OrdinalIgnoreCase)
            ? "SM75"
            : "SM86";

    public static bool IsGameEligible(DetectedGame game)
        => !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
           && game.HasConfirmedPatchTarget
           && ResolveArchitecture(game) == PeArchitecture.X64
           && game.EffectiveGraphicsApi == GraphicsApi.DirectX12
           && !string.IsNullOrWhiteSpace(game.PrefixPath)
           && Directory.Exists(game.PrefixPath)
           && WineRegistryEditor.HasUserHive(game.PrefixPath)
           && ResolveBinaryDirectory(game) is not null;

    public static string EligibilityReason(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "RTX 20 FG Unlock legado é um proxy DLL Windows e não se aplica a jogos Linux nativos.";
        if (!game.HasConfirmedPatchTarget)
            return "Selecione primeiro o executável real do jogo em Editar jogo.";
        if (ResolveArchitecture(game) != PeArchitecture.X64)
            return "RTX 20 FG Unlock legado exige um executável Windows x64.";
        if (game.EffectiveGraphicsApi != GraphicsApi.DirectX12)
            return "O caminho experimental RTX 20 / SM75 está habilitado somente para jogos DirectX 12.";
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !WineRegistryEditor.HasUserHive(game.PrefixPath))
            return "Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o RTX 20 FG Unlock legado.";
        if (ResolveBinaryDirectory(game) is null)
            return "O diretório do executável alvo não pôde ser resolvido.";
        return "Compatível com o caminho experimental: Windows x64 + DirectX 12 + prefixo Wine/Proton inicializado.";
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string? gpuGeneration = null,
        string? proxyName = null,
        CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já é gerenciado pelo RHI para este jogo.");

        var requestedGpu = gpuGeneration ?? GetPreferences(game).GpuGeneration;
        if (string.Equals(requestedGpu?.Trim(), GpuGenRtx30, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("RTX 30 agora usa Universal MFG. A rota SM86 antiga foi aposentada para novas instalações; use Universal MFG com RTX 30 Series.");

        if (!IsGameEligible(game))
            return ComponentOperationResult.Fail(EligibilityReason(game));
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        if (_records.Find(game, ManagedComponentKind.MfgAdaUnlock) is not null)
            return ComponentOperationResult.Fail("MFG Ada Unlock já está ativo. Remova-o antes de instalar o RTX 20 FG Unlock legado; os dois métodos de desbloqueio de Frame Generation não devem compartilhar o mesmo processo.");

        if (_records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG já está ativo. Remova-o antes de instalar o RTX 20 FG Unlock legado; os dois métodos não devem disputar o mesmo pipeline de Frame Generation.");

        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("ShortFuse já está ativo e gerencia a pilha DLSS/Frame Generation deste jogo. Remova-o antes de usar o RTX 20 FG Unlock legado para evitar duas autoridades sobre o mesmo runtime.");

        var preferences = GetPreferences(game);
        var selectedGpu = NormalizeGpuGeneration(requestedGpu);
        var selectedProxy = NormalizeProxyName(proxyName ?? preferences.ProxyName);
        if (!ProxyNames.Contains(selectedProxy, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Proxy não suportado: {selectedProxy}.");

        var binaryDir = ResolveBinaryDirectory(game)!;
        var proxyPath = Path.Combine(binaryDir, selectedProxy);
        var iniPath = Path.Combine(binaryDir, IniFileName);

        if (IsRhiManagedPath(game, proxyPath))
            return ComponentOperationResult.Fail($"{selectedProxy} já pertence a outro componente gerenciado pelo RHI. Escolha outro proxy no botão ⚙.");
        if (File.Exists(proxyPath))
            return ComponentOperationResult.Fail($"{selectedProxy} já existe e não pertence ao RTX 20 FG Unlock legado gerenciado pelo RHI. Escolha outro proxy; o RHI não sobrescreve uma DLL externa.");
        if (File.Exists(iniPath))
            return ComponentOperationResult.Fail($"{IniFileName} já existe e não é gerenciado pelo RHI. A instalação foi bloqueada para não adotar/sobrescrever uma configuração externa.");

        // The preference file is written last, after the download, the Wine override and the
        // record: refuse here when it is unreadable instead of rolling all of that back (Refs #57).
        try { _preferences.EnsureWritable(); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }

        Dlssg2030SnapshotInfo snapshot;
        Dlssg2030CachedPayload payload;
        try
        {
            snapshot = await ResolveLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
            payload = await EnsureCachedPayloadAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado download/validation failed: {ex.Message}");
        }

        var router = RouterFor(selectedGpu);
        var adjustedIni = WriteRouter(payload.IniText, router);
        string? proxyTemp = null;
        string? iniTemp = null;
        WineOverrideRecord? wineOverride = null;

        // The Wine override is journaled before it is written (#23): a crash before the record is saved
        // leaves an entry start-up recovery rolls back, never an override nothing owns.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RTX 20 FG Unlock install: " + game.Name, game, ManagedComponentKind.Dlssg2030Unlock, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var operation = scope.Operation;
        // Downloaded and validated: from the first change on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            wineOverride = await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath!, selectedProxy, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);

            // Verified, fsynced copies beside the targets (full re-read hash), placed without replacing anything.
            var stagedProxy = await AtomicFile.StageCopyAsync(payload.DllPath, proxyPath, null, null, cancellationToken).ConfigureAwait(false);
            proxyTemp = stagedProxy.Path;
            StagedFile stagedIni;
            using (var iniContent = new MemoryStream(new UTF8Encoding(false).GetBytes(adjustedIni)))
            {
                stagedIni = await AtomicFile.StageStreamAsync(iniContent, iniPath, null, null, long.MaxValue, cancellationToken).ConfigureAwait(false);
                iniTemp = stagedIni.Path;
            }

            ValidateCachedDll(proxyTemp);
            ValidateIniText(adjustedIni);

            // Tracked and declared first (#23): a crash before the record is saved removes both at the next start.
            await scope.PlaceStagedAsync(stagedProxy, proxyPath, cancellationToken, overwrite: false).ConfigureAwait(false);
            await scope.PlaceStagedAsync(stagedIni, iniPath, cancellationToken, overwrite: false).ConfigureAwait(false);

            var proxyHash = stagedProxy.Sha256;
            var iniHash = stagedIni.Sha256;
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaProxyName] = selectedProxy,
                [MetaGpuGeneration] = selectedGpu,
                [MetaRouter] = router,
                [MetaCommitSha] = snapshot.CommitSha,
                [MetaSourceDllSha] = payload.SourceDllSha256,
                [MetaSourceIniSha] = payload.SourceIniSha256,
                [MetaExperimental] = "1",
            };
            RecordDownloads(metadata, payload);

            // The preference is saved before the commit point (review w3-journal #3): a failure here still rolls back
            // before the record exists, and nothing after the record can throw into the rollback.
            _preferences.Set(game, new Dlssg2030Preferences { GpuGeneration = selectedGpu, ProxyName = selectedProxy });
            _records.Upsert(LinuxDeploymentRecovery.StampOperation(operation, new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.Dlssg2030Unlock,
                BinaryDirectory = binaryDir,
                SourceUrl = RepositoryUrl,
                Version = payload.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = wineOverride,
                Files =
                [
                    new LinuxDeployedFile { Path = proxyPath, ExistedBeforeInstall = false, Sha256 = proxyHash, AllowUserChanges = false },
                    new LinuxDeployedFile { Path = iniPath, ExistedBeforeInstall = false, Sha256 = iniHash, AllowUserChanges = true },
                ],
                Metadata = metadata
            }));
        }
        catch (Exception ex)
        {
            foreach (var leftover in FileCleanup.TryDeleteAll(new[] { proxyTemp, iniTemp }.OfType<string>()))
                AppLog.Warn("fg2030", $"Install rollback could not remove {leftover}");
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            _overrides.TryRestore(game.PrefixPath, wineOverride);
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado install rolled back: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record carries the operation id: it is the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(
            $"RTX 20 FG Unlock legado {payload.Version} instalado como {selectedProxy} · {selectedGpu} / {router}. O suporte sob VKD3D-Proton permanece experimental e deve ser confirmado em jogo real.",
            proxyPath, iniPath);
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado não é gerenciado pelo RHI para este jogo. A remoção externa permanece protegida.");

        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Registro do RTX 20 FG Unlock legado não possui o nome da DLL proxy; remoção bloqueada para proteger arquivos externos.");

        var proxyPath = Path.Combine(record.BinaryDirectory, proxyName);
        var iniPath = Path.Combine(record.BinaryDirectory, IniFileName);
        var proxyRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, proxyPath));
        var iniRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, iniPath));
        if (proxyRecord is null || string.IsNullOrWhiteSpace(proxyRecord.Sha256))
            return ComponentOperationResult.Fail("Registro do RTX 20 FG Unlock legado perdeu o hash da DLL gerenciada; remoção bloqueada.");

        if (File.Exists(proxyPath))
        {
            var currentHash = await FileHash.Sha256HexAsync(proxyPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, proxyRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"A DLL gerenciada {proxyName} foi modificada fora do RHI. Remoção bloqueada antes de alterar o override Wine.");
        }

        var preserveIni = false;
        if (File.Exists(iniPath) && iniRecord is not null && !string.IsNullOrWhiteSpace(iniRecord.Sha256))
        {
            var currentIniHash = await FileHash.Sha256HexAsync(iniPath, cancellationToken).ConfigureAwait(false);
            preserveIni = !string.Equals(currentIniHash, iniRecord.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        // Journaled (#23): the record is stamped with the removal's id first; a crash before it is removed puts the
        // files and the override back as the record still describes them.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "RTX 20 FG Unlock removal: " + game.Name, game, ManagedComponentKind.Dlssg2030Unlock, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;

        // Cancel is honoured only before the first change; from here the removal runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            await scope.DeleteAsync(proxyPath, cancellationToken).ConfigureAwait(false);
            if (!preserveIni) await scope.DeleteAsync(iniPath, cancellationToken).ConfigureAwait(false);
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.Dlssg2030Unlock);
        }
        catch (Exception ex)
        {
            // Files back from the scope's verified copies; RHI's value back only where this removal restored it (a later
            // value stays, #30); the record kept so the restored files stay owned.
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "fg2030").ConfigureAwait(false);
            if (overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado removal failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record is gone: the commit point.
        await scope.CommitAsync().ConfigureAwait(false);

        return ComponentOperationResult.Ok((preserveIni
            ? $"RTX 20 FG Unlock legado removido e override Wine restaurado. {IniFileName} foi preservado porque foi alterado pelo usuário/outro mod."
            : "RTX 20 FG Unlock legado removido; DLL, INI gerenciado e override Wine foram limpos.")
            + WineDllOverrideService.KeptValueNotes(overrideRestore));
    }

    public async Task<Dlssg2030UpdateStatus> CheckLatestAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await ResolveLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var record = GetRecord(game);
            var currentCommit = record?.Metadata.GetValueOrDefault(MetaCommitSha);
            var hasUpdate = record is not null && !string.Equals(currentCommit, snapshot.CommitSha, StringComparison.OrdinalIgnoreCase);
            return new(true, record?.Version, snapshot.Version, hasUpdate,
                record is null
                    ? $"Última versão upstream: {snapshot.Version} ({ShortSha(snapshot.CommitSha)})."
                    : hasUpdate
                        ? $"Atualização disponível: {record.Version ?? ShortSha(currentCommit)} → {snapshot.Version} ({ShortSha(snapshot.CommitSha)})."
                        : $"RTX 20 FG Unlock legado {record.Version ?? snapshot.Version} está no snapshot atual.");
        }
        catch (Exception ex)
        {
            return new(false, GetRecord(game)?.Version, null, false, $"Falha ao verificar RTX 20 FG Unlock legado: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado não está instalado pelo RHI.");

        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Registro do RTX 20 FG Unlock legado está sem proxy; remova e reinstale em vez de adotar o estado.");
        var proxyPath = Path.Combine(record.BinaryDirectory, proxyName);
        var iniPath = Path.Combine(record.BinaryDirectory, IniFileName);
        var proxyRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, proxyPath));
        if (proxyRecord is null || string.IsNullOrWhiteSpace(proxyRecord.Sha256) || !File.Exists(proxyPath))
            return ComponentOperationResult.Fail("A DLL gerenciada do RTX 20 FG Unlock legado está ausente ou sem hash. Remova/reinstale para recuperar com segurança.");
        if (!File.Exists(iniPath))
            return ComponentOperationResult.Fail($"{IniFileName} foi removido externamente. Atualização bloqueada; use remover/reinstalar para não recriar configuração apagada pelo usuário.");

        var currentHash = await FileHash.Sha256HexAsync(proxyPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, proxyRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"A DLL gerenciada {proxyName} foi modificada fora do RHI. Atualização bloqueada.");

        Dlssg2030SnapshotInfo snapshot;
        Dlssg2030CachedPayload payload;
        try
        {
            snapshot = await ResolveLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(record.Metadata.GetValueOrDefault(MetaCommitSha), snapshot.CommitSha, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Ok($"RTX 20 FG Unlock legado {record.Version ?? snapshot.Version} já usa o snapshot upstream atual.");
            payload = await EnsureCachedPayloadAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado update download/validation failed: {ex.Message}");
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RTX 20 FG Unlock update: " + game.Name, game, ManagedComponentKind.Dlssg2030Unlock, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        string? staged = null;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            var stagedCopy = await AtomicFile.StageCopyAsync(payload.DllPath, proxyPath, null, null, cancellationToken).ConfigureAwait(false);
            staged = stagedCopy.Path;
            ValidateCachedDll(staged);
            var newHash = stagedCopy.Sha256;
            // Tracked with a verified copy of the previous DLL first: a failure (or a crash before the record is saved)
            // puts it back.
            await scope.PlaceStagedAsync(stagedCopy, proxyPath, cancellationToken).ConfigureAwait(false);

            proxyRecord.Sha256 = newHash;
            record.Version = payload.Version;
            record.SourceUrl = RepositoryUrl;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[MetaCommitSha] = snapshot.CommitSha;
            record.Metadata[MetaSourceDllSha] = payload.SourceDllSha256;
            record.Metadata[MetaSourceIniSha] = payload.SourceIniSha256;
            RecordDownloads(record.Metadata, payload);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            if (staged is not null) FileCleanup.TryDelete(staged);
            // The previous DLL goes back from the scope's verified copy; a copy that cannot be put back is kept and named.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado update failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"RTX 20 FG Unlock legado atualizado para {payload.Version} ({ShortSha(snapshot.CommitSha)}); {IniFileName} do usuário foi preservado.");
    }

    internal async Task<Dlssg2030SnapshotInfo> ResolveLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, CommitsApiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined || !first.TryGetProperty("sha", out var shaElement))
            throw new InvalidDataException("Upstream dlssg_for_sm86 commit response did not contain a SHA.");
        var sha = shaElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(sha) || sha.Length < 7 || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException("Upstream dlssg_for_sm86 returned an invalid commit SHA.");
        return new Dlssg2030SnapshotInfo(sha, ShortSha(sha));
    }

    internal async Task<Dlssg2030CachedPayload> EnsureCachedPayloadAsync(
        Dlssg2030SnapshotInfo snapshot,
        CancellationToken cancellationToken)
    {
        var versionDir = Path.Combine(_cacheRoot, snapshot.CommitSha);
        var dllPath = Path.Combine(versionDir, DefaultProxyName);
        var iniPath = Path.Combine(versionDir, IniFileName);
        Directory.CreateDirectory(versionDir);

        var dllUrl = RawUrl(snapshot.CommitSha, DefaultProxyName);
        var iniUrl = RawUrl(snapshot.CommitSha, IniFileName);
        // Reuse the pair only when both files were stamped by this service's download of this commit
        // (#142); a file that reached the cache any other way is downloaded again, never deployed.
        // TryValidate hands back the hashes it just re-verified, so the pair is not hashed twice on reuse.
        if (!CacheStamp.TryValidate(dllPath, dllUrl, snapshot.CommitSha, null, out var sourceDllHash) ||
            !CacheStamp.TryValidate(iniPath, iniUrl, snapshot.CommitSha, null, out var sourceIniHash))
        {
            CacheStamp.Invalidate(dllPath);
            CacheStamp.Invalidate(iniPath);
            var dllBlob = await ResolveBlobShaAsync(snapshot.CommitSha, DefaultProxyName, cancellationToken).ConfigureAwait(false);
            var iniBlob = await ResolveBlobShaAsync(snapshot.CommitSha, IniFileName, cancellationToken).ConfigureAwait(false);
            using var dll = await TrustedDownload.DownloadToTempAsync(_http, dllUrl, dllPath, DownloadSources.Dlssg2030, cancellationToken: cancellationToken).ConfigureAwait(false);
            using var ini = await TrustedDownload.DownloadToTempAsync(_http, iniUrl, iniPath, DownloadSources.Dlssg2030, cancellationToken: cancellationToken).ConfigureAwait(false);
            VerifyBlob(dll.Path, dllBlob, DefaultProxyName);
            VerifyBlob(ini.Path, iniBlob, IniFileName);
            ValidateCachedDll(dll.Path);
            var iniText = await File.ReadAllTextAsync(ini.Path, cancellationToken).ConfigureAwait(false);
            ValidateIniText(iniText);
            dll.MoveTo(dllPath);
            ini.MoveTo(iniPath);
            sourceDllHash = CacheStamp.Write(dllPath, dllUrl, snapshot.CommitSha);
            sourceIniHash = CacheStamp.Write(iniPath, iniUrl, snapshot.CommitSha);
        }

        ValidateCachedDll(dllPath);
        var text = await File.ReadAllTextAsync(iniPath, cancellationToken).ConfigureAwait(false);
        ValidateIniText(text);
        var version = ParseVersionFromIni(text) ?? snapshot.Version;
        return new Dlssg2030CachedPayload(dllPath, iniPath, text, sourceDllHash.ToLowerInvariant(), sourceIniHash.ToLowerInvariant(), version);
    }

    /// <summary>
    /// The downloads the record comes from (#26): the DLL, and the template INI as part <c>Ini</c>. Both bytes were matched
    /// against the git blob id GitHub publishes for them at the commit (fail-closed), so both count as published-digest.
    /// </summary>
    private static void RecordDownloads(IDictionary<string, string> metadata, Dlssg2030CachedPayload payload)
    {
        VerifiedDownload.Record(metadata, payload.SourceDllSha256.ToUpperInvariant(), digestVerified: true);
        VerifiedDownload.Record(metadata, payload.SourceIniSha256.ToUpperInvariant(), digestVerified: true, part: "Ini");
    }

    /// <summary>Fixed start of the refusal of a file whose bytes are not the commit's blob (PT-BR pair); the file name follows.</summary>
    public const string BlobMismatchPrefix = "Download refused: the file is not the git blob GitHub reports for this commit: ";

    /// <summary>Fixed start of a missing blob id (PT-BR pair); file@commit follows.</summary>
    public const string BlobMissingPrefix = "GitHub reported no git blob id for this file: ";

    /// <summary>The git blob id GitHub reports for <paramref name="fileName"/> at <paramref name="commitSha"/> (contents API).</summary>
    private async Task<string> ResolveBlobShaAsync(string commitSha, string fileName, CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, ContentsApiUrl(commitSha, fileName), DownloadSources.GitHubApi,
            "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var sha = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("sha", out var shaElement)
            ? shaElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 40 || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{BlobMissingPrefix}{fileName}@{ShortSha(commitSha)}");
        return sha;
    }

    private static void VerifyBlob(string path, string expectedBlobSha, string fileName)
    {
        if (!string.Equals(GitBlobSha1(path), expectedBlobSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(BlobMismatchPrefix + fileName);
    }

    /// <summary>
    /// Git's object id for a file's bytes: SHA-1 over <c>"blob &lt;length&gt;\0"</c> followed by the content. Used only
    /// to match a download against the id GitHub publishes for it, not as a security hash on its own.
    /// </summary>
    internal static string GitBlobSha1(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(Encoding.ASCII.GetBytes($"blob {stream.Length}\0"));
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) sha1.AppendData(buffer, 0, read);
        return Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string RawUrl(string commitSha, string fileName)
        => $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{commitSha}/{fileName}";

    internal static string? ParseVersionFromIni(string text)
    {
        var match = Regex.Match(text, @"(?im)^\s*;\s*Native\s+([0-9]+(?:\.[0-9A-Za-z-]+)+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string WriteRouter(string text, string router)
    {
        router = string.Equals(router, "SM75", StringComparison.OrdinalIgnoreCase) ? "SM75" : "SM86";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var compatibility = -1;
        var sectionEnd = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal)) continue;
            if (compatibility >= 0) { sectionEnd = i; break; }
            if (trimmed.Equals("[Compatibility]", StringComparison.OrdinalIgnoreCase)) compatibility = i;
        }
        if (compatibility < 0)
            throw new InvalidDataException($"{IniFileName} does not contain [Compatibility].");

        for (var i = compatibility + 1; i < sectionEnd; i++)
        {
            if (!lines[i].TrimStart().StartsWith("Router=", StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = $"Router={router}";
            return string.Join(newline, lines);
        }

        lines.Insert(compatibility + 1, $"Router={router}");
        return string.Join(newline, lines);
    }

    internal static void ValidateIniText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 * 1024)
            throw new InvalidDataException($"{IniFileName} is empty or implausibly large.");
        if (!text.Contains("[Compatibility]", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("Router=", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("[FrameGeneration]", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("MaxGeneratedFrames=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{IniFileName} does not match the expected upstream contract.");
    }

    internal static void ValidateCachedDll(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("RTX 20 FG Unlock legado DLL was not downloaded.", path);
        var info = new FileInfo(path);
        if (info.Length < MinimumDllSize)
            throw new InvalidDataException($"RTX 20 FG Unlock legado DLL is implausibly small ({info.Length} bytes). ");
        if (PeArchitectureDetector.Detect(path) != PeArchitecture.X64)
            throw new InvalidDataException("RTX 20 FG Unlock legado payload is not a Windows x64 PE DLL.");
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

    private bool IsRhiManagedPath(DetectedGame game, string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var record in _records.FindAll(game))
        {
            if (record.Component == ManagedComponentKind.Dlssg2030Unlock) continue;
            if (record.Files.Any(file => PathsEqual(file.Path, full))) return true;
        }
        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal); }
        catch { return string.Equals(left, right, StringComparison.Ordinal); }
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "unknown" : sha.Length > 7 ? sha[..7] : sha;
}

public sealed record Dlssg2030SnapshotInfo(string CommitSha, string Version);

internal sealed record Dlssg2030CachedPayload(
    string DllPath,
    string IniPath,
    string IniText,
    string SourceDllSha256,
    string SourceIniSha256,
    string Version);

public sealed record Dlssg2030UpdateStatus(
    bool Success,
    string? InstalledVersion,
    string? LatestVersion,
    bool HasUpdate,
    string Message);
