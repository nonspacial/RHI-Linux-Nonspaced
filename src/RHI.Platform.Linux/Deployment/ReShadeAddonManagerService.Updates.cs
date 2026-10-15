using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #194 (upstream 2.8.7 <c>AddonPackService.CheckAndUpdateAllAsync</c>, parity T-19): the Addon Manager's add-ons take
/// part in Update All and the background updates. Each placed add-on records what it was downloaded as — the release
/// tag (GitHub API or a <c>/releases/download/&lt;tag&gt;/</c> link), else the server's validators (ETag, Last-Modified,
/// size), or for a Custom add-on its file's SHA-256 — and the check compares that with the add-on list's current source.
/// An update re-applies the game's selection in one journaled operation. A check that cannot tell (the list could not be
/// fetched, an add-on gone from it, no validators) is "could not be checked", never an update or current; an add-on
/// RHI turned off for ReShade's No Addons build is held.
/// </summary>
public sealed partial class ReShadeAddonManagerService
{
    internal const string VersionsMetadataKey = "AddonManager.Versions";
    public const string DisplayName = "ReShade Addon Manager";

    /// <summary>Fixed text of a check that found every add-on current (PT-BR pair).</summary>
    public const string CurrentMessage = "ReShade Addon Manager add-ons are current.";
    /// <summary>Fixed text before the add-on names when an update is available (PT-BR pair).</summary>
    public const string UpdateAvailablePrefix = "ReShade Addon Manager add-on update available: ";
    /// <summary>Fixed text before the reason when the check could not tell (PT-BR pair).</summary>
    public const string CouldNotCheckPrefix = "ReShade Addon Manager add-ons could not be checked: ";
    /// <summary>The reason when crosire's add-on list could not be fetched (PT-BR pair).</summary>
    public const string ListUnavailableReason = "the add-on list could not be downloaded.";
    /// <summary>Fixed text before the add-on name when it is no longer in the add-on list (PT-BR pair).</summary>
    public const string NotInListPrefix = "no longer in the add-on list: ";
    /// <summary>Fixed text before the add-on name when its source says nothing that tells versions apart (PT-BR pair).</summary>
    public const string NoValidatorPrefix = "its download gives nothing to compare: ";
    /// <summary>Fixed text before the count of add-ons an update re-applied (PT-BR pair).</summary>
    public const string UpdatedPrefix = "ReShade Addon Manager add-ons updated: ";

