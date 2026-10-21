using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One of DXVK HDR's DirectX 9 <c>dxvk.conf</c> presets (upstream <c>LiliumD3d9Presets</c>), safest first.</summary>
public sealed record DxvkHdrPreset(int Index, string Name, string Content);

/// <summary>
/// #200: upstream RHI 2.8.7's six DirectX 9 tiers (<c>DxvkService.LiliumD3d9Presets</c>), each the complete
/// <c>dxvk.conf</c>. Linux leaves out upstream's two async lines (<c>dxvk.enableAsync</c>, <c>dxvk.gplAsyncCache</c>):
/// they belong to the gplasync builds only; this build ignores them and DXVK HDR's own presets say "only enable these on
/// Windows".
/// </summary>
public static class DxvkHdrPresets
{
    /// <summary>Upstream's default tier (0, Safest: swap chain only).</summary>
    public const int Default = 0;

    private const string SwapChain = """
        d3d9.enableSwapChainUpgrade               = true
        d3d9.upgradeSwapChainFormatTo             = rgba16_sfloat
        d3d9.upgradeSwapChainColorSpaceTo         = scRGB
        """;

    private const string Window = "d3d9.enforceWindowModeInternally          = disabled";

    private static string RenderTargets(string to) => $"""
        d3d9.enableRenderTargetUpgrades           = true
        d3d9.upgrade_B5G6R5_UNORM_renderTargetTo  = {to}
        d3d9.upgrade_BGR5A1_UNORM_renderTargetTo  = {to}
        d3d9.upgrade_BGR5X1_UNORM_renderTargetTo  = {to}
        d3d9.upgrade_BGRA4_UNORM_renderTargetTo   = {to}
        d3d9.upgrade_BGRX4_UNORM_renderTargetTo   = {to}
        d3d9.upgrade_RGBA8_UNORM_renderTargetTo   = {to}
        d3d9.upgrade_RGBX8_UNORM_renderTargetTo   = {to}
        d3d9.upgrade_BGRA8_UNORM_renderTargetTo   = {to}
        d3d9.upgrade_BGRX8_UNORM_renderTargetTo   = {to}
        d3d9.upgrade_RGB10A2_UNORM_renderTargetTo = {to}
        d3d9.upgrade_BGR10A2_UNORM_renderTargetTo = {to}
        d3d9.upgrade_RGBA16_UNORM_renderTargetTo  = {to}
        """;

    private static string BackBuffer(string to) => $"""
        d3d9.enableBackBufferUpgrade              = true
        d3d9.upgradeBackBufferTo                  = {to}
        """;

    /// <summary>The six tiers in upstream's order and content (minus the async lines).</summary>
    public static IReadOnlyList<DxvkHdrPreset> All { get; } =
    [
        new(0, "Safest", string.Join('\n', SwapChain, Window)),
        new(1, "2nd Safest", string.Join('\n', BackBuffer("rgba16_unorm"), SwapChain, Window)),
        new(2, "Slightly Unsafe", string.Join('\n', SwapChain, BackBuffer("rgba16_sfloat"), Window)),
        new(3, "Unsafer", string.Join('\n', RenderTargets("rgba16_unorm"), BackBuffer("rgba16_unorm"), SwapChain, Window)),
        new(4, "Even Unsafer", string.Join('\n', RenderTargets("rgba16_unorm"), BackBuffer("rgba16_sfloat"), SwapChain, Window)),
        new(5, "Experimental", string.Join('\n', RenderTargets("rgba16_sfloat"), BackBuffer("rgba16_sfloat"), SwapChain, Window)),
    ];

    /// <summary>The tier at <paramref name="index"/>; upstream's fallback for anything else is Safest.</summary>
    public static DxvkHdrPreset For(int index) => index >= 0 && index < All.Count ? All[index] : All[Default];

    /// <summary>The whole <c>dxvk.conf</c> RHI writes for <paramref name="index"/> (a comment line, then the tier).</summary>
    public static string ConfFor(int index)
    {
        var preset = For(index);
        return $"# Written by RHI Linux for DX9 HDR (preset {preset.Index}: {preset.Name}). RHI leaves this file alone once you edit it.\n"
               + preset.Content + "\n";
    }
}

/// <summary>
/// #200 DX9 HDR: EndlesslyFlowering's DXVK HDR build (upstream RHI's "Lilium HDR" DXVK variant,
/// <c>DxvkService</c> DX9 direct mode) as the game's <c>d3d9.dll</c> beside its executable, with a <c>dxvk.conf</c> preset
/// beside it and a per-exe <c>d3d9=native,builtin</c> Wine override (GE-Proton10-18 and Proton Experimental skip a
/// game-folder d3d9.dll without it; <c>reviews/w7-dxvk-hdr-wine.md</c>). Opt-in per DirectX 9 game, removable byte for
/// byte; the owner overrode the fork's "Proton owns DXVK" rule for it ("the point of mods is to mod").
/// <para>An RHI ReShade installed as <c>d3d9.dll</c> moves to ReShade's per-game Vulkan layer in the same operation and
/// back on removal (<see cref="ReShadeDeploymentService.MoveToLayerForDxvkHdrInsideAsync"/>): both records are saved in one
/// ledger write. Everything is journaled; downloads come before the write gate; a user's own <c>d3d9.dll</c> or
/// <c>dxvk.conf</c> is never replaced, and a <c>dxvk.conf</c> the user changed is kept on removal.</para>
/// </summary>
public sealed partial class DxvkHdrDeploymentService
{
    public const string ReleaseApiUrl = "https://api.github.com/repos/EndlesslyFlowering/dxvk/releases/latest";
    /// <summary>The release's 7-Zip asset, not a <c>gplasync</c> one (upstream <c>EnsureStagingLiliumAsync</c>).</summary>
    internal const string AssetNamePattern = @"^(?!.*gplasync).*\.7z$";
    internal const string ReleasePathPrefix = "/EndlesslyFlowering/dxvk/releases/download/";
    public const string D3d9FileName = "d3d9.dll";
    public const string ConfFileName = "dxvk.conf";
    public const string DisplayName = ComponentProbe.DxvkHdrDisplayName;
    /// <summary>Largest d3d9.dll accepted from the archive (real ones are about 8 MB).</summary>
    public const long D3d9MaxBytes = 64L * 1024 * 1024;

