using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>
/// UI-11 (#79) and #143: every UI string lives in one key table with both languages, and the English UI never shows
/// Portuguese — neither from the table nor from platform messages the Linux services still emit in Portuguese only.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LocalizationTableTests
{
    // Product names and symbols that read the same in both languages.
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal)
    {
        "Ui.ShadersAddons", "Ui.Links", "Ui.Motd", "Ui.DlssStreamline", "Ui.Extras", "Ui.UxTab", "Ui.Rtx40MfgTitle",
        "Ui.HdrGamescope", "Vm.Symbol", "Vm.UltimateASILoader", "Vm.ShortFuse", "Win.Extra"
    };

    private static readonly Regex PortugueseLetters = new("[ãõçáâêôíúéóàÃÕÇÁÂÊÔÍÚÉÓÀ]");
    private static readonly Regex PortugueseWords = new(
        @"\b(não|você|jogo|jogos|arquivo|arquivos|selecione|selecionar|configurações|está|também|pasta|instalação|remova|atualização|dependência)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex Placeholder = new(@"\{(\d+)[^}]*\}");

    private static bool LooksPortuguese(string text) => PortugueseLetters.IsMatch(text) || PortugueseWords.IsMatch(text);

    private static int[] Placeholders(string text)
        => Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();

    [Fact]
    public void EveryKey_HasBothLanguages()
        => Assert.Empty(UiLocalizationService.KeysMissingAPair);

    // w3 integration: lanes appended rows under their own anchors; a text both languages already have is one key, so a
    // Portuguese text never stands for two keys (it held on fdf95e5, before the lanes' rows).
    [Fact]
    public void EveryPortugueseText_BelongsToOneKey()
    {
        var shared = UiStrings.Entries.GroupBy(row => row.PtBr, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(row => row.Key))}")
            .ToArray();
        Assert.True(shared.Length == 0, "PT-BR text under several keys:\n" + string.Join("\n", shared));
    }

    // #166: a PT-BR text never keeps an English action word ("será usada no próximo Install"); "download" is everyday
    // Portuguese and "Remove" is the verb remover, so neither is listed.
    private static readonly Regex EnglishActionWords = new(@"\b(install|installed|installing|uninstall|update|updated|updating)\b", RegexOptions.IgnoreCase);

    [Fact]
    public void NoPortugueseText_KeepsAnEnglishActionWord()
    {
        var kept = UiLocalizationService.Keys
            .Select(key => (key, pt: UiLocalizationService.TextFor("pt-BR", key)))
            .Where(row => EnglishActionWords.IsMatch(row.pt))
            .Select(row => $"{row.key}: {row.pt}")
            .ToArray();
        Assert.True(kept.Length == 0, "PT-BR text with an English action word:\n" + string.Join("\n", kept));
    }

    [Fact]
    public void EveryKey_IsTranslated_WithTheSamePlaceholders_AndNoPortugueseInEnglish()
    {
        var problems = new List<string>();
        foreach (var key in UiLocalizationService.Keys)
        {
            var pt = UiLocalizationService.TextFor("pt-BR", key);
            var en = UiLocalizationService.TextFor("en-US", key);
            if (string.IsNullOrWhiteSpace(pt) || string.IsNullOrWhiteSpace(en)) problems.Add($"{key}: empty");
            if (pt == en && !SameInBothLanguages.Contains(key)) problems.Add($"{key}: PT-BR is the English text");
            if (!Placeholders(pt).SequenceEqual(Placeholders(en))) problems.Add($"{key}: placeholders differ");
            if (LooksPortuguese(en)) problems.Add($"{key}: English text is Portuguese: {en}");
        }
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("Win.TrayShow", "Show RHI")]
    [InlineData("Win.TrayQuit", "Quit")]
    [InlineData("Win.PickD3DCompiler47", "Select Microsoft D3DCompiler_47.dll")]
    [InlineData("Win.PickReShadeCustomDll", "Select a custom ReShade DLL")]
    [InlineData("Win.PickDeepFriedChickenPackage", "Select the official Deep Fried Chicken package")]
    [InlineData("Win.RenoDxCompatibilityTitle", "RenoDX — Compatibility — {0}")]
    [InlineData("Win.OptiScalerAdvancedTitle", "OptiScaler — Advanced — {0}")]
    [InlineData("Win.ShaderTexturesTitle", "Shaders/Textures — {0}")]
    [InlineData("Ui.OptionAutomatic", "Automatic")]
    [InlineData("Win.ProviderOther", "Other")]
    [InlineData("Ui.SuperResolution", "Super resolution")]
    public void ListedMessages_AreEnglishInTheEnglishUi(string key, string english)
    {
        Assert.Equal(english, UiLocalizationService.TextFor("en-US", key));
        Assert.NotEqual(english, UiLocalizationService.TextFor("pt-BR", key));
    }

    [Fact]
    public void Format_UsesTheChosenUiLanguage_NotTheSystemCulture()
        => Assert.StartsWith("Algo deu errado: 1,5", UiLocalizationService.FormatFor("pt-BR", "Ui.UnexpectedError", 1.5, "/log"));

    [Fact]
    public void PortugueseOnlyPlatformMessages_AreEnglishInTheEnglishUi_AndUnchangedInPtBr()
    {
        var problems = new List<string>();
        foreach (var (ptTemplate, enTemplate) in PlatformMessageTranslations.PortugueseOnly)
        {
            var holes = Placeholders(ptTemplate);
            if (!holes.SequenceEqual(Placeholders(enTemplate))) { problems.Add($"placeholders differ: {ptTemplate}"); continue; }
            if (LooksPortuguese(enTemplate)) problems.Add($"English is Portuguese: {enTemplate}");
            var args = holes.Select(i => (object)$"Arg{i}x").ToArray();
            var pt = string.Format(ptTemplate, args);
            var en = string.Format(enTemplate, args);
            var presented = UiLocalizationService.PresentFor("en-US", pt);
            if (presented != en) problems.Add($"EN UI shows '{presented}' for '{pt}'");
            if (UiLocalizationService.PresentFor("pt-BR", pt) != pt) problems.Add($"PT-BR UI changed '{pt}'");
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void NestedPortugueseMessages_AreTranslatedInside()
    {
        var pt = "Não foi possível instalar automaticamente a dependência ReShade exigida por ReLimiter: "
                 + "dxgi.dll já existe e é externo. O RHI não sobrescreve nem adota uma DLL proxy existente; escolha outro nome.";
        Assert.Equal("Could not automatically install the dependency ReShade required by ReLimiter: "
                     + "dxgi.dll already exists and is external. RHI does not overwrite or adopt an existing proxy DLL; choose another name.",
            UiLocalizationService.PresentFor("en-US", pt));
    }

    /// <summary>Review m3: templates match whole messages only, so a name containing a known phrase is left alone.</summary>
    [Theory]
    [InlineData("Installed ReShade for 'Atualização disponível: 1.0 → 2.0.'")]
    [InlineData("Installed ReShade for 'Proxy não suportado: Remastered.'")]
    [InlineData("Installed ReShade in /games/Proxy não suportado: x./bin")]
    public void NameContainingAKnownPhrase_IsLeftAlone(string text)
        => Assert.Equal(text, UiLocalizationService.PresentFor("en-US", text));

    [Fact]
    public void KnownPhraseInsideAHole_IsCarriedOverVerbatim()
        => Assert.Equal("Invalid GPU generation: Jogo 'Atualização disponível: 1.0 → 2.0.'.",
            UiLocalizationService.PresentFor("en-US", "Geração de GPU inválida: Jogo 'Atualização disponível: 1.0 → 2.0.'."));

    [Fact]
    public void SameRowNestedInItself_IsTranslatedAtEveryLevel()
    {
        var pt = "Não foi possível instalar automaticamente a dependência ReShade exigida por ReLimiter: "
                 + "Não foi possível instalar automaticamente a dependência D3DCompiler exigida por ReShade: Proxy não suportado: dinput8.dll.";
        Assert.Equal("Could not automatically install the dependency ReShade required by ReLimiter: "
                     + "Could not automatically install the dependency D3DCompiler required by ReShade: Proxy not supported: dinput8.dll.",
            UiLocalizationService.PresentFor("en-US", pt));
    }

    [Fact]
    public void EveryTemplate_HasAMatchTimeout()
        => Assert.All(UiLocalizationService.PortugueseOnlyTemplateTimeouts, timeout =>
        {
            Assert.NotEqual(Regex.InfiniteMatchTimeout, timeout);
            Assert.True(timeout <= TimeSpan.FromSeconds(1));
        });

    /// <summary>#143: the real dependency-failure path (ReLimiter needs ReShade; ReShade cannot be fetched offline).</summary>
    [Fact]
    public async Task ReLimiterDependencyFailure_IsEnglishInTheEnglishUi()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        game.PrefixPath = Path.Combine(fx.Root, "pfx");
        Directory.CreateDirectory(Path.Combine(game.PrefixPath, "drive_c"));

        var result = await fx.Managed.InstallAsync(game, ManagedComponentKind.ReLimiter, [game.ExecutablePath!]);

        Assert.False(result.Success);
        Assert.StartsWith("Não foi possível instalar automaticamente a dependência ReShade exigida por ReLimiter", result.Message);
        var english = UiLocalizationService.PresentFor("en-US", result.Message);
        Assert.StartsWith("Could not automatically install the dependency ReShade required by ReLimiter", english);
        Assert.False(LooksPortuguese(english), english);
    }
}
