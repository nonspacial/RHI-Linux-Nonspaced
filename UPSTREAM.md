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

## Parity note: launch working directory (upstream 2.7.9, #22)

Direct launches (standalone Wine prefixes and manual games) start in the launch executable's folder, falling back to the game folder, as upstream's exe-override and auto-detected-exe launches do. Steam, Heroic and Lutris hand-offs keep the store client's own working directory, matching upstream's protocol launches. Upstream's per-game launch exe override and manifest `launchExeOverrides` as a *launch* target are not ported: Linux launches run the stored launch command, and the manifest override only selects the patch target.
