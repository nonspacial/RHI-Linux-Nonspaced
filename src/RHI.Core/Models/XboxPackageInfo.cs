namespace RHI.Core.Models;

/// <summary>
/// An Xbox / Game Pass (MSIXVC or Appx) install found by the Xbox detector (Refs #90). Such a game is started only by the
/// app that owns its download and licence (PartingX, or Xodus): Play opens that app, never the game exe.
/// </summary>
/// <param name="StoreId">Microsoft Store product id (the PartingX payload folder name), or null when the package names none.</param>
/// <param name="OwningApp">The app that controls this install: <c>PartingX</c> or <c>Xodus</c>.</param>
/// <param name="OpenCommand">The command that opens <paramref name="OwningApp"/> (the card's own launch command); null when RHI found no way to open it.</param>
/// <param name="JobsFile">PartingX's job list for the instance owning the title (<c>jobs.json</c>); null for Xodus-only installs.</param>
public sealed record XboxPackageInfo(string? StoreId, string OwningApp, string? OpenCommand, string? JobsFile);
