using System.Reflection;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>The GPU an OptiScaler INI template is tuned for (upstream 2.8.7 Settings "GPU Type").</summary>
public enum OptiScalerGpuType
{
    Nvidia,
    Amd,
    Intel
}

/// <summary>
/// The template a new OptiScaler install starts from: GPU type and, for AMD/Intel, whether the game feeds DLSS inputs; and
/// the global overlay key (Refs #187: <c>[Menu] ShortcutKey</c>, a virtual-key code) the template carries, or null to keep
/// the template's own. The key is not part of the file name.
/// </summary>
public sealed record OptiScalerTemplateChoice(OptiScalerGpuType Gpu, bool DlssInputs, string? MenuKey = null);

/// <summary>
/// Per-GPU OptiScaler INI templates (#97; upstream 2.8.7 <c>OptiScalerService.GetUserIniPath</c>/<c>SeedUserInis</c>): one
/// user-editable file per channel and GPU setup in RHI's INIs folder (<c>~/.config/rhi-linux/inis</c>), named as upstream
/// (<c>OptiScaler[_nightly|_dlssnr|_f5].{nvidia|amd-dlss|amd-nodlss}.ini</c>). A file is seeded from the copy RHI ships the
/// first time it is needed and never overwritten afterwards, so the user's edits are what every later install deploys
/// as the game's <c>OptiScaler.ini</c>. Intel uses the AMD files, as upstream.
/// </summary>
public sealed class OptiScalerIniTemplates
{
    public const string NotePrefix = " OptiScaler.ini comes from your INI template: ";
    public const string UnavailablePrefix = " The OptiScaler INI template could not be prepared, so the package's OptiScaler.ini was used: ";

    private readonly Func<OptiScalerTemplateChoice> _choice;

    public OptiScalerIniTemplates(string directory, Func<OptiScalerTemplateChoice> choice)
    {
        Directory = directory;
        _choice = choice;
    }

    /// <summary>The INIs folder (created on first seeding).</summary>
    public string Directory { get; }

    public OptiScalerTemplateChoice CurrentChoice => _choice();

    /// <summary>The settings' GPU type and DLSS inputs (unknown values read as upstream's defaults: NVIDIA, DLSS inputs on).</summary>
    public static OptiScalerTemplateChoice ChoiceFrom(LinuxAppSettings settings)
        => new(ParseGpuType(settings.OptiScalerGpuType), settings.OptiScalerDlssInputs,
            string.IsNullOrWhiteSpace(settings.OptiScalerMenuKey) ? null : settings.OptiScalerMenuKey);

    public static OptiScalerGpuType ParseGpuType(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "AMD" => OptiScalerGpuType.Amd,
        "INTEL" => OptiScalerGpuType.Intel,
        _ => OptiScalerGpuType.Nvidia
    };

    /// <summary>Upstream's user file name for a channel and GPU setup.</summary>
    public static string FileName(OptiScalerVariant variant, OptiScalerTemplateChoice choice)
        => $"OptiScaler{Suffix(variant)}.{GpuPart(choice)}.ini";

    /// <summary>
    /// The user template for <paramref name="variant"/> under the current choice, seeded from RHI's copy when it does
    /// not exist yet (never overwritten otherwise). With a global overlay key chosen (#187) the template carries it: its
    /// <c>[Menu] ShortcutKey</c> alone is set to that key (upstream writes the key into the template and every install).
    /// Throws when the folder or file cannot be written.
    /// </summary>
    public string EnsureSeeded(OptiScalerVariant variant) => EnsureSeeded(variant, CurrentChoice);

    public string EnsureSeeded(OptiScalerVariant variant, OptiScalerTemplateChoice choice)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, FileName(variant, choice));
        if (!File.Exists(path))
        {
            var bytes = Shipped(variant, choice);
            try { AtomicFile.WriteNew(path, bytes); }
            catch (IOException) when (File.Exists(path)) { } // seeded meanwhile by another instance: theirs stays
        }
        if (choice.MenuKey is { } key) SetMenuKey(path, key);
        return path;
    }

    /// <summary>
    /// Sets the global overlay key (#187, upstream's hotkey row) in every template that exists in the INIs folder; returns
    /// how many files hold it now and the ones that could not be written (each named by its file name; the loop goes on
    /// past a failure). Only <c>[Menu] ShortcutKey</c> changes; a template the user locked read-only is written and stays
    /// locked (the user-locked INI rule). Throws only when the folder itself cannot be listed.
    /// </summary>
    public OptiScalerTemplateKeyResult ApplyMenuKey(string key)
    {
        if (!System.IO.Directory.Exists(Directory)) return new(0, []);
        var count = 0;
        var failures = new List<(string File, string Reason)>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "OptiScaler*.ini", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            try
            {
                SetMenuKey(path, key);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add((Path.GetFileName(path), ex.Message));
            }
        }
        return new(count, failures);
    }

    private static void SetMenuKey(string path, string key)
    {
        if (string.Equals(OptiScalerIniEditor.GetValueInFile(path, "Menu", "ShortcutKey"), key, StringComparison.OrdinalIgnoreCase)) return;
        OptiScalerIniEditor.SetValuesInFile(path, [("Menu", "ShortcutKey", key)], ReadOnlyUserFile.WriteKeepingLock);
    }

    /// <summary>Seeds every template that is missing (upstream does this at start-up; RHI when the folder is opened).</summary>
    public IReadOnlyList<string> SeedAll()
    {
        var seeded = new List<string>();
        var key = CurrentChoice.MenuKey;
        foreach (var variant in Enum.GetValues<OptiScalerVariant>())
            foreach (var choice in new[] { new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true, key), new(OptiScalerGpuType.Amd, true, key), new(OptiScalerGpuType.Amd, false, key) })
                seeded.Add(EnsureSeeded(variant, choice));
        return seeded.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The template RHI ships (upstream 2.8.7's file for that channel and GPU setup).</summary>
    internal static byte[] Shipped(OptiScalerVariant variant, OptiScalerTemplateChoice choice)
    {
        // Upstream's NVIDIA and AMD-with-DLSS files are the same bytes, and Nightly's three are one file.
        var shared = variant == OptiScalerVariant.Nightly ? "" : UsesDlssInputs(choice) ? ".dlss" : ".nodlss";
        var name = $"OptiScalerTemplates.OptiScaler{Suffix(variant)}{shared}.ini";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("OptiScaler INI template missing from the build: " + name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool UsesDlssInputs(OptiScalerTemplateChoice choice) => choice.Gpu == OptiScalerGpuType.Nvidia || choice.DlssInputs;

    private static string GpuPart(OptiScalerTemplateChoice choice)
        => choice.Gpu == OptiScalerGpuType.Nvidia ? "nvidia" : choice.DlssInputs ? "amd-dlss" : "amd-nodlss";

    private static string Suffix(OptiScalerVariant variant) => variant switch
    {
        OptiScalerVariant.Nightly => "_nightly",
        OptiScalerVariant.DlssNr => "_dlssnr",
        OptiScalerVariant.F5 => "_f5",
        _ => ""
    };
}

/// <summary>#187: the INI templates the global overlay key was written to, and the ones that kept their file (name, reason).</summary>
public sealed record OptiScalerTemplateKeyResult(int Updated, IReadOnlyList<(string File, string Reason)> Failures);
