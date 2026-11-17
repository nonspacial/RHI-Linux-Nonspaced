using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w14 final review M5: a ⚙ setting written into a file in the game folder, as one journaled operation whose commit point
/// is the component's record (Display Commander's ⚙ pattern). A file the write creates is listed on the record with
/// <see cref="LinuxDeployedFile.AllowUserChanges"/>, so removal takes it while it still holds what RHI wrote and leaves it
/// once the user changed it; a file the record already lists while it holds RHI's bytes takes the new hash in the same
/// commit. A file that was there before and is not RHI's is written but never listed. Refused when the component is not
/// installed.
/// </summary>
public static class ComponentSettingsFile
{
    /// <summary>The component's name follows (it is the same in every language).</summary>
    public const string NotInstalledPrefix = "This setting is saved in a file RHI's install owns, so it can only be changed while RHI manages the component: ";

    public static async Task<ComponentOperationResult> WriteAsync(
        DeploymentJournal? journal,
        LinuxDeploymentRecordStore records,
        DetectedGame game,
        ManagedComponentKind component,
        string displayName,
        string path,
        Action write,
        CancellationToken cancellationToken = default)
    {
        // Find hands out a copy; the ledger changes only through the stamped Upsert below.
        if (records.Find(game, component) is not { } record) return ComponentOperationResult.Fail(NotInstalledPrefix + displayName);
        var target = Path.GetFullPath(path);
        var existed = File.Exists(target);
        var listed = record.Files.FirstOrDefault(file => PathIdentity.AreSame(file.Path, target));
        var owned = listed is not null && existed && listed.Sha256 is { Length: > 0 } recorded
                    && FileHash.HexEquals(await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false), recorded);

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, $"{displayName} setting: {game.Name}", game, component, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
            write();
            await scope.DeclareWrittenContentAsync(target, cancellationToken).ConfigureAwait(false);
            var hash = await FileHash.Sha256HexAsync(target, CancellationToken.None).ConfigureAwait(false);
            if (listed is null && !existed)
                record.Files.Add(new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = hash, AllowUserChanges = true });
            else if (owned)
                listed!.Sha256 = hash;
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(displayName, false, target); // #204: the record landed
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(target, target);
    }

    /// <summary>A settings file a ⚙ write created (listed with <see cref="LinuxDeployedFile.AllowUserChanges"/>).</summary>
    public static bool IsSettingsFile(LinuxDeployedFile file) => file.AllowUserChanges;

    /// <summary>Removal: deletes a settings file RHI created while it still holds what RHI wrote; a changed one stays.</summary>
    public static async Task DeleteIfUnchangedAsync(DeploymentScope scope, LinuxDeployedFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file.Path) || file.ExistedBeforeInstall || file.Sha256 is not { Length: > 0 } recorded) return;
        if (FileHash.HexEquals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), recorded))
            await scope.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An update that rebuilds the record keeps the settings files the previous one listed.</summary>
    public static void Carry(LinuxDeploymentRecord? previous, LinuxDeploymentRecord rebuilt)
    {
        if (previous is null) return;
        foreach (var file in previous.Files.Where(IsSettingsFile))
            if (!rebuilt.Files.Any(other => PathIdentity.AreSame(other.Path, file.Path))) rebuilt.Files.Add(file);
    }
}
