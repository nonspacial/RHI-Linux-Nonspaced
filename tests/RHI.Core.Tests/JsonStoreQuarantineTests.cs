using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;
using RHI.Platform.Linux.Ux;

namespace RHI.Core.Tests;

/// <summary>
/// DET-18 (Refs #57): every JSON store keeps an unreadable file. It is copied aside with its
/// original bytes, user data is never saved over, and the user is told once.
/// </summary>
public sealed class JsonStoreQuarantineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-json-store-" + Guid.NewGuid().ToString("N"));

    public JsonStoreQuarantineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Garbage(string tag) => Encoding.UTF8.GetBytes("{ \"truncated\": [1, 2, " + tag + " é");

    private static string[] Quarantined(string path)
        => Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");

    private static DetectedGame Game(string root) => new()
    {
        Name = "Store Game",
        InstallPath = root,
        BinaryDirectory = root,
        Source = "Steam",
        SteamAppId = 4242,
        Runner = "Proton",
        ForcedArchitecture = PeArchitecture.X64
    };

    // ---- Shared helper ---------------------------------------------------------------------

    [Fact]
    public void Helper_MissingOrBlankFile_IsMissingAndWritable()
    {
        var path = Path.Combine(_root, "blank.json");
        var store = new JsonFileStore<Dictionary<string, string>>(path);
        Assert.Equal(JsonStoreLoadStatus.Missing, store.Load().Status);

        File.WriteAllText(path, "  \n");
        Assert.Equal(JsonStoreLoadStatus.Missing, store.Load().Status);
        store.Save(new() { ["a"] = "b" });

        Assert.Equal("b", store.Load().Value!["a"]);
        Assert.Empty(Quarantined(path));
    }

    [Fact]
    public void Helper_GarbageFile_IsCopiedAsideOnceAndNeverOverwritten()
    {
        var path = Path.Combine(_root, "garbage.json");
        var original = Garbage("helper");
        File.WriteAllBytes(path, original);
        var notices = new List<JsonStoreQuarantineNotice>();
        using var _ = JsonStoreQuarantine.Listen(n => { if (n.FilePath == Path.GetFullPath(path)) lock (notices) notices.Add(n); });
        var store = new JsonFileStore<Dictionary<string, string>>(path);

        var first = store.Load();
        var second = new JsonFileStore<Dictionary<string, string>>(path).Load();
        var refused = Assert.Throws<JsonStoreUnavailableException>(() => store.Save(new() { ["a"] = "b" }));

        Assert.Equal(JsonStoreLoadStatus.Unreadable, first.Status);
        Assert.Equal(first.QuarantinePath, second.QuarantinePath);
        Assert.Equal(first.QuarantinePath, refused.QuarantinePath);
        var copy = Assert.Single(Quarantined(path));
        Assert.Equal(copy, first.QuarantinePath);
        Assert.Equal(original, File.ReadAllBytes(copy));
        Assert.Equal(original, File.ReadAllBytes(path));
        var notice = Assert.Single(notices);
        Assert.True(notice.WritesRefused);
        Assert.Equal(copy, notice.QuarantinePath);
    }

    [Fact]
    public void Helper_GarbageFile_IsWrittenToTheAppLog_WithItsCopy()
    {
        var path = Path.Combine(_root, "logged.json");
        File.WriteAllBytes(path, Garbage("log"));
        var log = new RHI.Core.Services.AppLog(Path.Combine(_root, "state", "rhi.log"));

        JsonStoreLoad<Dictionary<string, string>> load;
        using (RHI.Core.Services.AppLog.UseInThisFlow(log))
            load = new JsonFileStore<Dictionary<string, string>>(path).Load();

        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("[store]", text);
        Assert.Contains(Path.GetFullPath(path), text);
        Assert.Contains(load.QuarantinePath!, text);
        Assert.Contains("saves to it are refused", text);
    }

    [Fact]
    public void Helper_FixedFile_IsWritableAgain_AndANewCorruptionIsANewEvent()
    {
        var path = Path.Combine(_root, "fixed.json");
        File.WriteAllBytes(path, Garbage("one"));
        var store = new JsonFileStore<Dictionary<string, string>>(path);
        Assert.True(store.Load().IsUnreadable);

        File.WriteAllText(path, "{\"k\":\"v\"}");
        store.Save(new() { ["k"] = "w" });
        Assert.Equal("w", store.Load().Value!["k"]);

        File.WriteAllBytes(path, Garbage("two-longer"));
        Assert.True(store.Load().IsUnreadable);
        Assert.Equal(2, Quarantined(path).Length);
    }

    [Fact]
    public void Helper_NullLiteral_IsUnreadable()
    {
        var path = Path.Combine(_root, "null.json");
        File.WriteAllText(path, "null");
        var store = new JsonFileStore<Dictionary<string, string>>(path);

        Assert.True(store.Load().IsUnreadable);
        Assert.Throws<JsonStoreUnavailableException>(() => store.Save([]));
    }

    [Fact]
    public void Helper_CacheKind_CopiesAsideThenRebuilds()
    {
        var path = Path.Combine(_root, "cache.json");
        var original = Garbage("cache");
        File.WriteAllBytes(path, original);
        var store = new JsonFileStore<Dictionary<string, string>>(path, kind: JsonStoreKind.Cache);

        Assert.True(store.Load().IsUnreadable);
        store.Save(new() { ["x"] = "y" });

        Assert.Equal("y", store.Load().Value!["x"]);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(path))));
    }

    [Fact]
    public void Helper_Save_LeavesNoTempFiles()
    {
        var path = Path.Combine(_root, "atomic", "store.json");
        var store = new JsonFileStore<Dictionary<string, string>>(path);
        for (var i = 0; i < 3; i++) store.Save(new() { ["i"] = i.ToString() });

        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Equal("2", store.Load().Value!["i"]);
    }

    [Fact]
    public void Helper_StatusTextNamesTheFileAndItsCopy()
    {
        var text = MainWindowViewModel.StoreQuarantinedText(
            new JsonStoreQuarantineNotice("/x/game-library.json", "/x/game-library.json.corrupt-20260101-000000", WritesRefused: true));
        var refused = MainWindowViewModel.StoreSaveFailedText(new JsonStoreUnavailableException("/x/settings.json", "/x/settings.json.corrupt-1"));

        Assert.Contains("game-library.json", text);
        Assert.Contains("/x/game-library.json.corrupt-20260101-000000", text);
        Assert.Contains("settings.json", refused);
        Assert.Contains("/x/settings.json.corrupt-1", refused);
    }

    // ---- Review corrections (w1/stores) ------------------------------------------------------

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public void Bom_SettingsFile_LoadsAndStaysWritable()
    {
        // Editors offer to save with a UTF-8 BOM; File.ReadAllText (the old loader) skipped it.
        var dir = Path.Combine(_root, "bom-settings");
        var store = new LinuxSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "cache"), Path.Combine(dir, "data"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(store.SettingsPath, [.. Bom, .. Encoding.UTF8.GetBytes("{\"UiLanguage\":\"en\",\"VerboseLogging\":true}")]);

        var loaded = store.Load();
        Assert.True(loaded.VerboseLogging);
        loaded.VerboseLogging = false;
        store.Save(loaded);

        Assert.False(store.Load().VerboseLogging);
        Assert.Empty(Quarantined(store.SettingsPath));
    }

    [Fact]
    public void Bom_PreferenceStore_LoadsAndStaysWritable()
    {
        var path = Path.Combine(_root, "bom-api", "game-api-overrides.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var game = Game(_root);
        File.WriteAllBytes(path, [.. Bom, .. Encoding.UTF8.GetBytes("{\"steam:4242\":\"Vulkan\"}")]);
        var store = new GraphicsApiOverrideStore(path);

        Assert.Equal(GraphicsApi.Vulkan, store.Get(game));
        store.Set(game, GraphicsApi.DirectX11);

        Assert.Equal(GraphicsApi.DirectX11, store.Get(game));
        Assert.Empty(Quarantined(path));
    }

    [Fact]
    public void Bom_Only_IsAnEmptyFile()
    {
        var path = Path.Combine(_root, "bom-only.json");
        File.WriteAllBytes(path, [.. Bom, (byte)'\n']);
        var store = new JsonFileStore<Dictionary<string, string>>(path);

        Assert.Equal(JsonStoreLoadStatus.Missing, store.Load().Status);
        store.Save(new() { ["a"] = "b" });
    }

    public static TheoryData<string, string> CaseOnlyDuplicates => new()
    {
        { "api-overrides", "{\"/x/a\":\"DirectX11\",\"/X/A\":\"Vulkan\"}" },
        { "optiscaler-variants", "{\"/x/a\":\"Stable\",\"/X/A\":\"Nightly\"}" },
        { "neural-pins", "{\"/x/a|ShortFuse\":\"v1.0\",\"/X/A|shortfuse\":\"v2.0\"}" },
        { "dlssg2030", "{\"/x/a\":{},\"/X/A\":{}}" },
        { "rtx40-mfg", "{\"/x/a\":{},\"/X/A\":{}}" },
        { "dlss-enabler", "{\"/x/a\":{},\"/X/A\":{}}" },
    };

    [Theory]
    [MemberData(nameof(CaseOnlyDuplicates))]
    public async Task CaseOnlyDuplicateKeys_AreMergedNotThrown(string store, string json)
    {
        // A hand edit can leave keys that differ only in case; reading and saving must not throw,
        // and the saved file keeps one merged entry.
        var dir = Path.Combine(_root, "dup-" + store);
        Directory.CreateDirectory(dir);
        var game = Game(dir);
        var (path, read, write) = Scenario(store, dir, game);
        File.WriteAllText(path, json);

        read();
        Assert.True(await write());

        Assert.Empty(Quarantined(path));
        var saved = File.ReadAllText(path);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(saved, "\"/x/a", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public void CaseOnlyDuplicateKeys_LaterEntryWins()
    {
        var path = Path.Combine(_root, "dup-order", "game-api-overrides.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"steam:4242\":\"DirectX11\",\"STEAM:4242\":\"Vulkan\"}");

        Assert.Equal(GraphicsApi.Vulkan, new GraphicsApiOverrideStore(path).Get(Game(_root)));
    }

    [Fact]
    public void CaseOnlyDuplicateKeys_LibraryEditsAndHdr_LoadInsteadOfQuarantine()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "dup-library", "game-library.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.LibraryPath)!);
        File.WriteAllText(store.LibraryPath, """
            { "GameEdits": { "steam|steam:1|/g/a": { "LaunchProvider": "One" }, "steam|steam:1|/G/A": { "LaunchProvider": "Two" } },
              "HdrProfiles": { "k": { }, "K": { } } }
            """);

        var state = store.Load();

        Assert.Equal("Two", Assert.Single(state.GameEdits).Value.LaunchProvider);
        Assert.Single(state.HdrProfiles);
        Assert.Empty(Quarantined(store.LibraryPath));
        store.Save(state);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void SettingsReset_UnreadableFileThatCannotBeCopied_IsRenamedAsideNotDeleted()
    {
        var dir = Path.Combine(_root, "reset-unreadable");
        var store = new LinuxSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "cache"), Path.Combine(dir, "data"));
        Directory.CreateDirectory(dir);
        var original = Encoding.UTF8.GetBytes("{\"UiLanguage\":\"pt-BR\"}");
        File.WriteAllBytes(store.SettingsPath, original);
        File.SetUnixFileMode(store.SettingsPath, UnixFileMode.None); // unreadable: no copy possible
        try
        {
            store.Reset();

            Assert.False(File.Exists(store.SettingsPath));
            var aside = Assert.Single(Quarantined(store.SettingsPath));
            File.SetUnixFileMode(aside, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(original, File.ReadAllBytes(aside));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(dir)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void SettingsReset_WhenTheFileCanNeitherBeCopiedNorRenamed_IsRefusedAndKeepsIt()
    {
        var dir = Path.Combine(_root, "reset-stuck");
        var store = new LinuxSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "cache"), Path.Combine(dir, "data"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(store.SettingsPath, "{}");
        File.SetUnixFileMode(store.SettingsPath, UnixFileMode.None);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // no rename, no delete
        try
        {
            Assert.Throws<JsonStoreUnavailableException>(() => store.Reset());
            Assert.True(File.Exists(store.SettingsPath));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(store.SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void SettingsReset_UnreadableButCopiedFile_IsDeletedWithItsCopyKept()
    {
        var dir = Path.Combine(_root, "reset-garbage");
        var store = new LinuxSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "cache"), Path.Combine(dir, "data"));
        Directory.CreateDirectory(dir);
        var original = Garbage("reset");
        File.WriteAllBytes(store.SettingsPath, original);

        store.Reset();

        Assert.False(File.Exists(store.SettingsPath));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(store.SettingsPath))));
    }

    [Fact]
    public void QuarantineNotices_RaisedWithoutListener_GoToOneListenerOnly_AndDisposedListenersHearNothing()
    {
        // Serialised against other listeners by the notice path filter, not by global state.
        var path = Path.Combine(_root, "notices.json");
        var full = Path.GetFullPath(path);
        var first = new List<string>();
        var second = new List<string>();

        // Delivered to whoever listens now (or kept for the next listener when nobody does).
        using (JsonStoreQuarantine.Listen(n => { if (n.FilePath == full) lock (first) first.Add(n.FilePath); }))
        {
            File.WriteAllBytes(path, Garbage("notice"));
            new JsonFileStore<Dictionary<string, string>>(path).Load();
        }
        using (JsonStoreQuarantine.Listen(n => { if (n.FilePath == full) lock (second) second.Add(n.FilePath); }))
        {
            File.WriteAllBytes(path, Garbage("notice-two-longer"));
            new JsonFileStore<Dictionary<string, string>>(path).Load();
        }

        Assert.Equal([full], first);  // the disposed first listener missed the second event
        Assert.Equal([full], second); // and the second listener was not handed the first again
    }

    [Fact]
    public void QuarantineNotices_PendingAreHandedOutOnce()
    {
        var path = Path.Combine(_root, "pending.json");
        var full = Path.GetFullPath(path);
        File.WriteAllBytes(path, Garbage("pending"));
        var replays = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            var seen = 0;
            using (JsonStoreQuarantine.Listen(n => { if (n.FilePath == full) Interlocked.Increment(ref seen); }))
            {
                if (i == 0) new JsonFileStore<Dictionary<string, string>>(path).Load();
            }
            replays.Add(seen);
        }

        Assert.Equal([1, 0], replays);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void StoreFileWrite_SymlinkedStoreFile_StaysALinkAndTheTargetKeepsItsMode()
    {
        var dir = Path.Combine(_root, "symlink");
        var target = Path.Combine(dir, "dotfiles", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "{}");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(dir, "settings.json");
        File.CreateSymbolicLink(link, Path.Combine("dotfiles", "settings.json"));

        new JsonFileStore<Dictionary<string, string>>(link).Save(new() { ["k"] = "v" });

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Contains("\"k\"", File.ReadAllText(target));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void StoreFileWrite_Replace_KeepsAModeWiderThanTheUmaskAllows()
    {
        var path = Path.Combine(_root, "mode", "shared.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        var shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(path, shared);

        new JsonFileStore<Dictionary<string, string>>(path).Save(new() { ["k"] = "v" });

        Assert.Equal(shared, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Motd_UnreadableStateFile_IsCopiedAsideAndReplacedAtomically()
    {
        var path = Path.Combine(_root, "motd", "motd-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Garbage("motd");
        File.WriteAllBytes(path, original);
        using var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var motd = new LinuxMotdService(http, path, "https://motd.test/motd.md");

        motd.MarkSeen(new LinuxMotdMessage("hello", "abc123", false));

        Assert.Contains("abc123", File.ReadAllText(path));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(path))));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length); // no stray .tmp
    }

    [Fact]
    public async Task Motd_SeenHash_IsRemembered()
    {
        var path = Path.Combine(_root, "motd-seen", "motd-state.json");
        using var http = new HttpClient(new RouteHandler(_ => Text("Hello from the fork")));
        var motd = new LinuxMotdService(http, path, "https://motd.test/motd.md");

        var first = await motd.FetchAsync();
        motd.MarkSeen(first!);

        Assert.Null(await motd.FetchAsync());
        Assert.True((await motd.FetchAsync(includeSeen: true))!.AlreadySeen);
    }

    // ---- Staged multi-file commit (relocation repair) ---------------------------------------

    [Fact]
    public void StagedCommit_WritesEveryFile()
    {
        var a = new JsonFileStore<Dictionary<string, string>>(Path.Combine(_root, "staged-ok", "a.json"));
        var b = new JsonFileStore<Dictionary<string, string>>(Path.Combine(_root, "staged-ok", "b.json"));
        a.Save(new() { ["x"] = "1" });

        StagedFileCommit.Commit([
            a.Stage(map => { map!["x"] = "2"; return map; })!,
            b.Stage(_ => new() { ["y"] = "3" })!
        ]);

        Assert.Equal("2", a.Load().Value!["x"]);
        Assert.Equal("3", b.Load().Value!["y"]);
    }

    [Fact]
    public void StagedCommit_FileChangedSinceStaging_WritesNothing()
    {
        var a = new JsonFileStore<Dictionary<string, string>>(Path.Combine(_root, "staged-race", "a.json"));
        var b = new JsonFileStore<Dictionary<string, string>>(Path.Combine(_root, "staged-race", "b.json"));
        a.Save(new() { ["x"] = "1" });
        b.Save(new() { ["y"] = "1" });
        var writes = new[] { a.Stage(_ => new() { ["x"] = "2" })!, b.Stage(_ => new() { ["y"] = "2" })! };
        b.Save(new() { ["y"] = "someone else" });

        Assert.Throws<StagedFileCommitException>(() => StagedFileCommit.Commit(writes));

        Assert.Equal("1", a.Load().Value!["x"]);
        Assert.Equal("someone else", b.Load().Value!["y"]);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void StagedCommit_SecondWriteFails_FirstFileGetsItsBytesBack()
    {
        var okDir = Path.Combine(_root, "staged-fail", "ok");
        var lockedDir = Path.Combine(_root, "staged-fail", "locked");
        var a = new JsonFileStore<Dictionary<string, string>>(Path.Combine(okDir, "a.json"));
        var created = new JsonFileStore<Dictionary<string, string>>(Path.Combine(okDir, "new.json"));
        var b = new JsonFileStore<Dictionary<string, string>>(Path.Combine(lockedDir, "b.json"));
        a.Save(new() { ["x"] = "1" });
        b.Save(new() { ["y"] = "1" });
        var before = File.ReadAllBytes(a.FilePath);
        var writes = new[]
        {
            a.Stage(_ => new() { ["x"] = "2" })!,
            created.Stage(_ => new() { ["n"] = "2" })!,
            b.Stage(_ => new() { ["y"] = "2" })!
        };
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // no temp file there
        try
        {
            var error = Assert.Throws<StagedFileCommitException>(() => StagedFileCommit.Commit(writes));

            Assert.Empty(error.NotRestored);
            Assert.Equal(before, File.ReadAllBytes(a.FilePath));
            Assert.False(File.Exists(created.FilePath));
            Assert.Equal("1", b.Load().Value!["y"]);
        }
        finally
        {
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void StagedCommit_UnreadableUserData_RefusesToStage()
    {
        var path = Path.Combine(_root, "staged-unreadable.json");
        File.WriteAllBytes(path, Garbage("staged"));

        Assert.Throws<JsonStoreUnavailableException>(() =>
            new JsonFileStore<Dictionary<string, string>>(path).Stage(_ => new()));
    }

    // ---- Every store -------------------------------------------------------------------------

    public static TheoryData<string> Stores => new()
    {
        "game-library", "settings", "api-overrides", "optiscaler-variants", "luma-mode", "neural-pins",
        "dlssg2030", "rtx40-mfg", "dlss-enabler", "shortfuse", "ui-settings", "addon-manager",
        "shader-manifest", "shader-pack-settings"
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Store_GarbageFile_LoadThenSave_KeepsOriginalBytesAndRefusesTheWrite(string store)
    {
        var dir = Path.Combine(_root, store);
        Directory.CreateDirectory(dir);
        var game = Game(dir);
        var (path, read, write) = Scenario(store, dir, game);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Garbage(store);
        File.WriteAllBytes(path, original);

        read();
        var saved = await write();

        Assert.False(saved);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(path))));
    }

    [Fact]
    public async Task PcgwCache_GarbageFile_IsCopiedAsideAndRebuilt()
    {
        var path = Path.Combine(_root, "pcgw", "pcgw_graphics_api_cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Garbage("pcgw");
        File.WriteAllBytes(path, original);
        const string html = """<table id="table-api"><tr><th scope="row"><abbr title="Windows">Direct3D 11</abbr></th><td>11</td></tr></table>""";
        using var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));

        await new PcGamingWikiGraphicsApiService(http, path).FetchAsync("Game", "https://www.pcgamingwiki.com/wiki/Game");

        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(path))));
        Assert.Contains("pcgamingwiki.com", File.ReadAllText(path));
    }

    private (string Path, Action Read, Func<Task<bool>> Write) Scenario(string store, string dir, DetectedGame game)
    {
        static Task<bool> Refused(Action save)
        {
            try { save(); return Task.FromResult(true); }
            catch (JsonStoreUnavailableException) { return Task.FromResult(false); }
        }

        switch (store)
        {
            case "game-library":
            {
                var s = new LinuxGameLibraryStore(Path.Combine(dir, "game-library.json"));
                return (s.LibraryPath, () => Assert.Empty(s.Load().ManualGames), () => Refused(() => s.Save(s.Load())));
            }
            case "settings":
            {
                var s = new LinuxSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "cache"), Path.Combine(dir, "data"));
                return (s.SettingsPath, () => s.Load(), () => Refused(() => s.Save(s.Load())));
            }
            case "api-overrides":
            {
                var path = Path.Combine(dir, "game-api-overrides.json");
                var s = new GraphicsApiOverrideStore(path);
                return (path, () => Assert.Null(s.Get(game)), () => Refused(() => s.Set(game, GraphicsApi.Vulkan)));
            }
            case "optiscaler-variants":
            {
                var path = Path.Combine(dir, "optiscaler-variants.json");
                var s = new OptiScalerVariantPreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, OptiScalerVariant.Nightly)));
            }
            case "luma-mode":
            {
                var path = Path.Combine(dir, "luma-mode.json");
                var s = new LumaModePreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, LumaModePreference.Enabled)));
            }
            case "neural-pins":
            {
                var path = Path.Combine(dir, "neural-addon-version-pins.json");
                var s = new NeuralAddonVersionPreferenceStore(path);
                return (path, () => s.Get(game, ManagedComponentKind.ShortFuse), () => Refused(() => s.Set(game, ManagedComponentKind.ShortFuse, "v1.0")));
            }
            case "dlssg2030":
            {
                var path = Path.Combine(dir, "dlssg2030-preferences.json");
                var s = new Dlssg2030PreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, new Dlssg2030Preferences())));
            }
            case "rtx40-mfg":
            {
                var path = Path.Combine(dir, "rtx40-mfg-standalone-preferences.json");
                var s = new Rtx40MfgPreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, new Rtx40MfgPreferences())));
            }
            case "dlss-enabler":
            {
                var path = Path.Combine(dir, "dlss-enabler-standalone-preferences.json");
                var s = new StandaloneDlssEnablerPreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, new StandaloneDlssEnablerPreferences())));
            }
            case "shortfuse":
            {
                var path = Path.Combine(dir, "shortfuse-settings.json");
                var s = new ShortFuseSettingsStore(path);
                return (path, () => Assert.False(s.IsAutoConfigEnabled(game)), () => Refused(() => s.SetAutoConfigEnabled(game, true)));
            }
            case "ui-settings":
            {
                var s = new DetailSectionLayoutStore(Path.Combine(dir, "ui-settings.json"));
                return (s.Path, () => s.Load(), () => Refused(() => s.Save(s.Load())));
            }
            case "addon-manager":
            {
                // The Addon Manager refuses before touching the game: no add-on file is deployed.
                File.WriteAllText(Path.Combine(dir, "dxgi.dll"), "ReShade by crosire");
                var custom = Path.Combine(dir, "custom");
                Directory.CreateDirectory(custom);
                File.WriteAllBytes(Path.Combine(custom, "MyLocal.addon64"), CacheForeignFileRejectionTests.Pe(0x8664, 4096, 1));
                var statePath = Path.Combine(dir, "addon-manager.json");
                var http = new HttpClient(new RouteHandler(req => req.RequestUri?.AbsoluteUri == ReShadeAddonManagerService.AddonsIniUrl
                    ? Text("[01]\nPackageName=Generic\nDownloadUrl64=https://example.test/generic.addon64\n")
                    : req.RequestUri?.AbsoluteUri == "https://manifest.test/manifest.json"
                        ? Text("{\"version\":2}")
                        : new HttpResponseMessage(HttpStatusCode.NotFound)));
                var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(dir, "manifest-cache.json"));
                var s = new ReShadeAddonManagerService(http, manifest, Path.Combine(dir, "addon-cache"), custom, statePath);
                return (statePath, () => { }, async () =>
                {
                    var result = await s.ApplySelectionAsync(game, ["custom-MyLocal"]);
                    Assert.Empty(Directory.EnumerateFiles(dir, "*.addon64", SearchOption.TopDirectoryOnly));
                    return result.Success;
                });
            }
            case "shader-manifest":
            {
                var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ShaderZip()) }));
                var s = new ReShadeShaderPackService(http, Path.Combine(dir, "shader-cache"), customRoot: Path.Combine(dir, "custom-shaders"));
                var path = Path.Combine(dir, "reshade-shaders", "RHI", ".rhi-manifest.json");
                return (path, () => { }, async () =>
                {
                    try { await s.SyncAsync(dir, ["CrosireMaster"]); return true; }
                    catch (JsonStoreUnavailableException) { }
                    try { s.Cleanup(dir); return true; }
                    catch (JsonStoreUnavailableException) { return false; }
                });
            }
            case "shader-pack-settings":
            {
                var cache = Path.Combine(dir, "shader-cache");
                var s = new ReShadeShaderPackService(new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                    cache, customRoot: Path.Combine(dir, "custom-shaders"));
                return (Path.Combine(cache, ".pack-settings.json"), () => Assert.Empty(s.GetExcludedFiles("CrosireMaster")),
                    () => Refused(() => s.SetExcludedFiles("CrosireMaster", ["Test.fx"])));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(store), store, null);
        }
    }

    private static HttpResponseMessage Text(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/plain") };

    private static byte[] ShaderZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("reshade-shaders-master/Shaders/Test.fx").Open());
            writer.Write("float4 main() : SV_Target { return 1; }");
        }
        return memory.ToArray();
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}
