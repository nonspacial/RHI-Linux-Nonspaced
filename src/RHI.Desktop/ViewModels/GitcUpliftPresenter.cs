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

    /// <summary>
    /// w14/nr-2: the key Uplift's NR on/off hotkey takes, as upstream captures it (one key, no modifiers): the Windows
    /// virtual-key code Uplift reads from <c>[Uplift] EnableKey</c>, or 0 for Backspace (None). Null for a key the box lets
    /// through (Tab, Enter, Escape keep their normal job) or ignores (modifiers, the Windows keys, keys with no code here).
    /// </summary>
    public static int? VirtualKey(Avalonia.Input.Key key)
    {
        if (key == Avalonia.Input.Key.Back) return 0;
        return KeyCodes.TryGetValue(key, out var vk) ? vk : null;
    }

    /// <summary>A key's name ("None" for 0; upstream's hotkey display: "F10", "Numpad 0", "A", "Page Up"; 0x… for a code with no name).</summary>
    public static string KeyName(int vk)
    {
        if (vk <= 0) return UiLocalizationService.Text("Uplift.KeyNone");
        return KeyNames.TryGetValue(vk, out var name) ? name : "0x" + vk.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly IReadOnlyDictionary<Avalonia.Input.Key, int> KeyCodes = BuildKeyCodes();
    private static readonly IReadOnlyDictionary<int, string> KeyNames = BuildKeyNames();

    private static Dictionary<Avalonia.Input.Key, int> BuildKeyCodes()
    {
        var codes = new Dictionary<Avalonia.Input.Key, int>();
        for (var i = 0; i < 26; i++) codes[Avalonia.Input.Key.A + i] = 0x41 + i;
        for (var i = 0; i < 10; i++) codes[Avalonia.Input.Key.D0 + i] = 0x30 + i;
        for (var i = 0; i < 10; i++) codes[Avalonia.Input.Key.NumPad0 + i] = 0x60 + i;
        for (var i = 0; i < 24; i++) codes[Avalonia.Input.Key.F1 + i] = 0x70 + i;
        foreach (var (key, vk) in new (Avalonia.Input.Key, int)[]
                 {
                     (Avalonia.Input.Key.Multiply, 0x6A), (Avalonia.Input.Key.Add, 0x6B), (Avalonia.Input.Key.Subtract, 0x6D),
                     (Avalonia.Input.Key.Decimal, 0x6E), (Avalonia.Input.Key.Divide, 0x6F),
                     (Avalonia.Input.Key.Insert, 0x2D), (Avalonia.Input.Key.Delete, 0x2E), (Avalonia.Input.Key.Home, 0x24),
                     (Avalonia.Input.Key.End, 0x23), (Avalonia.Input.Key.PageUp, 0x21), (Avalonia.Input.Key.PageDown, 0x22),
                     (Avalonia.Input.Key.Pause, 0x13), (Avalonia.Input.Key.Scroll, 0x91), (Avalonia.Input.Key.NumLock, 0x90),
                     (Avalonia.Input.Key.CapsLock, 0x14), (Avalonia.Input.Key.PrintScreen, 0x2C), (Avalonia.Input.Key.Space, 0x20),
                     (Avalonia.Input.Key.Left, 0x25), (Avalonia.Input.Key.Up, 0x26), (Avalonia.Input.Key.Right, 0x27), (Avalonia.Input.Key.Down, 0x28),
                     (Avalonia.Input.Key.OemSemicolon, 0xBA), (Avalonia.Input.Key.OemPlus, 0xBB), (Avalonia.Input.Key.OemComma, 0xBC),
                     (Avalonia.Input.Key.OemMinus, 0xBD), (Avalonia.Input.Key.OemPeriod, 0xBE), (Avalonia.Input.Key.OemQuestion, 0xBF),
                     (Avalonia.Input.Key.OemTilde, 0xC0), (Avalonia.Input.Key.OemOpenBrackets, 0xDB), (Avalonia.Input.Key.OemPipe, 0xDC),
                     (Avalonia.Input.Key.OemCloseBrackets, 0xDD), (Avalonia.Input.Key.OemQuotes, 0xDE), (Avalonia.Input.Key.OemBackslash, 0xE2),
                 })
            codes[key] = vk;
        return codes;
    }

    private static Dictionary<int, string> BuildKeyNames()
    {
        var names = new Dictionary<int, string>();
        for (var i = 0; i < 26; i++) names[0x41 + i] = ((char)('A' + i)).ToString();
        for (var i = 0; i < 10; i++) names[0x30 + i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 10; i++) names[0x60 + i] = $"Numpad {i}";
        for (var i = 0; i < 24; i++) names[0x70 + i] = $"F{i + 1}";
        foreach (var (vk, name) in new (int, string)[]
                 {
                     (0x6A, "Numpad *"), (0x6B, "Numpad +"), (0x6D, "Numpad -"), (0x6E, "Numpad ."), (0x6F, "Numpad /"),
                     (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"), (0x21, "Page Up"), (0x22, "Page Down"),
                     (0x13, "Pause"), (0x91, "Scroll Lock"), (0x90, "Num Lock"), (0x14, "Caps Lock"), (0x2C, "Print Screen"),
                     (0x20, "Space"), (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"),
                     (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
                     (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"), (0xE2, "\\ (102nd key)"),
                 })
            names[vk] = name;
        return names;
    }

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
