using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One custom OptiScaler build (#271, upstream 2.9.2 <c>OsCustomBuild</c>).</summary>
/// <param name="Slug">The folder name and the key of its version id (<c>Custom:&lt;slug&gt;</c>).</param>
/// <param name="Directory">The build root: the folder that held OptiScaler.dll, copied into RHI's data folder.</param>
/// <param name="Origin">Where it came from: the link without its query string, or the local path.</param>
/// <param name="DllVersion">OptiScaler.dll's product (else file) version; null when it has none.</param>
/// <param name="NeedsNrDll">The "DLSS NR DLL" tick: NVIDIA's nvngx_dlssnr.dll is installed with it.</param>
/// <param name="LooksLikeOptiScaler">OptiScaler.dll identifies itself as OptiScaler (a note when not; the build is kept).</param>
public sealed record OptiScalerCustomBuild(
    string Slug,
    string Name,
    string Directory,
    string Origin,
    DateTime AddedUtc,
    string? DllVersion,
    bool NeedsNrDll,
    bool IsOs10,
    bool LooksLikeOptiScaler)
{
    public OptiScalerVariant Variant => OptiScalerVariant.Custom(Slug);

    /// <summary>The name in the version lists (upstream "Custom: &lt;name&gt;").</summary>
    public string Label => "Custom: " + Name;

    /// <summary>The version a game's record carries: the DLL's version, else the date the build was added.</summary>
    public string RecordedVersion => string.IsNullOrWhiteSpace(DllVersion) ? "custom-" + AddedUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) : DllVersion!;
}

/// <summary>The build that was added or replaced and a note to show (the DLL does not say it is OptiScaler), or why it failed.</summary>
public sealed record OptiScalerCustomImportResult(OptiScalerCustomBuild? Build, string? Error, string? Note = null)
{
    public bool Succeeded => Build is not null;
}

/// <summary>
/// #271 custom OptiScaler builds (upstream 2.9.2 <c>OsCustomBuildService</c>): any OptiScaler build the user adds from a
/// download link, a zip/7z/rar, a folder or a bare OptiScaler.dll. Each is a fixed copy RHI installs like a built-in
/// version and never checks for updates (upstream: keeping it legitimate and current is up to the user). Stored under RHI's
/// data folder, <c>$XDG_DATA_HOME/rhi-linux/optiscaler-custom/&lt;slug&gt;/</c>: <c>build/</c> (the build root) and
/// <c>rhi_custom_build.json</c> (name, origin, date, DLL version, the DLSS NR DLL tick). The folder is the list.
/// Linux deviations: links must be HTTPS (upstream also takes HTTP) and are capped at 512 MB; a folder is refused when it
/// is larger than an OptiScaler build can be; OptiScaler.dll must be a Windows DLL (MZ header); archives go through the
/// host extractor's time and size limits, and links or paths that would leave the build folder are never copied.
/// </summary>
public sealed class OptiScalerCustomBuildStore
{
    public const string MetaFileName = "rhi_custom_build.json";
    public const string BuildFolderName = "build";
    public const int MaxSearchDepth = 3;
    /// <summary>Largest build folder RHI copies (real OptiScaler 10 builds are well under 200 MB).</summary>
    public const long MaxBuildBytes = 512L * 1024 * 1024;
    public const int MaxBuildFiles = 4000;

    // Messages (fixed text first; each has a PT-BR presentation row).
    public const string NotFoundMessage = "That file, folder or link couldn't be found.";
    public const string NotHttpsMessage = "That isn't a web link RHI can download (it must start with https://).";
    public const string WebPageMessage = "That link opens a web page, not a file. Use the direct download link (right-click the download button › Copy link).";
    public const string DownloadFailedPrefix = "The download failed: ";
    public const string NoDllMessage = "No OptiScaler.dll was found in it (looked up to 3 folders deep).";
    public const string NotWindowsDllMessage = "The OptiScaler.dll in it is not a Windows DLL, so it was not added.";
    public const string TooLargeMessage = "That folder is larger than an OptiScaler build, so it was not added. Choose the folder that holds OptiScaler.dll.";
    public const string NotOptiScalerNote = "Note: this OptiScaler.dll doesn't identify itself as OptiScaler. It was added anyway; make sure it's the build you meant.";
    public const string CouldNotAddPrefix = "Couldn't add it: ";
    public const string UnknownBuildMessage = "That custom OptiScaler build no longer exists.";
    public const string NameRequiredMessage = "Give the build a name.";

    private readonly string _root;
    private readonly object _gate = new();
    private List<OptiScalerCustomBuild>? _builds;

