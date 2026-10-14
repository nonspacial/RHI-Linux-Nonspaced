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

/// <summary>The template a new OptiScaler install starts from: GPU type and, for AMD/Intel, whether the game feeds DLSS inputs.</summary>
public sealed record OptiScalerTemplateChoice(OptiScalerGpuType Gpu, bool DlssInputs);

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
        => new(ParseGpuType(settings.OptiScalerGpuType), settings.OptiScalerDlssInputs);

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
    /// not exist yet (never overwritten). Throws when the folder or file cannot be written.
    /// </summary>
    public string EnsureSeeded(OptiScalerVariant variant) => EnsureSeeded(variant, CurrentChoice);

    public string EnsureSeeded(OptiScalerVariant variant, OptiScalerTemplateChoice choice)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, FileName(variant, choice));
        if (File.Exists(path)) return path;
        var bytes = Shipped(variant, choice);
        try { AtomicFile.WriteNew(path, bytes); }
        catch (IOException) when (File.Exists(path)) { } // seeded meanwhile by another instance: theirs stays
        return path;
    }

    /// <summary>Seeds every template that is missing (upstream does this at start-up; RHI when the folder is opened).</summary>
    public IReadOnlyList<string> SeedAll()
    {
        var seeded = new List<string>();
        foreach (var variant in Enum.GetValues<OptiScalerVariant>())
            foreach (var choice in new[] { new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true), new(OptiScalerGpuType.Amd, true), new(OptiScalerGpuType.Amd, false) })
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
