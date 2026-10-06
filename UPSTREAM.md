# Upstream policy

RHI Linux targets functional parity with [RankFTW/RHI](https://github.com/RankFTW/RHI) for Windows games running through Wine and Proton.

The current RHI Linux v0.9.23.4-beta.1 release contains reviewed behavior synchronized through the upstream RHI 2.7.5 line.

Functional parity is the goal; source-code or platform-implementation parity is not. When upstream provides a feature that can function under Wine/Proton, RHI Linux should expose the same capability and workflow, adapting the implementation where Linux requires it. A feature may remain unavailable when its required runtime behavior genuinely cannot function on the target Linux/Wine/Proton configuration.

Platform adaptations include:

- Avalonia instead of WinUI / Windows App SDK;
- Steam, Heroic, Lutris and standalone Wine discovery;
- Wine/Proton prefix registry handling and per-game DLL overrides;
- DXVK/VKD3D-Proton integration through the selected runner rather than Windows-native management;
- Windows driver APIs are adapted only where a functional Linux/Wine/Proton path exists;
- native Linux games are not targets for Windows DLL injection components.

RHI Linux remains a GPLv3 derivative of the upstream project.

## Parity notes since 2.7.5

- **2.7.9 component update history (#18).** The "Updates" toolbar button opens a dated log (Today / Yesterday / full date, old → new version, clearable). Linux deviation: upstream logs global payload downloads; RHI Linux has no global staging, so it logs each per-game install, update and removal of RHI-managed components (manual actions, Update all and the background pass), including managed dependencies and failures. DLSS/Streamline DLL swaps, shader-pack catalogue refreshes and add-on selection changes are not recorded. Stored in `~/.local/share/rhi-linux/update-log.json` (newest 200 events, atomic writes; an unreadable file is copied aside and recording pauses until the history is cleared).