    public OptiScalerCustomBuildStore(string? root = null)
        => _root = root ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "optiscaler-custom");

    public string Root => _root;

    /// <summary>Raised after a build is added, replaced, renamed, re-ticked or removed (any thread).</summary>
    public event Action? Changed;

    /// <summary>Every build, by name.</summary>
    public IReadOnlyList<OptiScalerCustomBuild> All
    {
        get { lock (_gate) return _builds ??= Scan(); }
    }

    /// <summary>The build a version names (<c>Custom:&lt;slug&gt;</c>), or null (not a custom build, or removed).</summary>
    public OptiScalerCustomBuild? Get(OptiScalerVariant variant)
        => variant.IsCustom && variant.Key is { } slug ? All.FirstOrDefault(build => build.Slug == slug) : null;

    /// <summary>Re-reads the folder (another RHI window, or a change outside RHI).</summary>
    public void Reload()
    {
        lock (_gate) _builds = Scan();
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { AppLog.Warn("optiscaler", "Custom OptiScaler build listener failed", ex); }
    }

    private List<OptiScalerCustomBuild> Scan()
    {
        var list = new List<OptiScalerCustomBuild>();
        try
        {
            if (!System.IO.Directory.Exists(_root)) return list;
            foreach (var dir in System.IO.Directory.GetDirectories(_root))
            {
                var slug = Path.GetFileName(dir);
                if (!OptiScalerVariant.IsSafeKey(slug) || new DirectoryInfo(dir).LinkTarget is not null) continue;
                if (Load(dir) is { } build) list.Add(build);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("optiscaler", "Custom OptiScaler builds could not be listed", ex);
        }
        return list.OrderBy(build => build.Name, StringComparer.OrdinalIgnoreCase).ThenBy(build => build.Slug, StringComparer.Ordinal).ToList();
    }

    private static OptiScalerCustomBuild? Load(string slugDir)
    {
        try
        {
            var buildDir = Path.Combine(slugDir, BuildFolderName);
            var meta = ReadMeta(slugDir);
            if (meta is null || !File.Exists(Path.Combine(buildDir, "OptiScaler.dll"))) return null;
            return new OptiScalerCustomBuild(
                Path.GetFileName(slugDir),
                string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileName(slugDir) : meta.Name.Trim(),
                buildDir,
                meta.Origin ?? "",
                meta.AddedUtc,
                meta.DllVersion,
                meta.NeedsNrDll,
                System.IO.Directory.Exists(Path.Combine(buildDir, "OptiScaler")),
                meta.LooksLikeOptiScaler);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("optiscaler", $"Custom OptiScaler build skipped: {slugDir}", ex);
            return null;
        }
    }

    private static Meta? ReadMeta(string slugDir)
    {
        var path = Path.Combine(slugDir, MetaFileName);
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
        return JsonSerializer.Deserialize<Meta>(File.ReadAllText(path));
    }

    private sealed class Meta
    {
        public string Name { get; set; } = "";
        public string? Origin { get; set; }
        public DateTime AddedUtc { get; set; }
        public string? DllVersion { get; set; }
        public bool NeedsNrDll { get; set; }
        public bool LooksLikeOptiScaler { get; set; } = true;
    }

    // ── Add / replace ─────────────────────────────────────────────────────────

    /// <summary>A default name for a source: the file, folder or link's file name without its extension (upstream <c>SuggestName</c>).</summary>
    public static string SuggestName(string source)
    {
        source = source.Trim().Trim('"');
        string raw;
        if (IsLink(source, out var uri)) raw = Path.GetFileName(Uri.UnescapeDataString(uri!.AbsolutePath));
        else raw = System.IO.Directory.Exists(source) ? Path.GetFileName(source.TrimEnd('/')) : Path.GetFileName(source);
        foreach (var ext in new[] { ".zip", ".7z", ".rar", ".dll" })
            if (raw.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) raw = raw[..^ext.Length];
        if (raw.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase) || raw.Length == 0) raw = "OptiScaler build";
        return raw.Length > 60 ? raw[..60] : raw;
    }

    /// <summary>An http(s) link (what the link box takes; only HTTPS is downloaded).</summary>
    public static bool IsLink(string source, out Uri? uri)
    {
        uri = null;
        return Uri.TryCreate(source.Trim(), UriKind.Absolute, out uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>
    /// Adds a build from a link, a zip/7z/rar, a folder or an OptiScaler.dll; with <paramref name="replace"/> that build's
    /// files are replaced and its name, id and DLSS NR DLL tick stay (upstream <c>ImportAsync</c>). Nothing in RHI's list
    /// changes until the new copy is complete: it is built in <c>&lt;slug&gt;.tmp</c> and swapped in.
    /// </summary>
    public async Task<OptiScalerCustomImportResult> ImportAsync(
        string source,
        string name,
        OptiScalerVariant? replace,
        HttpClient http,
        IOptiScalerArchiveExtractor extractor,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        source = source.Trim().Trim('"');
        var previous = replace is { } replaced ? Get(replaced) : null;
        if (replace is not null && previous is null) return new(null, UnknownBuildMessage);
        if (previous is null && string.IsNullOrWhiteSpace(name)) return new(null, NameRequiredMessage);
        var work = Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "optiscaler-custom-import", Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(work);
            string origin;
            string input;
            if (IsLink(source, out var uri))
            {
                if (uri!.Scheme != Uri.UriSchemeHttps) return new(null, NotHttpsMessage);
                progress?.Report("Downloading...");
                var (error, saved) = await DownloadAsync(http, uri, Path.Combine(work, "dl"), cancellationToken).ConfigureAwait(false);
                if (error is not null) return new(null, error);
                input = saved;
                origin = uri.GetLeftPart(UriPartial.Path); // no query string (Discord links carry signatures)
            }
            else if (System.IO.Directory.Exists(source) || File.Exists(source))
            {
                input = Path.GetFullPath(source);
                origin = input;
            }
            else return new(null, NotFoundMessage);

            string searchRoot;
            if (System.IO.Directory.Exists(input)) searchRoot = input;
            else if (input.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                searchRoot = Path.Combine(work, "dll");
                System.IO.Directory.CreateDirectory(searchRoot);
                AtomicFile.Copy(input, Path.Combine(searchRoot, "OptiScaler.dll"));
            }
            else
            {
                progress?.Report("Unpacking...");
                searchRoot = Path.Combine(work, "x");
                System.IO.Directory.CreateDirectory(searchRoot);
                await extractor.ExtractAllAsync(input, searchRoot, cancellationToken).ConfigureAwait(false);
                OptiScalerPackageProvider.NormalizeBackslashEntries(searchRoot);
            }

            var dll = FindOptiScalerDll(searchRoot);
            if (dll is null) return new(null, NoDllMessage);
            if (!BinarySignatureInspector.HasPeSignature(dll, 64 * 1024)) return new(null, NotWindowsDllMessage);
            var buildRoot = Path.GetDirectoryName(dll)!;
            if (!FitsBuildLimits(buildRoot)) return new(null, TooLargeMessage);

            progress?.Report("Copying...");
            System.IO.Directory.CreateDirectory(_root);
            var slug = previous?.Slug ?? MakeSlug(name);
            var slugDir = Path.Combine(_root, slug);
            var tmpDir = slugDir + ".tmp";
            if (System.IO.Directory.Exists(tmpDir)) System.IO.Directory.Delete(tmpDir, recursive: true); // RHI's own staging folder
            var tmpBuild = Path.Combine(tmpDir, BuildFolderName);
            CopyTree(buildRoot, tmpBuild);

            var builtDll = Path.Combine(tmpBuild, "OptiScaler.dll");
            var info = PeVersionReader.Read(builtDll);
            var looksRight = BinarySignatureInspector.LooksLikeOptiScaler(builtDll);
            var meta = new Meta
            {
                Name = previous?.Name ?? name.Trim(),
                Origin = origin,
                AddedUtc = DateTime.UtcNow,
                DllVersion = !string.IsNullOrWhiteSpace(info?.ProductVersion) ? info!.ProductVersion!.Trim() : info?.FileVersion?.Trim(),
                // An F5-style forwarder in the build means it expects NVIDIA's NR DLL beside it (upstream).
                NeedsNrDll = (previous?.NeedsNrDll ?? false) || File.Exists(Path.Combine(tmpBuild, "nvngx.dll_dlssnr.dll")),
                LooksLikeOptiScaler = looksRight,
            };
            AtomicFile.WriteAllText(Path.Combine(tmpDir, MetaFileName), JsonSerializer.Serialize(meta, MetaJson));

            var oldDir = slugDir + ".old";
            if (System.IO.Directory.Exists(oldDir)) System.IO.Directory.Delete(oldDir, recursive: true);
            if (System.IO.Directory.Exists(slugDir)) System.IO.Directory.Move(slugDir, oldDir);
            try
            {
                BeforeSwapForTesting?.Invoke();
                System.IO.Directory.Move(tmpDir, slugDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // w14 final review m6: a failed Replace puts the build that was there back.
                if (!System.IO.Directory.Exists(slugDir) && System.IO.Directory.Exists(oldDir)) System.IO.Directory.Move(oldDir, slugDir);
                TryDelete(tmpDir);
                throw;
            }
            TryDelete(oldDir);
            AppLog.Current?.Write("optiscaler", $"{(previous is null ? "Added" : "Replaced")} custom OptiScaler build '{meta.Name}' ({slug}) from {ForLog(origin)}: version {meta.DllVersion ?? "?"}, NR DLL {meta.NeedsNrDll}.");
            Reload();
            return new(Get(OptiScalerVariant.Custom(slug)), null, looksRight ? null : NotOptiScalerNote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException
                                       or InvalidOperationException or UntrustedDownloadException or JsonException)
        {
            AppLog.Warn("optiscaler", $"Custom OptiScaler build not added from {ForLog(source)}", ex);
            return new(null, CouldNotAddPrefix + ex.Message);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static readonly JsonSerializerOptions MetaJson = new() { WriteIndented = true };

    /// <summary>Test seam: runs between moving the old build aside and moving the new one in (a Replace that fails there).</summary>
    internal Action? BeforeSwapForTesting { get; set; }

    /// <summary>
    /// Nit (log redaction): a link as the log shows it — up to its path; the query and fragment (a signed link's token) are
    /// left out. Anything that is not a web link is shown as it is.
    /// </summary>
    internal static string ForLog(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length > 0 || uri.Fragment.Length > 0 ? "?…" : "")
            : source;

    /// <summary>Downloads a link through the custom-build policy; the server's file name wins (it carries the extension).</summary>
    private static async Task<(string? Error, string Path)> DownloadAsync(HttpClient http, Uri uri, string folder, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(folder);
        var policy = DownloadSources.OptiScalerCustomBuildFor(uri);
        var fileName = SafeFileName(Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))) ?? "download";
        string? webPage = null;
        string? serverName = null;
        TempDownload temp;
        try
        {
            temp = await TrustedDownload.DownloadToTempAsync(http, uri.AbsoluteUri, Path.Combine(folder, "download"), policy,
                cancellationToken: cancellationToken,
                onResponse: response =>
                {
                    if (response.Content.Headers.ContentType?.MediaType is { } type && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                        webPage = WebPageMessage;
                    serverName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
                }).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return (DownloadFailedPrefix + (ex.StatusCode is { } status ? $"{(int)status} {status}" : ex.Message), "");
        }
        using (temp)
        {
            if (webPage is not null) return (webPage, "");
            fileName = SafeFileName(serverName?.Trim('"')) ?? fileName;
            var target = Path.Combine(folder, fileName);
            temp.MoveTo(target);
            if (Path.GetExtension(target).Length == 0 || !IsKnownExtension(target))
            {
                var sniffed = SniffType(target);
                if (sniffed is not null)
                {
                    AtomicFile.Rename(target, target + sniffed); // RHI's own download folder
                    target += sniffed;
                }
            }
            return (null, target);
        }
    }

    private static bool IsKnownExtension(string path)
        => new[] { ".zip", ".7z", ".rar", ".dll" }.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = Path.GetFileName(name.Replace('\\', '/'));
        return name.Length is > 0 and <= 128 && name != "." && name != ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? name : null;
    }

    /// <summary>The archive or DLL type from the first bytes (a link without an extension; upstream <c>SniffArchive</c>).</summary>
    internal static string? SniffType(string path)
    {
        var head = new byte[6];
        using (var file = File.OpenRead(path))
            if (file.Read(head, 0, head.Length) < 2) return null;
        if (head[0] == 'P' && head[1] == 'K') return ".zip";
        if (head[0] == '7' && head[1] == 'z') return ".7z";
        if (head[0] == 'R' && head[1] == 'a' && head[2] == 'r') return ".rar";
        if (head[0] == 'M' && head[1] == 'Z') return ".dll";
        return null;
    }

    /// <summary>The shallowest OptiScaler.dll within <see cref="MaxSearchDepth"/> folders (links never followed), or null.</summary>
    public static string? FindOptiScalerDll(string root)
    {
        var level = new List<string> { root };
        for (var depth = 0; depth <= MaxSearchDepth && level.Count > 0; depth++)
        {
            foreach (var dir in level)
            {
                var candidate = System.IO.Directory.EnumerateFiles(dir)
                    .FirstOrDefault(file => Path.GetFileName(file).Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
                                            && new FileInfo(file).LinkTarget is null);
                if (candidate is not null) return candidate;
            }
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    next.AddRange(System.IO.Directory.GetDirectories(dir).Where(sub => new DirectoryInfo(sub).LinkTarget is null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn("optiscaler", $"Folder skipped while looking for OptiScaler.dll: {dir}", ex);
                }
            }
            level = next.OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return null;
    }

    private static bool FitsBuildLimits(string buildRoot)
    {
        long bytes = 0;
        var files = 0;
        foreach (var file in SafeFileEnumeration.Files(buildRoot, ignoreInaccessible: true))
        {
            if (++files > MaxBuildFiles) return false;
            bytes += new FileInfo(file).Length;
            if (bytes > MaxBuildBytes) return false;
        }
        return true;
    }

    /// <summary>Copies a build tree: links are skipped, OptiScaler.dll keeps that exact name, every target stays inside <paramref name="destination"/>.</summary>
    internal static void CopyTree(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in SafeFileEnumeration.Files(source))
        {
            if (new FileInfo(file).LinkTarget is not null) continue;
            var relative = Path.GetRelativePath(source, file);
            if (relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) relative = "OptiScaler.dll";
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!PathIdentity.IsSameOrInside(target, destination)) continue;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.Copy(file, target);
        }
    }

    private string MakeSlug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var baseSlug = sb.ToString().Trim('-');
        if (baseSlug.Length == 0) baseSlug = "build";
        var slug = baseSlug;
        for (var i = 2; System.IO.Directory.Exists(Path.Combine(_root, slug)) || System.IO.Directory.Exists(Path.Combine(_root, slug + ".tmp")); i++)
            slug = $"{baseSlug}-{i}";
        return slug;
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    public bool Rename(OptiScalerVariant build, string newName)
        => !string.IsNullOrWhiteSpace(newName) && Update(build, meta => meta.Name = newName.Trim());

    public bool SetNeedsNrDll(OptiScalerVariant build, bool on) => Update(build, meta => meta.NeedsNrDll = on);

    private bool Update(OptiScalerVariant build, Action<Meta> change)
    {
        if (Get(build) is not { } found) return false;
        try
        {
            var slugDir = Path.Combine(_root, found.Slug);
            var meta = ReadMeta(slugDir) ?? new Meta();
            change(meta);
            AtomicFile.WriteAllText(Path.Combine(slugDir, MetaFileName), JsonSerializer.Serialize(meta, MetaJson));
            Reload();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("optiscaler", $"Custom OptiScaler build not changed: {found.Slug}", ex);
            return false;
        }
    }

    /// <summary>Deletes a build's files from RHI's data folder. Games that have it installed keep their files.</summary>
    public bool Remove(OptiScalerVariant build)
    {
        if (Get(build) is not { } found) return false;
        try
        {
            System.IO.Directory.Delete(Path.Combine(_root, found.Slug), recursive: true); // RHI's own data folder
            AppLog.Current?.Write("optiscaler", $"Removed custom OptiScaler build {found.Slug}.");
            Reload();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("optiscaler", $"Custom OptiScaler build not removed: {found.Slug}", ex);
            return false;
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("optiscaler", $"Temporary folder not removed: {dir}", ex); }
    }
}

/// <summary>
/// The process's custom OptiScaler builds (one store under RHI's data folder), read wherever a version is labelled or
/// classified (<see cref="OptiScalerVersions"/>). Tests swap in their own store for one async flow.
/// </summary>
public static class OptiScalerCustomBuilds
{
    private static readonly Lazy<OptiScalerCustomBuildStore> Default = new(() => new OptiScalerCustomBuildStore());
    private static readonly AsyncLocal<OptiScalerCustomBuildStore?> FlowStore = new();

    public static OptiScalerCustomBuildStore Store => FlowStore.Value ?? Default.Value;

    public static IReadOnlyList<OptiScalerCustomBuild> All => Store.All;

    public static OptiScalerCustomBuild? Get(OptiScalerVariant variant) => variant.IsCustom ? Store.Get(variant) : null;

    /// <summary>Test seam: <paramref name="store"/> in this async flow (and the tasks it starts) until disposed.</summary>
    internal static IDisposable UseInThisFlow(OptiScalerCustomBuildStore store)
    {
        var previous = FlowStore.Value;
        FlowStore.Value = store;
        return new FlowRestore(previous);
    }

    private sealed class FlowRestore(OptiScalerCustomBuildStore? previous) : IDisposable
    {
        public void Dispose() => FlowStore.Value = previous;
    }
}
