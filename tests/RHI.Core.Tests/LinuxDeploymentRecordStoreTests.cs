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

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
