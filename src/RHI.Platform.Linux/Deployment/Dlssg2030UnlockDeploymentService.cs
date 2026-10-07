using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Legacy experimental Linux/Wine deployment for sdli1995/dlssg_for_sm86.
/// New RTX 30 installs are retired in favor of Universal MFG; this service remains
/// available for RTX 20 / SM75 and for safe removal/update of historical managed records.
/// The payload is a Windows x64 proxy DLL plus dlssg_sm86.ini pinned to an immutable
/// Git commit with PE validation, per-game Wine native,builtin override and hash guards.
/// </summary>
public sealed class Dlssg2030UnlockDeploymentService
{
    public const string RepoOwner = "sdli1995";
    public const string RepoName = "dlssg_for_sm86";
    public const string RepositoryUrl = "https://github.com/sdli1995/dlssg_for_sm86";
    public const string CommitsApiUrl = "https://api.github.com/repos/sdli1995/dlssg_for_sm86/commits?path=version.dll&per_page=1";
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
    private readonly Dlssg2030PreferenceStore _preferences;
    private readonly string _cacheRoot;

    public Dlssg2030UnlockDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        Dlssg2030PreferenceStore? preferences = null,
        string? cacheRoot = null)
    {
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

        _preferences.Set(game, new Dlssg2030Preferences
        {
            GpuGeneration = normalizedGpu,
            ProxyName = normalizedProxy
        });
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
           && File.Exists(Path.Combine(game.PrefixPath, "user.reg"))
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
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !File.Exists(Path.Combine(game.PrefixPath, "user.reg")))
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
        var proxyTemp = proxyPath + $".rhi-install-{Guid.NewGuid():N}";
        var iniTemp = iniPath + $".rhi-install-{Guid.NewGuid():N}";
        WineOverrideRecord? wineOverride = null;
        var proxyMoved = false;
        var iniMoved = false;
        var recordWritten = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            wineOverride = _overrides.EnsureNativeBuiltin(game.PrefixPath!, selectedProxy, Path.GetFileName(game.ExecutablePath));

            File.Copy(payload.DllPath, proxyTemp, overwrite: false);
            await File.WriteAllTextAsync(iniTemp, adjustedIni, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            ValidateCachedDll(proxyTemp);
            ValidateIniText(adjustedIni);

            File.Move(proxyTemp, proxyPath);
            proxyMoved = true;
            File.Move(iniTemp, iniPath);
            iniMoved = true;

            var proxyHash = await Sha256FileAsync(proxyPath, cancellationToken).ConfigureAwait(false);
            var iniHash = await Sha256FileAsync(iniPath, cancellationToken).ConfigureAwait(false);

            _records.Upsert(new LinuxDeploymentRecord
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
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [MetaProxyName] = selectedProxy,
                    [MetaGpuGeneration] = selectedGpu,
                    [MetaRouter] = router,
                    [MetaCommitSha] = snapshot.CommitSha,
                    [MetaSourceDllSha] = payload.SourceDllSha256,
                    [MetaSourceIniSha] = payload.SourceIniSha256,
                    [MetaExperimental] = "1",
                }
            });
            recordWritten = true;

            _preferences.Set(game, new Dlssg2030Preferences { GpuGeneration = selectedGpu, ProxyName = selectedProxy });
            return ComponentOperationResult.Ok(
                $"RTX 20 FG Unlock legado {payload.Version} instalado como {selectedProxy} · {selectedGpu} / {router}. O suporte sob VKD3D-Proton permanece experimental e deve ser confirmado em jogo real.",
                proxyPath, iniPath);
        }
        catch (Exception ex)
        {
            try { if (proxyMoved && File.Exists(proxyPath)) File.Delete(proxyPath); } catch { }
            try { if (iniMoved && File.Exists(iniPath)) File.Delete(iniPath); } catch { }
            try { if (File.Exists(proxyTemp)) File.Delete(proxyTemp); } catch { }
            try { if (File.Exists(iniTemp)) File.Delete(iniTemp); } catch { }
            try { if (wineOverride is not null) _overrides.Restore(game.PrefixPath!, wineOverride); } catch { }
            try { if (recordWritten) _records.Remove(game, ManagedComponentKind.Dlssg2030Unlock); } catch { }
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado install rolled back: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado não é gerenciado pelo RHI para este jogo. A remoção externa permanece protegida.");

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
            var currentHash = await Sha256FileAsync(proxyPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, proxyRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"A DLL gerenciada {proxyName} foi modificada fora do RHI. Remoção bloqueada antes de alterar o override Wine.");
        }

        var preserveIni = false;
        if (File.Exists(iniPath) && iniRecord is not null && !string.IsNullOrWhiteSpace(iniRecord.Sha256))
        {
            var currentIniHash = await Sha256FileAsync(iniPath, cancellationToken).ConfigureAwait(false);
            preserveIni = !string.Equals(currentIniHash, iniRecord.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        var proxyTemp = proxyPath + $".rhi-remove-{Guid.NewGuid():N}";
        var iniTemp = iniPath + $".rhi-remove-{Guid.NewGuid():N}";
        var proxyMoved = false;
        var iniMoved = false;
        var overrideRestored = false;

        try
        {
            if (File.Exists(proxyPath))
            {
                File.Move(proxyPath, proxyTemp);
                proxyMoved = true;
            }
            if (!preserveIni && File.Exists(iniPath))
            {
                File.Move(iniPath, iniTemp);
                iniMoved = true;
            }

            _overrides.Restore(game.PrefixPath!, record.WineOverride);
            overrideRestored = true;
            _records.Remove(game, ManagedComponentKind.Dlssg2030Unlock);

            if (proxyMoved && File.Exists(proxyTemp)) File.Delete(proxyTemp);
            if (iniMoved && File.Exists(iniTemp)) File.Delete(iniTemp);

            return ComponentOperationResult.Ok(preserveIni
                ? $"RTX 20 FG Unlock legado removido e override Wine restaurado. {IniFileName} foi preservado porque foi alterado pelo usuário/outro mod."
                : "RTX 20 FG Unlock legado removido; DLL, INI gerenciado e override Wine foram limpos.");
        }
        catch (Exception ex)
        {
            try
            {
                if (proxyMoved && File.Exists(proxyTemp) && !File.Exists(proxyPath)) File.Move(proxyTemp, proxyPath);
                if (iniMoved && File.Exists(iniTemp) && !File.Exists(iniPath)) File.Move(iniTemp, iniPath);
                if (overrideRestored && record.WineOverride is not null)
                    _ = _overrides.EnsureNativeBuiltin(game.PrefixPath!, proxyName, record.WineOverride.ApplicationName ?? Path.GetFileName(game.ExecutablePath));
                _records.Upsert(record);
            }
            catch { }
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado removal failed and was rolled back as far as possible: {ex.Message}");
        }
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

        var currentHash = await Sha256FileAsync(proxyPath, cancellationToken).ConfigureAwait(false);
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

        var staged = proxyPath + $".rhi-update-new-{Guid.NewGuid():N}";
        var previous = proxyPath + $".rhi-update-old-{Guid.NewGuid():N}";
        try
        {
            File.Copy(payload.DllPath, staged, overwrite: false);
            ValidateCachedDll(staged);
            var newHash = await Sha256FileAsync(staged, cancellationToken).ConfigureAwait(false);
            File.Move(proxyPath, previous);
            File.Move(staged, proxyPath);

            proxyRecord.Sha256 = newHash;
            record.Version = payload.Version;
            record.SourceUrl = RepositoryUrl;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[MetaCommitSha] = snapshot.CommitSha;
            record.Metadata[MetaSourceDllSha] = payload.SourceDllSha256;
            record.Metadata[MetaSourceIniSha] = payload.SourceIniSha256;
            _records.Upsert(record);

            File.Delete(previous);
            return ComponentOperationResult.Ok($"RTX 20 FG Unlock legado atualizado para {payload.Version} ({ShortSha(snapshot.CommitSha)}); {IniFileName} do usuário foi preservado.");
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(staged)) File.Delete(staged);
                if (File.Exists(previous))
                {
                    if (File.Exists(proxyPath)) File.Delete(proxyPath);
                    File.Move(previous, proxyPath);
                }
            }
            catch { }
            return ComponentOperationResult.Fail($"RTX 20 FG Unlock legado update failed and was rolled back as far as possible: {ex.Message}");
        }
    }

    internal async Task<Dlssg2030SnapshotInfo> ResolveLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CommitsApiUrl);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            var dllTemp = dllPath + $".tmp-{Guid.NewGuid():N}";
            var iniTemp = iniPath + $".tmp-{Guid.NewGuid():N}";
            try
            {
                await DownloadToFileAsync(dllUrl, dllTemp, cancellationToken).ConfigureAwait(false);
                await DownloadToFileAsync(iniUrl, iniTemp, cancellationToken).ConfigureAwait(false);
                ValidateCachedDll(dllTemp);
                var iniText = await File.ReadAllTextAsync(iniTemp, cancellationToken).ConfigureAwait(false);
                ValidateIniText(iniText);
                File.Move(dllTemp, dllPath, overwrite: true);
                File.Move(iniTemp, iniPath, overwrite: true);
                sourceDllHash = CacheStamp.Write(dllPath, dllUrl, snapshot.CommitSha);
                sourceIniHash = CacheStamp.Write(iniPath, iniUrl, snapshot.CommitSha);
            }
            finally
            {
                TryDelete(dllTemp);
                TryDelete(iniTemp);
            }
        }

        ValidateCachedDll(dllPath);
        var text = await File.ReadAllTextAsync(iniPath, cancellationToken).ConfigureAwait(false);
        ValidateIniText(text);
        var version = ParseVersionFromIni(text) ?? snapshot.Version;
        return new Dlssg2030CachedPayload(dllPath, iniPath, text, sourceDllHash.ToLowerInvariant(), sourceIniHash.ToLowerInvariant(), version);
    }

    private async Task DownloadToFileAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
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

    internal static PeArchitecture ResolveArchitecture(DetectedGame game)
    {
        if (game.ForcedArchitecture is PeArchitecture.X86 or PeArchitecture.X64)
            return game.ForcedArchitecture.Value;
        return PeArchitectureDetector.Detect(game.ExecutablePath);
    }

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

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "unknown" : sha.Length > 7 ? sha[..7] : sha;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
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
