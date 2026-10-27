using System.Text;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

// #234 (owner decision 2026-10-22 item 4): the read-only "Check Neural Rendering" page's findings. Behavioural port of
// wizardkof/RHI-Linux v0.9.25.0-beta.1's DLSS5 trial advisor (Dlss5TrialAdvisor.cs, after Mhsbrian/DLSS5oneclick-forlinux,
// MIT): its log signatures, anti-cheat list and recommended route. Not ported: its "Install recommended setup" button and its
// CUDA-fatbin model check. Structured findings only — the page words them; nothing here writes anything anywhere.

/// <summary>The graphics card as the page shows it (from nr-gpu's <c>NvidiaGpuProbe</c> facts, never re-read).</summary>
public sealed record NrCheckGpu(bool NvidiaPresent, string? Model, RtxGeneration Generation, string? DriverVersion);

/// <summary>Whether the NR DLL in the game is made for the card (#229's policy, by manifest label).</summary>
public enum NrDllFit
{
    /// <summary>No <c>nvngx_dlssnr.dll</c> in the game.</summary>
    NoDll,
    /// <summary>Made for this card (or its label names no generation, which fits every card).</summary>
    Fits,
    /// <summary>Its label names other generations only.</summary>
    NotForThisCard,
    /// <summary>RHI could not tell which manifest version the DLL is.</summary>
    VersionUnknown,
    /// <summary>RHI could not tell the card's generation.</summary>
    CardUnknown,
    /// <summary>
    /// The owner's DLL on disk is no longer the file RHI recorded (replaced or removed outside RHI): no version, no fit
    /// verdict — the same answer Update All gives (could not check; it leaves the file as it is).
    /// </summary>
    ChangedOutsideRhi
}

/// <summary>
/// The game's <c>nvngx_dlssnr.dll</c>: where it is, which RHI component owns it (none = the game's own or a hand-placed
/// file), its manifest version as nr-gpu's one helper tells it, its fit for the card, "Latest" made for the card (only when
/// the DLSS manifest is already loaded: the page never fetches anything) and a version picked by hand.
/// </summary>
public sealed record NrCheckDll(
    string? Path,
    ManagedComponentKind? Owner,
    string? OwnerName,
    string? Version,
    InstalledNrVersionSource VersionSource,
    NrDllFit Fit,
    string? LatestForCard,
    string? PickedVersion,
    bool FaultsOnThisDriver);

/// <summary>
/// The recommended Neural Rendering method (null while the game's analysis has not told whether it ships DLSS) and the
/// ones RHI installed in this game (display names, and their kinds in the same order).
/// </summary>
public sealed record NrCheckMethod(ManagedComponentKind? Recommended, string? RecommendedName, IReadOnlyList<string> InUse,
    IReadOnlyList<ManagedComponentKind> InUseKinds);

/// <summary>
/// What the main window's controls a fix hint can name do for THIS game, worked out by the rules those controls use, so a
/// hint never names a control that is disabled or does something else here (follow-up F2).
/// </summary>
/// <param name="DlssNrLineSwaps">The DLSS / Streamline section's Neural rendering line and its Apply are enabled
/// (<c>DlssPanelViewModel.CanSwapDlssNr</c>: a confirmed executable, an NR DLL no RHI component owns, and no ShortFuse /
/// DLSS5 Feeder Full stack locking the section).</param>
/// <param name="NrDllListSwaps">The NR DLL list under NR versions swaps the owner's DLL in place (a ShortFuse or DLSS5
/// Feeder Full stack); otherwise it only saves the choice.</param>
/// <param name="HeldByScaler">Methods/owners whose swap or removal RHI refuses while the NR Cost Scaler wraps their DLL.</param>
/// <param name="Protontricks">The D3DCompiler 47 line shows its Protontricks button for this game.</param>
/// <param name="Winetricks">The D3DCompiler 47 line shows its Winetricks button for this game.</param>
/// <param name="RemovableRows">The Managed components rows that show an enabled Remove for this game (the window's own rows of
/// the game's analysis); null while RHI has not analysed the game (then no row and no DLSS line is named).</param>
public sealed record NrCheckControls(bool DlssNrLineSwaps, bool NrDllListSwaps, IReadOnlyList<ManagedComponentKind> HeldByScaler,
    bool Protontricks, bool Winetricks, IReadOnlyList<ManagedComponentKind>? RemovableRows = null);

