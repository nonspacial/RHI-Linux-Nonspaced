using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Persistence;

/// <summary>The mods the user has already been told about, per list (#185; upstream <c>seen_wiki_mods.json</c>, <c>seen_luma_mods.json</c>, <c>seen_ultraplus_mods.json</c>).</summary>
public sealed class SeenModsFile
{
    [JsonPropertyName("renodx")] public List<string> RenoDx { get; set; } = [];
    [JsonPropertyName("luma")] public List<string> Luma { get; set; } = [];
    [JsonPropertyName("ultraPlus")] public List<string> UltraPlus { get; set; } = [];
}

/// <summary>
/// #185: the "new mods" seen sets in one file under RHI's data folder (<c>~/.local/share/rhi-linux/seen-mods.json</c>),
/// through <see cref="JsonFileStore{T}"/> (an unreadable file is copied aside and never overwritten).
/// </summary>
public sealed class SeenModsStore
{
    private readonly JsonFileStore<SeenModsFile> _store;

    public SeenModsStore(string? path = null)
        => _store = new JsonFileStore<SeenModsFile>(path ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "seen-mods.json"),
            new JsonSerializerOptions { WriteIndented = true },
            parse: text => JsonSerializer.Deserialize<SeenModsFile>(text) is { } file
                ? new SeenModsFile { RenoDx = file.RenoDx ?? [], Luma = file.Luma ?? [], UltraPlus = file.UltraPlus ?? [] }
                : null);

    public string FilePath => _store.FilePath;

    /// <summary>The seen sets; empty sets when the file is missing; null when it exists but cannot be read (nothing is then shown or seeded).</summary>
    public SeenModsFile? Load()
    {
        var load = _store.Load();
        return load.IsUnreadable ? null : load.Value ?? new SeenModsFile();
    }

    /// <summary>Writes the seen sets (refused while an unreadable file is kept aside).</summary>
    public void Save(SeenModsFile file)
    {
        if (Path.GetDirectoryName(_store.FilePath) is { Length: > 0 } directory) RHI.Core.Deployment.AtomicFile.CreateDirectoryDurable(directory);
        _store.Save(file);
    }
}
