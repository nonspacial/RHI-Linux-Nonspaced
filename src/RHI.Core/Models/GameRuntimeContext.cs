namespace RHI.Core.Models;

public sealed record GameRuntimeContext(
    string GameDirectory,
    string? PrefixDirectory,
    string? Executable,
    string Source,
    int? SteamAppId,
    string? Runner);