    /// <summary>Record metadata: the preset index the dxvk.conf was written for.</summary>
    public const string MetaPreset = "DxvkHdr.Preset";

    // Platform messages: fixed text first, values last (PT-BR presentation pairs in UiLocalizationService).
    public const string NotOfferedMessage = "DX9 HDR is offered only for Windows games that use DirectX 9 (not Xbox / Game Pass installs).";
    /// <summary>A manifest-listed XNA game (#201): its d3d9.dll is loaded from the prefix's .NET assembly folder only (PT-BR pair).</summary>
    public const string XnaGameMessage =
        "This is an XNA game: it loads d3d9.dll only from the .NET assembly folder of its Wine/Proton prefix, never from the game folder, so DX9 HDR cannot work on it.";
    public const string PrefixNotStartedMessage = "The game's Wine/Proton prefix is not set up yet. Start the game once and close it, then install DX9 HDR.";
    public const string BitnessUnknownMessage = "RHI cannot tell whether the game is 32-bit or 64-bit, so DX9 HDR was not installed. Set the bitness in Edit game.";
    public const string AlreadyInstalledMessage = "DX9 HDR is already installed for this game.";
    public const string NotInstalledMessage = "DX9 HDR is not installed by RHI for this game.";
    public const string ForeignD3d9Prefix = "A d3d9.dll RHI did not install is beside the game; RHI does not replace it, so nothing was installed: ";
    public const string HeldByComponentPrefix = "d3d9.dll beside the game belongs to another RHI component, so DX9 HDR was not installed. Remove it first: ";
    /// <summary>Review minor 5 / coordinator decision 8: DX9 HDR asked for while Luma's or DLSS5 Feeder's DirectX 9 translation holds d3d9.dll.</summary>
    public const string Dx9TranslationPrefix = "This game uses the DirectX 9 translation (dgVoodoo2) of another RHI component, which needs the game's d3d9.dll just as DX9 HDR does, so nothing was changed. Remove that component from its row first, then install DX9 HDR. The component: ";
    /// <summary>Coordinator decision 8, the other way round: the DirectX 9 translation asked for while DX9 HDR is installed.</summary>
    public const string BlocksDx9TranslationMessage = "DX9 HDR is installed on this game and uses the game's d3d9.dll, which the DirectX 9 translation (dgVoodoo2) needs too, so nothing was changed. Remove DX9 HDR from its row first, then try again.";
    public const string ForeignConfPrefix = "A dxvk.conf RHI did not write is beside the game; RHI does not overwrite it, so nothing was installed. Move or delete it, then try again: ";
    public const string DownloadFailedPrefix = "DX9 HDR download failed; nothing was changed: ";
    public const string UntrustedAssetPrefix = "The DXVK HDR release link is not in EndlesslyFlowering's dxvk releases, so it was not downloaded: ";
    public const string PackageIncompletePrefix = "The DXVK HDR release does not hold the d3d9.dll this game needs: ";
    public const string NotDxvkHdrPrefix = "The d3d9.dll in the DXVK HDR release is not a DXVK HDR build for this game: ";
    public const string D3d9TooLargePrefix = "The d3d9.dll in the DXVK HDR release is larger than RHI accepts (64 MiB), so it was not used: ";

