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

## Parity notes (upstream 2.7.6–2.7.9 sync)

### Neural Rendering add-on versions (2.7.9, #19)

- Version lists (Feeder, DX11 Bridge, ShortFuse) are sorted newest first by version, then by rc number, so `rc10` lists above `rc5` and a stable release above any rc. ShortFuse "Latest" uses the same order; it previously fell back to GitHub's API order for every `-rcN` tag.
- Full Refresh re-fetches the NR version lists and the ShortFuse latest version. Linux had no 1-hour TTL; the lists were cached for the whole session.
- Update All and the background pass now include DLSS5 Feeder, DX11 Bridge and ShortFuse. With Latest selected they move to the newest release; a pinned tag is respected. Only the add-on file is replaced in place (Feeder: `dlss5-feed.addon64/.addon32`, plus `host64/dlss5-feed-host64.exe` on 32-bit games because it ships in the same archive). Upstream's `rhi_install.txt` ownership check has no Linux equivalent: Linux deployment records already own these files.

### Neural Rendering in-place swap and NR DLL version (2.7.6, #10)

- The Feeder, DX11 Bridge and ShortFuse version dropdowns stay enabled while the add-on is installed. Changing one saves the pin and swaps only the add-on file in place; the record gets the new version and hash. If the swap fails, the file, record, pin and dropdown go back to the previous version. If the previous file itself cannot be put back, its `.rhi-swap-old` backup is kept beside it and the status line names it; the record still describes the previous file. ShortFuse is located through its record, so the ZZZ load-order name (`zzz_renodx-dlss.addon64`) is swapped in place and keeps its name.
- New per-game "NR DLL" dropdown (Latest + DLSS manifest NR versions). ShortFuse and DLSS5 Feeder Full install that `nvngx_dlssnr.dll` version; while either is installed, changing it swaps only that DLL in place. Their records keep owning the DLL, so removal still restores the original. A version no longer in the manifest falls back to the newest, as upstream does; the status line and record name the version actually installed.
- Linux deviations: Linux has three NR methods (no standalone DLSS5 Tool), so the "all four methods" note maps to ShortFuse and Feeder. The DX11 Bridge owns no NR DLL on Linux; a game's own `nvngx_dlssnr.dll` stays under the regular DLSS swap row. The Feeder's neural consumer (RenoDX DLSS5 / Deep Fried Chicken) has no separate version selector on Linux, so it is not swapped. Upstream skipped the swap for 32-bit Feeder; Linux swaps `dlss5-feed.addon32` and `host64/dlss5-feed-host64.exe` together from the same release archive.
