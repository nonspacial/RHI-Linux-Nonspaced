using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>Minimal, dependency-free PE machine detector used to select 32/64-bit ReShade and RenoDX files.</summary>
public static class PeArchitectureDetector
{
    public static PeArchitecture Detect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return PeArchitecture.Unknown;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return PeArchitecture.Unknown; // MZ
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset + 6 > stream.Length) return PeArchitecture.Unknown;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return PeArchitecture.Unknown; // PE\0\0
            return reader.ReadUInt16() switch
            {
                0x014c => PeArchitecture.X86,
                0x8664 => PeArchitecture.X64,
                0xAA64 => PeArchitecture.Arm64,
                _ => PeArchitecture.Unknown
            };
        }
        catch { return PeArchitecture.Unknown; }
    }
}
