using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #243 (upstream 2.8.8 <c>49d4473</c>): manual Update All refreshes ReShade without a check (#113), so a game on the Custom
/// channel whose chosen DLL was removed failed its refresh. It is now checked first — that check is local — and reported
/// as "could not be checked" with the reason; every other channel still refreshes unchecked.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CustomReShadeUpdateAllTests
{
    private const string SourceGone = "Custom ReShade source file is unavailable; installed DLL remains untouched.";

    [Fact]
    public async Task CustomSourceGone_IsCouldNotBeChecked_WithItsReason_InBothModes_NotAFailedRefresh()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root);
        var record = Record(game, nameof(ReShadeChannel.Custom));
        record.Metadata["ReShade.CustomSource"] = Path.Combine(fx.Root, "Custom", "ReShade64.dll"); // removed by the user
        fx.Records.Upsert(record);

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], mode);

            Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
            var item = Assert.Single(result.Items);
            Assert.True(item.Unchecked);
            Assert.Equal(SourceGone, item.Message);
        }
        Assert.Equal("1.0", fx.Records.Find(game, ManagedComponentKind.ReShade)!.Version);
    }

    [Theory]
    [InlineData(nameof(ReShadeChannel.Custom), true)]
    [InlineData(nameof(ReShadeChannel.Stable), false)]
    [InlineData(nameof(ReShadeChannel.Nightly), false)]
    [InlineData(nameof(ReShadeChannel.NoAddons), false)]
    [InlineData(nameof(ReShadeChannel.Legacy), false)]
    public async Task OnlyCustomReShade_IsCheckedBeforeTheManualRefresh(string channel, bool checkedFirst)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root);
        fx.Records.Upsert(Record(game, channel));

        Assert.Equal(checkedFirst, fx.Managed.ChecksBeforeManualRefresh(game, ManagedComponentKind.ReShade));
        Assert.False(fx.Managed.ChecksBeforeManualRefresh(game, ManagedComponentKind.RenoDX));
    }

    [Theory]
    [InlineData(true, false, false, true)] // Custom source still there and unchanged: refreshed as before
    [InlineData(true, true, false, true)] // the source changed: refreshed
    [InlineData(true, false, true, false)] // the check could not tell: not refreshed, counted as not checked
    [InlineData(false, false, true, true)] // Stable/Nightly (not checked first): refreshed unchecked, as since #113
    public async Task ManualRefresh_IsStoppedOnlyByACheckThatCouldNotTell(bool checkFirst, bool update, bool couldNotCheck, bool refreshed)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-custom-ua-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var game = Game(root);
            var records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
            records.Upsert(Record(game, nameof(ReShadeChannel.Custom)));
            var fake = new FakeBackend(records, checkFirst, new ManagedUpdateCheck(true, update, SourceGone, CouldNotCheck: couldNotCheck));

            var result = await new LinuxUpdateAllService(records, fake, new NeverRunning()).RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

            Assert.Equal(checkFirst ? 1 : 0, fake.Checks);
            Assert.Equal(refreshed ? [ManagedComponentKind.ReShade] : [], fake.Updates);
            Assert.Equal(refreshed ? (1, 0, 0) : (0, 0, 1), (result.ChangedCount, result.FailedCount, result.UncheckedCount));
            if (!refreshed) Assert.Equal(SourceGone, Assert.Single(result.Items).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("en-US", "Update All: 0 updated · 0 current · 0 deferred · 0 failed · 1 could not be checked.", SourceGone)]
    [InlineData("pt-BR", "Atualizar tudo: 0 atualizado(s) · 0 em dia · 0 adiado(s) · 0 com falha · 1 não verificado(s).",
        "O arquivo de origem do ReShade personalizado não está disponível; a DLL instalada continua intocada.")]
    public void Summary_AndReason_ReadInTheUiLanguage(string language, string summary, string reason)
    {
        var result = new LinuxUpdateAllResult([
            new LinuxUpdateItemResult("Custom Quest", ManagedComponentKind.ReShade, true, false, false, SourceGone, Unchecked: true)
        ]);
        UiLocalizationService.Apply(language);
        try
        {
            Assert.Equal(summary, UiLocalizationService.UpdateAllSummary(result));
            Assert.Equal(reason, UiLocalizationService.Present(SourceGone));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // Correction round 1, review F1: manual Update All names the game and the reason, as it names a failure, instead of a
    // bare count (before #243 the same game was named as a failed update).
    [Theory]
    [InlineData("en-US", "Update All: 0 updated · 0 current · 0 deferred · 0 failed · 1 could not be checked."
        + " Not checked: Custom Quest (ReShade) — Custom ReShade source file is unavailable; installed DLL remains untouched.")]
    [InlineData("pt-BR", "Atualizar tudo: 0 atualizado(s) · 0 em dia · 0 adiado(s) · 0 com falha · 1 não verificado(s)."
        + " Não verificado: Custom Quest (ReShade) — O arquivo de origem do ReShade personalizado não está disponível; a DLL instalada continua intocada.")]
    public async Task ManualUpdateAll_NamesTheGameAndTheReason_InTheUiLanguage(string language, string expected)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-custom-ua-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var game = Game(root);
                using var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language);
                var record = Record(game, nameof(ReShadeChannel.Custom));
                record.Metadata["ReShade.CustomSource"] = Path.Combine(root, "Custom", "ReShade64.dll"); // removed by the user
                fx.Records.Upsert(record);
                fx.Vm.Updates.SetLibraryHasManagedTargets(true);

                await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));

                // (The offline fixture's DLSS manifest notice follows the line.)
                Assert.StartsWith(expected, fx.Vm.StatusText, StringComparison.Ordinal);
            }
            finally { Directory.Delete(root, true); }
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // Only a check that is local (ChecksBeforeManualRefresh) gives its "could not check" a reason worth naming; one that
    // could not reach its source (another family, background pass) stays in the count.
    [Theory]
    [InlineData(true, LinuxUpdateRunMode.Manual, true)]
    [InlineData(true, LinuxUpdateRunMode.Background, true)]
    [InlineData(false, LinuxUpdateRunMode.Background, false)]
    public async Task OnlyALocalCheckThatCouldNotTell_IsNamedWithItsReason(bool localCheck, LinuxUpdateRunMode mode, bool named)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-custom-ua-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var game = Game(root);
            var records = new LinuxDeploymentRecordStore(Path.Combine(root, "deployments.json"));
            records.Upsert(Record(game, nameof(ReShadeChannel.Custom)));
            var fake = new FakeBackend(records, localCheck, new ManagedUpdateCheck(true, false, SourceGone, CouldNotCheck: true));

            var result = await new LinuxUpdateAllService(records, fake, new NeverRunning()).RunAsync([game], _ => [], mode);

            var item = Assert.Single(result.Items);
            Assert.True(item.Unchecked);
            Assert.Equal(named, item.LocalReason);
            Assert.Equal(named ? 1 : 0, result.LocalUncheckedItems.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    // Each such item is named, after the failures, with the reason in the UI language; a remote one stays a count.
    [Theory]
    [InlineData("en-US", "Update All: 0 updated · 0 current · 0 deferred · 1 failed · 3 could not be checked."
        + " Failed: Alpha (RenoDX HDR) — Game executable was not detected."
        + " Not checked: Beta (ReShade) — Custom ReShade source file is unavailable; installed DLL remains untouched."
        + " Not checked: Delta (ReShade) — Custom ReShade source file is unavailable; installed DLL remains untouched.")]
    [InlineData("pt-BR", "Atualizar tudo: 0 atualizado(s) · 0 em dia · 0 adiado(s) · 1 com falha · 3 não verificado(s)."
        + " Falha: Alpha (RenoDX HDR) — O executável do jogo não foi detectado."
        + " Não verificado: Beta (ReShade) — O arquivo de origem do ReShade personalizado não está disponível; a DLL instalada continua intocada."
        + " Não verificado: Delta (ReShade) — O arquivo de origem do ReShade personalizado não está disponível; a DLL instalada continua intocada.")]
    public void Summary_NamesEachLocalUncheckedItem_AfterTheFailures(string language, string expected)
    {
        var result = new LinuxUpdateAllResult([
            new LinuxUpdateItemResult("Alpha", ManagedComponentKind.RenoDX, false, false, false, "Game executable was not detected."),
            new LinuxUpdateItemResult("Beta", ManagedComponentKind.ReShade, true, false, false, SourceGone, Unchecked: true, LocalReason: true),
            new LinuxUpdateItemResult("Gamma", ManagedComponentKind.ReLimiter, true, false, false, "GitHub could not be reached.", Unchecked: true),
            new LinuxUpdateItemResult("Delta", ManagedComponentKind.ReShade, true, false, false, SourceGone, Unchecked: true, LocalReason: true)
        ]);

        Assert.Equal(expected, UiLocalizationService.UpdateAllSummaryFor(language, result));
    }

    private static DetectedGame Game(string root)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "games", "Custom Quest")).FullName;
        var exe = Path.Combine(dir, "CustomQuest.exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        return new DetectedGame
        {
            Name = "Custom Quest", Source = "Manual", Runner = "Manual Windows", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            GraphicsApi = GraphicsApi.DirectX11
        };
    }

    private static LinuxDeploymentRecord Record(DetectedGame game, string channel) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
        BinaryDirectory = game.BinaryDirectory!, Version = "1.0", Metadata = { ["ReShade.Channel"] = channel }
    };

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    private sealed class FakeBackend(LinuxDeploymentRecordStore records, bool checkFirst, ManagedUpdateCheck answer) : ILinuxManagedUpdateBackend
    {
        public int Checks { get; private set; }
        public List<ManagedComponentKind> Updates { get; } = [];

        public bool ChecksBeforeManualRefresh(DetectedGame game, ManagedComponentKind component) => checkFirst;

        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            Checks++;
            return Task.FromResult(answer);
        }

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            Updates.Add(component);
            var record = records.Find(game, component)!;
            record.Version += "-new";
            records.Upsert(record);
            return Task.FromResult(ComponentOperationResult.Ok("refreshed"));
        }
    }
}
