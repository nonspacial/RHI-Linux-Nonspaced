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

## 2.7.6–2.7.9 parity notes

- **DLSS Tool (ShortFuse) ZZZ load order (2.7.9)** — ported. Per-game toggle deploys or renames the add-on to `zzz_renodx-dlss.addon64`; install, remove, probe and the DLSS5 Feeder conflict guard recognise both names. Applies under Wine/Proton because ReShade loads add-ons in directory-iteration order and Wine's ntdll sorts directory listings case-insensitively, as NTFS does. Linux deviations: the rename is refused (rather than forced) when the add-on was modified outside RHI or the other name is already taken; there is no in-place ShortFuse auto-update on Linux, so "update/version swap" means a reinstall, which honours the preference.

RHI Linux remains a GPLv3 derivative of the upstream project.
