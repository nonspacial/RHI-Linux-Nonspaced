using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #174 (owner, 2026-10-14): he saw no "F5" anywhere in the OptiScaler settings — the channel list only shows the chosen
/// entry until it is opened. The channel row's label now names every channel, and each channel has a plain description
/// (the entries' 2 s tooltips and the line under the list).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OptiScalerChannelTextTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void ChannelLabel_NamesEveryChannel_AndEachChannelHasItsOwnPlainDescription(string language)
    {
        try
        {
            UiLocalizationService.Apply(language);
            var label = UiLocalizationService.Text("Win.ChannelVariant");
            var options = OptionLists.OptiScalerVariant.Options();
            Assert.Equal(Enum.GetValues<OptiScalerVariant>().Length, options.Count);
            Assert.All(options, option => Assert.Contains(option.Display, label, StringComparison.Ordinal));

            var descriptions = options.Select(option => RuntimePanelViewModel.OptiScalerChannelText(option.Value)).ToList();
            Assert.Equal(descriptions.Count, descriptions.Distinct(StringComparer.Ordinal).Count());
            Assert.All(options, option => Assert.StartsWith(option.Display + ":", RuntimePanelViewModel.OptiScalerChannelText(option.Value), StringComparison.Ordinal));
            Assert.Contains("janblade", RuntimePanelViewModel.OptiScalerChannelText(OptiScalerVariant.F5), StringComparison.Ordinal);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }
}
