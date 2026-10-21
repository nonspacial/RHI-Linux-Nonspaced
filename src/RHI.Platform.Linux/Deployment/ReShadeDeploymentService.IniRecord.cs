using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

// w8/reshade-ini (#206): RHI's later settings writes into a game's ReShade.ini, recorded on ReShade's record the way
// "Deploy ReShade.ini" records its write (#203, RecordIniWrite). Kept apart from ReShadeDeploymentService.cs (lane
// commit-point's file); RecordIniWrite itself is unchanged.
public sealed partial class ReShadeDeploymentService
{
    /// <summary>
    /// #206: records a settings write into the game's ReShade.ini ("Apply to all games" / the per-game Apply, a preset's
    /// <c>PresetPath</c>) on <paramref name="record"/> with <see cref="RecordIniWrite"/>'s rule: the file's new hash becomes
    /// RHI's last write only when the file held RHI's last write before (<paramref name="hashBefore"/>; null = there was no
    /// file, so RHI created it), so a file the user changed meanwhile stays the user's; a file this write created is RHI's.
    /// Settings writes add no search paths. <paramref name="iniPath"/> is the game's ReShade.ini as the install records it
    /// (a link stays named by the link), <paramref name="writtenTarget"/> the file the write replaced (the link's target).
    /// False when the record does not manage ReShade.ini (installed with "Manage ReShade.ini" off).
    /// </summary>
    internal static bool RecordSettingsWrite(LinuxDeploymentRecord record, string iniPath, string writtenTarget, string? hashBefore)
        => RecordIniWrite(record,
            new ReShadeIniApplyResult(iniPath, ExistedBefore: hashBefore is not null, CreatedByRhi: hashBefore is null,
                EffectKeyCreated: false, TextureKeyCreated: false, AddedEffectPaths: [], AddedTexturePaths: [],
                Sha256: FileHash.Sha256Hex(writtenTarget)),
            hashBefore);
}
