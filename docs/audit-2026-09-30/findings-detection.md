# RHI-Linux audit — detection, library and startup path

Commit `3aa0f21` (detached worktree). Read-only review; no files under `src/` or `tests/` were changed.
All paths are relative to `/mnt/vdo_storage/worktrees/rhi-audit/`.

**How I checked the claims.** Every finding comes from reading the code. The ones marked **[repro]** were also run against the real detectors, either on this machine's own Steam/Lutris/Wine data (read-only) or on throwaway fixtures under `.scratch/audit/`. The harness is in `.scratch/audit/harness/` and the raw output is in `run1.txt` and `run2.txt`. The upstream manifest was loaded from a copy of `~/.cache/rhi-linux/upstream-manifest.json`, with the live URL deliberately unreachable. `dotnet test tests/RHI.Core.Tests`: 746/746 pass.

**Baseline on this machine.** A full scan takes 1.5 s and ends at 74–94 MB RSS. The Steam detector returns 29 raw entries for 14 distinct apps, which merge down to 20 cards (19 after policy).

---

## Critical

### DET-01 — Upstream blacklist is matched against install paths, so whole libraries silently disappear
- **Where:** `src/RHI.Platform.Linux/Detection/UpstreamGameLibraryPolicyService.cs:29-32, 166-198`; `src/RHI.Platform.Linux/Deployment/UpstreamRhiManifestPolicy.cs:240`.
- **What happens:**
  - `KnownGameNames` includes every `blacklist` entry. The live list contains `"Windows"`, `"Blender"`, `"Unreal Engine"`, `"Steamworks Common Redistributables"`, `"Wallpaper Engine"` and others.
  - When a game's title is not an exact or normalised manifest match (most games), `ResolveCanonicalName` falls back to a whole-word search of `InstallPath`, `BinaryDirectory` and `ExecutablePath`.
  - A unique hit becomes the canonical name, and `IsBlacklisted(canonical)` then drops the game.
- **[repro]** A Steam game at `/run/media/nope/Windows/SteamLibrary/...`, `/mnt/windows/SteamLibrary/...` or `~/Games/Nolvus Awakening Skyrim Windows/...` is dropped (`kept=0`). The same game at `/run/media/nope/Data/...` is kept. A manual game whose exe sits under `.../Blender/` is dropped, as is one under `~/Unreal Engine/Projects/...`.
- **Why it matters:** Dual-boot users very commonly mount their NTFS Steam library at `/mnt/windows` or under a partition labelled `Windows`. Every unlisted game on that drive vanishes, with no message. The same logic also removed Lutris `battle.net`, because its exe path contains `Battle.net.exe`.
- **Fix seam:** Apply the blacklist to the title only (upstream semantics). Use path identity only to pick metadata, and only after excluding blacklist-only keys and matching on the game's own folder segments, never the mount prefix.
- **Verify:** Add a policy test with a Steam game under `/mnt/Windows/SteamLibrary/steamapps/common/Foo` and assert it is kept.

## High

### DET-02 — Partial rescans (added in HEAD) throw away runtime enrichment, but the stale probe cache is still used
- **Where:**
  - `src/RHI.Core/Services/GameLibraryScanner.cs:41-72`: the reused raw snapshots are pristine clones.
  - `src/RHI.Desktop/ViewModels/MainWindowViewModel.cs:1117`: the cache is cleared only on a full refresh.
  - `MainWindowViewModel.cs:1000-1006`: a cache hit skips the probe.
  - Call sites: `MainWindowViewModel.cs:2529, 2544, 2573, 2591`.
- **What happens:**
  - After an add or remove of a manual game, or a save or reset of an edit, every game object is rebuilt from the raw detector output.
  - As a result `BinaryDirectory`, `GraphicsApi`, `EngineHint` and a re-selected `ExecutablePath` revert to their detector values; `BinaryDirectory` becomes `null`.
  - `_probeCache` still holds those games, so re-selecting one applies the cached components and never re-probes.
- **[repro]** Borderlands 3 after probe: `bin=.../Win64 api=DirectX12`. After `ScanAsync({"Manual"})`: `bin=<null> api=Unknown`, same library key.
- **Why it matters:**
  - Installs then fail: `ReShadeDeploymentService.InstallAsync` returns "Game binary directory is not known yet" at `ReShadeDeploymentService.cs:99-100`.
  - The API proxy choice uses `EffectiveGraphicsApi == Unknown`.
  - The Unreal/Unity filters are wrong until the next full refresh.
  - This is a regression introduced by HEAD; before it, `_probeCache.Clear()` always forced a re-probe.
- **Fix seam:** Store the derived runtime fields (`BinaryDirectory`, `ExecutablePath`, API fields, `EngineHint`) in `ProbeCacheEntry` and re-apply them on a cache hit. Alternatively, drop cache entries for every game whose instance was replaced.
- **Verify:** VM-level test: probe game A, add a manual game, re-select A, then assert `A.BinaryDirectory` is non-null and an install succeeds.

