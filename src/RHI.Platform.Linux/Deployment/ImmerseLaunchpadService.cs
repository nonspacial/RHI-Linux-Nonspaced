using System.IO.Compression;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>The files iMMERSE Launchpad needs, extracted into a stage folder (reshade-shaders relative path → staged file).</summary>
public sealed record LaunchpadStage(IReadOnlyList<(string Relative, string Source)> Files, string ArchiveSha256);

/// <summary>
/// w14/nr (#274), shared with HSF (#277): iMMERSE Launchpad (Marty McFly, the free iMMERSE repository), the ReShade effect
/// that makes motion vectors for effects reading them in the same frame (GITC Uplift, HSF). Upstream RHI 2.9.3
/// (<c>UpliftService.LaunchpadFiles</c>, <c>KeepLaunchpadFirst</c>; <c>5f409de</c>, <c>71af41e</c>). One service for every
/// component that uses it:
/// <list type="bullet">
/// <item><see cref="StageAsync"/> downloads the iMMERSE branch archive and extracts only Launchpad, its <c>MartysMods</c>
/// includes and its blue-noise texture.</item>
/// <item><see cref="PlaceAsync"/> puts them in the game's RHI shader tree (<c>reshade-shaders/RHI/iMMERSE-Launchpad</c>,
/// added to ReShade.ini's search paths by the caller) inside the caller's journaled scope. The files are shared: a file
/// another component's record already lists is listed again, never placed twice; a file nobody's record lists (the user's)
/// is left alone. <see cref="DeleteOwnedAsync"/> deletes a file only when no other record still lists it.</item>
/// <item><see cref="Enable"/> / <see cref="KeepFirst"/>: the preset edits (on above the effect that reads it; moved to the top
/// of the list, above GITC Uplift and HSF, when ReShade or HSF put it lower). <see cref="KeepFirstAsync"/> does the move in
/// a game, only while the game is not running, journaled on the calling component's record.</item>
/// </list>
/// </summary>
public sealed class ImmerseLaunchpadService
{
    public const string Token = "MartysMods_Launchpad@MartysMods_LAUNCHPAD.fx";
    public const string RepositoryUrl = "https://github.com/martymcmodding/iMMERSE";
    public const string ArchiveUrl = "https://codeload.github.com/martymcmodding/iMMERSE/zip/refs/heads/main";
    public const string TreeName = "iMMERSE-Launchpad";
    public const string EffectSearchPath = @".\reshade-shaders\RHI\iMMERSE-Launchpad\Shaders\**";
    public const string TextureSearchPath = @".\reshade-shaders\RHI\iMMERSE-Launchpad\Textures\**";
    /// <summary>The record metadata part its download is named under (<see cref="VerifiedDownload.Record"/>).</summary>
    public const string DownloadPart = "Launchpad";
    public const string EffectFile = "MartysMods_LAUNCHPAD.fx";
    public const string NoiseTexture = "iMMERSE_bluenoise_opt.png";
    public const string MissingMessage = "The iMMERSE archive does not hold iMMERSE Launchpad (MartysMods_LAUNCHPAD.fx with its MartysMods includes and texture).";

    private readonly HttpClient _http;
    private readonly IGameRunningProbe _running;

    public ImmerseLaunchpadService(HttpClient http, IGameRunningProbe? running = null)
    {
        _http = http;
        _running = running ?? new LinuxGameRunningProbe();
    }

    /// <summary>The game's Launchpad tree (its Shaders and Textures folders sit under it).</summary>
    public static string TreeRoot(string binaryDirectory) => Path.Combine(binaryDirectory, "reshade-shaders", "RHI", TreeName);

