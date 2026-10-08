using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// Final ui-stores review, findings 2–4 and 8: every line that embeds an exception's or a platform service's text shows
/// it through the UI language's presentation — the store-save helper (TryPersist), Clear cache, the folder and link
/// openers, the update history and HDR mods dialogs, the scan's problems line — so a PT-BR UI never shows a raw English
/// sentence. Samples are the producers' own texts (constants where they exist) with neutral paths.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ErrorPresentationTests
{
    private const string P = "/jogos/Exemplo/biblioteca.json";

    public static TheoryData<string> StoreSaveErrors() => new()
    {
        $"Access to the path '{P}.rhi-tmp-0a1b' is denied.",
        AtomicFile.VerifyFailurePrefix + P,
        AtomicFile.VerifyFailurePrefix + P + AtomicFile.TempLeftoverFragment + P + ".rhi-tmp-0a1b",
        AtomicFile.NoFolderPrefix + P,
        $"{P} changed while it was being read. Nothing was changed; try again.",
        $"No space left on device : '{P}'",
    };

    [Theory]
    [MemberData(nameof(StoreSaveErrors))]
    public void TryPersistRefusal_InPortuguese_PresentsTheReason(string error)
    {
        foreach (var sessionOnly in new[] { true, false })
        {
            var shown = InPortuguese(() => MainWindowViewModel.SaveFailedText(new IOException(error), "Ui.WhatGameLibrary", sessionOnly));
            AssertPortuguese(shown);
            Assert.StartsWith("Não foi possível salvar a biblioteca de jogos: ", shown);
        }
    }

    [Fact]
    public async Task RefusedFavouriteSave_InPortuguese_ShowsNoEnglish()
    {
        using var fx = await ViewModelFixture.StartAsync([], "pt-BR");
        UiLocalizationService.Apply("pt-BR");
        try
        {
            var game = fx.Game("Exemplo");
            var (added, message) = await fx.Vm.TryAddManualGameAsync(new() { Name = game.Name, InstallPath = game.InstallPath, Runner = "Manual Windows" });
            Assert.True(added, message);
            var mode = File.GetUnixFileMode(fx.DataDir);
            File.SetUnixFileMode(fx.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { fx.Vm.ToggleFavouriteCommand.Execute(null); }
            finally { File.SetUnixFileMode(fx.DataDir, mode); }

            Assert.StartsWith("Não foi possível salvar a biblioteca de jogos: Acesso ao caminho '", fx.Vm.StatusText);
            Assert.DoesNotContain("Access to the path", fx.Vm.StatusText);
            Assert.DoesNotContain(" is denied", fx.Vm.StatusText);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void StoreSaveFailedText_InPortuguese_PresentsTheReason()
        => AssertPortuguese(InPortuguese(() => MainWindowViewModel.StoreSaveFailedText(new IOException(AtomicFile.NoFolderPrefix + P))));

    [Fact]
    public void CacheNotCleared_InPortuguese_PresentsTheReason()
    {
        var shown = InPortuguese(() => MainWindowViewModel.CacheNotClearedText(new UnauthorizedAccessException($"Access to the path '{P}' is denied.")));

        Assert.Equal($"O cache não foi totalmente limpo: Acesso ao caminho '{P}' negado.", shown);
    }

    public static TheoryData<string> OpenerErrors() => new()
    {
        LinuxDesktopOpener.DirectoryMissingPrefix + "/jogos/Exemplo",
        LinuxDesktopOpener.NoOpenerMessage,
        $"Access to the path '{P}' is denied.",
        $"Could not find a part of the path '{P}'.",
    };

    [Theory]
    [MemberData(nameof(OpenerErrors))]
    public void FolderOpenFailures_InPortuguese_PresentTheReason(string error)
    {
        AssertPortuguese(InPortuguese(() => ErrorLineText.FolderNotOpened(error)));
        AssertPortuguese(InPortuguese(() => ErrorLineText.CustomShadersNotOpened(error)));
        AssertPortuguese(InPortuguese(() => ErrorLineText.CustomAddonsNotOpened(error)));
    }

    [Fact]
    public void LinkOpenFailure_InPortuguese_PresentsTheReason()
    {
        AssertPortuguese(InPortuguese(() => ErrorLineText.LinkNotOpened("ftp://exemplo", LinuxDesktopOpener.UnsupportedLinkPrefix + "ftp://exemplo")));
        AssertPortuguese(InPortuguese(() => ErrorLineText.LinkNotOpened("https://exemplo", LinuxDesktopOpener.NoOpenerMessage)));
        Assert.Equal("Não foi possível abrir https://exemplo", InPortuguese(() => ErrorLineText.LinkNotOpened("https://exemplo", null)));
    }

    [Fact]
    public void UpdateHistoryErrors_InPortuguese_ArePresented()
    {
        AssertPortuguese(InPortuguese(() => ErrorLineText.Of(new IOException($"{ComponentUpdateLogStore.UnreadablePrefix}{P}{ComponentUpdateLogStore.UnreadableSuffix}"))));
        AssertPortuguese(InPortuguese(() => ErrorLineText.Of(new IOException($"{P} changed while it was being read. Nothing was changed; try again."))));
    }

    [Fact]
    public void ScanProblems_InPortuguese_PresentDetectorAndPolicyErrors()
    {
        var scan = new GameLibraryScanResult([],
            [new DetectorScanStatus("Steam", 0, AtomicFile.NoFolderPrefix + P, [])],
            [new PolicyScanStatus("Outra política", "Upstream RHI manifest unavailable: E/S negada")]);

        var shown = InPortuguese(() => LibraryScanStatusText.Problems(scan, null) ?? string.Empty);

        Assert.Contains("Não foi possível determinar a pasta deste caminho: ", shown);
        Assert.Contains("Manifesto do RHI upstream indisponível: ", shown);
        Assert.DoesNotContain(AtomicFile.NoFolderPrefix, shown);
        Assert.DoesNotContain("Upstream RHI manifest unavailable", shown);
    }

    /// <summary>
    /// No window line embeds an exception's or opener's text without the presentation: <c>ex.Message</c>, an opener's
    /// <c>error</c> out-value handed to a format, or assigned to a text, must go through <c>Present</c> (directly or via
    /// <see cref="ErrorLineText"/>). Assigning the opener's out parameter itself is the one allowed raw use.
    /// </summary>
    [Fact]
    public void DesktopSources_NeverShowRawErrorText()
    {
        var desktop = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop");
        var raw = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (file: Path.GetFileName(f), line, i)))
            .Where(x => RawError.IsMatch(x.line) && !x.line.Contains("Present(", StringComparison.Ordinal)
                        && !AllowedRaw.IsMatch(x.line))
            .Select(x => $"{x.file}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(raw);
    }

    private static readonly Regex RawError = new(@"\bex\.Message\b|\berror\.Message\b|(TF|Format)\(.*\berror\)|Text\s*=\s*error\b");
    private static readonly Regex AllowedRaw = new(@"^\s*error = ex\.Message;|Log\.TryWrite|AppLog\.");

    private static string InPortuguese(Func<string> build)
    {
        UiLocalizationService.Apply("pt-BR");
        try { return build(); }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static void AssertPortuguese(string shown)
    {
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), shown);
        var english = UiLocalizationService.EnglishMarkersIn(shown).Where(w => !w.Equals("no", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(english.Length == 0, $"English left ({string.Join(", ", english)}): {shown}");
    }
}