    /// <summary>w7 follow-up B24: the archive lists the member more than once (PT-BR pair); nothing was extracted.</summary>
    public const string DuplicateMemberPrefix = "The DXVK HDR release lists the d3d9.dll this game needs more than once, so RHI did not use it: ";
    public const string InstalledPrefix = "DX9 HDR installed (d3d9.dll and dxvk.conf beside the game). Version: ";
    public const string ReShadeMovedNote = " ReShade moved to its Vulkan layer for this game; removing DX9 HDR puts it back as d3d9.dll.";
    public const string UpdatedPrefix = "DX9 HDR updated. Version: ";
    public const string AlreadyCurrentPrefix = "DX9 HDR is already the newest build: ";
    public const string RemovedMessage = "DX9 HDR removed; the game uses its runner's DirectX 9 again.";
    public const string ReShadeBackNote = " ReShade is back as d3d9.dll.";
    public const string KeptConfPrefix = " dxvk.conf was changed after RHI wrote it, so it was kept: ";
    public const string FailedRolledBackPrefix = "DX9 HDR failed and was rolled back; nothing was changed: ";
    public const string RemovalFailedPrefix = "DX9 HDR removal failed and was rolled back; DX9 HDR stays installed: ";
    public const string ModifiedFilePrefix = "DX9 HDR's d3d9.dll was changed outside RHI, so it was left as it is and nothing was changed: ";
    public const string ConfChangedPrefix = "dxvk.conf was changed after RHI wrote it, so RHI did not replace it; edit it yourself, or remove DX9 HDR and install it again: ";
    public const string PresetSetMessage = "DX9 HDR preset changed; the game uses it from its next start.";
    public const string UpdateAvailablePrefix = "A newer DX9 HDR build is available: ";
    public const string CurrentPrefix = "DX9 HDR is current: ";
    public const string CouldNotCheckPrefix = "Could not check for a newer DX9 HDR build: ";
    public const string RowNote = "Replaces the game's DirectX 9 with DXVK HDR for this game only (d3d9.dll and dxvk.conf beside the game). Remove puts everything back.";
    public const string RowReShadeNote = " ReShade on this game moves to its Vulkan layer while DX9 HDR is installed.";
    public const string HdrLaunchOffNote = " HDR launch is Off for this game: turn it on (HDR launch, in the game's details) to see HDR.";
    public const string WineD3dNote = " The game's launch command sets PROTON_USE_WINED3D, so Proton uses Wine's own DirectX 9 and DX9 HDR has no effect: take it out of the launch command (Edit game) to use DX9 HDR.";
    public const string ExternalNote = "A DXVK HDR d3d9.dll RHI did not install is beside the game; RHI leaves it alone.";
    public const string MissingD3d9Note = "DX9 HDR's d3d9.dll is no longer beside the game, so DX9 HDR does nothing now. Remove takes away the rest RHI set up for it; then you can install it again.";

    /// <summary>
    /// w7 follow-up B25: Update (and Update All) never puts back a d3d9.dll the user deleted, nor updates a game that no
    /// longer takes DX9 HDR (PT-BR pair).
    /// </summary>
    public const string UpdateNeedsReinstallMessage =
        "DX9 HDR was not updated: its d3d9.dll is no longer beside the game, or the game no longer takes DX9 HDR. Remove DX9 HDR, then install it again if you still want it.";

    /// <summary>Why DX9 HDR on <paramref name="game"/> (record <paramref name="record"/>) must not be updated, or null.</summary>
    private static string? UpdateRefusal(DetectedGame game, LinuxDeploymentRecord record)
        => D3d9Missing(record) || XnaGacReShade.IsListed(game) || !ComponentProbe.IsDxvkHdrCandidate(game) ? UpdateNeedsReinstallMessage : null;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly ReShadeDeploymentService _reshade;
    private readonly DeploymentJournal? _journal;
    private readonly string _cacheDir;