### DET-03 — Startup library scan waits behind an untimed DLSS manifest fetch (up to 5 min)
- **Where:** `src/RHI.Desktop/ViewModels/MainWindowViewModel.cs:823, 841`; `src/RHI.Core/Services/DlssManagerService.cs:40-47`; `src/RHI.Desktop/App.axaml.cs:36`.
- **What happens:**
  - `InitializeAsync` awaits `_dlss.FetchManifestAsync()` before calling `RefreshAsync()`.
  - That request has no per-call deadline; the shared `HttpClient.Timeout` is 5 minutes.
  - The upstream manifest (15 s, `UpstreamRhiManifestService.cs:640`) and PCGamingWiki (5–8 s) have deadlines; this call does not.
- **Why it matters:** On a black-holed network the app shows an empty library for up to 5 minutes with `IsBusy` set. Examples are a VPN kill-switch (this host runs Mullvad `wg0`) or a captive portal. A DNS failure fails fast; a black hole does not.
- **Fix seam:** Start the scan in parallel with the manifest fetch, or put a `CancelAfter(10-15 s)` around it and fall back to the cached copy (the cache path already exists).
- **Verify:** Test with a stub `HttpMessageHandler` that never completes; assert `ScanAsync` is called within about 1 s of `InitializeAsync`.

### DET-04 — Every Lutris launch command targets a non-existent slug
- **Where:** `src/RHI.Platform.Linux/Detection/LutrisGameDetector.cs:43, 73`.
- **What happens:** The command is built as `lutris lutris:rungame/<config filename stem>`, for example `battlenet-1763437958`. Lutris resolves `rungame/<x>` with `get_game_by_field(x, "slug")` (`/usr/lib/python3.14/site-packages/lutris/gui/application.py:688-691`). The slug in `pga.db` is `battlenet`; the stem is stored in `configpath`.
- **[repro]** `pga.db`: `battlenet|battle.net|battlenet-1763437958`. No game has slug `battlenet-1763437958`, so Lutris logs "No game found in library" and exits, and RHI still reports "Launching".
- **Fix seam:** Read `pga.db` (the `id` and `configpath` columns) and emit `lutris lutris:rungameid/<id>`. `game_slug` from the yml is ambiguous when two installs share it (true here).
- **Verify:** Unit test on a yml plus a fixture `pga.db`; assert the command uses the numeric id.

### DET-05 — Lutris native-wrapper games take the wrapper's folder as install path, and distinct games merge into one card
- **Where:** `src/RHI.Platform.Linux/Detection/LutrisGameDetector.cs:33-39`; `src/RHI.Core/Services/GameLibraryScanner.cs:96-107, 109-164`.
- **What happens:**
  - The install path is always `dirname(exe)`, even for `runner: linux` and even when `working_dir` is set.
  - Wrapper scripts in `~/.local/bin` therefore make `~/.local/bin` the game root.
  - Non-Steam games are keyed only by `path:<InstallPath>`, so two such games collapse and `Merge` keeps the first.
- **[repro]** Blue Prince and Rekordbox both get `InstallPath=/home/nope/.local/bin`. The merged library shows "Blue Prince" only; Rekordbox is gone. Blue Prince's real root, `working_dir: ~/Games/blue-prince-xodus`, is ignored.
- **Why it matters:** This is a silent false negative plus wrong patch targets: RHI would deploy DLLs into `~/.local/bin`. It is also a sibling of the prefix-root bug fixed in HEAD, because any generic folder can become a "game".
- **Fix seam:**
  - Prefer `working_dir` when the exe is outside it, or when the runner is `linux` with a non-PE exe.
  - Include `source + slug/appName` in `GameKey` for non-Steam entries, and only merge across sources when the paths are equal.
- **Verify:** Lutris test with two yml files whose exes share one parent directory; assert two games come back and each has its `working_dir` root.

### DET-06 — Manifest `installPathOverrides` and `splitGames` can move `InstallPath` outside the game
- **Where:** `src/RHI.Platform.Linux/Detection/UpstreamGameLibraryPolicyService.cs:34-53, 72-81, 223-243`.
- **What happens:**
  - Candidates are resolved with `Path.GetFullPath(Path.Combine(root, relative))` with no containment check.
  - The live manifest has `"Hades II": "Ship|..\\.."`. If `Ship` is missing, `..\..` wins.
  - For a Steam install that resolves to `steamapps/`; for a Heroic sideload rooted at `…/Hades II/Content` it resolves to the library parent.
- **[repro]** A fixture at `fx/lib/Hades II/Content` resolved to `fx/lib`.
- **Why it matters:** This is the same bug class HEAD fixed, a launcher root being fed to `GameFileIndexer`. It indexes every game in the library (up to the 200k-file cap) and makes the entire library the patch root.
- **Fix seam:** Reject any resolved candidate that is an ancestor of `steamapps/common`, a library root, a Wine prefix root, `$HOME` or `/`. Cap `..` climbing at the original root's parent.
- **Verify:** Policy test: Hades II fixture without `Ship`; assert `InstallPath` stays at the original root.

