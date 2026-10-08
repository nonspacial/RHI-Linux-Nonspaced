using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RHI.Core;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux/Wine deployment of upstream RHI's standalone DLSS Enabler extra.
/// The upstream payload ships a version.dll proxy. RHI-Linux extracts and validates the
/// x64 PE, deploys it under one upstream-supported proxy name beside the confirmed Patch
/// Target, applies a per-game Wine native,builtin override, and hash-guards update/removal.
/// It is independent of OptiScaler ownership and remains experimental under Wine/Proton.
/// </summary>
public sealed class StandaloneDlssEnablerDeploymentService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    public const string ReleasesUrl = "https://github.com/RankFTW/rhi-repo/releases";
    public const string TagPrefix = "DLSS-Enabler-";
    public const string InnerDllName = "version.dll";
    public const string DefaultProxyName = "version.dll";

    // Mirrors upstream RHI DetailPanelBuilder.Extras.cs (DeDllNames).
    public static readonly IReadOnlyList<string> ProxyNames =
        ["version.dll", "dxgi.dll", "winmm.dll", "dbghelp.dll", "psapi.dll", "winhttp.dll"];

    private const string MetaProxyName = "DLSSEnabler.ProxyName";
    private const string MetaTag = "DLSSEnabler.Tag";
    private const string MetaAssetName = "DLSSEnabler.AssetName";
    private const string MetaAssetSha256 = "DLSSEnabler.AssetSha256";
    private const string MetaPayloadSha256 = "DLSSEnabler.PayloadSha256";
    private const string MetaExperimental = "DLSSEnabler.ProtonExperimental";
    private const int MinimumDllSize = 128 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly StandaloneDlssEnablerPreferenceStore _preferences;

    /// <summary>The per-game store "Repair after move" carries over (Refs #38).</summary>
    internal Settings.IGameStateStore GameState => _preferences;
    private readonly string _cacheRoot;

    public StandaloneDlssEnablerDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        StandaloneDlssEnablerPreferenceStore? preferences = null,
        string? cacheRoot = null)
    {
        _http = http;
        _records = records;
        _overrides = overrides;
        _preferences = preferences ?? new StandaloneDlssEnablerPreferenceStore();
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "dlss-enabler-standalone");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.DlssEnablerStandalone);

    public StandaloneDlssEnablerPreferences GetPreferences(DetectedGame game)
    {
        var record = GetRecord(game);
        if (record is not null)
        {
            return new StandaloneDlssEnablerPreferences
            {
                ProxyName = record.Metadata.GetValueOrDefault(MetaProxyName) ?? DefaultProxyName
            }.Normalize();
        }
        return _preferences.Get(game);
    }

    public ComponentOperationResult SetPreferences(DetectedGame game, string proxyName)
    {
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("DLSS Enabler Standalone já está instalado. Remova e reinstale para trocar a DLL proxy.");

        var normalized = NormalizeProxyName(proxyName);
        if (!ProxyNames.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"DLL proxy não suportada pelo DLSS Enabler Standalone: {proxyName}.");

        try { _preferences.Set(game, new StandaloneDlssEnablerPreferences { ProxyName = normalized }); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }
        return ComponentOperationResult.Ok($"DLSS Enabler Standalone: proxy {normalized}.");
    }

    public static string NormalizeProxyName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? DefaultProxyName : Path.GetFileName(value.Trim());
        return ProxyNames.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? DefaultProxyName;
    }

    public static bool IsGameEligible(DetectedGame game)
        => !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
           && game.HasConfirmedPatchTarget
           && ResolveArchitecture(game) == PeArchitecture.X64
           && !string.IsNullOrWhiteSpace(game.PrefixPath)
           && Directory.Exists(game.PrefixPath)
           && File.Exists(Path.Combine(game.PrefixPath, "user.reg"))
           && ResolveBinaryDirectory(game) is not null;

    public static string EligibilityReason(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "DLSS Enabler Standalone é um proxy DLL Windows e não se aplica a jogos Linux nativos.";
        if (!game.HasConfirmedPatchTarget)
            return "Selecione primeiro o executável real do jogo em Editar jogo.";
        if (ResolveArchitecture(game) != PeArchitecture.X64)
            return "O pacote standalone atual do DLSS Enabler é tratado como Windows x64 no caminho Linux.";
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !File.Exists(Path.Combine(game.PrefixPath, "user.reg")))
            return "Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o DLSS Enabler Standalone.";
        if (ResolveBinaryDirectory(game) is null)
            return "O diretório do executável alvo não pôde ser resolvido.";
        return "Compatível com o modo standalone: Windows x64 + Patch Target confirmado + prefixo Wine/Proton inicializado.";
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string? proxyName = null,
        CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("DLSS Enabler Standalone já é gerenciado pelo RHI para este jogo.");
        if (!IsGameEligible(game))
            return ComponentOperationResult.Fail(EligibilityReason(game));

        var selectedProxy = NormalizeProxyName(proxyName ?? GetPreferences(game).ProxyName);
        if (!ProxyNames.Contains(selectedProxy, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Proxy não suportado: {selectedProxy}.");

        // The preference file is written last, after the download, the Wine override and the
        // record: refuse here when it is unreadable instead of rolling all of that back (Refs #57).
        try { _preferences.EnsureWritable(); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }

        var binaryDir = ResolveBinaryDirectory(game)!;
        var targetPath = Path.Combine(binaryDir, selectedProxy);
        if (IsRhiManagedPath(game, targetPath))
            return ComponentOperationResult.Fail($"{selectedProxy} já pertence a outro componente gerenciado pelo RHI. Escolha outro proxy no botão ⚙.");
        if (File.Exists(targetPath))
            return ComponentOperationResult.Fail($"{selectedProxy} já existe e é externo. O RHI não sobrescreve nem adota uma DLL proxy existente; escolha outro nome.");

        DlssEnablerReleaseInfo release;
        string payloadPath;
        try
        {
            release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            payloadPath = await EnsureCachedPayloadAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"DLSS Enabler Standalone download/validation failed: {ex.Message}");
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
                Component = ManagedComponentKind.DlssEnablerStandalone,
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
                    [MetaProxyName] = selectedProxy,
                    [MetaTag] = release.Tag,
                    [MetaAssetName] = release.AssetName,
                    [MetaAssetSha256] = release.AssetSha256 ?? "",
                    [MetaPayloadSha256] = payloadHash,
                    [MetaExperimental] = "1",
                }
            });
            recordWritten = true;
            _preferences.Set(game, new StandaloneDlssEnablerPreferences { ProxyName = selectedProxy });

            return ComponentOperationResult.Ok(
                $"DLSS Enabler Standalone {release.Version} instalado como {selectedProxy}. O deploy é independente do OptiScaler; a compatibilidade sob Wine/Proton deve ser confirmada no jogo.",
                targetPath);
        }
        catch (Exception ex)
        {
            try { if (moved && File.Exists(targetPath)) File.Delete(targetPath); } catch { }
            TryDelete(tempPath);
            try { if (wineOverride is not null) _overrides.Restore(game.PrefixPath!, wineOverride); } catch { }
            try { if (recordWritten) _records.Remove(game, ManagedComponentKind.DlssEnablerStandalone); } catch { }
            return ComponentOperationResult.Fail($"DLSS Enabler Standalone install rolled back: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("DLSS Enabler Standalone não é gerenciado pelo RHI para este jogo. Instalações externas permanecem protegidas.");

        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Registro do DLSS Enabler Standalone não possui o nome da DLL proxy; remoção bloqueada.");
        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, targetPath));
        if (fileRecord?.Sha256 is not { Length: > 0 })
            return ComponentOperationResult.Fail("Registro do DLSS Enabler Standalone perdeu o hash de ownership; remoção bloqueada.");

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
            _records.Remove(game, ManagedComponentKind.DlssEnablerStandalone);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return ComponentOperationResult.Ok("DLSS Enabler Standalone removido e o override Wine anterior foi restaurado.");
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
            return ComponentOperationResult.Fail($"DLSS Enabler Standalone removal failed and was rolled back as far as possible: {ex.Message}");
        }
    }

    public async Task<DlssEnablerUpdateStatus> CheckLatestAsync(
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
                    ? $"Último DLSS Enabler Standalone upstream: {latest.Version}."
                    : hasUpdate
                        ? $"Atualização disponível: {current ?? "desconhecida"} → {latest.Version}."
                        : $"DLSS Enabler Standalone {current ?? latest.Version} está atualizado.");
        }
        catch (Exception ex)
        {
            return new(false, GetRecord(game)?.Version, null, false, $"Falha ao verificar DLSS Enabler Standalone: {ex.Message}");
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("DLSS Enabler Standalone não é gerenciado pelo RHI para este jogo.");

        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail("Registro do DLSS Enabler Standalone não possui proxy; update bloqueado.");
        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, targetPath));
        if (fileRecord?.Sha256 is not { Length: > 0 })
            return ComponentOperationResult.Fail("Ownership hash do DLSS Enabler Standalone está ausente; update bloqueado.");
        if (!File.Exists(targetPath))
            return ComponentOperationResult.Fail("A DLL gerenciada do DLSS Enabler Standalone não existe mais; update não recriará um slot perdido silenciosamente.");

        var currentHash = await Sha256FileAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("A DLL gerenciada do DLSS Enabler Standalone foi modificada externamente; update bloqueado.");

        DlssEnablerReleaseInfo latest;
        string payloadPath;
        try
        {
            latest = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Ok($"DLSS Enabler Standalone {record.Version} já está atualizado.");
            payloadPath = await EnsureCachedPayloadAsync(latest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"DLSS Enabler Standalone update check/download failed: {ex.Message}");
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
            return ComponentOperationResult.Ok($"DLSS Enabler Standalone atualizado para {latest.Version}; proxy {proxyName} preservado.", targetPath);
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
            return ComponentOperationResult.Fail($"DLSS Enabler Standalone update failed and was rolled back as far as possible: {ex.Message}");
        }
    }

    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return new ManagedUpdateCheck(false, false, "DLSS Enabler Standalone is not RHI-managed.");
        var status = await CheckLatestAsync(game, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(true, status.Success && status.HasUpdate, status.Message, status.InstalledVersion, status.LatestVersion,
            CouldNotCheck: !status.Success);
    }

    internal async Task<DlssEnablerReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("rhi-repo release API returned an unexpected JSON shape.");

        var candidates = new List<(DlssEnablerReleaseInfo Info, Version Parsed)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement)) continue;
            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var versionText = tag[TagPrefix.Length..].Trim();
            if (!Version.TryParse(versionText, out var parsed)) continue;

            string? assetName = null;
            string? assetUrl = null;
            string? assetSha = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var isZip = name.Equals($"{TagPrefix}{versionText}.zip", StringComparison.OrdinalIgnoreCase)
                                || (name.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    var isDirect = name.Equals(InnerDllName, StringComparison.OrdinalIgnoreCase);
                    if (!isZip && !isDirect) continue;
                    assetName = name;
                    assetUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                    var digest = asset.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        assetSha = digest["sha256:".Length..].Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(assetUrl)) break;
                }
            }
            if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(assetUrl)) continue;
            candidates.Add((new DlssEnablerReleaseInfo(versionText, tag, assetName, assetUrl, assetSha), parsed));
        }

        if (candidates.Count == 0)
            throw new InvalidDataException("No DLSS-Enabler release with a usable ZIP/version.dll asset was found in rhi-repo.");
        return candidates.OrderByDescending(candidate => candidate.Parsed).First().Info;
    }

    internal async Task<(DlssEnablerReleaseInfo Release, string PayloadPath)> EnsureLatestPayloadForOptiScalerAsync(
        CancellationToken cancellationToken = default)
    {
        var release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var payload = await EnsureCachedPayloadAsync(release, cancellationToken).ConfigureAwait(false);
        return (release, payload);
    }

    internal async Task<string> EnsureCachedPayloadAsync(DlssEnablerReleaseInfo release, CancellationToken cancellationToken)
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
            if (!string.IsNullOrWhiteSpace(release.AssetSha256))
            {
                var actualAssetHash = await Sha256FileAsync(tempAsset, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualAssetHash, release.AssetSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("DLSS Enabler release asset failed the GitHub-published SHA-256 digest.");
            }

            if (release.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(tempAsset);
                var entries = zip.Entries
                    .Where(entry => entry.Name.Equals(InnerDllName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (entries.Length != 1)
                    throw new InvalidDataException(entries.Length == 0
                        ? $"{InnerDllName} was not found in the DLSS Enabler archive."
                        : $"DLSS Enabler archive contains multiple ambiguous {InnerDllName} entries.");
                var extractTemp = dllPath + $".tmp-{Guid.NewGuid():N}";
                try
                {
                    // Checked extraction (declared-size cap, fixed mode, length + CRC-32); never ExtractToFile (Refs #42).
                    if (await ReShadeArchiveExtractor.ExtractEntryAsync(entries[0], extractTemp, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false) is { } refused)
                        throw new InvalidDataException(ReShadeArchiveExtractor.ArchiveRefusedPrefix + refused);
                    ValidatePayload(extractTemp);
                    File.Move(extractTemp, dllPath, overwrite: true);
                }
                finally { TryDelete(extractTemp); }
            }
            else
            {
                ValidatePayload(tempAsset);
                File.Copy(tempAsset, dllPath, overwrite: true);
            }
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

    internal static void ValidatePayload(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("DLSS Enabler payload was not downloaded.", path);
        var length = new FileInfo(path).Length;
        if (length < MinimumDllSize)
            throw new InvalidDataException($"DLSS Enabler payload is implausibly small ({length} bytes).");
        if (PeArchitectureDetector.Detect(path) != PeArchitecture.X64)
            throw new InvalidDataException("DLSS Enabler standalone payload is not a Windows x64 PE DLL.");
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
            if (record.Component == ManagedComponentKind.DlssEnablerStandalone) continue;
            if (record.Files.Any(file => PathsEqual(file.Path, full))) return true;
        }
        return false;
    }

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

public sealed record DlssEnablerReleaseInfo(
    string Version,
    string Tag,
    string AssetName,
    string AssetUrl,
    string? AssetSha256);

public sealed record DlssEnablerUpdateStatus(
    bool Success,
    string? InstalledVersion,
    string? LatestVersion,
    bool HasUpdate,
    string Message);
