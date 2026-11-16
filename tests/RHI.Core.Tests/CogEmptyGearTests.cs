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

    /// <summary>w14/layouts-a (#297 owner decision): ASI Loader gets upstream's empty ⚙, between Install and Remove.</summary>
    [Fact]
    public void AsiLoader_HasTheNothingToSetGear_BetweenInstallAndRemove()
    {
        UiLocalizationService.Apply("en-US");
        Assert.Equal("ASI Loader Settings", RHI.Desktop.Views.CogDialog.TitleFor(ExtrasViewModel.UalCogName));
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "src", "RHI.Desktop", "Views", "MainWindow.axaml"));
        var row = xaml.Descendants().Single(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "UalRow");
        Assert.Equal(7, ((string)row.Attribute("Columns")!).Split(',').Length);
        var cog = row.Elements().Single(e => (string?)e.Attribute("Click") == "AsiLoaderSettings_Click");
        Assert.Equal("⚙", (string?)cog.Attribute("Content"));
        Assert.Equal("5", (string?)cog.Attribute("Grid.Column"));
        Assert.Equal("4", (string?)row.Elements().Single(e => (string?)e.Attribute("Command") == "{Binding InstallOrUpdateUalCommand}").Attribute("Grid.Column"));
        Assert.Equal("6", (string?)row.Elements().Single(e => (string?)e.Attribute("Command") == "{Binding RemoveUalCommand}").Attribute("Grid.Column"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RHI-Linux.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
