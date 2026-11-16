using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class GitcUpliftDeploymentService
{
    public const string UpToDatePrefix = "GITC Uplift is up to date: ";
    public const string UpdateAvailablePrefix = "GITC Uplift update available: ";
    public const string CheckFailedPrefix = "Could not check GITC Uplift: ";
    public const string UpdatedPrefix = "GITC Uplift updated to ";
    public const string SwappedPrefix = "GITC Uplift switched to ";
    public const string LaunchpadOnMessage = "iMMERSE Launchpad is on for GITC Uplift in this game (above the Uplift effect).";
    public const string LaunchpadOffMessage = "iMMERSE Launchpad is off for GITC Uplift in this game.";
    public const string LaunchpadSavedMessage = "Saved: GITC Uplift will be installed with this iMMERSE Launchpad choice.";
    public const string KeyWrittenPrefix = "GITC Uplift NR on/off key written: ";
    public const string NoKeyMessage = "Pick a key first. None leaves each game's own Uplift key alone.";

    // ── Remove ────────────────────────────────────────────────────────────────

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        // A changed add-on, helper, NR DLL or effect stops the removal before anything changes (a preset the user edited
        // and Launchpad files are looked at one by one below).
        foreach (var file in record.Files.Where(f => !f.AllowUserChanges && !ImmerseLaunchpadService.IsLaunchpadFile(f.Path) && File.Exists(f.Path)))
            if (!string.Equals(await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail(ModifiedPrefix + file.Path);

        var (opened, refusal) = await DeploymentScope.BeginRemovalAsync(_journal, _records, "GITC Uplift removal: " + game.Name, game,
            ManagedComponentKind.GitcUplift, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var kept = new List<string>();
        try
        {
            var binaryDir = record.BinaryDirectory;
            var presetCreated = record.Metadata.GetValueOrDefault(MetaPresetCreated) == "1";
            var presetPath = record.Metadata.GetValueOrDefault(MetaPresetPath);
            // Techniques: only those RHI switched on come out (upstream). A preset RHI created and nobody changed goes whole.
            if (!string.IsNullOrWhiteSpace(presetPath) && File.Exists(presetPath)
                && !(presetCreated && record.Files.FirstOrDefault(f => f.Path == presetPath) is { } owned
                     && string.Equals(await FileHash.Sha256HexAsync(presetPath, CancellationToken.None).ConfigureAwait(false), owned.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                var text = await File.ReadAllTextAsync(presetPath, CancellationToken.None).ConfigureAwait(false);
                var back = text;
                if (record.Metadata.GetValueOrDefault(MetaLaunchpad) == "1") back = ImmerseLaunchpadService.Disable(back).Text;
                if (record.Metadata.GetValueOrDefault(MetaTechnique) == "1") back = ReShadePresetText.RemoveTechnique(back, TechniqueToken).Text;
                if (!string.Equals(back, text, StringComparison.Ordinal))
                {
                    var target = ReShadePresetFile.WriteTarget(presetPath, binaryDir, game.InstallPath);
                    await scope.TrackAsync(target, CancellationToken.None).ConfigureAwait(false);
                    ReShadePresetFile.Write(presetPath, back, binaryDir, game.InstallPath);
                    await scope.DeclareWrittenContentAsync(target, CancellationToken.None).ConfigureAwait(false);
                }
            }

            // ReShade.ini: the NR key back to what it was (while it still holds RHI's value), then the search paths RHI added.
            if (record.Metadata.GetValueOrDefault(MetaIniPath) is { Length: > 0 } iniPath && File.Exists(iniPath))
            {
                var iniTarget = ReShadeIniManager.ResolveWriteTarget(iniPath, binaryDir, game.InstallPath);
                await scope.TrackAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
                RestoreKey(iniPath, binaryDir, game.InstallPath, record);
                ReShadeIniManager.Cleanup(iniPath,
                    record.Metadata.GetValueOrDefault(MetaIniCreated) == "1",
                    record.Metadata.GetValueOrDefault(MetaIniHash),
                    ReadList(record.Metadata.GetValueOrDefault(MetaIniEffectAdded)),
                    ReadList(record.Metadata.GetValueOrDefault(MetaIniTextureAdded)),
                    record.Metadata.GetValueOrDefault(MetaIniEffectKeyCreated) == "1",
                    record.Metadata.GetValueOrDefault(MetaIniTextureKeyCreated) == "1",
                    game.InstallPath);
                if (File.Exists(iniTarget)) await scope.DeclareWrittenContentAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
            }

            kept.AddRange(await ImmerseLaunchpadService.DeleteOwnedAsync(scope, record, _records, game).ConfigureAwait(false));
            foreach (var file in record.Files.Where(f => !ImmerseLaunchpadService.IsLaunchpadFile(f.Path) && !f.ExistedBeforeInstall))
            {
                if (!File.Exists(file.Path)) continue;
                if (file.AllowUserChanges
                    && !string.Equals(await FileHash.Sha256HexAsync(file.Path, CancellationToken.None).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(file.Path);
                    continue;
                }
                await scope.DeleteAsync(file.Path, CancellationToken.None).ConfigureAwait(false);
            }
            _records.Commit([], [(game, ManagedComponentKind.GitcUplift)]);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "gitc-uplift").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName, removal: true);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        foreach (var directory in record.DirectoriesCreatedByRhi.OrderByDescending(d => d.Length))
            FileCleanup.TryDeleteEmptyTree(directory);
        return ComponentOperationResult.Ok(RemovedMessage + (kept.Count == 0 ? "" : KeptPrefix + string.Join(", ", kept.Select(Path.GetFileName)) + "."));
    }

    private static void RestoreKey(string iniPath, string binaryDir, string? gameRoot, LinuxDeploymentRecord record)
    {
        if (record.Metadata.GetValueOrDefault(MetaKeyWritten) is not { Length: > 0 } written) return;
        var text = File.ReadAllText(iniPath);
        if (!string.Equals(ReShadePresetText.GetKey(text, IniSection, EnableKeyKey), written, StringComparison.Ordinal)) return; // the user's since
        var back = record.Metadata.GetValueOrDefault(MetaKeyPreviousExists) == "1"
            ? ReShadePresetText.SetKey(text, IniSection, EnableKeyKey, record.Metadata.GetValueOrDefault(MetaKeyPrevious) ?? "")
            : ReShadePresetText.RemoveKey(text, IniSection, EnableKeyKey, dropEmptySection: true);
        ReShadePresetFile.Write(iniPath, back, binaryDir, gameRoot);
    }

    // ── Update / version switch ───────────────────────────────────────────────

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, string? pin, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, NotInstalledMessage);
        if (await ManagedUpdateFileGuard.HeldAsync(_journal, game, ManagedComponentKind.GitcUplift, record.Version).ConfigureAwait(false) is { } held) return held;
        try
        {
            var (release, _) = await ResolveAsync(pin, cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, release.Version, StringComparison.OrdinalIgnoreCase);
            return new ManagedUpdateCheck(true, update,
                (update ? UpdateAvailablePrefix + $"{record.Version} → {release.Version}" : UpToDatePrefix + record.Version) + ".",
                record.Version, release.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, CheckFailedPrefix + ex.Message, record.Version, CouldNotCheck: true);
        }
    }

    /// <summary>
    /// Brings the installed add-on, helper and effects to the pinned version (null = Latest), all from one release and in
    /// one journaled swap: all of them change or none does (Uplift 2.0's add-on and helper refuse each other's other
    /// releases; upstream 2.9.3 fixed a running game leaving them on different versions). Refused while the game runs, and
    /// when a file changed outside RHI. The NR DLL, preset and ReShade.ini are not touched.
    /// </summary>
    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, string? pin, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (_running.IsRunning(game)) return ComponentOperationResult.Fail(RunningMessage);
        GitcUpliftPayload payload;
        string? pinNote;
        try
        {
            GitcUpliftRelease release;
            (release, pinNote) = await ResolveAsync(pin, cancellationToken).ConfigureAwait(false);
            payload = await EnsureCachedAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
        }

        var items = new List<(string RecordedPath, string SourcePath)>();
        foreach (var name in new[] { Addon64, Addon32, HelperExe }.Concat(ShaderFiles))
            if (record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase) && !ImmerseLaunchpadService.IsLaunchpadFile(f.Path)) is { } file)
                items.Add((file.Path, payload.File(name)));
        try
        {
            var changed = await ManagedFileSwap.ReplaceAsync(record, items, updated =>
            {
                updated.Version = payload.Release.Version;
                updated.SourceUrl = payload.Release.AssetUrl;
                updated.Metadata[MetaTag] = payload.Release.Tag;
                updated.Metadata[MetaPin] = pin is null || pinNote is not null ? "" : payload.Release.Version;
                VerifiedDownload.Record(updated.Metadata, payload.ZipSha256, payload.DigestVerified);
                _records.Upsert(updated);
            }, cancellationToken, _journal, game).ConfigureAwait(false);
            var verb = pin is null ? UpdatedPrefix : SwappedPrefix;
            return ComponentOperationResult.Ok(verb + payload.Release.Version + "." + (pinNote is null ? "" : " " + pinNote + "."),
                changed == 0 ? [] : items.Select(i => i.RecordedPath).ToArray());
        }
        catch (ManagedFileRestoreException ex)
        {
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return ComponentOperationResult.Fail(ModifiedPrefix + ex.Message);
        }
    }

    /// <summary>The NR DLL in Uplift's <c>GITC-Uplift</c> folder to a manifest version, in place (the NR DLL dropdown).</summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(DetectedGame game, string? version, CancellationToken cancellationToken = default)
        => _dlss is null
            ? Task.FromResult(ComponentOperationResult.Fail(NoNrDllNote.Trim()))
            : NeuralRenderingDllSwap.SwapAsync(_records, _dlss, game, ManagedComponentKind.GitcUplift, DisplayName, version, cancellationToken, _journal);

    // ── iMMERSE Launchpad toggle ──────────────────────────────────────────────

    /// <summary>
    /// Switches iMMERSE Launchpad on (its files placed, its technique on above Uplift's and moved to the top) or off (only a
    /// technique RHI switched on comes out; its files go when no other component lists them). Journaled on Uplift's record.
    /// </summary>
    public async Task<ComponentOperationResult> SetLaunchpadAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Ok(LaunchpadSavedMessage);
        var state = record.Metadata.GetValueOrDefault(MetaLaunchpad) ?? "";
        if (on == state is "1" or "user") return ComponentOperationResult.Ok(on ? LaunchpadOnMessage : LaunchpadOffMessage);
        LaunchpadStage? stage = null;
        var stageRoot = Path.Combine(_cacheRoot, $"stage-{Guid.NewGuid():N}");
        try
        {
            if (on) stage = await _launchpad.StageAsync(stageRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            DeleteStage(stageRoot);
            return ComponentOperationResult.Fail(DownloadFailedPrefix + ex.Message);
        }

        var (opened, refusal) = await DeploymentScope.BeginAsync(_journal, "GITC Uplift iMMERSE Launchpad: " + game.Name, game, ManagedComponentKind.GitcUplift, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            DeleteStage(stageRoot);
            return ComponentOperationResult.Fail(refusal);
        }
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            var updated = LinuxDeploymentRecordStore.Clone(record);
            var binaryDir = record.BinaryDirectory;
            if (on)
            {
                updated.Files.AddRange(await ImmerseLaunchpadService.PlaceAsync(scope, stage!, binaryDir, _records, game, ManagedComponentKind.GitcUplift).ConfigureAwait(false));
                VerifiedDownload.Record(updated.Metadata, stage!.ArchiveSha256, false, ImmerseLaunchpadService.DownloadPart);
                var iniPath = ReShadeIniManager.ResolveIniPath(binaryDir);
                var iniTarget = ReShadeIniManager.ResolveWriteTarget(iniPath, binaryDir, game.InstallPath);
                await scope.TrackAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
                var added = ReShadeIniManager.Apply(binaryDir, [ImmerseLaunchpadService.EffectSearchPath], [ImmerseLaunchpadService.TextureSearchPath], gameRoot: game.InstallPath);
                await scope.DeclareWrittenContentAsync(iniTarget, CancellationToken.None).ConfigureAwait(false);
                MergeIniAdditions(updated.Metadata, added);
                await EditPresetAsync(scope, game, binaryDir, updated.Metadata, updated.Files, (text, m) =>
                {
                    var (withUplift, upliftOn, _) = ReShadePresetText.AddTechnique(text, TechniqueToken);
                    if (string.IsNullOrEmpty(m.GetValueOrDefault(MetaTechnique))) m[MetaTechnique] = upliftOn ? "user" : "1";
                    var (withLaunchpad, launchpadOn, _) = ImmerseLaunchpadService.Enable(withUplift, TechniqueToken);
                    m[MetaLaunchpad] = launchpadOn && state != "1" ? "user" : "1";
                    return ImmerseLaunchpadService.KeepFirst(withLaunchpad).Text;
                }).ConfigureAwait(false);
            }
            else
            {
                if (state == "1")
                    await EditPresetAsync(scope, game, binaryDir, updated.Metadata, null, (text, _) => ImmerseLaunchpadService.Disable(text).Text).ConfigureAwait(false);
                await ImmerseLaunchpadService.DeleteOwnedAsync(scope, record, _records, game).ConfigureAwait(false);
                updated.Files.RemoveAll(f => ImmerseLaunchpadService.IsLaunchpadFile(f.Path));
                updated.Metadata[MetaLaunchpad] = "off";
            }
            _records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        finally
        {
            DeleteStage(stageRoot);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(on ? LaunchpadOnMessage : LaunchpadOffMessage);
    }

    /// <summary>True when the installed Uplift has iMMERSE Launchpad on (RHI's or the user's own technique).</summary>
    public bool LaunchpadOn(DetectedGame game) => GetRecord(game)?.Metadata.GetValueOrDefault(MetaLaunchpad) is "1" or "user";

    /// <summary>iMMERSE Launchpad back at the top of the game's preset when lower down (only while the game is not running).</summary>
    public Task<bool> KeepLaunchpadFirstAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => LaunchpadOn(game)
            ? _launchpad.KeepFirstAsync(game, _records, ManagedComponentKind.GitcUplift, _journal, cancellationToken)
            : Task.FromResult(false);

    private static void MergeIniAdditions(Dictionary<string, string> metadata, ReShadeIniApplyResult added)
    {
        var effects = ReadList(metadata.GetValueOrDefault(MetaIniEffectAdded)).Concat(added.AddedEffectPaths).Distinct(StringComparer.Ordinal).ToList();
        var textures = ReadList(metadata.GetValueOrDefault(MetaIniTextureAdded)).Concat(added.AddedTexturePaths).Distinct(StringComparer.Ordinal).ToList();
        metadata[MetaIniEffectAdded] = System.Text.Json.JsonSerializer.Serialize(effects);
        metadata[MetaIniTextureAdded] = System.Text.Json.JsonSerializer.Serialize(textures);
        if (added.TextureKeyCreated) metadata[MetaIniTextureKeyCreated] = "1";
        if (added.EffectKeyCreated) metadata[MetaIniEffectKeyCreated] = "1";
        metadata[MetaIniPath] = added.Path;
        metadata[MetaIniHash] = File.Exists(added.Path) ? FileHash.Sha256Hex(added.Path) : "";
    }

    // ── NR on/off key ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the NR on/off key (<c>[Uplift] EnableKey</c>, a Windows virtual-key code) into the game's ReShade.ini
    /// (upstream "Write key to all Uplift games", one game). Journaled on Uplift's record; the first value RHI replaced is
    /// what removal puts back.
    /// </summary>
    public async Task<ComponentOperationResult> WriteEnableKeyAsync(DetectedGame game, int vk, CancellationToken cancellationToken = default)
    {
        if (vk is <= 0 or > 255) return ComponentOperationResult.Fail(NoKeyMessage);
        var record = GetRecord(game);
        if (record is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        var iniPath = record.Metadata.GetValueOrDefault(MetaIniPath) is { Length: > 0 } recorded ? recorded : ReShadeIniManager.ResolveIniPath(record.BinaryDirectory);
        if (!File.Exists(iniPath)) return ComponentOperationResult.Fail(NeedsReShadeMessage);
        var (opened, refusal) = await DeploymentScope.BeginAsync(_journal, "GITC Uplift NR key: " + game.Name, game, ManagedComponentKind.GitcUplift, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return ComponentOperationResult.Fail(refusal);
        await using var scope = opened!;
        try
        {
            var updated = LinuxDeploymentRecordStore.Clone(record);
            var target = ReShadeIniManager.ResolveWriteTarget(iniPath, record.BinaryDirectory, game.InstallPath);
            await scope.TrackAsync(target, CancellationToken.None).ConfigureAwait(false);
            WriteKeyInto(iniPath, record.BinaryDirectory, game.InstallPath, vk, updated.Metadata, firstWrite: false);
            await scope.DeclareWrittenContentAsync(target, CancellationToken.None).ConfigureAwait(false);
            updated.Metadata[MetaIniHash] = FileHash.Sha256Hex(iniPath);
            _records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed(DisplayName);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(KeyWrittenPrefix + game.Name);
    }
}
