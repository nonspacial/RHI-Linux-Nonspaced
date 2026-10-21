using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w7/shader-sets (#179, review major 3 — decision 9 reversed): a game's shader packs brought to a new set WITHOUT
/// re-running ReShade's install. An Off toggle, a per-game choice or a global edit changes only the pack folder, the
/// search paths in ReShade.ini and the record's pack list (plus, the first time packs arrive, the native D3DCompiler_47
/// override ReShade's install would have set), all in one journaled operation on ReShade's record: ReShade's DLL, its
/// version, channel and download stay exactly as they were. Upstream likewise only re-copies the shader files.
/// <para>Owned by lane shader-sets; it reuses ReShade's own private steps (<c>CleanupManagedIni</c>,
/// <c>IniPathsTouchedByInstall</c>, <c>BuildD3DCompilerPlan</c>, <c>WriteIniMetadata</c>) so a change to how ReShade's
/// install treats ReShade.ini (#207) reaches this path too.</para>
/// </summary>
public sealed partial class ReShadeDeploymentService
{
    /// <summary>Fixed text before the reason when the shader packs could not be changed (PT-BR pair).</summary>
    public const string ShaderSyncFailedPrefix = "The shader packs were not changed: ";

    /// <summary>The game has no RHI ReShade, so it has no RHI shader packs to change (PT-BR pair).</summary>
    public const string ShaderSyncNotInstalledMessage = "RHI did not install ReShade on this game, so it has no RHI shader packs to change.";

    /// <summary>Fixed text before the pack names after a sync (PT-BR pair); ReShade itself was not touched.</summary>
    public const string ShaderSyncDonePrefix = "Shader packs updated; ReShade itself was not changed. Packs: ";

    /// <summary>A sync that took every RHI pack out (PT-BR pair).</summary>
    public const string ShaderSyncNoneMessage = "Shader packs updated; ReShade itself was not changed. RHI's shader packs were taken out of the game.";

    /// <summary>The pack list ReShade's record holds (what is deployed now).</summary>
    internal static IReadOnlyList<string> RecordedShaderPacks(LinuxDeploymentRecord? record)
        => record is null ? [] : ReadStringList(record.Metadata.GetValueOrDefault(MetaShaderPacks));

    /// <summary>
    /// Replaces the game's RHI shader packs with <paramref name="packIds"/> (dependencies added), keeping ReShade. Packs
    /// are downloaded and staged before the write phase; from the first change the sync runs to completion or full
    /// rollback, and the record carries the operation id as its commit point.
    /// </summary>
    public async Task<ComponentOperationResult> SyncShaderPacksAsync(DetectedGame game, IReadOnlyList<string> packIds,
        CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null) return ComponentOperationResult.Fail(ShaderSyncNotInstalledMessage);
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        var binaryDirectory = game.BinaryDirectory;
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);
        try { ReShadeShaderPackService.EnsureManifestReadable(binaryDirectory); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }

        // Packs need the native compiler as ReShade's install does (the Vulkan layer does not): checked, never fetched here.
        var layer = ReShadeVulkanLayer.IsLayerRecord(record);
        D3DCompilerPlan? compilerPlan = null;
        if (!layer && packIds.Count > 0)
        {
            var architecture = GameArchitecture.Of(game);
            if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
                return ComponentOperationResult.Fail("Could not determine whether the game executable is 32-bit or 64-bit.");
            try
            {
                compilerPlan = BuildD3DCompilerPlan(game, architecture,
                    new ReShadeInstallOptions { D3DCompiler47Path = record.Metadata.GetValueOrDefault(MetaD3DCompilerSource) }, record);
            }
            catch (Exception ex)
            {
                return ComponentOperationResult.Fail($"ReShade shader compiler compatibility check failed: {ex.Message}");
            }
        }

        ReShadeShaderPackService.PreparedShaderSync prepared;
        try
        {
            prepared = await _shaderPacks.PrepareSyncAsync(binaryDirectory, packIds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Cancel before any change: the caller says "cancelled", never "failed"
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail(ShaderSyncFailedPrefix + ex.Message);
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"ReShade shader packs: {game.Name}", game, ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var oldPacks = RecordedShaderPacks(record);
        var inis = IniPathsTouchedByInstall(game, record, coexist: false).ToArray();
        WineOverrideRecord? compilerOverride = null;
        var compilerOverrideCreated = false;
        ReShadeShaderSyncResult shaderResult;
        try
        {
            await scope.TrackAllAsync(inis, cancellationToken).ConfigureAwait(false);
            CleanupManagedIni(record, game.InstallPath);
            foreach (var ini in inis) await scope.DeclareWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);
            await _shaderPacks.CleanupAsync(binaryDirectory, scope.Operation).ConfigureAwait(false);

            LinuxDeployedFile? compilerFile = null;
            if (compilerPlan is not null)
            {
                if (compilerPlan.DeploySourcePath is not null && !record.Files.Any(file => IsD3DCompilerPath(file.Path)))
                {
                    var sha = await scope.PlaceCopyAsync(compilerPlan.DeploySourcePath, compilerPlan.TargetPath, cancellationToken, overwrite: false).ConfigureAwait(false);
                    compilerFile = new LinuxDeployedFile { Path = compilerPlan.TargetPath, ExistedBeforeInstall = false, Sha256 = sha };
                }
                if (!record.AdditionalWineOverrides.Any(IsD3DCompilerOverride))
                {
                    if (game.PrefixPath is not { } prefix || !Directory.Exists(prefix))
                        throw new InvalidOperationException("Wine/Proton prefix is not initialized; start the game once before enabling ReShade shader packs.");
                    compilerOverride = await _overrides.EnsureNativeBuiltinAsync(
                        scope.Operation, prefix, D3DCompilerOverrideName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                    compilerOverrideCreated = true;
                }
            }

            shaderResult = await _shaderPacks.ApplySyncAsync(prepared, scope.Operation, cancellationToken).ConfigureAwait(false);
            ReShadeIniApplyResult? iniResult = null;
            if (layer || record.Files.Any(file => IsReShadeIni(file.Path)))
                iniResult = ReShadeIniManager.Apply(binaryDirectory, shaderResult.EffectSearchPaths, shaderResult.TextureSearchPaths,
                    IniDefaults?.Invoke(game, layer), game.InstallPath);
            foreach (var ini in inis) await scope.DeclareWrittenContentAsync(ini, cancellationToken).ConfigureAwait(false);

            // The record as it was, with the new pack list and ReShade.ini state: version, channel, download and the DLL stay.
            record.Metadata[MetaShaderPacks] = JsonSerializer.Serialize(shaderResult.PackIds);
            WriteIniMetadata(record.Metadata, iniResult);
            record.Files.RemoveAll(file => IsReShadeIni(file.Path));
            if (iniResult is not null)
                record.Files.Add(new LinuxDeployedFile
                {
                    Path = iniResult.Path, ExistedBeforeInstall = iniResult.ExistedBefore, Sha256 = iniResult.Sha256, AllowUserChanges = true
                });
            if (compilerFile is not null) record.Files.Add(compilerFile);
            if (compilerOverride is not null) record.AdditionalWineOverrides.Add(compilerOverride);
            _records.Commit([scope.Stamp(record)]);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (scope.Operation is null)
            {
                // Unjournaled (tests, tools): the INI came back with the scope; the override and the packs go back here.
                if (compilerOverrideCreated && game.PrefixPath is { } prefix && Directory.Exists(prefix)) _overrides.TryRestore(prefix, compilerOverride);
                try
                {
                    await _shaderPacks.SyncAsync(binaryDirectory, oldPacks, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreError)
                {
                    AppLog.Warn("shader-sets", "Could not put the previous shader packs back after a failed shader pack change", restoreError);
                }
            }
            return ComponentOperationResult.Fail(ShaderSyncFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        // The record carries the operation id: the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(shaderResult.PackIds.Count == 0
            ? ShaderSyncNoneMessage
            : ShaderSyncDonePrefix + string.Join(", ", shaderResult.PackIds));
    }

    private static bool IsReShadeIni(string path) => Path.GetFileName(path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase);
}