/// <summary>An anti-cheat folder or file found in the game (information only: RHI never blocks or changes anything for it).</summary>
public sealed record NrCheckAntiCheat(string Name, string Path);

/// <summary>A known error in a log, with what the page tells the user to do about it.</summary>
public enum NrLogSignature
{
    /// <summary>The RenoDX DLSS 5 add-on skipped Neural Rendering after a failed feature update.</summary>
    NeuralRenderingSkipped,
    /// <summary><c>0xBAD00001</c> FeatureNotSupported: the driver/runtime refused the NR feature.</summary>
    FeatureNotSupported,
    /// <summary>The HLSL compiler in the prefix cannot build the shaders (<c>E5017</c>/<c>E5005</c>/<c>X3506</c>).</summary>
    ShaderCompiler,
    /// <summary>An access violation (<c>0xC0000005</c>) or a lost/removed GPU device.</summary>
    CrashOrDeviceLost,
    /// <summary>
    /// #248: NGX answered <c>0xBAD00012</c> (NotImplemented) for feature 18, neural rendering: the driver/Proton stack does not
    /// offer it (DLSS Super Resolution still runs).
    /// </summary>
    NeuralRenderingNotImplemented,
    /// <summary>#248: the Feeder's depth probe saw a flat depth while the scene moved: ReShade's Generic Depth is on the wrong buffer.</summary>
    FlatDepth,
    /// <summary>
    /// #248: the Feeder stopped because the DLSS evaluate crashed right after the game's swapchain or ReShade's effect runtime
    /// changed (an HDR or resolution switch) — its own "stopped: the DLSS evaluate crashed" line in that context.
    /// </summary>
    EvaluateCrashAfterDisplayChange
}

/// <summary>The RHI control a fix hint names (the page words it; a test checks each control exists in the main window).</summary>
public enum NrFixHint
{
    /// <summary>Put the NR DLL made for this card in (the owner's NR DLL list, or the DLSS section's Neural rendering line).</summary>
    NrDllForCard,
    /// <summary>The D3DCompiler 47 line of the ReShade profile section (Protontricks / Winetricks / Browse).</summary>
    NativeD3DCompiler,
    /// <summary>Remove the Neural Rendering method on its Managed components row.</summary>
    RemoveNrMethod,
    /// <summary>#248: a plain statement — nothing to change in RHI or the game (driver/Proton support is needed).</summary>
    NothingToChangeInRhi,
    /// <summary>#248: a plain step in ReShade's in-game overlay (Add-ons tab → Generic Depth), not an RHI control.</summary>
    GenericDepthInReShade,
    /// <summary>#248: a plain step — restart the game with its final display mode (HDR, resolution) already set.</summary>
    RestartWithDisplayModeSet
}

/// <summary>One known error: the log, its line (null when only the end of a large log was read) and the matching text.</summary>
public sealed record NrLogFinding(string Log, int? Line, NrLogSignature Signature, NrFixHint Fix, string Excerpt);

/// <summary>How a log was read.</summary>
public enum NrLogState
{
    /// <summary>Not in the game folder.</summary>
    Missing,
    /// <summary>Read whole.</summary>
    Read,
    /// <summary>Larger than the cap: only its end was read (line numbers unknown).</summary>
    ReadEnd,
    /// <summary>Not a regular file (a FIFO, a device, a folder): never opened for reading.</summary>
    NotRegular,
    /// <summary>A link that leads outside the game and its prefix: not read.</summary>
    OutsideGame,
    /// <summary>Could not be opened or read.</summary>
    Unreadable
}

/// <summary>One log the page looked at.</summary>
public sealed record NrCheckLog(string Name, string Path, NrLogState State, IReadOnlyList<NrLogFinding> Findings);

