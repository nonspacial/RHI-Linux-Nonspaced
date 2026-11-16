using System.Runtime.CompilerServices;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w14/nr (#274): GITC Uplift's shell-level actions (upstream <c>SettingsHandler.UpliftKey</c>, <c>MainViewModel.Uplift</c>):
/// the Settings card's NR on/off key list and "Write key to all Uplift games", and the start/refresh pass that keeps iMMERSE
/// Launchpad at the top of each Uplift game's preset. The file work is <see cref="GitcUpliftDeploymentService"/>'s.
/// </summary>
public sealed class GitcUpliftPresenter
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, GitcUpliftPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _managed;

    public static GitcUpliftPresenter For(MainWindowViewModel shell, LinuxManagedComponentService managed)
        => Presenters.GetValue(shell, s => new GitcUpliftPresenter(s, managed));

    private GitcUpliftPresenter(MainWindowViewModel shell, LinuxManagedComponentService managed)
    {
        _shell = shell;
        _managed = managed;
    }

    /// <summary>The keys the NR on/off key list offers (Windows virtual-key codes, as Uplift reads <c>[Uplift] EnableKey</c>); 0 = None.</summary>
    public static IReadOnlyList<(int Vk, string Name)> Keys { get; } =
    [
        (0, ""), // None: leave each game's own key
        .. Enumerable.Range(1, 12).Select(n => (0x6F + n, $"F{n}")),
        (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"), (0x21, "Page Up"), (0x22, "Page Down"),
        (0x13, "Pause"), (0x91, "Scroll Lock"),
        .. Enumerable.Range(0, 10).Select(n => (0x60 + n, $"Numpad {n}"))
    ];

    /// <summary>A key's name in the list ("None" for 0 or a code the list does not have).</summary>
    public static string KeyName(int vk)
        => Keys.FirstOrDefault(k => k.Vk == vk && vk != 0).Name is { Length: > 0 } name ? name : UiLocalizationService.Text("Uplift.KeyNone");

    /// <summary>
    /// Saves the key, then writes it into every library game (not hidden) where RHI installed Uplift; the line to show.
    /// None writes nothing (upstream: "Pick a key first").
    /// </summary>
    public async Task<string> WriteKeyToAllAsync(int vk, Func<int, string?> save)
    {
        if (vk <= 0) return UiLocalizationService.Text("Uplift.KeyChooseFirst");
        if (save(vk) is { } refused) return refused;
        using var op = _shell.Operations.TryBegin("gitc-uplift-key");
        if (op is null) return UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning");
        var games = _shell.AllLibraryGames.Where(g => !g.IsHidden).ToArray();
        try
        {
            var (written, failures) = await Task.Run(() => _managed.WriteUpliftKeyToAllAsync(games, vk, op.Token));
            var line = UiLocalizationService.Format("Uplift.KeyWritten", KeyName(vk), written);
            return failures.Count == 0
                ? line
                : line + UiLocalizationService.Format("Uplift.KeyFailed", failures.Count, failures[0].Game, UiLocalizationService.Present(failures[0].Message));
        }
        catch (OperationCanceledException)
        {
            return UiLocalizationService.Text("Uplift.KeyCancelled");
        }
    }

    /// <summary>iMMERSE Launchpad back at the top of every Uplift game's preset (games not running only); run at start and on Refresh.</summary>
    public Task<int> KeepLaunchpadFirstAsync(CancellationToken cancellationToken = default)
        => _managed.KeepLaunchpadFirstAsync(_shell.AllLibraryGames.ToArray(), cancellationToken);
}
