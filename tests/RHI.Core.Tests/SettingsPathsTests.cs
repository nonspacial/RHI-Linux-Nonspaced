using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review M2: help texts sent people to Settings tabs that no longer exist ("Settings → Accounts &amp; updates",
/// "Settings → Support", "Settings → Maintenance"). Every "Settings › …" (or "→") path named in any string, in English and
/// Portuguese, must name a real section of the Settings page, then (where named) a card or heading in that section, then
/// (where named) something shown in it.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SettingsPathsTests
{
    private sealed record Place(string Title, HashSet<string> Cards, HashSet<string> Texts);

    private static async Task<List<Place>> PageAsync(string language)
    {
        UiLocalizationService.Apply(language);
        using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language);
        // The app's own parity services (the Accounts & Data cards).
        using var http = new HttpClient(new Offline());
        var wine = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http,
            new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(System.IO.Path.Combine(fx.Root, "records.json")), wine,
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wine, System.IO.Path.Combine(fx.Root, "journal")));
        fx.Vm.Parity = services.Parity;
        UiLocalizationService.Apply(language);
        var model = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        var view = new RHI.Desktop.Views.Dialogs.SettingsDialogView { DataContext = model };
        var places = new List<Place>();
        foreach (var key in SettingsSections.Order)
        {
            var panel = view.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Tag as string == "Section:" + key);
            var all = panel.GetLogicalDescendants().ToList();
            var cards = all.OfType<TextBlock>().Where(t => t.Classes.Contains("settingsCardTitle") || t.Classes.Contains("settingsHead"))
                .Select(t => t.Text ?? "").ToHashSet(StringComparer.Ordinal);
            if (key == SettingsSections.Tools) cards.Add(XboxFoldersPresenter.Label); // shown where the Xbox app is set up
            var texts = all.OfType<TextBlock>().Select(t => t.Text ?? "")
                .Concat(all.OfType<ContentControl>().Select(c => c.Content as string ?? ""))
                .Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
            places.Add(new Place(SettingsSections.Title(key), cards, texts));
        }
        UiLocalizationService.Apply("en-US");
        return places;
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    private static IEnumerable<(string Where, string Text)> Strings(string language)
    {
        foreach (var key in UiLocalizationService.Keys) yield return (key, UiLocalizationService.TextFor(language, key));
        var english = language == "en-US";
        foreach (var (en, pt) in UiLocalizationService.ExactPresentationPairs.Concat(UiLocalizationService.PresentationPairs))
            yield return ("platform message", english ? en : pt);
        foreach (var (pt, en) in PlatformMessageTranslations.PortugueseOnly) yield return ("platform message", english ? en : pt);
    }

    private static readonly Regex Path = new(@"(?<!Steam [›→] ?)\b(?:Settings|Configurações) ?[›→] ?", RegexOptions.CultureInvariant);
    private static readonly Regex Separator = new(@"^ ?[›→] ?", RegexOptions.CultureInvariant);

    /// <summary>The path's problem, or null when it resolves.</summary>
    private static string? Check(string text, int start, List<Place> page)
    {
        var rest = text[start..];
        var section = page.Where(p => rest.StartsWith(p.Title, StringComparison.Ordinal)).MaxBy(p => p.Title.Length);
        if (section is null) return "no section";
        rest = rest[section.Title.Length..];
        var level = 0;
        while (Separator.Match(rest) is { Success: true } sep)
        {
            rest = rest[sep.Length..];
            var names = level == 0 ? section.Cards : section.Texts;
            var name = names.Where(n => n.Length > 0 && rest.StartsWith(n, StringComparison.Ordinal)).MaxBy(n => n.Length);
            if (name is null) return level == 0 ? $"no card or heading in {section.Title}" : $"nothing shown in {section.Title}";
            rest = rest[name.Length..];
            level++;
        }
        return null;
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task EverySettingsPath_NamesARealSection_AndCard(string language)
    {
        var page = await PageAsync(language);
        var broken = new List<string>();
        foreach (var (where, text) in Strings(language))
            foreach (Match m in Path.Matches(text))
                if (Check(text, m.Index + m.Length, page) is { } problem)
                    broken.Add($"{where}: {problem}: …{text[m.Index..Math.Min(text.Length, m.Index + 90)]}");
        Assert.True(broken.Count == 0, string.Join("\n", broken.Distinct()));
    }

    [Fact]
    public async Task Check_RefusesAMissingSectionOrCard()
    {
        var page = await PageAsync("en-US");
        string? Of(string text) => Check(text, Path.Match(text) is { Success: true } m ? m.Index + m.Length : throw new InvalidOperationException(), page);
        Assert.Null(Of("Settings › General › Updates."));
        Assert.Null(Of("Settings › Tools & Maintenance › Troubleshooting › Removed games."));
        Assert.Equal("no section", Of("Settings → Accounts & updates → Connect"));
        Assert.Equal("no section", Of("Settings → Support."));
        Assert.Equal("no card or heading in General", Of("Settings › General › Maintenance"));
    }
}