    /// <summary>True for a file in a game's Launchpad tree.</summary>
    public static bool IsLaunchpadFile(string path)
        => path.Replace('\\', '/').Contains("/reshade-shaders/RHI/" + TreeName + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads the iMMERSE archive (branch archive: no published digest; HTTPS to GitHub only, its SHA-256 recorded) into
    /// <paramref name="stageRoot"/> and extracts Launchpad's files there. Throws when the archive does not hold them.
    /// </summary>
    public async Task<LaunchpadStage> StageAsync(string stageRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stageRoot);
        var zipPath = Path.Combine(stageRoot, "iMMERSE.zip");
        string sha256;
        using (var download = await TrustedDownload.DownloadToTempAsync(_http, ArchiveUrl, zipPath, DownloadSources.ImmerseLaunchpad,
                   cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            sha256 = download.Sha256;
            download.MoveTo(zipPath);
        }

        var files = new List<(string, string)>();
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (RelativeOf(entry.FullName) is not { } relative) continue;
                var target = Path.Combine(stageRoot, "files", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, target, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
                files.Add((relative, target));
            }
        }
        var effect = files.FirstOrDefault(file => file.Item1 == Path.Combine("Shaders", EffectFile));
        if (effect == default
            || !files.Any(file => file.Item1 == Path.Combine("Textures", NoiseTexture))
            || !files.Any(file => file.Item1.StartsWith(Path.Combine("Shaders", "MartysMods") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            || !File.ReadAllText(effect.Item2).Contains("technique MartysMods_Launchpad", StringComparison.Ordinal))
            throw new InvalidDataException(MissingMessage);
        return new LaunchpadStage(files, sha256);
    }

    /// <summary>
    /// The reshade-shaders relative path an archive entry goes to, or null when Launchpad does not need it: upstream's
    /// <c>LaunchpadFiles</c> (the effect, every file of <c>Shaders/MartysMods</c>, the blue-noise texture). Only those exact
    /// shapes are taken, so no archive path can leave the stage folder.
    /// </summary>
    internal static string? RelativeOf(string entryName)
    {
        var segments = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments.Any(s => s is "." or "..")) return null;
        var rest = segments[1..]; // the archive's top folder (iMMERSE-main)
        if (rest.Length == 2 && rest[0] == "Shaders" && rest[1] == EffectFile) return Path.Combine("Shaders", EffectFile);
        if (rest.Length == 3 && rest[0] == "Shaders" && rest[1] == "MartysMods" && rest[2].EndsWith(".fxh", StringComparison.OrdinalIgnoreCase))
            return Path.Combine("Shaders", "MartysMods", rest[2]);
        if (rest.Length == 2 && rest[0] == "Textures" && rest[1] == NoiseTexture) return Path.Combine("Textures", NoiseTexture);
        return null;
    }

    /// <summary>
    /// Puts Launchpad's files in the game's tree through <paramref name="scope"/>; returns the files for the calling
    /// component's record. A file another record of the game already lists is shared (listed, not placed again); a file there
    /// that no record lists belongs to the user and is left alone (not listed).
    /// </summary>
    public static async Task<IReadOnlyList<LinuxDeployedFile>> PlaceAsync(
        DeploymentScope scope, LaunchpadStage stage, string binaryDirectory, LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind owner)
    {
        var root = TreeRoot(binaryDirectory);
        var listed = new List<LinuxDeployedFile>();
        foreach (var (relative, source) in stage.Files)
        {
            var target = Path.Combine(root, relative);
            if (File.Exists(target))
            {
                if (ListedElsewhere(records, game, owner, target) is { } shared)
                    listed.Add(new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = shared.Sha256 });
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var sha = await scope.PlaceCopyAsync(source, target, CancellationToken.None, overwrite: false).ConfigureAwait(false);
            Dlss5FeederFullDeploymentService.NormalizeReadableFile(target);
            listed.Add(new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = sha });
        }
        return listed;
    }

