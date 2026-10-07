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

## Parity notes (upstream 2.7.6–2.7.9)

### #10/#19 — Neural Rendering add-on versions, in-place swap and NR DLL version (2.7.6/2.7.9)

- Version lists (Feeder, DX11 Bridge, ShortFuse) are sorted newest first by version, then by rc number, so `rc10` lists above `rc5` and a stable release above any rc. ShortFuse "Latest" uses the same order; it previously fell back to GitHub's API order for every `-rcN` tag.
- Full Refresh re-fetches the NR version lists and the ShortFuse latest version. Linux had no 1-hour TTL; the lists were cached for the whole session.
- Update All and the background pass now include DLSS5 Feeder, DX11 Bridge and ShortFuse. With Latest selected they move to the newest release; a pinned tag is respected. Only the add-on file is replaced in place (Feeder: `dlss5-feed.addon64/.addon32`, plus `host64/dlss5-feed-host64.exe` on 32-bit games because it ships in the same archive). Upstream's `rhi_install.txt` ownership check has no Linux equivalent: Linux deployment records already own these files.
- The Feeder, DX11 Bridge and ShortFuse version dropdowns stay enabled while the add-on is installed. Changing one saves the pin and swaps only the add-on file in place; the record gets the new version and hash. If the swap fails, the file, record, pin and dropdown go back to the previous version. If the previous file itself cannot be put back, its `.rhi-swap-old` backup is kept beside it and the status line names it; the record still describes the previous file. ShortFuse is located through its record, so the ZZZ load-order name (`zzz_renodx-dlss.addon64`) is swapped in place and keeps its name.
- New per-game "NR DLL" dropdown (Latest + DLSS manifest NR versions). ShortFuse and DLSS5 Feeder Full install that `nvngx_dlssnr.dll` version; while either is installed, changing it swaps only that DLL in place. Their records keep owning the DLL, so removal still restores the original. A version no longer in the manifest falls back to the newest, as upstream does; the status line and record name the version actually installed.
- Linux deviations: Linux has three NR methods (no standalone DLSS5 Tool), so the "all four methods" note maps to ShortFuse and Feeder. The DX11 Bridge owns no NR DLL on Linux; a game's own `nvngx_dlssnr.dll` stays under the regular DLSS swap row. The Feeder's neural consumer (RenoDX DLSS5 / Deep Fried Chicken) has no separate version selector on Linux, so it is not swapped. Upstream skipped the swap for 32-bit Feeder; Linux swaps `dlss5-feed.addon32` and `host64/dlss5-feed-host64.exe` together from the same release archive.

### #11 — Available HDR mods, collapsed-section summaries, DLL-naming dropdowns (2.7.6)

- **Available HDR mods.** Toolbar button next to Quick start opens a searchable list of every game in the RenoDX and Luma catalogues (not only the library, as upstream), with ✓ / 🔨 (work in progress) / ✗ per mod and a link (RenoDX first, else Luma). Rows are joined by the normalised name both catalogues already use rather than upstream's exact name. Linux deviation: the list comes from the catalogues the installers use, so RenoDX mods with only a Nexus or Discord link and upstream's separate UE-Extended database (`RenoDXdb-unreal.json`) are absent, and Luma rows have no Nexus fallback link. Only http(s) links are offered to the desktop opener. If one catalogue cannot be loaded the dialog says so and shows the other.
- **Collapsed-section summaries.** A collapsed section shows one line under its title: Managed components lists installed components with versions (upstream "Components"), Extras shows ASI Loader and ShortFuse with its version, Runtime shows a forced graphics API (`API: DX11`, upstream "Game Overrides"). The DLSS / Streamline header already shows its summary line at all times; ReShade profile has no upstream counterpart. Upstream's Neural Rendering section and RS channel / launch-argument overrides have no matching Linux section.
- **DLL-naming dropdowns: not ported.** No Linux deployment service consumes a DLL name override: ReShade picks its proxy from the graphics API (`dxgi.dll`, `d3d9.dll`, `opengl32.dll`, or `ReShade64.dll` beside OptiScaler), OptiScaler always deploys as `dxgi.dll`, and Display Commander is a ReShade add-on on Linux, not a proxy DLL. The manifest fields `ReShadeDllNameOverride` / `DisplayCommanderDllNameOverride` / `OptiScalerDllNameOverride` are parsed but unused. Offering a dropdown needs name-aware install, update, uninstall, Wine override and coexistence logic first; tracked on the ticket.

### #12 — PCGamingWiki single-file data (2.7.6/2.7.7)

