using RHI.Core.Services;
using RHI.Platform.Linux.Detection.Launchers;

namespace RHI.Core.Tests;

/// <summary>#247: the Epic Games Launcher reader's own rules (install log, URL handler, record shape).</summary>
public sealed class EpicLauncherRecordsTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task InstallLog_LastEntryPerInstallationWins_OnlyALiteralTrueIsComplete()
    {
        var log = _tree.File("\uFEFF" + string.Join("\r\n",
            """{"version":1,"format":"installations"}""",
            """{"installationId":"a","complete":false,"dir":"C:\\A","artifactId":"artA"}""",
            "",
            """{"installationId":"b","complete":"true","dir":"C:\\B","artifactId":"artB"}""",
            """{"installationId":7,"complete":true,"dir":"C:\\N"}""",
            """["installationId","c"]""",
            """{"installationId":"A","complete":true,"dir":"C:\\A2","namespace":"ns","artifactId":"artA"}""") + "\r\n", "installations.jsonl");

        var (installs, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(EpicLauncherRecords.ReadInstallLog(log)));

        Assert.Empty(warnings);
        // Ids are matched ignoring case (the launcher's GUIDs change case between writers); the later entry wins.
        Assert.Equal(2, installs.Count);
        var a = Assert.Single(installs, i => i.ArtifactId == "artA");
        Assert.True(a.Complete);
        Assert.Equal(@"C:\A2", a.Dir);
        Assert.Equal("ns", a.Namespace);
        Assert.False(Assert.Single(installs, i => i.InstallationId == "b").Complete); // "true" as text is not true
    }

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win32\\EpicGamesLauncher.exe\" %1",
        "C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win32\\EpicGamesLauncher.exe")]
    [InlineData("C:\\EGL\\EpicGamesLauncher.exe %1", "C:\\EGL\\EpicGamesLauncher.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("rundll32 url.dll", null)]
    [InlineData("", null)]
    public void UrlHandlerCommand_NamesItsProgram(string command, string? expected)
        => Assert.Equal(expected, EpicLauncherRecords.HandlerProgram(command));

    [Fact]
    public void StoreNames_TheProviderMatchesTheLaunchersExecutable()
    {
        Assert.Equal("Epic Games Launcher", StoreLauncherRecords.DisplayName(StoreLauncher.Epic));
        // Lutris and Steam-shortcut parents are matched on this name: it must be what the launcher's exe is known as.
        Assert.Equal(RHI.Platform.Linux.Settings.LinuxGameIdentityService.InferProviderFromExecutable("/p/EpicGamesLauncher.exe"),
            StoreLauncherRecords.ProviderName(StoreLauncher.Epic));
    }

    [Fact]
    public async Task AManifestThatIsNotJson_IsSkippedWithAWarning()
    {
        var item = _tree.File("{\"DisplayName\": \"Half", "bad.item");

        var (manifest, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(EpicLauncherRecords.ReadManifest(item)));

        Assert.Null(manifest);
        Assert.Contains(warnings, w => w.Contains(item + " unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AManifestPastItsCap_IsNotRead()
    {
        var item = _tree.Path_("big.item");
        using (var file = File.Create(item)) file.SetLength(4L * 1024 * 1024 + 1);

        var (manifest, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(EpicLauncherRecords.ReadManifest(item)));

        Assert.Null(manifest);
        Assert.Contains(warnings, w => w.Contains(item + " is larger than 4194304 bytes", StringComparison.Ordinal));
    }
}
