using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Settings → Accounts &amp; updates → Nexus Mods (#99; upstream SettingsHandler Nexus section): connect with an API key
/// pasted from the Nexus account page or through the Nexus login (SSO) window, see who is connected, disconnect, and
/// make RHI the handler for "Mod Manager Download" links. The key lives in a 0600 file (<see cref="SecretFileStore"/>),
/// is checked with Nexus before it is saved and is never shown.
/// </summary>
public sealed partial class NexusAccountPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    public static string DefaultKeyPath => Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "nexus_api.txt");

    private readonly SecretFileStore _key;
    private readonly NexusApiClient _api;
    private readonly AccountsPreferenceStore _preferences;
    private readonly NxmHandlerRegistration _handler;
    private readonly Func<NexusSsoClient> _sso;
    private readonly Func<string, (bool Opened, string? Error)> _openLink;

    public NexusAccountPresenter(SecretFileStore key, NexusApiClient api, AccountsPreferenceStore preferences, NxmHandlerRegistration handler,
        Func<NexusSsoClient>? sso = null, string? ssoSlug = null, Func<string, (bool Opened, string? Error)>? openLink = null)
    {
        _key = key;
        _api = api;
        _preferences = preferences;
        _handler = handler;
        _sso = sso ?? (() => new NexusSsoClient());
        SsoSlug = ssoSlug;
        _openLink = openLink ?? (url => (LinuxDesktopOpener.TryOpenUri(url, out var error), error));
        _statusText = "";
        _accountText = "";
        _handlerText = T("Parity.NexusHandlerUnknown");
        SyncAccount();
    }

    /// <summary>Nexus's registration of this application for SSO; null hides the login button (API key only).</summary>
    public string? SsoSlug { get; }
    public bool SsoAvailable => SsoSlug is not null;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _accountText;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _handlerText;
    [ObservableProperty] private bool _handlerIsRhi;

    /// <summary>The saved key for an API call, or null (status says why) when none is usable.</summary>
    public string? UsableKey()
    {
        var read = _key.Read();
        return read.State == SecretFileState.Present ? read.Value : null;
    }

    public bool IsPremium => _preferences.Load().NexusIsPremium;

    /// <summary>Checks <paramref name="pasted"/> with Nexus and saves it when Nexus knows it; true when saved.</summary>
    public async Task<bool> SaveKeyAsync(string? pasted)
    {
        if (IsBusy) return false;
        var key = pasted?.Trim();
        if (!SecretFileStore.IsWellFormed(key))
        {
            StatusText = T("Parity.NexusKeyMalformed");
            return false;
        }
        IsBusy = true;
        StatusText = T("Parity.NexusChecking");
        try { return await CheckAndSaveAsync(key!); }
        finally { IsBusy = false; }
    }

    /// <summary>A key the SSO window received: checked and saved like a pasted one.</summary>
    internal async Task<bool> AcceptSsoKeyAsync(string key)
    {
        StatusText = T("Parity.NexusChecking");
        return await CheckAndSaveAsync(key);
    }

    private async Task<bool> CheckAndSaveAsync(string key)
    {
        NexusUser user;
        try
        {
            user = await _api.ValidateKeyAsync(key);
        }
        catch (NexusApiException ex) when (ex.Rejected)
        {
            StatusText = T("Parity.NexusKeyRejected");
            return false;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or RHI.Core.Deployment.UntrustedDownloadException or TaskCanceledException)
        {
            AppLog.Warn("nexus", "Could not check the Nexus Mods key; nothing was saved", ex);
            StatusText = T("Parity.NexusCouldNotCheck");
            return false;
        }
        try
        {
            _key.Write(key);
            _preferences.Update(p => p with { NexusUserName = user.Name, NexusIsPremium = user.IsPremium });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("nexus", "Could not save the Nexus Mods key", ex);
            StatusText = TF("Parity.NexusNotSaved", UiLocalizationService.Present(ex.Message));
            SyncAccount();
            return false;
        }
        SyncAccount();
        StatusText = TF("Parity.NexusConnected", user.Name, T(user.IsPremium ? "Parity.NexusPremium" : "Parity.NexusFree"));
        return true;
    }

    /// <summary>Deletes the key and forgets the account (upstream Disconnect).</summary>
    public void Disconnect()
    {
        try
        {
            _key.Delete();
            _preferences.Update(p => p with { NexusUserName = null, NexusIsPremium = false });
            StatusText = T("Parity.NexusDisconnected");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("nexus", "Could not remove the Nexus Mods key", ex);
            StatusText = TF("Parity.NexusNotSaved", UiLocalizationService.Present(ex.Message));
        }
        SyncAccount();
    }

    /// <summary>Nexus's API key page, where a personal key is created (the paste path).</summary>
    public const string KeyPageUrl = "https://www.nexusmods.com/users/myaccount?tab=api";

    public void OpenKeyPage()
    {
        var (opened, error) = _openLink(KeyPageUrl);
        if (!opened) StatusText = TF("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }

    /// <summary>The login window's model (only when <see cref="SsoAvailable"/>).</summary>
    public NexusSsoPresenter CreateSso() => new(this, _sso(), SsoSlug ?? throw new InvalidOperationException("Nexus SSO is not registered."), _openLink);

    public async Task RefreshHandlerAsync()
    {
        var (state, handler) = await _handler.QueryAsync();
        HandlerIsRhi = state == NxmHandlerState.Rhi;
        HandlerText = state switch
        {
            NxmHandlerState.Rhi => T("Parity.NexusHandlerRhi"),
            NxmHandlerState.Other => TF("Parity.NexusHandlerOther", handler),
            NxmHandlerState.None => T("Parity.NexusHandlerNone"),
            _ => T("Parity.NexusHandlerUnknown")
        };
        OtherHandler = state == NxmHandlerState.Other ? handler : null;
    }

    /// <summary>The other application that handles nxm:// links now (the view asks before replacing it), if any.</summary>
    public string? OtherHandler { get; private set; }

    public async Task RegisterHandlerAsync()
    {
        try
        {
            StatusText = await _handler.RegisterAsync() ? T("Parity.NexusHandlerRegistered") : T("Parity.NexusHandlerNotRegistered");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("nxm", "Could not write the nxm:// handler entry", ex);
            StatusText = TF("Parity.NexusNotSaved", UiLocalizationService.Present(ex.Message));
        }
        await RefreshHandlerAsync();
    }

    public async Task UnregisterHandlerAsync()
    {
        try
        {
            await _handler.UnregisterAsync();
            StatusText = T("Parity.NexusHandlerUnregistered");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("nxm", "Could not remove the nxm:// handler entry", ex);
            StatusText = TF("Parity.NexusNotSaved", UiLocalizationService.Present(ex.Message));
        }
        await RefreshHandlerAsync();
    }

    private void SyncAccount()
    {
        var read = _key.Read();
        IsConnected = read.State == SecretFileState.Present;
        var prefs = _preferences.Load();
        AccountText = read.State switch
        {
            SecretFileState.Present when !string.IsNullOrWhiteSpace(prefs.NexusUserName)
                => TF("Parity.NexusConnectedAs", prefs.NexusUserName, T(prefs.NexusIsPremium ? "Parity.NexusPremium" : "Parity.NexusFree")),
            SecretFileState.Present => T("Parity.NexusConnectedUnknown"),
            SecretFileState.Exposed => T("Parity.NexusKeyExposed"),
            SecretFileState.Malformed or SecretFileState.Unreadable => T("Parity.NexusKeyUnusable"),
            _ => T("Parity.NexusNotConnected")
        };
    }
}

/// <summary>The Nexus login (SSO) window's model: one login attempt, cancellable, with the page re-openable.</summary>
public sealed partial class NexusSsoPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>How long the window waits for the user to authorise in the browser.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly NexusAccountPresenter _account;
    private readonly NexusSsoClient _client;
    private readonly string _slug;
    private readonly Func<string, (bool Opened, string? Error)> _openLink;
    private readonly CancellationTokenSource _cancel = new();

    internal NexusSsoPresenter(NexusAccountPresenter account, NexusSsoClient client, string slug, Func<string, (bool Opened, string? Error)> openLink)
    {
        _account = account;
        _client = client;
        _slug = slug;
        _openLink = openLink;
        _statusText = T("Parity.NexusSsoConnecting");
    }

    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string? _authorisationUrl;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _succeeded;

    /// <summary>Runs the login: opens the Nexus page in the browser and waits for the key; true when an account was connected.</summary>
    public async Task<bool> RunAsync()
    {
        IsRunning = true;
        try
        {
            var (key, stage) = await _client.AuthoriseAsync(_slug, url =>
            {
                AuthorisationUrl = url;
                OpenPage();
            }, s => StatusText = s == NexusSsoStage.WaitingForBrowser ? T("Parity.NexusSsoWaiting") : T("Parity.NexusSsoConnecting"),
                Timeout, _cancel.Token);
            if (key is null)
            {
                StatusText = stage switch
                {
                    NexusSsoStage.Cancelled => T("Parity.NexusSsoCancelled"),
                    NexusSsoStage.TimedOut => T("Parity.NexusSsoTimedOut"),
                    _ => T("Parity.NexusSsoFailed")
                };
                return false;
            }
            Succeeded = await _account.AcceptSsoKeyAsync(key);
            StatusText = _account.StatusText;
            return Succeeded;
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void OpenPage()
    {
        if (AuthorisationUrl is not { } url) return;
        var (opened, error) = _openLink(url);
        if (!opened) StatusText = TF("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }

    public void Cancel() => _cancel.Cancel();
}
