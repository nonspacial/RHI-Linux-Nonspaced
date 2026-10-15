using System.Net;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #186: the ReShade profile's Legacy version is a dropdown of the RHI manifest's versions; a game the manifest pins
/// (Max Payne 3 → 6.4.1) starts on Legacy at that version until ReShade is installed, and shows the manifest's ReShade note.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ReShadeLegacyProfileTests
{
    private const string Manifest = """
        { "version": 3, "legacyReShadeAvailable": ["6.7.3", "6.6.2", "6.0.0"] }
        """;

    [Theory]
    [InlineData("en-US", "Open link")]
    [InlineData("pt-BR", "Abrir link")]
    public async Task PinnedGame_StartsOnLegacyAtThePin_TheListIsTheManifests_AndTheNoteShows(string language, string linkLabel)
    {
        var game = Game("Max Payne 3");
        game.ManifestLegacyReShadeVersion = "6.4.1";
        game.ManifestReShadeNote = "  ReShade 6.4.1 is required for RenoDX.  ";
        game.ManifestReShadeNoteUrl = "https://github.com/rhi-tests/notes";
        using var fx = await ViewModelFixture.StartAsync([game], language, httpResponse: ServeManifest);
        try
        {
            if (language == "pt-BR") UiLocalizationService.Apply("pt-BR");
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
            var legacy = fx.Vm.ReShadeLegacy;
            await legacy.Loaded.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(fx.Vm.IsReShadeLegacy);
            Assert.Equal("6.4.1", fx.Vm.ReShadeLegacyVersion);
            // The profile line says what a fresh install will use, not the Stable default.
            Assert.Contains("6.4.1", fx.Vm.ReShadeUpdateText, StringComparison.Ordinal);
            // The manifest's versions, plus the pin it does not list (the selection always has its option).
            Assert.Equal(["6.7.3", "6.6.2", "6.0.0", "6.4.1"], legacy.Versions);
            Assert.Equal("ReShade 6.4.1 is required for RenoDX.", legacy.Note);
            Assert.True(legacy.HasNote);
            Assert.True(legacy.HasNoteLink);
            Assert.Equal(linkLabel, legacy.NoteLinkLabel);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    [Fact]
    public async Task APinTheListLacks_IsOfferedBeforeTheProfileSelectsIt()
    {
        var first = Game("First Game");
        var pinned = Game("Older Pin Game");
        pinned.ManifestLegacyReShadeVersion = "6.1.1"; // not in the manifest's list
        using var fx = await ViewModelFixture.StartAsync([first, pinned], httpResponse: ServeManifest);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == first.Name).Game;
        var legacy = fx.Vm.ReShadeLegacy;
        await legacy.Loaded.WaitAsync(TimeSpan.FromSeconds(30));
        var offeredWhenSelected = false;
        fx.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(fx.Vm.ReShadeLegacyVersion) && fx.Vm.ReShadeLegacyVersion == "6.1.1")
                offeredWhenSelected = legacy.Versions.Contains("6.1.1");
        };

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == pinned.Name).Game;

        Assert.Equal("6.1.1", fx.Vm.ReShadeLegacyVersion);
        Assert.True(offeredWhenSelected); // Avalonia drops a selection whose option is missing
    }

    [Fact]
    public async Task UnpinnedGame_KeepsTheGlobalChannel_AndHidesTheNote_AndANonHttpsLinkIsNotOffered()
    {
        var plain = Game("Plain Game");
        var oddLink = Game("Odd Link Game");
        oddLink.ManifestReShadeNote = "Note.";
        oddLink.ManifestReShadeNoteUrl = "file:///etc/passwd";
        using var fx = await ViewModelFixture.StartAsync([plain, oddLink], httpResponse: ServeManifest);

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == plain.Name).Game;
        var legacy = fx.Vm.ReShadeLegacy;
        await legacy.Loaded.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(fx.Vm.IsReShadeLegacy);
        Assert.False(legacy.HasNote);
        Assert.Equal("6.4.1", fx.Vm.ReShadeLegacyVersion);
        Assert.Contains("6.4.1", legacy.Versions);

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == oddLink.Name).Game;
        Assert.True(legacy.HasNote);
        Assert.False(legacy.HasNoteLink);
        // w5 integration: an https link to a site manifest notes never use is not offered either (LinkHosts.ManifestNote).
        fx.Vm.SelectedGame!.ManifestReShadeNoteUrl = "https://evil.example/notes";
        Assert.False(legacy.HasNoteLink);
        fx.Vm.SelectedGame!.ManifestReShadeNoteUrl = "https://github.com/rhi-tests/notes";
        Assert.True(legacy.HasNoteLink);
    }

    [Fact]
    public async Task ChoosingAListedVersion_IsWhatApplyInstalls()
    {
        var game = Game("Choice Game");
        using var fx = await ViewModelFixture.StartAsync([game], httpResponse: ServeManifest);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
        var legacy = fx.Vm.ReShadeLegacy;
        await legacy.Loaded.WaitAsync(TimeSpan.FromSeconds(30));

        fx.Vm.ReShadeLegacyVersion = legacy.Versions[0];
        // A refill (the list read again) keeps the chosen version selected.
        legacy.Offer(fx.Vm.ReShadeLegacyVersion);

        Assert.Equal("6.7.3", fx.Vm.ReShadeLegacyVersion);
        Assert.Equal("6.7.3", legacy.Versions[0]);
    }

    private static HttpResponseMessage? ServeManifest(HttpRequestMessage request)
        => request.RequestUri!.AbsoluteUri == UpstreamRhiManifestService.LiveManifestUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest) }
            : null;

    private static DetectedGame Game(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-legacy-{Guid.NewGuid():N}");
        var bin = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        return new DetectedGame
        {
            Name = name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix, Runner = "Proton", Source = "Heroic",
            GraphicsApi = GraphicsApi.DirectX11
        };
    }
}
