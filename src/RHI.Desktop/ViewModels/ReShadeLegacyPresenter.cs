using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #186 (upstream "Legacy..." picker, <c>legacyReShadeAvailable</c>, <c>reshadeGameInfo</c>): the version list of the
/// ReShade profile's Legacy channel and the RHI manifest's ReShade note for the selected game. The list is the manifest's
/// (6.4.1 alone until it is read, or when it lists none); a version the profile shows that the list lacks (an installed
/// legacy ReShade, a manifest pin) is kept in it, so the dropdown's selection always has its option.
/// </summary>
public sealed partial class ReShadeLegacyPresenter : ObservableObject
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, ReShadeLegacyPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private IReadOnlyList<string> _listed = [UpstreamRhiManifestService.DefaultLegacyReShadeVersion];

    /// <summary>The presenter of <paramref name="shell"/> (one per shell; the shell's one-line hook).</summary>
    public static ReShadeLegacyPresenter For(MainWindowViewModel shell, LinuxManagedComponentService components)
        => Presenters.GetValue(shell, s => new ReShadeLegacyPresenter(s, components));

    private ReShadeLegacyPresenter(MainWindowViewModel shell, LinuxManagedComponentService components)
    {
        _shell = shell;
        _shell.PropertyChanged += OnShellChanged;
        Loaded = LoadAsync(components);
        UiExceptionGuard.Forget(Loaded, "reshade-legacy-versions");
    }

    /// <summary>The Legacy channel's versions, newest first as the manifest lists them.</summary>
    public ObservableCollection<string> Versions { get; } = [UpstreamRhiManifestService.DefaultLegacyReShadeVersion];

    /// <summary>The manifest list has been read (or the default kept); test seam.</summary>
    public Task Loaded { get; }

    /// <summary>The dropdown's 2 s tooltip.</summary>
    public string VersionTip => UiLocalizationService.Text("Rs.LegacyVersionTip");

    /// <summary>The RHI manifest's ReShade note for the selected game (empty: the row is hidden).</summary>
    public string Note => _shell.SelectedGame?.ManifestReShadeNote?.Trim() ?? "";
    public bool HasNote => Note.Length > 0;
    public bool HasNoteLink => HasNote && NoteUrl is not null;
    public string NoteLinkLabel => string.IsNullOrWhiteSpace(_shell.SelectedGame?.ManifestReShadeNoteUrlLabel)
        ? UiLocalizationService.Text("Card.OpenNotesLink")
        : _shell.SelectedGame!.ManifestReShadeNoteUrlLabel!.Trim();
    /// <summary>Only an https link is offered (manifest data).</summary>
    public string? NoteUrl => _shell.SelectedGame?.ManifestReShadeNoteUrl?.Trim() is { } url
                              && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;

    [RelayCommand]
    private void OpenNoteLink()
    {
        if (NoteUrl is not { } url) return;
        if (!LinuxDesktopOpener.TryOpenUri(url, out var error))
            _shell.StatusText = UiLocalizationService.Format("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }

    /// <summary>
    /// Makes sure <paramref name="version"/> is offered before the profile selects it (Avalonia drops a selection whose
    /// option is missing). Never removes the version the profile shows.
    /// </summary>
    public void Offer(string? version) => Sync(version);

    private async Task LoadAsync(LinuxManagedComponentService components)
    {
        var listed = await components.LegacyReShadeVersionsAsync();
        _listed = listed.Count > 0 ? listed : [UpstreamRhiManifestService.DefaultLegacyReShadeVersion];
        Sync(_shell.ReShadeLegacyVersion);
    }

    /// <summary>The list becomes the manifest's plus <paramref name="keep"/>, changed item by item (never cleared: a cleared
    /// list writes null into the bound selection).</summary>
    private void Sync(string? keep)
    {
        var wanted = _listed.ToList();
        if (!string.IsNullOrWhiteSpace(keep) && !wanted.Contains(keep.Trim(), StringComparer.Ordinal))
            wanted.Add(keep.Trim());
        for (var i = Versions.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Versions[i], StringComparer.Ordinal)) Versions.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = Versions.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at >= 0) Versions.Move(at, i);
            else Versions.Insert(i, wanted[i]);
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedGame)) return;
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(HasNoteLink));
        OnPropertyChanged(nameof(NoteLinkLabel));
        OnPropertyChanged(nameof(NoteUrl));
        OnPropertyChanged(nameof(VersionTip));
    }
}
