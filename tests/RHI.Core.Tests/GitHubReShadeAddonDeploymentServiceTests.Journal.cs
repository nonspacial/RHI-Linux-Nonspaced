using System.Net;
using System.Security.Cryptography;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: GitHub ReShade add-ons (ReLimiter, Display Commander, and the manifest add-on packs built on them) journal
// their files (#23); a failure after the copy no longer leaves an add-on without a record (#31).
public sealed partial class GitHubReShadeAddonDeploymentServiceTests
{
    private (DetectedGame Game, string AddonDir, HttpClient Http) ReLimiterGame()
    {
        var game = CreateGame();
        var addonDir = game.BinaryDirectory!;
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri.Contains("api.github")
            ? JsonResponse(json)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        return (game, addonDir, http);
    }

    [Fact]
    public async Task Install_RecordSaveFails_RemovesTheAddonAndTheConfigItCreated()
    {
        var (game, addonDir, http) = ReLimiterGame();
        using var _ = http;
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "save-fails.json"));
        store.SaveFaultForTesting = () => new IOException("disk full");

        var install = await NewReLimiter(http, store).InstallAsync(game);

        Assert.False(install.Success);
        Assert.False(File.Exists(Path.Combine(addonDir, "relimiter.addon64"))); // used to stay, unowned
        Assert.False(File.Exists(Path.Combine(addonDir, "relimiter.ini")));
        Assert.Empty(Directory.GetFiles(addonDir, "*.rhi-*"));
    }

    [Fact]
    public async Task Install_CrashWhileSavingTheRecord_NextStartRemovesTheAddonAndTheConfig()
    {
        var (game, addonDir, http) = ReLimiterGame();
        using var _ = http;
        var ledger = Path.Combine(_root, "crash-install.json");
        var store = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(_root, "journal-install");
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, store);
        using var death = new SimulatedDeath(journal);
        store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => NewReLimiter(http, store, journal).InstallAsync(game));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(addonDir, "relimiter.addon64")));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(Path.Combine(addonDir, "relimiter.addon64")));
        Assert.False(File.Exists(Path.Combine(addonDir, "relimiter.ini")));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsTheAddonBack()
    {
        var (game, addonDir, http) = ReLimiterGame();
        using var _ = http;
        var ledger = Path.Combine(_root, "crash-remove.json");
        var store = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(_root, "journal-remove");
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, store);
        var service = NewReLimiter(http, store, journal);
        Assert.True((await service.InstallAsync(game)).Success);
        var addon = Path.Combine(addonDir, "relimiter.addon64");
        var bytes = File.ReadAllBytes(addon);
        using var death = new SimulatedDeath(journal);
        // The first save is the removal's stamp; the process dies at the second (taking the record away).
        var saves = 0;
        store.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null;

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(game));
        Assert.True(died);
        Assert.False(File.Exists(addon));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(addon));
        Assert.NotNull(new LinuxDeploymentRecordStore(ledger).Find(game, ManagedComponentKind.ReLimiter));
    }

}
