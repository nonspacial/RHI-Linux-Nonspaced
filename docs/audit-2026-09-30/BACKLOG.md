# Backlog — 2026-09-30 audit

Tracker: local Forgejo `infra/RHI-Linux` (<http://localhost:3000/infra/RHI-Linux/issues>). Git remote `forge`.
Overview and order: #101 **Backlog overview** (pinned).

Order: #1 (PAR-B04) first; then the upstream-sync and data-safety lanes in parallel; then detection; then UI. Feature-parity items stay unscheduled until decided.

Branches: `main` = what Robbie runs. `upstream-sync/2.7.9` for the upstream port. `fix/<issue>` worktrees under `/mnt/vdo_storage/worktrees/rhi-linux/` for everything else. Push to `forge` only; a GitHub PR to `wizardkof/RHI-Linux` only after a change has lived on `main`.

| Milestone | Issues |
|---|---|
| `audit-remediation` | 102 |
| `upstream-sync/2.7.9` | 14 |
| `upstream-sync/2.8.5` | 13 |
| `feature-parity` | 13 |
| (none) | 1 (#101 overview) |
| **Total** | **143** |

## upstream-sync/2.7.9

| # | Finding | Title | Labels | Priority |
|---|---|---|---|---|
| #10 | PAR-D01, PAR-42 | [2.7.6] NR in-place version swap and NR DLL version selection for all methods | upstream-drift | P2 |
| #11 | PAR-D02, PAR-56, PAR-53 | [2.7.6] Available HDR Mods dialog, collapsed-section summaries and DLL-naming dropdowns | upstream-drift | P2 |
| #12 | PAR-D04, PAR-06 | [2.7.6] PCGW single-file data model (pcgw_data.json) and Retry-After backoff | upstream-drift | P1 |
| #13 | PAR-D05, PAR-47 | [2.7.6] OptiScaler FG Nvngx "Combo" option and RR Preset F | upstream-drift | P2 |
| #14 | PAR-D06 | [2.7.6] Unity addon URLs on GitHub Releases; stop re-downloading on every install | upstream-drift | P2 |
| #15 | PAR-D08, PAR-19 | [2.7.9] Unity per-game INI settings from the RHI database | upstream-drift | P2 |
| #16 | PAR-D08, PAR-63 | [2.7.9] Control Ultimate Edition RR automation | upstream-drift | P2 |
| #17 | PAR-D08, PAR-44 | [2.7.9] DLSS Tool "ZZZ load order" toggle | upstream-drift | P2 |
| #18 | PAR-D08, PAR-55 | [2.7.9] Component update history log | upstream-drift | P2 |
| #19 | PAR-D09 | [2.7.9] NR fixes: rc10 vs rc5 sort, Full Refresh bypasses version cache, redeploy untracked NR files | upstream-drift | P2 |
| #20 | PAR-D10 | [2.7.9] Global shader "Off" overrides per-game overrides; custom folder auto-tick | upstream-drift | P2 |
| #21 | PAR-D11 | [2.7.9] Custom ReShade channel shows a false update | upstream-drift | P2 |
| #22 | PAR-D12 | [2.7.9] Launch working directory for exe-override launches (test under Wine) | upstream-drift | P1 |
| #112 | sync-18 review 3 | Record NR in-place swaps (SwapNeuralAddonVersionAsync / SwapNeuralRenderingDllAsync) in the update history after sync/10-19 merges | upstream-drift | P2 |

Merged into `upstream-sync/2.7.9` (open until Robbie's GUI/Wine check): #22 `a30f632`, #20/#21 `8e06564`, #13/#14 `8aeca3f`, #12 `d49f1af`, #18 `b8fcee2`, #10/#19 `b87c35b`, #17 `dfe35f4`; integration fixes and #112 (NR swaps in the update history) `a108618`. Merged into `integration/wave2` (from `main` @ `dc528e8`; open until Robbie's GUI check): #11 `c68a918`, #15 `6f2c6d4`, #16 `3fd51a5` (fix/15 × fix/16 conflict resolved there, both post-install steps kept). Parity notes are consolidated in UPSTREAM.md under `## Parity notes (upstream 2.7.6–2.7.9)`.

## upstream-sync/2.8.5

| # | Finding | Title | Labels | Priority |
|---|---|---|---|---|
| #116 | — | [2.8.5] Upstream drift inventory RHI-2.7.9 → RHI-2.8.5 | upstream-drift | P2 |
| #129 | PAR-D (2.8.0) | [2.8.0] RenoDX updates: detect rebuilt snapshots; Update All installs only mods with a real update | upstream-drift | P1 |
| #130 | PAR-D (2.8.0) | [2.8.0] Update history: log RenoDX updates with the add-on's real version | upstream-drift | P2 |
| #131 | PAR-D (2.8.0) | [2.8.0] Show manifest install warnings before install, with clickable links | upstream-drift | P2 |
| #132 | PAR-D (2.8.3) | [2.8.3] ReShade download and update check fail when reshade.me answers HTTP 5xx | upstream-drift | P1 |
| #133 | PAR-D (2.8.3) | [2.8.3] Revoked GitHub token: stop sending it and fall back to anonymous access | upstream-drift | P1 |
| #135 | PAR-D22 | [2.8.1] OptiScaler FG: nvngx_dlssg.dll in the Streamline stack; Force Reflex and Use Games Reflex Markers settings | upstream-drift | P2 |
| #136 | PAR-D (2.8.1) | [2.8.1] Control UE: optional OptiScaler FG install and "Using HDR?" FgSlHdr10 toggle | upstream-drift | P2 |
| #138 | PAR-D (2.8.2) | [2.8.2] Donate dialog: mod authors, roles and Ko-fi links from the manifest | upstream-drift | P2 |
| #139 | PAR-D (2.8.1) | [2.8.1] Game detail: installed RenoDX/Luma add-on file names and mod authors | upstream-drift | P3 |
| #140 | PAR-D (2.8.5) | [2.8.5] Available HDR Mods: readable column headers and ✗ marks | upstream-drift | P3 |
| #141 | up-139 lane | [2.8.x] RenoDX: wiki-unlinked titles with a snapshot override must stay installable | upstream-drift | P2 |

Closed: #137 (dgVoodoo2 standalone, owner decision). #134 (GitHub sign-in) is parked under `feature-parity`.

Merged into `integration/wave3` (from `integration/wave2` @ `9983d1b`; open until Robbie's GUI check and the install): #142 `48979cd` (test HOME/XDG isolation; every download cache reused only through a `CacheStamp`; the RenoDX cache keeps wave3's HTTP-validator sidecar plus the stamp; the ReShade outage fallback only offers stamped copies, one `StableSetupUrl` for stamping and lookup), derived-entry stamps reject a blank binding (`a58196c`, fix-142 re-review nit 2), #133 `bc6150d`, #138 `e5554ac`, #139 `0289955`, #131 `5727d13`, #129/#130/#107 `5cc6454`, #132/#140 `44ffaf6`, #135 `f62ee1d`. Integration: a ReShade source outage in Manual Update All counts as "could not be checked" (`99a04eb`). Re-review minors/nits fixed there: #129 Update All summary names the first failure and its reason (`344b10e`); #138 built-in Ko-fi links pinned, no temp file left on a cancelled cache write (`a208bf5`); #139 snapshot-override engine-author test, Decorate's records reused (`26181da`); #133 notice appended to the current line, busy wiring extracted and tested, no dropped notice (`d7f3195`); #132 setup-download outage flagged, Shaders/Add-ons keeps the cached-copy note (`cffbe7c`); #131 one dependency plan for the warning list and the install, computed off the UI thread (`da75b2e`); #135 Streamline row ignores the OptiScaler stack, panel targets from `LinuxDlssDefaultsService` (`abe070e`), stranded legacy DLSS-swap backups given back, coexistence retry test (`76dd3cd`). Parity notes are consolidated in UPSTREAM.md under `## Parity notes (upstream 2.8.0–2.8.5)`.

## audit-remediation

| # | Finding | Title | Labels | Priority |
|---|---|---|---|---|
| #1 | PAR-B04 | PAR-B04: validate.sh hard-codes test count and ~20 source literals; every PR breaks CI | hygiene | P0 |
| #2 | PAR-B01 | PAR-B01: AppImage build is not in the repo; released artefact cannot be reproduced | hygiene | P1 |
| #3 | PAR-B02 | PAR-B02: AppImage does not bundle 7z/bsdtar; ReShade extraction needs a host tool | hygiene | P2 |
| #4 | PAR-B05 | PAR-B05: pin the .NET SDK with global.json and add a package lock file | hygiene | P2 |
| #5 | PAR-B06 | PAR-B06: CI hygiene: SHA-pin actions, add permissions block, cache, test results, release job | hygiene | P2 |
| #6 | PAR-B07 | PAR-B07: install-user.sh wipes the install dir silently and does not refresh desktop caches | hygiene | P3 |
| #7 | PAR-B08 | PAR-B08: validate.sh deletes all bin/obj and artifacts/ in a developer tree | hygiene | P3 |
| #8 | PAR-61 | MOTD URL returns 404: motd.md missing from the repo | hygiene | P2 |
| #9 | analyzers-summary | Analyzer warnings baseline (834): CA1869 JsonSerializerOptions per call ×18, CA2016 token not forwarded ×12, CA1310 culture-sensitive EndsWith in DetectionHelpers ×4 | hygiene | P3 |
| #23 | DEP-03 | DEP-03: write-ahead journal for deployments; crash mid-operation leaves unowned payloads and unswept .rhi-* temp copies | audit:data-safety | P1 |
| #24 | DEP-05 | DEP-05: lock user.reg and rewrite atomically; refuse edits while Wine owns the prefix; ShortFuse truncate-and-write restore | audit:data-safety | P1 |
| #25 | DEP-06 | DEP-06: RenoDX add-on downloads need an HTTPS + host allow-list (DLLs from any URL today) | audit:data-safety | P0 |
| #26 | DEP-07 | DEP-07: GitHub asset digests are optional: verification is fail-open almost everywhere | audit:data-safety | P1 |
| #27 | DEP-08 | DEP-08: ReShade binaries: no hash, a third-party proxy for nightly builds, and a size-only identity | audit:data-safety | P1 |
| #28 | DEP-09 | DEP-09: ReShade Addon Manager: fragile rollback, a separate ledger and unvalidated URLs | audit:data-safety | P1 |
| #29 | DEP-10 | DEP-10: RenoDX can overwrite and later delete a foreign file without a backup | audit:data-safety | P1 |
| #30 | DEP-11 | DEP-11: Wine override restore ignores user changes; the registry backup is taken once and never used | audit:data-safety | P1 |
| #31 | DEP-12 | DEP-12: Writes are not durable, and several "atomic" copies verify length only | audit:data-safety | P1 |
| #32 | DEP-13 | DEP-13: The record store re-reads and re-parses the whole JSON on every `Find`; `Decorate` does this about 39 times per refresh | audit:data-safety | P3 |
| #33 | DEP-14 | DEP-14: Hash and copy amplification in `TransactionalFileDeploymentService` | audit:data-safety | P2 |
| #34 | DEP-15 | DEP-15: Whole-file reads and naive scans in binary sniffing, repeated per probe | audit:data-safety | P2 |
| #35 | DEP-16 | DEP-16: No conditional requests, no release-lookup cache, and repeated downloads (GitHub rate limit) | audit:data-safety | P2 |
| #36 | DEP-17 | DEP-17: GitHub token stored in plain text with no permission check | audit:data-safety | P3 |
| #37 | DEP-18 | DEP-18: External extractors: no timeout and no post-extraction containment or symlink check | audit:data-safety | P3 |
| #38 | DEP-19 | DEP-19: `GameKey` identity and absolute paths make records brittle across library moves | audit:data-safety | P3 |
| #39 | DEP-20 | DEP-20: `DlssManagerService` cache hygiene | audit:data-safety | P3 |
| #40 | DEP-21 | DEP-21: `RestoreAsync` deletes an unowned `.rhi_custom` sidecar | audit:data-safety | P3 |
| #41 | DEP-22 | DEP-22: The Dlssg2030 doc claims a pinned commit, but the code resolves the latest commit at runtime | audit:data-safety | P3 |
| #42 | DEP-S1, DEP-S4, DEP-S5 | Deployment layer shared toolkit: TrustedDownload / AtomicFile / OwnedFileSet kit (~2–3k duplicated lines) | audit:structure | P2 |
| #43 | DET-03 | DET-03: Startup library scan waits behind an untimed DLSS manifest fetch (up to 5 min) | audit:detection | P1 |
| #44 | DET-05 | DET-05: Lutris native-wrapper games take the wrapper's folder as install path, and distinct games merge into one card | audit:detection | P1 |
| #45 | DET-06 | DET-06: Manifest `installPathOverrides` and `splitGames` can move `InstallPath` outside the game | audit:detection | P1 |
| #46 | DET-07 | DET-07: HDR launch wraps `xdg-open` for Heroic games and reports HDR as applied | audit:detection | P1 |
| #47 | DET-08 | DET-08: Steam roots and libraries are de-duplicated by string, so each library is walked 2–3 times | audit:detection | P2 |
| #48 | DET-09 | DET-09: Scan-time indexes are thrown away, and several walks ignore cancellation | audit:detection | P2 |
| #49 | DET-10 | DET-10: Lutris YAML parsing: folded/multi-line scalars truncate `exe`, and names fall back to a timestamped config stem | audit:detection | P2 |
| #50 | DET-11 | DET-11: WinePrefixDetector double-lists Lutris/umu prefixes, uses the wrong granularity, and launches Proton prefixes with system `wine` | audit:detection | P2 |
| #51 | DET-12 | DET-12: Heroic fuzzy prefix matching can attach the wrong Wine prefix | audit:detection | P2 |
| #52 | DET-13 | DET-13: Runtime-log attribution matches on the full log path, so a launcher inherits another game's renderer at High confidence | audit:detection | P2 |
| #53 | DET-14 | DET-14: An executable containing several API strings is reported as the highest API at High confidence | audit:detection, needs-decision | P2 |
| #54 | DET-15 | DET-15: PCGamingWiki fallback overrides native Linux results, caches no misses, and blocks selection | audit:detection | P2 |
| #55 | DET-16 | DET-16: `GameLibraryScanner.Merge` is lossy, and the merge key is not canonical | audit:detection | P2 |
| #56 | DET-17 | DET-17: Detector failures are swallowed and the scan still reports success | audit:detection | P2 |
| #57 | DET-18 | DET-18: every JSON store fails open to empty state and the next save overwrites user data (game-library, settings, API overrides, prefs, addon-manager, …) — not only deployments.json | audit:detection | P1 |
| #58 | DET-19 | DET-19: Removing or adding manual games can silently do nothing | audit:detection | P2 |
| #59 | DET-20 | DET-20: Manual (and Heroic) entries accept a prefix root, `$HOME` or `/` as the game root (sibling of the HEAD fix) | audit:detection | P1 |
| #60 | DET-21 | DET-21: `_probeCache` holds full file lists for every game ever selected, with no eviction | audit:detection | P2 |
| #61 | DET-22 | DET-22: Every refresh, including the "6 ms" partial one, invalidates and re-downloads the upstream manifest | audit:detection | P2 |
| #62 | DET-23 | DET-23: Path-identity canonicalisation assigns other titles' policies (sibling of DET-01) | audit:detection | P2 |
| #63 | DET-24 | DET-24: Synchronous JSON reads on the UI thread, N+1 per property evaluation | audit:detection | P2 |
| #64 | DET-25 | DET-25: Other recursive enumerations still follow symlinks | audit:detection | P3 |
| #65 | DET-26 | DET-26: `FindLikelyExecutable` hard-ignores by substring | audit:detection | P3 |
| #66 | DET-27 | DET-27: Steam classification is shallow | audit:detection | P3 |
| #67 | DET-28 | DET-28: Lutris path expansion is partial | audit:detection | P3 |
| #68 | DET-29 | DET-29: Refresh UX and cancellation | audit:detection | P3 |
| #69 | DET-30 | DET-30: Manual UE inference stops at `<Project>` | audit:detection | P3 |
| #70 | DET-31 | DET-31: Launcher assumptions | audit:detection | P3 |
| #71 | UI-03 | UI-03: Property getters do synchronous disk I/O on the UI thread (≈220 full `deployments.json` parses per game selection) | audit:ui | P1 |
| #72 | UI-04 | UI-04: Several bulk operations clear the cache without re-probing, which wedges component actions ("wait for analysis") | audit:ui | P1 |
| #73 | UI-05 | UI-05: Unhandled exceptions from UI handlers and persistence writes can terminate the process | audit:ui | P1 |
| #74 | UI-06 | UI-06: `_probeCache` keeps every visited game's full file index for the whole session | audit:ui | P2 |
| #75 | UI-07 | UI-07: Each search keystroke and filter change resets the selection, cancelling and restarting analysis | audit:ui | P2 |
| #76 | UI-08 | UI-08: `IsBusy` has no owner, so concurrent operations clear each other's gate | audit:ui | P1 |
| #77 | UI-09 | UI-09: Long operations cannot be cancelled while the busy overlay blocks the whole window | audit:ui | P2 |
| #78 | UI-10 | UI-10: Edit-game dialog closes by matching localised success text (add dialog fixed; finish the result record) | audit:ui | P2 |
| #79 | UI-11 | UI-11: localisation split across two mechanisms; PT-only strings remain (tray, pickers, window titles) | audit:ui | P2 |
| #80 | UI-12 | UI-12: A pinned neural version is saved but the ComboBox shows blank | audit:ui | P2 |
| #81 | UI-13 | UI-13: The background update loop dies permanently on its first exception | audit:ui | P2 |
| #82 | UI-15 | UI-15: The probe mutates the shared `DetectedGame` off-thread while getters read it | audit:ui | P3 |
| #83 | UI-16 | UI-16: Some mutations leave the cached file index stale | audit:ui | P3 |
| #84 | UI-17 | UI-17: "Clear cache" runs synchronously on the UI thread | audit:ui | P3 |
| #85 | UI-18 | UI-18: `DetectedGame` is not observable | audit:ui | P3 |
| #86 | UI-19 | UI-19: "Latest version" caches never expire | audit:ui | P3 |
| #87 | UI-20 | UI-20: Duplicated display↔value mapping between the VM and the Settings dialog | audit:ui | P3 |
| #88 | UI-14 | UI-14: Enable compiled bindings (212 reflection bindings, no build-time checking) | audit:ui | P1 |
| #89 | UI-Structure | Epic: Split MainWindowViewModel (7 units) | audit:structure, audit:ui | P2 |

### Follow-ups from the upstream-sync lane reviews

Review files: `/mnt/vdo_storage/worktrees/rhi-linux/reviews/sync-*.md`.

| # | Finding | Title | Labels | Priority |
|---|---|---|---|---|
| #102 | sync-12 review 1 | PCGW data: first 5 MB download is tied to the selected game's cancellation token (up to 120 s stall) | audit:detection | P2 |
| #103 | sync-12 review 2 | PcgwCentralDataService swallows every fetch and parse failure (bare catch blocks, no diagnostics) | audit:detection, hygiene | P3 |
| #104 | sync-12 review 3 | PCGW data is never refreshed within a session (no invalidation hook) | audit:detection | P3 |
| #105 | sync-12 review 4 | Test gap: PCGW in-file entry without API data must skip the live PCGW lookup | audit:detection, hygiene | P3 |
| #106 | sync-22 review 1 (see #46) | Unify store hand-off classification: LinuxGameLauncher.IsStoreHandoff vs LinuxHdrLaunchProfileService.IsLauncherMediated | audit:detection | P2 |
| #107 | sync-13-14 review 1 | RenoDX add-on cache is judged current by Content-Length only; a same-size rebuilt snapshot is served stale | audit:data-safety | P2 |
| #108 | sync-13-14 review 2 | Bare catch around the RenoDX cache HEAD check swallows programming faults | hygiene | P3 |
| #109 | sync-18 review 1 | Update history logs a failed (rolled-back) update as a success | audit:data-safety | P2 |
| #110 | sync-18 review 2 | Test gap: LinuxManagedComponentService.Track wiring into the update recorder is untested | hygiene | P3 |
| #111 | sync-18 review 4 | Update history silently stops recording on a log write error | audit:ui | P3 |
| #113 | sync-10-19 review 3 | Manual Update All reports every component as "updated" even when nothing changed (all component types) | audit:ui | P2 |
| #114 | sync-10-19 review 3 | DLSS5 Feeder swap re-downloads the whole release zip per game on every manual run | audit:data-safety | P3 |
| #115 | sync-10-19 review 4 | Legacy add-on-only DLSS5 Feeder installs: pin change always fails and Update All reports them as failed | audit:ui | P2 |
| #117 | sync-10-19 review 6 | Test: Full Refresh re-fetches NR version lists | hygiene | P3 |
| #118 | sync-10-19 re-review nit 2 | Record-copy uses indented JSON options (perf nit) | hygiene | P3 |
| #119 | #44/#50 decision; fix-50 review 1 | List games inside Lutris launcher prefixes as their own cards | audit:detection | P1 |
| #121 | fix-15 review 1 | ReShade.ini rewritten non-atomically by RenoDxIniOverrideManager; no snapshot on remove | audit:data-safety | P1 |
| #122 | fix-15 review 3 | Manifest renodxIniOverrides are not injection-checked | audit:data-safety | P2 |
| #123 | fix-15 review 2 | UPSTREAM.md #15: database keys first applied on update overwrite earlier user values | hygiene | P3 |
| #124 | fix-16 review 1–3 | Control UE post-install: keep .rhi-control-previous, update re-apply over user changes, missing tests | audit:data-safety | P3 |
| #125 | fix-50 review 2, 3 | Proton wine path not tied to a Proton install; Winetricks on orphaned Proton prefix uses system wine | audit:detection | P2 |
| #126 | fix-11 review 1 | Available HDR mods: add RHI Unity database games | upstream-drift | P3 |
| #127 | fix-header follow-up | Library overlay: dim/cover the status bar while open | audit:ui | P3 |
| #128 | fix-responsive follow-up | Responsive panels: share invalidation list, watch late children, pin Avalonia internals; tray restore un-maximises | audit:ui, hygiene | P3 |
| #142 | wave3 (up-129 re-review) | Tests write fake add-ons into the real ~/.cache/rhi-linux (isolate all XDG paths in tests; verify cache before reuse) | audit:data-safety | P0 |
| #143 | wave3 harness, up-131 lane | Portuguese-only message in the English UI when an automatic ReShade dependency fails | audit:ui | P2 |

Merged into `integration/wave2` (open until Robbie's GUI check): #50 `07d6eda`, #44 `c4cc212`, #127/#128 `9983d1b`. Owner decisions (recorded on the tickets): a Lutris entry with no real game folder (Rekordbox) is not listed (`884c716`); games inside Lutris launcher prefixes become their own cards (#119). Integration fixes: scanner merge keeps `RequiresExplicitPatchTarget` (`963059c`, fix-44 review 1); direct-wine Proton prefixes report native-Wayland HDR as Blocked (`327ea7f`, fix-50 review 4).

## feature-parity

| # | Finding | Title | Labels | Priority |
|---|---|---|---|---|
| #90 | PAR-03 | [parity] Store coverage: Xbox/Game Pass, EA App, Ubisoft, Battle.net, Rockstar, itch.io | feature-gap, needs-decision | P3 |
| #91 | PAR-09 | [parity] ReShade "No Addons" channel | feature-gap, needs-decision | P3 |
| #92 | PAR-10 | [parity] ReShade for Vulkan games (global implicit layer) | feature-gap, needs-decision | P3 |
| #93 | PAR-14 | [parity] Named shader selection profiles with zip export | feature-gap, needs-decision | P3 |
| #94 | PAR-23 | [parity] Luma mods from GitHub release assets not yet on the wiki | feature-gap, needs-decision | P3 |
| #95 | PAR-33 | [parity] Per-game opt-in DLSS auto-update from the manifest | feature-gap, needs-decision | P3 |
| #96 | PAR-43 | [parity] NR Cost Scaler | feature-gap, needs-decision | P3 |
| #97 | PAR-48 | [parity] OptiScaler per-GPU INI templates | feature-gap, needs-decision | P3 |
| #98 | PAR-49 | [parity] OptiPatcher auto-deploy | feature-gap, needs-decision | P3 |
| #99 | PAR-58 | [parity] Nexus Mods integration (NXM handler, API key, links) | feature-gap, needs-decision | P3 |
| #100 | PAR-62 | [parity] App self-update check | feature-gap, needs-decision | P3 |
| #120 | PAR-53 (deferred from #11) | User-chosen proxy DLL name for ReShade/OptiScaler (upstream DLL-naming dropdowns) | feature-gap | P3 |
| #134 | PAR-D (2.8.1) | [2.8.1] GitHub sign-in (device flow) in Settings | needs-decision, upstream-drift | P2 |

Not filed (already fixed on `main` by `3aa0f21`/`5d431b8`): DET-01, DET-02, DET-04, UI-01, UI-02, add-game `Added` check, DEP-01, DEP-02, DEP-04, PAR-D03, PAR-D13, PAR-D14, Lutris relative-exe / dosdevices indexing, partial rescan.
Not filed separately: PAR-D07 (moot until Vulkan ReShade, see the Vulkan parity issue), PAR-D15 (arrives through the live manifest), DEP-S2/S3 (inside the toolkit issue), PAR-B03/B09 (informational).
