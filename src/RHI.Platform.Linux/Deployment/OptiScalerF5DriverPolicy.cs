namespace RHI.Platform.Linux.Deployment;

/// <summary>What RHI tells the user before (or about) OptiScaler F5 DLSSNR on this host (#231). Never a refusal.</summary>
/// <param name="DriverVersion">The loaded NVIDIA kernel module's version; null when not loaded or unreadable.</param>
/// <param name="DriverBelowMinimum">The driver is older than <see cref="OptiScalerF5DriverPolicy.MinimumLinuxDriver"/>.</param>
/// <param name="DriverUnknown">No NVIDIA driver is loaded, or its version cannot be read.</param>
/// <param name="NrDll">The NR DLL's version F5 loads (null when unknown).</param>
/// <param name="NrDllFaults">That DLL is a 310.8.0.x build on an R615-or-newer driver (it faults there; 310.8.2 or newer is needed).</param>
public sealed record F5DriverAdvice(string? DriverVersion, bool DriverBelowMinimum, bool DriverUnknown, string? NrDll, bool NrDllFaults)
{
    /// <summary>The driver warning (below the minimum, unreadable, or no NVIDIA driver).</summary>
    public bool DriverWarning => DriverBelowMinimum || DriverUnknown;

    /// <summary>Anything to say: the dialog asks only then.</summary>
    public bool AnyWarning => DriverWarning || NrDllFaults;
}

/// <summary>
/// #231 (owner decision 2026-10-22: warn and ask to confirm, never refuse). wizardkof v0.9.25.0-beta.1 gates F5 on
/// NVIDIA driver ≥ 616.56 read through <c>nvidia-smi</c> and refuses below it. 616.56 is the first WINDOWS driver of
/// NVIDIA's R615 branch; Linux and Windows share the branch, not the minor numbers, so on Linux that literal number would
/// refuse every driver released so far (the owner's 615.78.08 included). The Linux equivalent is <b>615.71.09</b>, the
/// first Linux R615 driver — its NGX core knows DLSSNR, 610.57.04's does not (coordinator decision 2026-10-22, research in
/// <c>reviews/nvidia-driver-numbering.md</c>). The version comes from <c>/proc/driver/nvidia/version</c> (NvidiaGpuProbe),
/// never <c>nvidia-smi</c>. Second warning: the 310.8.0.x NR model faults on R615-or-newer drivers (they route NGX feature
/// 18 straight into the model); 310.8.2.0 or newer is needed.
/// </summary>
public static class OptiScalerF5DriverPolicy
{
    /// <summary>The first Linux R615 driver = the Linux equivalent of F5's Windows minimum 616.56.</summary>
    public const string MinimumLinuxDriver = "615.71.09";

    /// <summary>F5's published minimum, a Windows driver number (named beside the Linux one, never compared with it).</summary>
    public const string WindowsMinimum = "616.56";

    /// <summary>The first NVIDIA branch whose NGX core routes NR straight into the model (310.8.0.x faults there).</summary>
    public const int R615 = 615;

    /// <summary>Major, minor and patch of a Linux driver version (<c>615.78.08</c>); null when it cannot be read.</summary>
    public static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Trim().Split('.');
        if (parts.Length is < 1 or > 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        return numbers;
    }

    /// <summary>At or above <see cref="MinimumLinuxDriver"/>, compared number by number (615.78.08 passes; 610.57.04 does not).</summary>
    public static bool MeetsMinimum(string? driver)
    {
        if (Parse(driver) is not { } found) return false;
        var minimum = Parse(MinimumLinuxDriver)!;
        for (var i = 0; i < 3; i++)
            if (found[i] != minimum[i]) return found[i] > minimum[i];
        return true;
    }

    /// <summary>An R615-or-newer driver (where a 310.8.0.x NR DLL faults).</summary>
    public static bool IsR615OrNewer(string? driver) => Parse(driver) is [>= R615, ..];

    /// <summary>A 310.8.0.x NR DLL: the manifest label (<c>310.8.0 (50xx)</c>) or a PE version (<c>310,8,0,0</c>).</summary>
    public static bool IsFaultingNrDll(string? nrVersion)
    {
        if (string.IsNullOrWhiteSpace(nrVersion)) return false;
        var token = nrVersion.Trim().Split([' ', '('], StringSplitOptions.RemoveEmptyEntries)[0].Replace(',', '.');
        var parts = token.Split('.', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 && parts[0] == "310" && parts[1] == "8" && int.TryParse(parts[2], out var patch) && patch == 0;
    }

    /// <summary>The advice for F5 on a host with <paramref name="driver"/>, loading the NR DLL <paramref name="nrDll"/>.</summary>
    public static F5DriverAdvice Advise(string? driver, string? nrDll)
    {
        var known = Parse(driver) is not null;
        return new F5DriverAdvice(known ? driver : null, known && !MeetsMinimum(driver), !known, nrDll,
            IsFaultingNrDll(nrDll) && IsR615OrNewer(driver));
    }

    /// <summary>#231: the NR owners' row note for a 310.8.0.x DLL on an R615+ driver; the version follows.</summary>
    public const string FaultingNrDllNotePrefix =
        "This Neural Rendering DLL crashes on NVIDIA drivers 615 and newer; 310.8.2 or newer is needed. Installed: ";
}
