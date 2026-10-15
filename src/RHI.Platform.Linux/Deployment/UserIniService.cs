using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>How a batch over the library ended: counts only (the UI words the summary), problems go to rhi.log.</summary>
public sealed record IniBatchResult(int Updated, int Refused, int Failed)
{
    public int Total => Updated + Refused + Failed;
}

/// <summary>
/// INI deployment from the user's templates and Settings into games (#181; upstream 2.8.7 <c>AuxInstallService.Ini.cs</c>,
/// <c>MassDeployHandler</c>, <c>ApplyScreenshotPath_Click</c>): "Deploy ReShade.ini / relimiter.ini / DisplayCommander.ini"
/// for one game, the same for every game that has the component, and "Apply to all games" for the ReShade screenshot
/// folder, hotkeys and effect list style. Every write is one atomic user-file replace (a user's read-only lock is kept,
/// never a blocker); each game is refused, without a write, where an install would be (no confirmed patch target,
/// records left at a moved folder, an Xbox title its installer app is working on, a native Linux game).
/// </summary>
public sealed class UserIniService(UserIniTemplates templates, LinuxDeploymentRecordStore records, Func<DetectedGame, string?> writeRefusal)
{
    /// <summary>Fixed text before the game name when a game is not a Windows game (localisation key).</summary>
    public const string NativeGamePrefix = "INI files were not written: this is a native Linux game: ";

    /// <summary>Fixed text before the folder when the game's binary folder is gone (localisation key).</summary>
    public const string NoFolderPrefix = "INI files were not written: the game's folder is not available: ";

    public UserIniTemplates Templates => templates;

    /// <summary>The file name a component's INI is deployed under (beside its add-on).</summary>
    public static string? AddonIniName(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ReLimiter => UserIniTemplates.FileName(UserIniTemplate.ReLimiter),
        ManagedComponentKind.DisplayCommander => UserIniTemplates.FileName(UserIniTemplate.DisplayCommander),
        _ => null
    };

    /// <summary>
    /// "Deploy ReShade.ini" (upstream <c>MergeRsIni</c>): the template's keys and the Settings values go into the game's
    /// ReShade.ini (created from the template when there is none); every other line of the file stays.
    /// </summary>
    public ComponentOperationResult DeployReShadeIni(DetectedGame game, LinuxAppSettings settings, CancellationToken cancellationToken = default)
    {
        if (Refusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var result = ReShadeIniManager.Apply(game.BinaryDirectory!, [], [], ReShadeGlobalSettingsApplier.InstallDefaults(settings, game, templates));
        return ComponentOperationResult.Ok(DeployedPrefix + result.Path, result.Path);
    }

    /// <summary>Fixed text before the path after a deployed INI (localisation key).</summary>
    public const string DeployedPrefix = "INI deployed from your template: ";

    /// <summary>
    /// "Deploy relimiter.ini / DisplayCommander.ini" (upstream <c>CopyUlIni</c>/<c>CopyDcIni</c>): the game's file becomes a
    /// copy of the template, beside the add-on.
    /// </summary>
    public ComponentOperationResult DeployAddonIni(DetectedGame game, ManagedComponentKind kind, CancellationToken cancellationToken = default)
    {
        var name = AddonIniName(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No INI template for this component.");
        if (Refusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        var template = templates.Read(kind == ManagedComponentKind.ReLimiter ? UserIniTemplate.ReLimiter : UserIniTemplate.DisplayCommander, out _);
        var directory = RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory!);
        var target = ExistingCaseInsensitive(directory, name) ?? Path.Combine(directory, name);
        XboxWriteGate.ThrowIfRefused(target);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        IniLines.Write(target, IniLines.FromBytes(template), ReShadeGlobalSettingsApplier.GameRoots(game, game.BinaryDirectory!));
        return ComponentOperationResult.Ok(DeployedPrefix + target, target);
    }

    /// <summary>
    /// Mass INI deploy (upstream <c>MassDeployRsIni/UlIni/DcIni_Click</c>): every game where RHI manages the component.
    /// One game per unit: Cancel stops between games, a game whose write failed keeps its file.
    /// </summary>
    public Task<IniBatchResult> DeployToAllAsync(IReadOnlyList<DetectedGame> games, ManagedComponentKind kind, LinuxAppSettings settings, CancellationToken cancellationToken = default)
        => RunBatchAsync(
            games.Where(game => records.Find(game, kind) is not null).ToList(),
            (game, token) => kind == ManagedComponentKind.ReShade ? DeployReShadeIni(game, settings, token) : DeployAddonIni(game, kind, token),
            "ini-deploy", cancellationToken);

    /// <summary>
    /// "Apply to all games" (upstream <c>ApplyScreenshotPath_Click</c>): screenshot folder, hotkeys and effect list style
    /// into every game that has a ReShade.ini (main and numbered files).
    /// </summary>
    public Task<IniBatchResult> ApplyReShadeSettingsToAllAsync(IReadOnlyList<DetectedGame> games, LinuxAppSettings settings, CancellationToken cancellationToken = default)
        => RunBatchAsync(
            games.Where(HasReShadeIni).ToList(),
            (game, token) =>
            {
                if (Refusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
                _ = OperationCancellation.BeginWrites(token);
                return ComponentOperationResult.Ok(ReShadeGlobalSettingsApplier.ApplyToGame(settings, game));
            },
            "reshade-settings", cancellationToken);

    /// <summary>Whether the game's binary folder holds a ReShade.ini (any case).</summary>
    public static bool HasReShadeIni(DetectedGame game)
        => game.BinaryDirectory is { } dir && Directory.Exists(dir) && ExistingCaseInsensitive(dir, "ReShade.ini") is not null;

    /// <summary>Why nothing may be written into this game now, or null.</summary>
    public string? Refusal(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return NativeGamePrefix + game.Name;
        if (writeRefusal(game) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return NoFolderPrefix + (game.BinaryDirectory ?? game.InstallPath);
        return null;
    }

    /// <summary>
    /// One batch over games: one <see cref="OperationCancellation"/> per game linked to the batch token (Cancel is
    /// honoured between games only), a refused game counted apart from a failed one, nothing aborted by one game's error.
    /// </summary>
    internal static async Task<IniBatchResult> RunBatchAsync(
        IReadOnlyList<DetectedGame> games, Func<DetectedGame, CancellationToken, ComponentOperationResult> perGame, string area,
        CancellationToken cancellationToken)
    {
        int updated = 0, refused = 0, failed = 0;
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var unit = new OperationCancellation(cancellationToken);
            try
            {
                var result = await Task.Run(() => perGame(game, unit.Token), CancellationToken.None).ConfigureAwait(false);
                if (result.Success) updated++;
                else
                {
                    refused++;
                    AppLog.Warn(area, $"{game.Name}: {result.Message}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                failed++;
                AppLog.Warn(area, $"{game.Name}: {ex.Message}", ex);
            }
        }
        return new IniBatchResult(updated, refused, failed);
    }

    private static string? ExistingCaseInsensitive(string directory, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
