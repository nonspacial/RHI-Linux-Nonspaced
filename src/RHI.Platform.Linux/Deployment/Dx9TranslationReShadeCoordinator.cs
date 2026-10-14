using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed record ReShadeDx9MigrationState(
    string OriginalPath,
    string TranslatedPath,
    string OriginalHash,
    WineOverrideRecord D3D9Override);

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
        "The DirectX 9 translation (dgVoodoo2) needs RHI's ReShade installed as d3d9.dll; nothing was changed. Remove ReShade, set its DLL name to Automatic, install ReShade again, then try again. ReShade is installed now as: ";

    public Dx9TranslationReShadeCoordinator(LinuxDeploymentRecordStore records, WineDllOverrideService overrides, DeploymentJournal? journal = null)
    {
        _records = records;
        _overrides = overrides;
        _journal = journal;
    }

    public async Task<ReShadeDx9MigrationState> MigrateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (game.EffectiveGraphicsApi != GraphicsApi.DirectX9)
            throw new InvalidOperationException("DX9 ReShade migration is only valid for DirectX 9 games.");
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
                : await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath, "d3d9", exeName, cancellationToken).ConfigureAwait(false);
            dxgiOverride = await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath, "dxgi", exeName, cancellationToken).ConfigureAwait(false);
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
            if (!rollback.Success)
                AppLog.Warn("dx9-translation", $"Migration rollback could not put d3d9.dll back: {string.Join("; ", rollback.Failures)}", ex);
            _overrides.TryRestore(game.PrefixPath, dxgiOverride);
            if (originalRecordOverride is null) _overrides.TryRestore(game.PrefixPath, d3d9Override);
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

    public async Task RestoreAsync(DetectedGame game, ReShadeDx9MigrationState state, CancellationToken cancellationToken = default)
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
                overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath, translatedOverride, cancellationToken).ConfigureAwait(false);
            file.Path = state.OriginalPath;
            file.Sha256 = currentHash; // a rename cannot change content
            record.WineOverride = state.D3D9Override;
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            // The proxy goes back to dxgi.dll from the scope's copies; one that cannot be put back is kept and logged.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (!rollback.Success)
                AppLog.Warn("dx9-translation", $"Restore rollback could not put dxgi.dll back: {string.Join("; ", rollback.Failures)}", ex);
            // The record keeps the translated override, so put RHI's value back where this restore removed it (#30).
            if (overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, translatedOverride);
            record.WineOverride = translatedOverride;
            file.Path = state.TranslatedPath;
            if (File.Exists(state.TranslatedPath)) file.Sha256 = await FileHash.Sha256HexAsync(state.TranslatedPath, cancellationToken).ConfigureAwait(false);
            _records.Upsert(record);
            throw;
        }
        await scope.CommitAsync().ConfigureAwait(false);
    }

}
