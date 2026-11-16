using System.Runtime.CompilerServices;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// A notice with Accept / Cancel and "Don't show this again" (#285): <paramref name="Key"/> is what a tick remembers,
/// <paramref name="LinkUrl"/> an optional http(s) page shown as a link button under the text.
/// </summary>
public sealed record DismissibleNotice(string Key, string Title, IReadOnlyList<string> Paragraphs, string AcceptText, string? LinkUrl = null);

/// <summary>The window's answer: Accept pressed, and whether "Don't show this again" was ticked (counted on Cancel too, as upstream).</summary>
public readonly record struct DismissibleNoticeAnswer(bool Accepted, bool DontShowAgain);

/// <summary>The closed-source mods RHI installs (#285; upstream 2.9.3).</summary>
public enum ClosedSourceMod { RtxEncore, Dlssg2030, AnrFg }

/// <summary>
/// w14/extras-fg (#285, #278): the install notices with "Don't show this again" — the closed-source notice before the
/// first install of RTX Encore, 20/30 FG Unlock or ANR FG (upstream <c>ShowClosedSourceNoticeAsync</c>), and the reusable
/// confirm behind it, which the Universal MFG ReShade warning uses too. A ticked box is remembered per notice in
/// <see cref="DismissedNoticeStore"/> even when the user cancels (upstream saves it either way); a dismissed notice is
/// never asked again. Without a window that can ask (tests and headless runs) every notice is accepted.
/// <para>Other lanes call <see cref="ConfirmClosedSourceAsync"/> (ANR FG, from OptiScaler's presets) and
/// <see cref="ConfirmAsync"/> for their own notices.</para>
/// </summary>
public sealed class InstallNoticePresenter
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, InstallNoticePresenter> Presenters = new();
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>ANR FG's GitHub page (it has no row of its own, so the notice links to it).</summary>
    public const string AnrFgRepositoryUrl = "https://github.com/apex-compute/ANR";

    private readonly MainWindowViewModel _shell;
    private readonly DismissedNoticeStore _store;

    private InstallNoticePresenter(MainWindowViewModel shell, DismissedNoticeStore store)
    {
        _shell = shell;
        _store = store;
    }

    /// <summary>The presenter of <paramref name="shell"/> (one per shell; the shell's one-line hook).</summary>
    public static InstallNoticePresenter For(MainWindowViewModel shell, Func<DismissedNoticeStore> store)
        => Presenters.GetValue(shell, s => new InstallNoticePresenter(s, store()));

    /// <summary>The store's key of a closed-source notice (upstream's settings <c>…ClosedSourceDismissed</c> names).</summary>
    public static string ClosedSourceKey(ClosedSourceMod mod) => mod switch
    {
        ClosedSourceMod.RtxEncore => "RtxEncoreClosedSourceDismissed",
        ClosedSourceMod.Dlssg2030 => "Dlssg2030ClosedSourceDismissed",
        _ => "AnrFgClosedSourceDismissed"
    };

    /// <summary>The mod's name, as the row shows it, and its author (upstream's arguments).</summary>
    public static (string Name, string Author) ClosedSourceIdentity(ClosedSourceMod mod) => mod switch
    {
        ClosedSourceMod.RtxEncore => ("RTX Encore", "SilyNoMeta"),
        ClosedSourceMod.Dlssg2030 => ("20/30 FG Unlock", "sdli1995"),
        _ => ("ANR FG", "Apex Compute")
    };

    /// <summary>
    /// The closed-source notice. Mods with a row point at the row's ⓘ (it opens their GitHub page); ANR FG, which has no
    /// row, carries its GitHub page as a link.
    /// </summary>
    public static DismissibleNotice ClosedSource(ClosedSourceMod mod)
    {
        var (name, author) = ClosedSourceIdentity(mod);
        var link = mod == ClosedSourceMod.AnrFg ? AnrFgRepositoryUrl : null;
        return new DismissibleNotice(
            ClosedSourceKey(mod),
            TF("Notice.ClosedSourceTitle", name),
            [TF("Notice.ClosedSourceWhat", name, author), TF(link is null ? "Notice.ClosedSourceRowInfo" : "Notice.ClosedSourceLink", author)],
            T("Vm.Install"),
            link);
    }

    /// <summary>Whether the notice <paramref name="key"/> was dismissed ("Don't show this again").</summary>
    public bool IsDismissed(string key) => _store.IsDismissed(key);

    /// <summary>
    /// Shows <paramref name="notice"/> unless it was dismissed. True: go on (accepted, dismissed earlier, or no window
    /// that can ask). A ticked "Don't show this again" is saved whatever the answer; if it cannot be saved the status line
    /// says so and the notice shows again next time.
    /// </summary>
    public async Task<bool> ConfirmAsync(DismissibleNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_shell.ConfirmDismissibleAsync is not { } ask || _store.IsDismissed(notice.Key)) return true;
        var answer = await ask(notice);
        if (answer.DontShowAgain)
        {
            try { _store.Dismiss(notice.Key); }
            catch (JsonStoreUnavailableException ex) { _shell.StatusText = TF("Notice.DismissNotSaved", ex.Message); }
            catch (IOException ex) { _shell.StatusText = TF("Notice.DismissNotSaved", ex.Message); }
            catch (UnauthorizedAccessException ex) { _shell.StatusText = TF("Notice.DismissNotSaved", ex.Message); }
        }
        return answer.Accepted;
    }

    /// <summary>The closed-source notice of <paramref name="mod"/> (see <see cref="ConfirmAsync"/>).</summary>
    public Task<bool> ConfirmClosedSourceAsync(ClosedSourceMod mod) => ConfirmAsync(ClosedSource(mod));

    /// <summary>
    /// The notices a user-started install of <paramref name="kind"/> shows first: the closed-source notice (RTX Encore,
    /// 20/30 FG Unlock). False when the user cancelled; the status line then says the install was cancelled.
    /// </summary>
    public async Task<bool> ConfirmBeforeInstallAsync(DetectedGame game, ManagedComponentKind kind, string displayName)
    {
        ArgumentNullException.ThrowIfNull(game);
        var go = kind switch
        {
            ManagedComponentKind.RtxEncore => await ConfirmClosedSourceAsync(ClosedSourceMod.RtxEncore),
            ManagedComponentKind.Dlssg2030Unlock => await ConfirmClosedSourceAsync(ClosedSourceMod.Dlssg2030),
            _ => true
        };
        if (!go) _shell.StatusText = TF("Vm.InstallationCancelled", displayName);
        return go;
    }
}
