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

    /// <summary>The tool installed the DLL, but Wine still ran in the prefix afterwards; the prefix path follows.</summary>
    public const string OverrideNotSetWineRunningPrefix =
        "Native D3DCompiler_47 was installed, but Wine is still running in this prefix, so RHI did not set the d3dcompiler_47 override. Close everything using this prefix and run the install again. Prefix: ";

    /// <summary>wineserver stays a few seconds after the tool's last Wine process; this is how long RHI waits for it.</summary>
    internal TimeSpan WineServerExitWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// winetricks/protontricks download d3dcompiler_47 and run Wine. Once started they run to the end (Cancel is
    /// refused: the prefix is never left mid-install by a user), so a stuck run (a dialog nobody sees, a hung
    /// wineserver, a stalled download) is what this limit is for: after it the tool's whole process tree is killed and
    /// the install fails with <see cref="ToolTimedOutPrefix"/>. Generous on purpose (a slow mirror still finishes).
    /// </summary>
    internal TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Starts the helper tool. Tests replace it to prove a refused route never starts one.</summary>
    internal Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ExternalProcessResult>> RunTool { get; init; }
        = (startInfo, timeout, ct) => ExternalProcess.RunAsync(startInfo, timeout, ct);

    /// <summary>
    /// The helper ran past <see cref="ToolTimeout"/> and was stopped; the tool name and limit follow. It says the helper
    /// was stopped, not every process: the tree kill reaches only the descendants still under it, and a daemonised
    /// wineserver (reparented) ends on its own idle timeout (w2 final review, nit 2).
    /// </summary>
    public const string ToolTimedOutPrefix =
        "The D3DCompiler helper took too long and was stopped; the prefix may hold a partial d3dcompiler_47 install, so run the install again. Tool: ";

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
            if (IsRuntimeUnknownPrefix(game))
                return Task.FromResult(ComponentOperationResult.Fail(RuntimeUnknownMessagePrefix + game.PrefixPath));
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
                : IsRuntimeUnknownPrefix(game)
                    ? RuntimeUnknownMessagePrefix + game.PrefixPath
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

    /// <remarks>Internal for tests, which run a stand-in tool.</remarks>
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
        // Running a prefix tool while the game uses the prefix is unsafe as well: refuse before starting it.
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        try
        {
            // The tool writes into the prefix as soon as it starts, and a cancelled wait would leave it running behind an
            // idle RHI: from here on the install runs to the end (Cancel is refused). The shared runner (#37) still
            // applies its time limit, process-tree kill on timeout and closed stdin.
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            var run = await RunTool(startInfo, ToolTimeout, cancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return ComponentOperationResult.Fail($"{toolName} could not install {Verb}: {run.LastMeaningfulLine()}");

            var compiler = ReShadeDeploymentService.FindNativeD3DCompilerInPrefix(game.PrefixPath, architecture);
            if (compiler is null)
                return ComponentOperationResult.Fail($"{toolName} completed, but RHI could not verify a native Microsoft D3DCompiler_47.dll in this prefix.");

            var appName = Path.GetFileName(game.ExecutablePath);
            if (applyOverride)
            {
                // The tool's own wineserver rewrites user.reg when it exits: wait for it before editing.
                if (!await _overrides.Registry.WaitUntilIdleAsync(game.PrefixPath, WineServerExitWait, cancellationToken).ConfigureAwait(false))
                    return ComponentOperationResult.Fail(OverrideNotSetWineRunningPrefix + game.PrefixPath);
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
        catch (TimeoutException ex)
        {
            AppLog.Warn("d3dcompiler", $"{toolName} ran past its {(int)ToolTimeout.TotalSeconds} s limit and was stopped (its remaining process tree was killed)", ex);
            return ComponentOperationResult.Fail($"{ToolTimedOutPrefix}{toolName} ({(int)ToolTimeout.TotalSeconds} s)");
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
           && !IsOrphanedProtonPrefix(game)
           && !IsRuntimeUnknownPrefix(game);

    /// <summary>
    /// <see cref="DetectedGame.CompatibilityLayerType"/> of a prefix whose runtime markers (config_info, version) could
    /// not be read: RHI cannot tell a Proton-built prefix from a Wine one, so no Wine of any kind may run in it.
    /// </summary>
    public const string UnknownRuntimeLayer = "unknown";

    /// <summary>
    /// A prefix whose runtime RHI could not read (<see cref="UnknownRuntimeLayer"/>). It may be Proton-built, and host
    /// Wine (winetricks without a WINE binary) would run wineboot over it: Winetricks, the automatic D3DCompiler step
    /// and direct launches are refused, like an orphaned Proton prefix (w2-launch re-review, major 1).
    /// </summary>
    public static bool IsRuntimeUnknownPrefix(DetectedGame game)
        => !game.SteamAppId.HasValue
           && string.Equals(game.CompatibilityLayerType, UnknownRuntimeLayer, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refusal for a runtime-unknown prefix; the prefix path follows.</summary>
    public const string RuntimeUnknownMessagePrefix =
        "Winetricks is unavailable: RHI could not read this prefix's Wine/Proton markers (config_info), so it cannot tell which Wine may run in it, and system Wine could damage a Proton prefix. Make the prefix's config_info readable again and refresh the library. Prefix: ";

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

}
