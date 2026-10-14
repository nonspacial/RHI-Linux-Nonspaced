using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Settings → Accounts &amp; updates → RHI-Linux updates (#100): check this fork's releases at start-up (when chosen) and
/// on demand, and only tell the user (owner decision: nothing is downloaded or installed; the release page opens in the
/// browser on request).
/// </summary>
public sealed partial class AppUpdatePresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly AppUpdateCheckService _service;
    private readonly AccountsPreferenceStore _preferences;
    private readonly Func<string, (bool Opened, string? Error)> _openLink;

    /// <param name="openLink">Test seam (default: the desktop's link opener).</param>
    public AppUpdatePresenter(AppUpdateCheckService service, AccountsPreferenceStore preferences,
        Func<string, (bool Opened, string? Error)>? openLink = null)
    {
        _service = service;
        _preferences = preferences;
        _openLink = openLink ?? (url => (LinuxDesktopOpener.TryOpenUri(url, out var error), error));
        _checkAtStartup = preferences.Load().CheckAppUpdatesAtStartup;
        _statusText = TF("Parity.AppUpdateThisVersion", service.CurrentVersion);
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private bool _checkAtStartup;

    /// <summary>The newer release's page (github.com), when the last check found one.</summary>
    [ObservableProperty] private string? _releasePageUrl;

    public string CurrentVersion => _service.CurrentVersion;

    /// <summary>Checks now; returns the notice for the status line when a newer release is published, else null.</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return null;
        IsBusy = true;
        StatusText = T("Parity.AppUpdateChecking");
        try
        {
            var check = await _service.CheckAsync(cancellationToken);
            ReleasePageUrl = check.Result == AppUpdateCheckResult.UpdateAvailable ? check.Latest!.PageUrl : null;
            StatusText = check.Result switch
            {
                AppUpdateCheckResult.UpdateAvailable => TF("Parity.AppUpdateAvailable", check.Latest!.Version, check.CurrentVersion),
                AppUpdateCheckResult.UpToDate => TF("Parity.AppUpdateUpToDate", check.CurrentVersion),
                AppUpdateCheckResult.NoReleases => TF("Parity.AppUpdateNoReleases", check.CurrentVersion),
                _ => TF("Parity.AppUpdateCouldNotCheck", check.CurrentVersion)
            };
            return check.Result == AppUpdateCheckResult.UpdateAvailable
                ? TF("Parity.AppUpdateNotice", check.Latest!.Version, check.CurrentVersion)
                : null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The start-up check (when chosen): waits for <paramref name="after"/> (the library load), checks once and hands a
    /// notice to <paramref name="notify"/> only when a newer release is published; anything else stays quiet, as upstream.
    /// </summary>
    public async Task NotifyAtStartupAsync(Task after, Action<string> notify)
    {
        if (!CheckAtStartup) return;
        try { await after; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("app-update", "The library load failed; the start-up update check still runs", ex);
        }
        if (await CheckAsync() is { } notice) notify(notice);
    }

    /// <summary>Saves the start-up choice; on a refused save the box goes back and the status line says why.</summary>
    public void SetCheckAtStartup(bool value)
    {
        if (ParityPersistence.TryPersist(() => _preferences.Update(p => p with { CheckAppUpdatesAtStartup = value }), "Parity.WhatAccounts") is { } notSaved)
        {
            StatusText = notSaved;
            CheckAtStartup = _preferences.Load().CheckAppUpdatesAtStartup;
            return;
        }
        CheckAtStartup = value;
    }

    /// <summary>Opens the newer release's page in the browser (nothing is downloaded by RHI).</summary>
    public void OpenReleasePage()
    {
        if (ReleasePageUrl is not { } url) return;
        var (opened, error) = _openLink(url);
        if (!opened) StatusText = TF("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }
}