- **PCGamingWiki data (upstream 2.7.6/2.7.7).** Graphics API and engine hints come from upstream's single `database/pcgw_data.json` (about 55k games) instead of per-game PCGW requests. Lookup order matches upstream: `name_overrides`, exact page title, Steam AppID, then straight/curly apostrophe and ™/®/© normalisation. The engine fills `EngineHint` only when the manifest and local detection found none; a PE-scan DirectX 9 result is corrected when PCGW lists only DX11/DX12/Vulkan (log and config evidence is never overridden). Linux decision: the file is **fetched at runtime, not bundled** in the AppImage (it changes several times a week and is about 5 MB). It is cached at `~/.cache/rhi-linux/pcgw_data.json`, used without a request for 12 hours, then revalidated with its ETag; failures fall back to the last good copy, then to the copy pinned at RHI 2.7.9. Games missing from the file still use the live PCGW page lookup, which is serialised at one request per second and pauses on 429/503 for the server's `Retry-After` (clamped to 10 s–15 min; 5 min after a timeout or network error), then resumes on its own. Not ported: PCGW config-file paths for Engine.ini placement (the Linux port never consumed them) and the PCGW link on game cards (no such UI on Linux).

### #13 — OptiScaler FG Nvngx Combo and RR Preset F (2.7.6)

- 2.7.6 OptiScaler: FG Nvngx override offers `Combo` (`[FrameGen] FGNvngxReplacement=Combo`) and the DLSS RR preset list adds Preset F. Deviation: Linux writes `[DLSSD] RenderPresetForAll=6` for F, following OptiScaler's documented numbering (0 = Default, 1 = A); upstream writes `5`, which OptiScaler reads as E.

### #14 — RenoDX Unity add-on URL and download cache (2.7.6)

- 2.7.6 RenoDX Unity add-on: any `renodx-unityengine.addon64/32` URL (manifest, RHI database or wiki, including the old `notvoosh.github.io` Pages links) is resolved to the `NotVoosh/renodx-unity` GitHub Releases snapshot. Downloads are cached in `~/.cache/rhi-linux/renodx/` and reused unless the remote `Content-Length` explicitly differs (upstream 2.7.9 rule: a failed HEAD or missing length keeps the cache). Linux addition: a `.source` sidecar ties each cached file to its URL so a same-named add-on from another repository is never served from cache. Host allow-listing for catalogue URLs remains DEP-06.

### #15 — Unity per-game INI settings from the RHI database (2.7.9)

- Ported. `RenoDXdb-unity.json` is fetched from the URL upstream reads (`RankFTW/rhi-repo` `main`, `database/`), HTTPS on `raw.githubusercontent.com` only. Upstream keeps an in-memory ETag per session; Linux keeps the parsed file for 1 hour in memory, stores the last good copy and its ETag at `~/.cache/rhi-linux/renodx/RenoDXdb-unity.json` and revalidates with `If-None-Match`. If the live file fails, the cached copy is used, then the copy pinned at RHI 2.7.9. A database failure never blocks the add-on install.
- When RenoDX is installed or updated on a Unity game (engine hint says Unity, or `UnityPlayer.dll` is present, which is how upstream sets that hint), the game's `Upgrades` keys are written to `[renodx]` in its ReShade.ini. The game is looked up by name, ignoring case, then with ™/®/© removed. Token parsing is upstream's: `Output Size`/`Output Ratio`/`Any Size` become 1/2/3, `0`/`1`/`2` are kept as written, and a key with no value is written empty. Upstream writes the database keys before the manifest `renodxIniOverrides`, so the manifest wins on a shared key. Nothing is written when the game has no ReShade.ini.
- Linux deviations: the keys go through the same ownership state as the manifest overrides, unlike upstream, which force-writes them every time. A key the user changes after install is kept on update. Remove puts back each value RHI replaced and deletes the keys it added. Keys that are not plain `[A-Za-z0-9_]` names are dropped, so the remote file cannot inject INI lines. Not ported in this ticket: upstream's empty Unity placeholder keys, the Unity keys in the RenoDX ⚙ compatibility settings (Swapchain Proxy/Encoding, Tonemap/Scaling Offset and so on), Unity `Comments` in the info dialog, the Unity status icons and the Available HDR Mods rows.

### #16 — Control Ultimate Edition RR automation (2.7.9)

- Installing RenoDX on Control Ultimate Edition first shows upstream's confirmation (not an HDR mod; the two RR strategies; DLSS, RT and SSAO must be on — that line is from 2.8.x). When the catalogue resolves `renodx-control-rr.addon64`, the install then puts the newest DLSS manifest `nvngx_dlss.dll` and `nvngx_dlssd.dll` in the game folder and sets `"m_eHDRPreset": 2` in `renderer.ini`. As upstream, each step stands alone: a failed download or a missing `renderer.ini` (the game writes it on first launch) is reported and the other steps still run.
- Location: on Steam, Epic and GOG, `renderer.ini` sits in the game folder next to the executable (PCGamingWiki; upstream also patches it in the install folder), not in the Wine prefix. RHI Linux checks the binary folder, then the install root; it never edits the prefix.
- Linux deviations: upstream does not undo these steps on uninstall; RHI Linux records them on the RenoDX record and removing RenoDX undoes them. DLLs go through the usual `.original` replace. If `.original` already existed from an earlier DLSS swap, the DLL present at install time is kept as `<dll>.rhi-control-previous` and put back on removal. `renderer.ini` keeps its exact pre-edit bytes in the record. If the game has rewritten the file since, only the preset goes back, and only while it is still 2. A DLL changed outside RHI is left alone. An update or reinstall puts the old state back first, then applies the step again, so the restore point stays the pre-RHI state. The step re-runs on update only for installs that already carry it. It also requires the game title, not just the add-on name.
- Not ported: clearing the DLSS SR preset in the NVIDIA driver profile (NvAPI driver settings do not exist under Wine/Proton; the result says so). Upstream's drag-and-drop and watch-folder triggers do not apply: dropped add-ons are only imported into Custom Add-ons on Linux.

