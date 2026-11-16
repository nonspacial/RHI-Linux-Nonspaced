using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/extras-fg-2 (#281, upstream 2.8.9–2.9.4): 20/30 FG Unlock and Universal MFG Unlocker are offered on every graphics
/// card, as upstream's Extras rows are (upstream reads the card only for HSF and the BFI presenter). The mod 0.4.x finds
/// an RTX 20 or 30 card itself, so RTX 30 owners are no longer sent to Universal MFG, and neither row's note names a card
/// generation the user never picked.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class FgUnlockGpuGenerationTests
{
    public static TheoryData<RtxGeneration> Cards => new() { RtxGeneration.Rtx20, RtxGeneration.Rtx30, RtxGeneration.Rtx40, RtxGeneration.Rtx50, RtxGeneration.Unknown };

    /// <summary>A Proton DX12 x64 game with its own Streamline DLSS Frame Generation (what both unlocks need).</summary>
    private static DetectedGame StreamlineFgGame(ViewModelFixture fx, string name)
    {
        var bin = Path.Combine(fx.Root, "games", name, "bin");
        var prefix = Path.Combine(fx.Root, "games", name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
        File.WriteAllBytes(Path.Combine(bin, "nvngx_dlssg.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(bin, "sl.interposer.dll"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        return new DetectedGame
        {
            Name = name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
            PrefixPath = prefix, Runner = "Wine", Source = "Test", GraphicsApi = GraphicsApi.DirectX12
        };
    }

    private static IReadOnlyList<ManagedComponent> Decorated(ViewModelFixture fx, DetectedGame game)
    {
        var files = Directory.EnumerateFiles(game.InstallPath!, "*", SearchOption.AllDirectories).ToArray();
        var components = ComponentProbe.Probe(game, files);
        fx.Managed.Decorate(game, components);
        return components;
    }

    [Theory]
    [MemberData(nameof(Cards))]
    public async Task BothUnlocks_AreOfferedOnEveryCard_WithNotesThatNameNoCardGeneration(RtxGeneration card)
    {
        using var fx = await ViewModelFixture.StartAsync([], nvidiaGpu: () => NrGpuTestKit.Facts(card));
        var game = StreamlineFgGame(fx, "Card " + (int)card);
        // An RTX 30 owner's saved choice from before #281 (the old ⚙ offered "RTX 30 Series").
        if (card == RtxGeneration.Rtx30)
            Assert.True(fx.Managed.SetDlssg2030Preferences(game, Dlssg2030UnlockDeploymentService.GpuGenRtx30, "version.dll").Success);

        var components = Decorated(fx, game);
        var fg2030 = components.Single(c => c.Kind == ManagedComponentKind.Dlssg2030Unlock);
        var umfg = components.Single(c => c.Kind == ManagedComponentKind.Rtx40MfgStandalone);

        Assert.Equal(ComponentAvailability.Available, fg2030.Availability);
        Assert.True(fg2030.ActionAllowed, fg2030.Note);
        Assert.Equal(Dlssg2030UnlockDeploymentService.AvailableNotePrefix + "version.dll", fg2030.Note);
        Assert.Equal(ComponentAvailability.Available, umfg.Availability);
        Assert.True(umfg.ActionAllowed, umfg.Note);
        Assert.Equal(Rtx40MfgStandaloneDeploymentService.AvailableNotePrefix + "version.dll", umfg.Note);
        foreach (var note in new[] { fg2030.Note!, umfg.Note! })
        {
            Assert.DoesNotContain("RTX 40 ·", note, StringComparison.Ordinal);
            Assert.DoesNotContain("RTX 30 ·", note, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legado", note, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Notes_SayUpstreamsCardsForEachUnlock()
    {
        // Upstream 2.9.4's tooltips: 20/30 FG Unlock on RTX 20 and 30; Universal MFG on RTX 40 and 30 (RTX 20
        // experimental) plus XeMFG on any GPU.
        Assert.Contains("RTX 20 and 30", Dlssg2030UnlockDeploymentService.AvailableNotePrefix, StringComparison.Ordinal);
        Assert.Contains("detects the card itself", Dlssg2030UnlockDeploymentService.AvailableNotePrefix, StringComparison.Ordinal);
        Assert.Contains("RTX 40 and RTX 30", Rtx40MfgStandaloneDeploymentService.AvailableNotePrefix, StringComparison.Ordinal);
        Assert.Contains("RTX 20 experimental", Rtx40MfgStandaloneDeploymentService.AvailableNotePrefix, StringComparison.Ordinal);
        Assert.Contains("any GPU", Rtx40MfgStandaloneDeploymentService.AvailableNotePrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_HavePortugueseText()
    {
        foreach (var en in new[]
                 {
                     Dlssg2030UnlockDeploymentService.AvailableNotePrefix, Rtx40MfgStandaloneDeploymentService.AvailableNotePrefix
                 })
        {
            var pt = UiLocalizationService.PresentFor("pt-BR", en + "version.dll");
            Assert.NotEqual(en + "version.dll", pt);
            Assert.EndsWith("version.dll", pt, StringComparison.Ordinal);
            Assert.Equal(en + "version.dll", UiLocalizationService.PresentFor("en-US", pt));
        }
    }

    [Fact]
    public async Task InstalledRow_AnOldRtx20Record_SaysManagedWithItsVersion_NotACardGeneration()
    {
        using var fx = await ViewModelFixture.StartAsync([], nvidiaGpu: () => NrGpuTestKit.Facts(RtxGeneration.Rtx30));
        var game = StreamlineFgGame(fx, "Installed");
        var proxy = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.WriteAllBytes(proxy, [9, 9]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.Dlssg2030Unlock,
            BinaryDirectory = game.BinaryDirectory!, Version = "0.4.2",
            Files = [new LinuxDeployedFile { Path = proxy, Sha256 = "00" }],
            Metadata = new() { ["FG2030.ProxyName"] = "winmm.dll", ["FG2030.GpuGeneration"] = Dlssg2030UnlockDeploymentService.GpuGenRtx20 }
        });

        var row = Decorated(fx, game).Single(c => c.Kind == ManagedComponentKind.Dlssg2030Unlock);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.True(row.ActionAllowed);
        Assert.Equal("Managed by RHI Linux · 0.4.2.", row.Note);
    }
}