/// <summary>Everything the Check Neural Rendering page shows, in its section order.</summary>
public sealed record NrCheckDiagnosis(
    NrCheckGpu Gpu,
    NrCheckDll Dll,
    NrCheckMethod Method,
    IReadOnlyList<NrCheckAntiCheat> AntiCheat,
    IReadOnlyList<NrCheckLog> Logs,
    NrCheckControls Controls)
{
    public IReadOnlyList<NrLogFinding> Findings => Logs.SelectMany(log => log.Findings).ToArray();
}

/// <summary>
/// #234: the one owner of the page's log-signature table and anti-cheat list, and of how logs are read: one non-blocking
/// open of a regular file (<see cref="FileKind.OpenRegularFileForReading"/>), never through a link that leaves the game or
/// its prefix, at most <see cref="MaxLogBytes"/> from its end; anything else is a "could not read" finding, never an
/// exception. Reads only.
/// </summary>
public static class NeuralRenderingDiagnosis
{
    /// <summary>The logs the page reads, beside the game's executable (where ReShade, OptiScaler and the Feeder write them).</summary>
    public static readonly IReadOnlyList<string> LogNames = ["ReShade.log", "OptiScaler.log", "dlss5-feed.log"];

    /// <summary>How much of a log is read: its last 2 MiB.</summary>
    public const long MaxLogBytes = 2L * 1024 * 1024;

    /// <summary>The longest excerpt kept from a matching line (tooltip only).</summary>
    public const int MaxExcerptChars = 240;

