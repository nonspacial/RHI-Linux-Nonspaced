using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class PeArchitectureDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pe-tests-" + Guid.NewGuid().ToString("N"));
    public PeArchitectureDetectorTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData((ushort)0x8664, PeArchitecture.X64)]
    [InlineData((ushort)0x014c, PeArchitecture.X86)]
    [InlineData((ushort)0xAA64, PeArchitecture.Arm64)]
    public void Detect_RecognizesPeMachine(ushort machine, PeArchitecture expected)
    {
        var path = Path.Combine(_root, "test.exe");
        WritePe(path, machine);
        Assert.Equal(expected, PeArchitectureDetector.Detect(path));
    }

    [Fact]
    public void Detect_RejectsNonPe()
    {
        var path = Path.Combine(_root, "notpe.bin");
        File.WriteAllText(path, "ELF-ish");
        Assert.Equal(PeArchitecture.Unknown, PeArchitectureDetector.Detect(path));
    }

    internal static void WritePe(string path, ushort machine, int size = 1024)
    {
        var bytes = new byte[Math.Max(size, 256)];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