### #17 — DLSS Tool (ShortFuse) ZZZ load order (2.7.9)

- **DLSS Tool (ShortFuse) ZZZ load order (2.7.9)** — ported. Per-game toggle deploys or renames the add-on to `zzz_renodx-dlss.addon64`; install, remove, probe and the DLSS5 Feeder conflict guard recognise both names. Applies under Wine/Proton because ReShade loads add-ons in directory-iteration order and Wine's ntdll sorts directory listings case-insensitively, as NTFS does. Linux deviations: the rename is refused (rather than forced) when the add-on was modified outside RHI, the other name is already taken or the deployment ledger is unreadable, and a failed record write after the rename undoes the rename (or, if that fails too, reports which name the file and the record each hold); a reinstall honours the preference; any in-place update or version swap locates the add-on by the path recorded in its ownership record, never by the fixed `renodx-dlss.addon64` name, so both names keep working. The swap does not follow the other name when a rename was cut short and the record lags: it refuses with "Managed file is missing: … Reinstall the component instead" and changes nothing. Toggling the ZZZ setting repairs the record (it follows the add-on to the name on disk and saves that path), after which the swap works. The settings toggle shows the name actually on disk for an installed add-on.

### #18 — Component update history (2.7.9)

- **2.7.9 component update history (#18).** The "Updates" toolbar button opens a dated log (Today / Yesterday / full date, old → new version, clearable). Linux deviation: upstream logs global payload downloads; RHI Linux has no global staging, so it logs each per-game install, update and removal of RHI-managed components (manual actions, Update all and the background pass), including managed dependencies and failures. DLSS/Streamline DLL swaps, shader-pack catalogue refreshes and add-on selection changes are not recorded. Neural Rendering in-place swaps are recorded (#112): add-on version swaps from the dropdowns and Update All with old → new version, and the NR DLL swap as "<method> (nvngx_dlssnr.dll)" with the old → new NR DLL version; a failed swap is logged as a failure. Stored in `~/.local/share/rhi-linux/update-log.json` (newest 200 events, atomic writes; an unreadable file is copied aside and recording pauses until the history is cleared).

### #20/#21 — Shaders and ReShade channel (2.7.9)

- **Shaders (2.7.9):** upstream's global shader "Off" overriding per-game overrides does not apply: RHI Linux has no global shader mode or global selection fallback; each game's pack selection is stored on its own ReShade record. The custom-folder auto-tick fix is ported: dependency expansion never pulls in the local Custom Shaders pack; it deploys only when ticked explicitly.
- **ReShade (2.7.9):** the Custom-channel false update does not apply: the channel is stored on the ReShade record and rewritten by the apply that changes it, so the update check never sees a stale channel. Covered by tests.

### #22 — Launch working directory (2.7.9)

Direct launches (standalone Wine prefixes and manual games) start in the launch executable's folder, falling back to the game folder, as upstream's exe-override and auto-detected-exe launches do. Steam, Heroic and Lutris hand-offs keep the store client's own working directory, matching upstream's protocol launches. Upstream's per-game launch exe override and manifest `launchExeOverrides` as a *launch* target are not ported: Linux launches run the stored launch command, and the manifest override only selects the patch target.

## Parity notes (upstream 2.8.0–2.8.5)

### #138 — Donate dialog (2.8.2)

- Toolbar **Donate** button after Links / MOTD / Updates (Linux has no Help button); like every right-end button it moves into the `…` menu at narrow widths.
- The dialog lists every manifest `donationUrls` author with its `authorRoles` entry, sorted case-insensitively, each with a Ko-fi button (URL in a 2 s tooltip), plus upstream's intro and the "link Discord to Ko-fi before donating" warning. Data comes from the shared manifest service, so new authors show after a library Refresh or restart without an app update; offline it uses the last-known-good copy, then the pinned manifest (which predates `authorRoles`: names and links only).
- Only absolute https links without credentials are offered; anything else is dropped. Links open through `LinuxDesktopOpener.TryOpenUri` (argument list, no shell); a failure shows in the dialog's status line.
- Not ported: upstream's hard-coded fallback author list (the pinned manifest covers offline first runs) and the `authorDisplayNames` mapping (it maps wiki maintainer handles for card badges, not donation keys). No RHI-Linux entry: upstream's list only, pending owner choice.
