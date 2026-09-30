# RHI-Linux parity and project-health audit

- Audit date: 2026-09-30 (CEST). Read-only. No remote writes.
- Downstream baseline: local worktree `3aa0f21` (local-only commit by "nonspacial", not on `wizardkof/RHI-Linux`; remote `main` head is `f00fef4`, "Release RHI-Linux v0.9.23.4-beta.1", 2026-09-21).
- Upstream baseline: `RankFTW/RHI` `main` at `6a593fc` (2026-09-30), cloned to `.scratch/upstream-rhi/` (shallow, read-only).
- The fork pins upstream manifest commit `a94a5745` = upstream v2.7.5 (`src/RHI.Platform.Linux/Deployment/UpstreamRhiManifestService.cs:224`; `validate.sh:67`).
- Raw API captures are in `.scratch/api/*.json`. The filtered upstream commit list since 2.7.5 is in `.scratch/audit/upstream-commits-since-2.7.5.txt` (174 non-data/non-doc commits; 248 in total).
- Policy read first: `UPSTREAM.md:3-18` and `README.md` (Upstream section). The fork targets *functional* parity for Wine/Proton games. Windows NvAPI/DRS, DXVK/VKD3D management and native-Linux-game injection are explicitly out of scope.

Status key: **present** · **partial** · **absent** · **N/A** (not applicable on Linux per the fork's stated policy).

---

## A. Feature parity table

Upstream sources: README <https://github.com/RankFTW/RHI/blob/main/README.md>, patch notes `RenoDXCommander/RHI_PatchNotes.md`, and releases <https://github.com/RankFTW/RHI/releases>.

| ID | Upstream feature family | RHI-Linux | Evidence (RHI-Linux path:line) / note |
|---|---|---|---|
| PAR-01 | Library detection: Steam | present | `src/RHI.Platform.Linux/Detection/SteamGameDetector.cs:8`, launch `:73` |
| PAR-02 | Epic / GOG stores | present (adapted) | Via Heroic: `Detection/HeroicGameDetector.cs:13,83` |
| PAR-03 | Xbox/Game Pass, EA App, Ubisoft, Battle.net, Rockstar, itch.io | partial (adapted) | No native store readers. Coverage comes only through Lutris/Wine prefixes/manual add. Launcher executables are recognised in `Settings/LinuxGameIdentityService.cs:14-22,124-127`. |
| PAR-04 | Drag-and-drop `.exe` / addon / archive | present | `Ux/LinuxDroppedFileClassifier.cs:15-32`, `src/RHI.Desktop/Views/MainWindow.Ux.cs:453-458` |
| PAR-05 | Graphics-API detection (PE scan) + per-game override | present | `Detection/GraphicsApiDetector.cs:12`, `Paths/GraphicsApiOverrideStore.cs:12` |
| PAR-06 | PCGamingWiki data (API hint, link, engine badge) | partial | Uses per-game PCGW API calls (`Detection/PcGamingWikiGraphicsApiService.cs:39,118`). Upstream switched to a bundled `pcgw_data.json` in 2.7.6. See PAR-D04. |
| PAR-07 | ReShade, fully managed, with API-correct DLL naming | present | `Deployment/ReShadeDeploymentService.cs:11`, proxy map `:942-948` |
| PAR-08 | ReShade channels Stable/Nightly/Custom/Legacy | present | `Deployment/ReShadeOptions.cs:3-9` |
| PAR-09 | ReShade "No Addons" channel | absent | The enum has no such member (`ReShadeOptions.cs:3-9`). |
| PAR-10 | ReShade for Vulkan games (global implicit layer) | absent | `SelectProxyName` returns null for Vulkan (`ReShadeDeploymentService.cs:942-948`). The UI blocks it (`LinuxManagedComponentService.cs:230-233`). Feasibility under Proton was not assessed. |
| PAR-11 | Foreign-DLL protection / `.original` backups | present | Record store `Deployment/LinuxDeploymentRecordStore.cs`; foreign-DLL preservation `ReShadeDeploymentService.cs:496` |
| PAR-12 | `reshade.ini` seeding, hotkeys, screenshot path | present | `Deployment/ReShadeIniManager.cs`, `Settings/ReShadeGlobalSettingsApplier.cs`, `Desktop/Views/ReShadeHotkeyEditor.cs:13` |
| PAR-13 | Shader-pack management (upstream: 46+ packs, per-file selection) | present | 49 built-in entries (`Deployment/ReShadeShaderPackCatalog.cs:16`ff). Live-manifest packs are merged (`ReShadeShaderPackService.cs:420-460`). |
| PAR-14 | Named shader selection profiles, zip export/share | absent | No implementation found (grep for profile/export in `src/`). |
| PAR-15 | ReShade addon manager + custom addons | present | `Deployment/ReShadeAddonManagerService.cs:46`, `ViewModels/MainWindowViewModel.cs:2629` |
| PAR-16 | RenoDX (named mods, auto-detect) | present | `Deployment/RenoDxDeploymentService.cs:24`, `RenoDxCatalogService.cs` |
| PAR-17 | UE-Extended + Engine.ini HDR | present | `Deployment/UpstreamEngineIniFileManager.cs:36`, `DetectedGame.cs:110-112` |
| PAR-18 | Generic Unity RenoDX | present | `Deployment/RenoDxCatalogService.cs:27-28,182` |
| PAR-19 | Unity per-game INI settings (2.7.9) and named compat-cog options (2.7.7/2.7.9) | absent / partial | There is no Unity settings consumer. The RenoDX INI overrides exist in `RenoDxIniOverrideManager.cs`. |
| PAR-20 | RenoDX Upgrade (render-target upgrade snapshot) | present | `Deployment/RenoDxUpgradeDeploymentService.cs:14` |
| PAR-21 | RenoFX HDR Toolkit | present (as shader pack) | `ReShadeShaderPackCatalog.cs:64` |
| PAR-22 | Luma Framework (named + generic, Engine.ini keys) | present | `Deployment/LumaFrameworkDeploymentService.cs:29`, `LumaEngineIniOverrideManager.cs:20`, `LumaCatalogService.cs:39` |
| PAR-23 | Luma mods from GitHub release assets not yet on the wiki (2.7.6) | partial | The releases API is used for known repos (`LumaFrameworkDeploymentService.cs:873-879`). The upstream wiki-unlisted merge was not found. |
| PAR-24 | DXVK + Lilium HDR DXVK for DX9/DX11 | N/A (policy) | Delegated to Proton/Wine (`Desktop/Views/MainWindow.Ux.cs:584-589`; `UPSTREAM.md:13`). The Lilium HDR-DXVK variant has no Linux equivalent offered. |
| PAR-25 | DX9 translation for ReShade/Feeder (dgVoodoo2) | present | `Deployment/DgVoodooDeploymentService.cs:21`, `Dx9TranslationReShadeCoordinator.cs:17` |
| PAR-26 | ReLimiter | present | `Desktop/App.axaml.cs:52-60` via `GitHubReShadeAddonDeploymentService.cs:33` |
| PAR-27 | Display Commander (mutually exclusive with ReLimiter) | present | `App.axaml.cs:61-69` |
| PAR-28 | DLSS SR/RR/FG version swap + restore originals | present | `src/RHI.Core/Services/DlssManagerService.cs:14`; `MainWindowViewModel.cs:1891` |
| PAR-29 | Streamline version swap | present | `DlssManagerService.cs:211`; `MainWindowViewModel.cs:2115,2162` |
| PAR-30 | Global DLSS defaults + Quick Apply | present | `Deployment/LinuxDlssDefaultsService.cs:72`; `MainWindowViewModel.cs:1863` |
| PAR-31 | Batch DLSS deploy to many games | present | `Desktop/Views/MainWindow.axaml.cs:114` |
| PAR-32 | DLSS presets / render-scale override / "NVIDIA Override latest DLL" via driver profile | N/A (policy) | NvAPI/DRS is not emulated (`README.md` Compatibility; `DlssManagerService.cs:12`). OptiScaler-side presets and render scale exist (`OptiScalerDeploymentService.Advanced.cs:58-66`). |
| PAR-33 | DLSS auto-update from manifest | partial | Update All covers managed components (`LinuxUpdateAllService.cs:108`). Per-game DLSS auto-update opt-in was not found. |
| PAR-34 | NVIDIA driver profile panel (VSync, LLM, Smooth Motion, ReBAR, RTX HDR, G-Sync, vibrance, export/import) | N/A (policy) | No code, which is intentional (`README.md` "Windows-specific NVIDIA features"). |
| PAR-35 | RTX 50 MFG mode/frame-count config | N/A (policy) | Driver-profile based upstream (`MfgDialog.cs`, `DlssPresetService.cs`). |
| PAR-36 | RTX 40 MFG Unlock (ASI) / Universal MFG | present (adapted) | `Deployment/Rtx40MfgStandaloneDeploymentService.cs:20` |
| PAR-37 | MFG Ada Unlock addon | present | `App.axaml.cs:70-78` |
| PAR-38 | RTX 20/30 DLSSG unlock | present (experimental) | `Deployment/Dlssg2030UnlockDeploymentService.cs:19` |
| PAR-39 | DLSS5 Feeder full pipeline | present (experimental) | `Deployment/Dlss5FeederFullDeploymentService.cs:26` |
| PAR-40 | ShortFuse DLSS Tool | present | `Deployment/ShortFuseDeploymentService.cs:15` |
| PAR-41 | DLSS5 DX11 Bridge | present | `App.axaml.cs:83-91` |
| PAR-42 | NR DLL version selection | partial | There are global defaults (`MainWindowViewModel.cs:580`) and per-game addon version pins (`LinuxManagedComponentService.cs:1263`). In-place swap while installed (upstream 2.7.6) was not verified. |
| PAR-43 | NR Cost Scaler | absent | No match in `src/`. Upstream has it (10 files match `CostScaler`). |
| PAR-44 | DLSS Tool "ZZZ load order" toggle (2.7.9) | absent | No match in `src/`. |
| PAR-45 | OptiScaler Stable/Nightly/DLSS-NR variants | present | `Paths/OptiScalerVariantPreferenceStore.cs`; `OptiScalerDeploymentService.cs` |
| PAR-46 | OptiScaler Streamline + DLSS Enabler deploy | present | `OptiScalerDeploymentService.Advanced.cs:137`; `.Streamline.cs` |
| PAR-47 | OptiScaler FG settings (input/output/Nvngx/HUD fix) | partial | Present, but the "Combo" Nvngx option (upstream 2.7.6) is missing (`OptiScalerDeploymentService.Advanced.cs:54-56`). RR presets stop at E, so Preset F is missing (`:59`, `BuildPresetOptions(5)` at `:299-309`). |
| PAR-48 | OptiScaler per-GPU INI templates (6 templates) | absent | No template files or selection logic found. Upstream ships `OptiScaler.amd-dlss.ini` etc. |
| PAR-49 | OptiPatcher auto-deploy | absent | No match in `src/`. |
| PAR-50 | REFramework | present | `Deployment/REFrameworkDeploymentService.cs:15` |
| PAR-51 | Ultimate ASI Loader | present | `Deployment/UltimateAsiLoaderDeploymentService.cs:20` |
| PAR-52 | Universal UE DOF Fix | present | `Deployment/DofFixDeploymentService.cs:20` |
| PAR-53 | Per-component DLL naming overrides (user dropdowns, 2.7.6) | partial | Model fields are populated from the manifest only (`DetectedGame.cs:116-118`, `UpstreamGameLibraryPolicyService.cs:136`). There is no user-facing dropdown in `src/RHI.Desktop`. |
| PAR-54 | Update All + background check | present | `LinuxUpdateAllService.cs:108`; `MainWindowViewModel.cs:1153`; tests `UpdateAllBackgroundTests.cs` |
| PAR-55 | Component update-history log (2.7.9) | absent | No match in `src/`. |
| PAR-56 | "Available HDR Mods" dialog (2.7.6) | absent | No match in `src/`. |
| PAR-57 | Manifest-driven behaviour (live + fallback) | present, drifting | Live `main` manifest with a fallback pinned to v2.7.5 (`UpstreamRhiManifestService.cs:222-226`). See PAR-D03 for schema drift. |
| PAR-58 | Nexus Mods integration (NXM, API key, links) | absent | Only manifest `nexusUrlOverrides` is parsed (`UpstreamRhiManifestPolicy.cs:118`, `DetectedGame.cs:123`). Nothing in `src/RHI.Desktop` surfaces it. |
| PAR-59 | HDR auto-toggle on launch | present (adapted) | Per-game Native Wayland / Gamescope launch profile (`Hdr/LinuxHdrLaunchProfileService.cs:7-19,67,101-106`). |
| PAR-60 | Launch game through RHI | present (adapted) | `Launch/LinuxGameLauncher.cs:14-43` |
| PAR-61 | Tray, MOTD, Quick Start, favourites/hidden | present, MOTD broken | Tray: `MainWindowViewModel.cs:157-158`. Favourites: `DetectedGame.cs:21-23`. The MOTD URL `https://raw.githubusercontent.com/wizardkof/RHI-Linux/main/motd.md` (`src/RHI.Core/RhiProductInfo.cs:9`) returned **HTTP 404** on 2026-09-30, and `motd.md` is not in the repo tree. |
| PAR-62 | App self-update check | absent | No match. The Linux build has no update-check path. |
| PAR-63 | Control Ultimate Edition RR automation (2.7.9) | absent | No `renderer.ini` handling in `src/`. |
| PAR-64 | UI localisation | Linux extra | EN + PT-BR (`Desktop/Localization/UiLocalizationService.cs:6`). Upstream is English-only. |
| PAR-65 | Linux-only additions | Linux extra | Wine DLL overrides (`WineDllOverrideService.cs:10`), D3DCompiler into prefix (`D3DCompilerToolInstaller.cs:13`), standalone DLSS Enabler (`StandaloneDlssEnablerDeploymentService.cs:18`) |

Summary: the core install families are **present** (ReShade, RenoDX, Luma, OptiScaler, REFramework, UAL, ShortFuse, dgVoodoo, DLSS/Streamline swap, NR methods, frame limiters). Gaps are concentrated in upstream "comfort" features (update log, HDR mods dialog, Nexus, shader profiles, OptiPatcher, OptiScaler templates, Cost Scaler, ZZZ toggle, Vulkan ReShade). The NVIDIA driver surface is excluded by policy.

---

## B. Upstream drift since RHI 2.7.5

Upstream shipped **four releases after 2.7.5**, plus an unreleased 2.8.0 beta on `main`:

| Tag | Published (UTC) | URL |
|---|---|---|
| RHI-2.7.6 | 2026-09-24 14:55 | <https://github.com/RankFTW/RHI/releases/tag/RHI-2.7.6> |
| RHI-2.7.7 | 2026-09-26 21:37 | <https://github.com/RankFTW/RHI/releases/tag/RHI-2.7.7> |
| RHI-2.7.8 | 2026-09-27 21:14 | <https://github.com/RankFTW/RHI/releases/tag/RHI-2.7.8> |
| RHI-2.7.9 | 2026-09-29 18:34 | <https://github.com/RankFTW/RHI/releases/tag/RHI-2.7.9> |
| 2.8.0 Beta (untagged) | `main` @ `6a593fc`, 2026-09-30 | `RenoDXCommander/RHI_PatchNotes.md` |

`git rev-list a94a5745..origin/main` returns 248 commits; 174 remain after excluding `pcgw_data.json`, patch-note and doc commits.

Relevant items RHI-Linux now lacks. Windows-only freezes, WinUI layout, tray/foreground, NVAPI and driver-panel items are excluded.

- **PAR-D01 (2.7.6) NR in-place version swap and NR DLL selection for all methods.** Upstream commits `264adc5` and `0e59251`. Linux has version pins only (PAR-42).
- **PAR-D02 (2.7.6) Available HDR Mods dialog, collapsed-section summaries and DLL-naming dropdowns.** Upstream commit `748ab76` and the 2.7.6 notes. Linux: PAR-56 and PAR-53 are absent or partial.
- **PAR-D03 (2.7.7) Manifest schema drift.** Upstream renamed `engineOverrides` to `_engineOverrides` and added `_engineOverrides_disabled` (commit `4ff4f77`, 2026-09-26, "engine names now from pcgw_data.json"). The Linux reader still binds `"engineOverrides"` (`UpstreamRhiManifestService.cs:71-72`, consumed at `:442,461`), so against the live manifest it now gets `null`. Engine names for those titles would then fall back to the pinned 2.7.5 manifest only if the live fetch fails. This is inferred from code and the JSON diff, not observed at runtime.
- **PAR-D04 (2.7.6/2.7.7) PCGW data model.** Upstream loads one `pcgw_data.json` (about 55k games, engine names included). Linux still issues per-game PCGW API requests and has no engine-name source. Upstream also shipped PCGW rate-limit/Retry-After backoff (merged PR #102, `8ed40ad`). Linux `PcGamingWikiGraphicsApiService.cs` was not checked for equivalent backoff.
- **PAR-D05 (2.7.6) OptiScaler "Combo" FG Nvngx option and RR Preset F.** Commit `431bbcc`. Missing on Linux (PAR-47).
- **PAR-D06 (2.7.6) Unity addon URLs moved to GitHub Releases for reliable caching.** Commits `edbae2c` and `601e9f9`. Linux hard-codes the `NotVoosh/renodx-unity` snapshot release URLs (`RenoDxCatalogService.cs:27-28`), so its caching behaviour here was not assessed.
- **PAR-D07 (2.7.7) ReShade handling on Graphics-API switch to/from Vulkan.** Commits `e8cdedf` and `4fb2356`. On Linux this is moot while Vulkan ReShade is absent (PAR-10).
- **PAR-D08 (2.7.9) Unity game-specific INI settings, Control RR automation, ZZZ load-order toggle and component update history.** Commits `6651379` and `04ca3a4`. All absent (PAR-19, PAR-63, PAR-44, PAR-55).
- **PAR-D09 (2.7.9) NR fixes.** rc10-vs-rc5 sort (`95ad66c`), Full Refresh bypassing the NR version cache (`ba7ef69`), auto-redeploy of untracked NR files (`818ce9f`). Linux equivalents were not verified. Its sort logic was not located in this pass.
- **PAR-D10 (2.7.9) Global shader "Off" overriding per-game overrides; custom-folder auto-tick.** Commits `cde8f9d` and `d5e5af4`. Not verified on Linux.
- **PAR-D11 (2.7.9) Custom-channel false update notification.** Commits `ce148fa` and `8e8de89`. Not verified on Linux.
- **PAR-D12 (2.7.9) Working directory not set for exe-override launches.** Linux launches via `/bin/sh -lc` with the stored command (`LinuxGameLauncher.cs:19-31`). The Wine-prefix command is `WINEPREFIX=… wine exe` with no `cd` (`WinePrefixDetector.cs:59`). Whether this reproduces the upstream bug under Wine is **unverified**; worth a test.
- **PAR-D13 (2.8.0 beta) OptiScaler uninstall deleting game `plugins\`.** Commit `5a45f2a`. **Not affected:** Linux only removes empty directories it created (`OptiScalerDeploymentService.cs:554-564`).
- **PAR-D14 (2.7.6) Circular-symlink scan hang (RTX Remix).** Commit `1784726`. **Already covered:** `GameFileIndexer` depth limit is 8 (`src/RHI.Core/Services/GameFileIndexer.cs:19`), and local commit `3aa0f21` fixes prefix traversal.
- **PAR-D15 Manifest content.** New keys since 2.7.5 (`installWarnings`, `nexusUrlOverrides`, `installPathOverrides`, `lumaNameOverrides`, `dgVoodooVersions` 2.87.5, shader pack `UltrawideSideGlass`) arrive automatically through the live manifest where Linux parses those keys. Shader packs merge at `ReShadeShaderPackService.cs:450-460`.

---

## C. Contribution channels

**Plain answer:**

- A pull request to `wizardkof/RHI-Linux` **is possible**. PRs are enabled for everyone. Issues are **disabled**, and Discussions are disabled.
- A PR or issue to upstream `RankFTW/RHI` is also possible. Upstream Issues and PRs are enabled, and a competing Linux-port PR is already open there.
- There is no other formal channel: no CONTRIBUTING file, no Discussions, no issue templates.

Evidence, from the GitHub API, unauthenticated, 2026-09-30:

- **PAR-C01** `GET /repos/wizardkof/RHI-Linux` returns `fork: true`, parent/source `RankFTW/RHI`, `has_issues: false`, `has_discussions: false`, `has_pull_requests: true`, `pull_request_creation_policy: "all"`, `has_wiki: true`, `open_issues_count: 0`, 3 stars, 1 fork, GPL-3.0, created 2026-09-04, last push 2026-09-21. <https://api.github.com/repos/wizardkof/RHI-Linux>
- **PAR-C02** `GET /repos/wizardkof/RHI-Linux/pulls?state=all` returns `[]`: no PRs have ever been opened. `/discussions` returns HTTP 404.
- **PAR-C03 Contributors.** `RankFTW` has 1123 commits (inherited upstream history) and `wizardkof` has 10. The 10 fork commits are: the initial port on 2026-09-04 (`46bd855`, "Port RHI to native Linux: v0.1.0", 391 files changed, −106 965 lines, which replaced the upstream tree), seven README edits on 2026-09-04/05, and one squashed release commit on 2026-09-21 (`f00fef4`). **The cadence is two bursts in 17 days, with no pushes since 2026-09-21.** Development appears to happen off-GitHub and land as squashed drops.
- **PAR-C04 Divergence.** `compare RankFTW/RHI:main...wizardkof:RHI-Linux:main` reports `diverged`, 10 ahead and 529 behind, merge-base `199fe12` (upstream v2.5.7, 2026-09-03). Upstream "sync" is therefore manual and behavioural. There is no git-level merge path, and a PR from the fork into upstream is not viable as-is.
- **PAR-C05 Releases.** One pre-release, `v0.9.23.4-beta.1` (2026-09-21): <https://github.com/wizardkof/RHI-Linux/releases/tag/v0.9.23.4-beta.1>. Assets are `…-x86_64.AppImage` (40.2 MB, 5 downloads) and `.sha256` (1 download). The SHA-256 matched (`fd769ed6…`). The `.sha256` file embeds the builder's local path `/home/reliuz/Downloads/…/artifacts/appimage/…`, so it was built locally, not in CI.
- **PAR-C06 CI runs.** Two `Linux build` runs, both on `f00fef4`, both `success` (e.g. <https://github.com/wizardkof/RHI-Linux/actions/runs/35589134643>).
- **PAR-C07 Upstream Linux tracking.** Searched upstream issues/PRs for `linux`, `proton`, `wine` and `RHI-Linux`:
  - #1 "Linux support?" (closed). Owner: "maybe down the line".
  - #11 "Native linux port" (closed). The owner endorsed **fornkatt/Restall** as the Linux route ("Fornkatt has my full backing").
  - #84 "Is my linux branch of interest?" (open, TwoToneEddy, no owner reply).
  - **PR #116 "Add native Linux/Proton support with shared core and upstream tracking"** (open, TwoToneEddy, 2026-09-28): <https://github.com/RankFTW/RHI/pull/116>. It proposes a shared `RHI.Core` for WinUI and Avalonia plus CI upstream-drift checks.
  - `wine` returns 0 hits. `RHI-Linux`/`wizardkof` returns no mention of this fork upstream.
- **PAR-C08 Upstream merge practice.** Of 15 upstream PRs, 2 were merged (#63, #102), 6 are open, and the rest were closed unmerged. The Chinese localisation PRs #20 and #53 were not merged. There is no `CONTRIBUTING.md` (HTTP 404) and no `.github/` directory upstream.

Practical implication: fixes to RHI-Linux go by PR to `wizardkof/RHI-Linux`. Given zero PR history and squashed off-GitHub drops, acceptance is untested. Linux interest upstream is concentrated in PR #116, which is a separate and competing port, and the owner has previously pointed users to Restall.

---

## D. Build / packaging health

- **PAR-B01 Framework-dependent scripts vs self-contained release.**
  - `build.sh:28` and CI (`.github/workflows/linux-build.yml:18`) publish with `--self-contained false`, so the build needs the host .NET 8 runtime.
  - The released AppImage *is* self-contained: it bundles `libcoreclr.so` and `RHI.runtimeconfig.json` lists `includedFrameworks` Microsoft.NETCore.App 8.0.31. It is launched by a minimal `AppRun` that sets `PATH`/`LD_LIBRARY_PATH`.
  - **No AppImage tooling exists in the repo** (grep for appimage/appimagetool/linuxdeploy found nothing outside `.scratch`). The release artefact cannot be reproduced from source.
- **PAR-B02 7-Zip dependency.** The AppImage does not bundle 7z/bsdtar. ReShade extraction needs host `7z`/`7zz`/`7za` or `bsdtar` (`ReShadeArchiveExtractor.cs:34,76-77`). README lists `7zip` only in the from-source instructions.
- **PAR-B03 Tests in CI.** Yes, indirectly. CI runs `bash ./validate.sh` (`linux-build.yml:14`), which builds Release and runs `dotnet test` on `tests/RHI.Core.Tests` (`validate.sh:132,148`). `build.sh:27` also runs the tests.
- **PAR-B04 Hard-coded test inventory.** `validate.sh:8-10,85-92` fails unless exactly 604 `[Fact]` + 137 `[InlineData]` = 741 exist. It also hard-codes the version string and ~20 literal source strings (`:53-80`). Any added test or a benign refactor breaks CI. This makes contributions awkward: every PR must edit `validate.sh`.
- **PAR-B05 .NET pinning.**
  - There is no `global.json`, and CI uses `dotnet-version: '8.0.x'` (`linux-build.yml:12`).
  - The SDK feature band floats. `validate.sh:136-141` already tolerates Avalonia 12.1.2 analyser warning CS9057 because of a Roslyn mismatch with 8.0.1xx.
  - `TreatWarningsAsErrors=false` (`Directory.Build.props:6`), but `validate.sh` enforces zero warnings by grep.
  - Package versions are pinned (Avalonia 12.1.2, CommunityToolkit.Mvvm 8.4.0, HtmlAgilityPack 1.11.71, xunit 2.9.3). There is no lock file (`packages.lock.json` absent).
- **PAR-B06 CI hygiene.** Actions are tag-pinned (`@v4`), not SHA-pinned. The workflow has no `permissions:` block. It runs on every push and PR, uploads the framework-dependent `artifacts/linux-x64/` (not the AppImage), and has no release job, test-result upload or cache.
- **PAR-B07 Installer.** `install-user.sh:15` runs `rm -rf "$DEST"` on `~/.local/opt/rhi-linux` before copying. That is fine for the fixed path, but a user's local changes there are silently lost. It installs a `.desktop` file (`packaging/rhi-linux.desktop`, `Exec=rhi-linux`) that relies on `~/.local/bin` being on `PATH`. It does not run `update-desktop-database` or refresh the icon cache.
- **PAR-B08 validate.sh side effects.** It deletes all `bin/`/`obj/` under `src`/`tests` and `artifacts/` (`validate.sh:40-44`) before building. That is acceptable in CI but destructive in a developer tree.
- **PAR-B09 Single test project.** All 741 tests live in `tests/RHI.Core.Tests`, which references Desktop, Platform and Core. There are no UI/headless tests and no integration run against a real Wine prefix in CI.

---

## Verdict

1. Functional parity with upstream 2.7.5 is broadly credible. Every major installer family is implemented, and NVIDIA/DXVK exclusions follow a stated policy.
2. The fork is already four releases behind (2.7.6–2.7.9, plus a 2.8.0 beta). Upstream ships every 1–3 days, and one manifest key rename (`engineOverrides`) already silently desynchronises it.
3. Project health is thin: one maintainer, 10 commits, two push bursts, no pushes since 2026-09-21, one pre-release built locally, and no reproducible AppImage pipeline.
4. Contributing: PRs to `wizardkof/RHI-Linux` are enabled but have never been exercised, and Issues/Discussions are off. Upstream accepts issues/PRs but is weighing a different Linux port (PR #116) and previously endorsed Restall.
5. The CI builds and tests, but `validate.sh`'s hard-coded test counts and source literals make external PRs brittle. Pin the SDK (`global.json`) and add an AppImage job before relying on outside contributions.
