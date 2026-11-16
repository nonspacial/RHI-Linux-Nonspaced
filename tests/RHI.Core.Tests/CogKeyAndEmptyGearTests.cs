using Avalonia.Controls;
using Avalonia.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// w14/ui-settings (#297): DOF Fix's ⚙ (upstream 2.9.0: "Components with nothing to set … now say so") and the shared key
/// capture box, which saves a key the moment it is pressed (upstream 2.9.4 <c>SetUpliftKey</c>; GITC Uplift's NR on/off key).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CogKeyAndEmptyGearTests
{
    private static bool Press(TextBox box, Key key)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = box };
        box.RaiseEvent(args);
        return args.Handled;
    }

    [Fact]
    public void DofFix_HasAGear_AndOnlyDofFix_GetsTheNothingToSetGear()
    {
        UiLocalizationService.Apply("en-US");
        static ComponentRowViewModel Row(ManagedComponentKind kind, ComponentAvailability availability = ComponentAvailability.Available)
            => new(new ManagedComponent { Kind = kind, DisplayName = kind == ManagedComponentKind.DofFix ? "DOF Fix" : kind.ToString(), Availability = availability },
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));
        var dof = Row(ManagedComponentKind.DofFix);
        Assert.True(dof.HasEmptySettings);
        Assert.Equal("DOF Fix Settings", dof.EmptySettingsTip);
        Assert.True(Row(ManagedComponentKind.DofFix, ComponentAvailability.Installed).HasEmptySettings);
        Assert.False(Row(ManagedComponentKind.DofFix, ComponentAvailability.Unsupported).HasEmptySettings);
        Assert.False(Row(ManagedComponentKind.ReShade).HasEmptySettings);

        var row = LumaNexusRowLayoutTests.ComponentRow().Elements().Single(e => (string?)e.Attribute("IsVisible") == "{Binding HasEmptySettings}");
        Assert.Equal("4", (string?)row.Attribute("Grid.Column")); // the row's ⚙ column
        Assert.Equal("EmptySettings_Click", (string?)row.Attribute("Click"));
    }

    [Fact]
    public void TheKeyBox_SavesTheKeyTheMomentItIsPressed_AndARefusedKeyGoesBack()
    {
        UiLocalizationService.Apply("en-US");
        var saved = new List<int>();
        string? refusal = null;
        var shown = new List<string?>();
        var box = new KeyCaptureBox(0x78, vk => { saved.Add(vk); return refusal; }, shown.Add, "UpliftKey");
        Assert.Equal("F9", box.Box.Text);
        Assert.Empty(saved); // nothing is written when the box is built

        Assert.True(Press(box.Box, Key.F10));
        Assert.Equal([0x79], saved); // saved at once: no Save / Apply needed
        Assert.Equal(0x79, box.Key);
        Assert.Equal("F10", box.Box.Text);

        refusal = "Could not save the settings: disk full";
        Assert.True(Press(box.Box, Key.K));
        Assert.Equal([0x79, 0x4B], saved);
        Assert.Equal(0x79, box.Key); // refused: back to the saved key
        Assert.Equal("F10", box.Box.Text);
        Assert.Equal(refusal, shown[^1]);

        refusal = null;
        Assert.False(Press(box.Box, Key.LeftCtrl)); // a modifier alone is not a key
        Assert.False(Press(box.Box, Key.Tab)); // Tab keeps moving the focus
        box.Clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, saved[^1]); // ↺: None, saved at once
        Assert.Equal("", box.Box.Text);
    }

    [Fact]
    public async Task TheUpliftKey_PressedInTheBox_IsInTheSettingsFileAtOnce()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var box = new KeyCaptureBox(fx.Settings.Load().UpliftEnableKey, fx.Vm.Settings.SaveUpliftEnableKey);
        Assert.True(Press(box.Box, Key.F7));
        Assert.Equal(0x76, fx.Settings.Load().UpliftEnableKey);
    }
}
