using System.Text;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class SteamAppClassificationTests
{
    /// <summary>Writes a version-29 appinfo.vdf (keys in a trailing string table) with appinfo.common.type/oslist.</summary>
    internal static byte[] AppInfoV29(params (int AppId, string? Type, string? OsList)[] apps)
    {
        var strings = new List<string> { "appinfo", "common", "type", "oslist", "name" };
        using var body = new MemoryStream();
        using var w = new BinaryWriter(body, Encoding.UTF8);
        foreach (var (appId, type, osList) in apps)
        {
            using var entry = new MemoryStream();
            using var e = new BinaryWriter(entry, Encoding.UTF8);
            e.Write(new byte[4 + 4 + 8 + 20 + 4 + 20]);
            void ECStr(string s) { e.Write(Encoding.UTF8.GetBytes(s)); e.Write((byte)0); }
            e.Write((byte)0); e.Write(strings.IndexOf("appinfo"));
            e.Write((byte)0); e.Write(strings.IndexOf("common"));
            e.Write((byte)1); e.Write(strings.IndexOf("name")); ECStr("App " + appId);
            if (type is not null) { e.Write((byte)1); e.Write(strings.IndexOf("type")); ECStr(type); }
            if (osList is not null) { e.Write((byte)1); e.Write(strings.IndexOf("oslist")); ECStr(osList); }
            e.Write((byte)8); e.Write((byte)8); e.Write((byte)8);
            e.Flush();
            w.Write((uint)appId);
            w.Write((uint)entry.Length);
            w.Write(entry.ToArray());
        }
        w.Write(0u);
        w.Flush();
        var header = 4 + 4 + 8;
        var tableOffset = header + body.Length;
        using var file = new MemoryStream();
        using var f = new BinaryWriter(file, Encoding.UTF8);
        f.Write(0x07564429u);
        f.Write(1u);
        f.Write(tableOffset);
        f.Write(body.ToArray());
        f.Write((uint)strings.Count);
        foreach (var s in strings) { f.Write(Encoding.UTF8.GetBytes(s)); f.Write((byte)0); }
        f.Flush();
        return file.ToArray();
    }

    private static string CompatConfig(params (int AppId, string Name)[] mappings)
    {
        var entries = string.Concat(mappings.Select(m => $"\"{m.AppId}\"\n{{\n\"name\" \"{m.Name}\"\n\"config\" \"\"\n\"priority\" \"250\"\n}}\n"));
        return $"\"InstallConfigStore\"\n{{\n\"Software\"\n{{\n\"Valve\"\n{{\n\"Steam\"\n{{\n\"CompatToolMapping\"\n{{\n{entries}}}\n}}\n}}\n}}\n}}\n";
    }

    private static readonly byte[] Elf = [0x7f, (byte)'E', (byte)'L', (byte)'F', .. new byte[8192]];

    private sealed class SteamFixture : IDisposable
    {
        public DetectionTestTree Tree { get; } = new();
        public string Root { get; }
        public SteamFixture() => Root = Tree.Dir("Steam");

        public void App(int appId, string name, string installDir, string stateFlags = "4", bool windowsExe = true, bool nativeExe = false, bool prefix = false)
        {
            Tree.File(SteamGameDetectorTests.Manifest(appId, name, installDir, stateFlags), "Steam", "steamapps", $"appmanifest_{appId}.acf");
            if (windowsExe) Tree.Exe("Steam", "steamapps", "common", installDir, installDir + ".exe");
            if (nativeExe) Tree.Bytes(Elf, "Steam", "steamapps", "common", installDir, installDir.ToUpperInvariant());
            if (!windowsExe && !nativeExe) Tree.File("data", "Steam", "steamapps", "common", installDir, "readme.txt");
            if (prefix) Tree.Dir("Steam", "steamapps", "compatdata", appId.ToString(), "pfx", "drive_c");
        }

        public IReadOnlyList<RHI.Core.Models.DetectedGame> Detect() => SteamGameDetector.DetectIn([Root], CancellationToken.None);
        public void Dispose() => Tree.Dispose();
    }

    [Fact]
    public void PartialInstalls_AreSkipped_UpdatePendingAndUnflaggedAreKept()
    {
        using var steam = new SteamFixture();
        steam.App(1, "Downloading", "Downloading", stateFlags: "1026");
        steam.App(2, "Update Pending", "UpdatePending", stateFlags: "6");
        steam.App(3, "No Flags", "NoFlags", stateFlags: "");

        Assert.Equal(["No Flags", "Update Pending"], steam.Detect().Select(g => g.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AppInfoType_DecidesWhatIsAGame_NotTheName()
    {
        using var steam = new SteamFixture();
        steam.App(250820, "SteamVR", "SteamVR");
        steam.App(228980, "Steamworks Common Redistributables", "Steamworks Shared");
        steam.App(4183110, "Some Future Runtime", "FutureRuntime");
        steam.App(500, "Proton Quest", "Proton 2 Quest"); // a game whose name looks like a runtime
        steam.App(600, "Blender", "Blender");
        steam.Tree.Bytes(AppInfoV29(
            (250820, "Tool", "windows"), (228980, "Tool", "windows,linux"), (4183110, "Tool", "linux"),
            (500, "Game", "windows"), (600, "Application", "windows")), "Steam", "appcache", "appinfo.vdf");

        Assert.Equal(["Blender", "Proton Quest"], steam.Detect().Select(g => g.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WithoutAppInfo_TheNameListStillHidesRuntimes()
    {
        using var steam = new SteamFixture();
        steam.App(2805730, "Proton 9.0", "Proton 9.0");
        steam.App(700, "Real Game", "RealGame");

        Assert.Equal(["Real Game"], steam.Detect().Select(g => g.Name));
    }

    [Fact]
    public void NativeBuild_WinsOverAStalePrefix_UnlessACompatToolIsForced()
    {
        using var steam = new SteamFixture();
        // Broken Sword 1 shape: ships Linux, user has no forced tool (empty name), an old prefix lingers.
        steam.App(57640, "Native Game", "NativeGame", nativeExe: true, prefix: true);
        // Borderlands 2 shape: ships Linux too, but the user forced GE-Proton.
        steam.App(49520, "Forced Proton", "ForcedProton", nativeExe: true, prefix: true);
        // Windows-only, no mapping: Steam's global default tool runs it.
        steam.App(33610, "Windows Only", "WindowsOnly");
        // Same as no mapping, as written by real config.vdf files.
        steam.App(57641, "Native Too", "NativeToo", nativeExe: true, prefix: true);
        steam.Tree.Bytes(AppInfoV29((57640, "Game", "windows,macos,linux"), (49520, "game", "windows,linux"), (33610, "Game", "windows"), (57641, "Game", "linux")),
            "Steam", "appcache", "appinfo.vdf");
        steam.Tree.File(CompatConfig((57640, ""), (57641, "None"), (49520, "GE-Proton11-7"), (0, "GE-Proton11-5")), "Steam", "config", "config.vdf");

        var games = steam.Detect().ToDictionary(g => g.SteamAppId!.Value);

        Assert.Equal("Native Linux", games[57640].Runner);
        Assert.Null(games[57640].PrefixPath);
        Assert.Equal("NATIVEGAME", Path.GetFileName(games[57640].ExecutablePath));

        Assert.Equal("Native Linux", games[57641].Runner);

        Assert.Equal("Proton", games[49520].Runner);
        Assert.NotNull(games[49520].PrefixPath);
        Assert.Equal("ForcedProton.exe", Path.GetFileName(games[49520].ExecutablePath));
        Assert.Equal("GE-Proton11-7", games[49520].CompatibilityTool);

        Assert.Equal("Windows/Proton (prefix not created)", games[33610].Runner);
        Assert.Equal("GE-Proton11-5", games[33610].CompatibilityTool);
    }

    [Fact]
    public void AppInfoReader_ReadsOnlyRequestedApps_AndToleratesGarbage()
    {
        using var tree = new DetectionTestTree();
        var path = tree.Bytes(AppInfoV29((1, "Game", "linux"), (2, "Tool", "windows")), "appinfo.vdf");
        var read = SteamAppInfo.Read(path, new HashSet<int> { 2 });
        Assert.Equal(new SteamAppInfo.Entry("Tool", "windows"), Assert.Single(read).Value);

        var junk = tree.Bytes([1, 2, 3, 4, 5, 6, 7, 8, 9], "junk.vdf");
        Assert.Empty(SteamAppInfo.Read(junk, new HashSet<int> { 1 }));
    }
}