    /// <summary>The Update All check of a game's Addon Manager add-ons.</summary>
    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        LinuxDeploymentRecord? record;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TryReconcile(out _);
            record = _records.Find(game, RecordKind);
        }
        finally
        {
            _gate.Release();
        }
        if (record is null) return new ManagedUpdateCheck(false, false, $"{DisplayName} is not managed by RHI Linux.");
        var installed = record.Version;

        var (catalog, live) = await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
        var hasRemote = record.Files.Any(file => !IsCustomId(AddonIdOf(record, file.Path)));
        if (!live && (hasRemote || record.Files.Count == 0))
            return new ManagedUpdateCheck(true, false, CouldNotCheckPrefix + ListUnavailableReason, installed, CouldNotCheck: true);

        var architecture = RenoDxUpgradeDeploymentService.ResolveArchitecture(game);
        var byId = catalog.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var recorded = VersionsOf(record);
        var updates = new List<string>();
        string? cannotTell = null;
        foreach (var file in record.Files)
        {
            var id = AddonIdOf(record, file.Path);
            var name = Path.GetFileName(WithoutOffSuffix(file.Path));
            if (!byId.TryGetValue(id, out var definition))
            {
                cannotTell ??= NotInListPrefix + name;
                continue;
            }
            string? remote;
            try
            {
                remote = await CurrentVersionAsync(definition, architecture, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Warn("addon-manager", $"Could not check {definition.DisplayName} for an update", ex);
                remote = null;
            }
            if (remote is null)
            {
                cannotTell ??= NoValidatorPrefix + definition.DisplayName;
                continue;
            }
            // No recorded version (applied before #194): the update re-applies it once to record one (upstream does too).
            var same = recorded.TryGetValue(name, out var was) ? SameVersion(was, remote) : false;
            if (same == false) updates.Add(definition.DisplayName);
            else if (same is null) cannotTell ??= NoValidatorPrefix + definition.DisplayName;
        }

        if (updates.Count > 0)
        {
            var message = UpdateAvailablePrefix + string.Join(", ", updates);
            // #170: an add-on turned off for No Addons is held by ReShade's channel, not updated.
            return record.Files.Any(file => file.Path.EndsWith(ReShadeAddonSwitch.OffSuffix, StringComparison.Ordinal))
                ? new ManagedUpdateCheck(true, false, ReShadeAddonSwitch.HeldPrefix + DisplayName, installed, HeldBy: LinuxManagedComponentService.ReShadeNoAddonsName)
                : new ManagedUpdateCheck(true, true, message, installed);
        }
        if (cannotTell is not null)
            return new ManagedUpdateCheck(true, false, CouldNotCheckPrefix + cannotTell, installed, CouldNotCheck: true);
        return new ManagedUpdateCheck(true, false, CurrentMessage, installed, installed);
    }

    /// <summary>
    /// Update All's update: re-applies the game's selection (fresh downloads, one journaled operation). Refused while an
    /// add-on is turned off for No Addons (#170).
    /// </summary>
    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        LinuxDeploymentRecord? record;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TryReconcile(out _);
            record = _records.Find(game, RecordKind);
        }
        finally
        {
            _gate.Release();
        }
        if (record is null) return ComponentOperationResult.Fail($"{DisplayName} is not managed by RHI Linux.");
        if (record.Files.Any(file => file.Path.EndsWith(ReShadeAddonSwitch.OffSuffix, StringComparison.Ordinal)))
            return ComponentOperationResult.Fail(ReShadeAddonSwitch.HeldPrefix + DisplayName);
        var selected = SelectedIdsOf(record);
        var result = await ApplySelectionAsync(game, selected, cancellationToken).ConfigureAwait(false);
        return result.Success ? result with { Message = UpdatedPrefix + selected.Count } : result;
    }

    /// <summary>What the add-on list's source for <paramref name="definition"/> is now (null = cannot tell). Network.</summary>
    private async Task<string?> CurrentVersionAsync(ReShadeAddonDefinition definition, PeArchitecture architecture, CancellationToken cancellationToken)
    {
        var ext = architecture == PeArchitecture.X86 ? ".addon32" : ".addon64";
        if (definition.IsCustom)
        {
            var custom = architecture == PeArchitecture.X86 ? definition.CustomPath32 : definition.CustomPath64;
            return string.IsNullOrWhiteSpace(custom) || !File.Exists(custom) ? null : CustomVersion(custom);
        }
        if (!string.IsNullOrWhiteSpace(definition.ReleaseApiUrl))
            return TagVersion((await ResolveReleaseAssetAsync(definition.ReleaseApiUrl!, ext, cancellationToken).ConfigureAwait(false)).Tag);
        var url = (architecture == PeArchitecture.X86 ? definition.DownloadUrl32 : definition.DownloadUrl64) ?? definition.DownloadUrl;
        return string.IsNullOrWhiteSpace(url) ? null : await UrlVersionAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A download link's version: the tag of a GitHub <c>/releases/download/&lt;tag&gt;/</c> link (rolling <c>latest</c>
    /// and <c>snapshot</c> tags excepted, as upstream), else the server's validators from a HEAD request; null when neither
    /// tells (a failed request included). Never throws for a network failure.
    /// </summary>
    private async Task<string?> UrlVersionAsync(string url, CancellationToken cancellationToken)
    {
        if (ReleaseTagIn(url) is { } tag) return TagVersion(tag);
        try
        {
            using var response = await TrustedDownload.HeadAsync(_http, url, TrustedDownloadPolicy.ReShadeAddons, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var stamp = RenoDxRemoteStamp.FromResponse(response);
            return stamp.HasValidator ? HeadPrefix + stamp.Serialize() : null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    private const string TagPrefix = "tag:";
    private const string HeadPrefix = "head:";
    private const string CustomPrefix = "sha256:";

    private static string? TagVersion(string? tag) => string.IsNullOrWhiteSpace(tag) ? null : TagPrefix + tag.Trim();

    private static string CustomVersion(string path) => CustomPrefix + FileHash.Sha256Hex(path);

    private static bool IsCustomId(string id) => id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase);

    internal static string? ReleaseTagIn(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("download", StringComparison.OrdinalIgnoreCase) || !segments[i - 1].Equals("releases", StringComparison.OrdinalIgnoreCase))
                continue;
            var tag = Uri.UnescapeDataString(segments[i + 1]);
            return tag.Equals("latest", StringComparison.OrdinalIgnoreCase) || tag.Equals("snapshot", StringComparison.OrdinalIgnoreCase) ? null : tag;
        }
        return null;
    }

    /// <summary>True/false when the two versions tell; null when they cannot be compared (validators that do not overlap).</summary>
    internal static bool? SameVersion(string recorded, string current)
    {
        if (recorded.StartsWith(HeadPrefix, StringComparison.Ordinal) && current.StartsWith(HeadPrefix, StringComparison.Ordinal))
            return RenoDxRemoteStamp.Compare(RenoDxRemoteStamp.Deserialize(recorded[HeadPrefix.Length..]), RenoDxRemoteStamp.Deserialize(current[HeadPrefix.Length..])) switch
            {
                RemoteStampComparison.Same => true,
                RemoteStampComparison.Changed => false,
                _ => null
            };
        return string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> VersionsOf(LinuxDeploymentRecord record)
    {
        if (!record.Metadata.TryGetValue(VersionsMetadataKey, out var json) || string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(); }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    /// <summary>Records each placed add-on's version (by file name) and a readable summary as the record's version.</summary>
    private static void SetVersions(LinuxDeploymentRecord record, IReadOnlyList<(string Path, string? Version, string DisplayName)> placed)
    {
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, version, _) in placed)
            if (version is not null) versions[Path.GetFileName(path)] = version;
        record.Metadata[VersionsMetadataKey] = JsonSerializer.Serialize(versions, CanonicalOptions);
        var labels = placed.Select(item => $"{item.DisplayName} {Label(item.Version)}".Trim()).ToArray();
        record.Version = labels.Length == 0 ? null : string.Join(", ", labels);
    }

    private static string Label(string? version)
    {
        if (version is null) return "";
        if (version.StartsWith(TagPrefix, StringComparison.Ordinal)) return version[TagPrefix.Length..];
        if (version.StartsWith(CustomPrefix, StringComparison.Ordinal)) return "local " + version[CustomPrefix.Length..][..Math.Min(8, version.Length - CustomPrefix.Length)];
        if (version.StartsWith(HeadPrefix, StringComparison.Ordinal) && RenoDxRemoteStamp.Deserialize(version[HeadPrefix.Length..]) is { } stamp)
        {
            if (stamp.LastModified is { } modified) return modified.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (stamp.ETag?.Trim('"') is { Length: > 0 } etag) return etag[..Math.Min(8, etag.Length)];
        }
        return "";
    }
}
