using System.ComponentModel;
using System.Diagnostics;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Installs Microsoft's native D3DCompiler_47 into an already initialized Wine/Proton prefix
/// using the user's package-management helper. No shell is involved: arguments are passed through
/// ProcessStartInfo.ArgumentList, so game names/prefix paths cannot become shell syntax.
/// </summary>
public sealed class D3DCompilerToolInstaller
{
    private const string Verb = "d3dcompiler_47";
    private readonly WineDllOverrideService _overrides;

    public D3DCompilerToolInstaller(WineDllOverrideService overrides)
    {
        _overrides = overrides;
    }

    public Task<ComponentOperationResult> InstallWithProtontricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!game.SteamAppId.HasValue || string.IsNullOrWhiteSpace(game.PrefixPath))
            return Task.FromResult(ComponentOperationResult.Fail("Protontricks is available only for a detected Steam/Proton game with an initialized prefix."));

        var startInfo = BuildProtontricksStartInfo(game.SteamAppId.Value);
        return RunAndVerifyAsync(game, startInfo, "protontricks", applyOverride: true, cancellationToken: cancellationToken);
    }

    public Task<ComponentOperationResult> InstallWithWinetricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!CanInstallWithWinetricks(game))
        {
            if (IsOrphanedProtonPrefix(game))
                return Task.FromResult(ComponentOperationResult.Fail(OrphanedProtonMessage(game)));
            if (game.SteamAppId.HasValue)
                return Task.FromResult(ComponentOperationResult.Fail("This is a Steam/Proton game. Use the Protontricks button instead of Winetricks."));
            if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(ComponentOperationResult.Fail("Winetricks applies only to Windows games running through Wine/Proton."));
            return Task.FromResult(ComponentOperationResult.Fail("Wine prefix is not initialized/detected. Start the game once before running Winetricks."));
        }

        var prefixPath = game.PrefixPath;
        if (string.IsNullOrWhiteSpace(prefixPath))
            return Task.FromResult(ComponentOperationResult.Fail("Wine prefix is not initialized/detected. Start the game once before running Winetricks."));

        var launcherManaged = IsLauncherManagedPrefix(game);
        if (launcherManaged && (string.IsNullOrWhiteSpace(game.WineBinaryPath) || !File.Exists(game.WineBinaryPath)))
            return Task.FromResult(ComponentOperationResult.Fail(
                $"RHI detected a {game.Source} prefix, but could not resolve the Wine binary for the active {game.CompatibilityTool ?? game.Runner ?? "runner"}. Refresh the library after selecting/launching the desired runner in the launcher."));

        var startInfo = BuildWinetricksStartInfo(prefixPath, game.WineBinaryPath, game.WineServerPath);
        var toolLabel = launcherManaged
            ? $"winetricks via {game.CompatibilityTool ?? game.Runner ?? game.Source}"
            : "winetricks";
        return RunAndVerifyAsync(game, startInfo, toolLabel, applyOverride: true, cancellationToken: cancellationToken);
    }

    internal Task<ComponentOperationResult> InstallDependencyWithProtontricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!game.SteamAppId.HasValue || string.IsNullOrWhiteSpace(game.PrefixPath))
            return Task.FromResult(ComponentOperationResult.Fail("Protontricks is available only for a detected Steam/Proton game with an initialized prefix."));

        return RunAndVerifyAsync(
            game,
            BuildProtontricksStartInfo(game.SteamAppId.Value),
            "protontricks",
            applyOverride: false,
            cancellationToken: cancellationToken);
    }

    internal Task<ComponentOperationResult> InstallDependencyWithWinetricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!CanInstallWithWinetricks(game))
            return Task.FromResult(ComponentOperationResult.Fail(IsOrphanedProtonPrefix(game)
                ? OrphanedProtonMessage(game)
                : "No safe Winetricks route is available for this prefix."));

        var launcherManaged = IsLauncherManagedPrefix(game);
        if (launcherManaged && (string.IsNullOrWhiteSpace(game.WineBinaryPath) || !File.Exists(game.WineBinaryPath)))
            return Task.FromResult(ComponentOperationResult.Fail(
                $"RHI detected a {game.Source} prefix, but could not resolve the Wine binary for the active {game.CompatibilityTool ?? game.Runner ?? "runner"}."));

        var startInfo = BuildWinetricksStartInfo(game.PrefixPath!, game.WineBinaryPath, game.WineServerPath);
        var toolLabel = launcherManaged
            ? $"winetricks via {game.CompatibilityTool ?? game.Runner ?? game.Source}"
            : "winetricks";
        return RunAndVerifyAsync(game, startInfo, toolLabel, applyOverride: false, cancellationToken: cancellationToken);
    }

    internal async Task<ComponentOperationResult> RunAndVerifyAsync(
        DetectedGame game,
        ProcessStartInfo startInfo,
        string toolName,
        bool applyOverride,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected, so RHI cannot validate the D3DCompiler architecture.");
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            return ComponentOperationResult.Fail("Wine/Proton prefix is not initialized/detected.");

        var architecture = PeArchitectureDetector.Detect(game.ExecutablePath);
        if (architecture is not (PeArchitecture.X64 or PeArchitecture.X86))
            return ComponentOperationResult.Fail("Game PE architecture is unknown/unsupported, so the D3DCompiler install was not started.");

        try
        {
            // The tool writes into the prefix as soon as it starts, and a cancelled wait would leave it running behind an
            // idle RHI: from here on the install runs to the end (Cancel is refused).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            using var process = Process.Start(startInfo);
            if (process is null)
                return ComponentOperationResult.Fail($"Could not start {toolName}.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = LastMeaningfulLine(stderr) ?? LastMeaningfulLine(stdout) ?? $"exit code {process.ExitCode}";
                return ComponentOperationResult.Fail($"{toolName} could not install {Verb}: {detail}");
            }

            var compiler = ReShadeDeploymentService.FindNativeD3DCompilerInPrefix(game.PrefixPath, architecture);
            if (compiler is null)
                return ComponentOperationResult.Fail($"{toolName} completed, but RHI could not verify a native Microsoft D3DCompiler_47.dll in this prefix.");

            var appName = Path.GetFileName(game.ExecutablePath);
            if (applyOverride)
            {
                _overrides.EnsureNativeBuiltin(game.PrefixPath, Verb, appName);
                return ComponentOperationResult.Ok(
                    $"Native Microsoft D3DCompiler_47 installed/verified with {toolName}; per-game d3dcompiler_47=native,builtin is active for {appName}.",
                    compiler);
            }

            return ComponentOperationResult.Ok(
                $"Native Microsoft D3DCompiler_47 installed/verified with {toolName}. Registry ownership is deferred to the requesting managed component.",
                compiler);
        }
        catch (OperationCanceledException)
        {
            return ComponentOperationResult.Fail($"{toolName} D3DCompiler installation was cancelled.");
        }
        catch (Win32Exception ex)
        {
            return ComponentOperationResult.Fail($"{toolName} was not found or could not be started: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"{toolName} D3DCompiler installation failed: {ex.Message}");
        }
    }

    internal static ProcessStartInfo BuildProtontricksStartInfo(int appId)
    {
        var startInfo = BaseStartInfo("protontricks");
        startInfo.ArgumentList.Add(appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(Verb);
        return startInfo;
    }

    internal static ProcessStartInfo BuildWinetricksStartInfo(
        string prefixPath,
        string? wineBinaryPath = null,
        string? wineServerPath = null)
    {
        var startInfo = BaseStartInfo("winetricks");
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add(Verb);
        startInfo.Environment["WINEPREFIX"] = Path.GetFullPath(prefixPath);

        if (!string.IsNullOrWhiteSpace(wineBinaryPath))
        {
            var wine = Path.GetFullPath(wineBinaryPath);
            startInfo.Environment["WINE"] = wine;
            var wineDir = Path.GetDirectoryName(wine);
            if (!string.IsNullOrWhiteSpace(wineDir))
            {
                var currentPath = startInfo.Environment.TryGetValue("PATH", out var configuredPath)
                    ? configuredPath
                    : Environment.GetEnvironmentVariable("PATH");
                startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                    ? wineDir
                    : wineDir + Path.PathSeparator + currentPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(wineServerPath))
            startInfo.Environment["WINESERVER"] = Path.GetFullPath(wineServerPath);

        return startInfo;
    }

    /// <summary>
    /// Single eligibility authority shared by the Desktop UI and the backend. Manual games are first-class:
    /// any non-Steam Windows target with an initialized prefix can use system Winetricks even when its
    /// presentation runner is "Manual Wine" and no launcher-specific Wine binary was discovered.
    /// </summary>
    public static bool CanInstallWithWinetricks(DetectedGame game)
        => !game.SteamAppId.HasValue
           && !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(game.PrefixPath)
           && Directory.Exists(game.PrefixPath)
           && !IsOrphanedProtonPrefix(game);

    /// <summary>
    /// A Proton-built prefix (outside Steam) whose Proton build is no longer installed. Only that
    /// Proton's own Wine may run in it: system Wine runs wineboot over the prefix and can break it, so
    /// Winetricks and direct launches are refused until the build is back.
    /// </summary>
    public static bool IsOrphanedProtonPrefix(DetectedGame game)
        => !game.SteamAppId.HasValue
           && string.Equals(game.CompatibilityLayerType, "proton", StringComparison.OrdinalIgnoreCase)
           && (string.IsNullOrWhiteSpace(game.WineBinaryPath) || !File.Exists(game.WineBinaryPath));

    internal const string OrphanedProtonMessagePrefix =
        "Winetricks is unavailable: this prefix was built by a Proton build that is no longer installed, and system Wine could damage it. Reinstall that Proton (or choose an installed one in the launcher) and refresh the library. Missing Proton: ";

    private static string OrphanedProtonMessage(DetectedGame game)
        => OrphanedProtonMessagePrefix + (game.CompatibilityTool ?? game.Runner ?? "?");

    public static bool IsLauncherManagedPrefix(DetectedGame game)
        => game.Source.StartsWith("Heroic", StringComparison.OrdinalIgnoreCase)
           || string.Equals(game.Source, "Lutris", StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo BaseStartInfo(string executable)
        => new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

    private static string? LastMeaningfulLine(string text)
        => text.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
}
