using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class OptiScalerDeploymentService
{
    public const string FreshIniNotManagedMessage = "OptiScaler is not managed by RHI Linux for this game, so its OptiScaler.ini was not replaced.";
    public const string FreshIniNoTemplateMessage = "The OptiScaler INI template could not be prepared, so OptiScaler.ini was not replaced.";
    public const string FreshIniReplacedPrefix = "OptiScaler.ini was replaced with a fresh copy of your template: ";
    public const string FreshIniBackupPrefix = " The previous file was kept at: ";
    public const string FreshIniFailedPrefix = "OptiScaler.ini was not replaced: ";

    /// <summary>Where the previous OptiScaler.ini goes before a fresh copy replaces it (RHI's data folder, never the game's).</summary>
    public string FreshIniBackupDirectory { get; set; } = Path.Combine(LinuxPaths.DataHome, "rhi-linux", "optiscaler-ini-backups");

    /// <summary>
    /// #269 (upstream 2.8.9 "Replace with a fresh copy…", fixed there to use the fresh-install template): replaces the game's
    /// OptiScaler.ini with the template a fresh install of its version would take — the user's template for the channel,
    /// GPU type and DLSS Inputs (#97) — then sets what RHI's install sets (LoadAsiPlugins on; LoadReshade on while RHI's
    /// ReShade is chained as ReShade64.dll). Journaled; the record keeps the new file's hash. Linux addition: the previous
    /// file is copied to RHI's data folder first, so nothing the user wrote is lost.
    /// </summary>
    public async Task<ComponentOperationResult> ReplaceIniWithFreshCopyAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(FreshIniNotManagedMessage);
        if (await DeploymentScope.HoldRefusalAsync(_journal, game, ManagedComponentKind.OptiScaler).ConfigureAwait(false) is { } held)
            return ComponentOperationResult.Fail(held);
        var binaryDirectory = Path.GetFullPath(record.BinaryDirectory);
        var ini = Path.Combine(binaryDirectory, "OptiScaler.ini");
        string template;
        try
        {
            template = IniTemplates?.EnsureSeeded(GetInstalledVariant(record)) ?? throw new InvalidOperationException(FreshIniNoTemplateMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ComponentOperationResult.Fail(ex.Message == FreshIniNoTemplateMessage ? FreshIniNoTemplateMessage : FreshIniNoTemplateMessage + " " + ex.Message);
        }

        string? backup = null;
        if (File.Exists(ini))
        {
            try
            {
                var folder = Path.Combine(FreshIniBackupDirectory, string.Concat(LinuxDeploymentRecordStore.GameKey(game).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')));
                Directory.CreateDirectory(folder);
                backup = Path.Combine(folder, $"OptiScaler-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.ini");
                await AtomicFile.CopyAsync(ini, backup, await FileHash.Sha256HexAsync(ini, cancellationToken).ConfigureAwait(false), cancellationToken, overwrite: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ComponentOperationResult.Fail(FreshIniFailedPrefix + ex.Message);
            }
        }

        var (opened, refusal) = await DeploymentScope.BeginAsync(_journal, $"OptiScaler.ini fresh copy: {game.Name}", game, ManagedComponentKind.OptiScaler, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            var hash = await scope.PlaceCopyAsync(template, ini, cancellationToken).ConfigureAwait(false);
            OptiScalerIniEditor.SetValue(binaryDirectory, "Plugins", "LoadAsiPlugins", "true");
            if (_records.Find(game, ManagedComponentKind.ReShade)?.Files.Any(file => Path.GetFileName(file.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase)) == true)
                OptiScalerIniEditor.SetLoadReShade(binaryDirectory, true);
            await scope.DeclareWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);
            hash = await FileHash.Sha256HexAsync(ini, CancellationToken.None).ConfigureAwait(false);
            var owned = record.Files.FirstOrDefault(file => Path.GetFullPath(file.Path).Equals(ini, StringComparison.Ordinal));
            if (owned is null)
                record.Files.Add(new LinuxDeployedFile { Path = ini, ExistedBeforeInstall = false, Sha256 = hash, AllowUserChanges = true });
            else owned.Sha256 = hash;
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("OptiScaler");
            return ComponentOperationResult.Fail(FreshIniFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(FreshIniReplacedPrefix + template + (backup is null ? "" : FreshIniBackupPrefix + backup), ini);
    }
}
