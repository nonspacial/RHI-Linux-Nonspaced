using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// UI-20 (#87): one display↔value mapping per option list, shared by the view model and the Settings / Edit game
/// dialogs. Every value round-trips display → value → display in both UI languages, a label from the other language
/// (a dialog opened before a language switch) still reads as its value, and no label names two values.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OptionListsTests
{
    public static TheoryData<string> Languages => new() { "pt-BR", "en-US" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryList_RoundTripsEveryValue_InBothLanguages(string language)
    {
        UiLocalizationService.Apply(language);
        try
        {
            AssertRoundTrips(OptionLists.ReShadeChannel);
            AssertRoundTrips(OptionLists.UiLanguage);
            AssertRoundTrips(OptionLists.ViewMode);
            AssertRoundTrips(OptionLists.OptiScalerVariant);
            AssertRoundTrips(OptionLists.LumaMode);
            AssertRoundTrips(OptionLists.HdrMode);
            AssertRoundTrips(OptionLists.GraphicsApiOverride);
            AssertRoundTrips(OptionLists.LibraryFilter);
            AssertRoundTrips(OptionLists.LaunchProvider);
            // w5/update-policy rows
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            AssertRoundTrips(OptionLists.Bitness);
            AssertRoundTrips(OptionLists.OptiScalerMenuKey);
            // w5/reshade-core rows
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    [Fact]
    public void ListsHoldTheValuesTheStoresUse()
    {
        Assert.Equal(["Stable", "Nightly", "Legacy", "Custom", "NoAddons"], OptionLists.ReShadeChannel.Values);
        Assert.Equal(["pt-BR", "en-US"], OptionLists.UiLanguage.Values);
        Assert.Equal(["Detail", "Compact"], OptionLists.ViewMode.Values);
        Assert.Equal(Enum.GetValues<OptiScalerVariant>(), OptionLists.OptiScalerVariant.Values);
        Assert.Equal(Enum.GetValues<LumaModePreference>(), OptionLists.LumaMode.Values);
        Assert.Equal([RHI.Platform.Linux.Hdr.LinuxHdrModes.Off, RHI.Platform.Linux.Hdr.LinuxHdrModes.NativeWayland, RHI.Platform.Linux.Hdr.LinuxHdrModes.Gamescope],
            OptionLists.HdrMode.Values);
        Assert.Equal(LinuxGameIdentityService.LaunchProviderOptions, OptionLists.LaunchProvider.Values);
        foreach (var filter in OptionLists.LibraryFilter.Values)
            Assert.Equal(filter, LinuxGameLibraryStore.NormalizeFilterMode(filter));
        Assert.Contains(GraphicsApi.DirectX12, OptionLists.GraphicsApiOverride.Values);
        Assert.Null(OptionLists.GraphicsApiOverride.Fallback);
    }

    [Fact]
    public void OlderLabelsAndStoredTokens_ReadAsTheirValue_UnknownReadsAsTheFallback()
    {
        // The Settings dialog used to read "Desenvolvimento" as Stable while the view model read it as Nightly.
        Assert.Equal("Nightly", OptionLists.ReShadeChannel.Value("Desenvolvimento"));
        Assert.Equal("Nightly", OptionLists.ReShadeChannel.Value("Desenvolvimento (Nightly)"));
        Assert.Equal("Legacy", OptionLists.ReShadeChannel.Value("Legado"));
        Assert.Equal(OptiScalerVariant.Nightly, OptionLists.OptiScalerVariant.Value("Desenvolvimento"));
        Assert.Equal(LumaModePreference.Disabled, OptionLists.LumaMode.Value("Desativado"));
        Assert.Equal(LinuxGameIdentityService.AutomaticLaunchProvider, OptionLists.LaunchProvider.Value("Automatic"));
        Assert.Equal("Compact", OptionLists.ViewMode.Value("compact"));
        Assert.Equal("Stable", OptionLists.ReShadeChannel.Value("something else"));
        Assert.Equal("pt-BR", OptionLists.UiLanguage.Value(null));
        Assert.Equal(OptionLists.ReShadeChannel.Display("Stable"), OptionLists.ReShadeChannel.Display("unknown"));
    }

    [Fact]
    public void LatestAndDoNotChange_AreRecognisedInEitherLanguage()
    {
        foreach (var language in new[] { "pt-BR", "en-US" })
        {
            Assert.True(OptionLists.IsLatest(UiLocalizationService.TextFor(language, "Ui.OptionLatest")));
            Assert.True(OptionLists.IsDoNotChange(UiLocalizationService.TextFor(language, "Win.DoNotChange")));
        }
        Assert.False(OptionLists.IsLatest("v1.2"));
        Assert.False(OptionLists.IsDoNotChange("310.2.1"));
    }

    private static void AssertRoundTrips<T>(OptionList<T> list)
    {
        Assert.Equal(list.Values.Select(list.Display), list.Items);
        Assert.Equal(list.Items.Count, list.Items.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var owner = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list.Entries)
        {
            var display = list.Display(entry.Value);
            Assert.Equal(entry.Value, list.Value(display));
            Assert.Equal(display, list.Display(list.Value(display)));
            foreach (var label in list.Labels(entry))
            {
                Assert.Equal(entry.Value, list.Value(label)); // a label from either language, the stored value or an alias
                if (owner.TryGetValue(label, out var other))
                    Assert.True(EqualityComparer<T>.Default.Equals(other, entry.Value), $"'{label}' names both {other} and {entry.Value}");
                owner[label] = entry.Value;
            }
        }
    }
}
