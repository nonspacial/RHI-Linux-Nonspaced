using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

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
    private readonly Rtx40MfgPreferenceStore _preferences;
    private readonly string _cacheRoot;

    public Rtx40MfgStandaloneDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        Rtx40MfgPreferenceStore? preferences = null,
        string? cacheRoot = null)
    {
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

        _preferences.Set(game, new Rtx40MfgPreferences { GpuGeneration = normalizedGpu, ProxyName = requested });
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
               && File.Exists(Path.Combine(game.PrefixPath, "user.reg"))
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
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !File.Exists(Path.Combine(game.PrefixPath, "user.reg")))
            return "Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o Universal MFG.";
        if (ResolveBinaryDirectory(game) is null)
            return "O diretório do executável alvo não pôde ser resolvido.";
        if (!HasStreamlineFrameGeneration(game, managedOptiScaler))
            return "Universal MFG exige Streamline DLSS Frame Generation funcional: integração nativa do jogo (Streamline + nvngx_dlssg.dll) ou a pilha OptiScaler Nightly/DLSS NR gerenciada pelo RHI com Streamline + DLSS Enabler e FG Output=DLSSG / FGNvngxReplacement=Arturs.";
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

        var conflict = FindFrameGenerationConflict(game);
        if (conflict is not null)
            return ComponentOperationResult.Fail($"{conflict} já está ativo. Remova esse método antes de instalar Universal MFG para manter uma única autoridade de Frame Generation.");

        var requested = proxyName ?? preferences.ProxyName;
        var selectedProxy = CanonicalProxyOrNull(requested);
        if (selectedProxy is null)
            return ComponentOperationResult.Fail($"Proxy não suportado pelo Universal MFG: {requested}.");
        if (IsBinkProxy(selectedProxy))
            return ComponentOperationResult.Fail("Proxy Bink requer preservar/renomear a DLL original para *Hooked.dll. O RHI-Linux ainda não automatiza esse chaining; escolha outro proxy.");

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

        var tempPath = targetPath + $".rhi-install-{Guid.NewGuid():N}";
        WineOverrideRecord? wineOverride = null;
        var moved = false;
        var recordWritten = false;
        try
        {
            File.Copy(payloadPath, tempPath, overwrite: false);
            ValidatePayload(tempPath);
            wineOverride = _overrides.EnsureNativeBuiltin(game.PrefixPath!, selectedProxy, Path.GetFileName(game.ExecutablePath));
            File.Move(tempPath, targetPath);
            moved = true;

            var payloadHash = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);
            _records.Upsert(new LinuxDeploymentRecord
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
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                }
            });
            recordWritten = true;
            _preferences.Set(game, new Rtx40MfgPreferences { GpuGeneration = selectedGpu, ProxyName = selectedProxy });

            return ComponentOperationResult.Ok(
                $"Universal MFG {release.Version} instalado para {selectedGpu} como {selectedProxy}. Pressione Backspace no jogo para abrir o menu upstream; a compatibilidade sob Wine/Proton deve ser confirmada no jogo.",
                targetPath);
        }
        catch (Exception ex)
        {
            try { if (moved && File.Exists(targetPath)) File.Delete(targetPath); } catch { }
            TryDelete(tempPath);
            try { if (wineOverride is not null) _overrides.Restore(game.PrefixPath!, wineOverride); } catch { }
            try { if (recordWritten) _records.Remove(game, ManagedComponentKind.Rtx40MfgStandalone); } catch { }
            return ComponentOperationResult.Fail($"Universal MFG install rolled back: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Universal MFG não é gerenciado pelo RHI para este jogo. Instalações externas permanecem protegidas.");

        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName) ?? record.Metadata.GetValueOrDefault(LegacyMetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Registro do Universal MFG não possui o nome da DLL proxy; remoção bloqueada.");
        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, targetPath));
        if (fileRecord?.Sha256 is not { Length: > 0 })
            return ComponentOperationResult.Fail("Registro do Universal MFG perdeu o hash de ownership; remoção bloqueada.");

        if (File.Exists(targetPath))
        {
            var current = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"A DLL gerenciada {proxyName} foi modificada fora do RHI. Remoção bloqueada antes de alterar o override Wine.");
        }

        var tempPath = targetPath + $".rhi-remove-{Guid.NewGuid():N}";
        var moved = false;
        var overrideRestored = false;
        try
        {
            if (File.Exists(targetPath))
            {
                File.Move(targetPath, tempPath);
                moved = true;
            }
            _overrides.Restore(game.PrefixPath!, record.WineOverride);
            overrideRestored = true;
            _records.Remove(game, ManagedComponentKind.Rtx40MfgStandalone);
            TryDelete(tempPath);
            return ComponentOperationResult.Ok("Universal MFG removido e o override Wine anterior foi restaurado.");
        }
        catch (Exception ex)
        {
            try
            {
                if (moved && File.Exists(tempPath) && !File.Exists(targetPath)) File.Move(tempPath, targetPath);
                if (overrideRestored && record.WineOverride is not null)
                    _ = _overrides.EnsureNativeBuiltin(game.PrefixPath!, proxyName, record.WineOverride.ApplicationName ?? Path.GetFileName(game.ExecutablePath));
                _records.Upsert(record);
            }
            catch { }
            return ComponentOperationResult.Fail($"Universal MFG removal failed and was rolled back as far as possible: {ex.Message}");
        }
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

        var currentHash = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);
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

        var staged = targetPath + $".rhi-update-{Guid.NewGuid():N}";
        var previous = targetPath + $".rhi-previous-{Guid.NewGuid():N}";
        try
        {
            File.Copy(payloadPath, staged, overwrite: false);
            ValidatePayload(staged);
            File.Move(targetPath, previous);
            File.Move(staged, targetPath);
            var newHash = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);

            fileRecord.Sha256 = newHash;
            record.Version = latest.Version;
            record.SourceUrl = latest.AssetUrl;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[MetaTag] = latest.Tag;
            record.Metadata[MetaAssetName] = latest.AssetName;
            record.Metadata[MetaAssetSha256] = latest.AssetSha256 ?? "";
            record.Metadata[MetaPayloadSha256] = newHash;
            _records.Upsert(record);
            TryDelete(previous);
            return ComponentOperationResult.Ok($"Universal MFG atualizado para {latest.Version}; proxy {proxyName} preservado.", targetPath);
        }
        catch (Exception ex)
        {
            try
            {
                TryDelete(staged);
                if (File.Exists(previous))
                {
                    TryDelete(targetPath);
                    File.Move(previous, targetPath);
                }
            }
            catch { }
            return ComponentOperationResult.Fail($"Universal MFG update failed and was rolled back as far as possible: {ex.Message}");
        }
    }

    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return new ManagedUpdateCheck(false, false, "Universal MFG is not RHI-managed.");
        var status = await CheckLatestAsync(game, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(true, status.Success && status.HasUpdate, status.Message, status.InstalledVersion, status.LatestVersion);
    }

    internal async Task<Rtx40MfgReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            catch
            {
                CacheStamp.Invalidate(dllPath);
                TryDelete(dllPath);
            }
        }

        var assetPath = Path.Combine(versionDir, release.AssetName);
        var tempAsset = assetPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await DownloadToFileAsync(release.AssetUrl, tempAsset, cancellationToken).ConfigureAwait(false);
            var actualAssetHash = await Sha256FileAsync(tempAsset, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(release.AssetSha256) &&
                !string.Equals(actualAssetHash, release.AssetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RTX40MFG release asset failed the GitHub-published SHA-256 digest.");

            if (!string.IsNullOrWhiteSpace(release.ChecksumsUrl))
            {
                var sums = await DownloadTextAsync(release.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
                var expected = TryFindChecksum(sums, release.AssetName);
                if (string.IsNullOrWhiteSpace(expected))
                    throw new InvalidDataException($"SHA256SUMS.txt does not contain an entry for {release.AssetName}.");
                if (!string.Equals(actualAssetHash, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("RTX40MFG archive failed the upstream SHA256SUMS.txt verification.");
            }

            using var zip = ZipFile.OpenRead(tempAsset);
            var entries = zip.Entries
                .Where(entry => entry.Name.Equals(InnerDllName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException(entries.Length == 0
                    ? $"{InnerDllName} was not found in the RTX40MFG archive."
                    : $"RTX40MFG archive contains multiple ambiguous {InnerDllName} entries.");

            var extractTemp = dllPath + $".tmp-{Guid.NewGuid():N}";
            try
            {
                entries[0].ExtractToFile(extractTemp, overwrite: false);
                ValidatePayload(extractTemp);
                File.Move(extractTemp, dllPath, overwrite: true);
            }
            finally { TryDelete(extractTemp); }
        }
        finally
        {
            TryDelete(tempAsset);
        }

        ValidatePayload(dllPath);
        CacheStamp.Write(dllPath, release.AssetUrl, release.Version, release.AssetSha256);
        return dllPath;
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

    private async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
        var names = GameFileIndexer.Build(root, maxDepth)
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

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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
