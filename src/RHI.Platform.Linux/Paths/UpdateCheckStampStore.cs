using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Paths;

/// <summary>#292: when the automatic update pass last ran to the end, kept between launches.</summary>
public sealed class UpdateCheckStamp
{
    [JsonPropertyName("lastCheckUtc")]
    public DateTimeOffset? LastCheckUtc { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// #292 (upstream 2.8.9 <c>LastUpdateCheckUtc</c>): the time of the last automatic update pass, in its own file
/// (<c>~/.local/share/rhi-linux/update-check.json</c>), never in settings.json (the Settings dialog rebuilds the settings on
/// Save). A cache: a file that cannot be read counts as "never checked" (the next pass runs at once) and is replaced.
/// </summary>
public sealed class UpdateCheckStampStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly JsonFileStore<UpdateCheckStamp> _file;
    private readonly object _gate = new();

    public UpdateCheckStampStore(string? path = null)
        => _file = new JsonFileStore<UpdateCheckStamp>(path ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "update-check.json"),
            JsonOptions, JsonStoreKind.Cache);

    public string FilePath => _file.FilePath;

    /// <summary>The last pass's time, or null (never, or the file cannot be read).</summary>
    public DateTimeOffset? LastCheck()
    {
        lock (_gate) return _file.Load().Value?.LastCheckUtc;
    }

    /// <summary>Records a pass; a write that fails is logged by the caller and only means the next start checks sooner.</summary>
    public void Record(DateTimeOffset when)
    {
        lock (_gate)
        {
            var stamp = _file.Load().Value ?? new UpdateCheckStamp();
            stamp.LastCheckUtc = when.ToUniversalTime();
            _file.Save(stamp);
        }
    }
}
