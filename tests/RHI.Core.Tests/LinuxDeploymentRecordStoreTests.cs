using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class LinuxDeploymentRecordStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-record-store-tests-" + Guid.NewGuid().ToString("N"));

    public LinuxDeploymentRecordStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Load_NormalizesMetadataToCaseInsensitiveDictionary()
    {
        var path = Path.Combine(_root, "records.json");
        File.WriteAllText(path, """
[
  {
    "GameKey": "steam:42",
    "GameName": "Game",
    "Component": "ReShade",
    "BinaryDirectory": "/tmp/game",
    "Files": [],
    "DirectoriesCreatedByRhi": [],
    "Metadata": {
      "reshade.channel": "Nightly"
    }
  }
]
""");
        var store = new LinuxDeploymentRecordStore(path);
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", SteamAppId = 42 };

        var record = store.Find(game, ManagedComponentKind.ReShade);

        Assert.NotNull(record);
        Assert.True(record!.Metadata.TryGetValue("ReShade.Channel", out var channel));
        Assert.Equal("Nightly", channel);
    }

    [Fact]
    public void CorruptLedger_IsQuarantinedAndNeverOverwritten()
    {
        var path = Path.Combine(_root, "records.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new LinuxDeploymentRecordStore(path);
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", SteamAppId = 42 };

        Assert.Empty(store.FindAll(game));
        Assert.NotNull(store.CorruptLedgerPath);
        Assert.True(File.Exists(store.CorruptLedgerPath));

        Assert.Throws<InvalidOperationException>(() => store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:42", GameName = "Game", Component = ManagedComponentKind.ReShade, BinaryDirectory = "/tmp/game"
        }));
        Assert.Throws<InvalidOperationException>(() => store.Remove(game, ManagedComponentKind.ReShade));
        Assert.Equal("{ this is not json", File.ReadAllText(path));

        // Repairing the file on disk clears the quarantine without a new store instance.
        File.WriteAllText(path, "[]");
        Assert.Null(store.CorruptLedgerPath);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:42", GameName = "Game", Component = ManagedComponentKind.ReShade, BinaryDirectory = "/tmp/game"
        });
        Assert.Single(store.FindAll(game));
    }

    [Fact]
    public void Find_ReusesParsedLedgerUntilFileChanges()
    {
        var path = Path.Combine(_root, "records.json");
        var store = new LinuxDeploymentRecordStore(path);
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", SteamAppId = 42 };
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:42", GameName = "Game", Component = ManagedComponentKind.ReShade, BinaryDirectory = "/tmp/game"
        });

        var first = store.FindAll(game);
        // Callers must not be able to mutate the cached list through a returned copy.
        Assert.Single(first);
        var second = store.FindAll(game);
        Assert.Single(second);
        Assert.NotSame(first, second);

        // An external edit (different length) is picked up.
        File.WriteAllText(path, "[]");
        Assert.Empty(store.FindAll(game));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