    // wizardkof's signatures (FeatureNotSupported, HLSL E5017/E5005/fastopt, 0xC0000005 / device lost, the RenoDX DLSS NR
    // skip), plus two the DLSS5 Feeder add-on itself names as going to ReShade.log ("error X3506: unrecognized compiler
    // target 'cs_5_1'" for a d3dcompiler too old for its neural pass; "the D3D12 device was removed"). Short codes match as
    // whole words, so a hex address that happens to contain them is no finding. #248 adds three lines the DLSS5 Feeder
    // writes to dlss5-feed.log (the owner's Borderlands 3): NotImplemented for feature 18 only (that code for another
    // feature says nothing about neural rendering); the depth probe's "FLAT while the scene moves" verdict only (its
    // "sampled depth is flat" line is a still scene or a menu); its "stopped: the DLSS evaluate crashed" line, a finding
    // only right after a change (Classify) — it then stands in for the generic crash row of the same crash. The table's
    // order is the page's order: the specific crash row before the generic one.
    private static readonly (NrLogSignature Signature, NrFixHint Fix, Regex Pattern)[] Signatures =
    [
        (NrLogSignature.NeuralRenderingSkipped, NrFixHint.NrDllForCard,
            new(@"NR skipped evaluation because feature update failed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (NrLogSignature.FeatureNotSupported, NrFixHint.NrDllForCard,
            new(@"\b0xBAD00001\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (NrLogSignature.NeuralRenderingNotImplemented, NrFixHint.NothingToChangeInRhi,
            new(@"^(?=.*(?:\bfeature 18\b|\bneural rendering\b))(?=.*\b0xBAD00012\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (NrLogSignature.ShaderCompiler, NrFixHint.NativeD3DCompiler,
            new(@"\bE5017\b|\bE5005\b|\bfastopt\b|\berror X3506\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (NrLogSignature.FlatDepth, NrFixHint.GenericDepthInReShade,
            new(@"\bDepth probe\b.*\bdepth is FLAT while the scene moves\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (NrLogSignature.EvaluateCrashAfterDisplayChange, NrFixHint.RestartWithDisplayModeSet,
            new(@"\bstopped: the DLSS evaluate crashed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (NrLogSignature.CrashOrDeviceLost, NrFixHint.RemoveNrMethod,
            new(@"\b0xC0000005\b|DEVICE_LOST|device lost|DEVICE_REMOVED|device was removed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    // #248: the Feeder's own lines that tell where a run is — a new run (the add-on attached), the DLSS feature ready, a
    // frame delivered, and a change of the game's picture once the feature was ready (the swapchain's colour space, ReShade's
    // effect runtime made again, the feature rebuilt for a new size or format).
    private static readonly Regex FeederRunStart = new(@"\bdlss5-feed\b.*\battached\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FeederFeatureReady = new(@"\[feed\] feature ready:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FeederFrameDelivered = new(@"\[feed\] frame \d+ delivered\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FeederDisplayChange = new(@"\[feed\] (?:swapchain colour space:|effect runtime \S+ initialised\b|building: \d+x\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Folders and files anti-cheat software puts in a game (wizardkof's list). Information only.</summary>
    public static readonly IReadOnlyList<string> AntiCheatFolders =
        ["EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "GameGuard", "XIGNCODE", "PunkBuster"];

    public static readonly IReadOnlyList<string> AntiCheatFiles =
        ["EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_Setup.exe", "BEService.exe", "BEService_x64.exe", "GameGuard.des", "x3.xem"];

    /// <summary>Test seam: runs right after a log was opened (swap a FIFO in to prove nothing reads past the open).</summary>
    internal static readonly AsyncLocal<Action<string>?> AfterOpenForTesting = new();

    /// <summary>The fix hint of <paramref name="signature"/>.</summary>
    public static NrFixHint FixFor(NrLogSignature signature) => Signatures.First(row => row.Signature == signature).Fix;

    /// <summary>
    /// The known errors in <paramref name="text"/>: the LAST line of each signature (the most recent run), in signature
    /// order. <paramref name="firstLine"/> is the number of the text's first line, or null when it is not known (only the
    /// end of a large log: the run's start was not read, so its feature counts as ready already).
    /// <para>#248: the Feeder's "stopped: the DLSS evaluate crashed" is a finding only after a change of the game's picture
    /// that no delivered frame has come through yet (since the feature was ready in that run); it then replaces the generic
    /// crash rows of that crash (those seen since the change). Without that context the generic row stays.</para>
    /// </summary>
    public static IReadOnlyList<NrLogFinding> Classify(string log, string? text, int? firstLine = 1)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var last = new Dictionary<NrLogSignature, NrLogFinding>();
        var ready = firstLine is null;
        var changed = false;
        var rebuilt = false;
        NrLogFinding? crashSinceChange = null;
        void SettleChange()
        {
            if (crashSinceChange is { } crash) last[NrLogSignature.CrashOrDeviceLost] = crash;
            changed = rebuilt = false;
            crashSinceChange = null;
        }
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (FeederRunStart.IsMatch(line)) { SettleChange(); ready = false; }
            else if (FeederFrameDelivered.IsMatch(line) && changed && rebuilt) SettleChange();
            else if (FeederDisplayChange.IsMatch(line) && ready) { changed = true; rebuilt = false; }
            else if (FeederFeatureReady.IsMatch(line)) { rebuilt = changed; ready = true; }
            foreach (var (signature, fix, pattern) in Signatures)
            {
                if (!pattern.IsMatch(line)) continue;
                var finding = new NrLogFinding(log, firstLine is { } first ? first + i : null, signature, fix, Excerpt(line));
                if (signature == NrLogSignature.EvaluateCrashAfterDisplayChange)
                {
                    if (!changed) continue;
                    crashSinceChange = null;
                }
                else if (signature == NrLogSignature.CrashOrDeviceLost && changed)
                {
                    crashSinceChange = finding;
                    continue;
                }
                last[signature] = finding;
            }
        }
        SettleChange();
        return Signatures.Select(row => row.Signature).Where(last.ContainsKey).Select(signature => last[signature]).ToArray();
    }

    // ANSI/VT colour and cursor sequences (ESC [ … final byte) a log may carry: dropped whole, never shown as "[31m".
    private static readonly Regex AnsiSequence = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B[@-_]", RegexOptions.CultureInvariant);

    // Tooltip text only: colour sequences dropped, control characters shown as a space, and invisible format characters
    // (bidi overrides such as U+202E, zero-width marks) dropped, so a log line can never reorder or hide the tooltip's text.
    private static string Excerpt(string line)
    {
        var trimmed = AnsiSequence.Replace(line, "").Trim();
        var clean = new StringBuilder(Math.Min(trimmed.Length, MaxExcerptChars));
        foreach (var ch in trimmed)
        {
            if (clean.Length >= MaxExcerptChars) { clean.Append('…'); break; }
            if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) continue;
            clean.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return clean.ToString();
    }

    /// <summary>
    /// Reads one log for known errors: only a regular file inside one of <paramref name="roots"/> (after every link on its
    /// path is followed), opened once without blocking, at most <see cref="MaxLogBytes"/> from its end. Never throws for a
    /// file problem: the state says what happened.
    /// </summary>
    public static NrCheckLog ReadLog(string name, string path, IReadOnlyList<string> roots)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null && !Directory.Exists(path)) return new(name, path, NrLogState.Missing, []);
            var final = PathIdentity.Canonicalize(path);
            if (!roots.Any(root => !string.IsNullOrWhiteSpace(root) && PathIdentity.IsSameOrInside(final, root)))
                return new(name, path, NrLogState.OutsideGame, []);
            using var handle = FileKind.OpenRegularFileForReading(final);
            if (handle is null)
                return new(name, path, FileKind.IsRegularFile(final) == false ? NrLogState.NotRegular : NrLogState.Unreadable, []);
            AfterOpenForTesting.Value?.Invoke(final);
            var length = RandomAccess.GetLength(handle);
            var start = Math.Max(0, length - MaxLogBytes);
            var buffer = new byte[(int)Math.Min(length - start, MaxLogBytes)];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(filled), start + filled);
                if (read <= 0) break;
                filled += read;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, filled).Replace("\r", "");
            if (start > 0)
            {
                // The first line read is most likely cut: start after it.
                var newline = text.IndexOf('\n');
                text = newline < 0 ? "" : text[(newline + 1)..];
                return new(name, path, NrLogState.ReadEnd, Classify(name, text, firstLine: null));
            }
            return new(name, path, NrLogState.Read, Classify(name, text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(name, path, NrLogState.Unreadable, []);
        }
    }

    /// <summary>The logs of <paramref name="game"/>, read under the game folder, its binary folder and its prefix.</summary>
    public static IReadOnlyList<NrCheckLog> ReadLogs(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var directory = LogDirectory(game);
        if (directory is null) return [];
        var roots = new[] { game.InstallPath, game.BinaryDirectory, game.PrefixPath }
            .Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => root!).ToArray();
        var logs = new List<NrCheckLog>();
        foreach (var name in LogNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logs.Add(ReadLog(name, Path.Combine(directory, name), roots));
        }
        return logs;
    }

    /// <summary>Where the logs live: the binary folder (the executable's folder when it is not known).</summary>
    public static string? LogDirectory(DetectedGame game)
    {
        var directory = game.BinaryDirectory;
        if (string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(game.ExecutablePath))
            directory = Path.GetDirectoryName(game.ExecutablePath);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    /// <summary>
    /// Anti-cheat folders and files at the top of the game folder and of its binary folder (information only). Looks at
    /// names only; nothing is opened.
    /// </summary>
    public static IReadOnlyList<NrCheckAntiCheat> FindAntiCheat(DetectedGame game)
    {
        var found = new List<NrCheckAntiCheat>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { game.InstallPath, game.BinaryDirectory })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var folder in AntiCheatFolders)
                {
                    var path = Path.Combine(root, folder);
                    if (Directory.Exists(path) && seen.Add(PathIdentity.Canonicalize(path))) found.Add(new(folder, path));
                }
                foreach (var file in AntiCheatFiles)
                {
                    var path = Path.Combine(root, file);
                    if (File.Exists(path) && seen.Add(PathIdentity.Canonicalize(path))) found.Add(new(file, path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A folder RHI cannot look into shows nothing; the page is information only.
            }
        }
        return found;
    }
}

/// <summary>#234: the Check Neural Rendering page's facts from RHI's own records and nr-gpu's card policy.</summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>
    /// Whether the page has anything to show for <paramref name="game"/> (owner hide rule): an NVIDIA card, a Windows game
    /// with a confirmed executable, and a Neural Rendering DLL in it (<paramref name="nrDllFound"/>: the game's analysis
    /// saw one) or a Neural Rendering method RHI installed there. Reads RHI's records only (no game file).
    /// </summary>
    public bool NeuralRenderingCheckApplies(DetectedGame game, bool nrDllFound)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!_nvidiaGpu().Present) return false;
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) || !game.HasConfirmedPatchTarget) return false;
        return nrDllFound || NeuralRenderingMethodsInUse(game).Count > 0;
    }

    /// <summary>The Neural Rendering methods RHI installed in <paramref name="game"/>, by display name.</summary>
    public IReadOnlyList<string> NeuralRenderingMethodsInUse(DetectedGame game)
        => NeuralRenderingMethodsInUseByKind(game).Select(method => method.Name).ToArray();

    private List<(ManagedComponentKind Kind, string Name)> NeuralRenderingMethodsInUseByKind(DetectedGame game)
    {
        var inUse = new List<(ManagedComponentKind, string)>();
        foreach (var kind in new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.Dlss5Dx11Bridge })
            if (_records.Find(game, kind) is not null) inUse.Add((kind, DisplayNeuralRenderingMethod(kind)));
        if (NeuralRenderingRuntimeOwnership.OptiScalerOwner(_records, game) is { } optiScaler) inUse.Add((ManagedComponentKind.OptiScaler, optiScaler));
        if (NeuralRenderingRuntimeOwnership.RtxEncoreOwner(_records, game) is { } encore) inUse.Add((ManagedComponentKind.RtxEncore, encore));
        return inUse;
    }

    /// <summary>
    /// #234: what the Check Neural Rendering page shows. Reads files only (RHI's records, the DLL's version resource and
    /// checksum, the game's logs, folder names) and never fetches the DLSS manifest: "Latest" made for the card is named
    /// only when the manifest is already loaded. <paramref name="hasNativeDlss"/>: the game's analysis' answer (null = not
    /// known yet: no recommendation). <paramref name="nrDllPaths"/>: the NR DLLs the game's analysis found (null = not known).
    /// </summary>
    public async Task<NrCheckDiagnosis> DiagnoseNeuralRenderingAsync(DetectedGame game, bool? hasNativeDlss, IReadOnlyList<string>? nrDllPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var facts = _nvidiaGpu();
        var gpu = new NrCheckGpu(facts.Present, facts.Model, facts.Generation, facts.DriverVersion);
        cancellationToken.ThrowIfCancellationRequested();
        var dll = await DiagnoseNeuralRenderingDllAsync(game, facts, nrDllPaths, cancellationToken).ConfigureAwait(false);
        ManagedComponentKind? recommended = hasNativeDlss is { } native ? RecommendedNeuralRenderingMethod(game, native) : null;
        var inUse = NeuralRenderingMethodsInUseByKind(game);
        var method = new NrCheckMethod(recommended, recommended is { } kind ? DisplayNeuralRenderingMethod(kind) : null,
            inUse.Select(m => m.Name).ToArray(), inUse.Select(m => m.Kind).ToArray());
        var controls = NeuralRenderingFixControls(game, method, nrDllPaths);
        cancellationToken.ThrowIfCancellationRequested();
        var antiCheat = NeuralRenderingDiagnosis.FindAntiCheat(game);
        var logs = NeuralRenderingDiagnosis.ReadLogs(game, cancellationToken);
        return new NrCheckDiagnosis(gpu, dll, method, antiCheat, logs, controls);
    }

    // Follow-up F2: what each control a fix hint can name does for this game, by the rules the controls themselves use
    // (tests compare each one with the live control's state for the same game).
    private NrCheckControls NeuralRenderingFixControls(DetectedGame game, NrCheckMethod method, IReadOnlyList<string>? nrDllPaths)
    {
        var confirmed = game.HasConfirmedPatchTarget;
        // DlssPanelViewModel.OrchestratedDlssStackManaged (SelectionFacts reads these two): it locks the DLSS / Streamline
        // section, and it is when the NR DLL list swaps in place (ExtrasViewModel) instead of only saving the choice.
        var orchestrated = IsShortFuseManaged(game) || IsDlss5FeederFullManaged(game);
        // The DLSS line lists the analysis' NR DLLs (RHI-owned files left out); before an analysis it is not known (false:
        // the page then names no DLSS line).
        var detected = nrDllPaths is { Count: > 0 };
        var held = method.InUseKinds.Where(kind => NrCostScalerService.OwnerChangeRefusal(_records, game, kind) is not null).ToArray();
        return new NrCheckControls(
            DlssNrLineSwaps: confirmed && detected && !orchestrated,
            NrDllListSwaps: orchestrated,
            HeldByScaler: held,
            Protontricks: confirmed && game.UsesProton,
            Winetricks: confirmed && D3DCompilerToolInstaller.CanInstallWithWinetricks(game));
    }

    private async Task<NrCheckDll> DiagnoseNeuralRenderingDllAsync(DetectedGame game, Hdr.NvidiaGpuFacts facts, IReadOnlyList<string>? nrDllPaths,
        CancellationToken cancellationToken)
    {
        var card = CardGeneration;
        var latest = card != RtxGeneration.Unknown && NrDlss is { Manifest: not null } dlss
            ? dlss.LatestVersion(DlssComponentKind.NeuralRendering)
            : null;
        var ownerRecord = NeuralRenderingRuntimeOwnership.OwnerRecordOtherThan(_records, game, ManagedComponentKind.NrCostScaler);
        (string Name, LinuxDeploymentRecord Record)? owner = null;
        string? path;
        InstalledNrVersion installed;
        var changedOutside = false;
        if (ownerRecord is { } held && ManagedFileSwap.FindFile(held.Record, NeuralRenderingRuntimeOwnership.DllName) is { } file)
        {
            owner = held;
            path = file.Path;
            // Update All's own rule (one helper): the version named from the record counts only while the file on disk
            // (behind the NR Cost Scaler: its real DLL) still has the record's checksum; otherwise could not check.
            changedOutside = !await OwnerFileMatchesRecordAsync(game, held.Record, cancellationToken).ConfigureAwait(false);
            installed = changedOutside ? new(null, InstalledNrVersionSource.Unknown) : InstalledNeuralRenderingVersion(game, held.Record);
        }
        else
        {
            // Follow-up F3: an owner whose record lists no NR DLL did not put this one in (the game's own, or by hand).
            path = GameNeuralRenderingDll(game, nrDllPaths);
            installed = path is null
                ? new(null, InstalledNrVersionSource.Unknown)
                // The game's own (or a hand-placed) DLL: the same helper over a record that only names the file (no
                // metadata, no hash), so its PE version is mapped to exactly one manifest entry, as Update All would.
                : InstalledNeuralRenderingVersion(game, new LinuxDeploymentRecord
                {
                    Component = ManagedComponentKind.Dlss,
                    Files = [new LinuxDeployedFile { Path = path }]
                });
        }

        var fit = path is null ? NrDllFit.NoDll
            : changedOutside ? NrDllFit.ChangedOutsideRhi
            : installed.Version is null ? NrDllFit.VersionUnknown
            : card == RtxGeneration.Unknown ? NrDllFit.CardUnknown
            : NeuralRenderingGpuPolicy.MadeForAnotherGeneration(installed.Version, card) ? NrDllFit.NotForThisCard
            : NrDllFit.Fits;
        var picked = ownerRecord?.Record.Component is ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder
            ? _neuralVersionPins.GetNeuralRenderingDllVersion(game)
            : null;
        var faults = installed.Version is { } version && OptiScalerF5DriverPolicy.IsFaultingNrDll(version)
                     && OptiScalerF5DriverPolicy.IsR615OrNewer(facts.DriverVersion);
        return new NrCheckDll(path, owner?.Record.Component, owner?.Name, installed.Version, installed.Source, fit, latest,
            string.IsNullOrWhiteSpace(picked) ? null : picked, faults);
    }

    // The NR DLL the analysis found (the binary folder's first), else the one beside the executable; only a regular file
    // counts (its version resource is read next).
    private static string? GameNeuralRenderingDll(DetectedGame game, IReadOnlyList<string>? nrDllPaths)
    {
        var directory = NeuralRenderingDiagnosis.LogDirectory(game);
        var candidates = (nrDllPaths ?? [])
            .OrderBy(path => directory is not null && string.Equals(Path.GetDirectoryName(path), directory, StringComparison.Ordinal) ? 0 : 1)
            .ToList();
        if (directory is not null) candidates.Add(Path.Combine(directory, NeuralRenderingRuntimeOwnership.DllName));
        return candidates.FirstOrDefault(path => FileKind.IsRegularFile(path) == true);
    }
}
