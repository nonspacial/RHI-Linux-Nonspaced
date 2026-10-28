using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed record UpstreamRhiAddonDeploymentOptions(
    ManagedComponentKind Kind,
    string SectionId,
    string DisplayName,
    string ReleaseApiUrlFallback,
    long MinimumSize,
    string? ExactAssetName64 = null,
    string? ExactAssetName32 = null,
    string? AssetNamePattern64 = null,
    string? AssetNamePattern32 = null,
    string? DeployFileName64 = null,
    string? DeployFileName32 = null,
    bool X64Only = false);

/// <summary>
/// Binds one Linux-managed ReShade add-on to the addonPacks entry from the upstream RHI manifest.
/// The manifest is resolved at install time so upstream URL corrections (releaseApiUrl/disabled) are
/// consumed by the port without weakening Linux ownership, hash or rollback rules.
/// </summary>
public sealed class UpstreamRhiAddonDeploymentService
{
    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly UpstreamRhiManifestService _manifest;
    private readonly UpstreamRhiAddonDeploymentOptions _options;
    private readonly string? _cacheDir;
    private readonly RHI.Core.Deployment.DeploymentJournal? _journal;

    public UpstreamRhiAddonDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        UpstreamRhiManifestService manifest,
        UpstreamRhiAddonDeploymentOptions options,
        string? cacheDir = null,
        RHI.Core.Deployment.DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _manifest = manifest;
        _options = options;
        _cacheDir = cacheDir;
    }

    public ManagedComponentKind Kind => _options.Kind;

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, releaseTag: null, cancellationToken);

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string? releaseTag,
        CancellationToken cancellationToken = default)
    {
        if (_options.Kind == ManagedComponentKind.Dlss5Dx11Bridge &&
            _records.Find(game, ManagedComponentKind.ShortFuse) is not null)
            return ComponentOperationResult.Fail("Remove ShortFuse before installing DLSS5 DX11 Bridge. Both workflows own incompatible injection/orchestration state.");

        if (_options.X64Only && !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
            GameArchitecture.Of(game) != PeArchitecture.X64)
            return ComponentOperationResult.Fail($"{_options.DisplayName} is available only for 64-bit Windows games.");

        UpstreamAddonPackDefinition pack;
        try
        {
            var packs = await _manifest.FetchAddonPacksAsync(cancellationToken).ConfigureAwait(false);
            if (!packs.TryGetValue(_options.SectionId, out var resolvedPack))
                return ComponentOperationResult.Fail($"O manifesto do RHI não contém addonPacks/{_options.SectionId}.");
            pack = resolvedPack;
            if (pack.Disabled)
                return ComponentOperationResult.Fail($"{pack.PackageName} is disabled by the upstream RHI manifest.");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Não foi possível carregar o manifesto de add-ons do RHI: {ex.Message}");
        }

        var releaseApiUrl = string.IsNullOrWhiteSpace(pack.ReleaseApiUrl)
            ? _options.ReleaseApiUrlFallback
            : pack.ReleaseApiUrl!;
        if (string.IsNullOrWhiteSpace(releaseApiUrl))
            return ComponentOperationResult.Fail($"{pack.PackageName} não possui URL da API de releases do GitHub no manifesto do RHI.");

        var service = BuildService(pack.PackageName, releaseApiUrl);
        return await service.InstallAsync(game, releaseTag, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var releaseApiUrl = _options.ReleaseApiUrlFallback;
        try
        {
            var packs = await _manifest.FetchAddonPacksAsync(cancellationToken).ConfigureAwait(false);
            if (packs.TryGetValue(_options.SectionId, out var pack) && !string.IsNullOrWhiteSpace(pack.ReleaseApiUrl))
                releaseApiUrl = pack.ReleaseApiUrl!;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // The fallback endpoint is part of the Linux contract and is safe for an offline/stale manifest.
        }

        return await new GitHubReleaseAssetResolver(_http)
            .ListVersionsAsync(releaseApiUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(
        DetectedGame game,
        string? releaseTag = null,
        CancellationToken cancellationToken = default)
    {
        // F6 (review w12/mfg): a held game and a file removed outside RHI are told before any network call (the manifest
        // included), so offline they still read as what they are, not "update check failed".
        if (_records.Find(game, _options.Kind) is { } managed)
        {
            if (await ManagedUpdateFileGuard.HeldAsync(_journal, game, _options.Kind, managed.Version).ConfigureAwait(false) is { } held)
                return held;
            if (ManagedUpdateFileGuard.Removed(managed.Files.Select(file => file.Path), managed.Version) is { } removed)
                return removed;
        }
        try
        {
            var (displayName, releaseApiUrl) = await ResolveDefinitionAsync(cancellationToken).ConfigureAwait(false);
            return await BuildService(displayName, releaseApiUrl)
                .CheckUpdateAsync(game, releaseTag, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var record = _records.Find(game, _options.Kind);
            return new ManagedUpdateCheck(record is not null, false, $"{_options.DisplayName} update check failed: {ex.Message}", record?.Version, CouldNotCheck: true);
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        string? releaseTag = null,
        CancellationToken cancellationToken = default)
    {
        // w12 follow-up (re-review mfg N4): as the check (F6), a held game and a file removed outside RHI are told before the
        // manifest is asked, so a direct Update offline reads the hold's restart text or the removed line, not a network
        // failure.
        if (_records.Find(game, _options.Kind) is { } managed)
        {
            if (await DeploymentScope.HoldRefusalAsync(_journal, game, _options.Kind).ConfigureAwait(false) is { } held)
                return ComponentOperationResult.Fail(held);
            if (managed.Files.FirstOrDefault(file => !File.Exists(file.Path)) is { } removed)
                return ComponentOperationResult.Fail(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + removed.Path);
        }
        try
        {
            var (displayName, releaseApiUrl) = await ResolveDefinitionAsync(cancellationToken).ConfigureAwait(false);
            return await BuildService(displayName, releaseApiUrl)
                .UpdateAsync(game, releaseTag, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"{_options.DisplayName} update failed: {ex.Message}");
        }
    }

    private async Task<(string DisplayName, string ReleaseApiUrl)> ResolveDefinitionAsync(CancellationToken cancellationToken)
    {
        var displayName = _options.DisplayName;
        var releaseApiUrl = _options.ReleaseApiUrlFallback;
        var packs = await _manifest.FetchAddonPacksAsync(cancellationToken).ConfigureAwait(false);
        if (packs.TryGetValue(_options.SectionId, out var pack))
        {
            if (pack.Disabled)
                throw new InvalidOperationException($"{pack.PackageName} is disabled by the upstream RHI manifest.");
            if (!string.IsNullOrWhiteSpace(pack.PackageName)) displayName = pack.PackageName;
            if (!string.IsNullOrWhiteSpace(pack.ReleaseApiUrl)) releaseApiUrl = pack.ReleaseApiUrl!;
        }
        if (string.IsNullOrWhiteSpace(releaseApiUrl))
            throw new InvalidOperationException($"{displayName} has no GitHub releases API URL.");
        return (displayName, releaseApiUrl);
    }

    public Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => BuildService(_options.DisplayName, _options.ReleaseApiUrlFallback)
            .RemoveAsync(game, cancellationToken);

    private GitHubReShadeAddonDeploymentService BuildService(string displayName, string releaseApiUrl)
        => new(
            _http,
            _records,
            new GitHubReShadeAddonDefinition(
                _options.Kind,
                displayName,
                releaseApiUrl,
                _options.ExactAssetName64,
                _options.ExactAssetName32,
                _options.MinimumSize,
                BodyVersionPattern: null,
                AssetNamePattern64: _options.AssetNamePattern64,
                AssetNamePattern32: _options.AssetNamePattern32,
                DeployFileName64: _options.DeployFileName64,
                DeployFileName32: _options.DeployFileName32),
            _cacheDir,
            _journal);
}
