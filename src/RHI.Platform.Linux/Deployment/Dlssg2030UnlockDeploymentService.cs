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
/// 20/30 FG Unlock: the Linux/Wine deployment of sdli1995/dlssg_for_sm86 (experimental under Proton/VKD3D).
/// w14/extras-fg-2 (#281, upstream 2.8.9+): offered on RTX 20 and RTX 30 alike (and, as upstream's row, on every card);
/// the mod 0.4.x detects the card itself, so RTX 30 is no longer sent to Universal MFG.
/// <para>#261 (upstream 2.8.9 <c>68856af</c>): since 0.4.0 the mod is a release package, <c>dlssg-release-X-x64.zip</c>, and a game
/// needs, beside its rendering executable, exactly one proxy DLL, <c>dlssg_sm86.ini</c> and the whole <c>dlssg_sm86/</c> folder
/// (backend, kernel packs, bundled DLSS-G runtime, XeSS-FG, SR). Each proxy name is its own build that forwards to the system
/// DLL of that name (<c>version.dll</c> at the package root, the others in <c>alternatives/</c>), so a build is never renamed.
/// The version is the release tag; the zip is checked against the digest GitHub publishes for it, cached and stamped like
/// every release payload. Every package file RHI places is recorded and removed with the component. Installs made from the
/// old single-DLL path (a commit of the repository tree) read as an update and are brought to the package by Update.</para>
/// </summary>
public sealed partial class Dlssg2030UnlockDeploymentService
{
    public const string RepoOwner = "sdli1995";
    public const string RepoName = "dlssg_for_sm86";
    public const string RepositoryUrl = "https://github.com/sdli1995/dlssg_for_sm86";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/sdli1995/dlssg_for_sm86/releases/latest";

    public const string IniFileName = "dlssg_sm86.ini";

    /// <summary>The mod's own folder beside the proxy (backend, kernels, runtime; the mod writes its log under <c>logs/</c>).</summary>
    public const string ModFolderName = "dlssg_sm86";

    /// <summary>
    /// Install refused: an INI RHI does not manage is already there (e.g. one a Remove left because the user edited it).
    /// The way out comes last (w12 final review 2, m1).
    /// </summary>
    public const string ExternalIniRefusal = IniFileName
        + " já existe e não é gerenciado pelo RHI. A instalação foi bloqueada para não adotar/sobrescrever uma configuração externa."
        + " Mova ou apague esse arquivo (guarde uma cópia das suas alterações) e instale de novo.";
    public const string DefaultProxyName = "version.dll";
    public const string GpuGenRtx30 = "RTX 30 Series";
    public const string GpuGenRtx20 = "RTX 20 Series";

    /// <summary>
    /// The proxy names, in the order the mod's <c>alternatives/README.md</c> says to try them (#261): version.dll first, then
    /// winmm, dbghelp, dinput8, and dxgi or d3d12 (render-path proxies, last resort). Each is its own build.
    /// </summary>
    public static readonly IReadOnlyList<string> ProxyNames = Dlssg2030ProxyNames.Offered;

    /// <summary>
    /// w14/extras-fg-2 (#281, upstream 2.8.9+): both generations are accepted. The mod 0.4.x finds an RTX 20 or 30 card
    /// itself, so the saved value (from the ⚙ before #281) narrows nothing and RTX 30 is no longer sent to Universal MFG.
    /// </summary>
    public static readonly IReadOnlyList<string> GpuGenerations = [GpuGenRtx20, GpuGenRtx30];

    /// <summary>#281: the row's note before install (upstream 2.9.4's description); the proxy name follows.</summary>
    public const string AvailableNotePrefix = "Enables DLSS Frame Generation (up to 6X) on RTX 20 and 30 series GPUs (RTX 20 experimental); "
        + "the mod detects the card itself. DirectX 12 only, NVIDIA driver R580 or newer, no ASI Loader or ReShade needed. "
        + "Experimental under Proton/VKD3D. Proxy: ";

    /// <summary>Files under <see cref="ModFolderName"/> a package must hold for an install to work (upstream's list).</summary>
    public static readonly IReadOnlyList<string> RequiredModFiles =
        ["backend/sm86_backend.dll", "kernels/fg_kernels.dll", "runtime/nvngx_dlssg.dll"];

    private const string MetaProxyName = "FG2030.ProxyName";
    private const string MetaGpuGeneration = "FG2030.GpuGeneration";
    private const string MetaReleaseTag = "FG2030.ReleaseTag";
    private const string MetaExperimental = "FG2030.Experimental";
    // The single-DLL path's keys (before #261): dropped when Update brings a record to the package.
    private static readonly string[] RetiredMetadataKeys = ["FG2030.Router", "FG2030.CommitSha", "FG2030.SourceDllSha256", "FG2030.SourceIniSha256"];
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
            return ComponentOperationResult.Fail("20/30 FG Unlock já está instalado. Remova e reinstale para trocar a DLL proxy.");

        var normalizedGpu = NormalizeGpuGeneration(gpuGeneration);
        var normalizedProxy = NormalizeProxyName(proxyName);
        if (!GpuGenerations.Contains(normalizedGpu, StringComparer.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Geração de GPU inválida: {gpuGeneration}.");
        if (HookedDllNames.Canonical(normalizedProxy, ProxyNames) is null) // #284: a Hooked name of an offered name too
            return ComponentOperationResult.Fail($"Nome de proxy não suportado pelo 20/30 FG Unlock: {proxyName}.");

        try
        {
            _preferences.Set(game, new Dlssg2030Preferences
            {
                GpuGeneration = normalizedGpu,
                ProxyName = normalizedProxy
            });
        }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }
        return ComponentOperationResult.Ok($"20/30 FG Unlock: proxy {normalizedProxy}."); // #281: no card generation (the mod detects it)
    }

    public static string NormalizeGpuGeneration(string? value)
        => string.Equals(value?.Trim(), GpuGenRtx30, StringComparison.OrdinalIgnoreCase)
            ? GpuGenRtx30
            : GpuGenRtx20;

