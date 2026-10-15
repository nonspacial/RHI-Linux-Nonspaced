using System.Reflection;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #168/#169: every service the app builds that can journal (a <c>_journal</c> field) gets the app's journal, and every
/// service that keeps a ledger (<c>_records</c>) gets the app's one store — a service built without them silently loses
/// crash recovery (Luma and the Addon Manager were built without a journal).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class JournalWiringTests
{
    [Fact]
    public async Task EveryJournalingService_TheAppBuilds_HasTheAppsJournalAndLedger()
    {
        using var http = new HttpClient(new Offline());
        var records = new LinuxDeploymentRecordStore();
        var wineOverrides = new WineDllOverrideService();
        var journal = LinuxDeploymentRecovery.CreateJournal(wineOverrides);
        var services = RHI.Desktop.AppComposition.Create(http, records, wineOverrides, journal);
        using var vm = services.CreateViewModel();
        await vm.Initialization.WaitAsync(TimeSpan.FromSeconds(60));

        var objects = RepairStoreWiringTests.Reachable(new object[] { services, vm }).ToList();
        var withJournal = new List<string>();
        var missing = new List<string>();
        foreach (var item in objects)
        {
            foreach (var field in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.Name == "_journal" && field.FieldType == typeof(DeploymentJournal))
                {
                    withJournal.Add(item.GetType().Name);
                    if (!ReferenceEquals(field.GetValue(item), journal)) missing.Add(item.GetType().Name + "._journal");
                }
                if (field.Name == "_records" && field.FieldType == typeof(LinuxDeploymentRecordStore)
                    && !ReferenceEquals(field.GetValue(item), records))
                    missing.Add(item.GetType().Name + "._records");
            }
        }
        Assert.True(missing.Count == 0, "not wired: " + string.Join(", ", missing.Distinct()));
        Assert.Contains(nameof(LumaFrameworkDeploymentService), withJournal);
        Assert.Contains(nameof(ReShadeAddonManagerService), withJournal);
        Assert.Contains(nameof(Dlss5FeederFullDeploymentService), withJournal);
        Assert.Contains(nameof(Dx9TranslationReShadeCoordinator), withJournal);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline (test)");
    }
}
