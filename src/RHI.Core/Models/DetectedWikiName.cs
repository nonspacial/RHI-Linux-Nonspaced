namespace RHI.Core.Models;

/// <summary>
/// The wiki-name mapping the upstream manifest supplied for a card (null = none), captured once per card instance so that
/// an Edit game wiki name can replace it and clearing that name brings it back (Refs #190).
/// </summary>
public sealed record DetectedWikiName(string? Value);