### DET-07 — HDR launch wraps `xdg-open` for Heroic games and reports HDR as applied
- **Where:** `src/RHI.Platform.Linux/Hdr/LinuxHdrLaunchProfileService.cs:198-209`; `src/RHI.Platform.Linux/Detection/HeroicGameDetector.cs:174, 181`; `src/RHI.Desktop/ViewModels/MainWindowViewModel.cs:1276-1287`.
- **What happens:** `IsLauncherMediated` checks for `Source == "Heroic"` exactly. The detector emits `Heroic/Epic`, `Heroic/GOG`, `Heroic/Amazon` and `Heroic/Sideload`, and the command starts with `xdg-open`, not `heroic `.
- **Why it matters:** For a Heroic game, the gamescope/HDR environment wraps `xdg-open heroic://…`, which exits at once. HDR never reaches the game, yet the status says "Launching … with HDR profile".
- **Fix seam:** Use `StartsWith("Heroic", OrdinalIgnoreCase)`, and also treat `xdg-open` with a `heroic://`, `steam://` or `lutris:` URI as launcher-mediated.
- **Verify:** Extend `HdrLinuxIntegrationTests` with `Source="Heroic/Epic"`; assert `IsLauncherMediated` is true.

## Medium

### DET-08 — Steam roots and libraries are de-duplicated by string, so each library is walked 2–3 times
- **Where:** `src/RHI.Platform.Linux/Paths/LinuxPaths.cs:13-23`; `src/RHI.Platform.Linux/Detection/SteamGameDetector.cs:18-20, 91-105`.
- **What happens:**
  - `~/.steam/steam` is a symlink to `~/.local/share/Steam`, but `Distinct(StringComparer.Ordinal)` keeps both.
  - `FindLibraries` seeds the set with the root string and then adds the vdf's canonical path.
- **[repro]** 29 raw Steam entries for 14 apps. SteamVR is walked three times; every `/media/4TBssd` game twice. Each walk is a full `GameFileIndexer.Build` (8 levels deep, up to 200k files) plus a PE and size read per `.exe`.
- **Why it matters:** Startup cost is two to three times higher than needed, on HDD libraries too. Merge hides the duplicates, so nobody notices.
- **Fix seam:** Canonicalise roots and library paths with `new DirectoryInfo(p).ResolveLinkTarget(true)` (or realpath), and de-duplicate on the canonical path plus inode.
- **Verify:** Test with a fake `$HOME` that has `.steam/steam -> .local/share/Steam`; assert each appmanifest is parsed once.

### DET-09 — Scan-time indexes are thrown away, and several walks ignore cancellation
- **Where:**
  - `SteamGameDetector.cs:40`: index built per game, then discarded.
  - `WinePrefixDetector.cs:48`, `LutrisGameDetector.cs:53`, `HeroicGameDetector.cs:168`: `FindLikelyExecutable(install)` builds a new index and passes no token.
  - `DetectionHelpers.cs:102, 164`: the fallback builds an index.
  - `MainWindowViewModel.cs:1038-1040`: the selection probe re-indexes.
- **What happens:** Each full refresh walks every installed game's tree once or more, then walks it again on selection. `RefreshAsync` (`MainWindowViewModel.cs:1120`) passes no token to `ScanAsync`, so a running scan cannot be cancelled.
- **Why it matters:** Startup time grows linearly with library size and tree size, with no way out: cold-cache HDD libraries with 100+ games will take minutes.
- **Fix seam:**
  - Choose the exe from a shallow, bounded search (depth 3, `.exe` only) at scan time, and defer the full index to selection.
  - Thread the scan `CancellationToken` through `FindLikelyExecutable`.
  - Give `RefreshAsync` a cancellable CTS.
- **Verify:** Count `GameFileIndexer.Build` calls per full scan with an injected counter; target 0 full-depth builds.

### DET-10 — Lutris YAML parsing: folded/multi-line scalars truncate `exe`, and names fall back to a timestamped config stem
- **Where:** `src/RHI.Platform.Linux/Detection/LutrisGameDetector.cs:26-29, 162-167`.
- **What happens:** `MatchValue` reads one line and stops at `"` or `'`. Lutris wraps long values, for example `exe: …/Battle.net/Battle.net` followed by `    Launcher.exe` on the next line. `name` is not in the yml at all (it lives in `pga.db`).
- **[repro]**
  - `battlenet-1763437958.yml`: the exe is truncated to `…/Battle.net/Battle.net`, so the heuristic picks `Battle.net.exe`, not the configured `Battle.net Launcher.exe`.
  - `ablechum12-1775064156.yml` is shown as "ablechum12 1775064156".
  - `MatchValue` also matches the first indented `exe:`/`prefix:` anywhere in the file, including under `script:`.
- **Why it matters:** Wrong launch/patch executable and ugly names. Neither is caught by tests.
- **Fix seam:** Parse the YAML properly (YamlDotNet is a small dependency) and read the `game:` mapping only. Take the name from `pga.db` by `configpath`.
- **Verify:** Lutris test with a wrapped `exe:` and a nested `script.game.exe`; assert the top-level value is used.

