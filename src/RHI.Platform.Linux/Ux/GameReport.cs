using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Ux;

/// <summary>
/// A game's support report as markdown (Refs #192, upstream <c>GameReportEncoder.FormatMarkdown</c>): game info, the
/// corrections the user made in Edit game against what was detected, RHI's components on the game and the user's note.
/// Written in English for the people who read reports (as upstream). The caller masks it
/// (<see cref="DiagnosticsBundle.Mask"/>). An Xbox / Game Pass card's launch command is left out: it names the private
/// installer app's own start script.
/// </summary>
public static class GameReport
{
    public static string Markdown(DetectedGame game, IReadOnlyList<LinuxDeploymentRecord> records, string rhiVersion, string? note, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(game);
        var sb = new StringBuilder();
        sb.AppendLine($"# RHI-Linux game report — {game.DisplayName()}");
        sb.AppendLine();
        sb.AppendLine($"- **Date:** {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **RHI-Linux version:** {rhiVersion}");
        sb.AppendLine($"- **Store:** {game.Source}");
        if (!string.Equals(game.DisplayName(), game.Name, StringComparison.Ordinal))
            sb.AppendLine($"- **Detected name:** {game.Name}");
        if (game.SteamAppId is int appId) sb.AppendLine($"- **Steam AppID:** {appId}");
        if (!string.IsNullOrWhiteSpace(game.HeroicAppName)) sb.AppendLine($"- **Heroic app:** {game.HeroicAppName}");
        sb.AppendLine();

        sb.AppendLine("## Game info");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        Row(sb, "Install path", Code(game.InstallPath));
        Row(sb, "Executable", Code(game.ExecutablePath));
        Row(sb, "Binaries folder", Code(game.BinaryDirectory));
        Row(sb, "Engine", game.EngineHint);
        Row(sb, "Bitness", Bitness(GameArchitecture.Of(game)));
        Row(sb, "Graphics API", $"{game.EffectiveGraphicsApi} ({game.GraphicsApiConfidence})");
        if (game.EffectiveGraphicsApis.Count > 1) Row(sb, "Detected APIs", string.Join(", ", game.EffectiveGraphicsApis));
        Row(sb, "Manifest name", game.CanonicalManifestName);
        Row(sb, "Wiki name", game.WikiNameOverride);
        Row(sb, "Runner", game.Runner);
        Row(sb, "Compatibility tool", game.CompatibilityTool);
        Row(sb, "Wine prefix", Code(game.PrefixPath));
        if (game.XboxPackage is null) Row(sb, "Launch command", Code(game.LaunchCommand));
        else Row(sb, "Launch", "Xbox / Game Pass: through the app that installed it");
        if (game.NativeHdrByManifest) Row(sb, "Native HDR", "Yes");
        if (game.UeExtendedByManifest) Row(sb, "UE-Extended", "Yes");
        sb.AppendLine();

        var corrections = new List<string>();
        if (game.UserArchitectureOverride is { } arch) corrections.Add($"Bitness: {Bitness(arch)} (set in Edit game)");
        if (game.UserGraphicsApiOverride is { } api && api != GraphicsApi.Unknown)
            corrections.Add($"Graphics API: {game.ManifestGraphicsApiOverride ?? game.GraphicsApi} → {api}");
        if (!string.IsNullOrWhiteSpace(game.UserDisplayName)) corrections.Add($"Name: {game.Name} → {game.UserDisplayName}");
        if (!string.IsNullOrWhiteSpace(game.UserWikiName)) corrections.Add($"Wiki name: {game.UserWikiName}");
        if (game.PatchTargetAuthority == "Manual") corrections.Add("Patch target: set in Edit game");
        if (game.UserLaunchCommand is not null && game.XboxPackage is null) corrections.Add("Launch command: set in Edit game");
        if (corrections.Count > 0)
        {
            sb.AppendLine("## Corrections (detected → set by the user)");
            sb.AppendLine();
            foreach (var line in corrections) sb.AppendLine($"- {line}");
            sb.AppendLine();
        }

        sb.AppendLine("## RHI components");
        sb.AppendLine();
        if (records.Count == 0) sb.AppendLine("None installed by RHI.");
        else
        {
            sb.AppendLine("| Component | Version | Files |");
            sb.AppendLine("|-----------|---------|-------|");
            foreach (var record in records.OrderBy(r => r.Component.ToString(), StringComparer.Ordinal))
                sb.AppendLine($"| {record.Component} | {Cell(record.Version)} | {Cell(string.Join(", ", record.Files.Select(f => Path.GetFileName(f.Path))))} |");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(note))
        {
            sb.AppendLine("## User note");
            sb.AppendLine();
            sb.AppendLine(note.Trim());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The library row upstream's game-data export writes for <paramref name="game"/>.</summary>
    public static GameDataRow DataRow(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        string? exeRelative = null;
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && !string.IsNullOrWhiteSpace(game.InstallPath)
            && PathIdentity.IsSameOrInside(game.ExecutablePath, game.InstallPath))
            exeRelative = Path.GetRelativePath(game.InstallPath, game.ExecutablePath);
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(game.InstallPath ?? ""));
        var arch = GameArchitecture.Of(game);
        return new GameDataRow(
            game.Name,
            game.Source,
            string.IsNullOrEmpty(folder) ? null : folder,
            exeRelative,
            ApiName(game.EffectiveGraphicsApi),
            game.EffectiveGraphicsApis.Select(ApiName).Where(name => name is not null).Select(name => name!).Distinct().Order(StringComparer.Ordinal).ToArray(),
            arch switch { PeArchitecture.X86 => 32, PeArchitecture.X64 => 64, _ => null },
            string.IsNullOrWhiteSpace(game.EngineHint) ? null : game.EngineHint,
            game.SteamAppId,
            game.EpicAppName,
            game.HeroicAppName,
            game.PcgwUrlOverride,
            game.EngineIniProjectOverride);
    }

    private static string? ApiName(GraphicsApi api) => api switch
    {
        GraphicsApi.Unknown => null,
        _ => api.ToString().Replace("DirectX", "DX", StringComparison.Ordinal)
    };

    private static string Bitness(PeArchitecture arch) => arch switch
    {
        PeArchitecture.X86 => "32-bit",
        PeArchitecture.X64 => "64-bit",
        _ => "unknown"
    };

    private static void Row(StringBuilder sb, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"| {field} | {value} |");
    }

    private static string? Code(string? value) => string.IsNullOrWhiteSpace(value) ? null : $"`{value.Replace("`", "'", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)}`";

    private static string Cell(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Replace("|", "\\|", StringComparison.Ordinal);
}
