using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/nr (#274): GITC Uplift's interface — the Settings card (upstream Settings › Components › GITC Uplift: NR on/off key,
/// Write key to all Uplift games) and the per-game controls beside the Neural Rendering versions (Uplift Version, iMMERSE
/// Launchpad, the GITC Uplift link).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class GitcUpliftUiTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-uplift-ui-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, GitcUpliftDeploymentServiceTests.UpliftFixture.Pe(64 * 1024));
        return new DetectedGame
        {
            Name = name, Source = "Wine", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Wine", LaunchCommand = $"wine {name}.exe", GraphicsApi = GraphicsApi.DirectX12
        };
    }

    [Fact]
    public void SettingsCard_ShowsTheSavedKey_ByName_AndTakesTheKeyPressedInTheBox()
    {
        // w14/nr-2: the key is captured as upstream does (click the box, press a key; Backspace or ↺ = None), stored as the
        // same virtual-key code the nr lane saved (0x78 = F9).
        var presenter = (GitcUpliftPresenter)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitcUpliftPresenter));
        var card = new GitcUpliftSettingsCard(new LinuxAppSettings { UpliftEnableKey = 0x78 }, presenter, null!, _ => { });

        var panel = Assert.IsType<Border>(card.Control); // w14/ui-settings (#296): the card itself, in the Settings layout
        Assert.Contains("settingsCard", panel.Classes);
        var saves = 0;
        card.KeyChanged += () => saves++; // #297 (upstream): the key is saved the moment it is pressed
        var key = panel.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "GitcUpliftEnableKey");
        Assert.True(key.IsReadOnly);
        Assert.Equal("F9", key.Text);
        Assert.Equal(0x78, card.Key);
        Assert.Contains(panel.GetLogicalDescendants().OfType<Button>(), b => b.Name == "GitcUpliftWriteAll");

        Assert.True(Press(key, Avalonia.Input.Key.F10));
        Assert.Equal(0x79, card.Key);
        Assert.Equal(1, saves);
        Assert.Equal("F10", key.Text);
        Assert.True(Press(key, Avalonia.Input.Key.NumPad5));
        Assert.Equal(0x65, card.Key);
        Assert.True(Press(key, Avalonia.Input.Key.K));
        Assert.Equal(0x4B, card.Key);
        Assert.False(Press(key, Avalonia.Input.Key.LeftCtrl)); // a modifier alone is not a key
        Assert.False(Press(key, Avalonia.Input.Key.Tab)); // Tab keeps moving the focus
        Assert.Equal(0x4B, card.Key);
        Assert.Equal(3, saves); // F10, NumPad5, K; not the modifier nor Tab

        Assert.True(Press(key, Avalonia.Input.Key.Back));
        Assert.Equal(0, card.Key); // None: each game keeps its own key
        Assert.Equal("", key.Text);
        card.Reset(new LinuxAppSettings { UpliftEnableKey = 0x2D });
        Assert.Equal(0x2D, card.Key);
        Assert.Equal("Insert", key.Text);
        panel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "GitcUpliftKeyClear")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, card.Key);
    }

    private static bool Press(TextBox box, Avalonia.Input.Key key)
    {
        var args = new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, Source = box };
        box.RaiseEvent(args);
        return args.Handled;
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.F1, 0x70, "F1")]
    [InlineData(Avalonia.Input.Key.F12, 0x7B, "F12")]
    [InlineData(Avalonia.Input.Key.NumPad0, 0x60, "Numpad 0")]
    [InlineData(Avalonia.Input.Key.Insert, 0x2D, "Insert")]
    [InlineData(Avalonia.Input.Key.PageDown, 0x22, "Page Down")]
    [InlineData(Avalonia.Input.Key.Scroll, 0x91, "Scroll Lock")]
    [InlineData(Avalonia.Input.Key.A, 0x41, "A")]
    [InlineData(Avalonia.Input.Key.D7, 0x37, "7")]
    [InlineData(Avalonia.Input.Key.OemTilde, 0xC0, "`")]
    public void PressedKeys_MapToTheWindowsVirtualKeyCodesUpliftReads(Avalonia.Input.Key key, int vk, string name)
    {
        Assert.Equal(vk, GitcUpliftPresenter.VirtualKey(key));
        Assert.Equal(name, GitcUpliftPresenter.KeyName(vk));
    }

    [Fact]
    public async Task WriteKeyToAll_WithNone_WritesNothing_AndWithAKey_ReachesEveryUpliftGame()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("UpliftGame"), Game("OtherGame")]);
        var saved = new List<int>();
        var none = await fx.Vm.GitcUplift.WriteKeyToAllAsync(0, vk => { saved.Add(vk); return null; });
        Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Text("Uplift.KeyChooseFirst"), none);
        Assert.Empty(saved);

        var game = fx.Vm.AllLibraryGames.Single(g => g.Name == "UpliftGame");
        var ini = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\n");
        fx.Records.Upsert(new LinuxDeploymentRecord { GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name,
            Component = ManagedComponentKind.GitcUplift, BinaryDirectory = game.BinaryDirectory!, Version = "2.0.0" });

        var line = await fx.Vm.GitcUplift.WriteKeyToAllAsync(0x78, vk => { saved.Add(vk); return null; });

        Assert.Equal([0x78], saved);
        Assert.Contains("F9", line);
        Assert.Equal("120", ReShadePresetText.GetKey(File.ReadAllText(ini), "Uplift", "EnableKey"));
        Assert.False(File.Exists(Path.Combine(fx.Vm.AllLibraryGames.Single(g => g.Name == "OtherGame").BinaryDirectory!, "ReShade.ini")));
    }

    [Fact]
    public async Task ExtrasControls_UpliftVersionStartsAtLatest_AndLaunchpadIsOnForAGameWithoutDlss()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("NoDlss")]);
        fx.Vm.SelectedGameRow = fx.Vm.Games.Single(row => row.Name == "NoDlss");
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || fx.Vm.SelectedGame is null); i++) await Task.Delay(25);

        Assert.True(fx.Vm.Extras.ShowGitcUplift);
        Assert.Equal(fx.Vm.Extras.GitcUpliftVersionOptions[0], fx.Vm.Extras.SelectedGitcUpliftVersion);
        Assert.True(fx.Vm.Extras.UpliftLaunchpad); // upstream default: on in games without DLSS

        fx.Vm.Extras.UpliftLaunchpad = false; // not installed: the choice is saved for the install
        for (var i = 0; i < 200 && fx.Vm.Extras.UpliftLaunchpad; i++) await Task.Delay(25);
        Assert.False(fx.Vm.Extras.UpliftLaunchpad);
        Assert.False(fx.Managed.GetUpliftLaunchpadChoice(fx.Vm.SelectedGame!, gameHasDlss: false));
    }

    [Fact]
    public void MainWindow_PutsUpliftVersionWithTheOtherNrVersions_AndLaunchpadAndLinkUnderThem()
    {
        var path = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "Views", "MainWindow.axaml");
        var named = XDocument.Load(path).Descendants()
            .Where(e => e.Attributes().Any(a => a.Name.LocalName == "Name"))
            .ToDictionary(e => (string)e.Attributes().First(a => a.Name.LocalName == "Name"));

        var version = named["GitcUpliftVersion"];
        Assert.Equal("{Binding SelectedGitcUpliftVersion, Mode=TwoWay}", (string?)version.Attribute("SelectedItem"));
        // In the NR versions row, right after ShortFuse's version (upstream's middle "Uplift Version" combo).
        var wrap = version.Parent!.Parent!;
        Assert.Contains(wrap.Elements(), e => e.Descendants().Any(d => (string?)d.Attribute("SelectedItem") == "{Binding SelectedShortFuseVersion, Mode=TwoWay}"));
        Assert.Equal("{Binding UpliftLaunchpad, Mode=TwoWay}", (string?)named["GitcUpliftLaunchpad"].Attribute("IsChecked"));
        Assert.Equal("{Binding OpenGitcUpliftLinkCommand}", (string?)named["GitcUpliftLink"].Attribute("Command"));
    }
}