### DET-11 — WinePrefixDetector double-lists Lutris/umu prefixes, uses the wrong granularity, and launches Proton prefixes with system `wine`
- **Status:** fixed in `161634c` (#50). Lutris-config and umu prefixes are skipped (canonical paths); vendor folders give way to their products and `games/` libraries; `drive_c/Games` is scanned; Proton-built prefixes launch with their own `bin/wine` or not at all. Games inside Lutris-owned launcher prefixes are not listed by any detector yet (see #50).
- **Where:** `src/RHI.Platform.Linux/Detection/WinePrefixDetector.cs:17-22, 32, 36-48, 59`; `DetectionHelpers.cs:81-86`.
- **What happens:**
  - `IsLauncherManagedPrefix` skips only Heroic prefixes and `compatdata`. Lutris prefixes (`~/Games/<slug>`) and `~/Games/umu/*` are re-scanned.
  - Each top-level folder under `Program Files*` counts as one "game", so vendor folders (Ubisoft, EA, Microsoft) become the game.
  - `drive_c/Games` (Battle.net's default library) is never searched.
  - The launch command runs the host `wine`, whatever built the prefix.
- **[repro]**
  - Wine "Ubisoft" appears with exe `UbisoftExtension.exe`, as a duplicate of Lutris "Ubisoft Connect".
  - Prince of Persia: The Lost Crown (`…/Ubisoft Game Launcher/games/…`) and The Outer Worlds 2 / Blue Prince (`battlenet/drive_c/Games/…`) are not detected.
  - Bome MIDI Translator is listed as a game.
- **Why it matters:**
  - False positives and false negatives in the same pass.
  - Launching a GE-Proton prefix (`version: GE-Proton10-32`) with system Wine triggers a `wineboot` prefix update and can break the Proton prefix.
- **Fix seam:**
  - Skip prefixes referenced by any Lutris yml or a `lutris.json` marker, as well as `umu` prefixes.
  - Descend at most two levels under vendor directories; include `drive_c/Games`.
  - Read the `version` file and `config_info` to choose the runner, or launch via the owning launcher.
- **Verify:** Fixture prefix with `Program Files (x86)/Vendor/Launcher/games/Foo/Foo.exe` and a `lutris.json`; assert it is skipped, and that Foo is found when unmanaged.

### DET-12 — Heroic fuzzy prefix matching can attach the wrong Wine prefix
- **Where:** `src/RHI.Platform.Linux/Detection/HeroicGameDetector.cs:47-48, 347-361, 391-398`.
- **What happens:**
  - `Similarity` uses `a.Intersect(b).Count()`, i.e. distinct characters, with a 0.70 threshold.
  - Computed scores: "Hades" vs "Heads" = 1.0, "Limbo" vs "Bimbo" = 0.8, "Tunic" vs "Unity" = 0.8, "Hades II" vs "Hades" = 0.71.
  - One prefix directory can be assigned to several games, and native Linux Heroic games also get a prefix.
- **Why it matters:** `PrefixPath` drives `WineDllOverrideService` registry writes (for example `Dlssg2030UnlockDeploymentService.cs:214`). A wrong match writes DLL overrides into another game's prefix.
- **Fix seam:** Keep the exact `GamesConfig` match. For the fallback, require a normalised exact match, or an edit-distance ratio of 0.9 or more, and assign each prefix at most once. Never attach a prefix to games whose platform is Linux.
- **Verify:** Unit test with prefixes `Heads` and `Hades II` and a game "Hades"; assert no wrong match.

### DET-13 — Runtime-log attribution matches on the full log path, so a launcher inherits another game's renderer at High confidence
- **Where:** `src/RHI.Platform.Linux/Detection/GraphicsApiDetector.cs:478-483, 509-514`.
- **What happens:** `RuntimeLogMatchesGame` normalises the entire log path, including the prefix directory name, and checks whether it contains the root or exe token.
- **[repro]** "Ubisoft Connect" (exe token `ubisoftconnect`, prefix `~/Games/ubisoft-connect`) and Wine "Ubisoft" both report `DirectX11 / High / Runtime renderer log: Player.log`. The log actually belongs to Prince of Persia.
- **Why it matters:** High-confidence evidence overrides every other signal, and it chooses ReShade's proxy DLL. The existing test (`NativeRuntimeDetectionTests.cs:78`) uses an unrelated prefix name, so it misses this.
- **Fix seam:** Match only the path segments below `AppData/LocalLow/`, i.e. the company and product names.
- **Verify:** A copy of the existing test with the prefix directory named after the game; assert no runtime-log evidence.

### DET-14 — An executable containing several API strings is reported as the highest API at High confidence
- **Where:** `GraphicsApiDetector.cs:174-183, 193-202`, compared with the ambiguity rule at `233-237`.
- **What happens:** In the quick and deep exe passes, any number of matching APIs becomes `Order(...)[0]` with `High`. The nearby-binary pass correctly treats 3 or more as ambiguous.
- **[repro]**
  - RDR2: `d3d12, d3d11, d3d9, vulkan-1` → DX12 High. Its default renderer is Vulkan.
  - Borderlands 3 and Tiny Tina's Wonderlands: all five APIs → DX12 High.
- **Why it matters:** DX12 maps to proxy `dxgi.dll` (`ReShadeDeploymentService.cs:942-948`). ReShade will not load in a Vulkan-configured RDR2.
- **Fix seam:** Apply the same 3-or-more ambiguity rule to exe signatures (Medium confidence, keep the candidate list), and let the config and prefix settings pass decide. RDR2's `system.xml` sits in the prefix under `Documents/Rockstar Games/...`.
- **Verify:** Detector test: exe containing all five API strings; assert confidence below High.

### DET-15 — PCGamingWiki fallback overrides native Linux results, caches no misses, and blocks selection
- **Where:** `src/RHI.Platform.Linux/Detection/LinuxGameRuntimeProbe.cs:58-82`; `PcGamingWikiGraphicsApiService.cs:57-79, 23-25`.
- **What happens:**
  - The fallback runs whenever `GraphicsApi == Unknown`, including native Linux games whose result is "ambiguous Vulkan/OpenGL". The Windows API table then replaces the native candidate list.
  - Only positive results are cached, so a miss or network error re-queries on every selection: 5 s opensearch plus 8 s page, under `IsSelectionProbeBusy`.
  - The cache lives in `~/.local/share/RHI/`, outside `rhi-linux`, so `PurgeCache` does not clear it.
- **[repro]** Broken Sword 2 (native) returns `Unknown, Medium, Vulkan+OpenGL`, which qualifies it for the PCGW override.
- **Fix seam:**
  - Skip PCGW when `Runner == "Native Linux"` or when candidates are already non-empty.
  - Cache misses with a TTL.
  - Move the cache to `LinuxPaths.CacheHome/rhi-linux`.
- **Verify:** Runtime-probe test with a native-FNA fixture and a stub PCGW; assert zero HTTP calls.

### DET-16 — `GameLibraryScanner.Merge` is lossy, and the merge key is not canonical
- **Where:** `src/RHI.Core/Services/GameLibraryScanner.cs:90, 96-107, 109-164`.
- **What happens:**
  - The first candidate, in detector order, wins, and only null-valued fields are filled from the others.
  - Never merged: `IsManuallyAdded`, `UserGraphicsApiOverride`, `HeroicAppName`, `EpicAppName`, `BinaryDirectory`, `GraphicsApis`, `DofFix*`, `EngineIniFileOverride`, `RequiresExplicitPatchTarget`.
  - A manual entry that duplicates a Heroic or Lutris path loses `IsManuallyAdded`, so `CanRemoveManualGame` is false and the entry can never be removed from the UI. Its user-chosen exe is also dropped when the other entry has one.
  - The key uses `Path.GetFullPath`, which does not resolve symlinks and keeps trailing `/` (verified: `/a/b/` stays `/a/b/`). Grouping is also case-insensitive on a case-sensitive filesystem.
  - `~/Games/X` and `/media/4TBssd/Games/X` are the same directory on this host, because `~/Games` is a symlink, yet they are treated as different.
- **Fix seam:** Canonicalise the key (resolve symlinks, trim trailing separators, compare ordinally). Merge an explicit list of "sticky" fields with OR/union semantics, and let manual entries win for user-authored fields.
- **Verify:** Scanner test: a Manual and a Heroic entry at the same path, one of them with a trailing slash; assert one card with `IsManuallyAdded` true and the manual exe kept.

### DET-17 — Detector failures are swallowed and the scan still reports success
- **Where:**
  - `GameLibraryScanner.cs:55-58`: a detector throwing yields `[]`.
  - `SteamGameDetector.cs:25`: `EnumerateFiles` sits outside the per-ACF `try` at `:28`, so one unreadable library aborts the whole Steam detector.
  - `SteamGameDetector.cs:77` and `LutrisGameDetector.cs:77`: `catch { }` per entry.
  - `MainWindowViewModel.cs:1138`: "Scan complete — N games".
  - `src/RHI.Desktop/Program.cs:7-12`: only `LogToTrace`; no file log and no unhandled or unobserved exception hooks.
- **Why it matters:** An EACCES or EIO on one mount looks like "Steam has no games". Support cannot diagnose anything after the fact.
- **Fix seam:** Return per-detector status (`ok`, `failed:<message>`, `count`) from `ScanAsync`, show it in the status line, and write swallowed exceptions to an XDG state log (`~/.local/state/rhi-linux/rhi.log`).
- **Verify:** Scanner test with a throwing detector; assert a failure status is surfaced.

### DET-18 — Corrupt stores fail open to empty state, and the next save overwrites the user's data
- **Where:** `src/RHI.Platform.Linux/Settings/LinuxGameLibraryStore.cs:143-155, 157-165`; `src/RHI.Platform.Linux/Paths/GraphicsApiOverrideStore.cs:49-62`; the same pattern in `LinuxDeploymentRecordStore.cs:74` (the records used by every Paths store).
- **What happens:** Any exception, including a transient read error or a truncated file, returns a blank state. The next `Save` then replaces the file: a filter change, a favourite, an API override or an install.
- **Why it matters:** Silent loss of manual games, edits, favourites and hidden flags. For deployment records it also orphans deployed files, which RHI can then neither update nor remove. The existing test asserts the empty state but not that the original file is kept.
- **Fix seam:** On a parse failure, move the file aside to `*.corrupt-<ts>` and refuse to overwrite it without a backup. Distinguish "missing" from "unreadable".
- **Verify:** Write garbage, `Load`, `Save(new)`, then assert a `.corrupt-*` backup exists with the original bytes.

### DET-19 — Removing or adding manual games can silently do nothing
- **Where:**
  - `src/RHI.Desktop/ViewModels/MainWindowViewModel.cs:2583-2589`: remove matches `selected.InstallPath`, which edits (`LinuxGameIdentityService.cs:48`) and the manifest (`UpstreamGameLibraryPolicyService.cs:79`) rewrite.
  - `MainWindowViewModel.cs:1108`: `RefreshAsync` returns immediately when `IsBusy`, for example during the startup DLSS fetch or a background update pass.
- **What happens:**
  - Removing a manual game whose root was edited, or overridden by the manifest, reports "removed" but the entry stays in `game-library.json`.
  - `AddManualGameAsync` saves the entry, then `RefreshAsync` is a no-op, `SelectedGame` becomes `null`, and the success message is still shown.
- **Fix seam:** Match on `LibraryIdentityPath`, or better, on a stable manual-entry id. Queue the refresh, or await the busy gate, instead of returning.
- **Verify:** VM test: set an edit override on a manual game, remove it, then assert the store has no entry.

### DET-20 — Manual (and Heroic) entries accept a prefix root, `$HOME` or `/` as the game root (sibling of the HEAD fix)
- **Where:** `src/RHI.Platform.Linux/Detection/ManualGameDetector.cs:21-22`; `MainWindowViewModel.cs:2553`; `HeroicGameDetector.cs:143-161`; `GameFileIndexer.cs:46`.
- **What happens:**
  - Only `Directory.Exists` is checked. There is no `IsWinePrefixRoot` check and no home, filesystem-root or `steamapps/common` guard (HEAD added one for Lutris only).
  - Choosing `~/Games/battlenet` as the root indexes `drive_c/windows`; choosing `$HOME` indexes 200k files.
  - Truncation at 200k is silent: the status shows "200000 files indexed" with no warning, and which files get dropped depends on the DFS order.
- **Fix seam:** Move the guard into one shared `GameRootGuard.IsAcceptable(path)` used by every detector and by the Edit/Add dialogs. Have `GameFileIndexer.Build` return a `Truncated` flag.
- **Verify:** Test: `AddManualGameAsync` with a prefix root returns an error; `Build` on a tree above the cap reports truncated.

### DET-21 — `_probeCache` holds full file lists for every game ever selected, with no eviction
- **Where:** `MainWindowViewModel.cs:44-47, 70, 1059`.
- **What happens:** Each entry keeps up to 200k absolute-path strings (tens of MB per large game) for the life of the process. Only a full refresh clears the cache.
- **Fix seam:** LRU of 3–5 entries, or keep only the derived results and re-index on demand for Update All.
- **Verify:** Browse 20 large games and watch RSS in the test harness.

### DET-22 — Every refresh, including the "6 ms" partial one, invalidates and re-downloads the upstream manifest
- **Where:** `UpstreamGameLibraryPolicyService.cs:21-22`; `GameLibraryScanner.cs:74-86` (policies run on partial scans too); `UpstreamRhiManifestService.cs:585, 640`.
- **What happens:** On a black-holed network each add, remove or edit waits up to 15 s before falling back to the last-known-good copy. The HEAD claim of 6 ms holds only on a fast network.
- **Fix seam:** Skip `InvalidateProcessCache()` on partial scans. Add a TTL, or `If-None-Match`/ETag, to the live fetch.
- **Verify:** Stub handler that delays 20 s; the partial scan must complete in under 100 ms.

### DET-23 — Path-identity canonicalisation assigns other titles' policies (sibling of DET-01)
- **Where:** `UpstreamGameLibraryPolicyService.cs:181-197`; `UpstreamRhiManifestService.cs:521-546`.
- **What happens:**
  - **[repro]** "Ship of Harkinian" at `~/Games/Hades/shipwright` gets `canon=Hades`.
  - It then inherits Hades' `installPathOverrides`, `ForcedArchitecture`, engine hints and DLL-name overrides. `ForcedArchitecture` also makes `ApplyRuntimeMetadata` re-select a different exe (`DetectionHelpers.cs:306-315`).
- **Fix seam:** Require the canonical phrase to equal a whole path segment (the game folder or exe stem), not any substring of the full path. Ignore segments at or above the library root.
- **Verify:** Policy test with the case above; assert the canonical name is unchanged.

### DET-24 — Synchronous JSON reads on the UI thread, N+1 per property evaluation
- **Where:** `MainWindowViewModel.cs:169`, where `CanUpdateAll` calls `_updateAll.HasManagedTargets(_allGames)`, which does `FindAll` per game (`LinuxUpdateAllService.cs:143-144`), and each call deserialises the whole records file (`LinuxDeploymentRecordStore.cs:29-33, 59-64`).
- **What happens:**
  - This is re-evaluated on every `IsBusy` change (`:851-865`) and after each refresh (`:1147`).
  - `OnSelectedGameChanged` (`:990-998`) does 8 or more synchronous store reads: the API override, OptiScaler variant, Luma mode, HDR, ReShade (twice), UAL, ShortFuse (twice) and version pins.
  - The unconfirmed-target branch runs `ComponentProbe.Probe` on the UI thread (`:1025`).
  - `PrepareUpdateIndexesAsync` (`:1191`) does one records read per game, sequentially.
- **Fix seam:** Load the records once per refresh into an in-memory map with a write-through cache, and compute `CanUpdateAll` from that snapshot.
- **Verify:** Count `File.ReadAllText` calls per selection change with a test file system; target 1 or fewer.

## Low

- **DET-25 — Other recursive enumerations still follow symlinks.**
  - `HeroicGameDetector.cs:95` (`AllDirectories`; capped by `Take(500)` but still traverses), `DlssManagerService.cs:398` (a constructor sweep on the UI thread at startup) and `LinuxAppSettings.cs:131` (`PurgeCache` counts every file, then deletes).
  - Bounded but still symlink-following: `GraphicsApiDetector.cs:627` (`CollectNamedFiles`) and `WinePrefixDetector.cs:89` (`FindPrefixes`, where `~/Games` is itself a symlink on this host).
  - **[repro]** `.NET EnumerateFiles(AllDirectories)` followed `t/a/root -> /` into `/lib` (`.scratch/audit/symh`).
  - Fix: use `EnumerationOptions { AttributesToSkip = ReparsePoint, RecurseSubdirectories = true }` everywhere.
- **DET-26 — `FindLikelyExecutable` hard-ignores by substring** (`DetectionHelpers.cs:95-111`).
  - The names `eac`, `crash`, `install`, `report` and `setup` are matched anywhere, so `Beacon.exe`, `PeaceKeeper.exe` and `CrashBandicoot*.exe` are dropped and a worse candidate or `null` wins.
  - Fix: match on token boundaries (for example `^eac` or `[_-]eac`, and `crashhandler|crashreport`).
- **DET-27 — Steam classification is shallow** (`SteamGameDetector.cs:31-61`).
  - `StateFlags` is ignored, so partially installed or updating apps are listed.
  - Tool-like apps with their own install dir, such as `Skyrim Script Extender (SKSE)` (appid 365720), become games.
  - The existence of `compatdata/<id>/pfx` decides "Proton" versus native: Broken Sword 1 has a stale prefix, so the native `BS1DC` script is never considered and the exe is `null`.
  - Fix: read `CompatToolMapping` from `config/config.vdf` for the real per-app tool, and honour `StateFlags & 4`.
- **DET-28 — Lutris path expansion is partial** (`LutrisGameDetector.cs:171-176, 188-189`).
  - `Environment.ExpandEnvironmentVariables` expands `%VAR%` only; `$HOME` and `${VAR}` stay literal (verified). A bare `~` is not handled, and `$GAMEDIR` is stripped only as a leading `$GAMEDIR/` token.
  - `pga.db` `installed=0` and `directory` are ignored, so leftover yml files for uninstalled games appear.
- **DET-29 — Refresh UX and cancellation** (`MainWindowViewModel.cs:1108, 1161, 1242-1260`).
  - A Refresh click while a background update pass holds `IsBusy` is silently ignored.
  - Manual Update All uses `CancellationToken.None`.
  - The fire-and-forget selection probe mutates the shared `DetectedGame` from a pool thread while the UI and Update All read it. A cancelled probe can leave `ExecutablePath`/`BinaryDirectory` updated but the API fields stale (`DetectionHelpers.cs:309-349`).
- **DET-30 — Manual UE inference stops at `<Project>`** (`ManualGamePathInference.cs:16-22`).
  - `<Game>/<Project>/Binaries/Win64/X.exe` gives `<Game>/<Project>`, so `<Game>/Engine/**` (a common location of `nvngx_dlss.dll` and Streamline) is never indexed, and DLSS swap misses it.
  - Fix: when a sibling `Engine/` exists next to `<Project>`, return `<Game>`.
- **DET-31 — Launcher assumptions** (`LinuxGameLauncher.cs:19-31`; `SteamGameDetector.cs:73`).
  - `steam -applaunch` assumes a host `steam` binary, which is wrong for Flatpak Steam.
  - The `Process` is neither awaited nor disposed, and the exit code and stderr are dropped, so launch failures always look like success.
  - `sh -lc` sources login profiles on every launch.

## Gaps (expected by a typical Linux gamer; lower priority)

- **Non-Steam shortcuts.** `userdata/*/config/shortcuts.vdf` is not read; this host has one. Heroic, Lutris and umu games added to Steam show only via their own launcher, or not at all.
- **Steam install variants.** Snap (`~/snap/steam/common/.local/share/Steam`), `~/.steam/debian-installation` and Flatpak `…/com.valvesoftware.Steam/data/Steam` are not probed. Lutris probes all three.
- **Per-game Proton version.** `config.vdf` `CompatToolMapping` is never used, so the runner and HDR family for Steam games are guessed.
- **Bottles.** Only native `~/.local/share/bottles/bottles` is scanned, not Flatpak (`~/.var/app/com.usebottles.bottles/data/bottles/bottles`). `bottle.yml` is not read for program entries or runner, so bottle programs are guessed from `Program Files`.
- **umu-launcher.** `~/Games/umu/*` prefixes are scanned as bare Wine with no runner, and `umu-run` launch is unsupported. Faugus Launcher (umu-based) is not detected.
- **Lutris.** `pga.db` is not used as the authority for names, ids, installed state, service (GOG/Epic/Steam via Lutris services) or the playtime ordering users recognise.
- **Other launchers.** Minigalaxy (GOG), itch.io app (`~/.config/itch`), Rare / standalone Legendary without Heroic, GameHub, Playnite-on-Wine and Battle.net/EA/Ubisoft libraries inside launcher prefixes (per-launcher library files such as `Battle.net.config`, `settings.yaml` and `launcher_*.json`) are all unsupported.
- **Incremental scanning.** There is no library cache or filesystem watcher: every launch re-walks every tree.
- **Diagnostics.** There is no "why is my game missing" view: detector status, rejected entries and blacklist/merge decisions are invisible (see DET-01, DET-05, DET-17).

## Test blind spots (`tests/RHI.Core.Tests`)

| Finding | Current coverage |
|---|---|
| DET-01, DET-23 blacklist/canonical via path | Positive path-identity case only (`UpstreamGameLibraryPolicyServiceTests:169`); no negative "path contains blacklisted word" test. |
| DET-02 partial rescan and enrichment | `GameLibraryScannerPartialRescanTests` checks detector reuse only; no test of runtime fields or VM cache interaction. No `MainWindowViewModel` tests exist at all. |
| DET-03, DET-22 startup/refresh network deadlines | None: no test covers `InitializeAsync`, `FetchManifestAsync` timeout or partial-scan manifest refetch. |
| DET-04, DET-05, DET-10, DET-28 Lutris | 3 tests (runner parse, relative exe). None for launch command, `runner: linux`/`working_dir`, wrapped scalars, name source, env expansion. |
| DET-06 install-path override escape | `ApplyAsync_AppliesInstallPathLaunchAndCrossPlatformMetadata` covers the happy path only; no `..` containment test. |
| DET-07 HDR mediation | `HdrLinuxIntegrationTests:165-170` covers Steam and Manual only; no `Heroic/*` source. |
| DET-08, DET-27 Steam | `SteamGameDetectorTests` covers `ExtractAppIdFromAcfFilename` only. `Detect`, `FindLibraries`, root symlinks, `compatdata`, StateFlags and tool filtering beyond the name list are untested. |
| DET-11 WinePrefixDetector | No tests at all (only `IsLauncherManagedPrefix` and `IsKnownNonGameWineDirectory` in `LibraryFilteringTests`). |
| DET-12 Heroic fuzzy prefix | Exact `GamesConfig` prefix tested; `AttachPrefixes`/`Similarity` untested. |
| DET-13 log attribution | Only an unrelated-prefix case (`NativeRuntimeDetectionTests:78`). |
| DET-14 multi-API exe confidence | Tests assert DX12 wins over DX11 (`GraphicsApiDetectionTests:56`), the opposite of the ambiguity guard; no 3-or-more-API exe case. |
| DET-15 PCGW native guard / negative cache | None. |
| DET-16 `Merge` | No direct test; field-by-field merge, manual-flag survival and key canonicalisation untested. |
| DET-17 swallowed failures | Only policy fail-open (`UpstreamGameLibraryPolicyServiceTests:196`); detector-failure surfacing untested. |
| DET-18 corrupt stores | `LibraryStore_CorruptJsonFailsClosedToEmptyLibraryState` asserts the lossy behaviour; nothing asserts backup/preservation. |
| DET-19, DET-20, DET-21, DET-24, DET-29 VM flows | No `MainWindowViewModel` tests. |
| DET-25 symlink-following enumerations | Only `GameFileIndexer` is covered (`Build_DoesNotFollowDirectorySymlinks`). |
| DET-26, DET-30 exe/root heuristics | Busybox and Shipping cases only; no substring false-negative test; the UE test asserts the `<Project>` root. |

## Overall verdict

1. The detection code works on the happy path and is defensively written. Parsing is fail-open, the index is bounded and there is broad unit coverage (746 passing), but that coverage is almost all per-helper; there are no end-to-end detector or view-model tests.
2. The main weakness is identity. Paths are compared as uncanonicalised strings, the merge key is one flat string, and title-matching heuristics run across whole paths. Together these cause silent drops (DET-01, DET-05), wrong merges and wrong policy assignment.
3. The Lutris and Wine-prefix detectors are regex and heuristic scrapers where structured sources exist (`pga.db`, YAML, `config_info`, Steam `config.vdf`). That produces wrong exes, broken launch commands and duplicate launcher cards on this very machine.
4. Startup and refresh are coupled to network fetches with long or missing deadlines. HEAD's partial-rescan optimisation introduced a stale-cache regression (DET-02) that tests do not catch.
5. Robustness leans on `catch {}` with no log, so failures look like "no games". Fix identity canonicalisation, detector-status surfacing and store corruption handling before adding more launchers.
