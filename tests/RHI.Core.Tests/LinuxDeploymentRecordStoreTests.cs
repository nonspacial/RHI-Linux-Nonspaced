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

    [Fact]
    public void Find_ReturnsCopies_SoUnsavedMutationNeverReachesTheCache()
    {
        var path = Path.Combine(_root, "records.json");
        var store = new LinuxDeploymentRecordStore(path);
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", SteamAppId = 42 };
        store.Upsert(Seed());

        var found = store.Find(game, ManagedComponentKind.ShortFuse)!;
        Mutate(found);
        store.FindAll(game).Single().Files[0].Sha256 = "mutated";

        AssertUnchanged(store.Find(game, ManagedComponentKind.ShortFuse)!);
        // An unrelated save must persist the original, not the abandoned mutation.
        store.Upsert(new LinuxDeploymentRecord { GameKey = "steam:42", Component = ManagedComponentKind.ReShade, BinaryDirectory = "/tmp/game" });
        AssertUnchanged(new LinuxDeploymentRecordStore(path).Find(game, ManagedComponentKind.ShortFuse)!);
    }

    [Fact]
    public void Upsert_CachesPrivateCopy_SoCallerMutationAfterSaveIsNotSeen()
    {
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", SteamAppId = 42 };
        var record = Seed();
        store.Upsert(record);

        Mutate(record);

        AssertUnchanged(store.Find(game, ManagedComponentKind.ShortFuse)!);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Upsert_ThatFailsToSave_LeavesCacheAndLedgerUnchanged()
    {
        var path = Path.Combine(_root, "records.json");
        var store = new LinuxDeploymentRecordStore(path);
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", SteamAppId = 42 };
        store.Upsert(Seed());
        var ledger = File.ReadAllText(path);

        // The ledger directory refuses new files, so the write fails with the cache stamp intact.
        var directory = Path.GetDirectoryName(path)!;
        var mode = File.GetUnixFileMode(directory);
        var changed = store.Find(game, ManagedComponentKind.ShortFuse)!;
        Mutate(changed);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Upsert(changed));
        }
        finally
        {
            File.SetUnixFileMode(directory, mode);
        }

        AssertUnchanged(store.Find(game, ManagedComponentKind.ShortFuse)!);
        Assert.Equal(ledger, File.ReadAllText(path));
        Assert.DoesNotContain(Directory.GetFiles(directory), file => file.Contains(".rhi-tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public void Upsert_RefusedOnCorruptLedger_LeavesRecordUnchanged()
    {
        var path = Path.Combine(_root, "records.json");
        var store = new LinuxDeploymentRecordStore(path);
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", SteamAppId = 42 };
        store.Upsert(Seed());
        var ledger = File.ReadAllText(path);
        var changed = store.Find(game, ManagedComponentKind.ShortFuse)!;
        Mutate(changed);

        File.WriteAllText(path, "{ this is not json");
        Assert.Throws<InvalidOperationException>(() => store.Upsert(changed));
        File.WriteAllText(path, ledger);

        AssertUnchanged(store.Find(game, ManagedComponentKind.ShortFuse)!);
    }

    // #118: Clone switched to dedicated non-indented options; it must still copy every property. The
    // fixture sets every public property of the three record types, so a new field without a value
    // here fails the first assertion instead of being silently dropped by a future hand-written copy.
    [Fact]
    public void Clone_CopiesEveryPropertyDeeply()
    {
        var original = new LinuxDeploymentRecord
        {
            GameKey = "steam:42", GameName = "Game", Component = ManagedComponentKind.ShortFuse, BinaryDirectory = "/g/bin",
            SourceUrl = "https://example.invalid/a", Version = "1.2", InstalledAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Files = [new LinuxDeployedFile { Path = "/g/bin/a.dll", ExistedBeforeInstall = true, Sha256 = "AB", AllowUserChanges = true }],
            DirectoriesCreatedByRhi = ["/g/bin/d"],
            WineOverride = new WineOverrideRecord { DllName = "dxgi", PreviousValue = "builtin", ChangedByRhi = true, SectionCreatedByRhi = true, ApplicationName = "game.exe" },
            AdditionalWineOverrides = [new WineOverrideRecord { DllName = "d3dcompiler_47", PreviousValue = "b", ChangedByRhi = true, SectionCreatedByRhi = true, ApplicationName = "g.exe" }],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["Key"] = "Value" }
        };
        foreach (var item in new object[] { original, original.Files[0], original.WineOverride })
            foreach (var property in item.GetType().GetProperties())
            {
                var value = property.GetValue(item);
                var unset = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
                Assert.False(Equals(value, unset) || value is System.Collections.ICollection { Count: 0 },
                    $"{item.GetType().Name}.{property.Name} has no value in the fixture");
            }

        var copy = LinuxDeploymentRecordStore.Clone(original);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(copy));
        Assert.NotSame(original.Files[0], copy.Files[0]);
        Assert.NotSame(original.WineOverride, copy.WineOverride);
        Assert.NotSame(original.Metadata, copy.Metadata);
        Assert.Equal("Value", copy.Metadata["KEY"]);
    }

    private static LinuxDeploymentRecord Seed() => new()
    {
        GameKey = "steam:42", GameName = "Game", Component = ManagedComponentKind.ShortFuse, BinaryDirectory = "/tmp/game",
        Version = "1.0", SourceUrl = "https://example.invalid/1.0",
        Files = [new LinuxDeployedFile { Path = "/tmp/game/renodx-dlss.addon64", Sha256 = "old" }],
        DirectoriesCreatedByRhi = ["/tmp/game/reshade-shaders"],
        Metadata = new(StringComparer.OrdinalIgnoreCase) { ["GitHub.ReleaseTagPin"] = "v1.0" }
    };

    private static void Mutate(LinuxDeploymentRecord record)
    {
        record.Version = "2.0";
        record.SourceUrl = "https://example.invalid/2.0";
        record.Files[0].Sha256 = "new";
        record.Files.Add(new LinuxDeployedFile { Path = "/tmp/game/extra.dll" });
        record.DirectoriesCreatedByRhi.Clear();
        record.Metadata["GitHub.ReleaseTagPin"] = "v2.0";
    }

    private static void AssertUnchanged(LinuxDeploymentRecord record)
    {
        Assert.Equal("1.0", record.Version);
        Assert.Equal("https://example.invalid/1.0", record.SourceUrl);
        Assert.Equal("old", Assert.Single(record.Files).Sha256);
        Assert.Equal(["/tmp/game/reshade-shaders"], record.DirectoriesCreatedByRhi);
        Assert.Equal("v1.0", record.Metadata["github.releasetagpin"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
