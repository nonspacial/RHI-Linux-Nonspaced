using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

// w6 review finding 3: the guards of "Deploy ReShade.ini" on ReShade's record (#203) — the write is journaled, the record
// carries the operation stamp (its commit point), a file RHI creates is recorded as RHI's, and the merge keeps only what
// the file still lists. Most are the reviewer's probes.
public sealed partial class ReShadeChannelTests
{
    private (UserIniService Ini, string JournalDir) DeployService(string name, LinuxDeploymentRecordStore store, bool crashAtCommit = false)
    {
        var templates = new UserIniTemplates(Path.Combine(_root, name, "inis"));
        File.WriteAllText(templates.EnsureSeeded(UserIniTemplate.ReShade), "[GENERAL]\nEffectSearchPaths=.\\tmpl\\**\n");
        var journalDir = Path.Combine(_root, name, "journal");
        return (new UserIniService(templates, store, _ => null, new DeploymentJournal(journalDir) { CrashAtCommitForTesting = crashAtCommit }), journalDir);
    }

    [Fact]
    public async Task Deploy_LedgerSaveFails_PutsTheIniBack()
    {
        // Journaled: a Deploy whose record cannot be saved takes its write back.
        var (game, store, service, _, options) = IniUpdateGame("rv-blocked", packs: false);
        File.WriteAllText(IniOf(game), "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n");
        Assert.True((await service.InstallAsync(game, options)).Success);
        var before = File.ReadAllText(IniOf(game));
        var (ini, _) = DeployService("rv-blocked", store);

        using (LedgerWriteBlocker.Block(store, "records.json"))
            await Assert.ThrowsAnyAsync<IOException>(() => ini.DeployReShadeIniAsync(game, new LinuxAppSettings()));

        Assert.Equal(before, File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task Deploy_CrashAtCommit_RecoveryKeepsTheWriteTheStampedRecordDescribes()
    {
        // The record saved before the crash carries this operation's stamp, so recovery counts the Deploy committed and
        // keeps its write; the record's hash is the file's. Removal then still gives back the user's original file.
        var (game, store, service, _, options) = IniUpdateGame("rv-crash", packs: false);
        const string original = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n";
        File.WriteAllText(IniOf(game), original);
        Assert.True((await service.InstallAsync(game, options)).Success);
        var (ini, journalDir) = DeployService("rv-crash", store, crashAtCommit: true);

        await Assert.ThrowsAnyAsync<Exception>(() => ini.DeployReShadeIniAsync(game, new LinuxAppSettings()));
        await LinuxDeploymentRecovery.RunAtStartupAsync(new DeploymentJournal(journalDir), store, null);

        Assert.Contains(@".\tmpl\**", File.ReadAllText(IniOf(game)));
        var entry = store.Find(game, ManagedComponentKind.ReShade)!.Files.Single(f => Path.GetFileName(f.Path) == "ReShade.ini");
        Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(IniOf(game)), entry.Sha256!));
        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.Equal(original, File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task UserDeletedIni_DeployCreatesIt_RemovalDeletesIt()
    {
        // No file before the Deploy: the new file is RHI's (created-by-RHI recorded, its hash taken), so removal deletes it.
        var (game, store, service, _, options) = IniUpdateGame("rv-deleted", packs: false);
        File.WriteAllText(IniOf(game), "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n");
        Assert.True((await service.InstallAsync(game, options)).Success);
        File.Delete(IniOf(game));
        var templates = new UserIniTemplates(Path.Combine(_root, "rv-deleted", "inis"));
        Assert.True((await new UserIniService(templates, store, _ => null).DeployReShadeIniAsync(game, new LinuxAppSettings())).Success);
        Assert.True(File.Exists(IniOf(game)));

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.False(File.Exists(IniOf(game)));
    }

    private LinuxDeploymentRecord IniRecord(string name, string iniText, Dictionary<string, string> metadata)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ReShade.ini");
        File.WriteAllText(path, iniText);
        var record = new LinuxDeploymentRecord { Files = [new LinuxDeployedFile { Path = path, Sha256 = "00" }] };
        foreach (var (key, value) in metadata) record.Metadata[key] = value;
        return record;
    }

    private static ReShadeIniApplyResult Applied(string path, IReadOnlyList<string> addedEffects, bool effectKeyCreated = false)
        => new(path, ExistedBefore: true, CreatedByRhi: false, EffectKeyCreated: effectKeyCreated, TextureKeyCreated: false,
            addedEffects, [], FileHash.Sha256Hex(path));

    [Fact]
    public void RecordIniWrite_KeepsOnlyEarlierAddedPathsTheFileStillLists()
    {
        // A path RHI added earlier that the user took out is no longer RHI's: removal must not take it again if the
        // user puts it back themselves.
        var record = IniRecord("rv-merge", "[GENERAL]\nEffectSearchPaths=.\\b\\**,.\\c\\**\n",
            new() { ["ReShade.IniEffectPathsAdded"] = JsonSerializer.Serialize(new[] { @".\a\**", @".\b\**" }) });

        Assert.True(ReShadeDeploymentService.RecordIniWrite(record, Applied(record.Files[0].Path, [@".\c\**"]), hashBefore: null));

        Assert.Equal([@".\b\**", @".\c\**"], JsonSerializer.Deserialize<string[]>(record.Metadata["ReShade.IniEffectPathsAdded"])!);
    }

    [Fact]
    public void RecordIniWrite_AKeyRhiCreated_IsNoLongerRhisOnceTheFileHasNoSuchKey()
    {
        var gone = IniRecord("rv-key-gone", "[GENERAL]\nTextureSearchPaths=.\\t\\**\n",
            new() { ["ReShade.IniEffectKeyCreated"] = bool.TrueString });
        var kept = IniRecord("rv-key-kept", "[GENERAL]\nEffectSearchPaths=.\\x\\**\n",
            new() { ["ReShade.IniEffectKeyCreated"] = bool.TrueString });

        ReShadeDeploymentService.RecordIniWrite(gone, Applied(gone.Files[0].Path, []), hashBefore: null);
        ReShadeDeploymentService.RecordIniWrite(kept, Applied(kept.Files[0].Path, []), hashBefore: null);

        Assert.Equal(bool.FalseString, gone.Metadata["ReShade.IniEffectKeyCreated"]);
        Assert.Equal(bool.TrueString, kept.Metadata["ReShade.IniEffectKeyCreated"]);
    }
}