    /// <summary>
    /// Deletes the Launchpad files of <paramref name="record"/> through <paramref name="scope"/>: only those no other record
    /// of the game still lists and that still hold the recorded bytes (a file changed since is kept and returned).
    /// </summary>
    public static async Task<IReadOnlyList<string>> DeleteOwnedAsync(
        DeploymentScope scope, LinuxDeploymentRecord record, LinuxDeploymentRecordStore records, DetectedGame game)
    {
        var kept = new List<string>();
        foreach (var file in record.Files.Where(f => IsLaunchpadFile(f.Path) && !f.ExistedBeforeInstall))
        {
            if (!File.Exists(file.Path) || ListedElsewhere(records, game, record.Component, file.Path) is not null) continue;
            var current = await FileHash.Sha256HexAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(file.Path);
                continue;
            }
            await scope.DeleteAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
        }
        return kept;
    }

    /// <summary>Another component's record of the game that lists <paramref name="path"/>, as its file entry; null when none.</summary>
    public static LinuxDeployedFile? ListedElsewhere(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind owner, string path)
        => records.FindAll(game)
            .Where(r => r.Component != owner)
            .SelectMany(r => r.Files)
            .FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

    /// <summary>Launchpad switched on in the preset, just before <paramref name="before"/> (first without it). Upstream <c>EnableTechnique</c>.</summary>
    public static (string Text, bool AlreadyOn, bool Changed) Enable(string text, string? before)
        => ReShadePresetText.AddTechnique(text, Token, before);

    /// <summary>Launchpad moved to the top of the lists that already have it (nothing else changes). Upstream <c>KeepLaunchpadFirst</c>.</summary>
    public static (string Text, bool Changed) KeepFirst(string text) => ReShadePresetText.MoveTechniqueFirst(text, Token);

    /// <summary>Launchpad taken out of the preset's lists.</summary>
    public static (string Text, bool Changed) Disable(string text) => ReShadePresetText.RemoveTechnique(text, Token);

    /// <summary>
    /// Moves Launchpad to the top of the game's preset when it is there but lower down (ReShade appends an effect switched
    /// on in the overlay; HSF turns it on at the end), only while the game is not running (ReShade writes the preset back
    /// when the game closes). Journaled; the commit point re-saves <paramref name="owner"/>'s record. True when moved.
    /// </summary>
    public async Task<bool> KeepFirstAsync(
        DetectedGame game, LinuxDeploymentRecordStore records, ManagedComponentKind owner, DeploymentJournal? journal, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || records.Find(game, owner) is not { } record) return false;
        if (ReShadePresetFile.Resolve(game.BinaryDirectory) is not { } preset || ReShadePresetFile.Read(preset) is not { } text) return false;
        var (moved, changed) = KeepFirst(text);
        if (!changed) return false;
        if (_running.IsRunning(game))
        {
            AppLog.Warn("immerse-launchpad", $"'{game.Name}' is running; iMMERSE Launchpad stays where it is in {preset} until it closes.");
            return false;
        }

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "iMMERSE Launchpad order: " + game.Name, game, owner, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            AppLog.Warn("immerse-launchpad", $"iMMERSE Launchpad order not changed in {preset}: {refusal}");
            return false;
        }
        await using var scope = opened!;
        try
        {
            var target = ReShadePresetFile.WriteTarget(preset, game.BinaryDirectory, game.InstallPath);
            await scope.TrackAsync(target, CancellationToken.None).ConfigureAwait(false);
            ReShadePresetFile.Write(preset, moved, game.BinaryDirectory, game.InstallPath);
            await scope.DeclareWrittenContentAsync(target, CancellationToken.None).ConfigureAwait(false);
            records.Upsert(scope.Stamp(LinuxDeploymentRecordStore.Clone(record)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return true;
            AppLog.Warn("immerse-launchpad", $"iMMERSE Launchpad order not changed in {preset}", ex);
            return false;
        }
        await scope.CommitAsync().ConfigureAwait(false);
        AppLog.Warn("immerse-launchpad", $"iMMERSE Launchpad moved to the top of {preset}.");
        return true;
    }
}
