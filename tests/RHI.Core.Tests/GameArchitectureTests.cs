using System.Diagnostics;
using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #167: one bitness answer for every installer. The exe's PE header decides; an Xbox / Game Pass exe is encrypted on
/// disk, so its package's declared architecture (appxmanifest.xml) stands in; the RHI manifest's override still wins.
/// </summary>
public sealed class GameArchitectureTests : IDisposable
{
    // Not MZ: what every MSIXVC game exe looks like on disk.
    internal static readonly byte[] EncryptedExe = [0x13, 0x37, .. Enumerable.Repeat((byte)0xA5, 4096)];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-game-architecture-" + Guid.NewGuid().ToString("N"));

    public GameArchitectureTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    /// <summary>An Xbox card whose exe is encrypted and whose package declares <paramref name="declared"/>.</summary>
    internal static DetectedGame EncryptedXboxGame(string root, PeArchitecture? declared, string owningApp = "Xodus")
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(exe, EncryptedExe);
        return new DetectedGame
        {
            Name = "Encrypted", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton",
            XboxPackage = new XboxPackageInfo("9ENC", owningApp, null, null, Architecture: declared)
        };
    }

    [Theory]
    [InlineData(PeArchitecture.X64)]
    [InlineData(PeArchitecture.X86)]
    public void EncryptedExe_TakesThePackagesDeclaredArchitecture(PeArchitecture declared)
    {
        var game = EncryptedXboxGame(_root, declared);

        Assert.Equal(PeArchitecture.Unknown, PeArchitectureDetector.Detect(game.ExecutablePath));
        Assert.Equal(declared, GameArchitecture.Of(game));
    }

    [Fact]
    public void ReadableExe_HeaderWinsOverThePackage()
    {
        var game = EncryptedXboxGame(_root, PeArchitecture.X64);
        PeArchitectureDetectorTests.WritePe(game.ExecutablePath!, 0x014c);

        Assert.Equal(PeArchitecture.X86, GameArchitecture.Of(game));
    }

    [Fact]
    public void ManifestOverride_WinsOverHeaderAndPackage()
    {
        var game = EncryptedXboxGame(_root, PeArchitecture.X64);
        game.ForcedArchitecture = PeArchitecture.X86;

        Assert.Equal(PeArchitecture.X86, GameArchitecture.Of(game));

        PeArchitectureDetectorTests.WritePe(game.ExecutablePath!, 0x8664);
        Assert.Equal(PeArchitecture.X86, GameArchitecture.Of(game));
    }

    [Fact]
    public void NothingDeclared_OrNoExe_OrNotAPackage_StaysUnknown()
    {
        var undeclared = EncryptedXboxGame(Path.Combine(_root, "a"), declared: null);
        Assert.Equal(PeArchitecture.Unknown, GameArchitecture.Of(undeclared));

        var missingExe = EncryptedXboxGame(Path.Combine(_root, "b"), PeArchitecture.X64);
        File.Delete(missingExe.ExecutablePath!);
        Assert.Equal(PeArchitecture.Unknown, GameArchitecture.Of(missingExe));

        var notXbox = EncryptedXboxGame(Path.Combine(_root, "c"), PeArchitecture.X64);
        notXbox.XboxPackage = null;
        Assert.Equal(PeArchitecture.Unknown, GameArchitecture.Of(notXbox));
    }

    [Fact]
    public void InstallRefusal_OnlyWhenNothingTellsTheBitness()
    {
        Assert.Null(XboxWriteGate.InstallRefusal(EncryptedXboxGame(Path.Combine(_root, "x64"), PeArchitecture.X64)));
        Assert.Equal(XboxWriteGate.EncryptedExecutableMessage,
            XboxWriteGate.InstallRefusal(EncryptedXboxGame(Path.Combine(_root, "none"), declared: null)));

        // Scan re-review nit 4: a non-Xbox card with an unreadable exe is never refused by the Xbox gate.
        var notXbox = EncryptedXboxGame(Path.Combine(_root, "plain"), declared: null);
        notXbox.XboxPackage = null;
        Assert.Null(XboxWriteGate.InstallRefusal(notXbox));
    }

    // The D3DCompiler dependency (ReShade/Luma/Feeder) used to read the encrypted header and refuse before the tool ran.
    [Fact]
    public async Task D3DCompilerTool_OnAnEncryptedX64Exe_RunsForTheDeclaredArchitecture()
    {
        var game = EncryptedXboxGame(_root, PeArchitecture.X64);
        game.PrefixPath = Directory.CreateDirectory(Path.Combine(_root, "pfx")).FullName;
        var ran = 0;
        var installer = new D3DCompilerToolInstaller(new WineDllOverrideService())
        {
            RunTool = (_, _, _) => { ran++; return Task.FromResult(new ExternalProcessResult(1, "", "stand-in tool")); }
        };

        var result = await installer.RunAndVerifyAsync(game, new ProcessStartInfo("/bin/true"), "fake-tool", applyOverride: false, CancellationToken.None);

        Assert.Equal(1, ran);
        Assert.False(result.Success);
        Assert.DoesNotContain("architecture", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Every bitness question about the game goes through GameArchitecture: a direct header read of the game's exe gives
    // Unknown on an encrypted Xbox exe, and a bare ForcedArchitecture ?? read skips the package.
    private static readonly Regex DirectExeRead = new(@"PeArchitectureDetector\.Detect\([^;]*ExecutablePath", RegexOptions.Compiled);
    private static readonly Regex OverrideFallback = new(@"ForcedArchitecture\s*(\?\?(?!=)|is\s+null|==\s*PeArchitecture\.X(86|64)|is\s+PeArchitecture\.X(86|64)\s+or)", RegexOptions.Compiled);

    // Exe selection (which file is the game), not a bitness answer: the probe re-picks an exe the RHI manifest's
    // override disagrees with, by its header.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "src/RHI.Core/Services/GameArchitecture.cs",
        "src/RHI.Platform.Linux/Detection/DetectionHelpers.cs",
    };

    [Fact]
    public void EveryBitnessReadOfTheGame_GoesThroughTheResolver()
    {
        var repo = LocalizationKeyUsageTests.RepoRoot();
        var offending = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(repo, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || Allowed.Contains(relative))
                continue;
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectExeRead.IsMatch(code) || OverrideFallback.IsMatch(code)) offending.Add($"{relative}:{i + 1}: {code}");
            }
        }
        Assert.True(offending.Count == 0, "Game bitness read outside GameArchitecture.Of:\n" + string.Join("\n", offending));
    }
}
