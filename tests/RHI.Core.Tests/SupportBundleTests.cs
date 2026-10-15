using System.IO.Compression;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;

namespace RHI.Core.Tests;

/// <summary>
/// #192 (parity T-17): the support bundle. Logs zipped and game data exported with personal details masked (upstream
/// <c>CrashReporter.SanitisePath</c>: profile folder, Steam user ids; here also the user name, SteamID64s, tokens and the
/// private installer app's name), written only into RHI's support folder as new files; the game report as markdown; the
/// fork's CHANGELOG embedded for What's new.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SupportBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-support-{Guid.NewGuid():N}");
    private string Home => Path.Combine(_root, "home", "alice");
    private string Logs => Path.Combine(_root, "state", "logs");
    private string Out => Path.Combine(_root, "state", "support");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private DiagnosticsBundle Bundle(DateTime? at = null)
        => new(Logs, Out, Home, "alice", () => at ?? new DateTime(2026, 10, 15, 12, 30, 0));

    [Fact]
    public void Mask_HidesHomeUserSteamIdsTokensAndTheInstallerAppsName()
    {
        var installer = XboxInstallerApp.FileTag;
        var text = $"{Home}/Games/x.exe; {Home}; /run/media/alice/disk; userdata/123456789/config; " +
                   $"steam 76561198012345678; ghp_{new string('a', 36)}; /opt/{installer}/run.sh; {installer.ToUpperInvariant()}; malice/aliceX";

        var masked = Bundle().Mask(text);

        Assert.Equal("~/Games/x.exe; ~; /run/media/[user]/disk; userdata/[userid]/config; steam [steamid]; [token]; " +
                     $"/opt/{DiagnosticsBundle.InstallerPlaceholder}/run.sh; {DiagnosticsBundle.InstallerPlaceholder}; malice/aliceX", masked);
        Assert.DoesNotContain(installer, masked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogsArchive_HoldsEveryLogMasked_SkipsLinks_AndNeverReplacesAFile()
    {
        Directory.CreateDirectory(Path.Combine(Logs, "launch"));
        File.WriteAllText(Path.Combine(Logs, "rhi.log"), $"started from {Home}/Apps\n");
        File.WriteAllText(Path.Combine(Logs, "rhi.log.1"), "older\n");
        File.WriteAllText(Path.Combine(Logs, "recovery.log"), "recovered\n");
        File.WriteAllText(Path.Combine(Logs, "launch", "tunic-1.log"), "userdata/42/x\n");
        var secret = Path.Combine(_root, "secret.txt");
        File.WriteAllText(secret, "do not ship");
        File.CreateSymbolicLink(Path.Combine(Logs, "linked.log"), secret);

        var first = Bundle().CreateLogsArchive();
        var second = Bundle().CreateLogsArchive();

        Assert.NotNull(first);
        Assert.Equal(Path.Combine(Out, "RHI-Linux-logs-20261015-123000.zip"), first);
        Assert.Equal(Path.Combine(Out, "RHI-Linux-logs-20261015-123000-2.zip"), second);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(first!));
        var entries = ReadZip(first!);
        Assert.Equal(["launch/tunic-1.log", "recovery.log", "rhi.log", "rhi.log.1"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("started from ~/Apps\n", entries["rhi.log"]);
        Assert.Equal("userdata/[userid]/x\n", entries["launch/tunic-1.log"]);
        Assert.DoesNotContain(entries.Values, text => text.Contains("do not ship", StringComparison.Ordinal));
    }

    [Fact]
    public void LogsArchive_KeepsTheNewestPartOfAHugeLog_AndNoLogsMeansNoFile()
    {
        Assert.Null(Bundle().CreateLogsArchive());
        Assert.False(Directory.Exists(Out));

        Directory.CreateDirectory(Logs);
        var big = new string('o', (int)DiagnosticsBundle.MaxBytesPerLog) + "NEWEST";
        File.WriteAllText(Path.Combine(Logs, "rhi.log"), "OLDEST" + big);

        var text = ReadZip(Bundle().CreateLogsArchive()!)["rhi.log"];

        Assert.EndsWith("NEWEST", text);
        Assert.DoesNotContain("OLDEST", text);
        Assert.StartsWith("[… first ", text);
        Assert.Equal(4L * 1024 * 1024, DiagnosticsBundle.MaxBytesPerLog);
    }

    [Fact]
    public void GameDataExport_IsUpstreamsSchema_WithRelativePathsAndMaskedText()
    {
        var install = Directory.CreateDirectory(Path.Combine(Home, "Games", "Tunic")).FullName;
        var exe = Path.Combine(install, "bin", "Tunic.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var game = new DetectedGame
        {
            Name = "Tunic", Source = "Steam", SteamAppId = 553420, InstallPath = install, ExecutablePath = exe,
            GraphicsApi = GraphicsApi.DirectX11, GraphicsApis = [GraphicsApi.DirectX11, GraphicsApi.DirectX12],
            EngineHint = "Unity", UserArchitectureOverride = PeArchitecture.X64
        };

        // A folder named after the user: the export is masked like the logs (upstream exports the folder name).
        var named = new DetectedGame { Name = "Named", Source = "Manual", InstallPath = Directory.CreateDirectory(Path.Combine(_root, "alice")).FullName };

        var path = Bundle().ExportGameData([GameReport.DataRow(game), GameReport.DataRow(named)], "0.9-test");

        using var json = JsonDocument.Parse(ReadZip(path)["rhi_game_data.json"]);
        var root = json.RootElement;
        Assert.Equal("1", root.GetProperty("version").GetString());
        Assert.Equal(2, root.GetProperty("game_count").GetInt32());
        Assert.Equal("[user]", root.GetProperty("games")[1].GetProperty("install_subpath").GetString());
        var row = root.GetProperty("games")[0];
        Assert.Equal("Tunic", row.GetProperty("name").GetString());
        Assert.Equal("Tunic", row.GetProperty("install_subpath").GetString());
        Assert.Equal(Path.Combine("bin", "Tunic.exe"), row.GetProperty("exe_relative").GetString());
        Assert.Equal("DX11", row.GetProperty("api").GetString());
        Assert.Equal(["DX11", "DX12"], row.GetProperty("all_apis").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(64, row.GetProperty("bitness").GetInt32());
        Assert.Equal(553420, row.GetProperty("steam_appid").GetInt32());
        Assert.DoesNotContain(Home, root.GetRawText());
    }

    [Fact]
    public void GameReport_ShowsCorrectionsAndComponents_AndLeavesOutAnXboxCardsCommand()
    {
        var game = new DetectedGame
        {
            Name = "Tunic", Source = "Steam", SteamAppId = 553420, InstallPath = Path.Combine(Home, "Games", "Tunic"),
            UserDisplayName = "Fox", UserArchitectureOverride = PeArchitecture.X86, UserWikiName = "Tunic (wiki)",
            LaunchCommand = $"wine {Home}/Games/Tunic/Tunic.exe"
        };
        var record = new LinuxDeploymentRecord
        {
            Component = ManagedComponentKind.ReShade, Version = "6.4.1",
            Files = [new LinuxDeployedFile { Path = Path.Combine(game.InstallPath, "dxgi.dll") }]
        };

        var report = Bundle().Mask(GameReport.Markdown(game, [record], "0.9-test", " It crashes. ", new DateTime(2026, 10, 15)));

        Assert.StartsWith("# RHI-Linux game report — Fox", report);
        Assert.Contains("- **Detected name:** Tunic", report);
        Assert.Contains("| Bitness | 32-bit |", report);
        Assert.Contains("- Bitness: 32-bit (set in Edit game)", report);
        Assert.Contains("- Name: Tunic → Fox", report);
        Assert.Contains("| ReShade | 6.4.1 | dxgi.dll |", report);
        Assert.Contains("| Launch command | `wine ~/Games/Tunic/Tunic.exe` |", report);
        Assert.Contains("## User note\n\nIt crashes.", report.Replace("\r\n", "\n"));
        Assert.DoesNotContain(Home, report);

        var xbox = new DetectedGame
        {
            Name = "Forza", Source = "Xbox", InstallPath = "/g/forza", LaunchCommand = $"env 'A=1' '/opt/{XboxInstallerApp.FileTag}/run.sh' --play 9X",
            XboxPackage = new XboxPackageInfo("9X", XboxInstallerApp.DisplayName, "x", "/j")
        };
        var xboxReport = GameReport.Markdown(xbox, [], "0.9-test", null, DateTime.Now);
        Assert.DoesNotContain(XboxInstallerApp.FileTag, xboxReport, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--play", xboxReport);
        Assert.Contains("None installed by RHI.", xboxReport);
    }

    [Fact]
    public async Task CopyGameReport_SavesItAndCopiesTheMaskedText_AndTheCardMenuOffersIt()
    {
        using var fx = await ViewModelFixture.StartAsync([new DetectedGame { Name = "Tunic", Source = "Manual", InstallPath = Directory.CreateDirectory(Path.Combine(Home, "Games", "Tunic")).FullName, IsManuallyAdded = true }]);
        var support = fx.Vm.Support;
        support.Bundle = Bundle();
        var game = fx.Vm.Games.Single().Game;
        Assert.Contains(fx.Vm.CardMenuFor(fx.Vm.Games.Single()), item => item.Id == "game-report");
        DetectedGame? asked = null;
        support.ShowReportDialogAsync = g => { asked = g; return Task.CompletedTask; };
        await support.ReportCommand.ExecuteAsync(game);
        Assert.Same(game, asked);

        string? copied = null;
        var line = await support.CopyReportAsync(game, "note", text => { copied = text; return Task.FromResult(true); });

        var saved = Assert.Single(Directory.GetFiles(Out, "RHI-Linux-report-Tunic-*.md"));
        Assert.Equal($"Report for Tunic copied to the clipboard and saved: {saved}", line);
        Assert.Equal(File.ReadAllText(saved), copied);
        Assert.Contains("## User note", copied);
        Assert.Contains("`~/Games/Tunic`", copied); // masked before it reaches the clipboard
        Assert.DoesNotContain(Home, copied);

        line = await support.CopyReportAsync(game, null, _ => Task.FromResult(false));
        Assert.StartsWith("The clipboard is unavailable in this session; the file is saved: ", line);
        Assert.Equal(2, Directory.GetFiles(Out, "*.md").Length);
    }

    [Fact]
    public async Task CopyLogs_AndExportGameData_PutThePathOnTheClipboard()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var support = fx.Vm.Support;
        support.Bundle = Bundle();
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.Equal(UiLocalizationService.Text("Shell.NoLogsYet"), await support.CopyLogsAsync(Copy));
        Directory.CreateDirectory(Logs);
        File.WriteAllText(Path.Combine(Logs, "rhi.log"), "x");
        var line = await support.CopyLogsAsync(Copy);
        Assert.Equal($"Logs zipped; the file's path is on the clipboard: {copied.Single()}", line);
        Assert.True(File.Exists(copied.Single()));

        line = await support.ExportGameDataAsync(Copy);
        Assert.Equal($"Data for 0 game(s) exported; the file's path is on the clipboard: {copied[1]}", line);
        Assert.StartsWith(Out, copied[1]);
    }

    [Fact]
    public void WhatsNew_ShowsTheForksOwnChangelog()
    {
        var text = RHI.Desktop.Views.ChangelogText.Load();
        Assert.StartsWith("# Changelog", text);
        var blocks = RHI.Desktop.Views.ChangelogText.Blocks(text);
        Assert.Equal((1, "Changelog"), blocks[0]);
        Assert.Contains(blocks, block => block.Level == 2 && block.Text.StartsWith("Wave 4", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, block => block.Text.Contains("**", StringComparison.Ordinal) || block.Text.Contains("[!NOTE]", StringComparison.Ordinal));
    }

    [Fact]
    public void PortugueseUi_HasTheSupportLines()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Copiar registros", UiLocalizationService.Text("Shell.CopyLogs"));
            Assert.Equal("Registros compactados; o caminho do arquivo está na área de transferência: /x.zip",
                UiLocalizationService.Format("Shell.LogsCopied", "/x.zip"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        });
    }
}