    public DxvkHdrDeploymentService(HttpClient http, LinuxDeploymentRecordStore records, WineDllOverrideService overrides,
        ReShadeDeploymentService reshade, DeploymentJournal? journal = null, string? cacheDir = null)
    {
        _http = http;
        _records = records;
        _overrides = overrides;
        _reshade = reshade;
        _journal = journal;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "dxvk-hdr");
    }

    /// <summary>
    /// Whether the game's HDR launch setting is on, read from the app's in-memory library (the view model sets it: the
    /// same state its HDR controls and the ⚙ dialog read); null = unknown.
    /// </summary>
    public Func<DetectedGame, bool>? HdrLaunchOn { get; set; }

    /// <summary>
    /// <see cref="HdrLaunchOn"/> for the row note. The selection analysis decorates rows off the UI thread while the HDR
    /// control may change the library on it: a read that meets that write counts as unknown (no note) rather than failing
    /// the analysis; the next selection reads it again. Any exception counts (re-review nit 4): a read during the
    /// dictionary's first insert can throw NullReferenceException, not only the "collection was modified" one.
    /// </summary>
    internal bool? ReadHdrLaunchOn(DetectedGame game)
    {
        try { return HdrLaunchOn?.Invoke(game); }
        catch (Exception) { return null; }
    }

    /// <summary>Test seam: runs inside the install right after d3d9.dll is placed (to fail or kill it mid-way).</summary>
    internal Action? AfterD3d9PlacedForTesting { get; set; }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.DxvkHdr);

    /// <summary>The preset the installed dxvk.conf was written for (Safest when not installed).</summary>
    public int PresetOf(DetectedGame game)
        => GetRecord(game)?.Metadata.GetValueOrDefault(MetaPreset) is { } text && int.TryParse(text, out var index) ? DxvkHdrPresets.For(index).Index : DxvkHdrPresets.Default;

    /// <summary>Whether installing DX9 HDR now would move an RHI ReShade from d3d9.dll to its Vulkan layer (the confirmation says so).</summary>
    public bool WillMoveReShade(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.ReShade) is { } reshade && !ReShadeVulkanLayer.IsLayerRecord(reshade);

    /// <summary>Why DX9 HDR cannot be installed on <paramref name="game"/> now (plain reason), or null.</summary>
    public static string? OfferRefusal(DetectedGame game)
    {
        if (XnaGacReShade.IsListed(game)) return XnaGameMessage; // w7 integration (#200 × #201): XNA never loads it
        if (!ComponentProbe.IsDxvkHdrCandidate(game)) return NotOfferedMessage;
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath) || !WineRegistryEditor.HasUserHive(game.PrefixPath))
            return PrefixNotStartedMessage;
        return null;
    }

    /// <summary>The DX9 HDR row (the component service's decoration hook): installed state, action and note.</summary>
    /// <remarks>
    /// RHI's record makes the row Installed with Remove whatever is left on disk (review major 1): a d3d9.dll the user
    /// deleted, or a game no longer DirectX 9, must still let RHI take dxvk.conf, the per-exe override and ReShade's move
    /// to its Vulkan layer back. Without a record the row is shown only where DX9 HDR can apply (Unsupported = hidden).
    /// </remarks>
    public void Decorate(DetectedGame game, IReadOnlyList<ManagedComponent> components, LinuxDeploymentRecord? record)
    {
        if (components.FirstOrDefault(component => component.Kind == ManagedComponentKind.DxvkHdr) is not { } row) return;
        if (record is null && row.Availability == ComponentAvailability.Unsupported) return;
        row.ManagedByRhi = record is not null;
        if (record is not null && row.Availability is ComponentAvailability.Available or ComponentAvailability.Unsupported)
            row.Availability = ComponentAvailability.Installed;
        if (!game.HasConfirmedPatchTarget) return; // the service's patch-target note stays
        var hdrOff = (LaunchUsesWineD3d(game) ? WineD3dNote : "") + (ReadHdrLaunchOn(game) == false ? HdrLaunchOffNote : "");
        if (row.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable)
        {
            row.ActionAllowed = record is not null;
            row.Note = record is null ? ExternalNote : D3d9Missing(record) ? MissingD3d9Note : RowNote + hdrOff;
            row.InstalledVersion = record?.Version;
            return;
        }
        if ((OfferRefusal(game) ?? Dx9TranslationRefusal(game)) is { } refusal)
        {
            row.ActionAllowed = false;
            row.Note = refusal;
            return;
        }
        row.ActionAllowed = true;
        row.Note = RowNote + (WillMoveReShade(game) ? RowReShadeNote : "") + hdrOff;
    }

    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not null) return ComponentOperationResult.Fail(AlreadyInstalledMessage);
        if (OfferRefusal(game) is { } refusal) return ComponentOperationResult.Fail(refusal);
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory)
            || string.IsNullOrWhiteSpace(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        var architecture = GameArchitecture.Of(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64)) return ComponentOperationResult.Fail(BitnessUnknownMessage);
        if (_overrides.EditRefusal(game.PrefixPath) is { } running) return ComponentOperationResult.Fail(running);

        var directory = game.BinaryDirectory;
        var d3d9 = Path.Combine(directory, D3d9FileName);
        var conf = Path.Combine(directory, ConfFileName);
        // Before the ReShade checks: the translation moved ReShade to dxgi.dll, so its refusal would give the wrong advice.
        if (Dx9TranslationRefusal(game) is { } translation) return ComponentOperationResult.Fail(translation);
        var reshade = _records.Find(game, ManagedComponentKind.ReShade);
        var moveReShade = reshade is not null && !ReShadeVulkanLayer.IsLayerRecord(reshade);
        if (moveReShade && _reshade.DxvkHdrMoveRefusal(game, reshade!) is { } notMovable) return ComponentOperationResult.Fail(notMovable);
        if (!moveReShade && ReShadeDeploymentService.FindFileCaseInsensitive(directory, D3d9FileName) is { } taken)
            return ComponentOperationResult.Fail(HolderOf(game, taken) is { } holder ? HeldByComponentPrefix + holder : ForeignD3d9Prefix + taken);
        if (ReShadeDeploymentService.FindFileCaseInsensitive(directory, ConfFileName) is { } userConf)
            return ComponentOperationResult.Fail(ForeignConfPrefix + userConf);

        // Everything from the network first (DXVK HDR, and the Khronos loader when ReShade moves): nothing changes before.
        Staged staged;
        DxvkHdrReShadeMovePlan? movePlan = null;
        try
        {
            staged = await StageAsync(architecture, cancellationToken).ConfigureAwait(false);
            if (moveReShade) movePlan = await _reshade.PrepareDxvkHdrMoveAsync(game, reshade!, architecture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsSourceFailure(ex))
        {
            AppLog.Warn("dxvk-hdr", "The DX9 HDR download failed", ex);
            return new ComponentOperationResult(false, DownloadFailedPrefix + ex.Message, SourceUnavailable: ex is HttpRequestException or TaskCanceledException);
        }
        var preset = DxvkHdrPresets.Default;
        var confBytes = Encoding.UTF8.GetBytes(DxvkHdrPresets.ConfFor(preset));

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "DX9 HDR install: " + game.Name, game, ManagedComponentKind.DxvkHdr, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Downloaded and staged: from the first change on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var prefix = game.PrefixPath!;
        DxvkHdrReShadeMove? moved = null;
        WineOverrideRecord? d3d9Override = null;
        var overrideCreated = false;
        try
        {
            if (movePlan is not null)
            {
                moved = await _reshade.MoveToLayerForDxvkHdrInsideAsync(game, movePlan, scope, cancellationToken).ConfigureAwait(false);
                d3d9Override = moved.D3d9Override;
            }
            var d3d9Sha = await scope.PlaceCopyAsync(staged.D3d9Path, d3d9, cancellationToken, staged.D3d9Sha256, overwrite: false).ConfigureAwait(false);
            AfterD3d9PlacedForTesting?.Invoke();
            var confSha = await scope.PlaceBytesAsync(conf, confBytes, cancellationToken).ConfigureAwait(false);
            if (d3d9Override is null)
            {
                d3d9Override = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, prefix, "d3d9", Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);
                overrideCreated = true;
            }
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaPreset] = preset.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            staged.Download.RecordIn(metadata);
            var record = scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.DxvkHdr,
                BinaryDirectory = directory,
                SourceUrl = staged.Url,
                Version = staged.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = d3d9Override,
                Files =
                [
                    new LinuxDeployedFile { Path = d3d9, ExistedBeforeInstall = false, Sha256 = d3d9Sha },
                    new LinuxDeployedFile { Path = conf, ExistedBeforeInstall = false, Sha256 = confSha, AllowUserChanges = true }
                ],
                Metadata = metadata
            });
            // The commit point: DX9 HDR's record (stamped) and, when ReShade moved, ReShade's layer record in ONE write.
            _records.Commit(moved is null ? [record] : [record, moved.ReShadeRecord]);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            // Journaled, the rollback already undid these; unjournaled they go here (each only while it is still RHI's).
            if (overrideCreated) _overrides.TryRestore(prefix, d3d9Override);
            if (moved is not null) _reshade.UndoDxvkHdrMoveInside(game, moved);
            AppLog.Warn("dxvk-hdr", "DX9 HDR install failed; rolled back" + (rollback.Success ? "" : ": " + string.Join("; ", rollback.Failures)), ex);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        // The stamped record is saved: the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(InstalledPrefix + staged.Version + (moved is not null ? ReShadeMovedNote : ""), d3d9, conf);
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (_overrides.EditRefusal(game.PrefixPath) is { } running) return ComponentOperationResult.Fail(running);
        var d3d9 = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(D3d9FileName, StringComparison.OrdinalIgnoreCase));
        var conf = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(ConfFileName, StringComparison.OrdinalIgnoreCase));
        bool keepConf;
        try
        {
            if (d3d9 is not null && File.Exists(d3d9.Path)
                && !string.Equals(await FileHash.Sha256HexAsync(d3d9.Path, cancellationToken).ConfigureAwait(false), d3d9.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail(ModifiedFilePrefix + d3d9.Path);
            keepConf = conf is not null && File.Exists(conf.Path)
                       && !string.Equals(await FileHash.Sha256HexAsync(conf.Path, cancellationToken).ConfigureAwait(false), conf.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return ComponentOperationResult.Fail(RemovalFailedPrefix + ex.Message);
        }

        // ReShade on its Vulkan layer goes back to d3d9.dll (moved here by the install, or installed as a layer later).
        var reshade = _records.Find(game, ManagedComponentKind.ReShade);
        DxvkHdrReShadeBackPlan? backPlan = null;
        if (reshade is not null && ReShadeVulkanLayer.IsLayerRecord(reshade))
        {
            var (plan, refusal) = await _reshade.PrepareDxvkHdrMoveBackAsync(game, reshade, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) return ComponentOperationResult.Fail(refusal);
            backPlan = plan;
        }

        // Journaled removal (the record-removed commit probe): DX9 HDR's record is stamped with the removal's id first;
        // taking it out of the ledger (with ReShade's record back on d3d9.dll in the same write) is the commit point.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "DX9 HDR removal: " + game.Name, game, ManagedComponentKind.DxvkHdr, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        _ = OperationCancellation.BeginWrites(cancellationToken); // a removal runs to completion or full rollback
        var overrideRestores = new List<(WineOverrideRecord Record, WineOverrideRestoreResult Result)>();
        DxvkHdrReShadeBack? back = null;
        try
        {
            if (d3d9 is not null) await scope.DeleteAsync(d3d9.Path, CancellationToken.None).ConfigureAwait(false);
            if (conf is not null && !keepConf) await scope.DeleteAsync(conf.Path, CancellationToken.None).ConfigureAwait(false);
            if (backPlan is not null)
                back = await _reshade.MoveBackFromDxvkHdrInsideAsync(game, backPlan, record.WineOverride, scope, CancellationToken.None).ConfigureAwait(false);
            else if (record.WineOverride is { } d3d9Override && game.PrefixPath is { } prefix && Directory.Exists(prefix))
                overrideRestores.Add((d3d9Override, await _overrides.RestoreAsync(scope.Operation, prefix, d3d9Override, CancellationToken.None).ConfigureAwait(false)));
            _records.Commit(back is null ? [] : [back.ReShadeRecord], [(game, ManagedComponentKind.DxvkHdr)]);
        }
        catch (Exception ex)
        {
            // DX9 HDR's record (and ReShade's layer record, when the commit write took both) goes back before the files.
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "dxvk-hdr",
                alongside: back is not null && reshade is not null ? [reshade] : null).ConfigureAwait(false);
            foreach (var (overrideRecord, result) in overrideRestores.AsEnumerable().Reverse())
                if (result.Outcome == WineOverrideRestoreOutcome.Restored) _overrides.TryReapply(game.PrefixPath, overrideRecord);
            if (back is not null) _reshade.UndoDxvkHdrMoveBackInside(game, back);
            AppLog.Warn("dxvk-hdr", "DX9 HDR removal failed; rolled back" + (rollback.Success ? "" : ": " + string.Join("; ", rollback.Failures)), ex);
            return ComponentOperationResult.Fail(RemovalFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        // The record is gone: the commit point. Empty layer folders go only now (a rolled-back file needs its folder).
        await scope.CommitAsync().ConfigureAwait(false);
        if (back is not null) ReShadeDeploymentService.RemoveDxvkHdrLayerFolders(back);
        var notes = WineDllOverrideService.KeptValueNotes([.. overrideRestores.Select(item => item.Result),
            .. back is null ? [] : ReShadeDeploymentService.RestoreResults(back)]);
        return ComponentOperationResult.Ok(RemovedMessage + (back is not null ? ReShadeBackNote : "") + notes + (keepConf ? KeptConfPrefix + conf!.Path : ""),
            [.. record.Files.Select(file => file.Path)]);
    }

    /// <summary>Swaps d3d9.dll for the newest DXVK HDR build (dxvk.conf and the override stay as they are).</summary>
    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        var d3d9 = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(D3d9FileName, StringComparison.OrdinalIgnoreCase));
        if (d3d9 is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (UpdateRefusal(game, record) is { } reinstall) return ComponentOperationResult.Fail(reinstall); // before any download
        var architecture = GameArchitecture.Of(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64)) return ComponentOperationResult.Fail(BitnessUnknownMessage);
        if (_overrides.EditRefusal(game.PrefixPath) is { } running) return ComponentOperationResult.Fail(running);
        if (File.Exists(d3d9.Path) && !FileHash.HexEquals(FileHash.Sha256Hex(d3d9.Path), d3d9.Sha256 ?? ""))
            return ComponentOperationResult.Fail(ModifiedFilePrefix + d3d9.Path);

        Staged staged;
        try
        {
            staged = await StageAsync(architecture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsSourceFailure(ex))
        {
            AppLog.Warn("dxvk-hdr", "The DX9 HDR update download failed", ex);
            return new ComponentOperationResult(false, DownloadFailedPrefix + ex.Message, SourceUnavailable: ex is HttpRequestException or TaskCanceledException);
        }
        if (File.Exists(d3d9.Path) && FileHash.HexEquals(staged.D3d9Sha256, d3d9.Sha256 ?? ""))
        {
            // The same d3d9.dll under a newer tag (review nit 6): the record takes the tag, so the check stops offering
            // it. No file changes, so no operation: only the version, source and download lines move.
            if (!string.Equals(record.Version, staged.Version, StringComparison.Ordinal))
            {
                record.Version = staged.Version;
                record.SourceUrl = staged.Url;
                staged.Download.RecordIn(record.Metadata);
                _records.Upsert(record);
            }
            return ComponentOperationResult.Ok(AlreadyCurrentPrefix + staged.Version);
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "DX9 HDR update: " + game.Name, game, ManagedComponentKind.DxvkHdr, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            d3d9.Sha256 = await scope.PlaceCopyAsync(staged.D3d9Path, d3d9.Path, cancellationToken, staged.D3d9Sha256).ConfigureAwait(false);
            record.Version = staged.Version;
            record.SourceUrl = staged.Url;
            staged.Download.RecordIn(record.Metadata);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(UpdatedPrefix + staged.Version, d3d9.Path);
    }

    /// <summary>Update All's check: the newest release's tag against the installed one; offline is "could not check".</summary>
    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return new ManagedUpdateCheck(false, false, NotInstalledMessage);
        // B25: a deleted d3d9.dll or a game that no longer takes DX9 HDR is never "updated" back: a failure that says why.
        if (UpdateRefusal(game, record) is { } reinstall)
            return new ManagedUpdateCheck(true, false, reinstall, record.Version, Blocked: true);
        GitHubResolvedReleaseAsset release;
        try
        {
            release = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsSourceFailure(ex))
        {
            return new ManagedUpdateCheck(true, false, CouldNotCheckPrefix + ex.Message, record.Version, CouldNotCheck: true);
        }
        var newer = !string.Equals(release.Version, record.Version, StringComparison.Ordinal);
        return new ManagedUpdateCheck(true, newer, (newer ? UpdateAvailablePrefix : CurrentPrefix) + release.Version, record.Version, release.Version);
    }

    /// <summary>
    /// The ⚙ preset choice (upstream rewrites dxvk.conf when the preset changes): the conf is rewritten, journaled, while
    /// it still holds what RHI wrote; a conf the user changed is never replaced.
    /// </summary>
    public async Task<ComponentOperationResult> SetPresetAsync(DetectedGame game, int presetIndex, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(NotInstalledMessage);
        var preset = DxvkHdrPresets.For(presetIndex);
        var conf = record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(ConfFileName, StringComparison.OrdinalIgnoreCase));
        if (conf is null) return ComponentOperationResult.Fail(NotInstalledMessage);
        if (File.Exists(conf.Path) && !FileHash.HexEquals(FileHash.Sha256Hex(conf.Path), conf.Sha256 ?? ""))
            return ComponentOperationResult.Fail(ConfChangedPrefix + conf.Path);
        var bytes = Encoding.UTF8.GetBytes(DxvkHdrPresets.ConfFor(preset.Index));
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "DX9 HDR preset: " + game.Name, game, ManagedComponentKind.DxvkHdr, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            conf.Sha256 = await scope.PlaceBytesAsync(conf.Path, bytes, cancellationToken).ConfigureAwait(false);
            record.Metadata[MetaPreset] = preset.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(PresetSetMessage, conf.Path);
    }

    /// <summary>
    /// Coordinator decision 9: whether the game's launch command (detected, or typed in Edit game) sets Proton's
    /// <c>PROTON_USE_WINED3D</c> (or its older name <c>PROTON_USE_WINED3D11</c>) to a value Proton takes as on (not empty,
    /// not 0). Proton then sets <c>WINEDLLOVERRIDES=d3d9=b</c>, which beats the per-exe override on every runner
    /// (reviews/w7-dxvk-hdr-wine.md), so DX9 HDR's d3d9.dll is never loaded. Steam's own launch options are not read.
    /// </summary>
    /// <para>Re-review nit 5: per variable the last assignment in the command wins (<c>…=1 … PROTON_USE_WINED3D=0</c> is
    /// off), as the shell leaves it; Proton reads both names, so either one on counts.</para>
    internal static bool LaunchUsesWineD3d(DetectedGame game)
        => game.LaunchCommand is { } command
           && WineD3dSetting().Matches(command)
               .GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal)
               .Any(name => name.Last().Groups["value"].Value.Trim('"', '\'') is { Length: > 0 } value && value != "0");

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![A-Za-z0-9_])(?<name>PROTON_USE_WINED3D(?:11)?)=(?<value>""[^""]*""|'[^']*'|[^\s;&|]*)")]
    private static partial System.Text.RegularExpressions.Regex WineD3dSetting();

    /// <summary>
    /// Review minor 5: Luma Framework's or DLSS5 Feeder's DirectX 9 translation (dgVoodoo2 as d3d9.dll, ReShade moved to
    /// dxgi.dll) is on the game, read from the owner's own migration record; the refusal names the component to remove.
    /// </summary>
    private string? Dx9TranslationRefusal(DetectedGame game)
    {
        foreach (var (kind, key) in new[]
                 {
                     (ManagedComponentKind.LumaFramework, LumaFrameworkDeploymentService.MetaDx9Migration),
                     (ManagedComponentKind.Dlss5Feeder, Dlss5FeederFullDeploymentService.MetaDx9Migration)
                 })
            if (_records.Find(game, kind)?.Metadata.GetValueOrDefault(key) is { Length: > 0 })
                return Dx9TranslationPrefix + LinuxManagedComponentService.DisplayDependencyName(kind);
        return null;
    }

    /// <summary>Whether the record's d3d9.dll is gone from disk (the row then says Remove clears the rest).</summary>
    private static bool D3d9Missing(LinuxDeploymentRecord record)
        => record.Files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(D3d9FileName, StringComparison.OrdinalIgnoreCase)) is not { } d3d9
           || !File.Exists(d3d9.Path);

    /// <summary>The other RHI component recorded as owning <paramref name="path"/> (display name), or null.</summary>
    private string? HolderOf(DetectedGame game, string path)
        => _records.FindAll(game).FirstOrDefault(record => record.Component != ManagedComponentKind.DxvkHdr
               && record.Files.Any(file => string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(path), StringComparison.Ordinal))) is { } holder
            ? LinuxManagedComponentService.DisplayDependencyName(holder.Component)
            : null;

    private static bool IsSourceFailure(Exception ex)
        => ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or JsonException
            or UntrustedDownloadException or TaskCanceledException or UnauthorizedAccessException;

    private sealed record Staged(string D3d9Path, string D3d9Sha256, string Version, string Url, VerifiedDownload Download);

    private Task<GitHubResolvedReleaseAsset> ResolveLatestAsync(CancellationToken cancellationToken)
        => new GitHubReleaseAssetResolver(_http).ResolveAsync(new GitHubReleaseAssetQuery(ReleaseApiUrl, AssetNamePattern: AssetNamePattern), cancellationToken);

    /// <summary>Whether <paramref name="url"/> is an HTTPS asset of EndlesslyFlowering/dxvk's GitHub releases.</summary>
    internal static bool IsTrustedAssetUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal)
           && uri.AbsolutePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest release's archive through the stamped cache (GitHub's digest checked), and the game's bitness's
    /// d3d9.dll extracted by the host 7-Zip/bsdtar through <see cref="ExternalArchiveRunner"/> (time limit, link refusal),
    /// size-capped, checked to be a DXVK HDR PE of that bitness, and cached bound to the archive.
    /// </summary>
    private async Task<Staged> StageAsync(PeArchitecture architecture, CancellationToken cancellationToken)
    {
        var release = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
        if (!IsTrustedAssetUrl(release.AssetUrl)) throw new InvalidDataException(UntrustedAssetPrefix + release.AssetUrl);
        var folder = Path.Combine(_cacheDir, Safe(release.Version));
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, Safe(release.AssetName));
        var download = await TrustedDownload.EnsureVerifiedAsync(_http, release.AssetUrl, archive, DownloadSources.DxvkHdr, release.Version, release.Sha256,
            validate: (temp, _) => new FileInfo(temp).Length < 1024
                ? throw new InvalidDataException(PackageIncompletePrefix + Path.GetFileName(release.AssetUrl))
                : Task.CompletedTask,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var arch = architecture == PeArchitecture.X86 ? "x32" : "x64";
        var member = $"normal/{arch}/{D3d9FileName}";
        var d3d9 = Path.Combine(folder, arch, D3d9FileName);
        if (CacheStamp.IsValidBoundTo(d3d9, release.AssetUrl, release.Version, download.Sha256) && IsDxvkHdrPe(d3d9, architecture))
            return new Staged(d3d9, await FileHash.Sha256HexAsync(d3d9, cancellationToken).ConfigureAwait(false), release.Version, release.AssetUrl, download);

        CacheStamp.Invalidate(d3d9);
        var tool = ExternalArchiveRunner.FindTool() ?? throw new InvalidOperationException(ExternalArchiveRunner.NoToolPrefix + Path.GetFileName(archive));
        // The size cap holds before anything is extracted (review minor 4): the member's size as the archive lists it
        // (7-Zip and bsdtar write exactly that many bytes), and once more on what was written.
        var declared = await ListedSizeAsync(tool, archive, member, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException(PackageIncompletePrefix + member);
        if (declared > D3d9MaxBytes) throw new InvalidDataException(D3d9TooLargePrefix + member);
        var extract = Path.Combine(folder, "extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExternalArchiveRunner.ExtractAsync(archive, extract, cancellationToken, members: [member], tool: tool).ConfigureAwait(false);
            // 7-Zip extracts the member flat (e); bsdtar keeps its path.
            var found = new[] { Path.Combine(extract, D3d9FileName), Path.Combine(extract, "normal", arch, D3d9FileName) }.FirstOrDefault(File.Exists)
                        ?? throw new InvalidDataException(PackageIncompletePrefix + member);
            if (new FileInfo(found).Length > D3d9MaxBytes) throw new InvalidDataException(D3d9TooLargePrefix + member);
            if (!IsDxvkHdrPe(found, architecture)) throw new InvalidDataException(NotDxvkHdrPrefix + member);
            Directory.CreateDirectory(Path.GetDirectoryName(d3d9)!);
            var sha = AtomicFile.Copy(found, d3d9);
            CacheStamp.WriteBoundTo(d3d9, release.AssetUrl, release.Version, download.Sha256);
            return new Staged(d3d9, sha, release.Version, release.AssetUrl, download);
        }
        finally
        {
            if (Directory.Exists(extract))
            {
                FileCleanup.TryDeleteAll(SafeFileEnumeration.Files(extract, ignoreInaccessible: true).ToArray());
                FileCleanup.TryDeleteEmptyTree(extract);
            }
        }
    }

    private static bool IsDxvkHdrPe(string path, PeArchitecture architecture)
        => PeArchitectureDetector.Detect(path) == architecture && ComponentProbe.IsDxvkHdrBuild(path);

    /// <summary>
    /// The size <paramref name="member"/> has in <paramref name="archive"/> as the host tool lists it (7-Zip
    /// <c>l -slt</c>, bsdtar <c>-tvf</c>), through <see cref="ExternalProcess"/> (time limit, tree kill); null when the
    /// archive does not list it (or lists it without a size). A member listed twice is refused (re-review 1: the tool
    /// writes the last copy, so the first one's size would bound nothing).
    /// </summary>
    internal static async Task<long?> ListedSizeAsync(ExternalArchiveTool tool, string archive, string member, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(tool.Command) { WorkingDirectory = Path.GetDirectoryName(archive)! };
        foreach (var argument in tool.Kind == ExternalArchiveToolKind.SevenZip ? new[] { "l", "-slt", archive, member } : ["-tvf", archive, member])
            start.ArgumentList.Add(argument);
        var result = await ExternalProcess.RunAsync(start, ExternalArchiveRunner.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{ExternalArchiveRunner.FailedPrefix}{Path.GetFileName(tool.Command)} ({result.ExitCode}): {result.LastMeaningfulLine()}");
        var sizes = ListedSizes(tool.Kind, result.StandardOutput, member);
        return sizes.Count switch
        {
            0 => null,
            1 => sizes[0],
            _ => throw new InvalidDataException(DuplicateMemberPrefix + member),
        };
    }

    /// <summary>
    /// Every entry of <paramref name="member"/> in a host tool's listing, with its size (null for an entry the listing
    /// gives no size for). 7-Zip: the entries follow the "----------" line as "Key = value" blocks ending at a blank line
    /// (the archive's own block comes before it); bsdtar <c>-tv</c>: the size is the fifth field.
    /// </summary>
    internal static IReadOnlyList<long?> ListedSizes(ExternalArchiveToolKind kind, string output, string member)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries);
        var sizes = new List<long?>();
        if (kind == ExternalArchiveToolKind.SevenZip)
        {
            var entries = lines.SkipWhile(line => !line.StartsWith("----------", StringComparison.Ordinal)).ToArray();
            for (var i = 0; i < entries.Length; i++)
            {
                if (!entries[i].StartsWith("Path = ", StringComparison.Ordinal)
                    || !entries[i]["Path = ".Length..].Replace('\\', '/').Equals(member, StringComparison.Ordinal)) continue;
                long? size = null;
                for (var j = i + 1; j < entries.Length && entries[j].Length > 0; j++)
                    if (entries[j].StartsWith("Size = ", StringComparison.Ordinal))
                    {
                        size = long.TryParse(entries[j]["Size = ".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                        break;
                    }
                sizes.Add(size);
            }
            return sizes;
        }
        // bsdtar -tv: "-rwxr-xr-x  0 0      0     7311374 Sep  7 11:44 normal/x64/d3d9.dll" (size is the fifth field).
        foreach (var line in lines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 9 && fields[^1].Equals(member, StringComparison.Ordinal))
                sizes.Add(long.TryParse(fields[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : null);
        }
        return sizes;
    }

    private static string Safe(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
}
