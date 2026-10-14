using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

/// <summary>Settings → Accounts &amp; updates choices that are not secrets (the secrets live in 0600 files).</summary>
public sealed record AccountsPreferences
{
    /// <summary>Look for a newer RHI-Linux release at start-up (#100; upstream checks at every start).</summary>
    public bool CheckAppUpdatesAtStartup { get; init; } = true;

    /// <summary>Nexus Mods account name the saved key belongs to (#99), as Nexus reported it when the key was checked.</summary>
    public string? NexusUserName { get; init; }

    /// <summary>Whether that Nexus account is Premium (direct downloads without a link from the website).</summary>
    public bool NexusIsPremium { get; init; }
}

/// <summary>
/// <c>~/.config/rhi-linux/accounts.json</c>: <see cref="AccountsPreferences"/>, through <see cref="JsonFileStore{T}"/>
/// (missing = defaults; unreadable = copied aside and saves refused until it reads again). Kept out of
/// <c>settings.json</c>, which the Settings dialog rewrites from its own controls on every Save.
/// </summary>
public sealed class AccountsPreferenceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly JsonFileStore<AccountsPreferences> _file;

    public AccountsPreferenceStore(string? path = null)
        => _file = new JsonFileStore<AccountsPreferences>(path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "accounts.json"), Options);

    public string FilePath => _file.FilePath;

    public AccountsPreferences Load() => _file.LoadOr(() => new AccountsPreferences());

    /// <summary>Applies <paramref name="change"/> to the saved choices and saves them (throws an <see cref="IOException"/> when refused).</summary>
    public AccountsPreferences Update(Func<AccountsPreferences, AccountsPreferences> change)
    {
        var next = change(_file.Load().Value ?? new AccountsPreferences());
        _file.Save(next); // refused (JsonStoreUnavailableException) while an unreadable file is on disk
        return next;
    }
}
