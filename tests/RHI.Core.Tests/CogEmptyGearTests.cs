using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// w14/ui-settings (#297): DOF Fix's ⚙ (upstream 2.9.0: "Components with nothing to set … now say so").
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CogEmptyGearTests
{
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
}
