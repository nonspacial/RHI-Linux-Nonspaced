using RHI.Core.Deployment;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Journal undo of a Wine override (#23 registry remainder) under the editor's rules (#24, #30), and the PT-BR
/// presentation of every message the registry work added.
/// </summary>
public sealed class WineRegistryJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-wine-journal-" + Guid.NewGuid().ToString("N"));

    public WineRegistryJournalTests() => Directory.CreateDirectory(_root);

    private string Prefix()
    {
        var prefix = Path.Combine(_root, "pfx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return prefix;
    }

    [Fact]
    public async Task InterruptedOperation_WhileWineRunsInThePrefix_IsDeferredAndUndoneAtTheNextStart()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        var operation = await journal.BeginAsync("UAL install: Game");
        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "winmm.dll", "Game.exe");
        operation.AbandonAsIfCrashed();

        // Next start, the game is running: the editor sees wineserver even where the compensator's own check did not.
        var running = new WineDllOverrideService(new WineRegistryEditor(_ => true));
        var deferred = await new DeploymentJournal(journalDir, [new WineOverrideCompensator(running, _ => false)]).RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.Deferred, Assert.Single(deferred.Entries).Outcome);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));

        var later = await LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), journalDir, _ => false).RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(later.Entries).Outcome);
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.Empty(Directory.GetFiles(journalDir));
    }

    [Fact]
    public async Task InterruptedOperation_ThenTheUserSetTheirOwnValue_KeepsIt()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal-user");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        var operation = await journal.BeginAsync("OptiScaler install: Game");
        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "dxgi.dll", "Game.exe");
        operation.AbandonAsIfCrashed();
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "dxgi", "native", "Game.exe"));

        var report = await LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), journalDir, _ => false).RecoverAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, entry.Outcome);
        Assert.Equal("native", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task EnsureWithAnOperation_JournalsTheUndoStepBeforeWriting()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal-order");
        var editor = new WineRegistryEditor(_ => false);
        var overrides = new WineDllOverrideService(editor);
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        await using var operation = await journal.BeginAsync("Test: Game");
        string? journaledBeforeWrite = null;
        editor.InsideLockForTesting = _ => journaledBeforeWrite = File.ReadAllText(Path.Combine(journalDir, operation.Id + ".json"));

        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "d3d9.dll", "Game.exe");

        Assert.Contains(WineOverrideCompensator.KindName, journaledBeforeWrite);
        Assert.Contains("d3d9", journaledBeforeWrite);
    }

    public static TheoryData<string> Messages() => new()
    {
        WineRegistryEditor.PrefixInUseRefusalPrefix + "/jogos/prefixo",
        WineRegistryEditor.BusyRefusalPrefix + "/jogos/prefixo",
        WineRegistryEditor.MissingHiveMessage,
        D3DCompilerToolInstaller.OverrideNotSetWineRunningPrefix + "/jogos/prefixo",
        LinuxDeploymentRecovery.JournalUnavailablePrefix + "E/S negada); nothing was changed.",
        // The kept-value note rides on a removal message (here one the platform writes in Portuguese).
        "Universal MFG removido e o override Wine anterior foi restaurado."
            + WineDllOverrideService.KeptValueNotes(new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, "dxgi", "builtin")),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void RegistryMessage_HasAPortuguesePresentation(string message)
    {
        var pt = UiLocalizationService.PresentFor("pt-BR", message);

        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt);
        var english = UiLocalizationService.EnglishMarkersIn(pt).Where(w => !w.Equals("no", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(english.Length == 0, $"English left ({string.Join(", ", english)}): {pt}");
        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("en-US"), UiLocalizationService.PresentFor("en-US", message));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
