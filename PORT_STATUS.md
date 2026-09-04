# RHI Linux port status — v0.1.0

## Accepted in this snapshot

- **P0 Architecture:** native Linux process, `net8.0`, Avalonia desktop UI.
- **P1 Upstream model preservation:** original `DetectedGame` fields retained and Linux fields added.
- **P2 Steam discovery:** native paths + libraryfolders VDF + ACF + AppID + compatdata prefix.
- **P3 Other launchers:** Heroic/Lutris/standalone Wine discovery backends exist.
- **P4 Safe local probe:** component detection is read-only and does not overwrite game files.
- **P5 Deployment primitive:** `.original` preservation, verified temp-file replacement and exact pre-batch rollback implemented.
- **P6 DLSS file backend:** upstream manifest, cache, SR/RR/FG/NR detection, version download/swap/restore wired to the GUI.
- **P7 Streamline core:** known `sl.*.dll` set can be swapped transactionally; UI is intentionally not exposed yet.
- **P8 No false parity claims:** unsupported generic component modification buttons are locked in the GUI.

## Not accepted yet

- Full game-manifest parity with upstream `manifest.json` (the DLSS manifest path is already implemented).
- Generic component downloads/extraction beyond the implemented DLSS/Streamline backend.
- ReShade/RenoDX/Luma/OptiScaler installation/removal.
- Streamline swap/restore UI (DLSS SR/RR/FG/NR UI is present).
- Exact executable/path mapping for every launcher schema.
- Full original RHI settings/profile persistence.
- NVIDIA DRS overrides (Windows NvAPI-only).
- HDR compositor control.
- Flatpak/AppImage/AUR release packaging.

## Safety invariants for later gates

1. Never overwrite an unknown existing DLL without preserving the original.
2. Never treat a Proton-provided DXVK DLL as a game-local RHI-managed DXVK install.
3. Never write to a prefix or game tree merely because it was discovered; modification is explicit.
4. Any multi-file deployment must have rollback semantics.
5. Unsupported NVIDIA DRS controls must remain visibly unsupported rather than silently no-op.
6. Anti-cheat warning behavior from upstream must be retained for ReShade addon deployment.
