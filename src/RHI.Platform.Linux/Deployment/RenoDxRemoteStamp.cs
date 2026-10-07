using System.Globalization;
using System.Text.Json;

namespace RHI.Platform.Linux.Deployment;

public enum RemoteStampComparison
{
    Same,
    Changed,
    Unknown
}

/// <summary>
/// HTTP validators of one remote RenoDX add-on as served when it was downloaded. RenoDX snapshots
/// are rebuilt in place under the same URL and often keep their size, so ETag and Last-Modified
/// (sent by both GitHub Releases and GitHub Pages/renodx.com) decide whether the remote changed;
/// Content-Length is only the last resort (upstream 2.7.9 rule).
/// </summary>
public sealed record RenoDxRemoteStamp(string? ETag, DateTimeOffset? LastModified, long? Length)
{
    public bool HasValidator => !string.IsNullOrWhiteSpace(ETag) || LastModified is not null || Length is > 0;

    public static RenoDxRemoteStamp FromResponse(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag?.Tag;
        var length = response.Content.Headers.ContentLength;
        return new RenoDxRemoteStamp(
            string.IsNullOrWhiteSpace(etag) ? null : etag,
            response.Content.Headers.LastModified,
            length is > 0 ? length : null);
    }

    /// <summary>
    /// Compares the strongest validator both sides carry: ETag, then Last-Modified, then size.
    /// Unknown when no validator is shared, so the caller decides what an unprovable state means.
    /// </summary>
    public static RemoteStampComparison Compare(RenoDxRemoteStamp? recorded, RenoDxRemoteStamp? remote)
    {
        if (recorded is null || remote is null) return RemoteStampComparison.Unknown;
        if (!string.IsNullOrWhiteSpace(recorded.ETag) && !string.IsNullOrWhiteSpace(remote.ETag))
            return string.Equals(NormalizeETag(recorded.ETag), NormalizeETag(remote.ETag), StringComparison.Ordinal)
                ? RemoteStampComparison.Same
                : RemoteStampComparison.Changed;
        if (recorded.LastModified is { } recordedTime && remote.LastModified is { } remoteTime)
            return recordedTime.UtcDateTime == remoteTime.UtcDateTime ? RemoteStampComparison.Same : RemoteStampComparison.Changed;
        if (recorded.Length is > 0 && remote.Length is > 0)
            return recorded.Length == remote.Length ? RemoteStampComparison.Same : RemoteStampComparison.Changed;
        return RemoteStampComparison.Unknown;
    }

    // A weak validator from a compressing proxy names the same entity as its strong form.
    private static string NormalizeETag(string etag)
        => etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? etag[2..] : etag;

    public string Serialize() => JsonSerializer.Serialize(new Dto
    {
        ETag = ETag,
        LastModified = LastModified?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        Length = Length
    });

    public static RenoDxRemoteStamp? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json);
            if (dto is null) return null;
            DateTimeOffset? lastModified = DateTimeOffset.TryParse(dto.LastModified, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
            var stamp = new RenoDxRemoteStamp(dto.ETag, lastModified, dto.Length);
            return stamp.HasValidator ? stamp : null;
        }
        catch (JsonException)
        {
            // An unreadable stamp proves nothing; callers treat it like a record that never had one.
            return null;
        }
    }

    private sealed class Dto
    {
        public string? Url { get; set; }
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public long? Length { get; set; }
    }

    /// <summary>Cache sidecar: the URL the cached file came from and its stamp. Older sidecars hold the bare URL.</summary>
    internal static string SerializeSidecar(string url, RenoDxRemoteStamp? stamp)
        => JsonSerializer.Serialize(new Dto
        {
            Url = url,
            ETag = stamp?.ETag,
            LastModified = stamp?.LastModified?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            Length = stamp?.Length
        });

    internal static (string? Url, RenoDxRemoteStamp? Stamp) ReadSidecar(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{')) return (trimmed, null);
        try
        {
            var url = JsonSerializer.Deserialize<Dto>(trimmed)?.Url;
            return (url, Deserialize(trimmed));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
