using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed record ReShadeDx9MigrationState(
    string OriginalPath,
    string TranslatedPath,
    string OriginalHash,
    WineOverrideRecord D3D9Override);

/// <summary>What <see cref="Dx9TranslationReShadeCoordinator.MigrateInsideAsync"/> did: the state, the ReShade record to
/// save in the caller's commit write, and the overrides it set (for <see cref="WineDllOverrideService.TryRestore"/>).</summary>
internal sealed record Dx9MigrationInside(ReShadeDx9MigrationState State, LinuxDeploymentRecord ReShadeRecord, IReadOnlyList<WineOverrideRecord> CreatedOverrides);

/// <summary>
/// Temporarily moves an RHI-managed DX9 ReShade proxy from d3d9.dll to dxgi.dll while
/// dgVoodoo2 owns d3d9.dll. Registry ownership is transferred with the file and can be
/// restored exactly when the last DX9 translation consumer is removed.
/// </summary>
public sealed class Dx9TranslationReShadeCoordinator
{
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly DeploymentJournal? _journal;

    /// <summary>
    /// Fixed text before the installed name when RHI's ReShade sits under another name than d3d9.dll (a DLL name the user
    /// chose, e.g. dxgi.dll): what to do, in plain words (PT-BR presentation pair).
    /// </summary>
    public const string NeedsReShadeAsD3d9Prefix =
        "The DirectX 9 translation (dgVoodoo2) needs RHI's ReShade installed as d3d9.dll; nothing was changed. Remove ReShade, set its DLL name to d3d9.dll, install ReShade again, then try again. ReShade is installed now as: ";

    public Dx9TranslationReShadeCoordinator(LinuxDeploymentRecordStore records, WineDllOverrideService overrides, DeploymentJournal? journal = null)
    {
        _records = records;
        _overrides = overrides;
        _journal = journal;
    }

    public async Task<ReShadeDx9MigrationState> MigrateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var (record, file, d3d9, dxgi, hash) = await PrepareMigrationAsync(game, cancellationToken).ConfigureAwait(false);

