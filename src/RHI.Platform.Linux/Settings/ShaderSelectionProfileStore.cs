using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

/// <summary>
/// A named shader selection (#93; upstream <c>ShaderProfile</c>): the packs and, per pack, the files left out. The JSON
/// shape is upstream's (<c>Name</c>, <c>SelectedPacks</c>, <c>FileExclusions</c>), so a profile zip from either app reads
/// in the other.
/// </summary>
public sealed record ShaderSelectionProfile
{
    public string Name { get; init; } = "Profile";
    public List<string> SelectedPacks { get; init; } = [];
    public Dictionary<string, List<string>> FileExclusions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// <c>~/.config/rhi-linux/shader-profiles.json</c>: the saved shader profiles, global like upstream's (not per game,
/// so nothing moves with a game). Read and written through <see cref="JsonFileStore{T}"/>: missing = none, unreadable =
/// copied aside and saves refused until it reads again.
/// </summary>
public sealed class ShaderSelectionProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly JsonFileStore<List<ShaderSelectionProfile>> _file;

    public ShaderSelectionProfileStore(string? path = null)
        => _file = new JsonFileStore<List<ShaderSelectionProfile>>(path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "shader-profiles.json"), Options,
            parse: text => Normalize(JsonSerializer.Deserialize<List<ShaderSelectionProfile>>(text, Options)));

    public string FilePath => _file.FilePath;

    public IReadOnlyList<ShaderSelectionProfile> Load() => _file.LoadOr(() => []);

    /// <summary>Replaces the saved list (throws an <see cref="IOException"/> when refused).</summary>
    public void Save(IReadOnlyList<ShaderSelectionProfile> profiles) => _file.Save(profiles.ToList());

    /// <summary>Drops nameless entries and gives exclusions a case-insensitive map (a hand-edited file must not throw).</summary>
    internal static List<ShaderSelectionProfile>? Normalize(List<ShaderSelectionProfile>? profiles)
        => profiles?.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p with
            {
                Name = p.Name.Trim(),
                SelectedPacks = (p.SelectedPacks ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FileExclusions = JsonStoreMaps.CaseInsensitive(p.FileExclusions ?? [])
            })
            .ToList();
}

/// <summary>
/// The shareable zip of a shader profile (#93; upstream <c>ShaderProfileService.BuildExportZip</c>): the deployed files
/// of each selected pack under <c>Shaders/&lt;pack&gt;/…</c> and <c>Textures/&lt;pack&gt;/…</c> (the user's and the
/// global exclusions left out) plus <c>shader_import.json</c>. Written with <see cref="AtomicFile"/> into RHI's own
/// folder (<c>$XDG_DATA_HOME/rhi-linux/Shader profiles</c>), never into a game. Upstream copies the zip to the clipboard
/// to paste into Discord; on Linux the folder is opened instead. Import reads only <c>shader_import.json</c> (the
/// selection): the packs themselves come from their catalogue sources when applied, never from a file someone sent.
/// </summary>
public static class ShaderProfileArchive
{
    public const string ProfileEntry = "shader_import.json";

    /// <summary>Largest export written (shader packs are a few MB; the zip is built in memory).</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    /// <summary>Largest <see cref="ProfileEntry"/> read on import.</summary>
    public const long MaxProfileBytes = 1024 * 1024;

    /// <summary>Fixed refusal texts (PT-BR presentation pairs).</summary>
    public const string TooLargeMessage = "The shader profile export is larger than the limit (512 MiB).";
    public const string NotAProfileMessage = "This zip is not a shader profile (it has no shader_import.json).";
    public const string UnreadableProfileMessage = "The shader profile in this zip cannot be read.";

    public static string DefaultFolder => Path.Combine(LinuxPaths.DataHome, "rhi-linux", "Shader profiles");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Writes the zip and returns its path (<c>RHI_Shaders_&lt;profile&gt;_&lt;time&gt;.zip</c>).</summary>
    public static async Task<string> ExportAsync(ShaderSelectionProfile profile, ReShadeShaderPackService packs, string folder,
        DateTime now, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var packId in profile.SelectedPacks)
            {
                foreach (var (selector, staged) in await packs.ListDeployedPackFilesAsync(packId, cancellationToken).ConfigureAwait(false))
                {
                    var slash = selector.IndexOf('/');
                    var entryName = $"{selector[..slash]}/{packId}/{selector[(slash + 1)..]}";
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var input = File.OpenRead(staged);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    if (buffer.Length > MaxBytes) throw new InvalidDataException(TooLargeMessage);
                }
            }
            var json = zip.CreateEntry(ProfileEntry);
            await using (var output = json.Open())
                await output.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(profile, Options), cancellationToken).ConfigureAwait(false);
        }
        if (buffer.Length > MaxBytes) throw new InvalidDataException(TooLargeMessage);
        var safe = string.Concat(profile.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_');
        var path = Path.Combine(folder, $"RHI_Shaders_{(safe.Length == 0 ? "Profile" : safe)}_{now:yyyy-MM-dd_HH-mm-ss}.zip");
        Directory.CreateDirectory(folder);
        await AtomicFile.WriteAllBytesAsync(path, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>The profile a zip carries (its <see cref="ProfileEntry"/>); throws <see cref="InvalidDataException"/> otherwise.</summary>
    public static ShaderSelectionProfile ReadProfile(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(ProfileEntry) ?? throw new InvalidDataException(NotAProfileMessage);
        if (entry.Length > MaxProfileBytes) throw new InvalidDataException(UnreadableProfileMessage);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        ShaderSelectionProfile? read;
        try { read = JsonSerializer.Deserialize<ShaderSelectionProfile>(reader.ReadToEnd(), Options); }
        catch (JsonException ex) { throw new InvalidDataException(UnreadableProfileMessage, ex); }
        return (read is null ? null : ShaderSelectionProfileStore.Normalize([read])?.SingleOrDefault())
               ?? throw new InvalidDataException(UnreadableProfileMessage);
    }
}
