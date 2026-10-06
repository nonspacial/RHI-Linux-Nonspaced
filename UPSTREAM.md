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

### #12 — PCGamingWiki single-file data (2.7.6/2.7.7)

- **PCGamingWiki data (upstream 2.7.6/2.7.7).** Graphics API and engine hints come from upstream's single `database/pcgw_data.json` (about 55k games) instead of per-game PCGW requests. Lookup order matches upstream: `name_overrides`, exact page title, Steam AppID, then straight/curly apostrophe and ™/®/© normalisation. The engine fills `EngineHint` only when the manifest and local detection found none; a PE-scan DirectX 9 result is corrected when PCGW lists only DX11/DX12/Vulkan (log and config evidence is never overridden). Linux decision: the file is **fetched at runtime, not bundled** in the AppImage (it changes several times a week and is about 5 MB). It is cached at `~/.cache/rhi-linux/pcgw_data.json`, used without a request for 12 hours, then revalidated with its ETag; failures fall back to the last good copy, then to the copy pinned at RHI 2.7.9. Games missing from the file still use the live PCGW page lookup, which is serialised at one request per second and pauses on 429/503 for the server's `Retry-After` (clamped to 10 s–15 min; 5 min after a timeout or network error), then resumes on its own. Not ported: PCGW config-file paths for Engine.ini placement (the Linux port never consumed them) and the PCGW link on game cards (no such UI on Linux).

### #13 — OptiScaler FG Nvngx Combo and RR Preset F (2.7.6)

- 2.7.6 OptiScaler: FG Nvngx override offers `Combo` (`[FrameGen] FGNvngxReplacement=Combo`) and the DLSS RR preset list adds Preset F. Deviation: Linux writes `[DLSSD] RenderPresetForAll=6` for F, following OptiScaler's documented numbering (0 = Default, 1 = A); upstream writes `5`, which OptiScaler reads as E.

### #14 — RenoDX Unity add-on URL and download cache (2.7.6)

- 2.7.6 RenoDX Unity add-on: any `renodx-unityengine.addon64/32` URL (manifest, RHI database or wiki, including the old `notvoosh.github.io` Pages links) is resolved to the `NotVoosh/renodx-unity` GitHub Releases snapshot. Downloads are cached in `~/.cache/rhi-linux/renodx/` and reused unless the remote `Content-Length` explicitly differs (upstream 2.7.9 rule: a failed HEAD or missing length keeps the cache). Linux addition: a `.source` sidecar ties each cached file to its URL so a same-named add-on from another repository is never served from cache. Host allow-listing for catalogue URLs remains DEP-06.

### #20/#21 — Shaders and ReShade channel (2.7.9)

- **Shaders (2.7.9):** upstream's global shader "Off" overriding per-game overrides does not apply: RHI Linux has no global shader mode or global selection fallback; each game's pack selection is stored on its own ReShade record. The custom-folder auto-tick fix is ported: dependency expansion never pulls in the local Custom Shaders pack; it deploys only when ticked explicitly.
- **ReShade (2.7.9):** the Custom-channel false update does not apply: the channel is stored on the ReShade record and rewritten by the apply that changes it, so the update check never sees a stale channel. Covered by tests.

### #22 — Launch working directory (2.7.9)

Direct launches (standalone Wine prefixes and manual games) start in the launch executable's folder, falling back to the game folder, as upstream's exe-override and auto-detected-exe launches do. Steam, Heroic and Lutris hand-offs keep the store client's own working directory, matching upstream's protocol launches. Upstream's per-game launch exe override and manifest `launchExeOverrides` as a *launch* target are not ported: Linux launches run the stored launch command, and the manifest override only selects the patch target.
