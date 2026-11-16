using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>One game's OptiScaler.ini choices (#272): the edits applied in All Settings ("Section|Key" → value) and the saved INI it uses.</summary>
public sealed class OptiScalerIniChoices
{
    public Dictionary<string, string> Edits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name of the saved INI (<see cref="OptiScalerIniLibrary"/>) this game uses; null = RHI's template.</summary>
    public string? SavedIni { get; set; }

    internal bool IsEmpty => Edits.Count == 0 && SavedIni is null;
}

/// <summary>
/// <c>~/.config/rhi-linux/optiscaler-ini-choices.json</c> (#272, upstream <c>OsIniEdits</c> / <c>OsIniProfile</c> per game):
/// what is written into a game's OptiScaler.ini again after OptiScaler is installed there again (a reinstall or a version
/// switch). Cleared when the user removes OptiScaler, as upstream.
/// </summary>
public sealed class OptiScalerIniChoicesStore : IGameStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly JsonFileStore<Dictionary<string, OptiScalerIniChoices>> _file;
    private readonly object _gate = new();

    public OptiScalerIniChoicesStore(string? path = null)
        => _file = new JsonFileStore<Dictionary<string, OptiScalerIniChoices>>(
            path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "optiscaler-ini-choices.json"), JsonOptions);

    public OptiScalerIniChoices Get(DetectedGame game)
    {
        lock (_gate)
        {
            if (!GameRecordIdentity.TryGet(Load(), game, out var choices) || choices is null) return new OptiScalerIniChoices();
            return new OptiScalerIniChoices
            {
                Edits = new Dictionary<string, string>(choices.Edits ?? [], StringComparer.OrdinalIgnoreCase),
                SavedIni = OptiScalerIniLibrary.CleanName(choices.SavedIni)
            };
        }
    }

    /// <summary>Applied edits are added to the game's saved ones (a later value for a key replaces the earlier one).</summary>
    public void AddEdits(DetectedGame game, IEnumerable<KeyValuePair<string, string>> edits)
        => Change(game, choices => { foreach (var (id, value) in edits) choices.Edits[id] = value; });

    public void ForgetEdits(DetectedGame game) => Change(game, choices => choices.Edits.Clear());

    public void SetSavedIni(DetectedGame game, string? name) => Change(game, choices => choices.SavedIni = OptiScalerIniLibrary.CleanName(name));

    /// <summary>The user removed OptiScaler: nothing is written again at the next install (upstream).</summary>
    public void Clear(DetectedGame game) => Change(game, choices => { choices.Edits.Clear(); choices.SavedIni = null; });

    private void Change(DetectedGame game, Action<OptiScalerIniChoices> change)
    {
        lock (_gate)
        {
            var map = Load();
            var choices = Get(game);
            change(choices);
            if (choices.IsEmpty) GameRecordIdentity.Remove(map, game);
            else GameRecordIdentity.Set(map, game, choices);
            _file.Save(map);
        }
    }

    /// <summary>"Repair after move": entries under the game's old folder key follow it.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
            return _file.Stage(current =>
            {
                var map = JsonStoreMaps.CaseInsensitive(current);
                return move.MoveRecordEntries(map) ? map : null;
            });
    }

    private Dictionary<string, OptiScalerIniChoices> Load() => JsonStoreMaps.CaseInsensitive(_file.Load().Value);
}

/// <summary>
/// #272 (upstream 2.9.3 <c>OsIniLibrary</c>): the user's saved OptiScaler.ini files ("F5 super ini"), any number, in
/// <c>$XDG_DATA_HOME/rhi-linux/optiscaler/saved-inis/&lt;name&gt;.ini</c>. Saved from a game, added from a file, and put into
/// a game as its OptiScaler.ini.
/// </summary>
public sealed class OptiScalerIniLibrary
{
    public const int MaxNameLength = 60;
    /// <summary>Largest INI added from a file (OptiScaler's own is under 100 KB).</summary>
    public const long MaxIniBytes = 1024 * 1024;
    public const string NotAnIniMessage = "That file is not an OptiScaler.ini RHI can use (a text INI under 1 MB).";

    // Windows' file-name rules (upstream), so a name means the same on both and never names a path.
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public OptiScalerIniLibrary(string? directory = null)
        => Directory = directory ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "optiscaler", "saved-inis");

    public string Directory { get; }

    /// <summary>Saved names, A to Z.</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return [];
            return System.IO.Directory.GetFiles(Directory, "*.ini")
                .Where(path => new FileInfo(path).LinkTarget is null)
                .Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Where(name => CleanName(name) == name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("optiscaler", "Saved OptiScaler inis could not be listed", ex);
            return [];
        }
    }

    /// <summary>A usable name from what the user typed (upstream <c>CleanName</c>), or null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = new string(name.Trim().Where(ch => !char.IsControl(ch) && Array.IndexOf(Invalid, ch) < 0).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].Trim();
        return clean.Length == 0 ? null : clean;
    }

    public string PathFor(string name) => Path.Combine(Directory, (CleanName(name) ?? throw new ArgumentException("Not a saved ini name.", nameof(name))) + ".ini");

    public bool Exists(string? name) => CleanName(name) is { } clean && File.Exists(PathFor(clean));

    /// <summary>Copies <paramref name="sourceIni"/> in as <paramref name="name"/> (replacing one with that name); refused when it is no INI text.</summary>
    public void Save(string name, string sourceIni)
    {
        if (!LooksLikeIni(sourceIni)) throw new InvalidDataException(NotAnIniMessage);
        System.IO.Directory.CreateDirectory(Directory);
        RHI.Core.Deployment.AtomicFile.Copy(sourceIni, PathFor(name));
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) RHI.Core.Deployment.AtomicFile.Delete(path);
    }

    /// <summary>A name for an INI added from a file (upstream <c>SuggestName</c>): its file name, or its folder's for "OptiScaler.ini"; never an existing one.</summary>
    public string SuggestName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "");
            if (!string.IsNullOrWhiteSpace(folder)) name = folder;
        }
        var clean = CleanName(name) ?? "Imported ini";
        var unique = clean;
        for (var i = 2; Exists(unique); i++) unique = $"{clean} ({i})";
        return unique;
    }

    /// <summary>A regular text file under <see cref="MaxIniBytes"/> with no NUL byte.</summary>
    public static bool LooksLikeIni(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaxIniBytes) return false;
        return !File.ReadAllBytes(path).Contains((byte)0);
    }
}
