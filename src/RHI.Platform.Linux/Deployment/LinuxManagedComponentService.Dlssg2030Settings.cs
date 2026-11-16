using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>w14/extras-fg (#281): 20/30 FG Unlock's ⚙ reads and writes the installed <c>dlssg_sm86.ini</c>.</summary>
public sealed partial class LinuxManagedComponentService
{
    public IReadOnlyList<string>? Dlssg2030IniLines(DetectedGame game) => _dlssg2030.InstalledIniLines(game);

    /// <summary>Writes one setting (the refusal, or null). Refused for a game without a confirmed patch target.</summary>
    public string? SetDlssg2030IniSetting(DetectedGame game, Dlssg2030Setting setting, int index)
        => PatchTargetGuard(game)?.Message ?? _dlssg2030.SetIniSetting(game, setting, index);

    /// <summary>#280: rtx-encore.jsonc beside RHI's RTX Encore, or null.</summary>
    public string? RtxEncoreConfigPath(DetectedGame game) => _rtxEncore?.ConfigPath(game);

    /// <summary>#280: RTX Encore's Intel XeSS files, or null.</summary>
    public RtxEncoreXessStatus? RtxEncoreXessStatus(DetectedGame game) => _rtxEncore?.XessStatus(game);
}
