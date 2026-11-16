using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Artwork;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w14/layouts-b (#305, upstream 2.9.5 beta Settings › Accounts &amp; Data › SteamGridDB API key): paste a key and save it
/// once SteamGridDB accepts it, or remove it. The key is never shown again (the status says only that one is saved); the
/// view clears the box once a save succeeded (<see cref="SaveAsync"/> returns true).
/// </summary>
public sealed partial class SteamGridDbKeyPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly SteamGridDbService _service;

    public SteamGridDbKeyPresenter(SteamGridDbService service)
    {
        _service = service;
        _hasSavedKey = service.HasKey;
        _statusText = _hasSavedKey ? T("Sgdb.KeySavedState") : T("Sgdb.NoKeySaved");
    }

    [ObservableProperty] private bool _hasSavedKey;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText;

    /// <summary>Checks <paramref name="pasted"/> with SteamGridDB and saves it when accepted; true when it was saved.</summary>
    public async Task<bool> SaveAsync(string? pasted)
    {
        if (IsBusy) return false;
        if (string.IsNullOrWhiteSpace(pasted)) { StatusText = T("Sgdb.PasteFirst"); return false; }
        IsBusy = true;
        StatusText = T("Sgdb.Checking");
        try
        {
            SteamGridDbStatus status;
            try { status = await _service.SaveKeyAsync(pasted); }
            catch (IOException ex) { StatusText = UiLocalizationService.Format("Sgdb.KeyNotSaved", UiLocalizationService.Present(ex.Message)); return false; }
            HasSavedKey = _service.HasKey;
            StatusText = status switch
            {
                SteamGridDbStatus.Ok => T("Sgdb.KeySaved"),
                SteamGridDbStatus.BadKey => T("Sgdb.KeyRefused"),
                SteamGridDbStatus.Offline => T("Sgdb.KeyOffline"),
                SteamGridDbStatus.Error => T("Sgdb.BadReply"),
                _ => T("Sgdb.KeyNotChecked"),
            };
            return status == SteamGridDbStatus.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Removes the saved key.</summary>
    public void Clear()
    {
        _service.ClearKey();
        HasSavedKey = _service.HasKey;
        StatusText = T("Sgdb.KeyRemoved");
    }
}
