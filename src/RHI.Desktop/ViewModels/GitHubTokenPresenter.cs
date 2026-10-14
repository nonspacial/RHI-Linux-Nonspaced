using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Network;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Settings → GitHub token (#163): paste a token and save it once GitHub accepts it, check the saved one, clear it.
/// The status line never contains the token; the pasted text is the view's to clear once a save succeeded
/// (<see cref="SaveAsync"/> returns true).
/// </summary>
public sealed partial class GitHubTokenPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly GitHubTokenSettingsService _service;

    public GitHubTokenPresenter(GitHubTokenSettingsService service)
    {
        _service = service;
        _hasSavedToken = service.HasSavedToken;
        _statusText = SavedStateText(_hasSavedToken);
    }

    [ObservableProperty] private bool _hasSavedToken;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText;

    public string TokenPath => _service.TokenPath;

    /// <summary>Checks <paramref name="pasted"/> with GitHub and saves it when accepted; true when it was saved.</summary>
    public async Task<bool> SaveAsync(string? pasted)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusText = T("Parity.GitHubTokenChecking");
        try
        {
            var status = await _service.SaveAsync(pasted);
            HasSavedToken = _service.HasSavedToken;
            StatusText = status.Result == GitHubTokenCheck.Valid
                ? Prefix("Parity.GitHubTokenSaved", status)
                : FailureText(status, saving: true);
            return status.Result == GitHubTokenCheck.Valid;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Checks the saved token with GitHub.</summary>
    public async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = T("Parity.GitHubTokenChecking");
        try
        {
            var status = await _service.CheckSavedAsync();
            HasSavedToken = _service.HasSavedToken;
            StatusText = status.Result == GitHubTokenCheck.Valid
                ? Prefix("Parity.GitHubTokenSavedWorks", status)
                : FailureText(status, saving: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes the saved token.</summary>
    public void Clear()
    {
        if (IsBusy) return;
        try
        {
            _service.Clear();
            StatusText = T("Parity.GitHubTokenCleared");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("github-token", "Could not delete the GitHub token file", ex);
            StatusText = TF("Parity.GitHubTokenNotCleared", UiLocalizationService.Present(ex.Message));
        }
        HasSavedToken = _service.HasSavedToken;
    }

    private static string SavedStateText(bool saved) => T(saved ? "Parity.GitHubTokenIsSaved" : "Parity.GitHubTokenNoneSaved");

    private static string Prefix(string key, GitHubTokenStatus status)
        => status is { Limit: int limit, Remaining: int remaining }
            ? T(key) + " " + TF("Parity.GitHubTokenLimit", remaining, limit)
            : T(key);

    private static string FailureText(GitHubTokenStatus status, bool saving) => status.Result switch
    {
        GitHubTokenCheck.Rejected => T(saving ? "Parity.GitHubTokenRejectedNotSaved" : "Parity.GitHubTokenSavedRejected"),
        GitHubTokenCheck.Malformed => T(saving ? "Parity.GitHubTokenMalformed" : "Parity.GitHubTokenSavedMalformed"),
        GitHubTokenCheck.NoToken => T("Parity.GitHubTokenNoneSaved"),
        GitHubTokenCheck.FileExposed => T("Vm.GithubApiTxtCanReadBy"),
        GitHubTokenCheck.SaveFailed => T("Parity.GitHubTokenSaveFailed"),
        _ => T(saving ? "Parity.GitHubTokenCouldNotCheckNotSaved" : "Parity.GitHubTokenCouldNotCheck")
    };
}
