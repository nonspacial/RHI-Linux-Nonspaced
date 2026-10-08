using System.Diagnostics;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// Opens a directory through the desktop environment without flattening paths that contain spaces.
/// The v0.5.x UI passed a raw argument string to xdg-open, so game folders such as
/// "The Weird Hunter Demo" were split into multiple arguments and failed to open.
/// </summary>
public static class LinuxDesktopOpener
{
    // Fixed texts first, values last, so the UI's PT-BR presentation translates them.
    public const string UnsupportedLinkPrefix = "Unsupported link: ";
    public const string DirectoryMissingPrefix = "Directory does not exist: ";
    public const string NoOpenerMessage = "No supported desktop opener could be started.";

    internal static IReadOnlyList<ProcessStartInfo> BuildCandidates(string directory)
    {
        var path = Path.GetFullPath(directory);
        return
        [
            Build("xdg-open", path),
            Build("gio", "open", path),
            Build("kde-open6", path),
            Build("kioclient6", "exec", path),
            Build("kde-open5", path),
        ];
    }


    internal static IReadOnlyList<ProcessStartInfo> BuildUriCandidates(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only http/https links are allowed.", nameof(uri));
        var value = uri.AbsoluteUri;
        return
        [
            Build("xdg-open", value),
            Build("gio", "open", value),
            Build("kde-open6", value),
            Build("kioclient6", "exec", value),
            Build("kde-open5", value),
        ];
    }

    public static bool TryOpenUri(string url, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = UnsupportedLinkPrefix + url;
            return false;
        }

        Exception? lastError = null;
        foreach (var startInfo in BuildUriCandidates(uri))
        {
            try
            {
                var process = Process.Start(startInfo);
                if (process is not null)
                {
                    process.Dispose();
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                lastError = ex;
            }
        }
        error = lastError?.Message ?? NoOpenerMessage;
        return false;
    }

    public static bool TryOpenDirectory(string directory, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            error = DirectoryMissingPrefix + directory;
            return false;
        }

        Exception? lastError = null;
        foreach (var startInfo in BuildCandidates(directory))
        {
            try
            {
                var process = Process.Start(startInfo);
                if (process is not null)
                {
                    process.Dispose();
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                lastError = ex;
            }
        }

        error = lastError?.Message ?? NoOpenerMessage;
        return false;
    }

    private static ProcessStartInfo Build(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