        // The overrides are journaled before they are written (#23); the ReShade record carrying this
        // operation's id is the commit point.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "ReShade DX9 translation: " + game.Name, game, ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) throw new IOException(journalRefusal);
        await using var scope = opened!;
        var operation = scope.Operation;
        // From the first override on, the migration (or its undo) runs to the end whatever Cancel says.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var exeName = Path.GetFileName(game.ExecutablePath);
        var originalRecordOverride = record.WineOverride;
        var previousOperationId = record.Metadata.GetValueOrDefault(LinuxDeploymentRecovery.OperationIdMetadataKey);
        WineOverrideRecord? d3d9Override = null;
        WineOverrideRecord? dxgiOverride = null;
        try
        {
            d3d9Override = originalRecordOverride is not null &&
                           string.Equals(Path.GetFileNameWithoutExtension(originalRecordOverride.DllName), "d3d9", StringComparison.OrdinalIgnoreCase)
                ? originalRecordOverride
                : await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath!, "d3d9", exeName, cancellationToken).ConfigureAwait(false);
            dxgiOverride = await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath!, "dxgi", exeName, cancellationToken).ConfigureAwait(false);
            // Both names tracked and the move declared (#23): a crash before the record is saved puts the proxy back at
            // d3d9.dll at the next start.
            await scope.TrackAsync(d3d9, cancellationToken).ConfigureAwait(false);
            await scope.TrackAsync(dxgi, cancellationToken).ConfigureAwait(false);
            await scope.DeclareWriteAsync(dxgi, hash, cancellationToken).ConfigureAwait(false);
            AtomicFile.Rename(d3d9, dxgi);
            file.Path = dxgi;
            file.Sha256 = hash; // a rename cannot change content
            record.WineOverride = dxgiOverride;
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            // The proxy goes back to d3d9.dll from the scope's copies; one that cannot be put back is kept and logged.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            // #204: the record save landed and then threw: the migration is done (the record names dxgi.dll, as on disk).
            if (rollback.Committed) return new ReShadeDx9MigrationState(d3d9, dxgi, hash, d3d9Override!);
            if (!rollback.Success)
                AppLog.Warn("dx9-translation", $"Migration rollback could not put d3d9.dll back: {string.Join("; ", rollback.Failures)}", ex);
            if (!rollback.RolledBack) throw; // the ledger cannot tell: the next start decides, nothing more is undone here
            _overrides.TryRestore(game.PrefixPath!, dxgiOverride);
            if (originalRecordOverride is null) _overrides.TryRestore(game.PrefixPath!, d3d9Override);
            record.WineOverride = originalRecordOverride;
            // The rolled-back record must not claim this operation as its commit.
            if (previousOperationId is null) record.Metadata.Remove(LinuxDeploymentRecovery.OperationIdMetadataKey);
            else record.Metadata[LinuxDeploymentRecovery.OperationIdMetadataKey] = previousOperationId;
            file.Path = d3d9;
            file.Sha256 = File.Exists(d3d9) ? await FileHash.Sha256HexAsync(d3d9, cancellationToken).ConfigureAwait(false) : hash;
            _records.Upsert(record);
            throw;
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return new ReShadeDx9MigrationState(d3d9, dxgi, hash, d3d9Override);
    }

    /// <summary>
    /// The migration inside another component's journaled operation (#169: DLSS5 Feeder, Luma): the overrides are set
    /// through <paramref name="scope"/>'s operation and the proxy rename is tracked and declared in it, but no record is
    /// saved — the caller saves the returned ReShade record in the same ledger write as its own, so a crash leaves both or
    /// neither. Undo is the caller's: the scope's rollback (the rename and, journaled, the overrides) plus
    /// <see cref="WineDllOverrideService.TryRestore"/> of <see cref="Dx9MigrationInside.CreatedOverrides"/> (a no-op where
    /// the rollback already put the value back).
    /// </summary>
    internal async Task<Dx9MigrationInside> MigrateInsideAsync(DetectedGame game, DeploymentScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (record, file, d3d9, dxgi, hash) = await PrepareMigrationAsync(game, cancellationToken).ConfigureAwait(false);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var exeName = Path.GetFileName(game.ExecutablePath);
        var created = new List<WineOverrideRecord>();
        var d3d9Override = record.WineOverride is not null &&
                           string.Equals(Path.GetFileNameWithoutExtension(record.WineOverride.DllName), "d3d9", StringComparison.OrdinalIgnoreCase)
            ? record.WineOverride
            : null;
        if (d3d9Override is null)
        {
            d3d9Override = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, game.PrefixPath!, "d3d9", exeName, cancellationToken).ConfigureAwait(false);
            created.Add(d3d9Override);
        }
        var dxgiOverride = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, game.PrefixPath!, "dxgi", exeName, cancellationToken).ConfigureAwait(false);
        created.Add(dxgiOverride);
        await scope.TrackAsync(d3d9, cancellationToken).ConfigureAwait(false);
        await scope.TrackAsync(dxgi, cancellationToken).ConfigureAwait(false);
        await scope.DeclareWriteAsync(dxgi, hash, cancellationToken).ConfigureAwait(false);
        AtomicFile.Rename(d3d9, dxgi);
        file.Path = dxgi;
        file.Sha256 = hash; // a rename cannot change content
        record.WineOverride = dxgiOverride;
        return new Dx9MigrationInside(new ReShadeDx9MigrationState(d3d9, dxgi, hash, d3d9Override), record, created);
    }

    /// <summary>After a failed operation that ran <see cref="MigrateInsideAsync"/>: the overrides it set go (no-op where the
    /// scope's journaled rollback already put them back). Never throws.</summary>
    internal void UndoInside(DetectedGame game, Dx9MigrationInside migration)
    {
        if (string.IsNullOrWhiteSpace(game.PrefixPath)) return;
        foreach (var wineOverride in migration.CreatedOverrides.Reverse())
            _overrides.TryRestore(game.PrefixPath, wineOverride);
    }

    /// <summary>After a failed removal that ran <see cref="RestoreInsideAsync"/> and took RHI's translated override away: puts
    /// it back (the ReShade record keeps it). Never throws.</summary>
    internal void Reapply(string prefixPath, WineOverrideRecord translatedOverride) => _overrides.TryReapply(prefixPath, translatedOverride);

    /// <summary>
    /// The restore inside another component's journaled removal (#169): the rename is tracked and declared in
    /// <paramref name="scope"/>, the override put back through its operation; returns the ReShade record to save in the
    /// caller's commit write and the override outcome (for <see cref="WineDllOverrideService.TryReapply"/> after a failure).
    /// </summary>
    internal async Task<(LinuxDeploymentRecord ReShadeRecord, WineOverrideRestoreResult? OverrideRestore)> RestoreInsideAsync(
        DetectedGame game, ReShadeDx9MigrationState state, DeploymentScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (record, file, currentHash) = await PrepareRestoreAsync(game, state, cancellationToken).ConfigureAwait(false);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        await scope.TrackAsync(state.TranslatedPath, cancellationToken).ConfigureAwait(false);
        await scope.TrackAsync(state.OriginalPath, cancellationToken).ConfigureAwait(false);
        await scope.DeclareWriteAsync(state.OriginalPath, currentHash, cancellationToken).ConfigureAwait(false);
        AtomicFile.Rename(state.TranslatedPath, state.OriginalPath);
        WineOverrideRestoreResult? overrideRestore = null;
        if (record.WineOverride is { } translatedOverride)
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, translatedOverride, cancellationToken).ConfigureAwait(false);
        file.Path = state.OriginalPath;
        file.Sha256 = currentHash; // a rename cannot change content
        record.WineOverride = state.D3D9Override;
        return (record, overrideRestore);
    }

    /// <summary>
    /// DX9 HDR is on the game: the DirectX 9 translation (dgVoodoo2 as d3d9.dll) cannot be added. Luma Framework and the
    /// DLSS5 Feeder ask this in their own pre-checks, before any download or write (w7 follow-up B29); the migration asks it
    /// again. Null when DX9 HDR is not installed.
    /// </summary>
    internal static string? DxvkHdrRefusal(LinuxDeploymentRecordStore records, DetectedGame game)
        => records.Find(game, ManagedComponentKind.DxvkHdr) is not null ? DxvkHdrDeploymentService.BlocksDx9TranslationMessage : null;

    private async Task<(LinuxDeploymentRecord Record, LinuxDeployedFile File, string D3d9, string Dxgi, string Hash)> PrepareMigrationAsync(
        DetectedGame game, CancellationToken cancellationToken)
    {
        if (game.EffectiveGraphicsApi != GraphicsApi.DirectX9)
            throw new InvalidOperationException("DX9 ReShade migration is only valid for DirectX 9 games.");
        if (DxvkHdrRefusal(_records, game) is { } dxvkHdr) // w7/dxvk-hdr hook (#200, coordinator decision 8): say so plainly
            throw new InvalidOperationException(dxvkHdr);
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || string.IsNullOrWhiteSpace(game.PrefixPath) ||
            !Directory.Exists(game.BinaryDirectory) || !Directory.Exists(game.PrefixPath))
            throw new InvalidOperationException("DX9 ReShade migration requires a game binary directory and initialized Wine/Proton prefix.");
        var record = _records.Find(game, ManagedComponentKind.ReShade)
            ?? throw new InvalidOperationException("DX9 translation requires RHI-managed ReShade.");
        var d3d9 = Path.Combine(game.BinaryDirectory, "d3d9.dll");
        var dxgi = Path.Combine(game.BinaryDirectory, "dxgi.dll");
        var file = record.Files.FirstOrDefault(item =>
            Path.GetFullPath(item.Path).Equals(Path.GetFullPath(d3d9), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(NeedsReShadeAsD3d9Prefix + (ProxyPayloadLocator.ReShadePayload(record) is { } payload ? Path.GetFileName(payload.Path) : "?"));
        if (!File.Exists(d3d9))
            throw new FileNotFoundException("Managed ReShade d3d9.dll is missing.", d3d9);
        if (File.Exists(dxgi))
            throw new InvalidOperationException("dxgi.dll is already occupied; RHI will not overwrite it for DX9 translation.");
        var hash = await FileHash.Sha256HexAsync(d3d9, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(file.Sha256) && !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed ReShade d3d9.dll was modified outside RHI; DX9 migration is blocked.");
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            throw new WineRegistryEditRefusedException(wineRunning, inUse: true);
        return (record, file, d3d9, dxgi, hash);
    }

    private async Task<(LinuxDeploymentRecord Record, LinuxDeployedFile File, string CurrentHash)> PrepareRestoreAsync(
        DetectedGame game, ReShadeDx9MigrationState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            throw new InvalidOperationException("DX9 ReShade restoration requires the original Wine/Proton prefix.");
        var record = _records.Find(game, ManagedComponentKind.ReShade)
            ?? throw new InvalidOperationException("RHI-managed ReShade record is missing; DX9 migration cannot be restored safely.");
        var file = record.Files.FirstOrDefault(item =>
            Path.GetFullPath(item.Path).Equals(Path.GetFullPath(state.TranslatedPath), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("RHI-managed ReShade no longer owns the translated dxgi.dll path.");
        if (!File.Exists(state.TranslatedPath))
            throw new FileNotFoundException("Translated ReShade dxgi.dll is missing.", state.TranslatedPath);
        if (File.Exists(state.OriginalPath))
            throw new InvalidOperationException("d3d9.dll is still occupied. Remove the dgVoodoo2 wrapper before restoring ReShade.");
        var currentHash = await FileHash.Sha256HexAsync(state.TranslatedPath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(file.Sha256) && !currentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Translated ReShade dxgi.dll was modified outside RHI; restoration is blocked.");
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            throw new WineRegistryEditRefusedException(wineRunning, inUse: true);
        return (record, file, currentHash);
    }

    public async Task RestoreAsync(DetectedGame game, ReShadeDx9MigrationState state, CancellationToken cancellationToken = default)
    {
        var (record, file, currentHash) = await PrepareRestoreAsync(game, state, cancellationToken).ConfigureAwait(false);

        // Journaled like the migration (#23): the ReShade record saved with this operation's id is the commit point.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "ReShade DX9 translation restore: " + game.Name, game, ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) throw new IOException(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var translatedOverride = record.WineOverride;
        WineOverrideRestoreResult? overrideRestore = null;
        try
        {
            await scope.TrackAsync(state.TranslatedPath, cancellationToken).ConfigureAwait(false);
            await scope.TrackAsync(state.OriginalPath, cancellationToken).ConfigureAwait(false);
            await scope.DeclareWriteAsync(state.OriginalPath, currentHash, cancellationToken).ConfigureAwait(false);
            AtomicFile.Rename(state.TranslatedPath, state.OriginalPath);
            if (translatedOverride is not null)
                overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, translatedOverride, cancellationToken).ConfigureAwait(false);
            file.Path = state.OriginalPath;
            file.Sha256 = currentHash; // a rename cannot change content
            record.WineOverride = state.D3D9Override;
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            // The proxy goes back to dxgi.dll from the scope's copies; one that cannot be put back is kept and logged.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            // #204: the record save landed and then threw: the restore is done (the record names d3d9.dll, as on disk).
            if (rollback.Committed) return;
            if (!rollback.Success)
                AppLog.Warn("dx9-translation", $"Restore rollback could not put dxgi.dll back: {string.Join("; ", rollback.Failures)}", ex);
            if (!rollback.RolledBack) throw; // the ledger cannot tell: the next start decides, nothing more is undone here
            // The record keeps the translated override, so put RHI's value back where this restore removed it (#30).
            if (overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath!, translatedOverride);
            record.WineOverride = translatedOverride;
            file.Path = state.TranslatedPath;
            if (File.Exists(state.TranslatedPath)) file.Sha256 = await FileHash.Sha256HexAsync(state.TranslatedPath, cancellationToken).ConfigureAwait(false);
            _records.Upsert(record);
            throw;
        }
        await scope.CommitAsync().ConfigureAwait(false);
    }

}
