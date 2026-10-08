using System.Security.Cryptography;
using RHI.Core.Deployment;
using RHI.Core.Models;

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

    public Dx9TranslationReShadeCoordinator(LinuxDeploymentRecordStore records, WineDllOverrideService overrides)
    {
        _records = records;
        _overrides = overrides;
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
            ?? throw new InvalidOperationException("RHI-managed ReShade is not currently deployed as d3d9.dll, so automatic DX9 translation migration is blocked.");
        if (!File.Exists(d3d9))
            throw new FileNotFoundException("Managed ReShade d3d9.dll is missing.", d3d9);
        if (File.Exists(dxgi))
            throw new InvalidOperationException("dxgi.dll is already occupied; RHI will not overwrite it for DX9 translation.");
        var hash = await ComputeSha256Async(d3d9, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(file.Sha256) && !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed ReShade d3d9.dll was modified outside RHI; DX9 migration is blocked.");

        // From the first override on, the migration (or its undo) runs to the end whatever Cancel says.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var exeName = Path.GetFileName(game.ExecutablePath);
        var originalRecordOverride = record.WineOverride;
        var d3d9Override = originalRecordOverride is not null &&
                           string.Equals(Path.GetFileNameWithoutExtension(originalRecordOverride.DllName), "d3d9", StringComparison.OrdinalIgnoreCase)
            ? originalRecordOverride
            : _overrides.EnsureNativeBuiltin(game.PrefixPath, "d3d9", exeName);
        WineOverrideRecord? dxgiOverride = null;
        try
        {
            dxgiOverride = _overrides.EnsureNativeBuiltin(game.PrefixPath, "dxgi", exeName);
            File.Move(d3d9, dxgi);
            file.Path = dxgi;
            file.Sha256 = await ComputeSha256Async(dxgi, cancellationToken).ConfigureAwait(false);
            record.WineOverride = dxgiOverride;
            _records.Upsert(record);
            return new ReShadeDx9MigrationState(d3d9, dxgi, hash, d3d9Override);
        }
        catch
        {
            try { if (File.Exists(dxgi) && !File.Exists(d3d9)) File.Move(dxgi, d3d9); } catch { }
            if (dxgiOverride is not null) try { _overrides.Restore(game.PrefixPath, dxgiOverride); } catch { }
            if (originalRecordOverride is null) try { _overrides.Restore(game.PrefixPath, d3d9Override); } catch { }
            record.WineOverride = originalRecordOverride;
            file.Path = d3d9;
            file.Sha256 = File.Exists(d3d9) ? await ComputeSha256Async(d3d9, cancellationToken).ConfigureAwait(false) : hash;
            _records.Upsert(record);
            throw;
        }
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
        var currentHash = await ComputeSha256Async(state.TranslatedPath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(file.Sha256) && !currentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Translated ReShade dxgi.dll was modified outside RHI; restoration is blocked.");

        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var translatedOverride = record.WineOverride;
        File.Move(state.TranslatedPath, state.OriginalPath);
        try
        {
            if (translatedOverride is not null)
                _overrides.Restore(game.PrefixPath, translatedOverride);
            file.Path = state.OriginalPath;
            file.Sha256 = await ComputeSha256Async(state.OriginalPath, cancellationToken).ConfigureAwait(false);
            record.WineOverride = state.D3D9Override;
            _records.Upsert(record);
        }
        catch
        {
            try { if (File.Exists(state.OriginalPath) && !File.Exists(state.TranslatedPath)) File.Move(state.OriginalPath, state.TranslatedPath); } catch { }
            record.WineOverride = translatedOverride;
            file.Path = state.TranslatedPath;
            if (File.Exists(state.TranslatedPath)) file.Sha256 = await ComputeSha256Async(state.TranslatedPath, cancellationToken).ConfigureAwait(false);
            _records.Upsert(record);
            throw;
        }
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
