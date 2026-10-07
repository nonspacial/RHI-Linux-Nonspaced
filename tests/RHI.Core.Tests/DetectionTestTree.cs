namespace RHI.Core.Tests;

/// <summary>Disposable scratch directory for detector fixtures (files, symlinks), removed after the test.</summary>
internal sealed class DetectionTestTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "rhi-detect-" + Guid.NewGuid().ToString("N"));

    public DetectionTestTree() => Directory.CreateDirectory(Root);

    public string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Dir(params string[] parts)
    {
        var path = Path_(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string content, params string[] parts)
    {
        var path = Path_(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Bytes(byte[] content, params string[] parts)
    {
        var path = Path_(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A minimal Windows PE stub (MZ header) large enough to rank as an executable.</summary>
    public string Exe(params string[] parts) => Bytes([(byte)'M', (byte)'Z', .. new byte[4096]], parts);

    public string Link(string target, params string[] parts)
    {
        var path = Path_(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateSymbolicLink(path, target);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
