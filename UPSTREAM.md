# Upstream policy

RHI Linux targets functional parity with [RankFTW/RHI](https://github.com/RankFTW/RHI) for Windows games running through Wine and Proton.

The wizardkof RHI Linux v0.9.23.4-beta.1 release this fork started from was synchronized through the upstream RHI 2.7.5 line; this fork's release `2.8.8` is synchronized through the upstream RHI 2.8.8 release (tag `RHI-2.8.8`, commit `3689428`); its unpublished `2.8.8-beta.1` builds were synchronized through the v2.8.8 Beta notes, up to commit `c1076d3`. The notes below describe each ported feature and its Linux adaptation.

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

- **Available HDR mods** (**Available mods** since #256). Toolbar button next to Quick start opens a searchable list of every game in the RenoDX and Luma catalogues (not only the library, as upstream), with ✓ / 🔨 (work in progress) / ✗ per mod and a link (RenoDX first, else Luma). Rows are joined by the normalised name both catalogues already use rather than upstream's exact name. Since #197 the RenoDX column holds every named mod of the RHI database, also those with only a Nexus Mods or Discord link (the row links there), and the games of upstream's UE-Extended database (`RenoDXdb-unreal.json`). Linux deviation: Luma rows have no Nexus fallback link (the Luma catalogue the installer uses has none). Only http(s) links are offered to the desktop opener. If one catalogue cannot be loaded the dialog says so and shows the other. Since upstream 2.8.8 (#256, #257, see the 2.8.8 release notes below) the dialog is **Available mods**: an Ultra+ column, Source engine games in the RenoDX column, every tick a link to that mod's page instead of the link column, per-mod counts and mod filters in the search box.
- **Collapsed-section summaries.** A collapsed section shows one line under its title: Managed components lists installed components with versions (upstream "Components"), Extras shows ASI Loader and ShortFuse with its version, Runtime shows a forced graphics API (`API: DX11`, upstream "Game Overrides"). The DLSS / Streamline header already shows its summary line at all times; ReShade profile has no upstream counterpart. Upstream's Neural Rendering section and RS channel / launch-argument overrides have no matching Linux section.
- **DLL-naming dropdowns: not ported.** No Linux deployment service consumes a DLL name override: ReShade picks its proxy from the graphics API (`dxgi.dll`, `d3d9.dll`, `opengl32.dll`, or `ReShade64.dll` beside OptiScaler), OptiScaler always deploys as `dxgi.dll`, and Display Commander is a ReShade add-on on Linux, not a proxy DLL. The manifest fields `ReShadeDllNameOverride` / `DisplayCommanderDllNameOverride` / `OptiScalerDllNameOverride` are parsed but unused. Offering a dropdown needs name-aware install, update, uninstall, Wine override and coexistence logic first; tracked on the ticket.

### #12 — PCGamingWiki single-file data (2.7.6/2.7.7)

- **PCGamingWiki data (upstream 2.7.6/2.7.7).** Graphics API and engine hints come from upstream's single `database/pcgw_data.json` (about 55k games) instead of per-game PCGW requests. Lookup order matches upstream: `name_overrides`, exact page title, Steam AppID, then straight/curly apostrophe and ™/®/© normalisation. The engine fills `EngineHint` only when the manifest and local detection found none; a PE-scan DirectX 9 result is corrected when PCGW lists only DX11/DX12/Vulkan (log and config evidence is never overridden). Linux decision: the file is **fetched at runtime, not bundled** in the AppImage (it changes several times a week and is about 5 MB). It is cached at `~/.cache/rhi-linux/pcgw_data.json`, used without a request for 12 hours, then revalidated with its ETag; failures fall back to the last good copy, then to the copy pinned at RHI 2.7.9. Games missing from the file still use the live PCGW page lookup, which is serialised at one request per second and pauses on 429/503 for the server's `Retry-After` (clamped to 10 s–15 min; 5 min after a timeout or network error), then resumes on its own. Not ported: PCGW config-file paths for Engine.ini placement (the Linux port never consumed them). The PCGW link on game cards came with #183 (card menu → Web pages).

### #13 — OptiScaler FG Nvngx Combo and RR Preset F (2.7.6)

- 2.7.6 OptiScaler: FG Nvngx override offers `Combo` (`[FrameGen] FGNvngxReplacement=Combo`) and the DLSS RR preset list adds Preset F. Deviation: Linux writes `[DLSSD] RenderPresetForAll=6` for F, following OptiScaler's documented numbering (0 = Default, 1 = A); upstream writes `5`, which OptiScaler reads as E.

### #14 — RenoDX Unity add-on URL and download cache (2.7.6)

- 2.7.6 RenoDX Unity add-on: any `renodx-unityengine.addon64/32` URL (manifest, RHI database or wiki, including the old `notvoosh.github.io` Pages links) is resolved to the `NotVoosh/renodx-unity` GitHub Releases snapshot. Downloads are cached in `~/.cache/rhi-linux/renodx/` and reused while the remote matches (upstream 2.7.9 rule: a failed HEAD or a server sending no validators keeps the cache). Linux addition: a `.source` sidecar ties each cached file to its URL and to the ETag/Last-Modified/size it was served with, so a same-named add-on from another repository, or a same-size snapshot rebuild, is never served from cache (#107; upstream compares size only). A URL-only sidecar from an older build is refreshed once. Host allow-listing for catalogue URLs remains DEP-06.

### #15 — Unity per-game INI settings from the RHI database (2.7.9)

- Ported. `RenoDXdb-unity.json` is fetched from the URL upstream reads (`RankFTW/rhi-repo` `main`, `database/`), HTTPS on `raw.githubusercontent.com` only. Upstream keeps an in-memory ETag per session; Linux keeps the parsed file for 1 hour in memory, stores the last good copy and its ETag at `~/.cache/rhi-linux/renodx/RenoDXdb-unity.json` and revalidates with `If-None-Match`. If the live file fails, the cached copy is used, then the copy pinned at RHI 2.7.9. A database failure never blocks the add-on install.
- When RenoDX is installed or updated on a Unity game (engine hint says Unity, or `UnityPlayer.dll` is present, which is how upstream sets that hint), the game's `Upgrades` keys are written to `[renodx]` in its ReShade.ini. The game is looked up by name, ignoring case, then with ™/®/© removed. Token parsing is upstream's: `Output Size`/`Output Ratio`/`Any Size` become 1/2/3, `0`/`1`/`2` are kept as written, and a key with no value is written empty. Upstream writes the database keys before the manifest `renodxIniOverrides`, so the manifest wins on a shared key. Nothing is written when the game has no ReShade.ini.
- Linux deviations: the keys go through the same ownership state as the manifest overrides, unlike upstream, which force-writes them every time. A key the user changes after install is kept on update. A key first applied on a later update replaces the current value until removal: on games where RenoDX was installed before this port, the database keys are new to the ownership state on their first update, so a value the user set by hand earlier (e.g. `Use_Swapchain_Proxy=0`) is overwritten, as upstream's force-write and the manifest overrides do; it is recorded as the previous value and put back when RenoDX is removed. Remove puts back each value RHI replaced and deletes the keys it added. Keys that are not plain `[A-Za-z0-9_]` names are dropped, so the remote file cannot inject INI lines. Not ported in this ticket: upstream's empty Unity placeholder keys, the Unity keys in the RenoDX ⚙ compatibility settings (Swapchain Proxy/Encoding, Tonemap/Scaling Offset and so on), Unity `Comments` in the info dialog, the Unity status icons and the Available HDR Mods rows.

### #16 — Control Ultimate Edition RR automation (2.7.9)

- Installing RenoDX on Control Ultimate Edition first shows upstream's confirmation (not an HDR mod; the two RR strategies; DLSS, RT and SSAO must be on — that line is from upstream 2.8.1, `e10099f`). When the catalogue resolves `renodx-control-rr.addon64`, the install then puts the newest DLSS manifest `nvngx_dlss.dll` and `nvngx_dlssd.dll` in the game folder and sets `"m_eHDRPreset": 2` in `renderer.ini`. As upstream, each step stands alone: a failed download or a missing `renderer.ini` (the game writes it on first launch) is reported and the other steps still run.
- Location: on Steam, Epic and GOG, `renderer.ini` sits in the game folder next to the executable (PCGamingWiki; upstream also patches it in the install folder), not in the Wine prefix. RHI Linux checks the binary folder, then the install root; it never edits the prefix.
- Linux deviations: upstream does not undo these steps on uninstall; RHI Linux records them on the RenoDX record and removing RenoDX undoes them. DLLs go through the usual `.original` replace. If `.original` already existed from an earlier DLSS swap, the DLL present at install time is kept as `<dll>.rhi-control-previous` and put back on removal. `renderer.ini` keeps its exact pre-edit bytes in the record. If the game has rewritten the file since, only the preset goes back, and only while it is still 2. A DLL changed outside RHI is left alone, and so is its `.rhi-control-previous` copy (deleted only when the DLL or its `.original` already holds the same bytes; the note says where it is); an install never overwrites a copy left there and says so. An update or reinstall puts the old state back first, then applies the step again, so the restore point stays the pre-RHI state for every file RHI still owns. Unlike upstream, which re-runs every step on each install, a DLL or HDR preset the user changed since the install is left as they set it and is not applied again (#124); RHI no longer owns it, so removal leaves it too. The step re-runs on update only for installs that already carry it. It also requires the game title, not just the add-on name.
- Not ported: clearing the DLSS SR preset in the NVIDIA driver profile (there is no driver profile under Wine/Proton; the result says so). Since #211 a per-game DLSS preset is RHI's own launch setting (DLSS section → NVIDIA settings), which the Control install leaves as the user chose it. Upstream's drag-and-drop and watch-folder triggers: since #195 a dropped or watched Control add-on is refused with a pointer to the RenoDX row's Install, which runs these steps.

### #17 — DLSS Tool (ShortFuse) ZZZ load order (2.7.9)

- **DLSS Tool (ShortFuse) ZZZ load order (2.7.9)** — ported. Per-game toggle deploys or renames the add-on to `zzz_renodx-dlss.addon64`; install, remove, probe and the DLSS5 Feeder conflict guard recognise both names. Applies under Wine/Proton because ReShade loads add-ons in directory-iteration order and Wine's ntdll sorts directory listings case-insensitively, as NTFS does. Linux deviations: the rename is refused (rather than forced) when the add-on was modified outside RHI, the other name is already taken or the deployment ledger is unreadable, and a failed record write after the rename undoes the rename (or, if that fails too, reports which name the file and the record each hold); a reinstall honours the preference; any in-place update or version swap locates the add-on by the path recorded in its ownership record, never by the fixed `renodx-dlss.addon64` name, so both names keep working. The swap does not follow the other name when a rename was cut short and the record lags: it refuses with "Managed file is missing: … Reinstall the component instead" and changes nothing. Toggling the ZZZ setting repairs the record (it follows the add-on to the name on disk and saves that path), after which the swap works. The settings toggle shows the name actually on disk for an installed add-on.

### #18 — Component update history (2.7.9)

- **2.7.9 component update history (#18).** The "Updates" toolbar button opens a dated log (Today / Yesterday / full date, old → new version, clearable). Linux deviation: upstream logs global payload downloads; RHI Linux has no global staging, so it logs each per-game install, update and removal of RHI-managed components (manual actions, Update all and the background pass), including managed dependencies and failures. DLSS/Streamline DLL swaps, shader-pack catalogue refreshes and add-on selection changes are not recorded. Neural Rendering in-place swaps are recorded (#112): add-on version swaps from the dropdowns and Update All with old → new version, and the NR DLL swap as "<method> (nvngx_dlssnr.dll)" with the old → new NR DLL version; a failed swap is logged as a failure. Stored in `~/.local/share/rhi-linux/update-log.json` (newest 200 events, atomic writes; an unreadable file is copied aside and recording pauses until the history is cleared).

### #20/#21 — Shaders and ReShade channel (2.7.9)

- **Shaders (2.7.9):** upstream's fix (a global shader "Off" no longer overrides a game's own Select) is ported with the global shader set in wave 7 (#179, see below): a game on its own list or None keeps it whatever the global set holds. The custom-folder auto-tick fix is ported: dependency expansion never pulls in the local Custom Shaders pack; it deploys only when ticked explicitly.
- **ReShade (2.7.9):** the Custom-channel false update does not apply: the channel is stored on the ReShade record and rewritten by the apply that changes it, so the update check never sees a stale channel. Covered by tests.

### #22 — Launch working directory (2.7.9)

Direct launches (standalone Wine prefixes and manual games) start in the launch executable's folder, falling back to the game folder, as upstream's exe-override and auto-detected-exe launches do. Steam, Heroic and Lutris hand-offs keep the store client's own working directory, matching upstream's protocol launches. Upstream's per-game launch exe override and manifest `launchExeOverrides` as a *launch* target are not ported: Linux launches run the stored launch command, and the manifest override only selects the patch target.

## Parity notes (upstream 2.8.0–2.8.5)

### #129 — RenoDX updates: rebuilt snapshots, Update All only installs real updates (2.8.0)

- Update All (manual and background) checks each RHI-managed RenoDX mod before touching it and reinstalls only when the remote add-on changed; an unchanged mod is reported as current with no download, no file write and no history entry. Other families keep the manual refresh until #113 (`LinuxUpdateAllService.CheckedInManualMode`).
- Detection: upstream flags a rebuilt snapshot from its size (HEAD `Content-Length`) or, for `/snapshot/` and `*.github.io` URLs, by downloading the file and comparing hashes. Linux records the ETag, Last-Modified and size the add-on was served with in the RenoDX record and compares them with a HEAD of the same URL (ETag first, then Last-Modified, then size). Both hosts (GitHub Releases and GitHub Pages/renodx.com) send ETag and Last-Modified, so same-size rebuilds are seen without a download. A changed catalogue URL or mod name still counts as an update.
- A failed or refused HEAD, or a server sending no validators, is never an update: the item is counted as "could not be checked" in the Update All summary. A managed add-on modified outside RHI is still never overwritten (the update fails and the file is kept). A managed mod on a game without a confirmed patch target is counted as failed (updates are blocked until Edit game confirms the executable), never as current. The Update All summary, including "could not be checked", is shown in PT-BR and EN; when items failed it names the first one and its reason (a blocked game says so) and counts the rest (per-item detail is #113).
- Records installed before this change carry no stamp: a remote Last-Modified newer than the install time counts as an update, otherwise the remote size is compared with the installed file (upstream's legacy rule). Residual gap, dev builds only: the size-only download cache (never in a tagged release) could install a stale same-size file after a rebuild; such a record reads as current on every check until the snapshot is rebuilt again upstream or the mod is reinstalled, which records a stamp.

### #130 — Update history logs RenoDX with the add-on's own version (2.8.0)

- Every RenoDX install, update and removal through RHI (detail panel, Update All, background pass, installs pulled in as a dependency) is logged as `old → new` using the add-on file's version, so a same-name snapshot rebuild now writes an Update entry. A failed update is logged as failed with the version it kept.
- Version, as upstream `ReadInstalledVersion`: PE ProductVersion, else FileVersion; RenoDX's `0.YYYY.MMDD.HHMM` is shown as `YY.MMDD.HHMM` (`0.2026.0325.2215` → `26.0325.2215`); no version resource → file time in the same shape. Linux reads the PE `VS_VERSIONINFO` itself (`PeVersionReader`, bounded reads; `FileVersionInfo` does not read PE resources on Linux) and also accepts the fixed `VS_FIXEDFILEINFO` number before falling back to the file time. The cached download keeps the snapshot's Last-Modified as its file time, so a version-less add-on keeps one version across re-downloads.
- The version is stored in the RenoDX record metadata; the record's `Version` stays the catalogue mod name (used to spot a catalogue change). Records from older builds are read from the installed file the first time.
- Since #195 a dropped or watched RenoDX add-on installs into a game (see #195 below); its update-history entry is an Install with the file's own version.

### #131 — Manifest install warnings (2.8.0)

Ported. Before a user-initiated install, the manifest's `installWarnings[<game>][<component>]` text is shown with Continue / Cancel for every component the install adds: the one the user asked for (ReShade, RenoDX, Luma, OptiScaler, REFramework, ReLimiter or Display Commander) and each managed dependency Linux installs automatically with it — ReShade under RenoDX, ReLimiter, Display Commander, Luma, ShortFuse, DLSS5 Feeder and the other ReShade add-ons, and REFramework under ReShade on RE Engine. Upstream has no automatic dependencies (add-ons stay greyed until the user installs ReShade, RE Engine ReShade asks for REFramework first), so there each warning comes from its own install click; Linux shows them together in one dialog, one section per component in install order, each naming its component when more than one is shown. Cancel stops before any file is touched. The list of added components comes from the same dependency plan the install executes (computed off the UI thread), so a refused install (broken managed dependency, conflicting component) shows no dependency warning. Covered paths: the component row's Install, ShortFuse install, applying a ReShade profile (when ReShade is absent or its state is unknown, and for any dependency it would add), the Shaders/Add-ons manager's Apply when RHI does not manage ReShade yet, and a dropped or watched local Luma package. Update All and the background pass never prompt. The game is matched by its canonical manifest name (the library policy pass), and only when that name was reached from the game's own title: a manifest title found only in the folder path attaches no warning (upstream matches the card title). The text itself is upstream English; the title, section headings and buttons are localised. Links: every http(s) URL in the text becomes a button under it (a trailing URL moves out of the text, as upstream; a closing bracket that pairs with one inside the URL stays part of it); buttons open through the desktop handler (`LinuxDesktopOpener`, http(s) only). Linux deviations: upstream links only a trailing URL (GTA V Enhanced's inline DirectStorageFix link is clickable here too); upstream warns again on every ReShade reinstall/update click, Linux only on the first install. The `dxvk` key is DX9 HDR's (#200), shown before its install.

### #132 — ReShade when reshade.me answers HTTP 5xx (2.8.3)

- Ported: the stable-channel page fetch reads the body of a 5xx answer and uses it only when it carries the add-on download link; the setup exe it points to still needs a success status and passes the PE check. 4xx and other statuses stay failures.
- Linux addition: when reshade.me is down (5xx without the link, unreachable or timed out), a fresh stable install falls back to the newest stable copy already in the ReShade cache and says so; with no cached copy it fails with "try again later" before touching any game file. The fallback never replaces a newer installed ReShade with an older cached one. An update (Update All, managed update) uses the cache only when the cached copy is newer than the installed version; otherwise it fails before touching any file with "ReShade server unavailable; installed version kept; try again later" and is flagged as source-unavailable: Update All counts it as "could not be checked" (neither an update nor a failure). An explicit profile Apply may re-use the cached copy, because it applies the changed settings, and says so (the Shaders/Add-ons manager's result line too). The update check reports the outage as "could not be checked", keeps the installed ReShade and waits for the next check. The same holds when the page answers but the setup download then fails with 5xx, a network error or a timeout. A 404 never falls back. The page's download version must be a plain dotted number before it names a cache folder.

### #133 — Revoked GitHub token falls back to anonymous access (2.8.3/2.8.4)

Upstream validates its stored OAuth token against `/user` at start-up and, on 401, strips it from the shared client and clears it from settings. The fork has no sign-in (#134 is parked); the token is the user's own `~/.config/rhi-linux/github_api.txt`, attached per request to `api.github.com` only, so there is no start-up race to fix. `GitHubApiTokenHandler` instead treats any 401 on a request that carried that token as a rejection: GET/HEAD requests without a body are replayed once anonymously, later requests go anonymous for the session, and 403/429 rate-limit responses keep the token. The file is not deleted; rewriting it (new mtime) re-enables the token. The user gets one localised status notice per rejected file version. While an operation (library scan, game analysis, component action) is running, the notice is appended to each of its status lines, so the progress lines that follow do not hide it and the operation's final result line carries it; it is dropped once that line is left on screen with no operation running. The notice is appended to the line on screen, never shown in its place (a result line stays readable); one raised while nothing is running is also appended to the next status line, and a new notice raised just as an operation ends is kept. The token is never logged or shown. The file must be private (mode 0600): when group or other users can access it, RHI restricts it to 0600 before using the token and says so once per file version; if it cannot (e.g. the file belongs to another user) the token is ignored and the notice says to run `chmod 600` on it (#36).

### #135 — OptiScaler FG: nvngx_dlssg.dll in the Streamline stack; Reflex settings (2.8.1)

- Enabling Streamline / DLSS Enabler (OptiScaler Nightly/DLSS NR) also deploys the newest DLSS manifest `nvngx_dlssg.dll` to `OptiScaler/Streamline/`, from the existing DLSS cache. As upstream, it is best effort: when the manifest has no FG build or its download fails, Streamline and DLSS Enabler still deploy and the status line says `nvngx_dlssg.dll` was not deployed. Unlike upstream's fire-and-forget copy, the DLL joins the transactional, record-owned FG stack (hash, `OptiScaler.DlssgVersion`), so disabling the stack or removing OptiScaler deletes it.
- A foreign file already at any FG stack target is no longer refused: it is kept as `<name>.original`, marked pre-existing in the record and restored on disable or OptiScaler removal (an identical file is left alone and unowned, even next to an existing `.original`; otherwise an existing `.original` blocks the deploy). A failed deploy restores it before reporting. OptiScaler removal hash-checks kept files too, so it never restores an `.original` over a file the user replaced, and both disable and removal save their progress when they fail part-way, so a retry completes (also when the retry finds OptiScaler's `dxgi.dll` already gone and moves a coexisting ReShade back; once ReShade has moved, the step is no longer cancellable, so its record always follows the file). Files an older build's Quick Apply / Batch Deploy / panel Streamline swap replaced inside `OptiScaler/` are given back on disable, redeploy, update or removal when their `.original` is exactly RHI's recorded file; any other mismatch is still refused.
- The direct DLSS flows (per-game swap/restore, Quick Apply, Batch Deploy, Restore All) never target the `OptiScaler/` folder or any file in the managed OptiScaler record (incl. the FG stack and the DLSS NR runtime); a Streamline folder holding a record-owned `sl.*` is skipped as a whole. Upstream treats OptiScaler-folder copies as a fallback only; on Linux a swap there would break OptiScaler's ownership hashes or consume the `.original` it restores on removal. The DLSS panel therefore no longer lists those files as the game's, and neither does the Streamline component row: RHI's `OptiScaler/Streamline/sl.*` stack is not the game's Streamline, so it no longer marks Streamline as detected or satisfies MFG Ada Unlock's "game with Streamline frame generation" requirement. The panel takes its swap/restore targets from the same service (`LinuxDlssDefaultsService.TryPanelTargets`), re-filtered at action time.
- Stacks deployed before this change get `nvngx_dlssg.dll` the next time the stack is redeployed (toggle it or change the Streamline version); Update All does not add it.
- The managed stack's `OptiScaler/Streamline/nvngx_dlssg.dll` does not count as the game's own Streamline FG integration for Universal MFG eligibility; the managed stack still qualifies only through FG Output = DLSSG + Enabler.
- Advanced settings (Nightly/DLSS NR only, after FG Nvngx Replacement, as upstream's Nightly FG cog): **Force Reflex** writes `[fakenvapi] ForceReflex` and **Use game's Reflex markers** writes `[DLSSG] UseGamesReflexMarkers`. Linux keeps OptiScaler's own `auto` as a choice next to upstream's 0/1/2 and true/false: an explicit 0 would defeat OptiScaler forcing Reflex off for DLSSG `OverrideForceDMFG`. Force Reflex only acts through OptiScaler's bundled fakenvapi; Proton's own NVAPI is not touched.
- Upstream's new "FG Enabled" row already existed on Linux (`FrameGenEnabled`). The 2.8.3 OptiScaler presets carrying these keys are not ported (Linux has no OptiScaler presets).
- The setting ids are public constants (`DeployStreamlineDlssEnablerSettingId`, `ForceReflexSettingId`, `UseGamesReflexMarkersSettingId`) for the Control UE one-click FG setup (#136).

### #138 — Donate dialog (2.8.2)

- Toolbar **Donate** button after Links / MOTD / Updates (Linux has no Help button); like every right-end button it moves into the `…` menu at narrow widths.
- The dialog lists upstream's built-in authors (the 9 hard-coded in 2.8.5) merged with every manifest `donationUrls` author, as upstream merges them: a manifest link replaces the built-in one, a built-in author the manifest omits stays, and a case-only name clash keeps the first spelling. Each row shows the `authorRoles` entry and a link button ("Ko-fi" for ko-fi.com links, otherwise the site's host; URL in a 2 s tooltip), sorted case-insensitively, under upstream's intro and "link Discord to Ko-fi before donating" warning; the count reads "1 author" / "N authors". Data comes from the shared manifest service, so new authors show after a library Refresh or restart without an app update.
- Offline: the last-known-good manifest copy, else the pinned manifest — itself a remote URL, so it only covers a broken live URL (it predates `authorRoles`: names and links only). A fully offline first run with no cached copy shows the built-in list without roles, with a note giving the load error. Closing the dialog mid-load cancels the fetch.
- Only absolute https links without credentials are offered, built-in and manifest alike; a manifest entry with any other link drops that author rather than reviving the built-in link. Links open through `LinuxDesktopOpener.TryOpenUri` (argument list, no shell); a failure shows in the dialog's status line.
- Not ported: the `authorDisplayNames` mapping (it maps wiki maintainer handles for card badges, not donation keys). The list is upstream's authors only, with no RHI-Linux / fork-maintainer entry (owner decision).

### #139 — Installed RenoDX/Luma add-on file names and mod authors (2.8.1)

- Ported as a secondary line under the RenoDX HDR and Luma Framework rows of Managed components, e.g. `renodx-hogwartslegacy.addon64 · by Marat` and `Luma-Prey.addon · by Ersh, Pumbo`. Upstream puts the file names in the card badge row and prefixes the author badges with `RenoDX:` / `Luma:`; on Linux the row names already say which mod is which, and the header layout is owner-defined. The line stays on one row; a line wider than the row is shortened in the middle (file name start … author end) with the full text in a 2 s tooltip. It is hidden when there is nothing to show.
- File names come from the deployment record (what RHI wrote, so renamed add-ons show their real names), only on an Installed / Update-available row RHI manages, and only names the probe still finds on disk when it finds any (a stale record shows none). It refreshes after install/remove/update because those re-probe the game.
- RenoDX author, in upstream's order: Marat for the UE-Extended build; else the mod's maintainer — the RHI database `author` (kept when a manifest `snapshotOverrides` entry replaces the download, e.g. CODE VEIN II, Days Gone, RoboCop: Rogue City, Routine), or ShortFuse / Voosh for a generic Unreal / Unity fallback, as upstream's fallback mods carry them; else the manifest `authorOverrides` entry; else ShortFuse / Voosh when the add-on is the generic engine build. Upstream credits Marat from its UE-Extended toggle; Linux credits the add-on it actually installs, so the UE-Extended and generic checks read the download URL.
- Luma author: the author stored on the Luma record, else the named catalogue match, else Pumbo for the generic framework package. Dropped local packages, external installs, unsupported titles and not-installed rows of a game whose patch target is unconfirmed get no credit (RHI installs nothing there). The credit pass runs on the UI thread, so it never reads the game's files: it uses the RenoDX/Luma records the component pass already loaded (the record store re-reads `deployments.json` only when its mtime or size changed), the catalogue caches and what the probe found.
- Multi-author fields are split on `&` and `and`, as upstream does (`Marat + Ritsu` and `Ritsu/marat569` stay one name); a comma left before a separator (`Ritsu, & ShortFuse`) is dropped rather than shown. `OopyDoopy` shows as `Jon`, upstream's built-in alias; the manifest `authorDisplayNames` list is not read (it currently holds only that entry).
- Not ported: donation links on author badges (upstream `donationUrls`); the authors' donation links are in the toolbar Donate dialog instead (#138).

### #140 — Available HDR Mods header and ✗ legibility (2.8.5)

- Ported: column headers and ✗ marks use the theme's secondary text colour (`RhiSecondaryTextBrush`, #A0AABB) instead of 70 % opacity text and the muted grey. Measured on the #121518 dialog in the headless render: ✗ 4.19:1 → 7.82:1; headers 8.03:1 → 7.82:1 (already legible on Linux; now on the theme colour like the rest of the app).

### #136 — Control UE: OptiScaler frame generation and "Using HDR?" (2.8.1, pinned build 2.8.7)

- Ported: the Control UE RenoDX confirmation has upstream's two choices under the text, **Install OptiScaler FG** (No/Yes) and **Using HDR?** (open only with FG = Yes, back to No when FG goes to No), in English and Portuguese. FG = Yes runs after the RenoDX install succeeded: OptiScaler Nightly pinned to build `20261004` (upstream 2.8.7 `049077c`: newer nightlies break FG in Control) through the #156 pin, installed as `winmm.dll` with a `winmm` `native,builtin` Wine override, its Streamline + `nvngx_dlssg.dll` + DLSS Enabler stack, and upstream's INI keys (`[Upscalers] Dx12Upscaler=dlss`, `[FrameGen] Enabled=true FGInput=upscaler FGOutput=dlssg FGNvngxReplacement=None`, `[fakenvapi] ForceReflex=2`, `[DLSSG] UseGamesReflexMarkers=false`). The record keeps `Version = 20261004`, so the card shows that build and Update All reports it current, pinned. Then `FgSlHdr10=1|0` under `[renodx-preset1]` in `reshade.ini`, written through RenoDX's owned-key state (kept on the RenoDX record across updates; removal puts the earlier value back unless the user changed it).
- Each step stands alone, as upstream: a failed OptiScaler download or install, stack or INI step is named in the status line after the RenoDX result and never fails or undoes the RenoDX install; the HDR key is written whatever the OptiScaler step did.
- Linux differences: OptiScaler stays a normally managed component (Update All, removal of its files and override; RenoDX removal leaves it, as upstream). An OptiScaler RHI already manages under another build or DLL name is left as it is and the step says to remove it first (upstream reinstalls over it). RHI does not rename an installed ReShade to `dxgi.dll` (no in-place rename since #120); ReShade on DX11/DX12 is `dxgi.dll` by default, and another name is reported in the line. Upstream also excludes ReShade from Update All on that card; not ported: a ReShade update keeps its recorded name on Linux, so nothing can move it.
- An OptiScaler RHI did not install already beside the game under any of upstream's `SupportedDllNames` (the user's own, usually `dxgi.dll`) makes the step refuse, naming the file, before anything is downloaded: two OptiScaler proxies would both load. Upstream installs the second one anyway; RHI Linux keeps the refusal (never take over or double up on content RHI did not install). The same refusal covers every OptiScaler install, and the component scan recognises OptiScaler under all eight names (`d3d11.dll` was missing).
- `FgSlHdr10` is journaled: `reshade.ini` is tracked before the edit and the RenoDX record that owns the key is the commit point, so a crash in between puts `reshade.ini` back at the next start.
- Known limit, kept: the step runs after RenoDX's install has passed its write gate, so its downloads (pinned nightly, Streamline, DLSS Enabler) show no Cancel. Each step is its own journaled unit and RenoDX is already committed, so the result is always consistent; a cancellable second unit needs coordinator support and is ticket-worthy only if a long download proves a problem.
- Checked under Wine: real OptiScaler 0.9.4 as `winmm.dll` beside a test exe that imports winmm loads with the `AppDefaults\<exe>\DllOverrides winmm=native,builtin` value RHI writes (GE-Proton10-18 loads its builtin winmm without it; Wine 11.19 loads the game's copy either way).

## Parity notes (upstream 2.8.6–2.8.7)

### #153 — GitHub release lookups honour /releases/latest (2.8.6)

- Untagged release lookups now ask exactly the endpoint they are given, as upstream `2bfb083`: `/releases/latest` is GitHub's "Latest" release. The fork used to rewrite it to `/releases?per_page=1` (newest by creation date), which hands back an older build when a repo marks an earlier-created release as Latest (live 2026-10-14: discord-fix Latest v1.03, list head v1.0.2). This covers every resolver consumer: ReLimiter, MFG Ada Unlock, DLSS5 Bridge/Feeder, RE Framework, Luma, dgVoodoo (Feeder staging).
- Prerelease-only repos opt in to the list endpoint explicitly, as upstream's manifest does (`e9eef5d`): the offline fallbacks for MFG Ada Unlock, DLSS5 DX11 Bridge and the DLSS5 Feeder (add-on and full stack) now say `/releases?per_page=1`.
- The pinned fallback manifest moved from the 2.7.5 commit `a94a574` to the `RHI-2.8.7` tag commit `4bb5e5c`, so a broken live manifest URL falls back to the same `?per_page=1` URLs instead of the old `/latest` ones.

### #154 — OptiScaler identified by its version resource (2.8.6/2.8.7)

- A DLL counts as OptiScaler when its PE version resource says `FileDescription = OptiScaler` and `CompanyName = nitec` (both fields, as upstream `7920ae6`); a file whose resource names any other tool is not OptiScaler, whatever strings it carries (`5031d97`). RTX Encore's proxies (SilyNoMeta, they mention "OptiScaler") no longer show the OptiScaler badge, and installing over one gives the foreign-DLL refusal instead of "OptiScaler occupies dxgi.dll". Checked on real builds: OptiScaler 0.9.4 and F5 DLSSNR 0.1.30 = OptiScaler, RTX Encore 1.0.0-rc.2 = not.
- Only a file without a version resource is read for strings, and only for OptiScaler's own (`optiscaler/OptiScaler`, `OptiScaler_DontLoad`), never the bare name. Upstream also matches `optiscaler.dll` case-sensitively; the fork's string search ignores ASCII case, which would turn it into the common `OptiScaler.dll`, so it is left out.
- A file with OptiScaler's version resource is never read as ReShade: OptiScaler's own DLL contains "ReShade", which used to send an external OptiScaler down the "ReShade is already present" refusal and blocked ReShade (`ReShade64.dll`) beside an RHI-managed OptiScaler with a real OptiScaler build.
- Protection is unchanged: a DLL that is neither is still refused as foreign by every install path.

### #155 — Manifest DLSS/Streamline folder overrides (2.8.6)

- Manifest `dlssPathOverrides` and `streamlinePathOverrides` (game → folder relative to the game root) are read, as upstream `881a98c`/`cac10d7`. Example: The Sinking City 2 keeps DLSS and Streamline 9 folders down under `Engine/Plugins/…/Win64`, past the 8-level walk, so its DLSS section was empty.
- When the DLSS folder exists, only that folder is read: no walk, as upstream's exact-path detection. Streamline is read from the Streamline folder, or from the DLSS folder when no Streamline override is given; `nvngx_dlssg.dll` is also looked for in the Streamline folder (`7d500b0`). File names match ignoring case. A missing folder falls back to the normal walk, and `dlssSkipGames` still wins.
- The paths are kept relative on the game and resolved at detection time. A path that is absolute or leaves the game root (`..`, or a symlink pointing out, checked with symlinks resolved) is ignored. Files other components own are dropped as usual, so swap/restore ownership rules are unchanged.
- Every DLSS flow (panel, Quick Apply, Batch Deploy, Restore All) goes through these targets, and so does ShortFuse's "no stacking over direct swaps" check, which now also sees `.original` files in the manifest folder. Other file-index consumers (component badges) are unchanged, as upstream.

### Rolling back to a build older than wave 3 (F5 DLSSNR, nightly pins)

**Before going back to an older RHI Linux build, remove OptiScaler F5 DLSSNR from every game that has it and set every
pinned OptiScaler Nightly game back to "Latest (auto-update)".** An older build does not know either:

- it reads an F5 DLSSNR install as OptiScaler **Stable** (unknown channel names fall back to Stable), so its update check
  and Update All would replace the F5 build with Stable, and it does not count F5 as the `nvngx_dlssnr.dll` owner;
- it ignores the per-game nightly pin, so the next update moves a pinned game to the newest nightly.

Removing F5 and unpinning first leaves nothing the older build would misread; reinstall them after coming back to this
build.

### #156 — OptiScaler Nightly: pin a game to a nightly build (2.8.6)

- The OptiScaler ⚙ dialog has a **Nightly build** row, shown only for the Nightly channel: "Latest (auto-update)" plus the 30 newest builds, shown as dates (`2026-10-04`), as upstream `2975485`. A pinned game installs exactly that build; Latest keeps following the newest build.
- The choice is saved per game, in the same file as the Neural Rendering version pins, so it follows the game through "Repair after move". The build is downloaded when OptiScaler is installed (asked by its tag `nightly-YYYYMMDD`, so builds older than the listed 30 still install), cached per tag behind its cache stamp, and recorded in the deployment record (`OptiScaler.NightlyBuild`).
- While OptiScaler Nightly is installed the row stays live and an **Apply** button changes the installed build in place (wave 11, #232; it was locked until removal before). If the list cannot be loaded, the row offers Latest and the saved build and says so.
- A pinned game stays on its build: the update check reports "pinned and current" without asking GitHub (never "could not check", never an update); Update All (manual and background) counts it current; a direct update leaves it unchanged.
- Service API for Control UE (#136): `OptiScalerDeploymentService.InstallPinnedNightlyAsync(game, "20261004")`, or `LinuxManagedComponentService.InstallOptiScalerAsync(game, OptiScalerVariant.Nightly, "20261004")`.
- Not ported: upstream's pre-staging when a build is picked (Linux downloads at install, so browsing the list downloads nothing).

### #157 — OptiScaler F5 DLSSNR variant (2.8.7)

- New channel **F5 DLSSNR** in the OptiScaler ⚙ dialog, beside Stable, Nightly and DLSS NR (upstream `d4aac04`). It installs janblade's multi-pass NR fork from `janblade/OptiScaler-F5-DLSSNR-Multipass` `/releases/latest`, using only the `OptiScaler-DLSSNR-F5-*.zip` asset; the release's "Optional-Vendor-Neutral-DLSS5-Forwarder.zip" companion is never taken (`8dcf55c`, `4f28876`). Downloads go through their own GitHub-only host policy.
- The OptiScaler ⚙ channel row names every channel in its label ("Channel: Stable, Nightly, DLSS NR or F5 DLSSNR", #174), each entry has a 2 s tooltip saying what it is, and a line under the list describes the chosen one.
- F5 is treated exactly like DLSS NR. It deploys its bundled forwarder `nvngx.dll_dlssnr.dll` plus the manifest's `nvngx_dlssnr.dll` (the update check also compares that NR version), gets the Nightly-style frame-generation rows and the Streamline stack, and owns `nvngx_dlssnr.dll` under the one-owner rule below. Like every OptiScaler channel it auto-updates and is logged in the update history.
- The F5 zip is made on Windows and names entries with backslashes (`OptiScaler\libxess.dll`); 7-Zip and bsdtar on Linux extract those as flat file names. RHI moves them to the folders the names mean, as Windows extracts them. A name with `..`, an empty part, or one that collides with another entry refuses the archive (checked on the real 0.1.30 zip: 70 entries).
- Package files copied into the game now follow upstream's install step: top-level `.bat .sh .ps1 .txt .md .exe .pdb`, `LICENSE`, `!!…`, and the `docs/` and `redist/` folders stay out of the game folder; DLLs, INIs, `OptiScaler/` and `D3D12_Optiscaler/` are copied. This applies to every channel (Stable used to copy its `!! README … .txt`; F5 would otherwise copy its docs).
- Not ported: upstream's three `OptiScaler_f5.*.ini` templates (Linux deploys the package's own `OptiScaler.ini`) and the info/version links to the fork (PAR-D97).
- Since #231 (w11): before an F5 install RHI checks the NVIDIA driver and the NR DLL F5 will load, and asks to confirm
  below Linux driver 615.71.09 or with a 310.8.0.x NR DLL on a 615+ driver (never a refusal); the package must also hold a
  real Windows-DLL forwarder and its `OptiScaler/` folder. Upstream RankFTW 2.8.7 does not check either.

**`nvngx_dlssnr.dll` sharing rule** (`NeuralRenderingRuntimeOwnership`; the NR Cost Scaler #96 and RTX Encore #158 adopt it):
1. One owner per game: ShortFuse, DLSS5 Feeder (any record), OptiScaler DLSS NR or OptiScaler F5 DLSSNR. OptiScaler Stable/Nightly are no owners.
2. Installing an owner while another is recorded is refused before any download or change, naming the owner ("nvngx_dlssnr.dll already belongs to another RHI component for this game; remove it first: DLSS5 Feeder"). There is no co-ownership.
3. Only the owner's record (path + hash) governs updating, NR-version swaps and removal of the file.
4. Layers are not owners. A wrapper such as the Cost Scaler needs an owner; it refuses when the owner's file no longer matches the owner's hash, keeps the moved original (`nvngx_dlssnr_real.dll`) under its own record, and must be removed before the owner updates, swaps or removes the runtime. Removing the layer restores the owner's file byte for byte.

### #159 — RE Framework: delete the _storage_ cache folder (2.8.6)

- Ported: the RE Framework row has a ⚙ while RE Framework is installed (upstream enables its cog only then); it opens a dialog with "Delete _storage_ folder" and a line with the folder's path, or "No _storage_ folder in the game folder." with the button off. The path is one line with a middle ellipsis and the full path in a 2 s tooltip. No confirmation step, as upstream (the folder is REF's cache, rebuilt at every launch).
- Linux differences: the folder is looked for beside the game executable first (where REF's dinput8.dll lives), then in the game root where upstream looks. A `_storage_` that is a link, or whose real place is outside the game folder (an executable folder reached through a link), is refused and left as it is; a link inside the folder is removed as a link, its target untouched. The delete is refused while the game's Wine prefix runs, goes through the patch-target guard like every other change, and runs under the busy gate. Once started it runs to the end; anything it could not delete is listed in rhi.log and the status line says the folder was only partly deleted.

### #163 — Settings: paste, check and clear a GitHub token (2.8.6)

- Settings → **Accounts & updates** → GitHub token: paste a token, **Check and save**. As upstream `128bc14` (validate before apply), RHI asks GitHub once (`GET https://api.github.com/rate_limit` under the GitHub API host policy) and saves only when GitHub accepts the token; a refused (401), malformed (blank, over 512 characters, whitespace inside), unreachable or rate-limited check saves nothing and keeps the token already saved. The status line shows the hourly limit and what is left of it, never the token; the box masks it and is emptied once saved.
- Saved to the same `~/.config/rhi-linux/github_api.txt` the app already reads (#36/#133), atomically at mode 0600 (the temporary file is created 0600, so the token is never readable by other users, even briefly); a symlinked token file stays a link and the file it points at is replaced. **Check saved token** re-checks the saved file; **Clear token** deletes the file (a symlink is removed, not the file it points at) and the app goes back to anonymous access at once (the handler sees the file gone).
- The check runs on its own HTTP client, outside the session's ETag cache (a cached answer must never vouch for another token) and outside the token handler (it sends the candidate, not the saved token). Upstream's classic-PAT advice is kept in the note ("no scopes"); any GitHub token format is accepted. The device-flow sign-in stays parked (#134).
- The token file rule (what counts as a token) is now one rule for the handler and the Settings field (`SecretFileStore.IsWellFormed`), the 0600 store the Nexus Mods key uses too (#99).

### #162 — Desktop / applications-menu shortcut that starts a game through RHI (2.8.6)

- Right-click a library card → **Create shortcut** → **On the desktop** or **In the applications menu**. Each place is written only on its own explicit choice (owner decision); nothing is ever created automatically. Upstream writes a `.lnk` running `RHI.exe --launch "<game name>"`; Linux writes a freedesktop desktop entry running `<RHI> --launch <GameKey>`. The menu item is one line in the card menu (#161, vmsplit): `GameShortcutMenu.Create(vm, row.Game)`.
- The key is the library key (`steam:<AppID>`, `heroic:<app>`, else `folder:<source>|<path>`), not the name: it survives moving a Steam/Heroic game to another library or drive (#147); a key of a card merged into another still finds it. A folder-keyed game that is moved gets a new key: its old shortcut then says "no longer in the library; create the shortcut again" instead of starting something else.
- Starting: a running RHI receives the request through the single-instance hand-off (below) and starts the game through its normal Play path — the game is selected, its analysis awaited and Play run, so the per-game HDR launch profile applies exactly as with the button (a refused HDR profile refuses, as Play does). With no RHI running, the shortcut starts RHI, which does the same once its library has loaded. Every outcome goes to the status line.
- Exec: the AppImage (`$APPIMAGE`) when RHI runs from one, else this process's executable (the extracted AppImage tree's `usr/bin/RHI`, `install-user.sh`'s install); every argument quoted per the Desktop Entry Specification (`"`, `` ` ``, `$` and `\` take the Exec rule's backslash, written doubled because the string unescape runs first — `\\$` in the file, a backslash as four — and `%` doubled) — the same rule as `install-user.sh`; a single backslash before `"`/`` ` ``/`$` is not a valid string escape and GLib refuses the whole Exec (KDE kept it). Checked by a GLib-strict spec reader, the host's real GLib (`g_key_file_get_string` + `g_shell_parse_argv`) and `desktop-file-validate`. Keys with control characters are refused.
- Files: `rhi-linux-game-<name>-<8 hex of the key>.desktop` with an `X-RHI-Linux-GameKey` line; creating it again rewrites RHI's own file, and a same-named file without that line is left alone. Desktop folder from `xdg-user-dir DESKTOP` (else `user-dirs.dirs`, else `~/Desktop`); when it names the home folder itself there is no desktop and RHI says so. The desktop copy is mode 0755 (KDE/GNOME only offer to run executable launchers; GNOME may still ask once to "Allow launching"), the menu copy 0644 in `$XDG_DATA_HOME/applications`. Atomic writes (`AtomicFile`).
- Icon: the game's Steam library art when Steam has it (`appcache/librarycache/<AppID>/<sha1>.jpg`, the older `<AppID>_icon.jpg`, else the header), copied into `$XDG_DATA_HOME/rhi-linux/shortcut-icons/` so it outlives Steam's cache; otherwise RHI's own icon (`rhi-linux`). Since #202 the game exe's own icon comes first (see #202 below).
- Single-instance hand-off (shared with nxm:// links, #99): a second RHI started with `--launch`/an nxm link passes it to the running one over a Unix socket in `$XDG_RUNTIME_DIR/rhi-linux` (refused unless the folder is the user's own, mode 0700, not a link; the sender's uid is checked with `SO_PEERCRED`; one JSON line, 8 KiB cap, 3 s timeouts; a stale socket from a crash is replaced) and exits without a window. What arrives is untrusted: the key is only looked up in the library. A plain second start is unchanged — several RHI windows may still run, as before (upstream instead brings the running window forward; question to the owner).

### #161 — Library card right-click menu (PAR-D74a; upstream `6cc2b2d`, `27c66b7`, `f63579f`, `f38fb34`)
- Ported: right-click (or the menu key / Shift+F10) on a library card selects it and opens Open folder, Favourite/Unfavourite, Hide/Show and, when the game has a note, View notes — the same commands as the detail header, no new behaviour. Every entry is listed only where it applies to the card (owner rule: inapplicable options are hidden, not disabled): Open folder only once the game folder is confirmed (not for a folder-missing or not-allowed entry, nor a card still waiting for its target in Edit game), View notes only with a note.
- View notes shows the upstream manifest's `gameNotes` entry for the game (upstream's card notes come from the same entry, plus the RenoDX wiki note, which the fork does not keep per card), with its link button when the entry has one.
- Not ported here: "Discussion / Instructions" (upstream `NameUrl`; no fork data) and "Create Desktop Shortcut" (#162, parity lane: one entry in the menu's named slot in `MainWindowViewModel.CardMenuFor`).

### #160 — Control UE dialog: full list of required in-game settings (2.8.6)

- Ported (`ae5603c`): the Control Ultimate Edition RenoDX confirmation lists the required in-game settings under its own heading: DLSS on, SSAO on, post-processing High, screen-space reflections High (turn ray tracing off briefly to change it, or pick the High/Ultra render preset), RT reflections on, RT indirect diffuse on, render resolution in even numbers (can be set in `renderer.ini`). Upstream's 2.8.1 DLSS/RT/SSAO line stays. The text comes from `ControlUeInstallDialogModel` (EN + PT-BR), which a dialog template can bind; the shell shows its message in the existing confirmation, which scrolls when the window is small.
- Linux wording: the closing note says removal restores the previous DLLs and `renderer.ini` except what the user changed after the install (#124); upstream says its extra changes are not reverted. Upstream's OptiScaler FG and "Using HDR?" lines arrive with #136.

## Parity notes (feature-parity backlog)

### #126 — Available HDR mods: RHI Unity database games (2.7.9 parity)

- The Available HDR mods list now also lists the games in the RHI Unity database (`RenoDXdb-unity.json`, the same file the RenoDX installer reads, #15) in the RenoDX column, as upstream's `GetAllHdrMods` adds `_dbUnityEntries`. They show ✓, or 🔨 when their `Status` is `WIP`. A named RenoDX mod for the same game (joined by the normalised name, e.g. `Hades™ II` = `Hades II`) keeps its own row, status and link; Luma merges into a Unity row like any other.
- Unity rows have no RenoDX link, as upstream (`RenoDXUrl: null`; the generic Unity add-on is installed by RHI, not downloaded from this list); their Download button offers the Luma link when there is one. The ticket text says "linked to the Unity add-on snapshot, as upstream does", but upstream gives no link, so upstream's behaviour was kept.
- A database that cannot be loaded leaves those rows out (reason in the log); the list's sources are now each logged when they fail instead of being skipped silently. Upstream's UE-Extended database rows and Nexus/Discord-only RenoDX mods came with #197.

### #90 — Xbox / Game Pass games in the library
- Upstream (`GameDetectionService.Xbox.cs`) finds Xbox installs through Windows' package manager, `.GamingRoot` files and the registry; none exist on Linux, which has no official client. The fork reads Xodus-format package folders instead (owner, 2026-10-14): `.xodus-streaming.msixvc` or an Appx record plus `MicrosoftGame.config`/`appxmanifest.xml`, under `~/Games` and a configurable list of Xbox game folders (Settings). [Xodus](https://github.com/xodus-gaming/xodus) is a Linux Game Pass client still in development; RHI is ready to list the games it installs but does not rely on it working today. Upstream's `MicrosoftGame.config` test (GDK) is kept, case-insensitively.
- Cards are listed straight away; unfinished downloads are hidden. Play hands the game to the app that installed it (argument vector, no shell: that app's normal launch path with licensing, saves and cloud); the card menu can also show the game in that app. When RHI finds no way to start the owning app, Play is disabled with a 2 s tooltip. RHI never runs the game's executable or `xodus-cli run`.
- MSIXVC exes are encrypted on disk, so RHI cannot read their bitness: the card claims none and installs refuse with the plain reason until #167 reads it from the package (`ProcessorArchitecture`). One write gate (`XboxWriteGate`) refuses any change while the owning app is still downloading or updating the title, or when RHI cannot tell whether it is (every entry point: components, DLSS/Streamline swaps, DLSS defaults, ReShade settings, Repair after move), and the shared write primitives never write the encrypted exes, the msixvc, the package config/manifest, the owning app's markers or the DXVK/VKD3D/NVAPI DLLs it places in a title prefix.
- Cards carry the name of the installed product (#175): the payload folder's product record, then the catalogue index, then the newest job for that product, before the package names; a product name that only adds "Standard Edition" keeps the package name (mod lists match plain names). Control Ultimate Edition installs the shared "Control Standard Edition" package, and its card (and the Control UE features keyed on that name) now reads Control Ultimate Edition.
- On a system without Xodus or another Xbox installer app, Xbox detection is silent (no cards, scan warnings, log lines or menu items); the Settings tab **Xbox / Game Pass folders** still shows, with a one-line Xodus note and link.

### #90 — EA app, Ubisoft Connect, Battle.net, Rockstar and itch.io libraries
- Upstream detects EA app, Ubisoft Connect, Battle.net and Rockstar games on Windows from their registry keys and files (itch.io appears only in its README). The fork reads the launchers' records inside each Wine prefix, read-only: Ubisoft `Launcher\Installs` keys, Battle.net `product.db` plus its uninstall `--uid=`, EA `installerdata.xml` and Origin/publisher keys, Rockstar uninstall and `Rockstar Games\<title>` keys, itch's `butler.db` (with its write-ahead log) and `.itch/receipt.json.gz`. Windows paths are mapped through the prefix's drive links; folders go through the root-folder guard.
- Linux difference: upstream's Play runs the detected game exe. The fork starts the store client with the game's own start argument (`uplay://`, `--exec="launch …"`, `origin2://`, `-launchTitleInFolder`, `itch://games/<id>`), as Lutris's store services do; HDR profiles cannot be applied through it and the notice says so. Standalone Wine prefixes use the launcher exe in that prefix; Lutris prefixes the store's own client config, else the parent launcher; Heroic only the store's own sideloaded client, else no command (Edit game).
- Not in upstream: games of the itch.io app for Linux (native or Flatpak) are listed from its `butler.db`; Play opens the game in the itch app.


### #100 — App update check (notify only)

- Upstream's `UpdateService` checks RankFTW/RHI's `/releases/latest` (then legacy tags, beta for opt-ins) at start-up and offers to download and run its Windows installer. RHI-Linux checks **its own** releases instead and only tells the user (owner decision): nothing is downloaded, swapped or installed; the owner installs builds himself.
- Repository: `nonspacial/RHI-Linux-Nonspaced` only (owner decision 2026-10-27: releases are published as they happen; no other repository is asked, also when this one publishes nothing or does not exist — the check then reports that there is no published release). RHI reads `releases?per_page=20` rather than `/releases/latest` (GitHub's "latest" never names a pre-release) under the GitHub API host policy (ETag cache and token apply), skips drafts, and counts pre-releases only while the running build is itself a pre-release (upstream offers betas only to beta opt-ins). Builds before 2.8.7 reported `0.9.23.4-beta.1`; the first release, `v2.8.7` (level with upstream RHI 2.8.7), carries no pre-release label, so a 2.8.7 build counts only releases not marked as pre-releases. Versions compare as SemVer over up to four numbers (`0.9.23.4-beta.1` < `-beta.2` < `-beta.10` < `0.9.23.4` < `0.9.24-beta.1` < `2.8.7` < `2.8.7.1` < `2.8.8-beta.1` < `2.8.8`).
- Start-up: after the library has loaded, one check (Settings → Accounts & updates → "Look for a newer version at start-up", on by default as upstream); a newer release adds one line to the status bar ("RHI-Linux X is available (this is Y) — see Settings → Accounts & updates"); up to date, offline or no releases stay quiet. The Settings section shows the result of the last check, **Check now**, and **Open the release page** (only when a newer one was found; it opens the release's github.com page in the browser — a page link GitHub returns off github.com/<repo>/releases/ is replaced by the tag's page).
- The start-up choice lives in `~/.config/rhi-linux/accounts.json` (not `settings.json`, which the Settings dialog rewrites from its controls on Save).

### #99 — Nexus Mods: account (API key), nxm:// links, game pages

- Upstream ships its Nexus integration behind a feature flag the live manifest keeps off (`featureFlags.nexusMods: false`, dev-unlock only); RHI-Linux ships it (owner decision: build it).
- **Account** (Settings → Accounts & updates → Nexus Mods account; owner decision: API key only): one obvious **Connect Nexus account** button opens Nexus's API-key page (`https://www.nexusmods.com/users/myaccount?tab=api`) in the browser and shows the paste box (when the browser cannot be opened, the box still shows and the line names the page); **Check and save** sends `GET https://api.nexusmods.com/v1/users/validate.json` with `apikey`, `Application-Name: RHI-Linux`, `Application-Version` and saves only when Nexus knows the key. The key is stored in `~/.config/rhi-linux/nexus_api.txt` through the 0600 secret-file store (#163); the account name and Premium/Free tier in `accounts.json`; **Disconnect** deletes both. API calls use their own HTTP client that never follows a redirect (`apikey` is a custom header .NET would carry to the next host) and read at most 16 MiB per answer, announced or not.
- **SSO (upstream's "log in with Nexus" window) is dormant**, waiting on #171: it needs Nexus to register RHI-Linux as an application (Nexus issues each application its own slug; registration is a finalisation task, possibly done by the upstream fork). Upstream's slug `rhi` is upstream's identity and is never borrowed. The SSO client (`NexusSsoClient`: `wss://sso.nexusmods.com`, handshake `{id, token: null, protocol: 2}`, browser page `https://www.nexusmods.com/sso?id=…&application=<slug>`, key over the socket, 30 s pings, 5 min limit, cancellable) and its window stay in the code, unreachable: `NexusApplication.RegisteredSsoSlug` is null, there is no override, and no control opens the window. #171 sets the constant and adds the button back.
- **nxm:// links** ("Mod Manager Download"): **Open these links in RHI** writes a hidden `~/.local/share/applications/rhi-linux-nxm.desktop` (`MimeType=x-scheme-handler/nxm;`, `Exec="<this RHI>" "--nxm" %u`) and makes it the default with `xdg-mime default`; when another program (Vortex, MO2…) is the default RHI asks first, as upstream does; **Stop opening them in RHI** deletes RHI's entry only (marked with `X-RHI-Linux-Nxm=true`; a same-named file RHI did not write is never overwritten or deleted). The main menu entry (`packaging/rhi-linux.desktop`, `install-user.sh`, the AppImage) now also declares `MimeType=x-scheme-handler/nxm;` with `%u`, so a desktop with no other handler may pick RHI by itself — harmless, because every link is confirmed in RHI.
- A link is a trust boundary: parsed strictly (`nxm://<game>/mods/<n>/files/<n>`, lower-case game domain, no user info/port/fragment, only `key`/`expires`/`user_id` in their own character sets, key needs expiry, 2 KiB cap), passed to a running RHI through the single-instance hand-off (#162), and **always confirmed**: the dialog names the file (from the mod's file list), the mod, its game and the library game whose Nexus page matches, with the mod page as a link. Only then RHI asks Nexus for the download link (the link's key + expiry; Premium accounts also without), downloads it through `TrustedDownload` under the Nexus file policy (one label under `nexus-cdn.com`, 1 GiB cap; the API key goes to api.nexusmods.com only) into `~/.cache/rhi-linux/nexus/<game>-<mod>-<file>/`, records its SHA-256 in rhi.log, and offers what it can install through the prompts a dropped file gets: a ReShade add-on → import into Custom Add-ons; a Luma package → install into the matching (then selected) game; add-ons inside a zip are taken out by name only (no folders), capped and CRC-checked. Upstream instead copies the first `renodx-*.addon64` straight into the matched game as its RenoDX mod; on Linux nothing reaches a game folder without the existing confirmed install path. Anything else (7z/rar, no add-on) stays in the cache folder and the status line says where.
- **Game pages** (#174): right-click a card → **Nexus Mods page**, next to Create shortcut (#162), for every game Nexus has a page for; no account is needed to open it. Found as upstream `NexusModsService`: the manifest's `nexusUrlOverrides` first, then Nexus's public game list (`https://data.nexusmods.com/file/nexus-data/games.json`, ~2 MB, cached 24 h in `~/.cache/rhi-linux/nexus_games.json`, last good copy kept, own data.nexusmods.com host policy) by upstream's normalised name. Linux addition: a built-in table for store names Nexus spells differently (Steam's "The Elder Scrolls V: Skyrim Special Edition" is Nexus's "Skyrim Special Edition"; by Steam app ID or name, e.g. Skyrim/Oblivion/Morrowind/Fallout 3 GOTY/Metro Exodus Enhanced/Dark Souls II–III/RE2–RE3 remakes), checked before the list, and a store name's trailing "(year)" is tried without it. Until the list has loaded (it starts at start-up, never blocks the menu) only the manifest and the table answer. Only https pages on nexusmods.com one path segment deep are used.
- Not ported: Nexus mod update tracking (`NexusUpdateService`: GraphQL `legacyModsByDomain` baselines) and the Premium one-click "install latest MAIN file" from a card — both act on per-mod Nexus URLs (RenoDX wiki `nexusUrl`), which the fork's RenoDX catalogue does not read; the manifest's overrides are game pages. Questions for the owner.

### #94 — Luma mods built in a release but not on the wiki yet

- As upstream 2.7.6 (`748ab76`): the Luma catalogue also reads Filoppi's release list (`api.github.com/repos/Filoppi/Luma-Framework/releases?per_page=5`, GitHub API host policy) and takes the newest release's `Luma-<Game_Name>[-x32|-x64].zip` assets (`-Test`/`-Dev` builds skipped, `_` → space, `.` → apostrophe, the generic/developer packages excluded, one per name). Merge, wiki wins: a wiki row keeps its link; a wiki row without a link takes the asset's; a mod only in the release is added (no author, status ✅). Names match through the catalogue's own normalisation, as every other Luma lookup.
- A release-only mod installs like a wiki one: its link is a Filoppi release asset, so the install resolves it through the release's tag (published digest checked) under the Filoppi namespace rule. Upstream's matching manifest install warnings ("work in progress") arrive through the live manifest (#131).
- The release list is an enrichment: when it cannot be read the wiki alone is used (logged); the wiki is still required, as before.

### #93 — Named shader-selection profiles with zip export

- Shaders/Add-ons dialog → Shader packs tab, new **Shader profiles** panel at the top (upstream's shader-picker profile panel): pick a profile → **Load** (ticks its packs and restores each pack's file choices from **Files**; nothing reaches the game until the dialog's Apply) / **Delete**; type a name → **Save profile** (an existing name is updated in its saved spelling; blank updates the picked profile or creates "Profile N") / **Rename** the picked one; **Export zip**; **Import zip**. Buttons that do not apply are hidden.
- Profiles are global, as upstream's (`~/.config/rhi-linux/shader-profiles.json`, upstream's JSON shape `Name` / `SelectedPacks` / `FileExclusions`; unreadable file copied aside and never saved over). File choices are per pack and global in both apps, so loading a profile sets them for those packs. A pack the catalogue no longer has is skipped with a count in the status line.
- Export: upstream's zip layout (`Shaders/<pack>/…`, `Textures/<pack>/…`, `shader_import.json`) with each pack's files as a sync would deploy them (the user's and the global exclusions left out), written with `AtomicFile` into `~/.local/share/rhi-linux/Shader profiles/` (never into a game) as `RHI_Shaders_<profile>_<time>.zip`; the folder then opens. Upstream copies the zip to the clipboard to paste into Discord; on Linux "share" = the opened folder. Upstream also adds the `ReShade.fxh`/`ReShadeUI.fxh` headers; RHI-Linux deploys them itself with every pack (#239), so they are not in the zip.
- Import reads only `shader_import.json` (a profile named like an existing one becomes "Name (2)"). Upstream also unpacks the zip's shader files into its cache for packs it has not downloaded; RHI-Linux does not: packs come from their catalogue sources with their recorded hashes when applied, never from files someone sent (the cache-stamp rule). A zip without a profile is refused.
- The panel's packs and files come through the one shader-pack service, now constructed once in `AppComposition` and shared with ReShade's deployment (same cache folder as before).

### #96 — NR Cost Scaler

- Upstream (`DlssNrCostScalerService`, xenmods/DLSSNR-Cost-Scaler): a proxy `nvngx_dlssnr.dll` that runs Neural Rendering at a reduced scale (75 % by default, `nvngx_dlssnr.ini`) over the real runtime moved to `nvngx_dlssnr_real.dll`, plus `dlssnr-companion.addon64`. Upstream deploys it right after an NR method install when the per-game toggle is on, and removes it before that method uninstalls.
- Linux: Neural Rendering versions box → **NR Cost Scaler** row (shown only when the game has a Neural Rendering method RHI installed, or the scaler): **Install** / **Remove**, through the operation gate and the update history like every component. It follows sync's one-owner `nvngx_dlssnr.dll` rule (#157): a **layer, never an owner** — it needs an owner (ShortFuse, DLSS5 Feeder, OptiScaler DLSS NR; F5 DLSSNR joins with sync's rule), refuses when the owner's file no longer matches the owner's record, moves that file to `_real` unchanged and records it under its own record (`ManagedComponentKind.NrCostScaler`), and while it is installed the owner's install/reinstall, update, NR DLL swap and removal are refused ("Remove NR Cost Scaler first…"). Removal puts the owner's file back byte for byte (so the owner's hash check passes again) and removes the INI even if the user changed the scale; a proxy or add-on that changed since install is left alone and the removal refused.
- Download: the latest release under its own GitHub host policy, the zip checked against GitHub's published digest and cached through `CacheStamp`; the three files extracted capped and CRC-checked into a fresh staging folder, all before the write gate. Install and removal are journaled (every file tracked, writes declared, `BeginWrites` before the first change) and roll back as a whole (tested with a failed record save after the files moved and Cancel pressed).
- Library moves: "Repair after move" is layer-aware — while a scaler record lists the owner's `nvngx_dlssnr.dll`, the owner's file is checked under `nvngx_dlssnr_real.dll` (`NrCostScalerService.ContentPath`), so both records are rebased and the scaler can still be removed afterwards. Removal commits in one ledger write (scaler record out, owner record stamped with the removal), so a crash never leaves files without a record. Update All: an owner's update found while the scaler is installed is reported as "held by NR Cost Scaler", not attempted and not a failure.
- Deviation, safe and reversible: the scaler is installed on its own button after the method instead of "toggle first, then install the method" (the layer rule makes the order explicit and needs no change in each owner's install path). Since #199 Update All and the background pass update it (see #199 below).

### #158 — RTX Encore (Beta) (2.8.6)

- As upstream: SilyNoMeta's RTX Encore from its newest non-draft GitHub release (every release so far is a prerelease), installed under a DLL name chosen per game (`version.dll` by default), optionally with NVIDIA's Neural Rendering file; Check/Update in its ⚙ dialog and in Update All, update history.
- Checks: the zip against GitHub's digest and the release's SHA256SUMS, the DLL by RTX Encore's own version resource; the Neural Rendering file is NVIDIA's exact 310.8.0 (pinned SHA-256) from the DLSS list. A file RTX Encore's resource identifies is never read as ReShade or OptiScaler.
- Linux adaptation: install, update and removal are journaled with a per-game Wine `native,builtin` override; a foreign file under the chosen name is kept as `<name>.original` and put back. Names the game's API cannot load, Bink names and names another RHI component holds are not offered; `rtx-encore.asi` only beside an RHI ASI Loader. With its Neural Rendering file RTX Encore owns `nvngx_dlssnr.dll` under the one-owner rule (ShortFuse, DLSS5 Feeder and OptiScaler DLSS NR/F5 refuse; the NR Cost Scaler refuses and hides), and the DLSS panel, Quick Apply, batch and Restore leave its files alone.
- The row is shown only where it can apply (a 64-bit DirectX 11/12 or Vulkan game that ships its own DLSS, with a confirmed patch target); an RTX Encore RHI installed stays listed. Every note says it is not yet confirmed on Linux: frame generation and Neural Rendering may work through Proton's DLSS, Smooth Motion cannot (it needs the Windows driver).
- Rollback: a build older than wave 4 forgets RTX Encore's saved name and Neural Rendering choice (and the saved OptiScaler DLL name) for games where it is not installed the first time it saves a ReShade DLL name; it cannot update or remove RTX Encore and drops RTX Encore's install record the first time it saves any record, after which even this build cannot remove it (the `.original` and Wine override are left behind). Remove RTX Encore and switch No Addons games back to Stable or Nightly before rolling back.

### #91 — ReShade "No Addons" channel

- Ported (upstream `NormalReShadeUpdateService`, per-game channel "No Addons"): the ReShade profile's channel list has **No Addons**, the newest reshade.me build without add-on support (`/downloads/ReShade_Setup_<v>.exe`, the link without `_Addon`). It is staged, stamped and cached apart from the add-on build (`reshade/noaddons/<v>`), so an outage fallback or a cache reuse never mixes the two; Check Update and Update All compare it with the No Addons link on reshade.me. The setup is extracted like the add-on setup and accepted on the same reshade.me host policy (reshade.me publishes no digest; RHI records the SHA-256).
- As upstream, ReShade add-ons are not offered for a No Addons game: RenoDX, ReLimiter, Display Commander, DOF Fix, Luma, ShortFuse, the DLSS5/MFG add-ons and the Addon Manager selection are refused with a plain reason and their install buttons are disabled with that note (upstream greys them out).
- A game can switch to No Addons with add-ons still installed (as upstream). Linux difference (owner decision #170): the switch turns them **off** — each add-on file RHI installed (component add-ons and the Addon Manager's) is renamed in place to `<name>.rhi-off`, kept byte for byte, never loaded or deleted; the add-on records name the renamed files, saved in the same ledger write as ReShade's record and journaled with ReShade's install (a failure or crash puts them back). Switching back to a channel with add-on support turns exactly those back on. The status line says how many were turned off or on; a file already holding the other name refuses the switch before anything changes. While off, an add-on's row says so, it is not updated or reinstalled (Update All lists it as held by ReShade No Addons), and Remove still deletes it (both ShortFuse Remove buttons). A version-pin swap (Feeder, DX11 Bridge, ShortFuse) and ShortFuse's ZZZ load-order toggle are refused while it is off, with the same plain reason, so nothing puts a loadable copy back. If the Addon Manager's saved list cannot be read during a switch to or from No Addons, its add-ons are left as they are and the status line says so. Upstream leaves them in place to stop loading silently. The channel can also be the Settings default, like the other channels (`LinuxAppSettings.Normalize` keeps it).

### #172 — OptiScaler DLL name (upstream DLL-name dropdowns)

- OptiScaler's ⚙ settings have a **DLL name** dropdown, like ReShade's (#120): **Automatic (dxgi.dll)** by default, or any of upstream's `SupportedDllNames` (dxgi, winmm, d3d11, d3d12, dbghelp, version, wininet, winhttp). Names a file of another RHI component already uses on the game are left out (RHI's own ReShade is not: it moves aside to `ReShade64.dll` for OptiScaler as before), and a saved name that became taken is skipped for the automatic name, which the install result says in a plain sentence naming the component that holds it. Automatic stands for dxgi.dll, or for the first free name while another RHI component holds dxgi.dll (the dropdown shows that name). Saved per game in `dll-names.json` beside ReShade's choice; the install takes it and records it, update and removal follow the record; while OptiScaler is installed a choice renames it in place (#196). Control UE's frame generation still installs it as `winmm.dll` (#136).

### #120 — Per-game proxy DLL name for ReShade (upstream DLL-name dropdowns, 2.7.6)

- Ported for ReShade (phase A): the ReShade profile has a **DLL name** dropdown with upstream's list (`dxgi`, `d3d9`, `d3d10`, `d3d11`, `d3d12`, `opengl32`, `dinput8`, `version`, `winmm`, `ddraw`, `xinput1_3`, `msvcp140`, `bink2w64`, `ReShade64`, `ReShade32` `.dll`; the other bitness's ReShade name and `bink2w64.dll` are hidden for 32-bit games). **Automatic** is the default and shows the name it stands for. The choice is per game (`~/.config/rhi-linux/dll-names.json`, follows the game through Repair after move) and only offered names are ever stored or used.
- Install uses the name, records it with the payload's hash and gives it its own Wine `native,builtin` override (none for `ReShade64/32.dll`, which only another mod such as OptiScaler loads). An existing file under that name is protected as before (refused unless it is RHI's or the OptiScaler coexistence case; a name another RHI component holds, such as RE Framework's `dinput8.dll`, is refused naming that component). Never a second ReShade: with no RHI ReShade on the game, ReShade not installed by RHI under any offered name (any spelling) refuses every install path (profile Apply, the Luma/ShortFuse dependency, a manifest name) before any download; RHI's own ReShade keeps its recorded name. Linux deviation: upstream adopts a ReShade it finds already installed under another name; RHI Linux refuses instead (the fork's rule never to take over or discard content RHI did not install, and two ReShades would load twice), says nothing changed and names the file with the two choices (remove it, or keep using it). Keep this refusal when syncing upstream. `ReShade64.dll` chosen with RHI's OptiScaler installed is the coexistence case (OptiScaler's `LoadReshade` on, off again on removal). Update keeps the recorded name; removal deletes it and restores its override; a changed payload blocks both.
- Every consumer that looked for ReShade by fixed names finds it under any name: ShortFuse's payload move, the RenoDX / add-on / Addon Manager "ReShade present" checks, the component scan (another offered name counts only with ReShade's signature and without OptiScaler's, since OptiScaler mentions ReShade), Repair after move and its guard, and Update All's fingerprint. Luma still skips the ReShade a package bundles and refuses a package file with ReShade's chosen name; the DX9/dgVoodoo2 move still needs ReShade at `d3d9.dll` and refuses otherwise, changing nothing, with a plain next step (remove ReShade, set its DLL name to d3d9.dll, install it again).
- Manifest `dllNameOverrides` (`reshade`) take effect as upstream: the next install uses the manifest's name unless the user chose one. Linux deviation: a manifest name the game's graphics API would not load is not used (upstream sets `dxgi.dll` for the DX9 titles Medal of Honor: Airborne, The Witcher 2 and Vanquish, which it runs through dgVoodoo2; RHI Linux moves ReShade to `dxgi.dll` only when Luma or the DLSS5 Feeder installs dgVoodoo2); the tooltip says so. `dc` names do not apply (Display Commander is a ReShade add-on on Linux).
- In-place rename and Reset DLL names: ported in wave 7, see #196. Not ported: the automatic rename of installed files when the manifest changes (an installed ReShade keeps its name until the user picks another).
- Phase B, OptiScaler: installable under upstream's `SupportedDllNames` (`dxgi`, `winmm`, `d3d11`, `d3d12`, `dbghelp`, `version`, `wininet`, `winhttp` `.dll`) with that name's Wine `native,builtin` override; the name is recorded (`OptiScaler.ProxyName`; older records = `dxgi.dll`) and update/removal follow it; RHI's ReShade at that name is chained as `ReShade64.dll` and moved back to it on removal; a name another RHI component holds is refused naming it. Used by Control UE frame generation (`winmm.dll`, #136). The OptiScaler DLL-name dropdown (upstream's Game Overrides "OS DLL" row) landed in wave 4, see #172.

### #97 — OptiScaler per-GPU INI templates (feature-parity backlog)

- Ported: Settings → DLSS has upstream's **GPU type** (NVIDIA / AMD / Intel) and **DLSS inputs** (Yes / No, shown for AMD and Intel only; defaults NVIDIA / Yes, as upstream). A new OptiScaler install deploys, as the game's `OptiScaler.ini`, the user's template for its channel and GPU setup from RHI's INIs folder (`~/.config/rhi-linux/inis/`), named as upstream: `OptiScaler[_nightly|_dlssnr|_f5].{nvidia|amd-dlss|amd-nodlss}.ini` (Intel uses the AMD files, as upstream). A template is created from upstream 2.8.7's own file the first time it is needed and never overwritten, so the user's edits apply to every later install; **Open INI templates folder** creates any missing ones and opens the folder (upstream seeds all twelve at start-up; RHI writes nothing until an install or that button needs it). The 12 upstream files are 7 distinct ones; RHI ships those 7 (GPL-3, as this project) and maps the names.
- Unchanged: `OptiScaler.ini` stays the user's file on the record (never overwritten by Update All), a game's own `OptiScaler.ini` is kept as `.original` and put back on removal, and LoadReshade (chain) / LoadAsiPlugins are still set on the deployed copy: `LoadAsiPlugins=true` wins over a template that sets it false, and the Settings note on templates says so (#173). A template that cannot be prepared (folder unwritable) falls back to the package's `OptiScaler.ini`, and the install line says so.
- Hotkey row: ported in wave 5 (#187) — Settings → DLSS/Streamline → **OptiScaler menu key** sets `[Menu] ShortcutKey` in every template and new install, and **Apply to all games** in every game with RHI's OptiScaler; see #187 below.

### #98 — OptiPatcher auto-deploy (feature-parity backlog)

- Ported: every OptiScaler install also puts the newest OptiPatcher (`optiscaler/OptiPatcher`, `rolling` release, its single `OptiPatcher.asi`) into `plugins/` beside the game exe, as upstream 2.8.7 (install step 7). Every install sets `[Plugins] LoadAsiPlugins=true` in `OptiScaler.ini`, as upstream, also when OptiPatcher was not deployed (unreachable, or a `plugins/OptiPatcher.asi` of the user's own, which then loads). The build is the release notes' "Build commit" (upstream's version key); the file is cached per build through the stamped cache with GitHub's published SHA-256 checked, must be a 64-bit Windows DLL, and is downloaded under its own `DownloadSources.OptiPatcher` policy (GitHub release hosts). It is part of the OptiScaler record (`OptiScaler.OptiPatcherVersion`, `Download.OptiPatcher.*`): removed with OptiScaler, journaled with it.
- OptiPatcher being unreachable never fails OptiScaler (upstream logs and skips it); the install line says it was not deployed. A `plugins/OptiPatcher.asi` RHI does not own is left as it is.
- "Auto-updates silently": upstream re-copies a newer rolling build into every game that has it. On Linux a newer build makes OptiScaler's update check report an update (`OptiPatcher update available: old → new`; the check reads the release's build and digest only, never the file, as upstream `CheckOptiPatcherUpdateAsync`), so Update All and the opt-in background component updates refresh it; an install that has no OptiPatcher (before #98) does not gain one on update (upstream only refreshes existing copies). A pinned nightly (#156, Control UE FG) stays as it is, OptiPatcher included, and is never asked about.

## Parity notes (upstream 2.8.7 residuals)

<!-- w5/update-policy notes -->

### #177 — Follow newest DLSS / Streamline (upstream `RunDlssAutoUpdateAsync`, `AutoUpdateDlss`/`AutoUpdateStreamline`)

- Upstream: after each background scan, compares the newest DLSS and Streamline version in the DLSS manifest with `LastKnownNewest*`; the first run only seeds it; when the newest changed, every game whose SR/RR/FG DLL (or Streamline interposer) is exactly the previous newest is swapped to the new newest, games on any other version (picked by hand, the game's own, 1.x) are left alone. Two Settings combos, both Off.
- Linux: Settings → DLSS/Streamline → **Follow newest DLSS (SR/RR/FG)** and **Follow newest Streamline**, both off by default. A pass runs after start-up, every Full Refresh and every background pass (the background loop now runs while component updates or either toggle is on; turning a toggle on runs one at once, which seeds). Its manifest check runs without the busy gate; only a pass that found a newer version takes the gate (a background operation, Cancel between games) and swaps files. Each swap is one automatic Updates entry ("DLSS SR (nvngx_dlss.dll) 310.4.0 → 310.5.2"; Streamline one entry per folder).
- Differences, all deliberate: one baseline per family (SR, RR, FG, Streamline) in its own file `~/.local/share/rhi-linux/dlss-follow.json`, never in settings.json — the manifest's SR/RR/FG lists do not always share their newest version (RR lacks 310.8.0), and upstream's single DLSS baseline would then ask RR for a version its list does not have. A baseline moves only after a fetch the network answered (a download or a 304): a cached copy used offline never moves it. Targets are the Quick Apply set (manifest path overrides, `dlssSkipGames`, no `OptiScaler/` folder, no ShortFuse/DLSS5 Feeder Full stack) minus every file any RHI record of the game lists; versions are read from the DLL's own version resource and compared part by part (the manifest's `310.9.16` = the DLL's `310,9,16,0`); NR stays out (one owner per game; upstream has it behind a developer flag). The newest only moves forward: a manifest whose newest is older than the baseline (a release pulled, a stale or reordered list) moves nothing (upstream follows any change). A game a pass skips — running, an Xbox title its installer app is busy with, or the newest DLL could not be downloaded — is held under its own record key: the next pass follows that game alone from the version its file was on, never another game's file that merely sits on that old version (a version picked by hand, a game added later); a skip that repeats for the same reason is reported once (the status line names why; a failed download writes one Updates entry). A swap that fails keeps the game's file, is logged once (status line, rhi.log, Updates) and the baseline still advances (upstream). Turning a toggle off forgets its baseline, so turning it on later seeds afresh instead of following from a version that was newest long ago. Every payload is downloaded before the first game file changes.

### #178 — Update inclusion, per game and for every game (upstream `ExcludeFromUpdateAll*`, `GlobalSkip*Updates`, `UpdateInclusionHelper`)

- Upstream: Settings → **Update Inclusion** (Global Update Inclusion dialog: ReShade, RenoDX, ReLimiter, Display Commander, OptiScaler, RE Framework) skips a component in Update All; per game, the overrides panel's **Update Inclusion** dialog excludes RS/RDX/RL/DC/OS (+ DXVK, REF where they apply) from Update All and the silent auto-update. A component's own Update/Install button is not affected.
- Linux: Settings → Maintenance → **Update All includes** (the same six, ticked = included, all ticked by default; settings.json `GlobalSkip*Updates`). Per game: card right-click → **Choose what Update All changes…** (#209; upstream: Update inclusion…; only where it would offer a box), one box per installed RHI component Update All takes part in (every Update All family, not only upstream's five), plus **DLSS (follow newest)** / **Streamline (follow newest)** while that Follow newest toggle is on (#177) and the game has a DLSS / Streamline file Follow newest could move (known once the game has been analysed, i.e. selected); components Settings already leaves out for every game are named in a note instead of shown. Choices live in `~/.config/rhi-linux/update-inclusion.json` under the game's record key (Repair after move carries them; names a newer build wrote are kept). Update All and the background pass neither check nor update a left-out component: the summary counts it as "left out", never "current" or "could not be checked"; a running game whose components are all left out is not "deferred". The component row says why ("Left out of Update All in this game." / "… in every game (Settings)."). Install / Remove are never affected (as upstream).
- Differences: the global skip applies to the background pass too (upstream's auto-update reads only the per-game flags); upstream's side use of the ReShade exclusion (it also lets RenoDX install over a ReShade the user manages) is not needed: the fork's add-ons already accept a ReShade RHI did not install (Luma and ShortFuse keep needing RHI's own, as before). An unreadable inclusion file leaves every component out until it reads again (it was copied aside and the notice says so): updating what the user chose to keep is worse than updating nothing. The dialog then says the file cannot be read (boxes cleared and disabled, only Close), the rows say "the update-inclusion file cannot be read", and the Update All summary counts those apart from the user's own choices.

<!-- w5/catalogue-info notes -->

### #183 — Card links: PCGamingWiki, Ultra+, ultrawide fix, HDR Gaming Database, discussion page

- Upstream shows PCGW / UW Fix / Ultra+ buttons in the detail header and "Discussion / Instructions" in the card menu. The fork puts all of them in the card's right-click menu: **Discussion / instructions** (the matched RenoDX mod's `discussionUrl` in the RHI database, or the wiki row's name link), then a **Web pages** submenu with **PCGamingWiki page**, **Ultra+ page**, **Ultrawide fix** and **HDR Gaming Database page**. Each entry is listed only when the game has that page (owner rule: hidden, never disabled); its tooltip is the link. Only https links are handed to the browser.
- Sources and order are upstream's: the manifest's `pcgwUrlOverrides` / `ultraPlusUrlOverrides` / `uwFixUrlOverrides` first; Ultra+ from the theultraplace.com games page; ultrawide fixes from Lyall's Codeberg repositories (API, first 50, as upstream), then RoseTheFlower's and p1xel8ted's README indexes; the HDR Gaming Database from KoKlusz's README (name without ™/®/© and with straight quotes). Names are matched by upstream's normalisation. Every list has its own download policy (theultraplace.com, codeberg.org, raw.githubusercontent.com) with a 4 MB cap, loads in the background, is kept 24 h under `~/.cache/rhi-linux/links/` and is never waited for: until it has loaded, only the manifest answers. A failed load keeps the last good copy and is retried after 5 minutes.
- PCGamingWiki: manifest page, then `pcgw_data.json` (by name, the manifest's Steam AppID or the card's), then pages found earlier. A game none of them knows is looked up in the background when its menu opens (the entry then shows the next time): Steam AppID from the manifest, the card, a cached lookup, `steam_appid.txt` in the game folder, else Steam's store search (one request a second, `store.steampowered.com` only); with an AppID, `pcgw_data.json` again, then PCGW's `appid.php` redirect when the manifest's `pcgwUseAppId` is on, else PCGW's own search. PCGW requests share the graphics-API lookup's gate (one a second, pause on 429/503). Found pages, Steam AppIDs and definite misses (kept 7 days) live in `~/.cache/rhi-linux/links/pcgw-links.json`; a manifest `pcgwUrlCacheVersion` above the stored one drops found pages and misses, as upstream. Linux difference: upstream resolves up to 15 unknown games after every library build; the fork asks only for a game whose menu is opened.
- Linux difference: each kind of link opens only its own sites (https, exact host): PCGamingWiki pages on pcgamingwiki.com, Ultra+ on theultraplace.com, ultrawide fixes on Codeberg, GitHub or Nexus Mods, HDR Gaming Database threads and discussion pages on GitHub. A manifest override, a community README, a cached list file or a database field naming any other site is left out (upstream opens any https link). Full Refresh downloads these lists again, even when their 24 h copy is still fresh (upstream reads them once per session).

### #184 — Component ⓘ dialogs

- Component rows for RenoDX, Luma, OptiScaler, ReLimiter, Display Commander, REFramework and ReShade get an **ⓘ** button (not on a row that does not apply to the game). It opens a small dialog with that component's notes for the selected game, in upstream's order: the manifest's per-game note for the component (`gameNotes` for RenoDX, `lumaGameInfo`, `optiScalerGameInfo`, `relimiterGameInfo`, `displayCommanderGameInfo`, `reframeworkGameInfo`), then the game's wiki or database content, then RHI's short description of the component. Links open in the browser (https only; each link in a 2 s tooltip). `reshadeGameInfo` is not bound here (#186).
- RenoDX: the matched mod's notes and discussion page from the RHI database, plus the game's comment from the UE-Extended database (`RenoDXdb-unreal.json`, now read, 1 h cache) or the Unity database; a native-HDR UE-Extended game gets upstream's UE-Extended note instead of the wiki's technical notes; a mod published only on Nexus Mods shows its Nexus summary (Nexus's public GraphQL query, no account) and its page, a Discord-only mod its Discord. RenoDX and Luma link the game's HDR Gaming Database analysis (#183's list).
- Luma: the Luma wiki's special and feature notes and the manifest's `lumaGameNotes` with its link. OptiScaler: the wiki's Compatibility list (and FSR4 list, which the wiki has since merged into the main one) for the game's wiki name — the manifest's `optiScalerWikiNames`, else the name without ™/®/© — with status, upscaler inputs, notes and its wiki page. Linux difference: columns are found by their headings; upstream reads fixed column numbers, which on today's wiki (an OptiPatcher column was added) show the OptiPatcher column as upscaler inputs. Pages are read as text only (no scripts or images), capped at 8 MB, kept 24 h.
- ReLimiter and Display Commander: the three change-log entries from the installed version (`CHANGELOG.md` on GitHub, as upstream) and the releases page.
- Since #210 the ⓘ is highlighted when its dialog has something for the game (see #210 below); upstream's arrow on the install button is not ported. Every remote source is optional: one that cannot be read within 15 s is left out and the dialog still opens.
- The dialog opens at once with "Loading notes…" and fills in when the sources answer (upstream reads data it already loaded and fetches only a Nexus summary before showing it); while it is open a further ⓘ click brings it forward instead of opening a second one. The RHI database's `nexusUrl` also holds GameBanana pages, so that button is named after the site it opens ("Mod page on GameBanana" / "Mod page on Nexus Mods"; upstream always says Nexus). Mod-page, Discord, release and manifest-note links keep to their sites as in #183 (note links: the community sites the manifest uses today — Nexus Mods, Codeberg, GitHub, Ultra+, PCGamingWiki, GameBanana, Discord and the Steam store). Full Refresh also downloads the OptiScaler wiki and UE-Extended database again and forgets the session's change logs.

### #197 — Available HDR mods: UE-Extended rows and Nexus/Discord-only RenoDX mods

- The RenoDX column now lists every named mod of the RHI database (`RenoDXdb.json`), also the ones RHI cannot download because their author publishes them only on Nexus Mods or Discord; their row's Download button opens that page (upstream: download, else Nexus, else Discord). Only https links are offered.
- Games of RHI's UE-Extended database (`RenoDXdb-unreal.json`, RankFTW/rhi-repo, read under the component-notes download policy, 1 h cache, last good copy offline) join the RenoDX column with ✓ or 🔨 by their `Status` and no link, as upstream; a named mod for the same game keeps its own row. Order as upstream: named mods, UE-Extended, Unity, then Luma merged in by name. The list itself is built once by the app (`AppComposition.HdrModsCatalog`) and shares the UE-Extended database instance with the ⓘ dialogs. Since #256 (upstream 2.8.8) the Download button is gone: the RenoDX ✓/🔨 opens the mod's own link, and a UE-Extended, Unity or Source row's tick opens the RenoDX wiki's mod list; #257 adds the Source engine database's games after the Unity ones.

### #185 — "New mods" notice

- A **New mods** button appears at the left of the toolbar's right-hand group (where upstream puts "New Mods Available") when the RenoDX database (mods with a download or a Nexus Mods page, as upstream), the Luma wiki or the Ultra+ games list (#183's list) has mods the user has not marked as seen; with nothing new it is not in the toolbar at all, nor in the '…' menu (owner rule: hidden, never disabled). It joins the toolbar's overflow like every other button when the row is full. Its tooltip gives the count.
- The button opens the list: one section per catalogue (RenoDX, Ultra+, Luma, as upstream), at most 30 names each and "… and N more". **Mark as seen** remembers every listed mod and hides the button; **Close** keeps it, as upstream's Dismiss / Close.
- The seen sets live in one file, `~/.local/share/rhi-linux/seen-mods.json` (upstream keeps three). The first time a list is read it is only remembered, so the first start shows no notice; a list that cannot be read (offline) is skipped and never remembered empty; an unreadable file is kept aside and no notice is shown that session. The check runs once after the library has loaded (upstream also re-checks on its background refresh; the lists behind it are refreshed at most daily).

<!-- w5/ini-presets notes -->

### #181 — INI templates, apply to all games, effect list style

- Ported: RHI's INIs folder (`~/.config/rhi-linux/inis/`, beside the OptiScaler templates) holds upstream's `reshade.ini`, `reshade.rdr2.ini`, `reshade.vulkan.ini`, `relimiter.ini` and `DisplayCommander.ini`, created from upstream 2.8.7's files the first time they are needed and never overwritten. A ReShade install with "Manage ReShade.ini" on merges the template into the game's ReShade.ini: a new file is a copy of the template (as upstream `MergeRsIni`); into an existing file it adds only the keys the file lacks, so the game's own values stay (#208, owner decision; upstream replaces every template key), and every other key stays. The ⚙ **Deploy ReShade.ini** still writes every template key over the game's value, as upstream (the user asked for the template). Red Dead Redemption 2 and Max Payne 3 use `reshade.rdr2.ini` (End opens the overlay) without its Vulkan depth `PreprocessorDefinitions` on a DirectX install. The install also writes the screenshot folder (when set) and the overlay / screenshot hotkeys when they differ from Home / Print Screen, as upstream. A new ReLimiter / Display Commander install copies its template. The ReShade, ReLimiter and Display Commander rows get a ⚙ with **Deploy ReShade.ini / relimiter.ini / DisplayCommander.ini** and the INIs folder; Settings → ReShade has the mass deploy for the three files (every game where RHI manages the component, asked once with the count), **Effect list style** (Tabs / Tree, `[OVERLAY] VariableListUseTabs`, default Tabs) and **Apply to all games** (screenshot folder, both hotkeys and the effect list style into every game's ReShade.ini and its numbered `ReShade2.ini`…, RDR2 / Max Payne 3 keep their End key; settings saved first, as upstream).
- Linux deviations: edits keep every comment, blank line and duplicate key (upstream re-serialises the file). The two shader search-path lists are merged entry by entry instead of being replaced, and RHI records what the template added, so removing ReShade takes back exactly that; the game's own `PreprocessorDefinitions` is never stripped (upstream strips every such line). A Tree effect list is also written at install (upstream applies the style only through "Apply to all games", so a new install got the template's Tabs). A user-locked (read-only) INI is written and stays locked; a symlinked one is followed only to a same-named file inside the game's folders (for ReShade.ini also `~/.config/ReShade`, one rule for every path that writes it). The files of one game are written whole or not at all (a failed write puts back the ones already written). Numbered INIs are `ReShade<N>.ini` only (upstream's `reshade*.ini` pattern also rewrote `ReShadePreset.ini`). Each game is refused where an install would be (no confirmed target, a moved game, an Xbox title its installer app is busy with, a native Linux game); a batch is cancellable between games and reports updated / skipped / failed counts, details in `rhi.log`. Mass deploy, the ⚙ and Apply to all games cover only games where RHI installed the component (upstream: wherever it was detected); a copy the user installed is left alone. Deploy relimiter.ini / DisplayCommander.ini never loses the game's file (upstream overwrites it): a file that changed since RHI wrote it is replaced only after an inline yes (the mass deploy's question counts them) and is kept beside it as `<name>.rhi-backup-<utc>`, which the ⚙'s **Restore earlier <file>** puts back; deploy and restore are journaled on the component's record. Apply to all games asks inline with the number of games first (upstream writes at once).
- Not ported: upstream's per-game "keep ReShade.ini updated" lock (no Linux row yet).

### #188 — ReLimiter global settings

- Ported: Settings → Shaders/Add-ons → ReLimiter has upstream's four globals: **On-screen display key** (default F12; picked with the same key editor as ReShade's hotkeys and stored in ReLimiter's own `Ctrl+Alt+Shift+Key` form), **Shared presets** and **DLSS info on the display** (Off / On, default Off), **FPS cap** (Off, upstream's 14 VRR presets 59–431 FPS, or Custom 20–1000; the value box shows only for Custom). **Apply to all games** saves them and writes `[FrameLimiter] osd_toggle_key`, `shared_presets`, `dlss_info_hooks` and `target_fps` (removed when Off) into every game's `relimiter.ini` beside the add-on and RE Framework's `_storage_/relimiter.ini`, then into the user's `relimiter.ini` template so later installs start with them, as upstream.
- Linux deviations: only those four keys change (comments and every other key stay); only games where RHI installed ReLimiter are written, after an inline question with their number; each game is refused where an install would be, a game with a `relimiter.ini` link leading out of the game is skipped whole (a failed write puts back the game's files already written), a batch is cancellable between games and reports counts. Upstream saves each dropdown the moment it changes; here they are saved with Settings → Save or Apply to all games.
- Not ported: the per-game FPS cap and DLSS info overrides of upstream's ReLimiter ⚙ (the ⚙ here deploys the template).

### #180 — ReShade presets

- Ported: presets live in `~/.config/rhi-linux/inis/reshade-presets/` (upstream's `inis/reshade-presets`). The ReShade row's ⚙ installs one into the game (copied beside ReShade and selected with `[GENERAL] PresetPath=.\<name>`), opens the folder and lists the presets RHI installed there with Remove. A preset `.ini` dropped on RHI is checked (upstream `PresetValidator`: a `Techniques=` line with at least one `name@file.fx`) and installed into a game the user picks (the selected game first); it is kept in the folder once Install is chosen (Cancel keeps nothing). "Also add the shader packs it needs" (on by default) reads every `Techniques=` line (upstream `TechniquesParser`), matches the effect files against each shader pack's files (upstream `ShaderResolver`, any case; a pack not downloaded yet is fetched for its list) and ticks the matched packs for the game through the Shaders/Add-ons apply (dependencies follow as there); effects no pack has are named. Settings → ReShade → ReShade presets installs one preset into every game whose ReShade RHI manages, with the same option (upstream `MassPresetInstall`), asked once with the count.
- Linux deviations: presets go only into games whose ReShade RHI manages (the preset list is kept on ReShade's record, so ReShade updates keep it and the copy is journaled: a crash or a failed save takes the copy and the ReShade.ini change back; a ReShade.ini linked to the user's shared `~/.config/ReShade` file is journaled at that file, so the link stays a link and the shared file comes back byte for byte, #213). A preset already in the presets folder under the same name (any case) is never overwritten by a drop (upstream overwrites; ReShade names every preset `ReShadePreset.ini`): the same content is reused, other content is kept as `Name (2).ini`… and the line says so. A game file of the same name (any case, as Wine looks names up) that RHI did not write, or that the user changed after RHI put it there (ReShade saves overlay tweaks into the preset), is never overwritten (upstream overwrites); Remove deletes only an unchanged copy and clears `PresetPath` only while it still names that preset. Removing ReShade leaves installed presets in place (they hold the user's settings). Upstream's per-game shader mode switch to "Select" maps to the game's own pack selection, which the fork already keeps per game.

### #189 — Luma settings (HDR on first boot, TAA)

- Ported: the Luma row of an Unreal Engine game (manifest engine hint) whose Luma RHI installed gets upstream's ⚙: **HDR on first boot** — Default removes `[Luma] EnableHDR` and `DisplayMode` from ReShade.ini so Luma decides, Off writes both as 0, On as 1 (what the install writes, as upstream: the install's values are unchanged) — and **TAA** — On writes `r.DefaultFeature.AntiAliasing=2` and `r.PostProcessAAQuality=4` under `[SystemSettings]` in Engine.ini where they are missing (upstream `ApplyEngineIniCustomKeys`), Off takes them out. The ⚙ is hidden for other Luma games (upstream shows an empty dialog there), and TAA is hidden when no Engine.ini location can be found for the game.
- Linux deviations: both settings are kept on Luma's record, journaled with the file edits (a linked ReShade.ini or Engine.ini at the file the link leads to, never the link, #213): a Luma update keeps the choice (Default stays Default; TAA keys are not reconciled away with the wiki keys), and removing Luma puts back the `[Luma]` values from before RHI and removes the TAA keys RHI wrote. Off removes only TAA keys RHI wrote that still hold its value (upstream removes them whoever wrote them); the result line says when keys from before RHI stay. Engine.ini goes through `WriteUserEngineIni` (a user's read-only lock is kept). TAA state is read from the files and the record, not from a separate per-game list.

### #182 — Peak brightness, RenoDX settings parity

- Ported: Settings → ReShade → RenoDX peak brightness: **Peak brightness (nits)** (0 = not set), **Auto**, **Write when RenoDX is installed** (Off / On, default On, upstream "Auto-apply peak nits on deploy") into presets 1–3 (default all three; the boxes show only while it is on) and **Apply to all games**. A RenoDX install or update writes `ToneMapPeakNits` into the chosen `[renodx-presetN]` sections (created when missing, as upstream `ApplyPeakNits`); Apply to all games writes it into every game whose RenoDX RHI installed. The RenoDX ⚙ gets **Maximum nits in this game** (upstream "Set Maximum Nits": every `[renodx-presetN]` in the file, or preset 1) and, for Unreal Engine games the manifest does not bar, the **UE-Extended** switch (upstream `ToggleUeExtended`): RenoDX is reinstalled through the normal update path with the choice, which that update's own record keeps (a failed reinstall changes nothing) (UE-Extended = upstream's default `marat569.github.io/renodx/renodx-ue-extended.addon64`); updates keep the choice and do not report the catalogue's pick as an update.
- Auto on Linux: Windows' `DisplayMonitor.MaxLuminanceInNits` has no direct equivalent, so RHI reads each connected monitor's EDID (`/sys/class/drm/card*-*/edid`, read-only: the CTA-861 HDR Static Metadata block's desired content max luminance, `50·2^(CV/32)` nits) and prefers the peak brightness the user set for that very monitor in KDE's display settings (`~/.config/kwinoutputconfig.json` `maxPeakBrightnessOverride`, matched by the EDID's MD5 as KWin keys it): many TVs declare HDR without a luminance. Several monitors show a picker; none says so. Values are capped at 10000 nits, the box's limit.
- Linux deviations: every write is owned through RenoDX's per-key state on its record (one state per preset section), so removing RenoDX puts back the value from before RHI; an install or update does not overwrite a value changed in RenoDX's overlay since RHI wrote it (upstream overwrites at every ReShade.ini merge), while Apply to all games (asked inline with the number of games) and Set are explicit and write it whatever is there. Upstream also writes the global value into ReShade.ini at a ReShade install whether or not RenoDX is there; RHI writes it with RenoDX. The UE-Extended switch Off swaps a UE-Extended pick for the generic Unreal add-on (upstream restores the card's previous mod; the fork's catalogue picks UE-Extended only from a manifest override). Batches are cancellable between games and report counts.
- Not ported: manifest `componentUrls.ueExtended` (not in the fork's manifest policy; the default URL is used). Upstream's RTX HDR rows in the RenoDX ⚙ (NVIDIA driver feature, not on Linux). UE-Extended's own `[renodx]` native-HDR keys and Engine.ini HDR keys at install, with the ⚙ HDR row: ported in wave 8 (see #218 below).

<!-- w5/shell-launch notes -->

### #190 — Edit game: name, wiki mod name, bitness

- Ported: Edit game has a **Name and bitness** block (upstream's Game Overrides name/wiki/bitness rows): **Game name** (what the card, the details header, search and sorting show; search still finds the detected name), **Wiki mod name** (the name RenoDX wiki lookups use; wins over the manifest's `wikiNameOverrides`, and clearing it brings the manifest's back) and **Bitness** (Automatic / 32-bit / 64-bit). All three are library-state edits (`game-library.json` `GameEdits`), kept across rescans and Repair after move, cleared by Restore automatic detection. Bitness precedence is upstream's `ResolveIs32Bit`: the user's choice, then the manifest's override, then the exe header (`GameArchitecture.Of`; the user's choice is never stored in the manifest field).
- Linux deviations: the display name is presentation only — library keys, desktop shortcuts, launch-by-key, status lines and catalogue lookups keep the detected name (upstream renames the game everywhere and its lookups then use the new name; here the wiki name field is the lookup override). A bitness change that moves the game's effective bitness is refused while RHI components are installed (upstream uninstalls every component first; the fork never removes components as a side effect, as for a patch-target change); choosing the bitness the game already has is always allowed. The Luma lookup does not read the wiki name yet (it matches by name as before).

### #193 — Saved custom filters

- Ported: while the search box holds a search, **+ Save as filter** under the filter tabs saves it under a name (the search is the suggested name; a name is required and unique in any case, as upstream). Each saved filter is a chip there: clicking applies its search on top of the filter tab and the search box (upstream's independent second pass), clicking the active chip turns it off, and its context menu (also the menu key or Shift+F10) deletes it. Saving clears the search box and turns the new chip on, as upstream. The game count line names the active filter. Filters live in the library file (`game-library.json` `CustomFilters`, absent while there are none); which chip is on is kept for the session only. Saves follow the library-write rules (refused during Repair after move, adopted only once on disk).

### #192 — Logs, game report, help and What's new

- Ported: Settings → **Support** has **Copy logs** (upstream `CopyLogsArchive_Click`: every RHI log — `rhi.log` and its rotations, `recovery.log`, the newest 20 per-launch logs, the newest 4 MiB of each — zipped with personal details masked), **Open logs folder** and **Export game data** (upstream `ExportGameData_Click`: schema "1" JSON of every library game — folder name, exe relative to it, API and all APIs, bitness, engine, store ids, PCGW/Engine.ini overrides — zipped). The library card menu has **Copy game report** (upstream `GameReportEncoder`: upstream's "correct values first" gate as one line, an optional note, a markdown report of the game info, the corrections made in Edit game and RHI's components). **Quick start** opens **Help**: getting started, questions, credits and licences (upstream's About credits that apply on Linux, plus the Linux port and the UI toolkit; Donate is unchanged, upstream's authors only) and **What's new**, which shows the fork's own `CHANGELOG.md` (embedded at build time); clicking the version in the status bar opens it too, as upstream's patch notes.
- Masking (upstream `CrashReporter.SanitisePath`, Linux form): the home folder becomes `~`, the user name `[user]`, Steam `userdata/<id>` and SteamID64s placeholders, token-shaped strings `[token]`, and the private Xbox installer app's name a neutral placeholder; an Xbox card's report leaves its launch command out.
- Linux deviations: files are written whole as new files (never replacing one, mode 0600) into RHI's support folder `$XDG_STATE_HOME/rhi-linux/support/`, never into a game folder; upstream puts the file itself on the clipboard, RHI-Linux puts the file's path there (a report: its text), and when the session has no clipboard the line says where the file was saved. The report itself is in English, as upstream, for the people who read it.

### #187 — OptiScaler presets and the global menu key

- Ported: the OptiScaler ⚙ has upstream's **Presets** row (four global slots in `~/.config/rhi-linux/optiscaler-presets.json`; first run offers upstream's "DLSS Enabler" slot — FG input = upscaler, FG output = DLSSG, Nvngx replacement = Enabler, Streamline / DLSS Enabler on — and writes nothing until a slot is saved). Per slot: a name, **Save** (captures every ⚙ setting of this game except the menu key) and the slot's button, which applies it through the ⚙'s own setting path, the Streamline / DLSS Enabler stack and its version first; the ⚙ reopens to show the result. Shown where upstream shows it (Nightly, DLSS NR and F5 channels). Settings → DLSS/Streamline → **OptiScaler menu key** (upstream's hotkey row: Insert, Delete, Home, End, Page Up/Down, F1–F12 as virtual-key codes, plus OptiScaler's Automatic and Disabled): the key every INI template and new install carries; **Apply to all games** saves it and writes it into every template and every game's RHI-managed `OptiScaler.ini` (one game per unit, Cancel between games, a refused or unwritable game keeps its file and is counted). The ⚙'s per-game menu key lists the same keys.
- Linux deviations: the default is **As in the INI template** (nothing is rewritten until the user picks a key; upstream defaults to Insert, which is what the shipped templates' `auto` means anyway). A preset does not carry the channel (changing it reinstalls OptiScaler on Linux; choose the channel first) nor the DLSS NR runtime fields (the fork's NR settings live outside the ⚙); a captured setting the game's ⚙ does not offer, or a value it does not list, is left out and counted. A template the user locked read-only gets the key and stays locked (the user-locked INI rule); a locked game `OptiScaler.ini` is refused and counted, as for any ⚙ change.

### #191 — Resolution auto-toggle

- Ported: the HDR row has **Change resolution while playing** and a resolution (upstream's per-game RES toggle; off by default, saved with the game's HDR profile in `game-library.json`, follows Repair after move). A direct launch from Play switches the primary output to that size before the game starts and back when its launch command ends; with Gamescope chosen in HDR the size goes to gamescope as `-W/-H` and the desktop is not touched; a store hand-off (Steam, Heroic, Lutris, the Xbox app…) is never switched around, because RHI does not see the game close — the line under the toggle says so and points to Gamescope, whose launch options carry the size.
- Linux form: upstream calls Windows' `ChangeDisplaySettingsEx` on the primary display. RHI-Linux uses the desktop's own tool: `kscreen-doctor` on KDE Plasma (the primary output by its priority) or `wlr-randr` on wlroots compositors (sway, Hyprland, river, Wayfire, labwc…; the first enabled output), at the current refresh rate when that size offers it, else its highest. The size list is the display's own sizes as that tool lists them at start-up (upstream lists the display's modes too); a size saved earlier that the display lacks stays in the list; without the tool's list a fixed list is shown. The toggle is hidden where it can never apply: a store hand-off card without gamescope installed, or a desktop with neither a usable display tool nor gamescope (GNOME and other desktops; X11 outside KDE is not switched, no `xrandr`).
- Going back: the mode to go back to is written to `$XDG_STATE_HOME/rhi-linux/display-mode.json` before anything changes (no record, no switch), by resolution and refresh rate (kscreen-doctor's own `W`x`H`@`refresh` mode name, never its mode number, which can name another mode after a reboot or a monitor change). The launch shell that runs the game puts that mode back itself as soon as the game's command ends, so the display goes back even when RHI was closed meanwhile; RHI then only checks it is back and removes the record. A switch that fails or times out tries to go back at once and keeps the record unless that worked; a failed restore keeps it. A second switch is never stacked on one not yet put back. If RHI crashed and the shell's restore did not happen, the next start puts the mode back once the recorded game process has ended (a process whose start time could not be recorded is never waited on, so a reused process number cannot hold it back). Output names and modes from the tool, and every record read back, must be in the shapes RHI writes; anything else is never run, and an unreadable record is set aside (copy kept, plain status line) instead of blocking later switches.
- Limit (differs from upstream): upstream follows the game process that survives when the started program exits (`MonitorProcess`). RHI-Linux puts the display back when the launch command ends, so a game whose launcher stub starts the real game and exits at once (or a wrapper that daemonises) gets its display back early; such games can use Gamescope in HDR, which carries the size without switching the desktop.

<!-- w5/reshade-core notes -->

### #92 — ReShade for Vulkan games (implicit layer)

- Ported, adapted to Wine/Proton (spike: Wine's own `vulkan-1.dll` loads no Windows layers; the Khronos loader reads
  implicit layers only from HKEY_LOCAL_MACHINE under Wine, never HKEY_CURRENT_USER; Proton's DXVK and vkd3d-proton load
  `winevulkan.dll` directly, so DirectX games never see the layer — except with DX9 HDR (#200), whose DXVK HDR build asks
  for `vulkan-1.dll` first). Upstream registers one global layer for the whole
  PC; RHI Linux sets it up per game: the Khronos Vulkan loader (`vulkan-1.dll`, newest from LunarG's Vulkan runtime)
  beside the executable with a per-executable `vulkan-1=native,builtin` Wine override, the layer (`ReShade64.dll`/
  `ReShade32.dll` and upstream's `ReShade64.json` manifest) in the prefix's `C:\ProgramData\RHI-Linux\ReShadeVulkan\<game>\`,
  registered under `HKLM\Software\Khronos\Vulkan\ImplicitLayers` (`Wow6432Node` for 32-bit games) in the prefix's
  `system.reg`, and `ReShade.ini` beside the game, merged from the user's `reshade.vulkan.ini` template (#181, depth
  settings kept), as upstream's Vulkan install does; Deploy ReShade.ini on such a game uses that template too. The registration is visible to the whole prefix, but only a program
  that loads a native Khronos loader can see it: the game RHI set up, or (rare, in a shared Heroic/Lutris prefix) another
  game there that ships its own `vulkan-1.dll` with a native override. Games using Wine's own loader, DXVK or
  vkd3d-proton never load it.
- Offered only for a Windows game whose graphics API is Vulkan (Vulkan alone found in the executable, or the user's own
  API override set to Vulkan): the ReShade row then reads "ReShade · Vulkan" and its Install (or the ReShade profile's
  Apply) is the explicit choice. A multi-renderer game stays DirectX first; a component that needs ReShade (RenoDX, Luma
  …) never installs the layer by itself. Refused with a plain reason on Proton/Wine older than 8, an unknown runner, a
  prefix never started, or a game with its own `vulkan-1.dll`. One ReShade per game: the layer is refused while a DLL
  ReShade is installed; ShortFuse and OptiScaler (which move ReShade's DLL) are refused while the layer is installed.
- Update swaps the ReShade DLL; the loader only when LunarG lists a newer version (offline: kept, and the line says so).
  Update All never switches a game between the DLL and the layer. Removal takes away the loader, the layer files, the
  registration (only while it still holds RHI's value) and the Wine override, all journaled; the shared
  `ProgramData\RHI-Linux\ReShadeVulkan` folders and the `ImplicitLayers` key go with the prefix's last layer. As for
  `user.reg`, editing `system.reg` leaves `system.reg.rhi-lock` and up to three `system.reg.rhi-backup-<time>` copies in
  the prefix after removal. An update also puts back a loader that was deleted. Repair after move keeps working because
  the registration names a `C:\` path inside the prefix.
- A 32-bit game in a 32-bit-only prefix (`#arch=win32`, older Wine/Lutris prefixes; never Proton) is refused with a
  plain reason: a 32-bit game's loader reads the layer from `Wow6432Node`, which such a prefix does not have. On a Vulkan
  game without ReShade the add-on rows say ReShade is installed only on request; ShortFuse is not offered there.
- Changed from upstream (PAR-D07, `e8cdedf`): changing the graphics-API override to or from Vulkan while ReShade is
  installed is refused until ReShade is removed (upstream uninstalls ReShade on the switch; RHI never removes a
  component the user did not ask to remove). Changes within DirectX are not affected.
- Not ported: upstream's global layer for every game and its footprint marker (`RDXC_VULKAN_FOOTPRINT`; the record owns
  the files, and a layer whose record is gone is recognised by the game's own layer folder in the prefix next to the
  loader and `ReShade.ini`). Native Linux Vulkan games are out of scope (vkBasalt is a different tool).

### #186 — Legacy ReShade version list and manifest pins

- Ported: the ReShade profile's Legacy version is a dropdown of the RHI manifest's `legacyReShadeAvailable` (newest
  first, as upstream's "Legacy..." picker; 6.4.1 alone while the manifest is unread or lists none) instead of a free-text
  box. A version the profile shows that the list lacks (an installed legacy ReShade, a pin) stays offered.
- Ported: `legacyReShadeVersions` (Max Payne 3 → 6.4.1): a fresh ReShade install on that game, from its row, from a
  component that needs ReShade (RenoDX) or from the profile, uses that version; the profile starts on Legacy at it and
  the user may choose another channel. An installed ReShade is never changed by a pin (its own profile is re-applied).
- Ported: `reshadeGameInfo`: the manifest's ReShade note for the game is shown in the ReShade profile, with its link
  when the manifest gives one (https, on the sites manifest notes use, as the ⓘ dialog's note links), and is the text of
  ReShade's ⓘ dialog (#184).
- Changed from upstream: upstream stores the pin as a per-game channel override when it first sees the game; RHI Linux
  has no per-game channel setting outside the installed profile, so the pin applies to each fresh install instead.

## Parity notes (upstream 2.8.7, wave 7 owner decisions)

<!-- w7/smalls notes -->

### #209 — Card-menu label "Choose what Update All changes…"

- Upstream calls the per-game dialog **Update Inclusion**. Owner choice: the card-menu entry reads **Choose what Update All changes…** (PT-BR "Escolher o que Atualizar tudo muda…"); the dialog title ("Choose what Update All changes — <game>"), the saved line ("Saved what Update All changes for <game>."), the Settings → Maintenance note that points at the entry and the Update All summary part (" · N left out (your choice)") follow. Behaviour is unchanged (#178).

### #210 — Highlighted ⓘ when there are notes for the game

- Upstream (`HasRealInfoContent`, `AddonInfoResolver.GetSourceType`): per-game content = the manifest's note for that component, or the game's wiki/database entry (RenoDX: the matched mod's notes, discussion page or wiki status; Luma: wiki notes or `lumaGameNotes`; OptiScaler: the compatibility wiki row; RenoDX/Luma also an HDR Gaming Database page; native-HDR UE-Extended games); ReLimiter and Display Commander always count (change log). In 2.8.7 every clickable ⓘ is blue and the game-specific signal is a ◄ arrow on the install button.
- Linux (owner choice: highlight the ⓘ itself): the ⓘ of a row with such content gets the accent border on the pressed-state surface (existing theme brushes), and its 2 s tooltip says why ("Notes for this game about <component>…", or for ReLimiter / Display Commander "What is new in <component> from the installed version…"); other rows keep the plain ⓘ and the general tooltip (not muted: it still opens the general text). Worked out in the background from the lists the dialog reads (manifest, RenoDX/Luma catalogues, OptiScaler wiki, HDR Gaming Database; never a per-game request such as a Nexus Mods summary or a change log), so a game whose lists are still loading shows the plain ⓘ and it turns highlighted when they arrive (within the dialog's 15 s source limit) while the game stays selected; the answer is kept for the session and worked out again after a Full Refresh. A RenoDX mod published only on Nexus Mods is not highlighted (upstream's highlight does not read the Nexus summary either).
- Not built: the ◄ arrow on the install button (the highlighted ⓘ carries the same signal, and the row's Install/Remove button already sits beside up to two other buttons at 640 px).

### #202 — Shortcut icon from the game exe

- Upstream (`ShortcutService`): Steam and other games point the `.lnk` icon at the game exe (Windows draws its icon); Xbox games download store or Steam art; RHI's own icon last. Owner decision: the exe icon yes; bringing a running RHI forward on a second plain start no (not built; unreliable on KDE Wayland).
- Linux: a desktop entry cannot point into an exe, so RHI reads it (`PeIconReader`, RHI.Core): the exe's `RT_GROUP_ICON` (first group) → its largest `RT_ICON` (side, then colour depth, then size; the next one is tried when one cannot be read), PNG-format icons kept as stored, BMP icons (1/4/8/24/32 bits per pixel, uncompressed) converted to PNG with their alpha, or the AND mask as transparency when there is none (own small PNG encoder; no new package). Bounded: headers, section table, three directory levels and one icon of at most 4 MB, DIB sides up to 1024; an exe that is not a readable PE (an encrypted Game Pass exe, a native Linux binary, a malformed resource) gives nothing and the next icon is used silently. Order: exe icon, then the Steam library art (as before), then RHI's icon. The icon is written as `$XDG_DATA_HOME/rhi-linux/shortcut-icons/<shortcut name>.png` (one per game, shared by the desktop and menu entry, atomic write) and rewritten every time the shortcut is created; the older Steam-art copy of the same shortcut is removed when the exe icon takes over.
- RHI has no "remove shortcut" action (the user deletes the file): RHI's icon files whose shortcut is in neither the desktop nor the applications folder are removed the next time any shortcut is created (skipped while the desktop folder is unknown; only RHI's own `rhi-linux-game-*` files in its icon folder).

### #199 — NR Cost Scaler in Update All

- Upstream (`DlssNrCostScalerService.CheckForUpdateAsync` + `MainViewModel.BackgroundScan`/`Update`): after each background scan and on Update All, the latest xenmods/DLSSNR-Cost-Scaler release is compared with the staged one and, when newer, re-staged and copied over every game that has the scaler (proxy, INI and add-on overwritten).
- Linux: NR Cost Scaler is an Update All family (manual and background, after the methods it wraps). Check: the latest release's tag against the version RHI recorded; a release list that cannot be read is "could not be checked", never "current". Update: the proxy and the companion add-on are swapped in place for the release's (journaled, the download and checks before the first change, a failed save rolls the swap back as a whole; record, download digest and update history as every family); the owner's runtime `nvngx_dlssnr_real.dll` is never touched and the owner stays held by the scaler (#96). A proxy or add-on that changed since RHI wrote it refuses the update before anything changes; a release that no longer ships the add-on takes RHI's copy away. The per-game "Choose what Update All changes…" dialog offers it like every family (upstream has no global skip for it, so Settings does not list it).
- Difference: the INI is replaced only while it still holds what RHI wrote; a scale the user set is kept (upstream overwrites it) and the result line says so.

### #195 — Dropped or watched RenoDX add-on installs into the matched game

- Upstream (`DragDropHandler.ProcessDroppedAddon`, `AddonFileWatcher`): a dropped `.addon64`/`.addon32` (or one the downloads watcher finds) is offered for a game picked in a list — pre-selected by the add-on name (a game of two or more words with a word longer than three letters in the file name), else the selected game — then confirmed (file, game, path, the RenoDX add-on it replaces), copied into the game, recorded, and a watched file in the watch folder is deleted afterwards (`deleteSourceAfterInstall`).
- Which files get the game install follows upstream, with one difference: upstream counts any name starting `renodx` (no hyphen) as a RenoDX add-on, so a dropped `renodxfoo.addon64` replaces the game's RenoDX there; the fork needs the `renodx-` prefix and installs such a file beside RenoDX instead. Dropped: every `.addon64`, `.addon32` and `.addon` except `renodx-dlss5*` and `renodx-dlss.*` (a Luma add-on keeps the Luma install). Watched: only a `renodx-*.addon64|.addon32` game add-on (not `renodx-universal_ue`, `-devkit`, `-dlssfix`, `-upgrade`, `-mfgunlock`, `-dlss5`, `-dlss.`). Where upstream has no game install for a file, it keeps the Custom Add-ons import (#130): every other watched add-on, the two DLSS names above, and an add-on downloaded through an nxm link (#99). A dropped add-on whose name belongs to one of RHI's own component rows (RenoDX Upgrade, MFG Unlock, the devkit, DoF Fix, ReLimiter, Display Commander, DLSS5's) or a plain `.addon` that is not RenoDX's also keeps the import, which says why (RHI's row installs it; Custom Add-ons needs the bitness in the name).
- Linux, a RenoDX game add-on (`renodx-` name, as upstream's watcher) opens **Install RenoDX add-on**: the file, a list of the library games that can take it (native Linux games are left out) pre-selected by the RenoDX catalogue's add-on file for the game, then the game's whole name in the file name (the longest such name), then upstream's word match, then the selected game (otherwise nothing is pre-selected and Install is disabled, its 2 s tooltip pointing at the list), the folder it goes into, the RHI RenoDX add-on it replaces, and a note that the row's Remove takes it out and Update All cannot check it. Install goes through the operation gate, the install guards (patch target, Xbox write gate, an add-on turned off for No Addons) and the update history, into `RenoDxDeploymentService` as a local package: the RenoDX record (`Version` = the file name, `RenoDX.LocalPackage`), so Remove and Repair after move know it, and Update All counts it as "could not be checked". Refusals as a catalogue install (no ReShade, unknown bitness, a RenoDX add-on or same-named file RHI does not own, a changed RHI file, unreadable deployment records) plus the add-on's own bitness from its PE header (a file without an x86/x64 header — a saved web page, a cut-off download — is not an add-on): a 32-bit add-on for a 64-bit game or the reverse is refused with a plain reason. Only the add-on file is written, and on an Unreal game RenoDX's Engine.ini colour-grading line (#217, as upstream's drop; no `[renodx]`, Engine.ini template or Control UE step — Control's add-on is refused with a pointer to the row's Install); RHI's earlier RenoDX add-on of another name is removed in the same journaled operation (#205), before the record is saved: any failure before the save, or a crash, puts both files back.
- Linux, any other dropped add-on opens **Install add-on** (same matching and folder line; the list holds only games with ReShade with add-on support in their folder, and with none the status line says so and nothing is copied): upstream copies it into the game beside RenoDX; Linux imports it into Custom Add-ons (never over a different file) and adds it to the game's Shaders/Add-ons selection, whose journaled apply places and records it, and unticking it there takes it out. RenoDX is left alone. Its bitness must fit the game (plain reason).
- Linux (wave-7 integration with #179): the add-on is added to the game's own list. The add-ons the game gets now (from
  the global set, its own list or none) plus the dropped one become its **Choose for this game** list, so a later global
  change or Update All does not take it out again; the global set itself is never edited.
- A watched file directly in the watch folder is removed after a successful install, as upstream (the status line says so), but only while it still has the bytes RHI copied (a newer download under the same name stays); a dropped file is never touched.
- Since #216 (wave 10) a dropped or watched archive (`.zip`, `.7z`, `.rar`) offers the `renodx-`/Luma add-on inside for this same confirmed install: see #216 below.

<!-- w7/reshade notes -->

### #208 — The ReShade.ini template only fills in missing keys

- Owner decision, deliberately unlike upstream (`AuxInstallService.MergeRsIni` replaces the game's value of every key the template names): a ReShade install or update (DirectX proxy and the Vulkan layer) adds a template key only when the game's ReShade.ini lacks that key in that section, so a value the user changed (in ReShade's overlay or by hand) survives every update. A new ReShade.ini still starts as a copy of the template. The two shader search-path lists keep their entry-by-entry handling (RHI adds and takes back its own entries). The Settings values (screenshot folder, overlay / screenshot hotkeys, effect list style) are not template keys and are written as before. The explicit ⚙ **Deploy ReShade.ini** still writes every template key over the game's value (as upstream): it is the user asking for the template.

### #196 — Rename an installed ReShade / OptiScaler DLL in place; Reset DLL names

- Ported (upstream `DllOverrideService.UpdateDllOverrideNames`, Reset DLL Names): while RHI's ReShade (or OptiScaler, in its ⚙) is installed, its DLL-name dropdown stays enabled and offers Automatic and every free name; choosing one renames the installed DLL in place. The file, its per-game Wine `native,builtin` override (the new name's set, the old one restored while it still holds RHI's value; none for `ReShade64/32.dll`), the saved choice and the component's record change in ONE journaled operation, committed by the stamped record save: a failure or a crash before it puts the old name, override, choice and record back. ReShade.ini, D3DCompiler_47 and OptiScaler's other files stay where they are. **Reset DLL names** (beside ReShade's dropdown, wrapping below it at narrow widths; shown only when a ReShade / OptiScaler name of the game is saved or an installed one is not at its automatic name) puts both choices back to Automatic and renames installed DLLs back to their automatic names the same way (OptiScaler first, as upstream). RTX Encore's name is not part of it (upstream's Reset covers ReShade, Display Commander and OptiScaler; Display Commander is a ReShade add-on on Linux).
- Linux deviations (refused with a plain reason, nothing changed, where upstream renames or overwrites): a name another file or another RHI component already uses (upstream deletes the file in the way); a DLL that changed since RHI installed it; a DLL whose name RHI keeps the game's own original under (`.original`); while Wine runs for the prefix; an Xbox title its installer app is busy with. Names that cannot be taken are left out of the dropdown. The name stays (dropdown locked, reason in its tooltip) while another component decides it: ReShade chained to RHI's OptiScaler as `ReShade64.dll`, ShortFuse's `ReShade64.asi`, the DirectX 9 translation's move to `dxgi.dll`; remove and reinstall to change those. Reset DLL names is not shown for such a name (only a saved choice counts there, and Reset then clears that choice alone).
- OptiScaler renames freely while RHI's ReShade is chained to it as `ReShade64.dll` (as upstream; OptiScaler loads it by that name whatever its own). The rename also records on ReShade's record the name ReShade had when the chain was made, in the same journaled write; removing OptiScaler then moves ReShade back under that name with its own Wine override (as upstream restores ReShade's own name), never under OptiScaler's new one (`wininet.dll`, `winhttp.dll` and `dbghelp.dll` are not names ReShade loads under). Linux deviation: when another file has taken that name meanwhile, OptiScaler's removal is refused with a plain reason (remove ReShade first, or free the name) where upstream leaves ReShade as `ReShade64.dll`, which nothing loads once OptiScaler is gone. Upstream's Reset moves a ReShade that holds OptiScaler's default name aside to `ReShade64.dll`; RHI's automatic OptiScaler name already skips a name another RHI component holds (#172), so Reset never needs that move (RHI makes the chain only on install, with OptiScaler.ini's `LoadReshade`).

### #201 — ReShade for XNA games through the prefix's .NET assembly folder (Terraria)

- Ported (upstream `AuxInstallService.GacSymlink`, manifest `gacSymlinkGames`): on a game the RHI manifest lists (Terraria), ReShade's normal install takes this path: the 32-bit ReShade (`GAC_32`) goes into the game folder as `d3d9.dll` with ReShade.ini, and the DLL and ini are also placed in the listed folder (`C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Graphics\v4.0_…`) inside the game's own Wine/Proton prefix, where XNA loads `d3d9.dll` from; that ini copy carries `[INSTALL] BasePath`, the game folder as the game sees it (`Z:\…`), so ReShade takes its shaders and presets from the game folder. The game folder's own ReShade.ini never gets the key (it may be the user's shared `~/.config/ReShade` file, read by every game). The game's exe gets the per-game `d3d9=native,builtin` override (journaled, restored on removal). The template rules of #208 apply to that ReShade.ini. Update swaps both DLL copies; removal deletes what RHI placed and leaves the prefix (`user.reg`, `system.reg`, the assembly folder) as it was. The DLL-name dropdown is hidden on these games (XNA loads only `d3d9.dll`).
- Linux deviations: copies instead of upstream's symbolic links (a headless Wine check showed Wine and Proton load either the same way, but a journaled rollback restores a tracked path as a plain file, so RHI could not put a link back after a failure or crash; copies also survive a library move). Repair after move re-points the GAC copies with the game folder, and also when the prefix moved on its own or apart from the game folder (a custom, Heroic or Lutris prefix), and then rewrites the GAC ReShade.ini's BasePath to the game's new folder while that copy is still RHI's (#214); until then the game counts as moved and installing, updating or removing any component on it is refused. The GAC copies count as moved only when they left the recorded folder and the prefix the game uses now holds them (RHI's bytes, checked against the record's SHA-256, not just a `d3d9.dll` of that name): a game switched to another prefix (the old one still there), a remote-list change naming another folder, or the same prefix reached through a symlink is not a move. When the game folder moved too, the old prefix is gone and the prefix in use holds a `d3d9.dll` RHI did not place, Repair after move changes nothing and says what to do (move that file out, copy the game folder's `d3d9.dll` in its place, Repair again); when that folder has no `d3d9.dll` (a fresh prefix), it says to copy the game folder's `d3d9.dll` there and Repair again. A GAC `ReShade.ini` that changed since RHI copied it is kept on update and removal (never discard). Refused with a plain reason, nothing changed: a `d3d9.dll`/`ReShade.ini` RHI did not place in that folder (upstream deletes it), RHI's copy changed since, the prefix not set up or the folder missing ("start the game once"), a manifest path that would lead outside the game's prefix, Wine running for the prefix. Upstream needs Windows admin rights for the links; not on Linux. A native Linux build of a listed game gets no XNA path (Terraria's native Linux build is not XNA); its ReShade row says so.

<!-- w7/shader-sets notes -->

### #179 — Global shader and add-on sets, per game Global / Select / Off

- Ported (upstream `SelectedShaderPacks`, `GlobalShadersOff`/`UseCustomShaders`, `EnabledGlobalAddons`,
  `PerGameShaderMode`/`PerGameAddonMode`): a global shader set and global add-ons, and per game a choice for shaders and
  for add-ons. Layout (owner's pick, B): no new controls in the game's panel; the game's **Shaders/Add-ons** dialog starts
  each tab with **Use the global set / Choose for this game / None** (upstream Global / Select / Off), the list below can
  be ticked only for Choose (it shows the global set, or nothing, otherwise); Settings → ReShade → **Global
  shaders and add-ons…** (under "Manage ReShade.ini search paths") opens the same dialog for the global set (Save global
  set), with its own Cancel while the Save brings the games up to date (the main window's busy overlay is behind
  Settings). The add-on choice is not shown while ReShade is the No Addons build (its add-ons are held). Upstream's
  global shader mode is the global list itself: nothing ticked = Off, only "Custom Shaders (local)" ticked = Custom.
- One answer per game: ReShade's install and update (the ReShade row, profile Apply, ReShade installed for another
  component, Update All), the Shaders/Add-ons dialog, Update All's Addon Manager family (#194) and the start-up shader
  prefetch (the global set's packs first) all read the game's effective set. Every one of those ReShade paths gets a
  native D3DCompiler_47 for that set first (Protontricks/Winetricks, as profile Apply); when none can be had ReShade still
  installs, without the packs, and says why. A ReShade install on a game that has no Addon Manager add-ons yet also
  applies its effective add-ons (upstream deploys add-ons after ReShade); if that fails, ReShade stays installed and the
  message says why.
- A shader change (a game's choice, the global set) changes only the pack files, ReShade.ini's search paths and the
  record's pack list, in one journaled operation on ReShade's record — never ReShade itself (upstream likewise only
  re-copies shaders; review of decision 9). It is logged in the update history as "ReShade (reshade-shaders)". Add-on
  changes go through one Addon Manager operation each.
- Saving the global set brings every game with RHI's ReShade that uses it up to date, one game at a time; a game on its
  own list or None is never touched, nor a game left out of Update All for ReShade (shaders) or the Addon Manager
  (add-ons); a running game is skipped and named, Cancel stops between games, a game that fails keeps its files, and the
  status line counts the outcomes. Save also catches up games a cancelled or skipped run left behind; the game's dialog
  says when its files do not follow its choice yet.
- Ticking a different list in a game's dialog (Choose), ReShade profile Apply or a preset with its shaders makes the
  game use its own list (upstream); applying the list the game already gets keeps it on the global set.
- Migration (owner decision): a game's saved shader packs (its ReShade record) and Addon Manager add-ons become its own
  list ("Choose for this game") with exactly the same contents; a game without a saved selection uses the global set.
  The Addon Manager's older-build copy is brought into the ledger first. Stored in
  `~/.config/rhi-linux/shader-addon-sets.json` (keyed like the ledger, moved by Repair after move; unreadable = copied
  aside, never saved over, and every install keeps what the records say; nothing is saved while the ledger itself is
  unreadable).
- Linux differences: the global set starts **empty** (upstream: Lilium). Under Wine/Proton every shader pack needs a
  native D3DCompiler_47, so a Lilium default would make every ReShade install run Protontricks; a game without a saved
  selection deploys what it did before (nothing). Upstream's per-game **Custom** mode is covered by Choose (tick "Custom
  Shaders (local)"). Upstream re-syncs every game at each start; RHI Linux changes game files only when the user changes
  a choice, installs or updates. Switching shaders to the global set forgets the game's own pack list (upstream);
  switching add-ons keeps it for a later Choose (upstream).
- Older builds ignore the new file and keep using the records as before; a selection an older build changes after the
  migration is replaced by this build's saved choice at the next apply.

- Linux (wave-7 integration with #201 and #207): the shaders-only sync also rewrites RHI's ReShade.ini copy in an XNA
  game's .NET assembly folder (the game's ini plus `[INSTALL] BasePath`, kept when the user changed it), and writes a
  ReShade.ini linked to the shared `~/.config/ReShade` file through the link, journaled at the shared file; a link to a
  file outside the allowed folders is refused with "The shader packs were not changed: …".

<!-- w7/nvapi-launch notes -->

### #211 — NVIDIA DLSS presets, render scale and RTX 50 frame generation through launch variables

- Ported as a Linux route: upstream writes these per-game settings into the NVIDIA driver profile (NvAPI DRS); under Proton the same settings reach the game through dxvk-nvapi, which answers the game's DRS queries from `DXVK_NVAPI_DRS_<NAME>` variables. RHI keeps the per-game choice in its own launch profile (`game-library.json`, the game's HDR profile entry; moves and key changes carry it) and adds the variables when the game starts — for every HDR mode, Off included. RHI writes no driver profile, prefix registry or game file; `DXVK_NVAPI_SET_NGX_DEBUG_OPTIONS` (writes the prefix registry) is never set.
- Mapping (values are numbers, which every dxvk-nvapi with DRS variables reads): SR/RR/FG preset → `NGX_DLSS_SR|RR|FG_OVERRIDE_RENDER_PRESET_SELECTION`; SR/RR render scale (33–100 %) → `NGX_DLSS_SR|RR_MODE=6` (custom) + `NGX_DLSS_SR|RR_OVERRIDE_SCALING_RATIO`; frame generation (RTX 50) Fixed → `NGX_DLSSG_MODE=2` + `NGX_DLSSG_MULTI_FRAME_COUNT`, Dynamic → `NGX_DLSSG_MODE=4` + `NGX_DLSSG_DYNAMIC_MULTI_FRAME_COUNT_MAX` + `NGX_DLSSG_DYNAMIC_TARGET_FRAME_RATE` (all prefixed `DXVK_NVAPI_DRS_`). Names older dxvk-nvapi builds do not know (v0.9.0, the first with DRS variables: both scaling ratios; Proton 10's build: the FG preset, the DLSSG mode and the dynamic settings) are also written by id in `DXVK_NVAPI_DRS_SETTINGS=0x<id>=<value>,…`, so Proton 10 or newer applies every setting; a direct launch keeps a user's own `DXVK_NVAPI_DRS_SETTINGS` in front of RHI's entries. Upstream's "NVIDIA Override" (driver's newest DLL) becomes one per-game **Newest DLSS from NVIDIA** setting: `PROTON_ENABLE_NGX_UPDATER=1` (the driver's NGX updater for Proton downloads NVIDIA's newest DLSS files into the game's prefix; RHI downloads nothing; some games may fail to start with it, which its tooltip says) plus `NGX_DLSS_SR|RR|FG_OVERRIDE=1` for the features the game ships.
- Lists: upstream 2.8.7's built-in preset lists merged with the manifest's `dlssPresets` exactly as upstream merges them (new names alphabetically before NVIDIA Recommended, `disabled` removes); before a manifest is read, the lists include the manifest entries at the wave-7 sync (SR E/F - CNN, RR F - TF2, FG C from main `d1c39d0`). A saved preset the manifest no longer lists stays shown and selected. Render-scale and frame-generation lists are upstream's (`RenderScaleOptions`, MFG dialog: 2×–6×, up to 2×–6×, Off / max refresh rate / VRR caps / custom 20–1000 FPS).
- Placement (owner's layout B): one **NVIDIA settings…** button beside Apply defaults / Restore all in the DLSS section opens one dialog with every setting (one label + control per row, Close), instead of upstream's rows and separate MFG dialog. Shown only where it can apply: NVIDIA's own driver with an NVIDIA GPU (read-only `/proc/driver/nvidia/gpus/*/information`, else a PCI display device of vendor 0x10de while `/proc/driver/nvidia/version` exists; nouveau/NVK have no DLSS; RHI never runs `nvidia-smi`), a Windows game, a DLSS file of that feature in the game; the Frame Generation preset only on an RTX 40 or newer, multi frame generation only on an RTX 50 (driver model name), both with a frame-generation file. Hidden for standalone Wine-prefix entries (started with the runner's `wine`, not the `proton` script, so Proton's NVAPI layer is not set up) and for launchers that take no variables (store clients, the app that installed an Xbox title).
- Restore all in the DLSS section also sets the game's NVIDIA settings back to Default (as upstream's Restore All clears its preset overrides), and is offered when only NVIDIA settings are chosen.
- Store hand-offs (Steam, Heroic, Lutris, other launchers): RHI applies nothing; the dialog says where to paste the launch options or variables and has a Copy button, as HDR does; RHI never edits a launcher's configuration.
- Not ported: the SR "preset override mode" companion setting 0x00634291 (not in NVIDIA's public settings header nor in NVIDIA's or dxvk-nvapi's Linux recipe), the developer-only NR presets and `dlssPresetsDev`, ReBAR, the FPS limiter and the other driver-profile pages (no launch-variable route). Global default presets (upstream Settings → DLSS defaults, written by Apply defaults): built in wave 10 (#215, below).

<!-- w7/dxvk-hdr notes -->

### #200 — DX9 HDR (DXVK HDR for DirectX 9 games)

- Ported as an opt-in per game (owner decision 2026-10-20: "the point of mods is to mod"; the fork's "Proton owns DXVK"
  rule does not apply to it). Upstream's DXVK "Lilium HDR" variant in DX9 direct mode: EndlesslyFlowering's DXVK HDR
  build (latest GitHub release, the non-gplasync `.7z`, GitHub's digest checked, extracted by the host 7-Zip/bsdtar) as
  the game's `d3d9.dll` beside the executable, upstream's DX9 preset as `dxvk.conf` beside it (six tiers, Safest by
  default, chosen in the row's ⚙; changing it rewrites the file as upstream does), and — Linux only — a per-executable
  `d3d9=native,builtin` Wine override (GE-Proton10-18 and Proton Experimental skip a game-folder `d3d9.dll` without it;
  Proton's own `d3d9=n` for its DXVK lets the game-folder copy win anyway; `PROTON_USE_WINED3D=1` defeats it:
  `reviews/w7-dxvk-hdr-wine.md`). Offered only for Windows DirectX 9 games (detected or the user's/manifest's override),
  not native, not Xbox / Game Pass; DirectX 8 is left out (upstream places only `d3d9.dll`, which a DirectX 8 game does
  not load). A `d3d9.dll` or `dxvk.conf` RHI did not write refuses the install; removal gives everything back and keeps a
  `dxvk.conf` the user changed. Update All swaps `d3d9.dll` only; the manifest's `dxvk` install warning is shown first.
- Linux deviations: the preset files leave out upstream's two async lines (`dxvk.enableAsync`, `dxvk.gplAsyncCache`:
  gplasync builds only; DXVK HDR's own presets say "only enable these on Windows"). Upstream's NVIDIA present-method
  profile step is Windows-only and not ported. HDR output needs the game's own HDR launch setting on (the row says so
  while it is Off).
- ReShade: as upstream, an RHI ReShade installed as `d3d9.dll` moves to ReShade's Vulkan layer (#92, here per game: the
  Khronos loader beside the exe, `vulkan-1=native,builtin`, the layer registered in `system.reg`) in the same journaled
  operation — the install's confirmation says so first — and goes back to `d3d9.dll` when DX9 HDR is removed (both
  records saved in one ledger write each way). DXVK HDR's `d3d9.dll` asks for `vulkan-1.dll` before `winevulkan.dll`, so
  it reaches the per-game loader (LoadLibrary-level check; the overlay itself is the owner game test). Installing ReShade
  later on a DX9 HDR game takes the layer path; removing DX9 HDR then gives it the `d3d9.dll` name. A ReShade under
  another DLL name, one loaded through ShortFuse, or one that replaced a pre-existing `d3d9.dll` is refused with what to
  do.
- Linux additions (wave-7 review): RHI's record keeps the row's Remove whatever is left on disk (a `d3d9.dll` the user
  deleted, a game no longer DirectX 9), and Remove takes back `dxvk.conf`, the override and ReShade's move. The row notes
  when the game's launch command turns on `PROTON_USE_WINED3D` (DX9 HDR then has no effect; Steam's own launch
  options are not read). DX9 HDR and Luma Framework's / DLSS5 Feeder's DirectX 9 translation (dgVoodoo2) both need
  `d3d9.dll`: each refuses while the other is installed and names what to remove. The `d3d9.dll` taken from the archive
  is capped at 64 MiB before 7-Zip extracts it (the archive's listed size) and again after.
- Linux additions (wave-7 integration): DX9 HDR is not offered on a game the RHI manifest lists as XNA (#201): XNA's
  graphics assembly loads `d3d9.dll` only from the prefix's .NET assembly folder, so a game-folder `d3d9.dll` is never
  loaded. An XNA (GAC) ReShade is never moved to the Vulkan layer, even once the manifest no longer lists the game, and a
  listed XNA game's ReShade row keeps the GAC path beside an older DX9 HDR record. A ReShade DX9 HDR moved to its layer
  has no DLL name to change (#196): the locked dropdown says to remove DX9 HDR first.

## Parity notes (wave 8: RenoDX Engine.ini parity, journaling follow-ups)

<!-- w8/reshade-ini notes -->

### #213 — Presets and the Luma ⚙ journal a linked ReShade.ini at its target

- Fork-only (journaling; upstream has none). A preset's install and removal and the Luma ⚙ "HDR on first boot" journal
  the file a ReShade.ini write through a link replaces (the user's shared `~/.config/ReShade/…` file, under the one
  ReShade.ini link rule), and the Luma ⚙ TAA the file a linked Engine.ini leads to (same prefix or UE project), never the
  link: a failed save or a crash keeps the link a link and puts the shared file back byte for byte. A preset install into
  a game whose ReShade.ini links somewhere RHI does not write is refused before anything changes; a preset removal there
  still takes the preset out and leaves that file alone.

### #206 — Apply to all games and presets record RHI's ReShade.ini writes

- Fork-only (journaling and ownership; upstream records nothing). Where RHI installed ReShade, each game of **Apply to
  all games**, Settings' **Apply to the selected game** (the same method) and a preset's install and removal is one
  journaled operation on ReShade's record: every file (ReShade.ini and its numbered siblings) is tracked at the file the
  write replaces before anything is written, and the write is recorded as Deploy ReShade.ini's is (#203): the file's new
  hash counts as RHI's only while the file still held RHI's last write. So removing ReShade still deletes a ReShade.ini
  RHI created that nobody changed since (before, an Apply or a preset made RHI keep it as the user's), keeps one the user
  changed, and from a ReShade.ini that was there before RHI (or the user's shared `~/.config/ReShade` file) takes only
  RHI's search paths back: the screenshot folder, hotkeys and effect list style the user applied stay, as after Deploy
  ReShade.ini. An update or shader change that regenerates RHI's unchanged file from the template keeps the preset RHI
  selected (kept on ReShade's record). A crash before a game's record is saved puts that game's files and record back;
  one after it keeps both. Cancel stops between games; a game whose journal cannot be written is counted as skipped.

### #214 — XNA GAC ReShade.ini BasePath follows a library move

- Fork-only (upstream links the GAC files and has no Repair after move). Right after Repair after move re-points an XNA
  game's records, RHI's ReShade.ini copy in the prefix's .NET assembly folder gets `[INSTALL] BasePath` set to the game's
  new folder as the game sees it (the install's own mapping), journaled on ReShade's record with the new hash, so ReShade
  keeps finding its shaders, presets and settings after a library move without an update. Only while that copy still
  holds RHI's last write: a copy the user (or ReShade) changed is kept, and the Repair line names that file and says to
  set BasePath in it to the new folder (ticket option 2 named "update ReShade", which leaves a changed copy as it is).
  While Wine runs for the prefix the rewrite (not the repair) is skipped and the line says to use Apply / Update in the
  ReShade section once the game is closed; a copy that cannot be read or rewritten gets the same advice with the reason.
  The repair itself is never undone: these notes ride the app's "records now point to the new folder" line and go to
  `rhi.log`. A crash during the rewrite leaves the old or the new file and a record that agrees.

<!-- w8/renodx notes -->

### #205 — RenoDX install, update, local drop and removal are journaled

- Fork-only (upstream writes RenoDX's files without a journal). The RenoDX install and update (Update All's included), the local drop (#195) and the removal each run as one journaled operation on RenoDX's record, like every other component since wave 3: the add-on (verified copy), its `[renodx]` keys and peak brightness in ReShade.ini, the upstream Engine.ini template, RHI's earlier add-on under another name (now removed before the record is saved, so a failure brings it back) and the Control UE steps (DLSS DLL swaps with their `.original` and earlier copies, renderer.ini's HDR preset). The saved record is the commit point (for a removal: the record leaving the ledger). A failure before it puts every file back in process; a crash puts them back at the next start. The in-process byte snapshots and their `restore-snapshots` cache folder are gone (one undo, not two).
- A ReShade.ini that links to the user's shared file (`~/.config/ReShade/…`) is journaled at the shared file, never the link (the one link rule): a rollback keeps the link and puts the shared file back byte for byte. RenoDX peak brightness (⚙ "Set maximum nits", Apply to all games) does the same (#213 audit).
- A read-only Engine.ini keeps its lock through a rollback or crash recovery: the journal puts the file's mode back after its bytes, but only while the file holds its bytes from before the operation (a file changed after a crash keeps its own mode).
- A Control UE file that cannot be put back during removal (a locked renderer.ini) now undoes the whole removal: RenoDX stays installed with every file as it was (earlier builds kept the files already restored and narrowed the record), and a retry finishes once the file can be written.
- The ⚙'s own RenoDX settings (`SetCompatibilitySetting`: the `[renodx]` values chosen from the dialog's dropdowns) stay the user's writes and are not journaled: they never change a key RHI owns as such (a key RHI wrote and the user then changes is the user's from then on, and removal keeps it).

### #217 — Engine.ini colour-grading line for RenoDX on Unreal games

- Ported (upstream `AuxInstallService.ApplyEngineIniLutSetting` / `RemoveEngineIniLutSetting`, the RenoDX ⚙ "LUT Update Every Frame"): RenoDX's install, update (Update All's too) and a dropped add-on write `r.LUT.UpdateEveryFrame=1` under `[/Script/Engine.RendererSettings]` in an Unreal game's Engine.ini (manifest engine hint), unless the manifest's `ueExtendedCompatibility` says `lut: false`, a custom Engine.ini file (`engineIniFiles`) manages Engine.ini, or the user turned it off. The RenoDX ⚙ has **Fix colour grading updates (Unreal)** (Off / On, default On; the key only in its ~2 s tooltip), shown only where the line applies: Off takes RHI's line out, On puts it back, and updates follow the choice (kept on RenoDX's record). As upstream, Engine.ini is made read-only once RHI's line is in, so the game cannot rewrite it on launch.
- Linux deviations: the line is owned per key on RenoDX's record inside RenoDX's journaled operation (#205): a line the file already has (any value, any section, as upstream checks) is the user's — never changed, never removed, and the file is not locked for it (upstream locks the file anyway); removal and Off take out only RHI's line, and only while it still holds RHI's value, and put back the lock state the file had before (a file the user had locked stays locked; one RHI locked is unlocked; an Engine.ini RHI created and left empty is deleted, as upstream). Upstream's Engine.ini resolver creates `%LOCALAPPDATA%\<Project>\Saved\Config\Windows` when nothing exists yet; Linux writes only where an Engine.ini location is proven (an existing `Saved/Config/<platform>` folder in the prefix's `AppData/Local/<project>` or `Documents/My Games/<game name>` — as upstream, for UE4 games such as Borderlands 3 — or in the game folder, or the manifest's project override once the game has created that folder), the same resolver as the Engine.ini template (which therefore now also finds `My Games` configs) — otherwise nothing is written and the ⚙ row is hidden until the game has run once. A dropped add-on follows the user's ⚙ choice and the manifest too (upstream's drop writes the line on every Unreal game). As upstream, the line is optional: an Engine.ini RHI cannot follow (a link outside the prefix), read or write never fails RenoDX's install, update or drop — RenoDX goes in, Engine.ini is left as it was, the result says the line was not written and why, and rhi.log has it (upstream only logs it); such a file hides the ⚙ row unless RHI's line is in it. Removal and Off give the file back byte for byte: only RHI's lines come out, a section header (and the blank line before it) only where RHI added it, and the file's line endings, encoding and a missing final newline are kept (upstream drops every empty section header, trims trailing blank lines and rewrites the file with Windows line endings).

### #218 — UE-Extended: native-HDR keys, Engine.ini HDR keys and the ⚙ HDR row

- Ported (upstream `ApplyRenoDxNativeHdrSettings`, `ApplyEngineIniHdrSettings`, their removals, `RenoDXDbUnrealEntry.Method`/`Upgrades`, the ⚙ "Engine.ini HDR"): a UE-Extended RenoDX install or update fills in UE-Extended's `[renodx]` keys in ReShade.ini (`DumpLUTShaders`, `ForceBorderless`, `FxUpgradeRender`, `PreventFullscreen`, `SettingsMode`, `Set_Path` and the empty `Upgrade_*` keys) where the section lacks them; `Set_Path` is 1 (SDR path) when upstream's UE-Extended database (`RenoDXdb-unreal.json`) has no method or `upgrade` for the game, 0 for `native`/`ini`, and without an entry 1 on Unreal Engine 4, 0 otherwise; an `upgrade` entry's key (`Upgrade_<format>` = 1/2/3 for Output size/ratio/Any size) is written whatever is there. Where the entry says `ini` — without an entry where the manifest's `ueExtendedCompatibility[game].hdr` says so, else on every game but Unreal Engine 4 — and no custom Engine.ini file is used, Engine.ini gets `r.AllowHDR=1`, `r.HDR.EnableHDROutput=1`, `r.HDR.Display.OutputDevice=3`, `r.HDR.Display.ColorGamut=2`, `r.HDR.UI.CompositeMode=1` under `[SystemSettings]` (missing ones only), and the RenoDX ⚙ gets **Turn on the game's HDR output (UE-Extended)** (Off / On, default On; the keys only in the ~2 s tooltip), shown only on such games. Switching UE-Extended off in the ⚙ takes its keys out again (upstream does the same on its toggle).
- Linux deviations: every key is owned per key on RenoDX's record inside RenoDX's journaled operation (#205): removal, Off and switching UE-Extended off take out only keys RHI added and that still hold RHI's value (upstream removes the whole `[renodx]` section and every HDR key, whoever wrote them), and the Engine.ini lock state is put back as for #217 (upstream leaves the file writable after its HDR removal). `r.LUT.UpdateEveryFrame`, part of upstream's HDR list, is #217's own line and setting here, so HDR Off leaves it. The database is read like the ⓘ dialog's (cached, last good copy; an unreadable database falls back to the manifest / engine rule); upstream's developer-only "database source" setting is not ported (the database is always used). The ⚙ row reflects the last install's decision (a database change shows after the next update).

<!-- w8/commit-point notes -->

### #204 — A ledger write that lands and then reports an error is finished, not undone
- Fork-only crash safety (upstream has no journal). When the save that commits an install, update, change or removal
  reaches disk and then reports an error (e.g. the folder sync after the new `deployments.json` is in place), RHI reads
  the ledger file again — never its cache — with the rule start-up recovery uses: if the save landed, the operation is
  finished as committed (nothing is rolled back: files, Wine overrides, DLL renames, ReShade's DX9 translation and the
  second record of a two-record save all stay) and the result reads "Done; the deployment ledger reported an error after
  the change was written, so nothing was undone: <component>" (or "Removed; …"); if it did not land, everything is rolled
  back as before. If the ledger cannot be read (damaged, no permission, a disk error): when no save of the operation got
  as far as replacing the file, it is rolled back at once as before; otherwise nothing is undone now and the change waits
  — RHI finishes or undoes it before the next install, update or removal on that game in the same session (as soon as the
  ledger reads again; until then that game's next change is refused: "RHI has not yet finished or undone an earlier change
  to this game… Restart RHI to finish it, then try again." — #221), or at the next start. The row, Update All and the update history then match the ledger (a DX9
  Feeder/Luma install used to leave ReShade's record naming `dxgi.dll` while ReShade was back at `d3d9.dll`, so every
  later removal failed).

### #213 — Luma and DLSS5 Feeder journal the file behind a linked ReShade.ini
- Luma Framework's install and removal and the DLSS5 Feeder's install and removal journal the file a ReShade.ini link
  leads to (the user's shared file in `~/.config/ReShade`), never the link: a failed or interrupted install keeps the link
  a link and puts the shared file back byte for byte. Luma's own ReShade.ini write and the Feeder's preset edit still
  refuse a linked ReShade.ini (unchanged), so such an install fails and is rolled back cleanly; the Feeder's rollback used
  to replace the link with a plain copy and leave its search paths in the shared file.

<!-- w8 integration notes -->

### #204 / #213 — integration of the wave-8 lanes
- Every journaled operation the other wave-8 lanes added or changed follows #204: RenoDX install, update, dropped add-on,
  removal, ⚙ colour-grading and HDR switches and peak brightness, RenoDX Upgrade, Control UE's `FgSlHdr10`, ReShade
  presets, the per-game Apply and **Apply to all games**, Deploy ReShade.ini and add-on INIs, the XNA GAC ReShade.ini
  rewrite after a move and the Luma ⚙. A commit write that lands and then reports an error is reported as done.
- The Luma ⚙ TAA switch tracks `Engine.ini` through RenoDX's tracker (one resolver; the file's Unix mode and any folder the
  write creates are journaled), so a rolled-back change keeps a read-only `Engine.ini` read-only.
- DX9 HDR's ReShade.ini write (when the game has none) tracks the link-rule target like every other ReShade.ini writer.
  The audit's narrow case (a dangling link to a shared file not made yet) does not occur: .NET counts a dangling link as
  an existing file, so no ReShade.ini is written there.

## Parity notes (wave 10: DLSS default presets, archive offer, journal holds)

<!-- w10/dlss-presets notes -->
### #215 — Global default DLSS presets (Settings → DLSS/Streamline, Apply defaults, DLSS batch)
- Upstream (RHI-2.8.7): `DlssDefaultsDialog` holds `DefaultSrPreset/DefaultRrPreset/DefaultFgPreset` beside the version
  defaults; Apply defaults (`DetailPanelBuilder.Overrides.NvidiaProfile.cs`) writes them into the game's driver profile
  after the DLL swaps (SR not for a DLSS 1.x `nvngx_dlss.dll`, RR likewise for `nvngx_dlssd.dll`, FG when the game ships
  dlssg); `hasDefaults` also counts a non-Default preset; the Settings summary names them; `MassDlssDeployDialog` puts
  three preset dropdowns (None + the lists, pre-filled from the defaults) under the versions after a divider; its
  Restore resets the three presets to Default and clears the NVIDIA driver override on every ticked game.
- Linux (owner decision 2026-10-21: upstream's way): Settings → DLSS/Streamline shows a "Default presets" group with the
  three lists (the NVIDIA settings' own lists and labels, Default first; a saved value the manifest no longer lists stays
  shown). Apply defaults copies each non-Default default into the selected game's own NVIDIA settings (the per-game
  launch profile of #211), only into a row that game's NVIDIA settings… dialog shows (`NvidiaLaunchPlan.PresetRows`: NVIDIA
  GPU, a launch RHI or the store passes variables to, the feature's file, FG preset on an RTX 40 or newer) and never over a
  DLSS 1.x file (`LinuxDlssDefaultsService.NvidiaPresetRowsFor`, read from the DLL's version resource). A copied preset
  replaces the game's own (upstream sets it unconditionally); a Default default writes nothing. Apply defaults is offered
  when only preset defaults are set (upstream `hasDefaults`). The status line names what was set and what was not, and why.
- DLSS batch: three preset dropdowns ("Do not change" first, pre-filled from the defaults) under the versions after a
  1 px divider; Apply copies them into every ticked game (also games other than the selected one) by the same row rule,
  in upstream's order: game by game, that game's DLL swaps, then its presets. Cancel stops between games (a game whose
  swaps ran gets its presets too; games not reached keep theirs) and the line names the games that got presets. The
  line counts the games set and those skipped, by reason (row not offered / DLSS 1.x file). Restore selected follows
  upstream's mass restore, game by game: that game's DLLs back, then its three presets back to Default and "Newest DLSS
  from the driver" (upstream's NVIDIA Override) off; render scale and frame-generation settings stay.
- Restore all (one game) already set every NVIDIA setting of the game back to Default (#211) and so clears the copied presets.
- Not ported: a launch-time fallback (owner rejected option 2: a game whose preset is Default never picks up a global
  default); upstream's NR preset default (developer-only list), the render-scale and driver-override defaults (no
  Settings rows on Linux yet), the batch's "Auto-create NVIDIA profiles" box (no driver profile under Proton).
- Also fixed: the DLSS 1.x guard of the Super Resolution swap (Apply defaults, batch) read the version through
  `FileVersionInfo`, which reads no PE resource on Linux, so it never fired; it now reads the version resource.

<!-- w10/archives notes -->

### #216 — Dropped or watched archives: the add-on inside is offered for the same confirmed install

- Upstream (`DragDropHandler.ProcessDroppedArchive`, `AddonFileWatcher`, `MainWindow.HandleArchiveFile`): a dropped
  archive (and a watched `.zip`/`.7z`/`.rar` whose name holds `renodx` or `luma`) is extracted whole by 7-Zip into a temp
  folder (60 s limit); the `.addon64`/`.addon32`/`.addon` files whose name starts `renodx-` or holds `Luma` are the
  candidates; none → "No Addon Found"; several → a picker; a Luma one sends the original archive to the Luma package
  install on a Luma-capable game, anything else goes to the dropped-add-on install. The temp folder is deleted
  afterwards. A watched Luma archive is deleted from the watch folder; other archives are kept.
- Linux, inspect before anything is extracted (fork-only safety): RHI copies the archive into a private mode-0700 folder
  under its cache (`~/.cache/rhi-linux/dropped-archives/<id>`, never the watch folder or beside the archive), hashing it
  as it copies (1 GiB cap); everything later reads that copy. The copy is listed — a `.zip` by RHI itself, a `.7z`/`.rar`
  by the host 7-Zip (`l -slt`) or bsdtar (`-tvf`), read strictly — and the whole archive is refused, before anything is
  extracted, when any member has an absolute path, a `..` or empty part, is listed twice, is a link or a device, has a
  name with control characters, or when an add-on lists more than 256 MiB, all members together more than 2 GiB, or more
  than 100 times the archive's own size above 16 MiB (a zip bomb). Backslash names are read as folders
  (`OptiScalerPackageProvider.NormalizeBackslashEntries`' rules). Nested archives are never opened. Only the add-on chosen
  for the install is extracted (a zip entry through the bounded, CRC-checked `ReShadeArchiveExtractor.ExtractEntryAsync`;
  a 7-Zip/bsdtar member by its literal name), and it must come out as exactly one plain file of the size the archive
  listed (a header can lie). What 7-Zip or bsdtar may write is bounded while it runs (#216 review): every file by a
  file-size limit the kernel enforces (`ulimit -f` in a `/bin/sh` that `exec`s the tool), all of them together by a
  watcher that stops the tool's process tree; the bound is the size the listing gives, so an archive that unpacks to more
  (a gzip stream behind a `.7z` name lists its trailer's size) is stopped there and refused, never after it has filled
  the cache. Every other host-tool extraction gets a 2 GiB bound. A zip entry is never read past its listed size. A file
  that changes while RHI copies it (a download still being written) is refused. The private folder is deleted after
  the install, a refusal, a failure or Cancel; folders a crashed or killed RHI left are removed at the next start (each
  folder names its process and that process's start time, so another running RHI's folder stays).
- Linux, candidates as upstream through the dropped-file rules: a RenoDX game add-on becomes the game's RenoDX mod
  (`InstallLocalRenoDxAddonAsync`), another `renodx-` add-on a dropped file would install goes in beside it through
  Shaders/Add-ons (`InstallDroppedAddonForGameAsync`, the #179 choice rules included), a Luma add-on sends the whole
  package to the Luma package install (the same install warnings as a dropped Luma package): RHI's copy of a `.zip`, or
  a `.7z`/`.rar` taken out whole with the same checks and caps and zipped again inside RHI's private folder (the Luma
  install reads a `.zip`; the user's file is never touched); what comes out must be exactly the files the listing gives.
  Names a dropped file would only import into Custom Add-ons are not offered; an archive with no add-on keeps a status
  line that says so and nothing is imported. Every refusal of the dropped-file path is unchanged and is asked before
  anything is extracted; extraction happens inside the operation, before its first game write, so Cancel stops it and
  leaves the game as it was.
- Linux, one dialog: the existing drop dialog names the archive and the add-on; with two or more add-ons an **Add-on**
  dropdown sits above the game dropdown (upstream shows a separate picker first). Each add-on keeps its own game list and
  match (its file name, then the archive's name, then the selected game).
- Linux deviations: a `.zip` named for Luma, dropped or watched, goes through the same offer and dialog as every
  archive (one dialog for every dropped or watched archive; upstream likewise routes it through `ProcessDroppedArchive`);
  a Luma `.zip` downloaded through Nexus keeps the Luma package prompt for the selected game (#227). The watcher raises a file only once its size and change time have stayed the
  same for about a second (a download still being written is never offered or removed). A watched archive directly in the watch folder is removed after a
  successful install, but only while it still holds the bytes RHI read (SHA-256 of RHI's copy against the file right
  before the removal; a newer download under the same name stays and the status line says so); upstream keeps renodx
  archives and deletes Luma ones. A dropped archive is never removed. Without 7-Zip or bsdtar a `.7z`/`.rar` is refused
  with the existing "needs 7-Zip or bsdtar" line (upstream ships 7-Zip); a `.zip` needs neither.

<!-- w10/journal-holds notes -->

### #221 — The unreadable-ledger hold says plainly to restart RHI
- Fork-only wording (behaviour unchanged since #204): while the deployment ledger cannot be read after a change failed,
  that game's next change is still refused and other games carry on. The refusal now leads with the restart — "…Nothing
  was changed. Restart RHI to finish it, then try again." — and the note a failed change adds says "…so nothing was undone
  now. Restart RHI to finish it." (PT-BR: "Reinicie o RHI para concluir."). The old "Repair the ledger" advice is gone.

### #220 — A game whose undo could not be finished is held until the undo succeeds
- Fork-only crash safety. When RHI cannot finish undoing a failed or interrupted change (the game's folder or a file is
  not writable, say), the journal entry is kept for the next start as before, and now that game is held: in the same
  session (the failed undo of a change that just failed) and after every start (start-up recovery's undo failed), the
  game's next install, update or removal first tries the undo again; while it still fails the change is refused before
  anything changes — "RHI could not finish undoing an earlier change to this game, so it makes no new change here.
  Nothing was changed. Restart RHI to finish it (first check that RHI can write to the game's folders and files; rhi.log
  names the file that failed), then try again." Other games carry on. The hold lifts as soon as the undo succeeds (at
  that next change or at a restart). A retry used to go ahead and commit, and the start that finally finished the old
  undo then put the files back under the retry's record.
- A change whose undo of a Wine setting was deferred (Wine was running) also retries it before the game's next change;
  it is never refused for it (the next start finishes it, as before). A plain file change that names no game holds up no
  game.
- Only a failure a retry can fix holds the game. When RHI's saved copy of an earlier file is gone (deleted, or the game
  was reinstalled), no restart can put it back: the change is closed and the status line says which file RHI could not
  put back, why, and to check that game's mods (Remove, then Install again). A step this build does not know (written by
  a newer RHI) is left in the journal untouched for that build, logged, and holds nothing. While a copy cannot even be
  looked for (a folder refuses RHI), the game stays held.
- A refusal gives the true reason: when RHI cannot read or update its own note of the earlier change (its data folder),
  it says so instead of blaming the ledger; such a note holds only its own game when that is known, else none.

### #222 — The "saved, but the save reported an error" note on the last few lines
- Fork-only (follows #204's landed note). A preset installed into every game together with its shader packs names a game
  whose preset save reported an error after it was written, whether the packs then fail or go in. When ReShade's install
  applies the game's add-ons after it (the ReShade row, Shaders/Add-ons Apply on a game where RHI does not manage ReShade
  yet, a preset with its packs on such a game) and the add-ons' save reports such an error, the line says so too. A
  "Shaders applied, but the add-ons failed" line keeps the note of a shader-pack change or ReShade install that was saved
  with an error.

## Parity notes (wave 11: wizardkof v0.9.25 — NR DLL per card generation, F5 driver warning, Nightly switch, download idle timeout, NR check)

<!-- w11/download-idle notes -->
### #233 — A download that stops sending data gives up after an idle time

- Source: wizardkof/RHI-Linux v0.9.25.0-beta.1 (`26e5259`, PAR-D111): its OptiScaler package provider gives each
  download a 5-minute total deadline and serialises stagings with a per-provider lock. Ported behaviourally, with an
  IDLE limit instead of a total deadline: a big file on a slow but steady link still finishes.
- Every HTTP body RHI reads through `TrustedDownload` (payloads via `DownloadToTempAsync`/`EnsureVerifiedAsync`, metadata
  via `GetStringAsync`/`GetBytesAsync`/`PostJsonAsync`) is wrapped in `IdleTimeoutStream`: each read has its own timer of
  `TrustedDownload.IdleLimit` (60 s with no byte received). A stall throws `DownloadStalledException`, an
  `HttpRequestException` (never an `OperationCanceledException`), so every existing "could not reach" rule treats it as a
  network failure: an Update All check reads "could not be checked", an update fails for that game and the pass moves
  on; the user's Cancel still reads "Operation cancelled.". Message, EN and PT-BR: "The download stopped: no data arrived
  for 60 seconds. Source: OptiScaler" / "O download parou: nenhum dado chegou durante 60 segundos. Origem: OptiScaler".
- GitHub API answers: the app's `HttpClient` has `GitHubConditionalCacheHandler` (#35) in front, which reads every
  api.github.com answer that carries an ETag or Last-Modified (GitHub always sends one) inside `SendAsync`, before
  `TrustedDownload` sees it. That read is itself under the same idle limit (correction round): a stalled release lookup
  gives up after 60 s with the message above ("Source: GitHub API"). The handler holds at most 8 MiB + 1 byte; a longer
  answer is not cached and reaches the caller whole, still under the limit.
- A stalled download's temp file is removed and no cache entry or stamp is written; downloads stay before the write gate,
  so nothing reaches a game folder.
- Per gap, never a total deadline (owner rule): a server that keeps sending a byte every 59 s is never stopped by the
  idle limit (a payload then runs until its size cap). The manual Cancel works throughout the download. A
  minimum-throughput floor is follow-up #235 (no milestone).
- Not covered by the idle limit: the wait for the response headers, bounded by `HttpClient.Timeout` (the app's client:
  5 min; the GitHub token check and Nexus Mods clients: 30 s), and a GitHub API answer that keeps trickling for longer than
  that 5 min in all. On `TrustedDownload` paths and the Nexus Mods client that timeout reads "The server took too long to
  answer: RHI stopped waiting after <timeout> seconds. Source: <policy name>" — "… after 300 seconds. Source: GitHub API" /
  "O servidor demorou demais para responder: o RHI parou de esperar após 300 segundos. Origem: GitHub API" on the app's
  client, "… after 30 seconds. Source: Nexus Mods API" on the Nexus Mods client (`DownloadTimedOutException`, a network
  failure), never .NET's "The request was canceled due to the configured HttpClient.Timeout …"; the token check shows its
  "could not check" line. The Nexus Mods client's 15-s connect limit arrives in the same shape but reads "Nexus Mods could
  not be reached." (w11 follow-up E1: the host was never reached, so the client's 30 s are not the time waited).
- Other body readers: the Luma wiki page, the GitHub token check and the Nexus Mods API answers now read under the same
  idle limit (they had no bound on the body); the RenoDX Unity list (30 s), upstream manifest (15 s), PCGW central data
  (60 s), DLSS manifest (15 s) and PCGW API reads run under a timeout token for the whole body instead; reshade.me's page,
  the RenoDX catalogue and the MOTD are buffered inside `HttpClient`, so only its 5-minute total timeout bounds them
  (reshade.me reports "did not answer in time"; the catalogue and MOTD fall back quietly).
- Two stagings of one OptiScaler package cannot run at once: a manual install and the background pass take the same busy
  gate. A second copy would also be harmless in `TrustedDownload` (unique temp names, atomic rename, stamp written from
  the file in place), so no cache-entry lock was added (wizardkof's provider lock is not ported).


<!-- w11/nr-gpu notes -->

### #229 — "Latest" Neural Rendering DLL = the newest one made for your card generation
- Source: wizardkof/RHI-Linux v0.9.25.0-beta.1 (`26e5259`, `DlssNrGpuPackagePolicy`), re-implemented under the owner's
  decision of 2026-10-22; upstream RankFTW 2.8.7 always takes the manifest's first NR entry.
- RHI reads the card from the NVIDIA driver's model names under `/proc/driver/nvidia/gpus/*/information` (read-only):
  GeForce RTX by number, workstation cards by family (Turing "Quadro RTX"/"TITAN RTX" = 20, Ampere "RTX A…" = 30, "Ada"
  = 40, "Blackwell" = 50); two GeForce cards by their chip (RTX 2050 = Ampere = 30, RTX 3050 A Laptop = Ada = 40). One
  card's unreadable information file leaves out that card only. With several NVIDIA cards the newest generation counts. No NVIDIA driver, no model, a card
  from before RTX or a name RHI does not know = unknown. Never `nvidia-smi` (wizardkof) and never the DLL's CUDA fatbin
  list (the real `310.8.3 (50xx)` DLL lists sm_75/86/89/120, so a fatbin check passes the wrong file).
- A manifest NR entry fits the generations its label names (`(50xx)`, `(20/30/40/50)`, `(RTX 40 Series)`…); an entry
  without a label RHI can read fits every card (it cannot be judged, so it is never hidden — wizardkof never offers it).
  "Latest" = the first fitting entry in manifest order (the manifest lists NR newest first).
- Every NR list shows only the fitting entries: the NR DLL dropdown, the DLSS panel's NR swap list, Settings → DLSS
  defaults, the DLSS batch. Every "Latest" install follows it: OptiScaler DLSS NR / F5, ShortFuse, DLSS5 Feeder, the NR
  DLL swap with no pin, and a pin no longer published. An unknown card lists everything, newest first, as upstream (never
  blocked, unlike wizardkof), and the NR DLL dropdown's tooltip says RHI could not tell the card.
- A hand-picked per-game NR pin stays selected and is installed as chosen even when hidden from the list (#230 adds its
  row note). A saved NR default (Settings) or a DLSS batch NR choice made for another card is skipped for that game with
  a line, before any file changes; its other DLSS steps still run (wizardkof refuses the whole game).
- RTX Encore keeps its own fixed 310.8.0 runtime whatever the card.
- The NVIDIA driver version is now read by the same probe (moved from the HDR service); it also reads the open kernel
  module's line (`NVIDIA UNIX Open Kernel Module for x86_64  615.78.08 …`), which the old pattern missed.

### #230 — Update All moves "Latest" Neural Rendering installs onto the DLL made for your card
- Source: wizardkof/RHI-Linux v0.9.25.0-beta.1's NR update check (`26e5259`), re-implemented under the owner's decision
  of 2026-10-22 (item 2); fork-specific because the fork has per-game NR pins.
- ShortFuse and DLSS5 Feeder Full: with no NR pin, the update check reports an update when the installed
  `nvngx_dlssnr.dll` is not "Latest" made for the card, and the update swaps only that DLL in place through the existing
  journaled NR swap (logged in the update history as "… (nvngx_dlssnr.dll)"); the add-on part of the check and update works
  as before. Their earlier update path never touched the NR DLL (it swaps the add-on only). OptiScaler DLSS NR / F5 keep
  their own runtime step (its check already compares the installed runtime with "Latest", now the one for the card).
- Which version is installed: the record's own note (`NeuralRendering.DllVersion`, now written by every ShortFuse and
  Feeder install; OptiScaler's runtime-version key); for older records (Borderlands 3's) the record's SHA-256 of the file
  matched against RHI's stamped NR cache entries, else the DLL's PE version mapped to exactly one manifest entry. None or
  several = "could not be checked", never "current", never a blind swap. Before a move RHI checks the file on disk still
  has the record's SHA-256 (behind the NR Cost Scaler: `nvngx_dlssnr_real.dll`): a DLL the user replaced or removed by hand
  is "could not be checked" with its own line and left as it is, never an update the swap would refuse on every run. The
  update history's "from" of an older record is the version told at swap time.
- A version picked by hand is never changed; when it is made for another card generation the component row says so.
  Unknown card: nothing moves because of the card. Update All exclusions, the NR Cost Scaler hold ("held", never a
  failure), ReShade No Addons holds, journal holds and the "modified outside RHI" refusal of the swap all still apply.
- The NR DLL swap (by hand or by Update All) now says so when its record save reported an error after the write landed.

### #231 — OptiScaler F5 DLSSNR: warn and ask below Linux driver 615.71.09 or with a 310.8.0 NR DLL; two package checks
- Source: wizardkof/RHI-Linux v0.9.25.0-beta.1 (`26e5259`: `OptiScalerF5DriverPolicy`, a hard refusal below 616.56 read
  through `nvidia-smi`; `OptiScalerPackageProvider.ValidatePackageRoot`). Re-implemented under the owner's decision of
  2026-10-22: warn and ask to confirm, never refuse.
- Threshold: Linux NVIDIA driver **615.71.09**, the first Linux driver of NVIDIA's R615 branch and the Linux equivalent of
  F5's Windows minimum **616.56** (coordinator decision 2026-10-22 from `reviews/nvidia-driver-numbering.md`): Linux and
  Windows share the branch, not the minor numbers, so wizardkof's literal 616.56 would refuse every Linux driver released
  so far (the owner's 615.78.08 included); 610.57.04's NGX core has no DLSSNR, 615.71.09's has. Compared number by number
  (`major.minor.patch`). The version comes from `/proc/driver/nvidia/version` (one reader, `NvidiaGpuProbe`), never
  `nvidia-smi`.
- Every user-started F5 install (a reinstall too) shows the existing confirm dialog when the driver is below 615.71.09,
  unreadable, or NVIDIA's driver is not loaded — naming both numbers ("615.71.09 or newer on Linux (616.56 on Windows)") and
  the version found — with "Install anyway" and Cancel; at or above it there is no dialog. Declined: nothing changes and the
  status line says the install was cancelled. Other OptiScaler channels never ask.
- Second warning, same dialog: the `nvngx_dlssnr.dll` F5 will stage is 310.8.0.x on a 615-or-newer driver (it crashes
  there; 310.8.2 or newer is needed) — read from the cached DLL's version resource, else the manifest label. The same note
  shows on the row of any NR owner (ShortFuse, DLSS5 Feeder, OptiScaler DLSS NR / F5) whose installed NR DLL is 310.8.0.x
  on such a driver.
- Update All (manual and background) never asks and never refuses; when it took an F5 install in while the driver is below
  615.71.09 or unreadable, its status line says so.
- F5 package checks: `nvngx.dll_dlssnr.dll` must be a Windows DLL (MZ and a PE header), and the package must hold its
  `OptiScaler/` helper folder (after the backslash names are rebuilt); otherwise it is refused before any game file changes.

<!-- w11/nightly-switch notes -->

### #232 — OptiScaler Nightly: change an installed Nightly's build in place

Source: wizardkof v0.9.25.0-beta.1 (`26e5259`) "Apply selected build now" (inventory PAR-D109), and upstream RankFTW 2.8.7
`MainWindow.Events.Components.cs:2677-2696` (the build dropdown stays enabled while Nightly is installed).

- The OptiScaler ⚙ **Nightly build** dropdown stays enabled while OptiScaler Nightly is installed. Choosing a build saves
  the choice (as before); while it differs from the installed build an **Apply** button sits beside the dropdown (under it,
  right-aligned, when the dialog is narrow) and the note line says which build is installed. Apply runs under the busy
  gate, with its own Cancel while the build is still downloading (the main window's Cancel is behind the dialog); the
  outcome shows on the status line and under the row.
- The switch is OptiScaler's own update transaction with the chosen package (`OptiScalerDeploymentService.SwitchNightlyBuildAsync`,
  `LinuxManagedComponentService.ApplyOptiScalerNightlyBuildAsync`): the same "modified outside RHI" refusal before any
  download, the same journal and rollback, the same DLL name, `OptiScaler.ini` (never overwritten), OptiPatcher and
  Streamline stack handling. Switching to a date pins the game (`OptiScaler.NightlyBuild`); switching to Latest drops the
  pin, so Update All follows the newest build again. It is logged in the update history.
- The record's pin, the files and the saved choice always end on the same build: the choice is put back on the installed
  build before anything is downloaded and written with the new record (tracked by the journal, before the commit
  point), so a refusal, a failed download, a failed save, a Cancel or a crash leaves the installed build with the choice
  back on it (as the ticket asks; upstream keeps the new choice and stages it for the next install).
- Update All is unchanged: a pinned Nightly stays on its build, and a Latest Nightly updates to the newest build, never to
  the saved choice.
- Control Ultimate Edition's frame-generation install (Nightly `20261004` as `winmm.dll`, #136) keeps its build: on that
  game the dropdown stays locked with a note saying why, there is no Apply, and the service refuses the switch.
- Differences: wizardkof applies on top of its Update path and refuses while the game runs; here, as for the fork's other
  single-game installs, updates and renames, there is no running-game check (Update All still skips running games). Files
  the old build had and the new build lacks stay, owned by the record, as after any OptiScaler update.

### Wave 11 integration — held games on the new update paths

- The w10 rule "a service that checks the game's files before its journaled operation asks the hold first" now also
  covers OptiScaler's update transaction (Update All and the in-place Nightly switch, #232), the shared in-place file
  swap (an NR version swap, add-on version swaps) and Update All's NR move (#230): a game held after an undo RHI could
  not finish gets the restart text, never "modified outside RHI" or "changed outside RHI" for RHI's own half-undone
  file. Before asking, the hold is settled (the undo is retried); an undo that now finishes puts RHI's file back, so the
  NR move checks the file again.
- Follow-up: OptiScaler's removal asks the hold too, before its own files' and the chained `ReShade64.dll`'s hash checks
  (a held game got "Managed ReShade64.dll was modified outside RHI"; pre-existing since wave 3).
- A stalled download (#233) inside the NR move or a Nightly switch happens before the first write: that game's line is
  the stall message, and the DLL, the record and the saved choice stay as they were. A Nightly owns no NR DLL, so the NR
  move and the switch never touch each other's files.

<!-- w11/nr-check notes -->

### #234 — Check Neural Rendering: a read-only page per game

Source: wizardkof v0.9.25.0-beta.1 (`26e5259`) DLSS5 trial advisor (`Dlss5TrialAdvisor.cs`, `MainWindow.Dlss5Trial.cs`;
inventory PAR-D108), which credits Mhsbrian/DLSS5oneclick-forlinux (MIT). Behavioural port; no code taken.

- Owner pick (2026-10-22): **both** entry points open one page — a **Check…** button right after the detail panel's
  Neural rendering recommendation (it wraps under the text when the line is narrow) and **Check Neural Rendering…** as
  the card menu's last item. Both are hidden where the page has nothing to show: no NVIDIA card, a native Linux game, no
  confirmed executable, or neither a Neural Rendering DLL nor a Neural Rendering method (ShortFuse, DLSS5 Feeder, DLSS5
  DX11 Bridge, OptiScaler DLSS NR / F5 DLSSNR, RTX Encore with its NR file) in the game.
- The page (label/value rows, amber warnings, Close only): **Graphics card** (model and RTX generation from #229's
  `NvidiaGpuProbe` facts), **Neural Rendering DLL** (its manifest version through #230's one helper,
  `NeuralRenderingInstalledVersion`, so it agrees with Update All; whether #229's policy says it is made for this card; who
  put it there; a version picked by hand; the 310.8.0.x-on-R615 crash note of #231; "Latest" made for this card and where
  to choose it), **Recommended method** (`RecommendedNeuralRenderingMethod`, as on the panel) and the methods in use,
  **Anti-cheat folders** (wizardkof's list, information only — never a block), **Log findings** from `ReShade.log`,
  `OptiScaler.log` and `dlss5-feed.log` beside the executable: wizardkof's signatures (the RenoDX NR skip, `0xBAD00001`
  FeatureNotSupported, HLSL `E5017`/`E5005`/`fastopt`, `0xC0000005`/device lost) plus two the DLSS5 Feeder add-on itself
  names (`error X3506` for a compiler too old for `cs_5_1`, "device was removed"); short codes match as whole words. Each
  finding names the log, the line and a fix that points at a real control (the NR DLL list under NR versions, the DLSS
  section's Neural rendering line with Apply, the D3DCompiler 47 line's Protontricks / Winetricks / Browse, a component
  row's Remove); paths and the matching text sit in ~2 s tooltips.
- Reads only, never writes (game folder, prefix, RHI's data — a test snapshots all three byte for byte) and never
  fetches the DLSS manifest. Logs: one non-blocking open of a regular file, refused when a link on the path leaves the
  game folder or its prefix, at most the last 2 MiB (line numbers only when the whole file was read); a FIFO, a folder or
  an unreadable file is a line on the page, never an error.
- Not ported: wizardkof's "Install recommended setup (trial)" button (owner decision: read-only page, no install
  wording), its CUDA-fatbin model check (passes the wrong DLL on real files; #229 judges by the manifest label) and its
  `nvidia-smi` compute-capability query (the fork never runs `nvidia-smi`).

## Parity notes (upstream 2.8.8)

<!-- w12/mfg notes -->

### #236 — Universal MFG accepts MFG Unlocker releases

Behavioural port of upstream `bb9b2fd` (`Rtx40MfgService.ReleaseDllNames`) and the Rtx40Mfg part of `1b51cb2`. The
publisher renamed the project (`dashdogy/RTX40MFG-Unlock` → `dashdogy/MFG-Unlocker`; the old API URL answers 301): from
v1.5.0 the release ships `MFGUnlocker-v<ver>.zip` holding `MFGUnlocker.dll`, so every release since v1.5.0 was refused.
RHI now asks the new repository, takes the `MFGUnlocker-*.zip` asset first and `RTXMFG-*.zip` second (exactly one zip of
the chosen naming), and inside it `MFGUnlocker.dll` first, `RTXMFG.dll` second (exactly one entry of the chosen name).
The fail-closed SHA rule (GitHub digest or `SHA256SUMS.txt`), PE x64, size and the per-release cache stamp are
unchanged. A v1.4.x install updates in place under its proxy name through the journaled update (history
`1.4.2 → 1.7.0`). Upstream's staging-format bump fixes a bug the fork never had (the cache stamp is written only after a
verified extraction). Upstream pushes these at start and compares file contents (`IsOutdatedIn`); the fork keeps its one
rule (owner decision D1 = No): Update All, and the background pass when automatic updates are on, with a game that was
running caught up at the next pass. Fork-only: the check reports a proxy removed outside RHI (never put back), changed
outside RHI, or recorded without its hash or proxy name as "could not be checked" with a line saying why (a record
without its proxy name: one line for Universal MFG, DLSSG 20/30, the ASI Loader and DLSS Enabler Standalone, ending
"Remove it, then Install it again", in the check and the update); a game held by its own failed undo reads the hold's
restart text first, before any file is read; and the row keeps Remove and notes the removed proxy (no note on a game
moved after the install, where Remove waits for Repair after move). Remove follows that line through (w12 final review
M1): for a record without its proxy name it deletes only the listed files that still hold RHI's bytes, restores the
recorded Wine override, drops the record and names every file it left (missing, without a hash or changed outside RHI;
the ASI Loader puts a chained original back over its proxy while the original exists, else leaves the proxy), so Install
works again.

### #237 — MFG Ada Unlock updates

Behavioural port of upstream `abf18c7` (`AddonPackService.RedeployToTrackedGames`, `UntrackAddonDeployment`) and the
add-on part of `49d4473`. MFG Ada Unlock is an Update All family (after the other add-ons), with exclusions and No
Addons holds as every family; a game held by its own failed undo reads the hold's restart text in the check and in a
direct Update (both before the manifest or any file is read; offline too, as does an add-on removed outside RHI), never
"changed outside RHI" about RHI's own half-undone add-on. Upstream's "removed copies reinstalled" bug was latent here
(the generic add-on update re-ran the install): an update of any GitHub add-on (ReLimiter, Display Commander, MFG Ada
Unlock, the DLSS5 DX11 Bridge's version switch) never re-creates a file removed outside RHI; only Remove, then Install,
puts it back. No start-up push (D1 = No).

### #238 — DLSSG 20/30 Unlock and Ultimate ASI Loader in Update All

Behavioural port of upstream `bb9b2fd` (DLSSG 20/30 deployed at start) and `a1a4e48` (ASI Loader updates reach games). Both are
Update All families with their existing checks adapted (could not check when the lookup fails, the game is held by a failed
undo, or the record lacks its proxy name or hash; never a swap over a file removed or changed outside RHI), proxy names kept. The
ASI Loader updates only for the bitness it was installed for: once the game's executable reads as the other one (a game patch,
or Edit game pointing at another exe) the check could not check and the update refuses (Remove, then Install, picks the right
loader); never an unattended cross-bitness swap, and the Extras section's ASI Loader check says the same (never "Update
available" for an update that would refuse). Upstream's "x64 re-downloaded at every launch, x86 never updated" never
existed here (per-architecture stamped cache); tests pin it. No start-up push (D1 = No).

<!-- w12/shaders notes -->

### #239 — ReShade core headers with every shader pack

- Ported (upstream `ShaderPackService.Deploy.cs` `EnsureReShadeHeaders`, `ShaderPackService.Mirror.cs` `AddPackFiles`, origin/main `c1076d3`): `ReShade.fxh` and `ReShadeUI.fxh` go into the game with every sync that deploys at least one shader pack (ReShade install and update, the shaders-only sync, the Vulkan layer and XNA paths, which all use the one prepared sync). Before, only ClshortfuseShaders and MaxG2DSimpleHDR brought them, so packs that include them without shipping a copy (Prod80 and many others) failed to compile ("could not open included file 'ReShade.fxh'").
- Linux layout: the headers sit in RHI's own `reshade-shaders/RHI/_Core/Shaders` with that folder on ReShade.ini's `EffectSearchPaths` (each pack keeps its own folder; upstream copies everything into one `reshade-shaders/Shaders`). They are listed in `.rhi-manifest.json` like pack files, stay while one RHI pack remains and go with the last; a `ReShade.fxh` of the user's elsewhere is never touched. As upstream (a header missing from staging is skipped), headers that cannot be fetched never stop the packs (a local custom pack still works offline).

### #240 — Shader sync leaves no empty pack folders

- Fork bug (owner's Borderlands 3: empty `reshade-shaders/RHI/Prod80/{Shaders,Textures}` after Prod80 was turned off). Inside a journaled sync or ReShade removal the journal keeps its copy of each removed file beside it until the commit, so the folders were never empty when the sync tried to prune them. The folders that held RHI's files are now pruned after the commit (`DeploymentOperation.AfterCommit`), deepest first and only when empty; `reshade-shaders/RHI` itself goes with RHI's last file. A folder holding anything of the user's stays, a folder outside RHI's pack folders (the user's own, even empty) stays, nothing is followed through or removed as a folder link (when `reshade-shaders/RHI` or `reshade-shaders` itself is a link, no folder at all is pruned: they all live where the link points), and nothing outside `reshade-shaders/RHI` is touched. A failure or crash before the commit puts every file back in its folder. Upstream's mirror prunes every empty sub-folder of its whole managed `reshade-shaders` folder (it treats everything there as its own); RHI keeps to the folders its manifest listed. The pruning runs while the operation still holds its journal lock. Empty folders of packs no longer selected that no manifest lists (named after a catalogue pack — the owner's Prod80 leftovers from an older build, or from a crash between the commit and the pruning) are pruned by the next sync, by ReShade's removal (also when no manifest is left) or, after a crash during that removal, by the next ReShade install; put plainly: an empty folder the user made inside the folder of a pack that is no longer ticked goes with that pack's folder; an empty folder inside a pack that is still ticked (the user's, or one RHI left there before this build or after a crash) stays until that pack is unticked; `_Core` (it goes with every pack) and folders with other names stay.

### #241 — Shader sync mirrors RHI's shader folder

- Ported (upstream v2.8.8 Beta `f32704c`, `ShaderPackService.Mirror.cs` `MirrorManagedFolder`): a shader sync no longer deletes every RHI shader file and copies everything back. Per file: same size and SHA-256 as the staged copy (and still the hash RHI recorded) → left untouched and not journaled; RHI's copy with other content → replaced; new → placed; no longer selected → deleted. The manifest lists exactly what is deployed and is not rewritten when nothing changed, so applying the same shaders (Shaders/Add-ons Apply, Update All, a ReShade update) writes nothing in the game and a running game keeps its shaders. Every path mirrors: the shaders-only sync, ReShade's install and update (DX proxy and Vulkan layer, which no longer clean the packs out before the prepared sync), XNA. The core headers (#239) are mirrored like pack files; when they cannot be fetched this time, the copy RHI placed before stays.
- Linux deviations: upstream tells a changed file by size, then bytes only when the staged copy is newer (`IsFileStale`); RHI compares size and SHA-256 every time. Upstream deletes anything in its managed folder that is not planned; RHI deletes only files its manifest lists with their recorded hash (a file the user changed or added stays), and a planned path holding a file that is not RHI's still refuses the sync before any change, with a message that says how to go on (move or delete the file, or untick its pack in Shaders/Add-ons; for a core header in `_Core`, which goes with every pack, move or delete it only). Two catalogue packs whose ids would give one folder name refuse the sync with their own line (RHI's own clash, never "changed outside RHI"). Folders the mirror creates are journaled (a rollback or a crash before the commit removes them again). A pack folder or file that is a link refuses the sync before any change (RHI never writes through it). Upstream's sync at every launch never existed here (no start-up sync).

<!-- w12/update-all notes -->

### #242 — A game deferred because it was running is updated soon after it closes
- Upstream `79f5680` (`AutoUpdateService.EnqueueRetry`, `RetryPassAsync`): Automatic Updates queue the running games and a 60 s
  timer updates each one alone once it is no longer running.
- Linux: the background pass reports the games it deferred (`LinuxUpdateAllResult.RunningGames`); the loop keeps them in
  `DeferredGameRetry` and, every 60 s while any is left and "Automatically update RHI-managed components" is on, asks the
  existing running-game probe (`/proc`, no process started) about each. A game that has closed gets a pass of its own with
  the background pass's rules (update inclusion, holds, families, check first), once the busy gate is free. A game that
  runs again before its pass waits for the next poll; one whose pass ran is done (a failure is left to the next scheduled
  pass, as upstream drops it). Turning automatic updates off, or any change that restarts the loop, forgets the list (the
  restarted loop's first pass defers them again). A probe error is logged once and only means "ask again next poll"; every
  poll waits the full minute first. A pass that changed something writes "Deferred updates ran after the game closed:
  <game>." followed by the usual Update All counts. Manual Update All is unchanged: a running game is reported as
  deferred. Difference: upstream queues only games whose update check found something; Linux defers before checking (as
  before), so the closed game's pass does the check.

### #243 — Update All: a Custom-channel ReShade whose source DLL is gone is reported, not failed
- Upstream `49d4473` (`UpdateOrchestrationService.UpdateAllReShade`): a `FileNotFoundException` from a Custom-channel game
  whose DLL was removed from the Custom folder is caught; the card says "ReShade DLL not found — see Custom ReShade folder"
  and no crash report is written.
- Linux: manual Update All re-deploys ReShade without a check (#113), so such a game counted as a failed update. A
  ReShade on the Custom channel is now checked first (`ILinuxManagedUpdateBackend.ChecksBeforeManualRefresh`; the check is
  local: the chosen DLL is still there, and its hash): a source that is gone reads "could not be checked" and nothing is
  touched. The Update All line names it as it names a failure, after the counts: "Not checked: <game> (ReShade) —
  Custom ReShade source file is unavailable; installed DLL remains untouched." (PT-BR too; every such game is named
  wherever that line shows, so a background pass names it when it writes its line). Only a check whose reason is local is
  named this way (`LinuxUpdateItemResult.LocalReason`): a check made first because it is local (`ChecksBeforeManualRefresh`),
  or a check that says so itself (`ManagedUpdateCheck.LocalReason`: the MFG families' and GitHub add-ons' game held by its
  own failed undo, file removed or changed outside RHI, record without its file's hash or proxy name, game whose bitness
  changed; the GitHub add-ons' and RenoDX's missing or unreadable game executable, and a RenoDX installed from a local
  file); a source that could not be reached (a reshade.me or GitHub outage) stays in the
  "could not be checked" count. Difference: upstream writes its note on the game's card; the Linux card has no note
  line, so the reason shows in the Update All line (its tooltip holds the whole line) and in the ReShade section's
  update-check line. A source still there is refreshed as before. Stable, Nightly, No Addons and Legacy still refresh
  unchecked on every manual Update All.

<!-- w12/github-cache notes -->

### #244 — GitHub API cache: keep answers between launches, share overlapping requests, 30 s API timeout

- Ported from upstream `feb126c` (`GitHubETagCache` saved to `github_etag_cache.json`, 30-day `MaxAge`, saves batched
  2 s, `ApiTimeout` 30 s) and `1b51cb2` (overlapping calls share one request, `FreshFor` 60 s, rate-limit flag for
  api.github.com only). `GitHubConditionalCacheHandler` (#35) is still the one place: every service's api.github.com
  GET goes through it, so no per-service change was needed (upstream moved six checks onto its cache in `1b51cb2`).
- Kept between launches in `~/.cache/rhi-linux/github-api-cache.json` (`GitHubApiCacheStore`, `JsonFileStore` cache
  kind: an unreadable file is copied aside to `.corrupt-<utc>` and rebuilt). Each entry: URL + Accept, ETag /
  Last-Modified, content type, body, and when GitHub last confirmed it (a 200 or a 304; upstream only counts the 200).
  Dropped on load after 30 days, or when invalid or dated more than a day ahead. Limits as before: 512 answers, 8 MiB
  each; a body that is not UTF-8 text stays for the session only. Saves wait 2 s after the last change and are flushed
  when RHI exits. Never a request header: the token is added by `GitHubApiTokenHandler` behind the cache, and a request
  that carries its own Authorization header is neither cached, shared nor saved.
- Linux difference: a saved answer is only used once GitHub answered 304 this session (or, as before, while GitHub's
  advertised rate-limit window runs); it is never treated as current without asking. A 5xx, or a 403 that is no rate
  limit (GitHub's secondary limit in a start-up burst looks like one), at the next start reads as could not check;
  upstream serves its saved body on any error. An answer GitHub confirmed this session still stands in for such an
  error (#35). Upstream's 60 s reuse follows the same rule (it starts empty each launch).
- Linux difference: a request that times out reads as could not check ("The server took too long to answer: RHI
  stopped waiting after 30 seconds. Source: GitHub API", EN + PT-BR), even when an answer is cached; upstream hands
  back the stale cached body, which an update check would read as "current". The user's Cancel stays a Cancel, and a
  caller's Cancel ends only its own wait for a shared request. The 30 s covers the request and the body the cache reads;
  `HttpClient.Timeout` (5 min) stays for payloads, and the 60 s idle limit (#233) is unchanged.
- An answer that ended on another host after a redirect is not kept. `rhi.log` (`[github-cache]`) names how many saved
  answers were loaded at start and each one GitHub confirmed unchanged (304).

<!-- w12/luma notes -->

### #245 — Luma: Nexus-only mods are checked on Nexus, never updated from a GitHub build
- Upstream 2.8.8 Beta `20d3297` (`UpdateOrchestrationService` leaves Nexus-only Luma mods out of the GitHub build check
  and clears the stale badge; `MainViewModel.CheckLumaNexusUpdatesAsync` compares Nexus with a baseline). Behavioural
  port: the wiki's Download Link cell gives a download link, a Nexus Mods link, or both, in either order; a Nexus link is
  the mod's page, never its download (`LumaCatalogEntry.NexusUrl`, `IsNexusOnly`), and the release merge never fills a
  Nexus-only row from a same-named GitHub build. Before this the fork took the first link as the download, so a
  Nexus-only mod could never be checked (could not check, forever) and a GitHub mod listed Nexus-first was unusable.
- The check (`Network/NexusModUpdateCheck`, the one Nexus "newer file" check; #198 reuses it) uses the connected
  account's API key only (owner rule; upstream asks the anonymous GraphQL API), Nexus's v1 file list, and compares the
  newest MAIN/UPDATE file's upload time with the install time (upstream's baseline; no Luma install records a Nexus file
  id). No account / Nexus error / rate limit / a refused key = could not check. Only a 401 is a refused key (coordinator
  ruling in the w12 follow-up): its line names Settings → Accounts & updates → Disconnect, then Connect Nexus account,
  and that key is not asked with again this session until a key is saved (the account connected again). A 403 (a mod
  Nexus hides or removed) reads "Nexus Mods would not show this mod's files …" and never advises disconnecting a key
  that works. One file list per mod is kept for an hour; after a 429 Nexus is left alone for 5 minutes (the limit is
  the account's).
- Update never downloads it: Update All and the background pass count it as could not be checked (never failed). The
  fork's rows have no per-row Update button, so the Luma row has a Nexus button in its own column, first among the row's
  buttons (Nexus → ⚙ → Install/Remove → ⓘ; an Unreal game's Luma Engine.ini ⚙, #189, stays beside it). Every other
  row keeps the ⚙ → Install/Remove → ⓘ order and layout it had before: only the Nexus column takes no place while its
  button is hidden (`ResponsiveRow.SkipEmptyColumns="3"`, a column list read once when set; an entry that is not a
  column number is ignored). "Update on Nexus" (opens the page through
  `LinkHosts.NexusMod`) when Nexus has a newer file; "Open on Nexus" when no account is connected or Nexus refused the
  key (the 2 s tooltip names the control that fixes it) and, without asking Nexus, when RHI has not installed the mod
  (upstream's card links these mods' Nexus page; download it there and drop the file on RHI). Hidden otherwise.
- Pins gap 2 (upstream `b8094e6`, `SafeCombine` on Luma uninstall): Luma removal and update delete a recorded file or
  folder only inside the game folder, its binary folder or ReShade's `AddonPath` (where Luma's add-on goes); a ledger
  entry naming anything else is left in place and never blocks the removal; removal and update each log one line per
  such entry (an update also leaves it out of the new record).

### #246 — Luma mode: no manifest default (upstream removed lumaDefaultGames)
- Upstream 2.8.8 Beta `230ac6a` removed `lumaDefaultGames` from `manifest.json` (no upstream consumer since at least
  2.7.5). The fork still read it, so with the live manifest (no key) no game defaulted to Luma mode while the pinned 2.8.7
  fallback (offline) turned it on for 35 games. Now `LumaPolicyService` reads only `lumaRenodxCompat`: Luma mode is on
  only where the user chose Enabled, off for Disabled and Automatic, whichever manifest answers; saved choices are kept.
  The Luma library filter still lists games with RHI's Luma installed (record) or Luma mode turned on by the user.

<!-- w12/epic notes -->

### #247 — Epic Games Launcher in a Wine/Lutris prefix (classic manifests and the new launcher)
- Upstream (`c1076d3`, `GameDetectionService.Epic.cs`; classic manifests since before 2.8.5) reads the classic launcher's `ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` and the new launcher's (`com.epicgames.eglv2`) `%LOCALAPPDATA%\com.epicgames.eglv2\installations.jsonl` (last entry per installationId wins, only `complete: true`), naming its games from `HKCU\…\Uninstall\EpicGames-<artifactId>`, then the migrated `import-scan\v1-items\<installationId>.item`; uninstall entries the log does not mention are listed too; de-duplicated by path. The fork reads the same records inside every Wine prefix (standalone, Lutris, Heroic, Steam non-Steam shortcut), read-only, through the #90 store-launcher reader: `user.reg` only where the prefix holds Epic data; regular files only, opened without blocking, capped (manifest 4 MiB, log 32 MiB); a malformed log line is skipped with one scan warning per file; folders go through the prefix's drive links and the root-folder guard.
- Launch: upstream starts `com.epicgames.launcher://apps/<AppName>?action=launch&silent=true` (AppName = the new launcher's artifactId). The fork hands that URI to the launcher exe in the prefix (the `com.epicgames.launcher` URL handler's program from the registry, else the classic launcher's default places, then the new launcher's per-user `AppData\Local\Epic Games\Launcher\EpicGamesLauncher.exe`), with the #90 parent rules (Lutris: the game's own Lutris store entry, else the launcher's own config, else the prefix's parent launcher; Steam shortcut to the launcher: started through Steam; Heroic: its sideloaded launcher, else no command). A store id that is not plain letters/digits/`_ . -` never reaches the command (the launcher only opens).
- Lutris store services (fork only; w12 correction): Lutris' Epic Games Store, Ubisoft Connect, EA app and Battle.net services write one config per game — a copy of the client's config plus that game's start argument (`-com.epicgames.launcher://apps/<AppName>?action=launch`, `uplay://launch/<id>`, `origin2://game/launch?offerIds=<id>`, `--exec="launch <code>"`). Such an entry is its own game's Play and is not listed again as a card at the client's folder; an entry that starts another game is never a game's Play or a prefix's parent launcher (before, the first such entry in file-name order started every game of that store in the prefix). The dashed Lutris form of the Epic URI counts as a store hand-off.
- A prefix whose only store entries start one game each (no entry for the client itself, e.g. a per-game installer put the client into the game's prefix, or the client's own entry was removed from Lutris): every other game recorded there, and every store-library game found by folder layout, is still listed — with the prefix's runner and Wine fields but no launch command and no parent launcher (Play says to set one in Edit game), plus one log line per prefix. The scan notice counts them on their own line ("Lutris: N game(s) listed without a launch command — set one in Edit game", EN + PT-BR), never as skipped items; Wine prefixes whose runtime markers cannot be read report their games the same way. A prefix where only games share (no store entry at all) lists no extra games, as before.
- A per-game store entry that becomes its game's card moves from the client's folder to the game's folder, so its library key changes: favourite/hidden marks and Edit-game changes saved on the old card (keyed by the Lutris config file) do not follow it and must be set again. Not migrated.
- Uninstall entries (`EpicGames-<artifactId>`) the log does not mention are listed wherever the prefix holds Epic data (classic manifests or the new launcher's data folder), as upstream lists them whether or not its data folder is found.
- Ubisoft: Lutris names a game by its launch id, Ubisoft's record by its install id; where the two differ, the game keeps its card (Play through the client) at its real folder and its per-game Lutris entry stays a second card at the client's folder (no wrong game, nothing dropped; the same as before #247).
- Differences, all small: a game with no name anywhere keeps its folder name (upstream skips it); a DLC's classic manifest never names its game's card; upstream's `HKCU\Software\Epic Games\EpicGamesLauncher\AppDataPath` data-folder override is not read (only the launchers' default places; a relocated data folder inside a prefix is not found); a folder another store's record names keeps that store's hand-off.
- Not covered: the new launcher's native Linux build (`~/.local/share/com.epicgames.eglv2`, Proton-run Windows games) is not read; Epic games installed through Heroic/Legendary are unchanged.

<!-- w12/nr-logs notes -->
### #248 — Check Neural Rendering: three DLSS5 Feeder failures in plain words

Fork-only (owner-reported, Borderlands 3 `dlss5-feed.log`; no upstream commit in `b057033..c1076d3`). Extends #234's
**Log findings** (same rows: log, line, plain headline, a "Fix:" line; the matching text and path in the ~2 s tooltip).

- `0xBAD00012` (NotImplemented) for the Feeder's NGX "feature 18" requirements query: the driver did not confirm Neural
  Rendering support. The line proves only that (the DLSS5 Feeder's README records the same answer on a driver where the
  neural pass ran), so the row is worded that way and does not contradict the page's recommended method: nothing to
  change in RHI or the game; if the picture shows no neural effect, this driver and Proton (dxvk-nvapi) do not run it
  yet; DLSS Super Resolution still works. The same code for another feature is no finding, even on a line that mentions
  neural rendering. #248 also asked to name a method that works without Neural Rendering: every RHI Neural Rendering
  method needs it, so the row names none; DLSS Super Resolution (the game's own DLSS) is what keeps working.
- The Feeder's depth probe "depth is FLAT while the scene moves": a setting in ReShade's in-game overlay (Add-ons tab →
  Generic Depth → the buffer that moves with the scene), stated plainly as not an RHI control. Its "sampled depth is
  flat" line (a still scene or a menu) is no finding.
- The Feeder's "stopped: the DLSS evaluate crashed" after a change of the game's picture (swapchain colour space,
  ReShade's effect runtime made again, the feature rebuilt) that no delivered frame has come through yet, since the
  feature was ready in that run. A frame delivered while a rebuild for the change is announced ("holding the feature
  (re)build", "building:") but not ready yet is the old feature's and settles nothing; a change the feature survives
  without a rebuild (an alt-tab or effect reload on a D3D12 game, per the Feeder's README) is settled by the next
  delivered frame, so a later crash gets the generic row. The Feeder writes "frame N delivered" only for its first
  `log_frames` frames and then every 1800 frames, so after such a change a crash before the next delivered line (up to
  about 30 s at 60 fps) still gets this row; no other Feeder line proves a frame was delivered (its STALL lines also
  appear while a rebuild is held), and the row's last sentence covers that case. Then: restart the game with its final
  display mode (HDR, resolution) set in the game's own options; if the Feeder stops again, turn HDR off in the game's
  options (the Feeder's README records the same evaluate fault inside NGX with the DLSS 5 add-on, which a restart may
  not cure). This row replaces the generic `0xC0000005` row of the same crash (the evaluate lines right after the
  stopped line included, whichever the Feeder writes first); the generic row ("remove the method") stays for every other
  crash, including a Feeder crash on its first build, and a stopped line in a run with no other crash line is that run's
  generic row (never "No known errors").
- Every hint is a plain step (no RHI control named). Reads stay bounded; nothing is written.

<!-- w12/pins notes -->

### #249 — Regression pins for upstream 2.8.8 bugs that never existed here

Upstream v2.8.8 Beta fixed these in `713c222`, `d61a696`, `e634e52`, `79f5680`, `feb126c`, `49d4473`, `abf18c7`,
`a1a4e48`, `b8094e6`, `1b51cb2`, `f170aa6`/`865f784`. The rewrite never had them; tests (`Upstream288Pins*.cs`, plus the
existing tests named in the lane's report) pin today's behaviour, each proved by a mutant of the line it guards: numeric
DLSS version equality and the DLSS section re-read after a swap; a failed swap rolled back and the DLSS batch carrying
on past a failing game; OptiScaler Stable / DLSS NR / F5 updates keeping their variant, reporting the new version and
landing in Update History, as Luma updates do; the ReShade nightly reused for an unchanged Actions run; unchanged RenoDX
snapshots judged by HEAD validators; concurrent ledger writes; Luma TAA kept across a display-name change; the GitHub
token only on `https://api.github.com` and not carried through a redirect to `codeload.github.com`; Luma and shader
archive members with `..` or an absolute path refused; Luma removal deleting only its recorded files; the Ultimate ASI
Loader cache per architecture; Follow newest and the Streamline dropdown with `2.14.16.0-v2`. Two pins found real gaps,
both fixed in this wave. The DLSS result line (batch apply and restore, Apply defaults, Restore all) now names each
failed game with its reason after the counts ("Failed: N game(s) • <game>: <reason>", EN + PT-BR), as upstream `d61a696`
does in its mass-deploy summary, and its counts now follow the UI language (they were English in PT-BR) (pins
correction). Follow-up: the reasons a swap gives without one of its own ("Could not apply <component> <version>.", the
two Streamline refusals) are Portuguese in PT-BR too; games are told apart by library key, so the same game on two
stores is two entries; the platform's English-only count summary is gone; on a DLSS batch dialog narrower than 700 px
the line has its own row above the buttons (wide dialogs unchanged). Luma removal's inside-the-game check on the paths
its record lists (upstream `b8094e6`, `SafeCombine` on uninstall) is fixed and pinned in lane w12/luma. The default 3 s
wait for `pcgw_data.json` during a selection is pinned with its literal value.

<!-- w12/hdr-wsi notes -->

### #250 — Legacy HDR WSI: the tick only where the host needs it

Fork-only fix (owner report, no upstream commit; upstream RHI is Windows-only and has no HDR WSI layer). The per-game
"Legacy WSI" tick shows only when the HDR host probe says the layer is needed (an NVIDIA driver older than 595); it is
hidden on newer drivers, without a readable NVIDIA driver and until the host is probed. `ENABLE_HDR_WSI=1` goes into a
launch plan only on such a host (`LinuxHdrLaunchProfileService.OffersLegacyWsi`), so a tick saved earlier is left out of
the launch line RHI shows for pasting into Steam or into the app that installed an Xbox game, the Heroic/Lutris steps
and a game RHI starts directly — and stays saved (it applies again on an older driver; `game-library.json` is not
rewritten). The plan's note then says the saved tick is not used with this driver; with nothing saved it says nothing
about Legacy WSI there (no sentence about a control that is not shown). HDR Off is unchanged (no variables).

### #251 — HDR launch section only for games that can use HDR

Fork-only (owner decision; upstream RHI has no Linux HDR launch section). The HDR launch section shows unless RHI's data
says the game has no HDR and RHI installed no HDR mod on it (RenoDX, RenoDX Upgrade, Luma Framework, DX9 HDR); unknown,
offline, a rate limit or a failed lookup show it ("safe, not sorry"). `HdrSupportService` answers, first decisive source
wins: RHI's curated list (embedded `Hdr/hdr-support-overrides.json`, keyed `steam:`/`heroic:`/`xbox:`/`name:`), the
PCGamingWiki page's `hdr` field (page found as the graphics-API lookup finds it — manifest page URL, pcgw_data.json title,
Steam AppID, PCGW search — and read as wikitext through `action=parse`, sharing that lookup's request gate; `true`,
`hackable`, `limited`, `always on` → Yes, `false` → No, anything else → Unknown; an AppID PCGW does not know answers its
`Special:IdLookup` page, which falls back to the name search). For a game the app that installed it keeps a Microsoft
Store record for (the instance's `catalogue/products/<id>.json`, the file RHI reads the card's name from), that record's
`CapabilityHDR` attribute on PC turns a PCGW `false` or unknown into Yes (the publisher's own listing; the curated list
still wins). Answers are cached in `~/.cache/rhi-linux/pcgw_hdr_cache.json` with page and fetch time, asked again only
after a full Refresh or Settings → Clear cache; the lookup of a game the user has left is cancelled. A hidden section's
saved HDR mode stays in `game-library.json` but is not applied at a direct launch nor offered as launch text (HDR Off).
"Change resolution while playing" is not HDR: it has its own row below; when the HDR row is hidden it stays wherever a
desktop switch can apply (a game RHI starts, the desktop's tool installed) and switches the resolution around a direct
start; gamescope-only cases (store hand-offs, no desktop tool) need the HDR row and hide with it. Provenance is only in the
section label's 2 s tooltip (EN + PT-BR).

## Parity notes (upstream 2.8.8 release)

Wave 13 ports the upstream delta from the 2.8.8 beta sync point (`c1076d3`) to the 2.8.8 release (tag `RHI-2.8.8`,
`3689428`).

<!-- w13/addonpath notes -->

### #255 — ReShade add-ons follow reshade.ini AddonPath everywhere

Behavioural port of upstream `54ec8f2` (`AddonPackService.DeployAddonsForGame`, `DetailPanelBuilder.Extras.Unlocks`).
Upstream fixed two writers that ignored `[ADDON] AddonPath`: the add-on picker and MFG Ada Unlock from Extras. On Linux
both already resolved the folder through `RenoDxDeploymentService.ResolveAddonDeployPath` (the Addon Manager, and every
GitHub add-on: MFG Ada Unlock, ReLimiter, Display Commander, the DLSS5 DX11 Bridge), as did RenoDX, the RenoDX Upgrade,
ShortFuse, Luma, the DLSS5 Feeder and dropped add-ons. Two writers still put their add-on beside the executable and now
follow AddonPath (relative to the binary folder, or absolute): the Universal UE DOF Fix and NR Cost Scaler's
`dlssnr-companion.addon64` (its proxy, its INI and the owner's runtime stay beside `nvngx_dlssnr.dll`). Upstream 2.8.8
still puts these two in the game folder.

When AddonPath is set or changed after an install, the next install or update (Update All included) moves RHI's copy
from the path in its record to the AddonPath folder in the same journaled operation, and the record follows; upstream
deletes the game-folder copy its tracker lists. RHI's copy is deleted only while it still holds the bytes RHI placed; a
copy changed since is kept and the update refused, as before. A same-named file in the game folder that RHI does not own
is left, and the result line names it ("Not RHI's, so left in the game folder …", EN + PT-BR). Remove follows the record
wherever the file is. The component rows also look in the AddonPath folder when it lies outside the game tree
(upstream's MFG Ada Unlock check reads the AddonPath folder; the Linux file index already covered an AddonPath inside
the game). An AddonPath outside the game folder is used, as before (Luma relies on it); it is not refused.

<!-- w13/available-mods notes -->

### #256 — Available HDR mods becomes Available mods: Ultra+ column, ticks link to mod pages, counts, mod filters

Behavioural port of upstream `54ec8f2` (`MainWindow.Events.Settings.cs`, `Models/HdrModEntry.cs`,
`MainViewModel.GetAllHdrMods`). The toolbar button, dialog title, tooltip and localisation keys say **Available mods**
(`Ui.AvailableMods`, `Win.RHILinuxAvailableMods`; PT-BR "Mods disponíveis"). Columns: Game, RenoDX, Luma, **Ultra+**.
Ultra+ rows come from the theultraplace.com games list the card links already read (#183,
`~/.cache/rhi-linux/links/ultraplus-games.json`, 24 h); the dialog waits for that list only, not the ultrawide or HDR
lists. A game's Ultra+ page is the manifest's `ultraPlusUrlOverrides` entry for its name when that page is on
theultraplace.com (upstream `UltraPlusService.ResolveUrl`), else the list's. Rows are joined by the dialog's normalised
name (as RenoDX and Luma already are; upstream also normalises for Ultra+), the first Ultra+ entry for a name wins, and
a game with only an Ultra+ mod gets its own row.

Every ✓ or 🔨 with a page is a button that opens it; ✗ is plain text. The separate Download column is gone. RenoDX opens
the mod's own link (download, else Nexus Mods, else Discord, #197), and a game covered only by a generic add-on
(UE-Extended, Unity, Source) opens the RenoDX wiki's mod list (`github.com/clshortfuse/renodx/wiki/Mods`), as upstream;
Luma opens its download link; Ultra+ its game page. Link checks are unchanged per site: RenoDX and Luma links must be
http(s), Ultra+ pages https on theultraplace.com. The top line reads "{n} RenoDX mods, {n} Luma mods, {n} Ultra+ mods"
(upstream's counts), then which source could not be loaded (RenoDX or Luma catalogue as before, the Source database, the
Ultra+ list); the other columns still show. Search: a leading `renodx`, `luma` or `ultra+` (also `ultra` and
`ultraplus`, upstream's tokens), any case, keeps only games with that mod, and the rest of the text narrows by name
(`luma dead`); any other first word is a plain name search. Linux deviation kept from #11: the list covers the
catalogues, not only the library, so the counts are catalogue-wide.

### #257 — Available mods: Source engine games from the RenoDX Source database

Behavioural port of upstream `698956d` (`Models/RenoDXDbSourceEntry.cs`, `RenoDXDbService.FetchSourceEntriesAsync`) for
the database upstream `f5678e0` adds to its editor. `RenoDXdb-source.json` is read from RankFTW/rhi-repo `main` as the
Unity database is (#15; the loader is now shared): HTTPS on raw.githubusercontent.com only, 8 MB cap, kept 1 h in
memory, revalidated with its ETag, the last good copy and its ETag in `~/.cache/rhi-linux/renodx/`, then the copy pinned
at RHI 2.8.8 (rhi-repo `9a9cf39`, the file's only commit, 13 games). Its games join the RenoDX column after the Unity
ones (upstream's order), ✓ or 🔨 by `Status`, never over a named mod's row; Luma and Ultra+ merge into them by name.
`Bitness` is not read (upstream ignores it as well). A database that cannot be loaded leaves those rows out and the
dialog says so. Installing the Source add-on from RHI is not part of this (upstream added it in 2.9.3).

<!-- w13/manifest notes -->

### #258 — Manifest-only and Windows-only commits of the 2.8.8 release

Nothing to port in code; each reaches Linux through the fetched manifest or does not apply. Pinned by
`UpstreamManifest288ReleaseTests` with the entries copied from upstream's `manifest.json` at `RHI-2.8.8`.

- `e40845a` Portal `installPathOverrides` → `bin`: applied by `UpstreamGameLibraryPolicyService` as every install path
  override (inside the game root only, #45), so Portal's mods go into `bin`.
- `2281a09`, `d4dba12` engine versions (DAEMON X MACHINA Unreal Engine 4.20.3, Phantom Blade Zero Unreal Engine 5.7.4):
  `engineHintOverrides` become the game's engine hint, which the UE rules read (neither is in the DOF Fix range, UE
  5.0–5.6).
- `fc63285` Metro 2033 Redux `profileNameOverrides` and `cc08885` (manifest profile overrides win over upstream's cached
  NVIDIA profile matches): not applicable. There is no NVIDIA driver profile under Wine/Proton (#16, #211: the DLSS
  preset is RHI's own launch setting), and nothing in the fork reads `profileNameOverrides` or `profileExeExclusions`.
- `b1f3396` (one commit after the tag, upstream's DLSS manifest): the Streamline entry's label is now `2.14.16.0`
  instead of `2.14.16.0-v2` (same archive). The fork reads `dlss_manifest.json` from upstream's `main` and shows each
  entry's `version` as its label, so the list shows `2.14.16.0` with no change here; the package is cached once more
  under the new label.
- `698956d`, `be3bee9` notes: upstream now tells users to add a GitHub Personal Access Token rather than "Sign in with
  GitHub". The fork never had the sign-in (#163, #134 parked); the Settings note now says the token is recommended (it
  was "Optional"), why (almost every download comes from GitHub; 60 requests an hour without it, 5,000 with it) and how
  (github.com/settings/tokens → Generate new token (classic), any name and expiry, no scopes), as does the README.
- `e97d1a5`, `7d9a715`, `edeafed`, `3689428` `pcgw_data.json` updates: fetched at runtime (#12), nothing to port.

## Parity notes (fixes from upstream 2.8.9–2.9.3 sources)

### #262 — HDR Gaming Database links read the README's HTML table (#183 follow-up)

Upstream 2.9.3 `3d99906`. KoKlusz's README moved its games index from a Markdown list to an HTML `<table>` in November
2026, so the list (#183) parsed no game and the HDR Gaming Database link was gone from every card menu and ⓘ dialog.
`GameLinkCatalogue.ParseHdrDatabase` now reads each `<a href="…/HDR-Gaming-Database/discussions/N">Game</a>` and still
the old `* [Game](…/discussions/N)` lines. Names are HTML-decoded (`Ratchet &amp; Clank`), as upstream, then matched by
#183's rule (no ™/®/©, straight quotes, ignoring case); only https discussion threads of that repository are taken. A
non-empty README in which no game is recognised already kept the last good cached copy (and a loaded copy through Full
Refresh); tests now pin that for this list, so a further format change leaves the links as they were instead of removing
them. The README as served on 2026-11-16 (138 games) is a test fixture.

### #260 — OptiScaler F5 DLSSNR takes the renamed release zip (#157 follow-up)

Upstream 2.9.1 `1f8a32e`. Since v0.1.37 janblade's F5 release names its package `v0.1.37-Optiscaler-F5.zip` instead of
`OptiScaler-DLSSNR-F5-*.zip`, so the #157 rule above (that prefix only) found no package and every F5 install and update
failed. Upstream now takes the first zip whose name contains "OptiScaler" and does not start with "Optional". RHI takes
the one `.zip` whose name has "OptiScaler" and "F5" as a word of its own, does not start with "Optional" and is no
forwarder, debug, symbol or source build (`OptiScalerPackageProvider.SelectF5AssetName`), so both name shapes install
and the companions ("Optional-Vendor-Neutral-DLSS5-Forwarder.zip", "Optional-F5Low-Update-….zip", the troubleshooting
`.md`) are never taken. Linux deviation: a release with two such packages (v0.1.28 published a test build beside the
package) is refused and both names are shown; upstream takes whichever GitHub lists first. The asset list of v0.1.38
(`/releases/latest` on 2026-11-16) is a test fixture; the lists of v0.1.37.2, v0.1.36, v0.1.30 and v0.1.28 are pinned in
the tests.

Not ported: `1f8a32e` also treats a game as Unreal when the scan says Unknown or UE3 but the game is listed in the
UE-Extended database or its manifest entry carries a UE4/UE5 engine hint (Phantom Blade Zero). That half is deferred to
Forgejo issue #287 (wave 14).

### #261 — DLSSG 20/30 FG Unlock installs the 0.4.x release package (#238 follow-up)

Upstream 2.8.9 `68856af` (its ⚙ settings dialog is a separate item and not ported here). Since 0.4.0
sdli1995/dlssg_for_sm86 ships as a release package (`dlssg-release-0.4.2-x64.zip`): one proxy DLL, `dlssg_sm86.ini` and
the `dlssg_sm86/` folder (backend, kernel packs, bundled DLSS-G runtime, XeSS-FG, SR). RHI still deployed only
`version.dll` from the repository tree (#238), so frame generation never started. `Dlssg2030UnlockDeploymentService` now
takes GitHub's latest release: the version is the tag, the package is the one `dlssg-release-<tag>-x64.zip` asset (none
or a second one refuses; upstream takes the first `dlssg-release-*-x64.zip`), downloaded under the GitHub release hosts,
checked against the digest GitHub publishes, cached and stamped like every release payload, and extracted with the
checked extractor (no `..`, no rooted or duplicate names; a package without `version.dll`, the INI or the backend,
kernel and runtime DLLs is refused). Only what a game needs goes in: the proxy, the INI and `dlssg_sm86/` without its
logs or Markdown; readmes, manuals, `docs/` and the manifest stay out.

Each DLL name installs the mod's own build for that name (`version.dll` from the package root, the others from
`alternatives/`), never a renamed copy, in the mod's order: version, winmm, dbghelp, dinput8, then dxgi or d3d12.
`winhttp.dll` is no longer offered; an install on it is moved to `version.dll` by Update (old proxy and its Wine
override out, `version.dll` and its override in; refused with a line saying what to do when `version.dll` is taken).
Every package file is recorded with its hash. Remove deletes those that still hold RHI's bytes and the mod's own
`dlssg_sm86/logs/*.log` (upstream deletes the whole folder), names any file changed outside RHI and leaves it, and
prunes the empty folders after the commit point; folders an install creates are journaled, so a crash before the record
is saved removes them again. The INI is written as the release ships it (no `Router=` any more: 0.4 picks the RTX 20 /
30 kernels itself).

Repair of old installs: a record from the single-DLL path (no release tag; commit, router and blob-hash keys) or under
`winhttp.dll` reads as an update in the check and in Update All ("… (single DLL, before the release package) → 0.4.2"),
and Update brings it to the package: the proxy's build, the whole folder, new files placed, files the release dropped
deleted, the old metadata dropped. Linux deviations: an INI still holding RHI's bytes is replaced by the release's, one
the user changed is kept as it is (upstream rewrites it, keeping six known keys); a package file changed outside RHI
makes the check could-not-check and the update refuse, as for the proxy (#238). The repair runs through Update All and
Automatic Updates, not as a start-up push (D1 = No). Unchanged: new installs stay RTX 20 only (RTX 30 uses Universal
MFG) and the GPU-generation choice stays in the ⚙ dialog but no longer changes the INI. Pinned in the tests: the 0.4.2
release JSON, the package's entry list and its `dlssg_sm86.ini` (2026-11-14).

## Parity notes (upstream 2.8.9–2.9.4, lane renodx-reshade)

### #268 — Generic Unity add-on from its new location; manifest `componentUrls.unity64` / `unity32`

Upstream 2.9.5 beta `a593a09` (ported ahead of the 2.9.5 release, as the triage asks). Voosh moved the generic Unity
add-on to GitHub Pages: the 64-bit link is now `https://notvoosh.github.io/renodx/renodx-unityengine.addon64`; 32-bit
stays on his `renodx-unity` release, as upstream does (its new Pages copy returned 404 on 15 Nov 2026; on 16 Nov it
serves, and upstream's manifest still points 32-bit at the release). The manifest's `componentUrls.unity64` / `unity32`
move either link without an app update (`GenericUnityAddonUrls`); upstream's manifest on 16 Nov 2026 (`7e234dc`) sets
both, and that `componentUrls` block is a test fixture. The add-on is recognised by file name, so the older links in
the database, the wiki, manifest snapshot overrides and install records all follow the current one: a game with the
add-on from the old release link shows an update, which installs from the new link. Checked on 16 Nov 2026: the old
release links still serve the 14 Oct build, so nothing breaks before the move; the new links serve the 16 Nov build.

When a link cannot be reached, the download tries the add-on's other links in upstream's order (new Pages, old
release, old Pages, ShortFuse's Pages); the record keeps the catalogue's link and the failure names every link tried.
Linux deviations: a manifest link is taken only when the RenoDX download policy allows it (HTTPS, a trusted host) and
it names a 64- or 32-bit add-on to match its key; anything else keeps the built-in link (upstream takes any http(s)
link). Each fallback link passes the same policy. A copy downloaded from a fallback link is not reused from the cache
for the catalogue's link, so it is downloaded again while that link stays unreachable. Not ported from `a593a09`: the
Built-in add-ons setting (Generic Depth / Effect Runtime Sync), which belongs to the ReShade settings lanes.

### #267 / #265 — Shader packs you downloaded are checked for updates at every start, manifest packs included

Upstream 2.8.9 `b59a60f` (`EnsureDownloadedPacksAsync`) and 2.9.4 `73af594` (`EnsureManifestPacksAsync`). RHI Linux had
no start-up check for staged packs at all: with "Download all shaders" on, start-up staged only the packs missing from
the cache (#194), and an install refreshed only the packs that game uses. Now every start checks each pack already in
the cache, whatever the setting and whether or not a game uses it, and stages it again when its source has a newer
build (`ReShadeShaderPackService.RefreshDownloadedAsync`); packs never downloaded stay on demand. The catalogue is
refreshed from the manifest first, so the packs it adds (SD Shaders, UltrawideSideGlass, BaBa Shaders…) are checked
with the built-in ones in one pass; upstream needs a second pass because its start-up check runs before the manifest
loads. Games get the new files with their next shader sync (ReShade install or update, Shaders/Add-ons, Update All of
ReShade), as before.

Two Linux gaps closed on the way. A branch-archive pack (`DirectUrl`, most packs) was keyed by its link alone, so it
never updated once staged; its version is now the server's ETag, else its Last-Modified, asked with a HEAD under the
shader-pack policy (upstream `ResolveDirectUrlVersion`). Each pack staged by an older build is therefore downloaded
once more after the upgrade. A pack that is one `.fx` / `.fxh` file (UltrawideSideGlass's release asset) was refused
("format is not supported"); it is now staged in the pack's `Shaders` folder, as upstream copies it. Upstream's
second 2.8.9 fix (shaders a pack removed or renamed staying behind) never applied here: each build of a pack replaces
the whole staged tree.

Linux deviations: when the server cannot be asked or sends no validator, the staged copy is kept (upstream downloads
it again on every check). The refresh runs under the pack cache gate; a ReShade install that staged a pack just
before could see it replaced before it copies it, as two installs at once already could, and its journaled sync then
fails and is undone rather than deploying a mix. Not ported: upstream's Zenteon+ link is a Patreon file link, which
the shader-pack download policy (GitHub, GitLab) refuses, so Zenteon+ shows in the list but does not download; adding
patreon.com as a trusted host is an owner decision.

### #287 — RenoDX: GitHub fallback for modder-hosted add-ons, second-download names, Unreal mod for known UE4/UE5 games

- **GitHub fallback** (upstream 2.8.9 `ef62d59`). An add-on at `<owner>.github.io/<repo>/<file>` (Marat's UE-Extended and
  other modders' sites) that cannot be reached is downloaded from `github.com/<owner>/<repo>/releases/download/snapshot/<file>`,
  then from ShortFuse's snapshot release; the generic Unreal add-on falls back to ShortFuse's release and Pages, as
  upstream. The same candidate list carries #268's Unity links (`RenoDxDownloadFallback`). Only network failures
  (unreachable, timeout, an HTTP error) move on to the next link; every link passes the RenoDX download policy, and a file
  that is not a PE add-on still fails the install. The record keeps the catalogue's link, so the update check asks that
  link; while it stays unreachable the check says it could not check.
- **Second-download names** (upstream 2.9.3 `870f4a9`, `AddonFileNames`, same pattern: ` (1)`, `(2)`, ` - Copy`,
  ` - Copy (2)`). A dropped or watched `renodx-x (1).addon64` installs as `renodx-x.addon64` and is recorded under that
  name. An install or update that finds a second download of the add-on it is placing no longer refuses it as a foreign
  RenoDX add-on. Linux deviation: upstream deletes such copies; RHI never deletes a user's file, so it renames each to
  `<name>.rhi-duplicate` (ReShade loads only `.addon`, `.addon32` and `.addon64`) inside the install's journaled
  operation (a failed install puts it back), names it in the result and keeps the list on the RenoDX record
  (`RenoDX.DuplicatesSetAside`). Removing RenoDX leaves those copies as they are. A second download of another mod is
  still refused as a foreign add-on. Not applicable: upstream's "Download from Discord" state for an unrecognised
  add-on (and its clearing after uninstall) never existed here; the Linux probe already counted any `renodx*` add-on as
  RenoDX, second downloads included.
- **Unreal mod for known UE4/UE5 games** (upstream 2.9.1 `1f8a32e`, the half #260 above deferred). When no named mod
  matches and the game's files do not look like Unreal, the generic Unreal mod is still offered when RHI's data says
  UE4/UE5: the manifest's engine hint, or the game's own engine hint (`Unreal Engine 4…` / `Unreal Engine 5…`; `Unreal
  Engine 3` does not count), or an entry in the UE-Extended database (`RenoDXdb-unreal.json`). A Unity game stays
  Unity, as upstream. Linux deviation: upstream also changes the engine tag to Unreal; here only the RenoDX choice
  changes. A game known only from the UE-Extended database keeps its engine hint, so the Unreal-only extras that read
  the hint (RenoDX's Engine.ini line, the UE-Extended switch) stay off until a manifest or scan hint says Unreal.
- **Unreal warnings in Info** (2.8.9): not applicable; the Linux Info dialog never showed upstream's "Common Unreal
  Engine mod warnings" block.

### #288 — Vulkan ReShade follows the chosen channel and version; update badge and Legacy for Vulkan games

Upstream 2.9.3 `badb2d9`. Upstream installs one Vulkan layer for the whole PC (`VulkanLayerService`); its three bugs
came from that shared copy: a Vulkan game kept the build the layer had (6.7.3 after 6.8.0), showed no ReShade update,
switched Nightly/Stable unreliably (it compared the layer DLL by size), and ignored a Legacy choice. Upstream now brings
the shared layer to the game's channel on install, resolves one channel for all Vulkan games (an explicit Stable never
hides another game's Nightly or Legacy) and asks to apply a Legacy version to all Vulkan games.

RHI Linux has never shared the layer: since #92 each Vulkan game has its own layer in the prefix, installed from the
channel and version that game chose, recorded on its ReShade record and checked by the same update check as the DLL
ReShade. So nothing was broken here; the three upstream cases are pinned as tests
(`ReShadeChannelTests.VulkanChannel.cs`): a layer on an older Stable shows the update and the update installs the newer
build; Nightly and Stable swap the layer DLL even when both builds are the same size (compared by content); a Legacy
version installs into the layer and stays pinned. Linux deviation: no "apply to all Vulkan games" question, because a
channel or version choice is that game's own and changes no other game's layer.

### #286 — RenoDX Source engine one-click install (partial: matching and folder rules; the DXVK chain waits for a game test)

Upstream 2.9.3 `870f4a9` / `3da42bc` (`SourceEngineSetup.cs`, `MainViewModel.SourceEngine.cs`): for the games of
`RenoDXdb-source.json`, ReShade as `d3d9.dll` beside the engine (bin for 32-bit games, the main folder for 64-bit,
Garry's Mod both), stable DXVK's `d3d9.dll` as `dxgi_dxvk.dll` loaded through ReShade's `[PROXY]`, `[GENERAL]
Disable=1`, a pre-made `ReShade2.ini`, `dxvk.conf` with the two `deferSurfaceCreation` lines, `-insecure` in RHI's launch
options with a VAC warning, a Source Engine badge, and update and uninstall for all of it.

Linux design (comment on #286, 2026-11-16). The chain's point is ReShade's second, Vulkan runtime on DXVK's device
(`Disable=1` turns the DirectX 9 one off). Under Wine, stable DXVK asks for `winevulkan.dll` before `vulkan-1.dll`
(found for #200), so ReShade most likely never gets that runtime and the game would be left with none; the runner's own
DXVK has the same problem. The route that reaches the same runtime is #200's: a DXVK build that asks for
`vulkan-1.dll` first (DX9 HDR's) as the game's `d3d9.dll` and ReShade on this game's Vulkan layer (#92). That needs a
game run to confirm, so this wave does not install the mod.

Ported now:
- Matching (`RenoDxSourceEngine`, upstream `GetDbSourceEntry` / `MakeGenericSourceFor`): a game the Source database lists
  (exact name, then ignoring punctuation and spacing) gets "Generic Source Engine", CommandGenius's add-on
  (`CommandGenius/renodx` snapshot release, 64- and 32-bit), with the database's comment as its note and WIP status, when
  no named RenoDX mod matches. The catalogue and Available mods share one database loader.
- Upstream's folder rules (`DetectTargets`, `PrimarySubfolder`), pinned with upstream's test layouts; Linux matches the
  `bin` / `win64` folders and engine DLLs ignoring case. Not used yet: the install folder change belongs with the
  one-click install and the install-folder work (#293).
- Installing the Source add-on is refused before any download with a plain EN/PT-BR reason that points to the RenoDX
  wiki's manual steps, so the add-on is never placed without the chain it needs.

Deferred to the owner test: the DXVK chain (above), `dxvk.conf`, the `ReShade2.ini` and `Disable=1` handling (not needed
on the layer route), Garry's Mod's second install, moving a 32-bit main-folder ReShade to bin, the Source Engine badge,
update and uninstall. `-insecure`: RHI Linux hands Steam, Heroic and Lutris games to their client, whose launch options
RHI does not edit, so the one-click install will say to add it there (upstream's own advice for Steam starts).

## Parity notes (upstream 2.8.9–2.9.4, lane library-updates)

### #266 — The app update check never says "up to date" when GitHub refused it (2.9.4)

Upstream 2.9.4 fixed its update check reading GitHub's hourly-limit refusal as "no newer version". Here the check
already told a refusal apart, but the GitHub API cache (#35/#244) answers a refused or rate-limited request with the
release list it holds (confirmed this session, or saved by an earlier launch while GitHub's advertised rate-limit window
runs), and the check read that older list as current, so RHI-Linux said "You have the newest version" when a newer
release might be out. The cache now marks every answer it stands in with (`X-RHI-GitHub-Stand-In: rate-limit` or
`refused`), and the app update check refuses such an answer: it says GitHub's hourly request limit is reached (and that a
token raises it) or that it could not check, and offers no release page. An answer GitHub confirmed less than a minute
earlier (the cache's ordinary reuse) still counts. Every other caller of the cache keeps the stand-in answers as before.
The start-up check stays quiet unless a newer release is found, as upstream. Not part of this lane: upstream 2.9.4's
handling of an expired or revoked token.

### #291 — Hidden games are left out of library-wide changes (2.8.9)

As upstream 2.8.9: a hidden game is never changed by anything that runs over the whole library. Left out are Update
All (and its availability: the button is off when only hidden games have RHI components), the automatic component pass
and its after-the-game-closed pass (#242; a game hidden while it waited is dropped), Follow newest DLSS / Streamline
(#177, the Linux form of upstream's automatic DLSS / Streamline updates), and every Settings "Apply to all games" or
"Deploy to all" button: ReShade settings, ReLimiter settings, peak brightness, the INI mass deploy (and the counts their
questions name) and the OptiScaler overlay key. The game's own buttons still work. One rule
(`GameLibraryFilter.Unattended`) decides it for every caller. Linux deviation: upstream's Update All already skipped
hidden games; here it did not, so it changes with this port. Not changed: games set to the global shader / add-on set
still follow it when the global set is saved (a choice the game itself made, not an apply-to-all), and the DLSS batch
dialog lists every game because the user ticks each one.

### #292 — Update cadence across restarts, hourly with a token, link lists every 4 h, Refresh rescans (2.8.9–2.9.3)

- **Cadence.** The automatic pass (component updates and Follow newest DLSS / Streamline) runs every 4 hours, every
  hour while a GitHub token is in use (upstream 2.9.2; the interval is read before each wait, so adding or removing a
  token changes the next one). The time of the last pass that ran to the end is kept in
  `~/.local/share/rhi-linux/update-check.json` (a cache: unreadable means "never", and it is replaced). A start waits
  what is left of the interval since that pass, or runs at once when it is over (upstream 2.8.9: "at least every 4
  hours, even if you restart RHI"). Linux difference: before this, every start ran a pass at once and any Settings save
  with automatic updates on ran another; now only turning automatic updates or a Follow toggle on runs one at once.
  A manual Update All counts as a pass and restarts the countdown, as upstream's manual check does. The hidden
  `UpdateCheckIntervalHours` setting (always 4) is no longer read. A token GitHub rejected counts as no token.
  Upstream's floor of 5 minutes before the first periodic check has no counterpart: its launch check runs separately,
  here the first pass is that check.
- **Ultra+ and ultrawide fix lists** are kept 4 hours instead of a day, and a list loaded earlier in the session is
  asked again once it is older than that (the copy in use stays until the new one is in; a list that cannot be fetched
  is asked again after 5 minutes). The HDR Gaming Database list stays at a day. Full Refresh already downloaded every
  list again (#174), as upstream 2.8.9 now does.
- **Refresh rescans the selected game** (upstream 2.9.3 `5b0054b`): Refresh here was always the full refresh (every
  detector, every cached analysis dropped), so DLSS a game update added or removed already showed at once. Pinned by a
  test, no code change.

### #289 — Update History records every component (2.9.3)

Upstream 2.9.3 (`487b4fc`) added Update History entries for components its own update paths missed. Here every
install, update and removal through the managed component path (Install, Update, Update All, the automatic pass,
dependencies installed on the way) was already recorded by diffing the deployment ledger (#18/#112), today's
list (Universal MFG, DLSS Enabler, NR Cost Scaler, Ultimate ASI Loader, RE Framework, UE DOF Fix, ReShade on either
channel, the Addon Manager) and the NR DLL swap. DLSS5 Feeder and DX11 Bridge are listed under their own names
(pinned). The gap was DLSS and Streamline files outside Follow newest: the DLSS batch, the DLSS defaults and the DLSS
panel's swap and restore wrote nothing. They now record one entry per DLL family ("DLSS SR (nvngx_dlss.dll)",
"Streamline (sl.*.dll)") with the version before and after (read from the DLL), and a failed swap with its reason.

One generic hook, `ComponentHistory.Record(game, component, action, from, to, …)`, keyed by `ManagedComponentKind`,
writes for any change made outside the managed path; an update is written only when the version changed (upstream
`ShouldRecord`), a failure always. A new component recorded through the managed path needs nothing but its name in
`LinuxManagedComponentService.DisplayDependencyName` (or `ComponentHistory.NameFor`). Not applicable: DXVK (the runner's,
never RHI's here). Nexus Mods files reach RHI as dropped add-ons or Luma packages and are recorded on those paths.

### #295 — ⚙ and Settings saving fixes from 2.8.9, checked one by one

- **Add-on watch folder that does not exist** (fixed): Settings saved any typed folder and the watcher then said it
  could not open it. Save now keeps the previous folder, saves the rest and says the folder does not exist. An empty
  box (Downloads) and an unchanged folder are saved as before.
- **Invalid ReLimiter custom FPS** (fixed): an empty or out-of-range custom box was saved as "no cap" without a word.
  It now keeps the saved cap and the line says the value must be a whole number from 20 to 1000.
- **Keys saved on press, typed folders and peak brightness saved on blur** (Linux deviation, not ported here): the
  Linux Settings dialog saves everything with its Save button, so no typed value is lost to a missing Enter. Save on
  change comes with the Settings restyle (phase 2 of wave 14). The overlay, screenshot and ReLimiter OSD key editors
  hand their value to that Save.
- **A failed ⚙ save reverts and says why** (already so): the ⚙ presenters save through `ParityPersistence.TryPersist` or
  the operation result; a refused save puts the control back to the saved value and shows the reason.
- **"Deploy dxvk.conf" reporting success after a failed copy** (not applicable): Linux has no separate dxvk.conf deploy
  button; DXVK HDR writes `dxvk.conf` journaled and its failure is the operation's failed result.
- **Addon Manager switching an add-on back off silently after a failed download** (not verified in this lane): the
  Addon Manager's update path reports a failed download as could-not-check; the selection-apply path was not traced to
  the end. Left open on #295.
- **RE Framework "Delete _storage_" stuck on "Deleting…"** (already so): the button is restored in a `finally` and the
  result line is shown.
- **ReShade hotkey "Applied!" with no reshade.ini** (not applicable): Linux has no per-game hotkey Apply button; keys
  reach games through "Apply to all games", whose line counts only the ReShade.ini files it wrote.
- **ReLimiter ⚙ with a separate add-on folder** (already so since #255): the ⚙ finds relimiter.ini in the AddonPath
  folder.

### #294 — Detection fixes (2.8.9–2.9.3)

- **Helper libraries and exes made games look DX12** (fixed; upstream 2.9.2 `93d0fcb`): the graphics-API detector's
  last step reads nearby binaries, and FFmpeg (`avutil`, `avcodec`, …), Qt 5/6, SDL 2/3, CEF, ANGLE (`libEGL`,
  `libGLESv2`) and `d3dcompiler` name D3D11/D3D12 whatever the game renders with (OpenMW, an OpenGL game, read as DX12).
  They are skipped there now, with upstream's list, and so are RHI's own helper exes (`gitc-uplift-helper64.exe`,
  `dlss5-feed-host64.exe`) and crash reporters / uninstallers.
- **GOG games read as 32-bit from the uninstaller** (already so; upstream 2.9.3 `870f4a9`): upstream took the biggest
  exe in the folder for bitness. Here bitness comes from the chosen game exe, and the chooser has always skipped
  uninstallers, redistributables and crash handlers by name (`unins000.exe`, `UnityCrashHandler64.exe`). Pinned with
  Ghost Song's layout (a 3 MB 32-bit uninstaller beside a 650 KB 64-bit game exe).
- **Running-game tracking following the crash reporter** (already so; upstream 2.9.3 `2984022`): the running probe
  matches the chosen exe's full path in `/proc/<pid>/cmdline`, and the chooser never picks `CrashReportClient.exe`
  (pinned with Phantom Blade Zero's layout).
- **DLSS / Streamline for Unreal games outside `Binaries`** (already so): DLSS detection reads the game's whole file
  index (depth 8 from the install root), not only the exe's folder, and manifest `dlssPathOverrides` /
  `streamlinePathOverrides` (#155) reach deeper layouts such as Mafia: The Old Country's.
- **Config button and UE-Extended Engine.ini with several PCGW config paths** (not applicable): RHI Linux never used
  PCGamingWiki's config paths (see #12); the Engine.ini RenoDX writes (#217/#218) has its own lookup, which this lane
  did not change.

### #290 — Block Steam updates (2.9.4)

Upstream 2.9.4 adds "Block Steam updates" to the Game settings cog beside a Steam game's title: the game's Steam app
manifest is made read-only so Steam cannot update the game, and turning it off lets Steam update again. Ported as a
header action beside Favourite / Hide ("Block Steam updates" / "Allow Steam updates"), shown only for a Steam game whose
manifest RHI finds; Linux has no Game settings cog yet (the header is restyled in phase 2 of wave 14). The tooltip says
what it does to the user's Steam: Steam may show the game as needing an update, or report a disk write error, while it
is blocked.

Linux details: the manifest is `steamapps/appmanifest_<appid>.acf` in the game's own Steam library (the parent of
`steamapps/common/<installdir>`), so native and Flatpak Steam libraries and extra library folders all work. RHI checks
that the manifest's `appid` and `installdir` are this game's before touching it. Only the file's mode changes (all write
bits off; Allow gives the owner write back), never its contents. Blocking is refused while Steam is updating the game
(`StateFlags` update running, started or paused); an update that is only queued can be blocked, which is the point. The
state is read from the file every time, so if Steam replaces the manifest with a new file the switch reads "Block"
again. Not journaled: nothing in the game folder changes, and the change is undone by the same switch.

### #293 — Game folders: Remove from RHI and Removed games (2.9.0–2.9.3; partial)

- **Remove from RHI** (ported): a header action beside Hide for every detected game. The game is gone from every list,
  Hidden included, and a scan does not bring it back: the library file keeps it under `RemovedGames` by its library key
  (omitted while empty, so the file keeps the shape older builds wrote). Upstream places the button in Management;
  Linux has no Management section yet, so it sits with Favourite / Hide (restyle in phase 2). A game the user added is
  removed with the existing "Remove manual game", as upstream deletes it. Linux deviation: upstream offers to uninstall
  the game's components at the same time; here they stay installed and the tooltip says to remove them first if wanted.
- **Settings → Removed games** (ported, as its own Settings tab; upstream Tools & Maintenance › Troubleshooting): every
  removed game, newest first, with Restore, which drops it from the list and rescans so the game comes back.
- **Reset install folder** (by design here): Linux's Edit game keeps one install-folder override per game; clearing it
  goes back to the folder the scan found, so upstream's "goes back to an earlier pick" bug has no counterpart.
- **"Folder moved → offer Refresh instead of a folder browser"** (not applicable): RHI Linux never opens a folder
  browser for a missing game folder; it says the folder is missing and offers Refresh and Repair after move (#38).
- **Not ported in this wave (deferred, see #293):** custom game folders (Settings list, scan on start / Refresh / Scan
  now, review list with name, exe choice and "Check the exe", ignore list, and on Linux a runner / prefix choice like
  manual games), and install-folder migration (moving a game's mods, settings and presets after Change / Reset install
  folder, automatic when RHI corrects the folder, manifest `installPathMigrations`). Both need journaled moves of user
  and RHI files across folders and a review UI; they were left for a follow-up so this lane's other items could land
  with their tests.

## Parity notes (upstream 2.8.9–2.9.4, lane extras-addons)

### #283 — Load ReShade as ASI (2.8.9)

Upstream 2.8.9 `fdcc5b4` (`MainViewModel.ReShadeAsi`). ReShade's ⚙ has a "Loading" section with **Load ReShade as ASI**
(Off / On). On renames RHI's ReShade to `ReShade64.asi` (`ReShade32.asi` on 32-bit games) for an ASI loader; RHI does
not install a loader (ASI Loader in Extras, or the user's own). Installs and updates keep the name: the setting wins
over the user's DLL name and the manifest's, and an installed ReShade keeps its recorded name on update. Off renames it
back to the user's DLL name, else the automatic one (manifest, then the graphics API's). While it is on, the DLL-name
dropdown is locked to the `.asi` name and says where to turn it off, as upstream greys it. Not offered for Vulkan games
(ReShade runs there as a layer). The setting is stored per game beside the DLL-name choices (`dll-names.json`,
`reshadeAsi`).

Linux adaptation: an `.asi` gets no Wine DLL override (only an ASI loader loads it); the rename goes through the #196
in-place rename, so the file, the Wine override (the old name's restored, none for the `.asi`), the saved setting and
ReShade's record move in one journaled operation, and a refused or failed rename leaves the setting as it was.
Deviations: upstream backs up a game file already at the target name as `.original` and renames over it; RHI Linux
refuses and names the file (game files are never overwritten by a rename). The switch is refused, with the reason,
while another component decides ReShade's name (the OptiScaler chain's `ReShade64.dll`, ShortFuse, the DirectX 9
translation, DX9 HDR, the XNA GAC install). Without ReShade installed the ⚙ is not shown on Linux (the ⚙ is for an
RHI-managed ReShade); the setting can still be saved through the service, and the DLL-name dropdown then shows the
`.asi` name the install will use.

### #284 — ASI Loader: Hooked names for extras, names in use greyed, renames followed (2.9.0)

Upstream 2.9.0 `a54bb3e` (`HookedDllNames`, `MainViewModel.UalHooked`, `DetailPanelBuilder.Extras.DllNames`). With RHI's
ASI Loader installed as X.dll, the DLL-name lists of 20/30 FG Unlock (RTX 20 FG Unlock on Linux), Universal MFG,
RTX Encore and DLSS Enabler offer XHooked.dll right after X.dll, marked "loaded by ASI Loader": ASI Loader loads it as
the real X.dll, so the extra runs alongside it. 20/30 FG Unlock copies its X.dll build under the Hooked name; RTX
Encore offers no Hooked name for its Bink names (they already use Hooked for the game's own Bink file). Names another
RHI component's file uses in the game folder are greyed with that component ("in use by …") and cannot be chosen; a
Hooked name the game's own DLL already sits under is greyed as the game's. Installing ASI Loader under an extra's name
moves the extra to XHooked.dll and its record (file and recorded name) follows, in the same journaled operation and
ledger write as ASI Loader's install; the install says which extra moved. Removing ASI Loader moves it back to X.dll.
An existing Hooked file stops the install, as before. Reinstalling ASI Loader never chains its own file (Linux: an
installed ASI Loader is updated in place; Install refuses while it is installed).

Linux adaptation: Wine overrides follow each rename. The extra's own `native,builtin` override for X.dll goes back first
and ASI Loader sets its own; XHooked.dll needs none (Wine loads a DLL it does not ship from the game folder); on removal
ASI Loader's override goes back and the extra gets X.dll's override again. A crash in between is rolled back by the
journal (the extra back under X.dll with its override). Deviations: a Hooked name is refused at install unless ASI Loader
is installed under the name it stands in for (upstream relies on the list only offering it then); RTX Encore's Hooked
name is offered, but the component scan that finds a user-installed RTX Encore still looks under its documented names
only. Upstream also moves RHI's `.original` sentinel with the file; Linux records own the files, so there is no sentinel.

### #282 — Display Commander: main settings in its ⚙; a fresh install writes DisplayCommander.ini (2.8.9/2.9.3)

Upstream 2.8.9 `8c89336` (`DcCogButton_Click`) and 2.9.3 `eacc622`. Display Commander's ⚙ has its main settings, under
Display Commander's own names, written at once to the game's `DisplayCommander.ini` (`[DisplayCommander]`, beside the
add-on, ReShade's AddonPath honoured): **Frame Rate** — FPS Limit (No limit, upstream's VRR presets 59–431, or Custom…,
20–1000 with up to three decimals, `fps_limit`), FPS Limiter Mode (DC's fps limiter / NVIDIA Reflex,
`fps_limiter_mode`), Inject Reflex (`inject_reflex`), Window Mode (No changes, Borderless fullscreen, Borderless
windowed, No exclusive fullscreen, `WindowMode`); **Reflex** — Auto Configure Reflex, Enable Reflex Sleep Mode, Use
Reflex Markers, Generate Reflex Markers (`Reflex*`); **Other** — Win+Up grace time (Off, 1, 2, 5, 10 seconds, Always;
a value set by hand is shown as it is), and Open DisplayCommander.ini. Absent keys read as Display Commander's defaults.
Only these keys and values are written (anything else is refused and changes nothing); every other line of the file is
kept. "Deploy DisplayCommander.ini" stays in the ⚙ as before (#181). The DC menu names are kept in English in both
languages, as they appear in Display Commander's own menu.

Linux adaptation: each change is journaled on Display Commander's record (a crash before the record is saved puts the
file back), goes through the one user-INI write path (a read-only lock is kept, a link is followed only to a same-named
file in the game) and is refused while RHI's patch-target guard refuses INI writes. NVIDIA Reflex under Proton needs
NVAPI enabled (the tooltip says so); Win+Up may be taken by the desktop before the game sees it (the tooltip says so).
Upstream's running-game warning is the intro line's "change these with the game closed".

Fresh install: upstream 2.9.3 fixed a Windows packaging bug (the template was looked up beside RHI.exe and missing from
the single-file build). RHI Linux was not affected: a new Display Commander install already creates `DisplayCommander.ini`
from the user's template, or RHI's shipped one (`[DisplayCommander]`, the same as upstream's file at `RHI-2.9.4`), when
absent; `GitHubReShadeAddonDeploymentServiceTests` pins it.

### #277 — HSF frame generation (2.9.3; HSF 0.9.8)

Upstream 2.9.3 `e5075bd` / `7211cc9` (`HsfService`, `DetailPanelBuilder.Extras.Hsf`, `GpuInfoService`), unlocked for
everyone in 2.9.4. HSF (Hunter's Sloppy Frames, Capt-Hunter/HSF) is a ReShade add-on that adds DLSS frame generation,
up to 6x, to DirectX 9, 11, 12 and Vulkan games (Vulkan: 64-bit). One click installs the add-on for the game's bitness,
`HSF_streamline/` (Streamline, `nvngx_dlssg.dll`, the 64-bit helper, with `LICENSE.txt`, `README.txt` and `licenses/`
inside it), `HSF.ini` beside the add-on (ReShade's AddonPath honoured), `HSF.fx` in the game's `reshade-shaders/Shaders`
and iMMERSE Launchpad (unless its box is unticked). The release list is read (pre-releases included, drafts skipped);
the zip goes through the trusted-download policy (`DownloadSources.Hsf`, GitHub hosts, 128 MiB) and GitHub's published
digest; a release without the files RHI knows is refused before anything changes. DirectX 12 games get HSF's D3D12 route
in HSF.ini before the first launch (`InGameDlssg=2` with the game's own Streamline, else `SlBringIn=2`), following the
Rendering API override; a route HSF or the player set is never changed. Update brings the release's clean HSF.ini with
the player's settings carried over (upstream's merge: `Dx9ExRun`, `Presenter`, `Generated`, `Pacing` dropped, other
sections kept) and is part of Update All. The ⚙ shows the installed version, the DirectX 12 mode (HSF decides, the
game's own Streamline, HSF's own; "HSF decides" is remembered so RHI never pre-sets it) and turns Launchpad off for
LumeniteFX or the game's own motion; opening it moves Launchpad to the top of the game's ReShade preset where HSF listed
it lower down (only the order changes). On RTX 20/30/40 cards the row offers Universal MFG Unlocker after the install
(from RHI's existing NVIDIA probe, `/proc/driver/nvidia` model names; not on RTX 50 or an unknown card). For Vulkan games
the row warns when a DirectX ReShade (`dxgi.dll`) in the game folder keeps the Vulkan layer out. The row shows the
anti-cheat warning before an install. Real data pinned in the tests: the release list (2026-11-16), the HSF-0.9.8.8.zip
entry list and its HSF.ini.

Linux adaptation: every install, update, removal and ⚙ change is journaled on HSF's record. Files RHI did not place are
never overwritten: a hand-installed `hsf.addon64/32`, an `HSF_streamline` folder or an `HSF.fx` already there refuses the
install; an `HSF.ini` already there is kept as it is (and not pre-set); a Launchpad file a user's own iMMERSE pack placed is
left and never recorded. Removal takes RHI's files and the logs HSF writes (beside the add-on and in `HSF_streamline`);
HSF's camera profiles (`HSF_profiles/`) and anything else stay and are named (upstream deletes the folders whole). A file
RHI placed that changed since refuses update and removal.

Deviations and partial items:
- Placement: Linux lists Universal MFG and the other unlocks as component rows, so there is no "MFG Unlocks" group yet; HSF
  is a row in Extras (below ShortFuse) until the Phase 2 lanes restyle the page (#296/#297).
- ReShade: HSF needs RHI's ReShade installed first (the row says so); it does not install ReShade as a dependency.
- iMMERSE Launchpad: the nr lane's reusable Launchpad service (#274) was not on `forge/w14/nr` yet, so a minimal step
  (`ILaunchpadStep` / `ImmerseLaunchpadStep`) takes Launchpad's effect, includes and blue-noise texture from the iMMERSE
  branch archive (`DownloadSources.ShaderPacks`, no digest published) and HSF records them. To unify at the merge.
  Upstream moves Launchpad first whenever the game is not running; Linux does it when the ⚙ opens and after an update.
- Universal MFG with HSF: the offer calls Universal MFG's own install unchanged (#278 owns it); upstream's 32-bit
  placement in HSF's folder and the install without the RTXMFG-XeSS folder are not done here (64-bit games only), for
  #278 to add.
- D3D12 route: a game's Streamline is found by `sl.interposer.dll` under its folder (8 levels); its version is not read,
  so a Streamline 1.x game gets `InGameDlssg=2` (HSF's value 2 lets HSF fall back by itself).
- The ManagedComponentKind value is 30 (a gap, not the next free number) so another wave-14 lane's new kind cannot take it
  unnoticed; it may be renumbered at the merge before release.

## Parity notes (upstream 2.8.9–2.9.4, lane optiscaler)

### #264 — OptiPatcher reads its build commit again (2.9.3)

Upstream 2.9.3 `487b4fc`. Since November 2026 the OptiPatcher rolling release notes read `**Build commit:** ^G<sha>`
(a literal caret and G before the commit). RHI's pattern wanted the commit right after the label, found none and fell
back to the asset digest (`sha256-…`), so the card and the update log showed a digest instead of a build. Updates were
still detected (the digest changes with every build), but each build was cached and named by digest. RHI now takes
upstream's pattern: after `Build commit:` and its bold markers, a `^` and any one character are skipped, then anything
that is not hex or a line break, and the commit is 7–40 hex digits (`OptiPatcherProvider.ParseBuildCommit`). A real
control character (BEL) before the commit is read too. A release that names no commit still falls back to the asset
digest; upstream keeps the previous version string. The release as served on 2026-11-16 is a test fixture
(`optipatcher-rolling-2026-11.json`), with upstream's `OptiPatcherVersionTests` cases.

### #273 — OptiScaler fixes from 2.8.9–2.9.3

Each upstream fix was checked against the fork. One was a real bug here; the others never existed on Linux and are
pinned by tests.

- **OptiPatcher where OptiScaler 10 builds read it (2.8.9), fixed.** RHI put `OptiPatcher.asi` in `<game>/plugins`,
  which only the 0.9 stable build reads. Nightly, DLSS NR, F5 and the forks bring an `OptiScaler/` folder and read
  `OptiScaler/plugins`, so OptiPatcher never loaded with them. As upstream's `GetPluginsDir`, OptiPatcher now goes to
  `OptiScaler/plugins` when the package has that folder, else to `<game>/plugins`. Installs made before this are
  repaired through the update check: an OptiPatcher RHI recorded in `<game>/plugins` beside an `OptiScaler/` folder
  reads as an update ("OptiPatcher is in the game's plugins folder …"), and Update moves it, journaled, with the newest
  build or, when that cannot be downloaded now, with its own bytes. The old `plugins` folder is removed only when RHI
  created it and it is empty; a game's own plugins stay. Linux deviation: upstream moves the file for every game at
  start-up; RHI does it through Update and Update All, like every other repair of a game folder. When a file RHI does
  not own already sits at the new place, OptiPatcher stays where it is.
- **Updates keep `plugins/` and the custom provider folder (2.8.9, 2.9.3), never existed.** Upstream's update emptied
  the `OptiScaler/` folder except `plugins` and `custom`. RHI's update replaces only the files its record owns and never
  deletes a folder, so `OptiScaler/plugins`, `OptiScaler/custom` (custom upscalers and FG providers such as ANR FG) and
  `<game>/plugins` keep their files. Pinned by a test.
- **FG Nvngx Override greyed with "DLSSG via Nvngx" (2.9.3), never existed.** The Linux ⚙ greys no row; the override
  is offered, written and read back with that FG input. Pinned.
- **Upscaler API changing the upscaler (2.8.9), not applicable.** The Linux ⚙ has no Upscaler API selector: DirectX
  11, DirectX 12 and Vulkan each have their own upscaler row. A test pins that changing one leaves the others alone.
  The ⚙ restyle to upstream's layout (phase 2) brings the selector; this test then covers it.
- **F5 showing the Stable version (2.9.2), never existed.** The version shown is the record's, which is the F5 release
  installed. Pinned in the F5 install test.
- **Vulkan DLL-name reset to winmm.dll (2.8.9), not applicable.** RHI Linux does not install OptiScaler on Vulkan
  games (only DirectX 11/12 are offered), so the name never resets there.
- **A preset saved on F5 switching the game to F5 (2.8.9), Linux deviation kept.** Presets on Linux do not carry the
  channel (#187): changing it reinstalls OptiScaler, which the ⚙ does not do while it is installed. Applying a preset
  never changes the channel. Revisited with the presets panel (#269).
- **Replace OptiScaler.ini using the NVIDIA template (2.8.9)** is part of "Replace with a fresh copy" (#269).

### #270 — OptiScaler versions: Default version, DXGI Spoofing, installed vs latest, Release pin, Remove old Nightly builds (2.9.0–2.9.3)

- **Default version (2.9.0).** Settings, beside OptiScaler's GPU Type and DLSS Inputs: Stable, Nightly, DLSS NR or F5
  DLSSNR. A game with no version chosen in its ⚙ takes it; a game with OptiScaler installed shows the version it has.
  The per-game choice now stores Stable too (it used to remove the entry), so Stable picked by hand is never replaced
  by the default. Linux deviation: a game whose Stable choice was saved before this build has no entry and takes the
  default. Forks and custom builds join the list with #271.
- **DXGI Spoofing (2.9.0).** In the ⚙ on every version, after the frame-rate limit: Auto (Default), True or False,
  written to `[Spoofing] Dxgi`. Presets capture it like every other ⚙ setting.
- **Installed vs latest, Check now, Update (2.9.2).** A line at the top of the ⚙: "Installed: x · Latest: y (state)".
  Installed games use the same check as Update All (a pinned build or release reads "pinned to …, so it isn't updated");
  a game without OptiScaler shows the newest release the install would take, or its pin. Check now asks GitHub again
  (RHI's GitHub answers are revalidated with their ETag on every request, so there is no cache to clear); Update shows
  only when an update applies and runs the component row's update, journaled as always. When GitHub cannot be asked the
  line says so and points to the GitHub token, as upstream.
- **Release (2.9.2).** A Release row in the ⚙ for Stable, DLSS NR and F5: "Latest (auto-update)" or one of the
  channel's releases (GitHub's newest 30, newest first; Stable without prereleases; only releases that publish a package
  RHI installs, so the display-filter tool in the DLSS NR repository and F5 v0.1.28's double package are not offered).
  As upstream, it can be changed only while RHI's OptiScaler is not installed; the next install takes that release by
  its tag (never "latest"), records it, and Update / Update All leave the game on it. Nightly keeps its own Nightly
  build row (#156/#232). Linux deviations: upstream stores one per-game value for the Nightly build and the release; RHI
  keeps the one per-game slot but saves a release with its channel (`F5+v0.1.29`), so a pin made for one channel never
  applies to another. Tags are limited to letters, digits and `. _ + -` before RHI asks GitHub for them. Pinned
  releases come from the same GitHub release hosts, digests and size checks as the latest ones.
- **Remove old Nightly builds (2.9.2).** Settings, beside the Default version; off by default. After a Nightly
  download RHI deletes the cached Nightly builds except the newest, the one just downloaded and every build a game uses
  (installed Nightly pins and saved Nightly build choices). Only RHI's own `~/.cache/rhi-linux/optiscaler/nightly/
  nightly-YYYYMMDD` folders are touched; a folder that cannot be deleted is logged and kept. Linux deviation: upstream
  skips folders still unpacking; RHI's cache entries are stamped, and a deleted build is downloaded again when needed.
- **Newer release before a cached copy (2.9.2), never existed.** RHI asks GitHub for the newest release on every
  install and update and keeps each release in its own stamped cache folder, so an older cached build is never taken for
  the latest. Pinned by a provider test.
- The release lists of Stable, DLSS NR and F5 as served on 2026-11-16 are test fixtures.

### #269 — OptiScaler ⚙ Presets panel: recommended presets, slots dropdown, Replace with a fresh copy (2.8.9)

- **Every version.** The Presets panel shows for every OptiScaler RHI manages, Stable included, and now sits near the top
  of the ⚙, above the OptiScaler.ini settings (upstream opens the ⚙ on it). A preset setting the ⚙ does not offer on
  that version (Stable has no Streamline row) is counted as not offered, as before.
- **Recommended presets.** One button per preset with its description as the tooltip; the one matching the game's
  current settings is ticked (every setting it sets that the ⚙ offers has that value, and Universal MFG Unlocker is
  installed when the preset installs it). The list is upstream's `optiscaler_presets.json` (RankFTW/RHI `main`), read
  in the background at start from raw.githubusercontent.com (64 KiB cap, `docs/download-trust.md`), cached at
  `~/.cache/rhi-linux/optiscaler_presets.json`; until it is read once, and whenever it cannot be read, upstream's
  built-in copies are used, so new presets arrive without an app update. Upstream field names map to the Linux ⚙
  setting ids (`deployStreamline`/`deployDlssEnabler` → the Streamline / DLSS Enabler row, `streamlineVersion: latest`
  → the newest Streamline the row offers, `fgEnabled`, `fgInput`, `fgOutput`, `fgNvngxReplacement`, `hudFix`,
  `forceReflex`, `useGamesReflexMarkers`, `dxgiSpoofing`); unknown fields are ignored. A preset applies through the ⚙'s
  own setting path, the stack first, and stops at the first setting that fails. UMFG XeFG then installs Universal MFG
  Unlocker when the game lacks it, through that component's own install (its service is unchanged here). Linux
  deviation: the ANR FG preset (2.9.3) is left out until RHI installs ANR FG (#272).
- **Your presets.** The four slots are one dropdown with Apply, Save current (Overwrite for a slot in use) and a "…"
  button to rename or clear the slot. Presets still carry no channel (#187, #273).
- **Replace with a fresh copy….** At the bottom of the ⚙; asks first. The game's OptiScaler.ini becomes the template a
  fresh install of its version takes (the user's template for its channel, GPU type and DLSS Inputs, #97), which is
  upstream's 2.8.9 fix, then LoadAsiPlugins is turned on and, while RHI's ReShade is chained as ReShade64.dll,
  LoadReshade, as the install does. Journaled; the record takes the new file's hash. Linux addition: the previous file
  is copied to `~/.local/share/rhi-linux/optiscaler-ini-backups/` first. Upstream's mass "Deploy OptiScaler.ini" to
  every game has no Linux counterpart and is not added.
- Upstream's `optiscaler_presets.json` as published with RHI 2.9.4 is a test fixture.

### #271 — OptiScaler forks (manifest `osForks`); custom builds deferred (2.9.2/2.9.3)

**Forks, ported.** Venom, NR PreSR MFG, Feeder NR and OpenNR sit after Stable, Nightly, DLSS NR and F5 DLSSNR in the
⚙ version list and in the Default version setting, each with its manifest description as its tooltip and line. Each is
installed, updated, pinned (#270 Release row) and removed like the built-ins:

- Releases come from the fork's own GitHub repository only: newest by publish date with prereleases (upstream lists
  them for forks, so Venom's dated nightlies count as its newest), drafts skipped; the asset is the best by upstream's
  `ScoreAsset` (.7z or .zip; not "Optional-…", forwarder, troubleshooting, source, symbol or checksum files;
  "optiscaler" in the name and .7z win), ties to the newest upload. The asset link must lie under
  `https://github.com/<repo>/releases/download/`, else the release is refused. Downloads use the OptiScaler host policy,
  GitHub's published digest and the per-tag stamped cache (`docs/download-trust.md`).
- Every fork is an OptiScaler 10 build: the ⚙ gets the Streamline / DLSS Enabler and Reflex rows, OptiPatcher goes
  to `OptiScaler/plugins` (#273) and a new install takes the Nightly INI template, as upstream.
- NR PreSR MFG and Feeder NR are marked `needsNrDll` in the manifest: NVIDIA's `nvngx_dlssnr.dll` from the DLSS
  manifest goes beside them under the one-owner rule (#157), like DLSS NR and F5. They do not need F5's forwarder.
  OpenNR ships its own NR runtime and gets none.
- Linux deviation: the fork list is upstream's manifest `osForks` as of RHI 2.9.4, built in, because the version list,
  the per-game choice and the install records name each version by a fixed value. A fork upstream adds to its manifest
  later needs an RHI Linux update; a fork removed upstream stays installable here. Upstream's `assetPattern`,
  `includePrereleases` and `disabled` fields are unused (no current fork sets them).
- The release lists of the four repositories as served on 2026-11-16 are test fixtures, with upstream's
  `OsForkTests` asset cases.

**Custom OptiScaler builds: deferred.** Settings › Components › Custom OptiScaler builds (add from a link, a zip, 7z
or rar, a folder or an OptiScaler.dll, also by drag and drop; Replace, Rename, Remove; the "DLSS NR DLL" tick; never
auto-updated; Replace offers to redeploy the games using it) is not in this wave. It needs version values that are not
fixed at build time (the version list, per-game choice, Default version and install records), an import path through
the archive checks for user-supplied files and links (a trust warning for links outside the download policy), and new
Settings rows. Tracked on #271.

### #272 — OptiScaler custom FG providers in FG Nvngx Override (2.9.3); All Settings, saved inis and ANR FG deferred

- **Custom FG providers, ported.** FG Nvngx Override (the ⚙'s FG Nvngx Replacement row) lists, after OptiScaler's own
  choices, every custom frame generation provider in OptiScaler's custom folder by name: a folder in
  `OptiScaler/custom` (OptiScaler 10 builds, else `custom` beside OptiScaler) holding a `dlssg` folder with a DLL, as
  upstream's `FindCustomFgProviders`. The name is written as `FGNvngxReplacement`. Linux addition: a folder name with a
  line break, `=`, `[` or `]` is not offered, so it cannot break the INI. Updates keep that folder (#273).
- **Deferred to a later wave (tracked on #272):** the All Settings section (every OptiScaler.ini setting with the
  options the installed build's own ini lists, search, Apply with a backup, changes re-applied after reinstall, update or
  version switch); Save ini / Load saved ini; and the ANR FG preset (Apex Compute's closed-source DLL, verified against
  its published hashes, `nr_fg.ini` kept, the closed-source notice first; #285's shared notice is in the extras lane).
  Until RHI installs ANR FG, the recommended presets leave the ANR FG preset out (#269).

## Parity notes (upstream 2.8.9–2.9.4, lane nr)

### #275 — DLSS5 Feeder 2.0: built-in motion vectors, added to the preset, matching add-on/host pair (2.9.3)

Upstream 2.9.3 `cdff33f` (`FeederPreset.cs`, following Feeder 2.0.0's own `Install-DLSS5Feeder.ps1`). Ported as
`FeederPreset` and `ReShadePresetText` (pure text edits; upstream's `FeederPresetTests` run here unchanged) and
`ReShadePresetFile` (which preset a game uses: `[GENERAL] PresetPath` in ReShade.ini relative to the game's binary
folder, else `ReShadePreset.ini` there, any case).

- **Motion vectors.** A new install reads the game's preset and the release's `DLSS5_Feed.fx`: a preset that already
  names a provider keeps it; otherwise `DLSS5_MV_PROVIDER=5` (the estimator built into the shader since 2.0.0, found by
  `float4 BaseLevel(` as Feeder's installer does), or 3 (LumeniteFX Kernel) with an older shader. LumeniteFX is
  downloaded, placed and recorded only for providers 3 and 4. The provider is written in the preset's
  `[DLSS5_Feed.fx] PreprocessorDefinitions`, never in ReShade.ini (Linux never wrote it there; upstream's clean-up of
  its old global entry, `RemoveGlobalProvider`, is ported for completeness but has nothing to remove on Linux).
  Existing installs keep their LumeniteFX stack; Feeder's own menu (Alt+C) can switch them.
- **Added to the preset, not replacing it.** Before this, Linux wrote its own `RHI-DLSS5-Feeder.ini` and pointed
  `PresetPath` at it, so the game's own preset stopped being used while Feeder was installed. Now Feeder's techniques
  are merged into the preset the game already uses (only Techniques, TechniqueSorting and the one definition change;
  with the built-in provider a LumeniteFX flow effect that was there only for the feed is switched off, as upstream);
  `PresetPath` is not touched. The write is journaled (the previous bytes come back on a rollback or a crash) and goes
  through ReShade.ini's link rule. A preset that did not exist is created and owned by the record (removed with Feeder
  while unchanged, kept when the user changed it).
- **Removal never deletes a preset that was there before Feeder.** Linux goes further than upstream, which leaves the
  merged techniques in the preset: the record keeps what the install changed, and removal takes exactly that out again
  (entries it added, a flow effect it switched off back on, the provider definition back to its previous value while it
  still holds the value the install wrote). A preset nobody changed since comes back byte for byte; the user's later
  edits stay. Installs from before this change still remove as before (their own preset file and `PresetPath` value).
- **Matching add-on/host pair (32-bit).** Feeder 2.0 add-ons and `host64\dlss5-feed-host64.exe` refuse another
  release's (IPC v12). Upstream fixed a shared staging copy that a pinned version could overwrite; Linux never shared
  one: install and the version swap take `dlss5-feed.addon32` and the host from the same release zip and swap them
  together. A test pins that one archive serves both.
- **Status and description.** The Feeder row's "Managed by RHI Linux" note names the motion vectors — "Built-in motion
  vectors" or "LumeniteFX motion vectors" — read from the preset, so a switch made in Feeder's menu shows (upstream's NR
  status tag). The row's description before install says Feeder 2.0 has its own motion vectors and an in-game menu
  (Alt+C). Linux has no Neural Rendering section text to change (the method rows are in Managed components).
- **Check Neural Rendering (#248).** Every Feeder log signature the page reads (`feature 18` with `0xBAD00012`, the
  depth probe's "depth is FLAT while the scene moves", "stopped: the DLSS evaluate crashed", `frame N delivered`,
  `building:`, `holding the feature (re)build`, `swapchain colour space:`, `effect runtime … initialised`) is still in
  the format strings of the 2.0.0 add-on (checked against `DLSS5-Feeder-2.0.0.zip`, commit `9bd0af3`).
- Linux deviations: a `PresetPath` given as a Windows drive path (`C:\…`) refuses the install with a line saying so
  (upstream writes wherever it points); upstream also sets `PresetPath=.\ReShadePreset.ini` when it is missing, Linux
  relies on ReShade's own default for the same file.

### #274 — GITC Uplift as a Neural Rendering method, to its 2.0 behaviour (2.9.2/2.9.3)

Upstream 2.9.2 `93d0fcb`, `d5b2df4`, `18088a3`; 2.9.3 `5f409de`, `7748d10`, `71af41e` (`UpliftService`,
`MainViewModel.Uplift`, `SettingsHandler.UpliftKey`); ported to the 2.9.4 end state. `GitcUpliftDeploymentService`
(new component kind `GitcUplift`), `ImmerseLaunchpadService` (shared with HSF, #277), `LinuxManagedComponentService.GitcUplift`.

- **Install** (the GITC Uplift row in Managed components; ReShade is installed first when missing, as for every ReShade
  add-on): `gitc-uplift.addon64` (`.addon32` on 32-bit games) and `gitc-uplift-helper64.exe` in ReShade's add-on folder
  (the binary folder, or reshade.ini's `AddonPath`), the NR DLL in its `GITC-Uplift` subfolder, `Uplift.fx` and
  `UpliftMask.fx` in RHI's shader tree `reshade-shaders/RHI/GITC-Uplift` (added to ReShade.ini's search paths; upstream
  copies them into `reshade-shaders\Shaders` through its shader sync). An `nvngx_dlssnr.dll` the user already put in
  `GITC-Uplift` is kept and not owned (upstream). A manual Uplift already in the game is refused, never overwritten.
  Everything is one journaled operation; the record is the commit point. Supports DX9–12, Vulkan and OpenGL, 32 and
  64-bit, as upstream (Vulkan through RHI's Vulkan ReShade layer).
- **Downloads**: the releases of `ghostinthecamera/GITC-Uplift` (full releases only, as upstream), the zip checked against
  GitHub's digest and the release's `SHA256SUMS.txt`, and each file taken out of the zip against its own line there;
  fail-closed (a release with neither is not offered; upstream continues without a check). Cached per version with a
  download stamp. Pinned in the tests: the release listing (2026-11-16), the 2.0.0 zip's entry names and `SHA256SUMS.txt`.
- **Uplift Version** (upstream's Neural Rendering "Uplift Version"): a list beside Feeder / DX11 Bridge / ShortFuse under
  NR versions. Latest follows new releases through Update All and the background pass; a picked version stays. Changing it
  while installed swaps the add-on, helper and effects together. A pin no longer published installs Latest and says so.
- **Add-on and helper always together** (2.9.3 fix): every update or version change replaces the add-on, the helper and
  the effects from one release in one journaled swap, all or nothing; a file changed outside RHI refuses the whole swap.
  Linux equivalent of upstream's "file in use" probe: never while the game runs (Update All already skips running games;
  a direct change says to close the game).
- **NR on/off key**: Settings › DLSS/Streamline, a "GITC Uplift" card right after the OptiScaler key (upstream:
  Settings › Components, after the ReLimiter and OptiScaler card): "NR on/off key" and "Write key to all Uplift games"
  (hidden games skipped). Install writes `[Uplift] EnableKey` (decimal virtual-key code) into the game's ReShade.ini when a
  key is set; None leaves each game's own key. Removal puts back the value it replaced while the key still holds RHI's.
  Linux deviation: the key is picked from a list (F1–F12, Insert, Delete, Home, End, Page Up/Down, Pause, Scroll Lock,
  Numpad 0–9) as the OptiScaler key here, instead of being pressed into a box. Upstream's removed "Start with NR on" is not
  ported (2.9.4 end state).
- **iMMERSE Launchpad** (motion vectors): placed from the `martymcmodding/iMMERSE` archive (only `MartysMods_LAUNCHPAD.fx`,
  its `MartysMods/*.fxh` includes and `iMMERSE_bluenoise_opt.png`) into `reshade-shaders/RHI/iMMERSE-Launchpad`, switched
  on in the preset right above the Uplift technique and moved to the top. On by default in games without DLSS; an
  "iMMERSE Launchpad" check box under NR versions changes it per game (applied at once when Uplift is installed, saved for
  the install otherwise). Off takes out only a technique RHI switched on. The Launchpad files are shared: a component that
  finds them listed by another record lists them too, and removal deletes them only when no other record still does
  (`ImmerseLaunchpadService`, for HSF).
- **Kept first**: at start and with each background pass, Launchpad goes back to the top of each Uplift game's preset when
  ReShade or HSF put it lower, only while the game is not running (Linux running-game probe), journaled on Uplift's record,
  nothing else changed (upstream: launch, the 4-hour refresh and the toggle).
- **OpenGL MSAA set-up**: on OpenGL games the `Uplift@Uplift.fx` technique is switched on first in the preset at install,
  so NR runs on ReShade's resolved single-sampled copy (upstream `EnsureTechniqueFor`). A technique the user already had on
  stays on after removal. Upstream also re-detects OpenGL games once that its older builds saw as DX12 because of the
  helper; Linux never counted the helper.
- **Default method**: the Neural Rendering recommendation names GITC Uplift only where DLSS5 Feeder was the default and
  all of Uplift works — 64-bit DX11 / DX12 / Vulkan games without DLSS; 32-bit, OpenGL and DX9 keep Feeder; games with DLSS
  keep the DX11 Bridge / ShortFuse. Linux has no automatic method choice: games keep whatever is installed. Installing
  Uplift removes the other RHI Neural Rendering method first (and the others remove Uplift), as upstream's method switch.
  OptiScaler DLSS NR / F5 keep refusing a second NR owner.
- **Link**: a "GITC Uplift" button under NR versions opens the project page with its guide (upstream: the links at the
  bottom of the Neural Rendering section).
- **Elsewhere**: the NR DLL list swaps Uplift's own DLL in place; Check Neural Rendering lists Uplift among the methods in
  use; Update All and the update history name it "GITC Uplift". The NR Cost Scaler stays off for Uplift (upstream hides it:
  the DLL is not beside the executable).
- Linux deviations, in short: RHI shader trees instead of the shared shader folder; fail-closed download checks; key list
  instead of key capture; the card under DLSS/Streamline until the Settings restyle (C1, #296) brings the Components
  section; no automatic method choice.

### #276 — Foveated DLSS5 Tool switch (2.9.3): not ported yet (owner decision)

Upstream 2.9.3 `77cd3f7` offers the switch only for its "DLSS5 Tool" and "DLSS5 Tool + DX11 Bridge" methods.
FoveatedDLSS5 (maximedgoulet) is a build of the DLSS5 Tool's `renodx-dlss5.addon64`, published as pre-releases with a
`.sha256` beside the add-on. RHI Linux has no DLSS5 Tool method: its DX11 Bridge row deploys only
`dlss5-dx11-bridge.addon64`, and `renodx-dlss5.addon64` is deployed only as the DLSS5 Feeder's consumer, where upstream
offers no Foveated switch. The switch waits for the owner's choice on the tracker: port the DLSS5 Tool methods first, or
use another mapping.

## Parity notes (upstream 2.8.9–2.9.4, lane extras-fg)

### #285 — Closed-source notice for RTX Encore, 20/30 FG Unlock and ANR FG (2.9.3)

- Before the first install of RTX Encore or 20/30 FG Unlock (the row's Install; never on update), RHI shows upstream's
  notice: "<mod> is closed source: <author> publishes only the finished DLL, not its code …", Install / Cancel and
  **Don't show this again**. As upstream, a tick is saved whatever the answer and is kept per mod (upstream's
  `RtxEncoreClosedSourceDismissed`, `Dlssg2030ClosedSourceDismissed`, `AnrFgClosedSourceDismissed`).
- The notice is a reusable confirm (`InstallNoticePresenter.ConfirmAsync`): the Universal MFG ReShade warning (#278)
  uses it, and the OptiScaler lane calls `ConfirmClosedSourceAsync(ClosedSourceMod.AnrFg)` before an ANR FG preset
  install; ANR FG has no row, so its notice carries its GitHub page as a link, as upstream.
- The ⓘ button of the RTX Encore, 20/30 FG Unlock, Universal MFG Unlocker and MFG Unlock rows opens the mod's GitHub
  page (upstream's Info buttons) instead of a notes dialog.
- Linux deviations: the dismissals live in `~/.config/rhi-linux/dismissed-notices.json`, not in the main settings file,
  so Settings → Reset does not bring the notices back. A dismissal that cannot be saved (an unreadable file) is reported
  on the status line and the notice shows again next time. Without a window (tests, headless runs) every notice is
  accepted.

### Shared: Intel XeSS files for the frame-generation mods (2.8.9–2.9.3)

- One service (`IntelXessFiles`) downloads and places Intel's XeSS SDK files for Universal MFG Unlocker (#278), MFG
  Unlock (#279) and RTX Encore (#280), from upstream's source (`intel/xess` at tag `v3.0.2` on
  raw.githubusercontent.com, policy `IntelXess` in [`docs/download-trust.md`](docs/download-trust.md)).
- Linux deviation: every file is checked against a pinned SHA-256 (upstream pins them for RTX Encore only and accepts any
  `MZ` file of at least 1 KiB for the other two mods), and a DLL must be a 64-bit PE. A file that does not match is
  refused.
- The moves run inside the component's own journaled operation, so a failure or a crash undoes them with the rest.
  A step either adds a file the folder lacks (a file RHI did not place is left alone) or replaces the game's own copy in
  place after copying it to a backup name. Unlike upstream, an existing backup is never overwritten: that file is
  skipped and named. Removal deletes an added file, or puts the game's copy back from its backup, only while the file
  still holds RHI's bytes; a file changed since is left, with its backup, and named.
- Linux deviation: installs from before get the files through Update / Update All, not at start-up (the check plans it
  even without a new release), as for the 20/30 FG Unlock package repair (#261, D1 = No).

### #278 — Universal MFG Unlocker: rename, RTX 30 and XeMFG, Intel files, OptiScaler-plugin mode, ReShade warning (2.8.9)

- The row is **Universal MFG Unlocker** (internal key and records unchanged) and its ⓘ opens
  `github.com/dashdogy/MFG-Unlocker`.
- The GPU-generation dropdown is gone, as upstream. Linux kept a stricter API rule for a saved "RTX 30" choice (DX12
  only); it now applies one rule to every card: DirectX 12, Vulkan experimental. A saved generation is kept in the
  record but no longer read. The proxy-name dropdown stays in the ⚙ (upstream asks for the name in a picker at install).
- Intel files: `libxess_fg.dll` and `libxell.dll` go in `RTXMFG-XeSS` beside the proxy (the binary folder; upstream uses
  its install folder, which is the same place for the games it supports). A download that fails does not stop the
  install (upstream); the message says so and Update adds them.
- OptiScaler-plugin mode: with RHI's OptiScaler Nightly, F5 DLSSNR or DLSS NR (an `OptiScaler` folder beside the game)
  the mod is placed as `OptiScaler/plugins/MFGUnlocker.asi`, with no Wine override and no Intel files. Installing such an
  OptiScaler moves an existing proxy install in (proxy, Wine override and Intel files removed); removing RHI's OptiScaler
  first moves it back beside the game under the name it came from, else version.dll, winmm.dll, dinput8.dll or the next
  free proxy name, never over an existing file, and puts the Intel files back. OptiScaler keeps `LoadAsiPlugins=true`,
  which it already sets on every install. Stable OptiScaler is not a host. Linux deviations: upstream also reconciles
  every game at start-up; RHI Linux moves only through these OptiScaler installs and removals, so an install made before
  this build stays a proxy until OptiScaler is installed again or the mod is reinstalled. A move that cannot be made (a
  file changed outside RHI, Wine running) is named in OptiScaler's message and never fails OptiScaler's own result.
- ReShade warning: before a user-started install beside a ReShade (RHI's or the user's; not for a Vulkan game, whose
  ReShade is the layer, and not for a plugin install), upstream's "XeMFG and ReShade" notice with Continue / Cancel and
  **Don't show this again** (key `UmfgReShadeWarningDismissed`, through the #285 confirm).
- ⚙: upstream's text (Backspace menu and its modes, preset B above 2x), the "Loaded by OptiScaler" section for a plugin
  install, the XeMFG section with "XeSS files: installed / missing", and the ReShade-as-dxgi.dll warning at 5x/6x
  (Linux wording points at installing ReShade under another DLL name instead of upstream's DLL naming overrides).
  Upstream's "Open GitHub page" button is the row's ⓘ.

### #279 — MFG Unlock (formerly MFG Ada Unlock): rename, frame-generation method, XeSS files with backup, DX11 warning (2.8.9/2.9.2)

- The row, its ⚙ and RHI's own messages say **MFG Unlock**; the component key, records and the add-on file
  (`renodx-mfgunlock.addon64`) are unchanged, and the upstream manifest's `packageName` ("MFG Ada Unlock") is no longer
  shown (`KeepDisplayName`). The ⓘ opens the mod's GitHub page (#285).
- New ⚙ (upstream's `BuildMfgAdaCogContent`): **Frame generation method** writes `[Backend] Selected` (`DLSSG`/`XeMFG`)
  and `[DLSSG] XmxGenerator` (`0`/`1`) to `MFGAdaUnlock.ini` in the game's executable folder (the mod reads it from
  there, not from the add-on folder); **DLSS-SR input (experimental)** writes `[Adapters] DlssSr` and is greyed out
  unless the method is Intel XeMFG (CUDA). A missing file reads as the mod's defaults and a change creates it; other
  lines and comments are kept. The help texts sit under each row, as upstream. **Open MFGAdaUnlock.ini** is greyed out
  while the file does not exist.
- Intel XeSS files (shared service above): `libxess_fg.dll` and `libxell.dll` go in the game's executable folder; a game
  copy of another build is kept as `<file>.mfgada-original` and put back on removal; an identical copy is left alone.
  The step runs as its own journaled operation after the add-on's install or update, with MFG Unlock's record as its
  commit point (the add-on's update keeps the list when it rebuilds the record). Removal puts the game's copies back
  first and refuses, removing nothing, when that cannot be done. Linux deviation: a download that fails never fails the
  add-on's install; the message says so and Update adds the files.
- ⚙ status: "ready", "missing (…)" or "a version MFG Unlock doesn't accept (…)", judged by RHI's pinned file or upstream's
  accepted versions (libxess_fg 1.3.1.78, libxell 1.3.2.10 / 1.3.0.5) read from the PE version resource.
- API warning, as upstream (only in the ⚙): any DirectX 12 among the game's APIs is fine; Vulkan gets the "experimental"
  text; DirectX 8–11 or OpenGL get "won't work here". Linux reads the effective API list (manifest or detection).

### #281 — DLSSG 20/30 FG Unlock ⚙ settings (2.8.9)

- The ⚙ shows upstream's six settings as dropdowns of valid values only, in upstream's order and with its help text
  under each: Frame Generation (`[General] Enabled` 1/0), Engine (`[FrameGeneration] Engine` DLSS/XeSS), Max Frames
  (`MaxGeneratedFrames` 1–5, "Game decides" 0), Preset (`[DLSSG] Preset` A/B/C), Speed Tier (`[DLSSG] Optimized` 0–3)
  and Super Resolution (`[SuperResolution] Mode` Off/M-INT8/L-INT8/M-FP16/L-FP16). Upstream's Engine/Max Frames rule is
  kept: XeSS at 5X/6X moves Max Frames to 4X, and 5X/6X are refused under XeSS. Until the mod is installed the rows are
  greyed out with upstream's warning. **Open dlssg_sm86.ini** and **Open log** (`dlssg_sm86/logs/dlssg_sm86.log`) are
  greyed out while the file does not exist. The values are pinned against the 0.4.2 package's INI fixture.
- Kept on update, as upstream: a change made in the ⚙ keeps RHI's ownership of the INI (the record takes the new hash),
  so Update puts the new release's INI in place and writes the six values back into it, and Remove deletes it. Linux
  deviation (unchanged from #261): an INI changed outside RHI is the user's and is kept whole on update and removal.
- Linux deviation: a missing Speed Tier reads as 1, the mod's documented default (upstream's fallback reads 0 while its
  label marks 1 as the default).
- The GPU-generation dropdown is gone, as upstream; the intro now describes the mod (RTX 20 and 30, DirectX 12,
  driver R580+) instead of "Legacy SM75 path" (EN and PT-BR), and the dialog title is "20/30 FG Unlock". A saved
  generation is not asked for any more; the preference store already reads an old "RTX 30" choice as RTX 20.

### #280 — RTX Encore: ⚙ settings, XeSS files in place, NR DLL 310.9.16, Bink names, .asi build, DLL-name recommendation (2.8.9–2.9.3)

- The row is **RTX Encore** (no "Beta").
- ⚙ settings: upstream's sections and settings, with its keys and values (`renderingEngines.frameGenerationEngine`,
  `frameGeneration.mode`/`multiplier`/`dynamicTargetFrameRate`/…, `xefg.*`, `imageQuality.*`, `hudUi.*`, upscaling,
  `neuralRendering/core.nrEnabled` and the NR presets, `general.gpuSeries`, `compatibility.*`,
  `nativeMenuFeatures.hairEnabled`), written at once into `rtx-encore.jsonc` beside RTX Encore by a port of upstream's
  line editor (`RtxEncoreConfig`): only the value token changes, comments and newline style are kept, a missing key or
  section is inserted, and a missing file is created with just that setting. Upstream's dependencies grey rows out
  (Multiplier in Fixed mode, the dynamic rows in Dynamic mode, the XeFG engine rows with XeFG, XeSS on Tensor Cores with
  the XeSS upscaler, custom scale with Custom). Number boxes check their range and go back to the saved value when it is
  out of range. Greyed out until RHI's RTX Encore is installed. Pinned against upstream's own editor tests.
  Linux deviations: Smooth Motion is left out (it needs NVIDIA's Windows driver and does not run under Wine/Proton);
  the rows have labels but not upstream's long per-row tooltips yet; the "game is running" warning is not shown.
- Intel XeSS files (shared service above): the game's own `libxess.dll`, `libxess_fg.dll` and `libxell.dll` (any
  folder under the game, Intel's version resource, links, `OptiScaler.ini` folders, `rtx-encore-runtimes`,
  `RTXMFG-XeSS` and files another RHI component placed skipped; at most 16 levels and 20 000 folders) are replaced in
  place, each kept as `<file>.encore-original`; the others and Intel's licence (`license.txt`) go in
  `rtx-encore-runtimes/intel` beside RTX Encore. All in RTX Encore's own journaled install, removal or update. The ⚙
  says whether they are installed and how many replace the game's copies.
- Neural Rendering file 310.9.16 (`nvngx_dlssnr.dll`, pinned SHA-256 `CDFA6C1E…FEABF3ED`, from the DLSS list's
  "310.9.16 (20/30/40/50)" entry). An RHI copy of 310.8.0 is replaced by Update, while it still holds RHI's bytes.
- Bink names: `binkw64.dll` / `bink2w64.dll` are offered; the game's own Bink DLL is kept as `<stem>Hooked.dll` (where
  RTX Encore loads it) and put back on removal. Linux deviation: a `…Hooked.dll` RHI did not record is never adopted,
  so the install is refused with the file named instead of being treated as already set up.
- `.asi`: the separate ASI build (`alternative-proxies/rtx-encore.asi`; the DLL when a release has none, as upstream)
  replaces the renamed DLL; an .asi install from an older build is corrected by Update. Linux deviation: the `.asi`
  stays beside the game's executable next to its Neural Rendering file, where RHI Linux has always put it (Ultimate ASI
  Loader reads that folder), so upstream's move to `plugins/` is not needed.
- Recommended DLL name: the manifest's `rtxEncoreDllNames` (Euro Truck Simulator 2: `winmm.dll`) is read into the game
  and, when it is one of RTX Encore's DLL names, listed first and used as the default; the user's own choice wins; the
  ⚙ names it.
- Extras summary (upstream 2.9.4 fix): on Linux the RTX Encore row is in Managed components, whose collapsed summary
  already lists it.
- Linux deviation: installs from before get the XeSS files, 310.9.16 and the ASI build through Update / Update All (the
  check plans it), not at start-up. A failed XeSS download does not stop an install (upstream); the message says so.

## Parity notes (upstream 2.8.9–2.9.4, lane ui-logic)

### #299 — Game search like Steam's (2.9.3)

- Ported from upstream's `FilterViewModel` (2.9.4) into `GameSearch` (Core): punctuation and spaces ignored, any word
  order, initials and partial initials (three or more characters anywhere in them), capital Roman numerals I–XXXIX as
  numbers (a lone "I" only at the end of a name), typos by word length (5+ letters: 2 off, 4 letters: 1 off, shorter:
  none; a word being typed is compared with the start of longer words), near misses hidden when a name or whole words
  match, and best match first (whole name, starts with, whole words, part of the name, initials, near miss, then games
  found by a tag), A to Z within each rank. Upstream's test table is pinned in `GameSearchTests`.
- Tags match whole words only (`GameSearch.TagMatches`), as upstream: store, engine, graphics API, bitness and author.
- Linux deviations: a card is found by its Edit game display name, its detected name and its manifest name (#190); the
  rank is the best of the three. Linux's extra search fields (runner, compatibility tool and layer, wiki status, the
  manifest's game note, the install, executable and prefix folders, HDR, Manual) are tags too, so they also match whole
  words only (a folder by any of its path's words). Upstream also finds a game by its RenoDX/Luma mod's name and author
  and by DLSS, UW Fix and Ultra+ badges; Linux searched none of these before and does not now (the author tag is the
  manifest's author override).
- Upstream's rules give the same near misses: "steam" also shows Stellar Blade ("stell" is two letters off), ranked
  above the Steam games found by their store.

### #300 — Game collections (2.9.2)

- Ported from upstream's `GameCollectionStore`, `MainViewModel.Collections` and `MainWindow.Collections`: create (with
  the game when made from its menu), rename (the active collection stays active), move left/right, delete (the games
  stay; the list goes back to all games), membership ticks in the game menu's **Add to collection** (under Hide, as
  upstream) with **New collection…**, and upstream's name rules (trimmed, 1–40 characters, no control characters, not
  a built-in filter's name, unique in any case). A chip shows only the collection's games, hidden ones excepted, and
  puts the filter tab back to All underneath; the search and a custom filter still narrow it; any filter tab leaves the
  collection; the active one is remembered across launches; an empty collection says why (no games yet, none match
  the search, or hidden / no longer detected).
- Storage: in the library file (`~/.local/share/rhi-linux/game-library.json`, `GameCollections` and
  `ActiveCollection`) through its store: atomic, verified writes, an unreadable file copied aside and never overwritten;
  each change saves a copy and adopts it once on disk, and nothing is written during Repair after move. Not written
  while there are none, so the file keeps its older shape.
- Linux deviations: members are kept by the game's library key (the key favourites and hidden use), not upstream's
  "GameName|Store", so an Edit game display name change needs no migration and the collections follow the game when an
  older key is adopted or Repair after move gives it a new one. The built-in names refused also cover the Linux filter
  tabs (Unreal, Unity, Other, RenoDX, Luma) and their PT-BR words. Classic layout only for now: Spotlight's placement
  beside the search box and the controller's right-stick switching come with the phase-2 layouts (#305, #308). The chip
  row is the Avalonia `CollectionsRow` under the filter tabs, above #193's saved custom filters.

### #301 — Colour themes, custom colours, share/import codes (2.9.3)

- Ported from upstream's `ThemePalette`, `ThemePresets`, `ThemeColorMapper` (Core `ThemePalette.cs`; upstream's
  `ThemePaletteTests` and `ThemeShareCodeTests` run against it unchanged but for the colour type) and its Settings card:
  the nine themes in upstream's order with its colours, applied live; Custom's nine colours (Look and Status colours),
  first use starting from the theme that was showing, Start from a theme…, Reset to default, the contrast warning
  (text under 4.5:1 on the background or the panels), Share theme / Import theme with upstream's `RHI-THEME:` code
  (nine colours in a fixed order; a pasted message around it, case, `#` and spaces are fine), "Violet" opening as Kiro.
  Settings keep `ColourTheme` and `CustomThemeColours` (upstream's format), saved when they change.
- Linux mechanism: every colour the Avalonia views use is now a named brush resource (`Theming/ThemeResources`, one
  list with the default look's colours; AXAML uses `{DynamicResource Rhi…Brush}`, code-built views use style classes
  in App.axaml or `ThemeResources.Bind`). A theme puts each brush, recoloured by upstream's mapper, into the app's
  resources, so open windows repaint at once. A test checks that no AXAML holds a literal colour and that every
  `Rhi…Brush` used exists, so the phase-2 layouts take their colours from the same keys.
- Linux deviations: the card is in Settings › UX (the Linux tab for upstream's General until the phase-2
  Settings layout, #296). Custom colours are typed as `#RRGGBB` beside a live swatch: Avalonia's colour picker is a
  separate package this build does not ship (owner decision whether to add it). Import is a row under the buttons
  rather than a dialog. Fluent's own control templates (scroll bars, drop-down surfaces) keep Avalonia's dark colours,
  as upstream leaves WinUI's own resources alone. Upstream 2.9.5's theme polish is not ported: 2.9.5 is still in
  beta (latest release RHI-2.9.4 on 2026-11-16).

### #310 — Per-game FPS limit (2.8.9; owner decision 2026-11-16 for the Linux equivalent)

- Upstream sets the NVIDIA driver profile's FPS Limiter V3 (`0x10835002`) per game, with a global value on the base
  profile. There is no driver profile under Wine/Proton, so RHI sets `DXVK_FRAME_RATE` (DXVK, Direct3D 8–11) and
  `VKD3D_FRAME_RATE` (VKD3D-Proton, Direct3D 12) in the game's launch variables, both at once (each layer reads its own),
  the way #211 hands the NVIDIA settings over: in the environment of a direct launch, or in the launch options shown
  for Steam / Heroic / Lutris. Nothing is written to disk, the prefix or the game. Unlike the NVIDIA settings, it does
  not need an NVIDIA GPU or a DLSS file; it is not offered for native Linux games or for store clients and the Xbox app,
  which take no launch variables.
- Choices as upstream: Global (named with the global value), Off, upstream's VRR presets, a game's own custom value just
  above Custom…, and Custom… (20–1000; any other value is refused and nothing changes). Settings › DLSS/Streamline has
  the global FPS Limit (Off, the presets, Custom…), written on Save.
- Upstream's rule that a cap turns off the ReLimiter / Display Commander per-game limit: upstream writes the driver limit
  Off into those games (on their install and whenever the global cap is set) and clears it on uninstall. Linux applies
  the same outcome when the plan is built: a game on Global with ReLimiter or Display Commander installed by RHI gets no
  cap; a value chosen for the game itself still applies, as upstream lets the user set one afterwards. Deviation: an
  explicit per-game value is never overwritten by an install or a global change (upstream's writes would reset it to Off).
- Storage: the per-game choice is in the library file (`FpsLimits`, by library key, following key changes and Repair
  after move; Global = no entry), the global one in settings.json (`GlobalFpsLimit`, 0 = Off).
- Placement: the FPS Limit row is in the DLSS section beside NVIDIA settings… for now; the phase-2 layouts move it into
  Driver Settings with upstream's other driver rows. Launch and hand-off lines say "FPS limit" when it is the only thing
  applied, and use the NVIDIA settings' wording (the pasted line then carries both) when NVIDIA settings are chosen too.

### #313 — Not applicable: Windows-only changes in upstream 2.8.9–2.9.4

These upstream changes have no Linux counterpart and are not ported. Each reason is why the feature cannot work, or has
nothing to fix, under Linux + Wine/Proton.

- **Remove NVIDIA App override limits** (2.8.9): patches the Windows NVIDIA App's files and restarts its Windows
  services. There is no NVIDIA App on Linux; DLSS overrides go through #211's launch variables.
- **Compile shaders now** (2.9.3): runs NVIDIA's Windows shader compiler (the NVIDIA App's Compile now). Linux shader
  caches belong to the driver, DXVK/VKD3D-Proton and Steam's shader pre-caching; RHI does not manage them.
- **ReBAR helper and Low Latency admin fixes** (2.8.9–2.9.3): NVIDIA driver profile settings written with admin rights
  through NVAPI. Linux has no driver profile; resizable BAR is a firmware/kernel matter and low latency belongs to the
  game, dxvk-nvapi (Reflex) and the compositor.
- **Xbox / Game Pass achievements and Game Pass folders** (2.9.2/2.9.3): read from the Windows Xbox app and its
  `XboxGames` / `WindowsApps` folders. Linux Game Pass titles come through the installer apps (#90), which keep neither.
  Steam achievements from Steam's local files are #307 (phase 2).
- **Windows App Runtime, installer and `%TEMP%` clean-up** (2.8.9/2.9.0): the Windows installer, the Windows App SDK
  runtime it installs and the .NET single-file extraction folder. RHI Linux ships as .deb, .rpm and .tar.gz builds with
  no runtime installer and nothing extracted to a temp folder.
- **Antivirus deletion handling** (2.9.0): explains Windows Defender removing freshly downloaded DLLs. Linux has no
  on-access antivirus in the default install; a missing download is already reported by the trusted-download checks.
- **DXVK / Universal MFG Unlocker downloaded only when used** (2.9.0): upstream downloads DXVK itself for its DX9 path.
  Here DXVK comes from the selected Wine/Proton runner (never downloaded by RHI), and Universal MFG Unlocker is
  downloaded only by its install, so there was nothing downloaded up front to stop.
- **Epic client launch fixes** (2.9.0/2.9.2): start the Windows Epic Games Launcher before a direct exe launch and pass
  its `namespace:item:app` id. Linux Epic games launch through Heroic, Legendary or Lutris (and #262's prefix rules);
  revisit `18088a3` only if RHI Linux ever launches Epic URIs itself.
- **Sign in with GitHub removal** (2.9.0): the browser sign-in was never ported; RHI Linux has always used a GitHub
  token file (`github_api.txt`, #100), the method upstream now recommends.
- **Output colour / admin messages** (2.9.0): Windows' display colour-depth and digital vibrance settings through
  NVAPI with admin rights. Linux display colour belongs to the compositor; HDR output is #251's launch section.
- **Admin Mode cursor** (2.9.0): the busy cursor while Windows elevates RHI. RHI Linux never runs elevated.
- **Windows animation hover flash** (2.8.9/2.9.3): a WinUI hover-animation glitch with Windows' "Animation effects" off.
  Avalonia has no such animation path.
- **Light-mode headings** (2.9.2): unreadable headings with Windows' light app mode. RHI Linux is always dark
  (`RequestedThemeVariant="Dark"`); since #301 the colour themes are all dark too, as upstream's.
- **Freeze diagnostics / auto-restart while a game runs** (2.9.2/2.9.3): upstream's UI-thread watchdog takes dumps and
  restarts RHI when Windows pages it out for a memory-hungry game. RHI Linux has no watchdog, dump writer or
  self-restart, so it has nothing to hold back while a game runs.
- **RHI-Stats** (upstream's Windows usage statistics tool): not part of RHI Linux; RHI Linux sends no statistics.
- **Upstream beta channel** (`RankFTW/rhi-beta`): Linux releases follow this fork's own release policy and its own
  update check (#100), not upstream's beta feed.
- **UE3 CPU/RAM after launch** (upstream 2.9.5 Beta 2, `539cf2f`, verified here): upstream watched a launched game by
  searching for processes under the game's folders, and for Remember Me (UE3, no project folder) its Unreal rule made
  that folder `steamapps\common`, so every Steam app counted as the game; its exit wait (`WaitForExitAsync` on
  processes RHI could not wait on) then failed at once and spun, writing hundreds of crash reports a second. Neither can
  happen here: RHI Linux waits only for the process it started itself (`LinuxGameLauncher.RunAndWatchAsync`, one
  `WaitForExitAsync` on its own child), the display-mode recovery checks one recorded PID and its start time every
  5 s (`DisplayModeSwitcher.RecoverAsync`), and Update All's running check matches the game's exact executable path in
  `/proc` (`LinuxUpdateAllService.IsRunning`), never a folder; none of them searches a store library folder or loops
  without a delay.
