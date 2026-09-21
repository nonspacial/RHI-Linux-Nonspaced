using System.Text;
using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed record LumaPolicySnapshot(
    bool ManifestDefault,
    bool RenoDxCompatible,
    LumaModePreference UserPreference,
    bool EffectiveModeEnabled,
    string Authority,
    string Summary);

/// <summary>
/// Linux policy layer for the upstream RHI Luma mode contract. It consumes
/// lumaDefaultGames and lumaRenodxCompat, preserves explicit user enable/disable authority,
/// and never mutates package state on its own. Package install/remove remains transactional
/// in LumaFrameworkDeploymentService.
/// </summary>
public sealed class LumaPolicyService
{
    private readonly UpstreamRhiManifestService _manifest;
    private readonly LumaModePreferenceStore _preferences;
    private readonly object _gate = new();
    private readonly Dictionary<string, LumaPolicySnapshot> _cache = new(StringComparer.Ordinal);

    public LumaPolicyService(UpstreamRhiManifestService manifest, LumaModePreferenceStore? preferences = null)
    {
        _manifest = manifest;
        _preferences = preferences ?? new LumaModePreferenceStore();
    }

    public async Task<LumaPolicySnapshot> PrimeAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        IReadOnlySet<string> defaults;
        IReadOnlySet<string> compat;
        try
        {
            defaults = await _manifest.FetchLumaDefaultGamesAsync(cancellationToken).ConfigureAwait(false);
            compat = await _manifest.FetchLumaRenoDxCompatAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            compat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var isDefault = MatchesAny(game, defaults);
        var coexist = MatchesAny(game, compat);
        var pref = _preferences.Get(game);
        var enabled = pref switch
        {
            LumaModePreference.Enabled => true,
            LumaModePreference.Disabled => false,
            _ => isDefault
        };
        var authority = pref switch
        {
            LumaModePreference.Enabled => "user enabled",
            LumaModePreference.Disabled => "user disabled",
            _ when isDefault => "upstream lumaDefaultGames",
            _ => "automatic/no default"
        };
        var summary = $"Luma mode {(enabled ? "enabled" : "not enabled")} · authority: {authority}" +
                      (coexist ? " · RenoDX coexistence allowed by lumaRenodxCompat" : " · RenoDX/Luma mutually exclusive");
        var snapshot = new LumaPolicySnapshot(isDefault, coexist, pref, enabled, authority, summary);
        lock (_gate) _cache[LinuxDeploymentRecordStore.GameKey(game)] = snapshot;
        return snapshot;
    }

    public LumaPolicySnapshot GetCachedOrDefault(DetectedGame game)
    {
        var key = LinuxDeploymentRecordStore.GameKey(game);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var value)) return value;
        }
        var pref = _preferences.Get(game);
        var enabled = pref == LumaModePreference.Enabled;
        var authority = pref switch
        {
            LumaModePreference.Enabled => "user enabled",
            LumaModePreference.Disabled => "user disabled",
            _ => "automatic/not primed"
        };
        return new(false, false, pref, enabled, authority,
            $"Luma mode {(enabled ? "enabled" : "not enabled")} · authority: {authority} · manifest policy not primed");
    }

    public LumaPolicySnapshot SetPreference(DetectedGame game, LumaModePreference preference)
    {
        _preferences.Set(game, preference);
        var previous = GetCachedOrDefault(game);
        var enabled = preference switch
        {
            LumaModePreference.Enabled => true,
            LumaModePreference.Disabled => false,
            _ => previous.ManifestDefault
        };
        var authority = preference switch
        {
            LumaModePreference.Enabled => "user enabled",
            LumaModePreference.Disabled => "user disabled",
            _ when previous.ManifestDefault => "upstream lumaDefaultGames",
            _ => "automatic/no default"
        };
        var updated = previous with
        {
            UserPreference = preference,
            EffectiveModeEnabled = enabled,
            Authority = authority,
            Summary = $"Luma mode {(enabled ? "enabled" : "not enabled")} · authority: {authority}" +
                      (previous.RenoDxCompatible ? " · RenoDX coexistence allowed by lumaRenodxCompat" : " · RenoDX/Luma mutually exclusive")
        };
        lock (_gate) _cache[LinuxDeploymentRecordStore.GameKey(game)] = updated;
        return updated;
    }

    private static bool MatchesAny(DetectedGame game, IReadOnlySet<string> names)
    {
        if (names.Count == 0) return false;
        if (!string.IsNullOrWhiteSpace(game.CanonicalManifestName) && names.Contains(game.CanonicalManifestName)) return true;
        if (names.Contains(game.Name)) return true;
        var target = Normalize(game.CanonicalManifestName ?? game.Name);
        if (target.Length > 0 && names.Any(name => Normalize(name) == target)) return true;

        var evidence = new[] { game.InstallPath, game.BinaryDirectory, game.ExecutablePath }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .ToArray();
        var matches = names.Where(name =>
        {
            var normalized = Normalize(name);
            return normalized.Length >= 6 && evidence.Any(item => ContainsPhrase(item, normalized));
        }).Take(2).ToArray();
        return matches.Length == 1;
    }

    private static bool ContainsPhrase(string haystack, string phrase)
        => haystack == phrase || haystack.StartsWith(phrase + " ", StringComparison.Ordinal)
           || haystack.EndsWith(" " + phrase, StringComparison.Ordinal)
           || haystack.Contains(" " + phrase + " ", StringComparison.Ordinal);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
