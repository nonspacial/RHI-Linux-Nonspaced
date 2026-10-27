using System.Collections.Concurrent;
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
    [InlineData(false)] // Custom source still there and unchanged: refreshed as before
    [InlineData(true)] // the source changed: refreshed
    public async Task ManualRefresh_CheckedFirst_StillRefreshes_WhenTheCheckCouldTell(bool updateAvailable)
    {
        var fake = new FakeBackend { CheckFirst = true, Answer = new ManagedUpdateCheck(true, updateAvailable, "checked") };
        using var fixture = new LocalFixture();
        var service = new LinuxUpdateAllService(fixture.Records(fake), fake, new NeverRunning());

        var result = await service.RunAsync([fixture.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal([ManagedComponentKind.ReShade], fake.Checks);
        Assert.Equal([ManagedComponentKind.ReShade], fake.Updates);
        Assert.Equal((1, 0, 0), (result.ChangedCount, result.FailedCount, result.UncheckedCount));
    }

    [Fact]
    public async Task ManualRefresh_CheckThatCouldNotTell_IsNotRefreshed_AndCountsAsCouldNotBeChecked()
    {
        var fake = new FakeBackend { CheckFirst = true, Answer = new ManagedUpdateCheck(true, false, SourceGone, CouldNotCheck: true) };
        using var fixture = new LocalFixture();
        var service = new LinuxUpdateAllService(fixture.Records(fake), fake, new NeverRunning());

        var result = await service.RunAsync([fixture.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Empty(fake.Updates);
        Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
        Assert.Equal(SourceGone, Assert.Single(result.Items).Message);
    }

    [Fact]
    public async Task ManualRefresh_NotCheckedFirst_RefreshesWithoutACheck()
    {
        // Stable/Nightly ReShade (the backend's default answer): unchanged since #113, no check before the refresh.
        var fake = new FakeBackend { Answer = new ManagedUpdateCheck(true, false, "never asked", CouldNotCheck: true) };
        using var fixture = new LocalFixture();
        var service = new LinuxUpdateAllService(fixture.Records(fake), fake, new NeverRunning());

        var result = await service.RunAsync([fixture.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Empty(fake.Checks);
        Assert.Equal([ManagedComponentKind.ReShade], fake.Updates);
        Assert.Equal((1, 0, 0), (result.ChangedCount, result.FailedCount, result.UncheckedCount));
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

    private sealed class LocalFixture : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-custom-ua-" + Guid.NewGuid().ToString("N"))).FullName;
        public DetectedGame Game { get; }

        public LocalFixture() => Game = CustomReShadeUpdateAllTests.Game(_root);

        public LinuxDeploymentRecordStore Records(FakeBackend fake)
        {
            var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
            records.Upsert(Record(Game, nameof(ReShadeChannel.Custom)));
            fake.Store = records;
            return records;
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    }

    private sealed class FakeBackend : ILinuxManagedUpdateBackend
    {
        public bool CheckFirst { get; init; }
        public required ManagedUpdateCheck Answer { get; init; }
        public LinuxDeploymentRecordStore? Store { get; set; }
        public ConcurrentQueue<ManagedComponentKind> Checks { get; } = new();
        public ConcurrentQueue<ManagedComponentKind> Updates { get; } = new();

        public bool ChecksBeforeManualRefresh(DetectedGame game, ManagedComponentKind component) => CheckFirst;

        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            Checks.Enqueue(component);
            return Task.FromResult(Answer);
        }

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            Updates.Enqueue(component);
            var record = Store!.Find(game, component)!;
            record.Version += "-new";
            Store.Upsert(record);
            return Task.FromResult(ComponentOperationResult.Ok("refreshed"));
        }
    }
}
