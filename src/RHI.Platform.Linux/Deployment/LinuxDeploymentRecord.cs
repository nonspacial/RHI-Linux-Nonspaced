using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

public sealed class LinuxDeploymentRecord
{
    public string GameKey { get; set; } = "";
    public string GameName { get; set; } = "";
    public ManagedComponentKind Component { get; set; }
    public string BinaryDirectory { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string? Version { get; set; }
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
    public List<LinuxDeployedFile> Files { get; set; } = [];
    public List<string> DirectoriesCreatedByRhi { get; set; } = [];
    public WineOverrideRecord? WineOverride { get; set; }
    /// <summary>Additional per-component Wine overrides (for example ReShade shader compiler compatibility). Old records deserialize as an empty list.</summary>
    public List<WineOverrideRecord> AdditionalWineOverrides { get; set; } = [];
    /// <summary>Small component-specific state used for safe updates/rollback. Missing keys are treated conservatively for old records.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What is installed, for "did this change?" questions (Update All's updated count, update history): the
    /// version plus each RHI-owned payload file and its hash. Install time, operation ids in metadata and files the
    /// user may edit (configs) are left out, so re-deploying the same payload compares equal (#113). Hashes compare
    /// case-insensitively: older records hold lower-case hex, the w2/trust toolkit writes upper case.
    /// </summary>
    public static string PayloadFingerprint(LinuxDeploymentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var files = record.Files
            .Where(file => !file.AllowUserChanges)
            .Select(file => $"{file.Path}\t{file.Sha256?.Trim().ToUpperInvariant()}")
            .Order(StringComparer.Ordinal);
        return string.Join("\n", files.Prepend(record.Version ?? string.Empty));
    }
}

public sealed class LinuxDeployedFile
{
    public string Path { get; set; } = "";
    public bool ExistedBeforeInstall { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>True for configuration files that the user or another RHI component may legitimately edit after installation.</summary>
    public bool AllowUserChanges { get; set; }
}

public sealed class WineOverrideRecord
{
    public string DllName { get; set; } = "";
    public string? PreviousValue { get; set; }
    public bool ChangedByRhi { get; set; }
    /// <summary>True only when RHI had to create the target DllOverrides registry section.</summary>
    public bool SectionCreatedByRhi { get; set; }
    public string? ApplicationName { get; set; }
}
