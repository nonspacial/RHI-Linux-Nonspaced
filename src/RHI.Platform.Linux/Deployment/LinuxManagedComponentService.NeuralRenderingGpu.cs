using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Hdr;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w11/nr-gpu: the host's NVIDIA facts every Neural Rendering decision reads (#229 card generation, #231 driver
/// version) — one seam, set once by the composition (the app: <see cref="NvidiaGpuProbe.Read()"/>, read-only /proc;
/// tests and the harness: a fake). Setting it also points the DLSS service's NR lists and "Latest" at the same card, so
/// every consumer follows one answer. #230: Update All moves "Latest" NR installs of ShortFuse and DLSS5 Feeder Full onto
/// the DLL made for the card; a hand-picked version stays, with a row note.
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    private Func<NvidiaGpuFacts> _nvidiaGpu = static () => NvidiaGpuFacts.None;

    /// <summary>
    /// The NVIDIA facts (#229/#231). Default (never set): no NVIDIA facts — unknown card, no driver — so a service built
    /// without the composition never reads the real /proc and never hides or moves anything because of the card.
    /// </summary>
    public Func<NvidiaGpuFacts> NvidiaGpu
    {
        get => _nvidiaGpu;
        set
        {
            var facts = value ?? (static () => NvidiaGpuFacts.None);
            _nvidiaGpu = facts;
            if (NrDlss is { } dlss) dlss.CardGeneration = () => facts().Generation;
        }
    }

    /// <summary>#229: the card generation NR lists and "Latest" follow (unknown = every entry, as upstream).</summary>
    public RtxGeneration CardGeneration => NrDlss is { } dlss ? dlss.CardGeneration() : _nvidiaGpu().Generation;

    // The one DLSS service every NR owner stages from (the app and the fixtures build one).
    private DlssManagerService? NrDlss => _shortFuse?.Dlss ?? _dlss5Feeder?.Dlss;

    // ---- #230: Update All moves "Latest" NR installs --------------------------------------------------------------

    /// <summary>What Update All does with an owner's NR DLL.</summary>
    internal enum NrMoveKind
    {
        /// <summary>Nothing to judge: no NR DLL owned, a legacy add-on-only Feeder, a hand-picked pin, or an unknown card.</summary>
        NotApplicable,
        /// <summary>The installed DLL is "Latest" made for this card.</summary>
        Current,
        /// <summary>A "Latest" install on another DLL: Update All swaps it to <see cref="NrMovePlan.To"/>.</summary>
        Move,
        /// <summary>RHI cannot tell which version is installed (or there is no manifest): could not check, never a blind swap.</summary>
        Unknown,
        /// <summary>A move was due but the file on disk is no longer the one RHI recorded (the user replaced or removed it): could not check.</summary>
        ChangedOutsideRhi
    }

    internal sealed record NrMovePlan(NrMoveKind Kind, string? From = null, string? To = null);

    /// <summary>#230: fixed text of the update check's NR part; the versions follow (from → to).</summary>
    public const string NrMovePrefix = "The Neural Rendering DLL follows \"Latest\" and is not the one made for this graphics card; Update All moves it: ";

    /// <summary>#230: fixed text when the installed NR DLL cannot be told (never "current", never a blind swap).</summary>
    public const string NrUnknownMessage = "RHI could not tell which Neural Rendering DLL is installed, so it was not checked against the one made for this graphics card.";

    /// <summary>#230 (review finding 1): fixed text when the NR DLL on disk is no longer the one RHI installed (never moved, never a failure).</summary>
    public const string NrChangedOutsideMessage = "The Neural Rendering DLL was changed or removed outside RHI, so it was not checked against the one made for this graphics card; Update All leaves it as it is.";

    /// <summary>#230: the row note of a hand-picked NR version made for another generation; the version follows.</summary>
    public const string NrPinOtherGenerationPrefix = "The Neural Rendering DLL picked for this game is made for another graphics card generation; Update All leaves it as picked: ";

    internal async Task<NrMovePlan> PlanNeuralRenderingMoveAsync(DetectedGame game, ManagedComponentKind owner, CancellationToken cancellationToken)
    {
        if (owner is not (ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder) || NrDlss is not { } dlss)
            return new(NrMoveKind.NotApplicable);
        var record = _records.Find(game, owner);
        if (record is null || ManagedFileSwap.FindFile(record, NeuralRenderingDllSwap.DllName) is null
            || (owner == ManagedComponentKind.Dlss5Feeder && !IsDlss5FeederFullManaged(game)))
            return new(NrMoveKind.NotApplicable);
        // A version the user picked by hand is never changed behind their back (its row says so when it is for another card).
        if (!string.IsNullOrWhiteSpace(_neuralVersionPins.GetNeuralRenderingDllVersion(game))) return new(NrMoveKind.NotApplicable);
        // Unknown card: nothing moves because of the card (today's behaviour).
        var card = dlss.CardGeneration();
        if (card == RtxGeneration.Unknown) return new(NrMoveKind.NotApplicable);
        try
        {
            await dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("nr-gpu", $"The DLSS manifest could not be read to check {game.Name}'s Neural Rendering DLL", ex);
            return new(NrMoveKind.Unknown);
        }
        var latest = dlss.LatestVersion(DlssComponentKind.NeuralRendering);
        if (latest is null) return new(NrMoveKind.Unknown);
        var installed = InstalledNeuralRenderingVersion(game, record);
        if (!installed.Known) return new(NrMoveKind.Unknown);
        if (string.Equals(installed.Version, latest, StringComparison.OrdinalIgnoreCase))
            return new(NrMoveKind.Current, installed.Version, latest);
        // The version above comes from the RECORD (its metadata or its hash vs the cache stamps): before a move, check the
        // file on disk still is that file. One the user replaced (or removed) would only be refused by the swap ("modified
        // outside RHI") and fail that game on every Update All, so it is "could not check" instead (review finding 1).
        // Hashed only here, when a move is planned, never on every check — and not while the NR Cost Scaler wraps the
        // runtime: the move is then held, never attempted, and the check says so (HeldByLayer).
        if (NrCostScalerService.OwnerChangeRefusal(_records, game, owner) is null
            && !await OwnerFileMatchesRecordAsync(game, record, cancellationToken).ConfigureAwait(false))
            return new(NrMoveKind.ChangedOutsideRhi, installed.Version, latest);
        return new(NrMoveKind.Move, installed.Version, latest);
    }

    // The owner's NR content (behind the NR Cost Scaler: nvngx_dlssnr_real.dll) still has the record's SHA-256. A record
    // without a hash cannot show it is unchanged: not moved (safe side; RHI writes a hash for every file it places).
    private async Task<bool> OwnerFileMatchesRecordAsync(DetectedGame game, LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var file = ManagedFileSwap.FindFile(record, NeuralRenderingDllSwap.DllName)!;
        var content = NrCostScalerService.ContentPath(record, file, _records.FindAll(game));
        try
        {
            if (!File.Exists(content)) return false;
            var current = await FileHash.Sha256HexAsync(content, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
            AppLog.Warn("nr-gpu", $"{game.Name}'s Neural Rendering DLL no longer matches RHI's record (changed outside RHI); it is not moved: {content}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("nr-gpu", $"{game.Name}'s Neural Rendering DLL could not be read to check it before a move", ex);
            return false;
        }
    }

    /// <summary>#230 (one helper, #234 reuses it): the manifest version of the owner's installed NR DLL.</summary>
    public InstalledNrVersion InstalledNeuralRenderingVersion(DetectedGame game, LinuxDeploymentRecord record)
        => NrDlss is { } dlss
            ? NeuralRenderingInstalledVersion.Identify(record, dlss.PublishedVersions(DlssComponentKind.NeuralRendering),
                dlss.CacheFolder(DlssComponentKind.NeuralRendering), _records.FindAll(game))
            : new(null, InstalledNrVersionSource.Unknown);

    /// <summary>
    /// The ShortFuse / DLSS5 Feeder update check with its NR DLL part: the add-on part as before; a "Latest" install on
    /// another DLL than "Latest" made for this card is an update; an NR DLL RHI cannot tell, or one changed outside RHI, is "could not check" unless
    /// the add-on itself has an update.
    /// </summary>
    private async Task<ManagedUpdateCheck> WithNeuralRenderingMoveAsync(DetectedGame game, ManagedComponentKind owner, ManagedUpdateCheck addon,
        CancellationToken cancellationToken)
    {
        if (!addon.Managed) return addon;
        var plan = await PlanNeuralRenderingMoveAsync(game, owner, cancellationToken).ConfigureAwait(false);
        return plan.Kind switch
        {
            NrMoveKind.Move => addon with
            {
                UpdateAvailable = true,
                CouldNotCheck = false,
                Message = Join(addon.UpdateAvailable ? addon.Message : "", NrMovePrefix + $"{plan.From} → {plan.To}")
            },
            NrMoveKind.Unknown when !addon.UpdateAvailable => addon with { CouldNotCheck = true, Message = Join(addon.Message, NrUnknownMessage) },
            NrMoveKind.ChangedOutsideRhi when !addon.UpdateAvailable => addon with { CouldNotCheck = true, Message = Join(addon.Message, NrChangedOutsideMessage) },
            _ => addon
        };
    }

    private static string Join(string first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : first.TrimEnd() + " " + second;

    /// <summary>
    /// The ShortFuse / DLSS5 Feeder update (Update All, manual and background): the add-on as before, and a "Latest" NR
    /// install moved onto "Latest" made for this card through the owner's journaled in-place swap (logged in the update
    /// history like a hand-made NR swap). Every download happens before the first write: the NR DLL is staged first, the
    /// add-on swap downloads before it writes, and the NR swap then finds the staged copy.
    /// </summary>
    private async Task<ComponentOperationResult> UpdateNeuralRenderingOwnerAsync(DetectedGame game, ManagedComponentKind owner,
        CancellationToken cancellationToken)
    {
        var plan = await PlanNeuralRenderingMoveAsync(game, owner, cancellationToken).ConfigureAwait(false);
        if (plan.Kind != NrMoveKind.Move) return await SwapNeuralAddonVersionCoreAsync(game, owner, cancellationToken).ConfigureAwait(false);

        try
        {
            await NrDlss!.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, plan.To, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(NrStageFailedPrefix + ex.Message);
        }

        var pin = _neuralVersionPins.Get(game, owner);
        var addonCheck = owner == ManagedComponentKind.ShortFuse
            ? await _shortFuse.CheckManagedUpdateAsync(game, pin, cancellationToken).ConfigureAwait(false)
            : await _dlss5Feeder.CheckUpdateAsync(game, pin, cancellationToken).ConfigureAwait(false);
        ComponentOperationResult? addon = null;
        if (addonCheck.UpdateAvailable)
        {
            addon = await SwapNeuralAddonVersionCoreAsync(game, owner, cancellationToken).ConfigureAwait(false);
            if (!addon.Success) return addon;
        }

        // Once the add-on swap has written, this unit runs to the end: the NR swap never sees a Cancel pressed after it
        // (its DLL is staged above, so nothing is downloaded from here on).
        var nrToken = addon is null ? cancellationToken : CancellationToken.None;
        var nr = await TrackNeuralRenderingDll(game, owner, () => owner == ManagedComponentKind.ShortFuse
            ? _shortFuse.SwapNeuralRenderingDllAsync(game, plan.To, nrToken)
            : _dlss5Feeder.SwapNeuralRenderingDllAsync(game, plan.To, nrToken)).ConfigureAwait(false);
        if (addon is null) return nr;
        // A landed line keeps its note first (LandedLine reads it from the start of the message).
        return nr with
        {
            Message = nr.Landed ? Join(nr.Message, addon.Message) : Join(addon.Message, nr.Message),
            Landed = nr.Landed || addon.Landed
        };
    }

    /// <summary>#230: the NR DLL could not be downloaded before the move; nothing was changed. The reason follows.</summary>
    public const string NrStageFailedPrefix = "The Neural Rendering DLL made for this graphics card could not be downloaded, so nothing was changed: ";

    /// <summary>The component rows' Neural Rendering notes (w11/nr-gpu), added after every other note.</summary>
    private void DecorateNeuralRenderingRows(DetectedGame game, IReadOnlyList<ManagedComponent> components)
        => _rows.DecorateNeuralRendering(game, components, _neuralVersionPins.GetNeuralRenderingDllVersion(game), CardGeneration, _nvidiaGpu().DriverVersion,
            record => InstalledNeuralRenderingVersion(game, record).Version);

    // ---- #231: OptiScaler F5 DLSSNR driver advice (warn and confirm, never refuse) ----------------------------------

    /// <summary>
    /// #231: the NR DLL F5 will load — "Latest" made for this card, the one its install stages: the cached DLL's PE version
    /// when RHI has it, else the manifest label. Null when RHI cannot tell (no manifest). Never downloads the DLL.
    /// </summary>
    public async Task<string?> F5NeuralRenderingDllVersionAsync(CancellationToken cancellationToken = default)
    {
        if (NrDlss is not { } dlss) return null;
        try
        {
            if (dlss.Manifest is null) await dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("nr-gpu", "The DLSS manifest could not be read for the F5 DLSSNR driver check", ex);
            return null;
        }
        var latest = dlss.LatestVersion(DlssComponentKind.NeuralRendering);
        if (latest is null) return null;
        return dlss.CachedDllPath(DlssComponentKind.NeuralRendering, latest) is { } cached && DlssAutoFollowService.PeFileVersion(cached) is { } pe
            ? pe
            : latest;
    }

    /// <summary>#231: what to tell the user before an F5 install on this host (driver below 615.71.09 / unreadable, a 310.8.0.x NR DLL on R615+).</summary>
    public async Task<F5DriverAdvice> F5DriverAdviceAsync(CancellationToken cancellationToken = default)
        => OptiScalerF5DriverPolicy.Advise(_nvidiaGpu().DriverVersion, await F5NeuralRenderingDllVersionAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>#231: the driver advice without the NR DLL part (Update All's line: no manifest read, no I/O beyond /proc).</summary>
    public F5DriverAdvice F5DriverAdvice() => OptiScalerF5DriverPolicy.Advise(_nvidiaGpu().DriverVersion, null);

    /// <summary>#231: the game's RHI OptiScaler is the F5 DLSSNR channel.</summary>
    public bool HasOptiScalerF5(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler) is { } record
           && OptiScalerDeploymentService.GetInstalledVariant(record) == OptiScalerVariant.F5;
}
