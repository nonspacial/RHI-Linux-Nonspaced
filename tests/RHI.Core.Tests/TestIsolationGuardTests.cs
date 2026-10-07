using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #142: the suite must never write into the owner's real ~/.cache, ~/.local/share or ~/.config.
/// These tests check the module initializer's redirect and drive a representative set of services
/// constructed with their DEFAULT paths, asserting that what they write lands under the per-run root.
/// </summary>
public sealed class TestIsolationGuardTests
{
    [Fact]
    public void LinuxPaths_ResolveUnderPerRunTestRoot()
    {
        TestEnvironmentIsolation.AssertIsolated();

        var root = TestEnvironmentIsolation.Root;
        Assert.False(string.IsNullOrEmpty(root));
        // .NET resolves SpecialFolder.UserProfile from $HOME at call time on Unix; LinuxPaths.Home relies on it.
        Assert.Equal(Path.Combine(root, "home"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.Equal(Path.Combine(root, "home"), LinuxPaths.Home);
        Assert.Equal(Path.Combine(root, "cache"), LinuxPaths.CacheHome);
        Assert.Equal(Path.Combine(root, "data"), LinuxPaths.DataHome);
        Assert.Equal(Path.Combine(root, "config"), LinuxPaths.ConfigHome);
        Assert.Equal(Path.Combine(root, "state"), Environment.GetEnvironmentVariable("XDG_STATE_HOME"));
        foreach (var real in TestEnvironmentIsolation.RealRhiDirectories)
            Assert.False(TestEnvironmentIsolation.IsUnder(real, root), $"real directory {real} must not be inside the test root");
    }

    [Fact]
    public async Task DefaultPathServices_WriteOnlyUnderTestRoot()
    {
        var root = TestEnvironmentIsolation.Root;
        var marker = "rhi142guard" + Guid.NewGuid().ToString("N");
        var before = SnapshotFiles(root);

        // Watch the owner's real rhi-linux directories (read-only) for anything this test writes.
        // Only events that carry this run's marker count, so the real app or another run writing
        // there at the same time cannot make the test flaky.
        var events = new ConcurrentBag<string>();
        var watchers = new List<FileSystemWatcher>();
        foreach (var real in TestEnvironmentIsolation.RealRhiDirectories.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(real) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Created += (_, e) => events.Add(e.FullPath);
                watcher.Changed += (_, e) => events.Add(e.FullPath);
                watcher.Renamed += (_, e) => events.Add(e.FullPath);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var game = CreateGame(Path.Combine(root, "guard-games", marker), marker);
        try
        {
            // Record store (~/.local/share/rhi-linux/deployments.json)
            var records = new LinuxDeploymentRecordStore();
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game) + "-seed",
                GameName = marker,
                Component = ManagedComponentKind.ReShade,
                BinaryDirectory = game.BinaryDirectory!
            });

            // Settings, preference/override stores (~/.config/rhi-linux/...)
            var settings = new LinuxSettingsStore();
            settings.Save(new LinuxAppSettings());
            new GraphicsApiOverrideStore().Set(game, GraphicsApi.DirectX12);
            new OptiScalerVariantPreferenceStore().Set(game, OptiScalerVariant.Nightly);
            new LinuxGameLibraryStore().Save(new LinuxGameLibraryState { FavouriteGames = { marker } });

            // Update log (~/.local/share/rhi-linux/update-log.json)
            var log = new ComponentUpdateLogStore();
            log.Record(new ComponentUpdateLogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                GameName = marker,
                GameKey = marker,
                Component = ManagedComponentKind.ReLimiter,
                ComponentName = "ReLimiter",
                ToVersion = marker
            });

            // Shader service creates its custom-shader tree under ~/.local/share/rhi-linux
            using var unusedHttp = new HttpClient(new Handler(_ => throw new InvalidOperationException("no network")));
            var shaders = new ReShadeShaderPackService(unusedHttp);

            // Add-on cache (~/.cache/rhi-linux/addons/<kind>) — the path the original leak wrote to.
            var payload = MakePePayload(150 * 1024);
            var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var release = JsonSerializer.Serialize(new
            {
                tag_name = "v9.9.9-" + marker,
                name = marker,
                body = "",
                assets = new[] { new { name = "relimiter.addon64", browser_download_url = "https://example.test/" + marker, digest = "sha256:" + digest } }
            });
            using var http = new HttpClient(new Handler(req => req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var addon = new GitHubReShadeAddonDeploymentService(http, records, new GitHubReShadeAddonDefinition(
                ManagedComponentKind.ReLimiter, "ReLimiter", "https://api.github.test/repos/x/relimiter/releases/latest",
                "relimiter.addon64", "relimiter.addon32", 100 * 1024));
            var install = await addon.InstallAsync(game);
            Assert.True(install.Success, install.Message);

            await Task.Delay(500); // let inotify deliver anything that happened under the real directories

            var created = SnapshotFiles(root).Except(before).ToArray();
            string[] expected =
            [
                Path.Combine(root, "data", "rhi-linux", "deployments.json"),
                Path.Combine(root, "data", "rhi-linux", "update-log.json"),
                Path.Combine(root, "config", "rhi-linux", "settings.json"),
                Path.Combine(root, "config", "rhi-linux", "game-api-overrides.json"),
            ];
            foreach (var path in expected)
                Assert.True(File.Exists(path), $"default-path service did not write {path} under the test root");
            Assert.True(TestEnvironmentIsolation.IsUnder(settings.CacheRoot, root));
            Assert.True(TestEnvironmentIsolation.IsUnder(settings.DataRoot, root));
            Assert.True(TestEnvironmentIsolation.IsUnder(shaders.CustomRoot, root));
            Assert.True(Directory.Exists(shaders.CustomRoot));
            Assert.True(TestEnvironmentIsolation.IsUnder(log.FilePath, root));
            Assert.Contains(created, path =>
                path.StartsWith(Path.Combine(root, "cache", "rhi-linux", "addons", "relimiter") + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                path.EndsWith("relimiter.addon64", StringComparison.Ordinal));
            Assert.Contains(created, path => path.EndsWith("relimiter.addon64" + CacheStamp.Suffix, StringComparison.Ordinal));
            Assert.Contains(created, path => path.StartsWith(Path.Combine(root, "config", "rhi-linux") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                             && FileContains(path, marker));

            var leaked = events.Distinct().Where(path => path.Contains(marker, StringComparison.Ordinal) || FileContains(path, marker)).ToArray();
            Assert.True(leaked.Length == 0, "Test wrote into the real user directories: " + string.Join(", ", leaked));
        }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }

    private static HashSet<string> SnapshotFiles(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal)
            : [];

    private static bool FileContains(string path, string marker)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 16 * 1024 * 1024) return false;
            return File.ReadAllText(path).Contains(marker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static DetectedGame CreateGame(string bin, string marker)
    {
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "game.exe");
        var pe = new byte[512];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        pe[0x80] = (byte)'P'; pe[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84);
        File.WriteAllBytes(exe, pe);
        var reshade = MakePePayload(2 * 1024 * 1024);
        Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(reshade, 1024);
        File.WriteAllBytes(Path.Combine(bin, "dxgi.dll"), reshade);
        return new DetectedGame
        {
            Name = marker,
            InstallPath = bin,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            Source = "Steam",
            Runner = "Proton",
            PrefixPath = Path.Combine(bin, "pfx")
        };
    }

    private static byte[] MakePePayload(int size)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var i = 2; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);
        return bytes;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
