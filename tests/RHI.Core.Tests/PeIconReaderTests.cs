using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using static RHI.Core.Tests.TestPeFiles;

namespace RHI.Core.Tests;

/// <summary>
/// #202: a shortcut's icon comes from the game's exe (largest RT_ICON of its RT_GROUP_ICON, PNG as stored, BMP converted),
/// then the Steam art, then RHI's icon; a file RHI cannot read falls back silently; RHI's icon files follow the shortcuts.
/// </summary>
public sealed class PeIconReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-pe-icon-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly Func<int, int, bool> NoMask = (_, _) => false;

    [Fact]
    public void TheLargestIcon_IsChosen_AndAPngIconIsKeptAsStored()
    {
        var png = PngIcon(256, red: 200);
        var exe = Write("game.exe", WithIcons(
            new Icon(16, 16, 32, DibIcon(16, 16, 32, (_, _) => [1, 2, 3, 255], NoMask), Id: 1),
            new Icon(0, 0, 32, png, Id: 2),                     // width/height 0 = 256 (the PNG-format size)
            new Icon(48, 48, 32, DibIcon(48, 48, 32, (_, _) => [4, 5, 6, 255], NoMask), Id: 3)));

        Assert.Equal(png, PeIconReader.ReadLargestIconPng(exe));
    }

    [Fact]
    public void A32BitBmpIcon_IsConvertedToPng_WithItsAlpha_TopRowFirst()
    {
        // Top-left pixel red and half transparent, everything else opaque blue (BGRA in the DIB).
        var dib = DibIcon(4, 3, 32, (x, y) => x == 0 && y == 0 ? [0, 0, 255, 128] : [255, 0, 0, 255], NoMask);
        var exe = Write("bmp32.exe", WithIcons(new Icon(4, 3, 32, dib, Id: 7)));

        var (width, height, rgba) = ReadPng(PeIconReader.ReadLargestIconPng(exe)!);

        Assert.Equal((4, 3), (width, height));
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, rgba[..4]);                // first PNG row = top of the icon
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[^4..]);
    }

    [Fact]
    public void A256ColourIcon_UsesItsPalette_AndTheAndMaskForTransparency()
    {
        var palette = new byte[256][];
        for (var i = 0; i < 256; i++) palette[i] = [0, 0, 0, 0];
        palette[5] = [10, 20, 30, 0]; // B, G, R
        var dib = DibIcon(8, 2, 8, (_, _) => [5], (x, y) => x == 7 && y == 1, palette);
        var exe = Write("bmp8.exe", WithIcons(new Icon(8, 2, 8, dib, Id: 1)));

        var (_, _, rgba) = ReadPng(PeIconReader.ReadLargestIconPng(exe)!);

        Assert.Equal(new byte[] { 30, 20, 10, 255 }, rgba[..4]);
        Assert.Equal(0, rgba[^1]);                                              // bottom-right masked out
        Assert.Equal(255, rgba[^5]);
    }

    [Fact]
    public void A32BitIconWithoutAnyAlpha_FallsBackToTheAndMask()
    {
        var dib = DibIcon(2, 1, 32, (_, _) => [9, 9, 9, 0], (x, _) => x == 1);
        var (_, _, rgba) = ReadPng(PeIconReader.ReadLargestIconPng(Write("noalpha.exe", WithIcons(new Icon(2, 1, 32, dib, Id: 1))))!);
        Assert.Equal(new byte[] { 255, 0 }, new[] { rgba[3], rgba[7] });
    }

    [Fact]
    public void FilesItCannotRead_GiveNull_NeverAnException()
    {
        var good = WithIcons(new Icon(16, 16, 32, DibIcon(16, 16, 32, (_, _) => [1, 2, 3, 255], NoMask), Id: 1));
        var cases = new Dictionary<string, byte[]>
        {
            ["encrypted"] = Enumerable.Range(0, 4096).Select(i => (byte)(i * 37 + 11)).ToArray(), // a Game Pass exe on disk
            ["elf"] = [0x7F, (byte)'E', (byte)'L', (byte)'F', .. new byte[200]],
            ["truncated"] = good[..0x150],
            ["mz-only"] = [(byte)'M', (byte)'Z', .. new byte[100]],
            ["missing-icon"] = Build([new Icon(16, 16, 32, good[..64], Id: 1)], groupIds: [9]),
            ["not-an-image"] = WithIcons(new Icon(16, 16, 32, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], Id: 1)),
            ["too-large"] = WithIcons(new Icon(0, 0, 32, DibIcon(1, 1, 32, (_, _) => [0, 0, 0, 255], NoMask) is var d
                ? Patch(d, 4, PeIconReader.MaxDibSide + 1) : d, Id: 1)),
            ["rle"] = WithIcons(new Icon(1, 1, 8, Patch(DibIcon(1, 1, 8, (_, _) => [0], NoMask), 16, 1), Id: 1)),
        };
        foreach (var (name, bytes) in cases)
            Assert.True(PeIconReader.ReadLargestIconPng(Write(name + ".exe", bytes)) is null, name);
        Assert.Null(PeIconReader.ReadLargestIconPng(Path.Combine(_root, "absent.exe")));
        Assert.Null(PeIconReader.ReadLargestIconPng(null));
    }

    [Fact]
    public void AnIconOverTheSizeCap_IsNotRead()
    {
        var huge = new byte[PeIconReader.MaxIconBytes + 1];
        PngIcon(1, 0).CopyTo(huge, 0);
        Assert.Null(PeIconReader.ReadLargestIconPng(Write("huge.exe", WithIcons(new Icon(0, 0, 32, huge, Id: 1)))));
        // The next one down the list is used instead.
        var small = PngIcon(32, 3);
        Assert.Equal(small, PeIconReader.ReadLargestIconPng(Write("huge2.exe", WithIcons(new Icon(0, 0, 32, huge, Id: 1), new Icon(32, 32, 32, small, Id: 2)))));
    }

    private static byte[] Patch(byte[] bytes, int offset, int value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        return bytes;
    }

    // ── The shortcut ────────────────────────────────────────────────────────────────────────────────────────────

    private string DesktopDir => Path.Combine(_root, "Desktop");
    private string MenuDir => Path.Combine(_root, "applications");
    private string IconDir => Path.Combine(_root, "icons");

    private DesktopShortcutService Service(IReadOnlyList<string>? steamRoots = null)
    {
        Directory.CreateDirectory(DesktopDir);
        return new(_ => Task.FromResult<string?>(DesktopDir), MenuDir, IconDir, () => ["/opt/rhi/RHI"], () => steamRoots ?? []);
    }

    private static string IconLine(string shortcut)
        => File.ReadAllLines(shortcut).Single(line => line.StartsWith("Icon=", StringComparison.Ordinal))["Icon=".Length..];

    private DetectedGame Game(string name, byte[]? exe, int? appId = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "games", name)).FullName;
        string? exePath = null;
        if (exe is not null) File.WriteAllBytes(exePath = Path.Combine(folder, "game.exe"), exe);
        return new DetectedGame { Name = name, InstallPath = folder, ExecutablePath = exePath, SteamAppId = appId, Source = appId is null ? "Lutris" : "Steam" };
    }

    private string SteamWithArt(int appId)
    {
        var steam = Path.Combine(_root, "steam");
        var cache = Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache", appId.ToString())).FullName;
        File.WriteAllBytes(Path.Combine(cache, "header.jpg"), [9, 9, 9]);
        return steam;
    }

    [Fact]
    public async Task TheExeIcon_ComesFirst_WrittenAsPngIntoRhisOwnFolder_AndRewrittenWhenCreatedAgain()
    {
        var png = PngIcon(64, 1);
        var game = Game("Night City", WithIcons(new Icon(64, 64, 32, png, Id: 1)), appId: 1091500);
        var service = Service([SteamWithArt(1091500)]);

        var first = await service.CreateAsync(game, "steam:1091500", ShortcutPlace.Desktop);

        var icon = IconLine(first.FilePath!);
        Assert.Equal(IconDir, Path.GetDirectoryName(icon));
        Assert.EndsWith(".png", icon, StringComparison.Ordinal);
        Assert.Equal(png, File.ReadAllBytes(icon)); // the exe's icon beats the Steam art
        Assert.Empty(Directory.EnumerateFiles(IconDir, "*.jpg"));

        var newer = PngIcon(128, 2);
        File.WriteAllBytes(game.ExecutablePath!, WithIcons(new Icon(128, 128, 32, newer, Id: 1)));
        var second = await service.CreateAsync(game, "steam:1091500", ShortcutPlace.ApplicationsMenu);
        Assert.Equal(icon, IconLine(second.FilePath!)); // one icon file per game, both places
        Assert.Equal(newer, File.ReadAllBytes(icon));
    }

    [Fact]
    public async Task AnExeWithoutAReadableIcon_FallsBackToTheSteamArt_ThenRhisIcon_Silently()
    {
        var encrypted = Enumerable.Range(0, 2048).Select(i => (byte)(i * 13 + 7)).ToArray();
        var steamGame = Game("Encrypted Steam", encrypted, appId: 42);
        var steam = await Service([SteamWithArt(42)]).CreateAsync(steamGame, "steam:42", ShortcutPlace.Desktop);
        Assert.Equal(ShortcutOutcome.Created, steam.Outcome);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(IconLine(steam.FilePath!)));

        var plain = await Service().CreateAsync(Game("Native", [0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0]), "folder:Lutris|/g/n", ShortcutPlace.Desktop);
        Assert.Equal(DesktopShortcutService.DefaultIcon, IconLine(plain.FilePath!));
        var noExe = await Service().CreateAsync(Game("No Exe", exe: null), "folder:Lutris|/g/x", ShortcutPlace.Desktop);
        Assert.Equal(DesktopShortcutService.DefaultIcon, IconLine(noExe.FilePath!));
    }

    [Fact]
    public async Task AShortcutThatWasRemoved_LosesRhisIconFile_AtTheNextCreation_AndALiveOneKeepsIt()
    {
        var service = Service();
        var kept = await service.CreateAsync(Game("Kept", WithIcons(new Icon(32, 32, 32, PngIcon(32, 1), Id: 1))), "folder:Lutris|/g/kept", ShortcutPlace.Desktop);
        var gone = await service.CreateAsync(Game("Gone", WithIcons(new Icon(32, 32, 32, PngIcon(32, 2), Id: 1))), "folder:Lutris|/g/gone", ShortcutPlace.ApplicationsMenu);
        var goneIcon = IconLine(gone.FilePath!);
        File.Delete(gone.FilePath!); // the user removed that shortcut
        File.WriteAllBytes(Path.Combine(IconDir, "my-own.png"), [1]); // not RHI's: never touched

        await service.CreateAsync(Game("Third", exe: null), "folder:Lutris|/g/third", ShortcutPlace.Desktop);

        Assert.False(File.Exists(goneIcon));
        Assert.True(File.Exists(IconLine(kept.FilePath!)));
        Assert.True(File.Exists(Path.Combine(IconDir, "my-own.png")));
    }

    [Fact]
    public async Task ASteamArtCopy_IsReplaced_WhenTheExeIconBecomesReadable()
    {
        var game = Game("Later Icon", Enumerable.Repeat((byte)1, 512).ToArray(), appId: 77);
        var service = Service([SteamWithArt(77)]);
        var first = await service.CreateAsync(game, "steam:77", ShortcutPlace.Desktop);
        Assert.EndsWith(".jpg", IconLine(first.FilePath!), StringComparison.Ordinal);

        File.WriteAllBytes(game.ExecutablePath!, WithIcons(new Icon(16, 16, 32, PngIcon(16, 5), Id: 1)));
        var second = await service.CreateAsync(game, "steam:77", ShortcutPlace.Desktop);
        Assert.EndsWith(".png", IconLine(second.FilePath!), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(IconDir, "*.jpg")); // the older copy does not linger
    }
}