    /// <summary>A supported proxy name; anything else (also <c>winhttp.dll</c>, no longer offered since #261) is version.dll.</summary>
    public static string NormalizeProxyName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? DefaultProxyName : Path.GetFileName(value.Trim());
        return HookedDllNames.Canonical(name, ProxyNames) ?? DefaultProxyName; // #284: XHooked.dll kept
    }

    /// <summary>
    /// #261: the package file that is the build for <paramref name="proxyName"/> — <c>version.dll</c> at the package root, every
    /// other name in <c>alternatives/</c>, never another name renamed (each forwards to the system DLL of its own name).
    /// Null for a name the mod does not ship (<c>winhttp.dll</c> and anything else).
    /// </summary>
    public static string? PackagePathFor(string? proxyName)
    {
        // #284: a Hooked name is the build of the name it stands in for, copied under the Hooked name.
        var requested = proxyName is null ? null : HookedDllNames.BaseOf(proxyName.Trim());
        var name = ProxyNames.FirstOrDefault(candidate => candidate.Equals(requested, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : name == DefaultProxyName ? name : "alternatives/" + name;
    }

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
            return "20/30 FG Unlock é um proxy DLL Windows e não se aplica a jogos Linux nativos.";
        if (!game.HasConfirmedPatchTarget)
            return "Selecione primeiro o executável real do jogo em Editar jogo.";
        if (ResolveArchitecture(game) != PeArchitecture.X64)
            return "20/30 FG Unlock exige um executável Windows x64.";
        if (game.EffectiveGraphicsApi != GraphicsApi.DirectX12)
            return "O 20/30 FG Unlock funciona somente em jogos DirectX 12.";
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !WineRegistryEditor.HasUserHive(game.PrefixPath))
            return "Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o 20/30 FG Unlock.";
        if (ResolveBinaryDirectory(game) is null)
            return "O diretório do executável alvo não pôde ser resolvido.";
        return "Compatível com o caminho experimental: Windows x64 + DirectX 12 + prefixo Wine/Proton inicializado.";
    }

    // ── Messages (#261; English with PT-BR pairs) ───────────────────────────────────────────────────────────────

    /// <summary>The release names its package <c>dlssg-release-&lt;tag&gt;-x64.zip</c>; none, or more than one, is refused. The tag follows.</summary>
    public const string NoPackagePrefix = "The DLSSG 20/30 release publishes no single dlssg-release-<version>-x64.zip package, so nothing was installed: ";

    /// <summary>A package missing a file a game needs; the package path follows.</summary>
    /// <summary>
    /// w13 final review nit: the files RHI would extract declare more than <see cref="PackageMaxTotalBytes"/> together
    /// (each is capped on its own as well); the declared total in bytes follows (PT-BR presentation pair).
    /// </summary>
    public const string PackageTooLargePrefix = "The DLSSG 20/30 release package would unpack to more than 1 GiB, so nothing was installed. Declared bytes: ";

    /// <summary>The cap on the declared size of every file RHI extracts from one package, together (1 GiB).</summary>
    public const long PackageMaxTotalBytes = 1L << 30;

    public const string PackageFileMissingPrefix = "The DLSSG 20/30 release package is missing a file the mod needs, so nothing was installed: ";

    /// <summary>A package file RHI would place where a file that is not RHI's already is; the path follows.</summary>
    public const string PackageFileCollisionPrefix =
        "A DLSSG 20/30 package file would replace a file RHI does not manage, so nothing was changed. Move or delete it, then try again: ";

    /// <summary>The game's <c>dlssg_sm86</c> folder (or one inside it) is a link; RHI never writes through one. The path follows.</summary>
    public const string ModFolderLinkPrefix = "The dlssg_sm86 folder beside the game is a link, and RHI does not write through links: ";

    /// <summary>A package file RHI placed was changed outside RHI; an update never replaces it. The path follows.</summary>
    public const string PackageFileChangedPrefix =
        "Not updated: a DLSSG 20/30 package file was changed outside RHI, and an update never replaces it. Remove it, then Install it again: ";

    /// <summary>Update of a <c>winhttp.dll</c> install (no longer offered) to version.dll refused: that name is taken. The path follows.</summary>
    public const string RetiredProxyTargetTakenPrefix =
        "winhttp.dll is no longer offered and RHI moves this install to version.dll, but that name is taken. Remove it, then Install it with another DLL name: ";

    /// <summary>The install's result line: this fixed text, then "&lt;version&gt; (&lt;proxy name&gt;).", then <see cref="InstalledNote"/>.</summary>
    public const string InstalledPrefix = "20/30 FG Unlock installed with dlssg_sm86.ini and the dlssg_sm86 folder: ";
    public const string InstalledNote = " Support under VKD3D-Proton remains experimental and must be confirmed in a real game.";

    /// <summary>The update's result line: this fixed text, then "&lt;version&gt;.", then the notes below.</summary>
    public const string UpdatedPrefix = "20/30 FG Unlock updated to the release package ";
    public const string UpdatedIniKeptNote = " Your dlssg_sm86.ini was kept.";
    public const string UpdatedIniReplacedNote = " dlssg_sm86.ini now comes from the new release.";
    public const string UpdatedProxyMovedNote = " winhttp.dll is no longer offered, so it now runs as version.dll.";

    /// <summary>The check's lines; the version (or "old → new") follows.</summary>
    public const string CurrentPrefix = "20/30 FG Unlock is on the latest release: ";
    public const string LatestPrefix = "Latest DLSSG 20/30 release: ";
    public const string UpdateAvailablePrefix = "DLSSG 20/30 update available: ";

    /// <summary>What the check calls an install made from the old single-DLL path (no release tag in its record).</summary>
    public const string OldLayoutSuffix = " (single DLL, before the release package)";

    // ── Install ─────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string? gpuGeneration = null,
        string? proxyName = null,
        CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("20/30 FG Unlock já é gerenciado pelo RHI para este jogo.");

        var requestedGpu = gpuGeneration ?? GetPreferences(game).GpuGeneration; // #281: any card; the mod detects it

        if (!IsGameEligible(game))
            return ComponentOperationResult.Fail(EligibilityReason(game));
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        if (_records.Find(game, ManagedComponentKind.MfgAdaUnlock) is not null)
            return ComponentOperationResult.Fail("MFG Unlock já está ativo. Remova-o antes de instalar o 20/30 FG Unlock; os dois métodos de desbloqueio de Frame Generation não devem compartilhar o mesmo processo.");

        if (_records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG já está ativo. Remova-o antes de instalar o 20/30 FG Unlock; os dois métodos não devem disputar o mesmo pipeline de Frame Generation.");

        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("ShortFuse já está ativo e gerencia a pilha DLSS/Frame Generation deste jogo. Remova-o antes de usar o 20/30 FG Unlock para evitar duas autoridades sobre o mesmo runtime.");

        var preferences = GetPreferences(game);
        var selectedGpu = NormalizeGpuGeneration(requestedGpu);
        var selectedProxy = NormalizeProxyName(proxyName ?? preferences.ProxyName);
        if (HookedDllNames.Canonical(selectedProxy, ProxyNames) is null)
            return ComponentOperationResult.Fail($"Proxy não suportado: {selectedProxy}.");
        if (ExtraDllNames.HookedRefusal(_records, game, selectedProxy) is { } notHooked) return ComponentOperationResult.Fail(notHooked); // #284

        var binaryDir = ResolveBinaryDirectory(game)!;
        var proxyPath = Path.Combine(binaryDir, selectedProxy);
        var iniPath = Path.Combine(binaryDir, IniFileName);

        if (IsRhiManagedPath(game, proxyPath))
            return ComponentOperationResult.Fail($"{selectedProxy} já pertence a outro componente gerenciado pelo RHI. Escolha outro proxy no botão ⚙.");
        if (File.Exists(proxyPath))
            return ComponentOperationResult.Fail($"{selectedProxy} já existe e não pertence ao 20/30 FG Unlock gerenciado pelo RHI. Escolha outro proxy; o RHI não sobrescreve uma DLL externa.");
        if (File.Exists(iniPath))
            return ComponentOperationResult.Fail(ExternalIniRefusal);
        if (ModFolderLink(binaryDir) is { } link)
            return ComponentOperationResult.Fail(ModFolderLinkPrefix + link);

        // The preference file is written last, after the download, the Wine override and the
        // record: refuse here when it is unreadable instead of rolling all of that back (Refs #57).
        try { _preferences.EnsureWritable(); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }

        Dlssg2030Package package;
        try
        {
            var release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            package = await EnsureCachedPackageAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"20/30 FG Unlock download/validation failed: {ex.Message}");
        }
        if (package.ProxySource(selectedProxy) is not { } proxySource)
            return ComponentOperationResult.Fail(PackageFileMissingPrefix + PackagePathFor(selectedProxy));
        var modFiles = package.ModFiles.Select(rel => (Source: package.PathOf(ModFolderName + "/" + rel), Target: ModTarget(binaryDir, rel))).ToArray();
        if (modFiles.FirstOrDefault(file => File.Exists(file.Target) || Directory.Exists(file.Target)) is { Target: not null } taken)
            return ComponentOperationResult.Fail(PackageFileCollisionPrefix + taken.Target);

        string? proxyTemp = null;
        WineOverrideRecord? wineOverride = null;

        // The Wine override is journaled before it is written (#23): a crash before the record is saved
        // leaves an entry start-up recovery rolls back, never an override nothing owns.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "20/30 FG Unlock install: " + game.Name, game, ManagedComponentKind.Dlssg2030Unlock, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var operation = scope.Operation;
        var createdFolders = new List<string>();
        // Downloaded and validated: from the first change on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            wineOverride = await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath!, selectedProxy, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);

            // Verified, fsynced copy beside the target (full re-read hash), placed without replacing anything.
            var stagedProxy = await AtomicFile.StageCopyAsync(proxySource, proxyPath, null, null, cancellationToken).ConfigureAwait(false);
            proxyTemp = stagedProxy.Path;
            ValidateCachedDll(proxyTemp);

            // Tracked and declared first (#23): a crash before the record is saved removes every file at the next start.
            await scope.PlaceStagedAsync(stagedProxy, proxyPath, cancellationToken, overwrite: false).ConfigureAwait(false);
            var iniHash = await scope.PlaceCopyAsync(package.IniPath, iniPath, cancellationToken, overwrite: false).ConfigureAwait(false);
            var files = new List<LinuxDeployedFile>
            {
                new() { Path = proxyPath, ExistedBeforeInstall = false, Sha256 = stagedProxy.Sha256, AllowUserChanges = false },
                new() { Path = iniPath, ExistedBeforeInstall = false, Sha256 = iniHash, AllowUserChanges = true },
            };
            foreach (var (source, target) in modFiles)
            {
                await EnsureFolderAsync(binaryDir, Path.GetDirectoryName(target)!, operation, createdFolders, cancellationToken).ConfigureAwait(false);
                var hash = await scope.PlaceCopyAsync(source, target, cancellationToken, overwrite: false).ConfigureAwait(false);
                files.Add(new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = hash, AllowUserChanges = false });
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaProxyName] = selectedProxy,
                [MetaGpuGeneration] = selectedGpu,
                [MetaReleaseTag] = package.Tag,
                [MetaExperimental] = "1",
            };
            VerifiedDownload.Record(metadata, package.ArchiveSha256.ToUpperInvariant(), package.DigestVerified);

            // The preference is saved before the commit point (review w3-journal #3): a failure here still rolls back
            // before the record exists, and nothing after the record can throw into the rollback.
            _preferences.Set(game, new Dlssg2030Preferences { GpuGeneration = selectedGpu, ProxyName = selectedProxy });
            _records.Upsert(LinuxDeploymentRecovery.StampOperation(operation, new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.Dlssg2030Unlock,
                BinaryDirectory = binaryDir,
                SourceUrl = package.SourceUrl,
                Version = package.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = wineOverride,
                Files = files,
                Metadata = metadata
            }));
        }
        catch (Exception ex)
        {
            if (proxyTemp is not null) FileCleanup.TryDelete(proxyTemp);
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("20/30 FG Unlock");
            if (rollback.RolledBack)
            {
                _overrides.TryRestore(game.PrefixPath, wineOverride);
                RemoveEmptyFolders(createdFolders);
            }
            return ComponentOperationResult.Fail($"20/30 FG Unlock install rolled back: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record carries the operation id: it is the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"{InstalledPrefix}{package.Version} ({selectedProxy}).{InstalledNote}", proxyPath, iniPath);
    }

    // ── Remove ──────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("20/30 FG Unlock não é gerenciado pelo RHI para este jogo. A remoção externa permanece protegida.");

        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        // w12 final review M1: a record without its proxy's name is removed by its file list (the way out the check names).
        if (ProxylessRecordRemoval.Applies(record, MetaProxyName))
            return await RemoveWithoutProxyNameAsync(game, record, cancellationToken).ConfigureAwait(false);
        var proxyName = record.Metadata[MetaProxyName];
        var proxyPath = Path.Combine(record.BinaryDirectory, proxyName);
        var iniPath = Path.Combine(record.BinaryDirectory, IniFileName);
        var proxyRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, proxyPath));
        var iniRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, iniPath));
        if (proxyRecord is null || string.IsNullOrWhiteSpace(proxyRecord.Sha256))
            return ComponentOperationResult.Fail("Registro do 20/30 FG Unlock perdeu o hash da DLL gerenciada; remoção bloqueada.");

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

        // #261: the package files under dlssg_sm86/ that still hold RHI's bytes go; one changed outside RHI stays and is named.
        var modFolder = Path.Combine(record.BinaryDirectory, ModFolderName);
        if (ModFolderLink(record.BinaryDirectory) is { } link)
            return ComponentOperationResult.Fail(ModFolderLinkPrefix + link);
        var (modDelete, modLeft) = await PlanModFilesAsync(record, cancellationToken).ConfigureAwait(false);
        var logs = ModLogFiles(modFolder);

        // Journaled (#23): the record is stamped with the removal's id first; a crash before it is removed puts the
        // files and the override back as the record still describes them.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "20/30 FG Unlock removal: " + game.Name, game, ManagedComponentKind.Dlssg2030Unlock, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;

        // Cancel is honoured only before the first change; from here the removal runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            await scope.DeleteAsync(proxyPath, cancellationToken).ConfigureAwait(false);
            if (!preserveIni) await scope.DeleteAsync(iniPath, cancellationToken).ConfigureAwait(false);
            foreach (var path in modDelete.Concat(logs))
                await scope.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.Dlssg2030Unlock);
        }
        catch (Exception ex)
        {
            // Files back from the scope's verified copies; RHI's value back only where this removal restored it (a later
            // value stays, #30); the record kept so the restored files stay owned.
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "fg2030").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("20/30 FG Unlock", removal: true);
            if (rollback.RolledBack && overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return ComponentOperationResult.Fail($"20/30 FG Unlock removal failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record is gone: the commit point. The journal's copies are gone with it, so the empty folders can go.
        await scope.CommitAsync().ConfigureAwait(false);
        PruneModFolder(record.BinaryDirectory);

        return ComponentOperationResult.Ok((preserveIni
            ? $"20/30 FG Unlock removido e override Wine restaurado. {IniFileName} foi preservado porque foi alterado pelo usuário/outro mod."
            : "20/30 FG Unlock removido; DLL, INI gerenciado e override Wine foram limpos.")
            + WineDllOverrideService.KeptValueNotes(overrideRestore)
            + (modLeft.Count == 0 ? "" : ProxylessRecordRemoval.NotDeletedPrefix + string.Join("; ", modLeft)));
    }

    /// <summary>
    /// w12 final review M1: Remove for a record without its proxy's name (<see cref="ProxylessRecordRemoval"/>): every
    /// listed file (proxy, INI, the package files under dlssg_sm86/) that still holds RHI's bytes goes, the recorded override
    /// is restored, the record is dropped, and the files left behind are named. Same journaled removal as <see cref="RemoveAsync"/>.
    /// </summary>
    private async Task<ComponentOperationResult> RemoveWithoutProxyNameAsync(
        DetectedGame game, LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        if (await DeploymentScope.HoldRefusalAsync(_journal, game, ManagedComponentKind.Dlssg2030Unlock).ConfigureAwait(false) is { } held)
            return ComponentOperationResult.Fail(held);
        if (ModFolderLink(record.BinaryDirectory) is { } link)
            return ComponentOperationResult.Fail(ModFolderLinkPrefix + link);
        var plan = await ProxylessRecordRemoval.PlanAsync(record, cancellationToken, ModFolderName).ConfigureAwait(false);
        var logs = ModLogFiles(Path.Combine(record.BinaryDirectory, ModFolderName));

        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "20/30 FG Unlock removal: " + game.Name, game, ManagedComponentKind.Dlssg2030Unlock, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            foreach (var path in plan.Delete.Concat(logs))
                await scope.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.Dlssg2030Unlock);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "fg2030").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("20/30 FG Unlock", removal: true);
            if (rollback.RolledBack && overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return ComponentOperationResult.Fail($"20/30 FG Unlock removal failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        PruneModFolder(record.BinaryDirectory);
        return ComponentOperationResult.Ok(ProxylessRecordRemoval.Message("20/30 FG Unlock", plan.Left, WineDllOverrideService.KeptValueNotes(overrideRestore)));
    }

    // ── Update ──────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<Dlssg2030UpdateStatus> CheckLatestAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            var record = GetRecord(game);
            var hasUpdate = record is not null && NeedsPackageUpdate(record, release.Tag);
            return new(true, record?.Version, release.Version, hasUpdate,
                record is null
                    ? LatestPrefix + release.Version
                    : hasUpdate
                        ? $"{UpdateAvailablePrefix}{InstalledLabel(record)} → {release.Version}"
                        : CurrentPrefix + (record.Version ?? release.Version));
        }
        catch (Exception ex)
        {
            return new(false, GetRecord(game)?.Version, null, false, $"Falha ao verificar 20/30 FG Unlock: {ex.Message}");
        }
    }

    /// <summary>
    /// #261: a record needs the package when it is not on <paramref name="latestTag"/>, came from the old single-DLL path (no
    /// release tag), or runs under a proxy name the mod no longer ships (winhttp.dll).
    /// </summary>
    internal static bool NeedsPackageUpdate(LinuxDeploymentRecord record, string latestTag)
        => !string.Equals(record.Metadata.GetValueOrDefault(MetaReleaseTag), latestTag, StringComparison.OrdinalIgnoreCase)
           || PackagePathFor(record.Metadata.GetValueOrDefault(MetaProxyName)) is null;

    private static string InstalledLabel(LinuxDeploymentRecord record)
        => record.Metadata.ContainsKey(MetaReleaseTag) ? record.Version ?? "?" : (record.Version ?? "?") + OldLayoutSuffix;

    /// <summary>
    /// #238: the Update All check. The proxy or the INI removed outside RHI is never put back (seen before any network call);
    /// the update is planned only while the proxy and every package file RHI placed still hold RHI's bytes (hashed only then);
    /// a failed lookup could not check.
    /// </summary>
    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return new ManagedUpdateCheck(false, false, "20/30 FG Unlock is not RHI-managed.");
        // F1 (review w12/mfg): a game held by its own earlier undo gets the hold's restart text before its files are read.
        if (await ManagedUpdateFileGuard.HeldAsync(_journal, game, ManagedComponentKind.Dlssg2030Unlock, record.Version).ConfigureAwait(false) is { } held)
            return held;
        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return new ManagedUpdateCheck(true, false, ManagedUpdateFileGuard.MissingProxyNameMessage,
                record.Version, CouldNotCheck: true, LocalReason: true);
        var proxyPath = Path.Combine(record.BinaryDirectory, proxyName);
        if (ManagedUpdateFileGuard.Removed([proxyPath, Path.Combine(record.BinaryDirectory, IniFileName)], record.Version) is { } removed)
            return removed;
        var status = await CheckLatestAsync(game, cancellationToken).ConfigureAwait(false);
        var planned = new ManagedUpdateCheck(true, status.Success && status.HasUpdate, status.Message, record.Version, status.LatestVersion,
            CouldNotCheck: !status.Success);
        var proxyRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, proxyPath));
        var guarded = await ManagedUpdateFileGuard.BeforePlanningAsync(planned, [(proxyPath, proxyRecord?.Sha256)], cancellationToken).ConfigureAwait(false);
        if (!guarded.UpdateAvailable) return guarded;
        var (_, changed) = await PlanModFilesAsync(record, cancellationToken).ConfigureAwait(false);
        return changed.Count == 0
            ? guarded
            : guarded with { UpdateAvailable = false, CouldNotCheck = true, LocalReason = true, Message = PackageFileChangedPrefix + changed[0] };
    }

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("20/30 FG Unlock não está instalado pelo RHI.");

        // F1 (review w12/mfg; toolkit rule since w10 D15): the hold's restart text before the file checks below.
        if (await DeploymentScope.HoldRefusalAsync(_journal, game, ManagedComponentKind.Dlssg2030Unlock).ConfigureAwait(false) is { } held)
            return ComponentOperationResult.Fail(held);
        var proxyName = record.Metadata.GetValueOrDefault(MetaProxyName);
        if (string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail(ManagedUpdateFileGuard.MissingProxyNameMessage);
        var binaryDir = record.BinaryDirectory;
        var proxyPath = Path.Combine(binaryDir, proxyName);
        var iniPath = Path.Combine(binaryDir, IniFileName);
        var proxyRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, proxyPath));
        var iniRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, iniPath));
        if (proxyRecord is null || string.IsNullOrWhiteSpace(proxyRecord.Sha256) || !File.Exists(proxyPath))
            return ComponentOperationResult.Fail("A DLL gerenciada do 20/30 FG Unlock está ausente ou sem hash. Remova/reinstale para recuperar com segurança.");
        if (!File.Exists(iniPath))
            return ComponentOperationResult.Fail($"{IniFileName} foi removido externamente. Atualização bloqueada; use remover/reinstalar para não recriar configuração apagada pelo usuário.");

        var currentHash = await FileHash.Sha256HexAsync(proxyPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, proxyRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"A DLL gerenciada {proxyName} foi modificada fora do RHI. Atualização bloqueada.");
        if (ModFolderLink(binaryDir) is { } link)
            return ComponentOperationResult.Fail(ModFolderLinkPrefix + link);
        var (ownedMod, changedMod) = await PlanModFilesAsync(record, cancellationToken).ConfigureAwait(false);
        if (changedMod.Count > 0)
            return ComponentOperationResult.Fail(PackageFileChangedPrefix + changedMod[0]);

        // #261: winhttp.dll (or any name the mod no longer ships) moves to version.dll, the mod's first choice.
        var newProxyName = PackagePathFor(proxyName) is null ? DefaultProxyName : NormalizeProxyName(proxyName);
        var moveProxy = !newProxyName.Equals(proxyName, StringComparison.OrdinalIgnoreCase);
        var newProxyPath = Path.Combine(binaryDir, newProxyName);
        if (moveProxy && (File.Exists(newProxyPath) || IsRhiManagedPath(game, newProxyPath)))
            return ComponentOperationResult.Fail(RetiredProxyTargetTakenPrefix + newProxyPath);
        if (moveProxy && _overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        Dlssg2030Package package;
        try
        {
            var release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (!NeedsPackageUpdate(record, release.Tag))
                return ComponentOperationResult.Ok(CurrentPrefix + (record.Version ?? release.Version));
            package = await EnsureCachedPackageAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"20/30 FG Unlock update download/validation failed: {ex.Message}");
        }
        if (package.ProxySource(newProxyName) is not { } proxySource)
            return ComponentOperationResult.Fail(PackageFileMissingPrefix + PackagePathFor(newProxyName));

        // The package's files under dlssg_sm86/: RHI's own are replaced, a new one must not land on a file that is not RHI's.
        var owned = new HashSet<string>(ownedMod.Select(Path.GetFullPath), StringComparer.Ordinal);
        var modFiles = package.ModFiles.Select(rel => (Source: package.PathOf(ModFolderName + "/" + rel), Target: ModTarget(binaryDir, rel))).ToArray();
        if (modFiles.FirstOrDefault(file => !owned.Contains(file.Target) && (File.Exists(file.Target) || Directory.Exists(file.Target))) is { Target: not null } taken)
            return ComponentOperationResult.Fail(PackageFileCollisionPrefix + taken.Target);
        var dropped = owned.Except(modFiles.Select(file => file.Target), StringComparer.Ordinal).ToArray();

        // The INI: still RHI's bytes → the new release's (an old single-DLL INI has keys 0.4 no longer reads); the user's → kept.
        var replaceIni = iniRecord is not null && !string.IsNullOrWhiteSpace(iniRecord.Sha256)
                         && string.Equals(await FileHash.Sha256HexAsync(iniPath, cancellationToken).ConfigureAwait(false), iniRecord.Sha256, StringComparison.OrdinalIgnoreCase);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "20/30 FG Unlock update: " + game.Name, game, ManagedComponentKind.Dlssg2030Unlock, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        string? staged = null;
        var createdFolders = new List<string>();
        WineOverrideRestoreResult? oldOverrideRestore = null;
        WineOverrideRecord? newOverride = null;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            var updated = LinuxDeploymentRecordStore.Clone(record);
            var stagedCopy = await AtomicFile.StageCopyAsync(proxySource, newProxyPath, null, null, cancellationToken).ConfigureAwait(false);
            staged = stagedCopy.Path;
            ValidateCachedDll(staged);
            if (moveProxy)
            {
                await scope.DeleteAsync(proxyPath, cancellationToken).ConfigureAwait(false);
                oldOverrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
                newOverride = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, game.PrefixPath!, newProxyName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                // Tracked with a verified copy first: a failure (or a crash before the record is saved) removes it again.
                await scope.PlaceStagedAsync(stagedCopy, newProxyPath, cancellationToken, overwrite: false).ConfigureAwait(false);
                updated.WineOverride = newOverride;
                updated.Metadata[MetaProxyName] = newProxyName;
            }
            else
            {
                // Tracked with a verified copy of the previous DLL first: a failure (or a crash before the record is saved)
                // puts it back.
                await scope.PlaceStagedAsync(stagedCopy, newProxyPath, cancellationToken).ConfigureAwait(false);
            }
            staged = null;

            var files = new List<LinuxDeployedFile>
            {
                new() { Path = newProxyPath, ExistedBeforeInstall = false, Sha256 = stagedCopy.Sha256, AllowUserChanges = false },
            };
            if (replaceIni)
            {
                // #281 (upstream DeployIni): the ⚙'s settings set in the INI being replaced are written into the new one.
                var kept = Dlssg2030Ini.Kept(IniLines.Read(iniPath));
                var iniSha = await scope.PlaceCopyAsync(package.IniPath, iniPath, cancellationToken).ConfigureAwait(false);
                var lines = IniLines.Read(iniPath);
                if (kept.Any(pair => !string.Equals(IniLines.Get(lines, pair.Setting.Section, pair.Setting.Key)?.Trim(), pair.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var (setting, value) in kept) IniLines.Set(lines, setting.Section, setting.Key, value);
                    IniLines.Write(iniPath, lines, [binaryDir]);
                    await scope.DeclareWrittenContentAsync(iniPath, cancellationToken).ConfigureAwait(false);
                    iniSha = await FileHash.Sha256HexAsync(iniPath, cancellationToken).ConfigureAwait(false);
                }
                files.Add(new LinuxDeployedFile { Path = iniPath, ExistedBeforeInstall = false, AllowUserChanges = true, Sha256 = iniSha });
            }
            else if (iniRecord is not null)
                files.Add(iniRecord);
            foreach (var (source, target) in modFiles)
            {
                await EnsureFolderAsync(binaryDir, Path.GetDirectoryName(target)!, scope.Operation, createdFolders, cancellationToken).ConfigureAwait(false);
                var hash = await scope.PlaceCopyAsync(source, target, cancellationToken, overwrite: owned.Contains(target)).ConfigureAwait(false);
                files.Add(new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = hash, AllowUserChanges = false });
            }
            foreach (var path in dropped)
                await scope.DeleteAsync(path, cancellationToken).ConfigureAwait(false);

            updated.Files = files;
            updated.Version = package.Version;
            updated.SourceUrl = package.SourceUrl;
            updated.InstalledAtUtc = DateTime.UtcNow;
            updated.Metadata[MetaReleaseTag] = package.Tag;
            foreach (var key in RetiredMetadataKeys) updated.Metadata.Remove(key);
            VerifiedDownload.Forget(updated.Metadata, "Ini");
            VerifiedDownload.Record(updated.Metadata, package.ArchiveSha256.ToUpperInvariant(), package.DigestVerified);
            _records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex)
        {
            if (staged is not null) FileCleanup.TryDelete(staged);
            // Every file goes back from the scope's verified copies; a copy that cannot be put back is kept and named.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("20/30 FG Unlock");
            if (rollback.RolledBack)
            {
                _overrides.TryRestore(game.PrefixPath, newOverride);
                if (oldOverrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored) _overrides.TryReapply(game.PrefixPath, record.WineOverride);
                RemoveEmptyFolders(createdFolders);
            }
            return ComponentOperationResult.Fail($"20/30 FG Unlock update failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        if (dropped.Length > 0) PruneModFolder(binaryDir);
        return ComponentOperationResult.Ok(UpdatedPrefix + package.Version + "."
            + (replaceIni ? UpdatedIniReplacedNote : UpdatedIniKeptNote)
            + (moveProxy ? UpdatedProxyMovedNote + WineDllOverrideService.KeptValueNotes(oldOverrideRestore) : ""));
    }

    // ── Release and package ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>GitHub's latest release: its tag and the one <c>dlssg-release-&lt;tag&gt;-x64.zip</c> package.</summary>
    internal async Task<Dlssg2030ReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, LatestReleaseApiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("GitHub's latest-release answer for dlssg_for_sm86 is not an object.");
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || !ReleaseTag().IsMatch(tag))
            throw new InvalidDataException("GitHub's latest release of dlssg_for_sm86 has no usable tag.");
        var assets = root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array
            ? assetsElement.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object).ToArray()
            : [];
        var name = SelectPackageAssetName(assets.Select(asset => asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToArray(), tag);
        var selected = assets.First(asset => asset.TryGetProperty("name", out var n) && n.GetString() == name);
        var url = selected.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidDataException(NoPackagePrefix + tag);
        var digest = TrustedDownload.NormalizeDigest(selected.TryGetProperty("digest", out var d) ? d.GetString() : null);
        return new Dlssg2030ReleaseInfo(tag, VersionOf(tag), name, url, digest);
    }

    /// <summary>
    /// #261: the release's package, <c>dlssg-release-&lt;version&gt;-x64.zip</c> for this tag (upstream takes the first
    /// <c>dlssg-release-*-x64.zip</c>; a "-310.9" alternative-runtime build or any other second zip is never chosen). None, or
    /// more than one, is refused.
    /// </summary>
    public static string SelectPackageAssetName(IReadOnlyList<string> assetNames, string tag)
    {
        var expected = $"dlssg-release-{VersionOf(tag)}-x64.zip";
        var matches = assetNames.Where(name => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(NoPackagePrefix + tag);
    }

    private static string VersionOf(string tag) => tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    /// <summary>
    /// The package, downloaded under the DLSSG 20/30 host policy and checked against GitHub's digest when published, cached and
    /// stamped (#26, #142), then extracted (only the files a game needs; checked extraction, never through a link, no
    /// <c>..</c>) into a tree stamped with the archive's hash, so a tree from any other bytes is never deployed.
    /// </summary>
    internal async Task<Dlssg2030Package> EnsureCachedPackageAsync(Dlssg2030ReleaseInfo release, CancellationToken cancellationToken)
    {
        var versionDir = Path.Combine(_cacheRoot, Sanitize(release.Tag));
        Directory.CreateDirectory(versionDir);
        var archive = Path.Combine(versionDir, release.AssetName);
        var extractDir = Path.Combine(versionDir, "package");
        var download = await TrustedDownload.EnsureVerifiedAsync(
            _http, release.Url, archive, DownloadSources.Dlssg2030, release.Tag, release.Digest,
            validate: (temp, _) =>
            {
                using var zip = ZipFile.OpenRead(temp);
                PlanPackageEntries(zip);
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!CacheStamp.IsValidBoundTo(extractDir, release.Url, release.Tag, download.Sha256))
        {
            CacheStamp.Invalidate(extractDir);
            // RHI's own cache folder: a stale tree that cannot be removed must not be extracted over.
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);
            using (var zip = ZipFile.OpenRead(download.Path))
            {
                foreach (var (entry, relative) in PlanPackageEntries(zip))
                {
                    var target = Path.Combine(extractDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, target, DownloadSources.PayloadArchiveMaxBytes, cancellationToken).ConfigureAwait(false);
                }
            }
            ValidatePackage(extractDir);
            CacheStamp.WriteBoundTo(extractDir, release.Url, release.Tag, download.Sha256);
        }
        else ValidatePackage(extractDir);

        var modRoot = Path.Combine(extractDir, ModFolderName);
        var modFiles = SafeFileEnumeration.Files(modRoot)
            .Select(path => Path.GetRelativePath(modRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new Dlssg2030Package(release.Tag, release.Version, release.Url, extractDir, download.Sha256.ToLowerInvariant(),
            download.DigestVerified, modFiles);
    }

    /// <summary>
    /// The archive entries a game needs, with their package path: <c>version.dll</c>, <c>dlssg_sm86.ini</c>, the proxy builds in
    /// <c>alternatives/</c> and everything under <c>dlssg_sm86/</c> except its logs and Markdown (upstream's runtime-file rule).
    /// Docs, readmes, manuals and the configurator page stay out. A name with <c>..</c>, an empty part, a rooted path or a
    /// duplicate refuses the whole package; a package without the required files is refused.
    /// </summary>
    internal static IReadOnlyList<(ZipArchiveEntry Entry, string Relative)> PlanPackageEntries(ZipArchive zip, long maxTotalBytes = PackageMaxTotalBytes)
    {
        var plan = new List<(ZipArchiveEntry, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue; // a folder entry
            var parts = name.Split('/');
            if (name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal) || parts.Any(part => part.Length == 0 || part is "." or ".."))
                throw new InvalidDataException(PackageFileMissingPrefix + "(invalid entry name) " + entry.FullName);
            if (!IsRuntimeFile(name)) continue;
            if (!seen.Add(name))
                throw new InvalidDataException(PackageFileMissingPrefix + "(duplicate entry) " + entry.FullName);
            // The declared sizes, summed: the extractor checks each file against its own length as it writes.
            declared += entry.Length;
            if (declared > maxTotalBytes)
                throw new InvalidDataException(PackageTooLargePrefix + declared);
            plan.Add((entry, name));
        }
        foreach (var required in new[] { DefaultProxyName, IniFileName }.Concat(RequiredModFiles.Select(file => ModFolderName + "/" + file)))
            if (!seen.Contains(required))
                throw new InvalidDataException(PackageFileMissingPrefix + required);
        return plan;
    }

    internal static bool IsRuntimeFile(string path)
    {
        if (path.Equals(DefaultProxyName, StringComparison.OrdinalIgnoreCase) || path.Equals(IniFileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("alternatives/", StringComparison.OrdinalIgnoreCase))
            return ProxyNames.Any(name => path.Equals("alternatives/" + name, StringComparison.OrdinalIgnoreCase));
        if (path.StartsWith(ModFolderName + "/", StringComparison.OrdinalIgnoreCase))
            return !path.StartsWith(ModFolderName + "/logs/", StringComparison.OrdinalIgnoreCase)
                   && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static void ValidatePackage(string root)
    {
        foreach (var name in ProxyNames)
        {
            var path = Path.Combine(root, PackagePathFor(name)!.Replace('/', Path.DirectorySeparatorChar));
            if (name == DefaultProxyName || File.Exists(path)) ValidateCachedDll(path);
        }
        ValidateIniText(File.ReadAllText(Path.Combine(root, IniFileName)));
        foreach (var file in RequiredModFiles)
            if (!File.Exists(Path.Combine(root, ModFolderName, file.Replace('/', Path.DirectorySeparatorChar))))
                throw new InvalidDataException(PackageFileMissingPrefix + ModFolderName + "/" + file);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static string ModTarget(string binaryDir, string relative)
        => Path.GetFullPath(Path.Combine(binaryDir, ModFolderName, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsUnderModFolder(string binaryDir, string path)
    {
        var folder = Path.GetFullPath(Path.Combine(binaryDir, ModFolderName)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(folder, StringComparison.Ordinal);
    }

    /// <summary>
    /// The record's files under dlssg_sm86/: those that still hold RHI's bytes (or are already gone) and those changed outside
    /// RHI (or recorded without a hash). A file already gone is neither deleted nor named.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Owned, IReadOnlyList<string> Changed)> PlanModFilesAsync(
        LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var owned = new List<string>();
        var changed = new List<string>();
        foreach (var file in record.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || !IsUnderModFolder(record.BinaryDirectory, file.Path)) continue;
            var path = Path.GetFullPath(file.Path);
            if (!File.Exists(path)) continue;
            var matches = !string.IsNullOrWhiteSpace(file.Sha256)
                          && string.Equals(await FileHash.Sha256HexAsync(path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase);
            (matches ? owned : changed).Add(path);
        }
        return (owned, changed);
    }

    /// <summary>The mod's own log files (<c>dlssg_sm86/logs/*.log</c>, regular files): removed with the mod, as upstream does.</summary>
    private static IReadOnlyList<string> ModLogFiles(string modFolder)
    {
        var logs = Path.Combine(modFolder, "logs");
        var info = new DirectoryInfo(logs);
        if (!info.Exists || info.LinkTarget is not null) return [];
        return info.EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .Where(file => file.LinkTarget is null)
            .Select(file => file.FullName)
            .ToArray();
    }

    /// <summary>The first of dlssg_sm86/ and the folders inside it that is a link (RHI never writes or deletes through one), or null.</summary>
    private static string? ModFolderLink(string binaryDir)
    {
        var folder = new DirectoryInfo(Path.Combine(binaryDir, ModFolderName));
        if (folder.LinkTarget is not null) return folder.FullName;
        if (!folder.Exists) return null;
        try
        {
            return SafeFileEnumeration.Entries(folder.FullName)
                .FirstOrDefault(entry => entry.LinkTarget is not null && (entry is DirectoryInfo || Directory.Exists(entry.FullName)))?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return folder.FullName;
        }
    }

    /// <summary>
    /// Creates <paramref name="folder"/> and the missing folders above it up to <paramref name="binaryDir"/>, outermost first;
    /// journaled before each is made, so a rollback or a crash before the commit removes it again once its files are gone.
    /// </summary>
    private static async Task EnsureFolderAsync(string binaryDir, string folder, DeploymentOperation? operation, List<string> created, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(binaryDir).TrimEnd(Path.DirectorySeparatorChar);
        var missing = new Stack<string>();
        for (var current = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
             current.Length > root.Length && !Directory.Exists(current);
             current = Path.GetDirectoryName(current)!)
            missing.Push(current);
        while (missing.TryPop(out var path))
        {
            if (operation is not null)
                await operation.AddCompensationAsync(CreatedDirectoryCompensator.KindName, CreatedDirectoryCompensator.Describe(path), cancellationToken).ConfigureAwait(false);
            AtomicFile.CreateDirectoryDurable(path);
            created.Add(path);
        }
    }

    private static void RemoveEmptyFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Length))
            if (new DirectoryInfo(folder) is { Exists: true, LinkTarget: null }) FileCleanup.TryDeleteEmptyDirectory(folder);
    }

    /// <summary>dlssg_sm86/ and the folders in it, deepest first, each only when empty; never through a link.</summary>
    private static void PruneModFolder(string binaryDir)
    {
        var folder = new DirectoryInfo(Path.Combine(binaryDir, ModFolderName));
        if (!folder.Exists || folder.LinkTarget is not null) return;
        try
        {
            var all = SafeFileEnumeration.Directories(folder.FullName).Append(folder.FullName);
            RemoveEmptyFolders(all.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("fg2030", $"Could not tidy {folder.FullName}", ex);
        }
    }

    /// <summary>
    /// The mod's INI contract (0.4.x): frame generation keys. Before #261 the template also carried <c>[Compatibility] Router=</c>,
    /// which 0.4 no longer needs (it picks the RTX 20 / 30 kernels itself).
    /// </summary>
    internal static void ValidateIniText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 * 1024)
            throw new InvalidDataException($"{IniFileName} is empty or implausibly large.");
        if (!text.Contains("[FrameGeneration]", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("MaxGeneratedFrames=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{IniFileName} does not match the expected upstream contract.");
    }

    internal static void ValidateCachedDll(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("20/30 FG Unlock DLL was not downloaded.", path);
        var info = new FileInfo(path);
        if (info.Length < MinimumDllSize)
            throw new InvalidDataException($"20/30 FG Unlock DLL is implausibly small ({info.Length} bytes). ");
        if (PeArchitectureDetector.Detect(path) != PeArchitecture.X64)
            throw new InvalidDataException("20/30 FG Unlock payload is not a Windows x64 PE DLL.");
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

    private static string Sanitize(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

    [GeneratedRegex(@"^[vV]?[0-9]+(?:\.[0-9A-Za-z-]+)*$", RegexOptions.None, 1000)]
    private static partial Regex ReleaseTag();

    /// <summary>
    /// #281: the ⚙ writes one setting into the installed <c>dlssg_sm86.ini</c> (upstream's <c>WriteIniValue</c>; lines,
    /// comments and other keys kept). While the INI still holds what RHI placed, RHI keeps owning it: the record takes the
    /// new hash, so an update carries the ⚙'s settings into the new release's INI and removal deletes it, as upstream. An
    /// INI changed outside RHI stays the user's (kept whole on update and removal). Returns the refusal, or null.
    /// </summary>
    public string? SetIniSetting(DetectedGame game, Dlssg2030Setting setting, int index)
    {
        if (GetRecord(game) is not { } record) return Dlssg2030Ini.NotInstalledMessage;
        var iniPath = Path.Combine(record.BinaryDirectory, IniFileName);
        if (!File.Exists(iniPath)) return Dlssg2030Ini.NotInstalledMessage;
        var iniRecord = record.Files.FirstOrDefault(file => PathsEqual(file.Path, iniPath));
        var owned = iniRecord?.Sha256 is { Length: > 0 } recorded && FileHash.HexEquals(FileHash.Sha256Hex(iniPath), recorded);
        var lines = IniLines.Read(iniPath);
        if (Dlssg2030Ini.Apply(lines, setting, index) is { } refusal) return refusal;
        IniLines.Write(iniPath, lines, [record.BinaryDirectory]);
        if (owned)
        {
            var updated = LinuxDeploymentRecordStore.Clone(record);
            updated.Files.First(file => PathsEqual(file.Path, iniPath)).Sha256 = FileHash.Sha256Hex(iniPath);
            _records.Upsert(updated);
        }
        return null;
    }

    /// <summary>The installed INI's lines (empty when not installed or the file is gone), for the ⚙.</summary>
    public IReadOnlyList<string>? InstalledIniLines(DetectedGame game)
        => GetRecord(game) is { } record && File.Exists(Path.Combine(record.BinaryDirectory, IniFileName))
            ? IniLines.Read(Path.Combine(record.BinaryDirectory, IniFileName))
            : null;
}

public sealed record Dlssg2030ReleaseInfo(string Tag, string Version, string AssetName, string Url, string? Digest);

/// <summary>An extracted, validated package: paths are package-relative with '/' separators.</summary>
internal sealed record Dlssg2030Package(
    string Tag,
    string Version,
    string SourceUrl,
    string Root,
    string ArchiveSha256,
    bool DigestVerified,
    IReadOnlyList<string> ModFiles)
{
    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string IniPath => PathOf(Dlssg2030UnlockDeploymentService.IniFileName);

    /// <summary>The build for <paramref name="proxyName"/> in this package, or null when the package does not ship it.</summary>
    public string? ProxySource(string proxyName)
        => Dlssg2030UnlockDeploymentService.PackagePathFor(proxyName) is { } relative && File.Exists(PathOf(relative)) ? PathOf(relative) : null;
}

public sealed record Dlssg2030UpdateStatus(
    bool Success,
    string? InstalledVersion,
    string? LatestVersion,
    bool HasUpdate,
    string Message);
