using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// w14 final review M2: display-modes.json's per-game state is keyed by the library key, so it follows a game whose key
/// changes, as the other per-game stores do: "Repair after move" and an install folder move stage it with their commit
/// (<see cref="StageMove"/>), and a scan moves it from the keys older builds wrote (<see cref="AdoptOlderKeys"/>).
/// The staged file is the session's data with the game re-keyed; once the commit is through, the owner reloads the file
/// (<see cref="ReloadAfterCommit"/>) so the session follows.
/// </summary>
public sealed class DisplayModeGameState(DisplayModeStore store, DisplayModeFileStore file) : IGameStateStore
{
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        var data = store.Snapshot();
        if (!move.MoveLibraryEntries(data.Games)) return null;
        return file.Stage(data);
    }

    /// <summary>After a committed move: the session takes the file's data (the game under its new key).</summary>
    public void ReloadAfterCommit()
    {
        if (file.Load(out var unreadable) is { } data && !unreadable) store.Load(data);
    }

    /// <summary>
    /// Moves the state saved under <paramref name="olderKeys"/> (keys this game had in older builds or as a merged
    /// duplicate) onto <paramref name="currentKey"/>; state already under the current key is the newer one and stays.
    /// True when anything moved (the caller saves).
    /// </summary>
    public bool AdoptOlderKeys(string currentKey, IEnumerable<string> olderKeys)
    {
        var moved = false;
        foreach (var old in olderKeys)
            moved |= store.MoveGame(old, currentKey);
        return moved;
    }
}
