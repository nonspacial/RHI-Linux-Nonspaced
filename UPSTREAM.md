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

## Upstream 2.7.6–2.7.9 port notes

- 2.7.6 OptiScaler: FG Nvngx override offers `Combo` (`[FrameGen] FGNvngxReplacement=Combo`) and the DLSS RR preset list adds Preset F. Deviation: Linux writes `[DLSSD] RenderPresetForAll=6` for F, following OptiScaler's documented numbering (0 = Default, 1 = A); upstream writes `5`, which OptiScaler reads as E.
