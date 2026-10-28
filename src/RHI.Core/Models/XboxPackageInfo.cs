namespace RHI.Core.Models;

/// <summary>
/// An Xbox / Game Pass (MSIXVC or Appx) install found by the Xbox detector (Refs #90). Such a game is started only by the
/// app that owns its download and licence (the installer app, or Xodus): Play hands it to that app, never the game exe.
/// </summary>
/// <param name="StoreId">Microsoft Store product id (the installer app's payload folder name), or null when the package names none.</param>
/// <param name="OwningApp">The app that controls this install: <c>Xbox app</c> or <c>Xodus</c>.</param>
/// <param name="OpenCommand">
/// The card's own launch command (what Edit game shows as the detected launch): the installer app's Play entry point rendered as a
/// shell line when <paramref name="PlayArgv"/> is set, else the command that opens <paramref name="OwningApp"/>; null when
/// RHI found no way to open it.
/// </param>
/// <param name="JobsFile">the installer app's job list for the instance owning the title (<c>jobs.json</c>); null for Xodus-only installs and unknown instances.</param>
/// <param name="PlayArgv">
/// The installer app's Play entry point for this title (its breadcrumb's <c>launch.argv</c> + <c>entryPoints.play</c>, store id
/// filled in): executed as an argument vector, no shell. Null without a breadcrumb-backed instance.
/// </param>
/// <param name="ShowArgv">The same for <c>entryPoints.show</c> ("Show in the app that installed it"); null without one.</param>
/// <param name="LaunchEnvironment">The breadcrumb's <c>launch.env</c>, applied to both entry points.</param>
/// <param name="Architecture">
/// The package's declared <c>ProcessorArchitecture</c> (appxmanifest.xml, else MicrosoftGame.config): the game's bitness
/// when its exe is encrypted on disk (<see cref="RHI.Core.Services.GameArchitecture"/>, #167); null when it declares none.
/// </param>
/// <param name="StateDir">
/// w12/hdr-wsi (#251 correction F4): the state folder of the installer-app instance owning the title, where RHI reads its
/// Microsoft Store catalogue records (<c>catalogue/products/&lt;StoreId&gt;.json</c>) for the name and the HDR flag;
/// null for Xodus-only installs and unknown instances.
/// </param>
public sealed record XboxPackageInfo(
    string? StoreId,
    string OwningApp,
    string? OpenCommand,
    string? JobsFile,
    IReadOnlyList<string>? PlayArgv = null,
    IReadOnlyList<string>? ShowArgv = null,
    IReadOnlyDictionary<string, string>? LaunchEnvironment = null,
    PeArchitecture? Architecture = null,
    string? StateDir = null);
