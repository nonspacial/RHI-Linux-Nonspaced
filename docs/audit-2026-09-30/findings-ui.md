# RHI-Linux — UI / ViewModel layer audit

- **Commit:** `3aa0f21` (detached worktree `/mnt/vdo_storage/worktrees/rhi-audit`). Read-only: nothing under `src/` or `tests/` was modified.
- **Scope:** `src/RHI.Desktop/**`, `src/RHI.Platform.Linux/Ux/**`, `src/RHI.Platform.Linux/Settings/LinuxAppSettings.cs`. `MainWindowViewModel.cs` has 3385 lines at this commit, not 3372.
- **Baseline:** `dotnet test tests/RHI.Core.Tests -c Release` gives 746 passed, 0 failed.
- **Evidence method:** every claim below comes from reading the code. Where a claim was reproduced, the probe lives under `.scratch/audit/{combo-probe,vm-probe,vm-probe2,vm-probe3}` (see `.scratch/audit/ui-probes-README.md`).
  - These probes construct the real `MainWindowViewModel` headless, with an offline `HttpClient` and `XDG_*`/`HOME` redirected into a throw-away fixture.
  - Where a Control was needed, they use a plain Avalonia 12.1.2 `ComboBox`/`ListBox`. No window was opened.

---

## Critical

### UI-01 — Saving Settings silently deletes per-game HDR, Luma, OptiScaler and neural-pin preferences

- **Where:**
  - Trigger path: `MainWindowViewModel.cs:685` (`SaveLinuxSettings`) and `:699` (`ResetLinuxSettings`) both call `RefreshLocalizedPresentation` (`:712-789`).
  - Collections cleared: `:723` `ReShadeChannels.Clear()`, `:727` `OptiScalerVariantOptions.Clear()`, `:731` `HdrModeOptions.Clear()`, `:735` `LumaModeOptions.Clear()`, and `:761-763` → `ReplaceLatestLabel` → `:794` `options.Clear()`.
  - Also `:2757-2759` → `ReplaceVersionOptions` → `:764` `target.Clear()`, reached from startup (`:818`) and the "Load history" command (`:2696-2710`).
  - Settings dialog callers: `MainWindow.axaml.cs:617-625` (Save), `:632-644` (Apply to game) and `:513-542` (Reset).
- **What happens:**
  - Each of those ComboBoxes binds `ItemsSource` to the collection and `SelectedItem` TwoWay (`MainWindow.axaml:170-190, 321, 391-401`).
  - Clearing the collection makes Avalonia write `null` back into the ViewModel property. This was proven in `combo-probe`: `after clear: vm=<null> writes=v1.2|<null>`.
  - The `_loading*` guards are raised only at `:740-757`, after the clears. So the null writes run the persistence handlers:
    - `OnSelectedHdrModeChanged` (`:950-958`) → `SaveHdrProfile` (`:3111-3119`) → `ParseHdrMode(null)` = Off → the profile is removed from `library.json`.
    - `OnSelectedLumaModePreferenceChanged` (`:942-948`) → Auto → the preference is removed.
    - `OnSelectedOptiScalerVariantChanged` (`:926-940`) → Stable is persisted.
    - `OnSelectedDlss5*VersionChanged` (`:902-909`) → `SaveNeuralVersionPin(null)` (`:2678-2693`) → `IsLatestSelection(null)` is false → `Set(game, kind, null)` → the pin is deleted.
  - Lines `:746-749` then restore the old ViewModel values under the guards. The screen still shows the old choice while disk has lost it.
- **Reproduced in `vm-probe2`** (real VM with bound combos; Settings → Save with unchanged settings):
  ```
  BEFORE: persisted opti=Nightly hdr=Gamescope luma=Enabled feederPin=v1.2.3 | vm opti=Nightly hdr=Gamescope luma=Enabled feeder=v1.2.3
  AFTER : persisted opti=Stable  hdr=<none>    luma=Auto    feederPin=<none>  | vm opti=Nightly hdr=Gamescope luma=Enabled feeder=<null>
  ```
- **Why it matters:**
  - This is silent data loss of user configuration on the most ordinary action in the app. It also happens on every "Load history" click, and at startup whenever the neural catalogue arrives after a game has been selected.
  - The next launch (`:1264-1293`) of a restarted session loses the HDR wrapper.
  - A pinned Feeder, Bridge or ShortFuse version silently reverts to Latest at install time.
- **Minimal seam:** stop using localised display strings as ComboBox values.
  - Bind `ItemsSource` to immutable option records `{Value, Display}` and bind `SelectedValue`/`SelectedValueBinding` to the invariant value.
  - Refresh `Display` in place (INPC) instead of `Clear()`/re-adding items.
  - Interim fix: raise all `_loading*` guards before any `Clear()`, and add a `_loadingNeuralPins` guard around `ReplaceLatestLabel`/`ReplaceVersionOptions`.
- **Verification:** a headless xUnit test that binds a `ComboBox` to each option collection, sets a non-default value, calls `SaveLinuxSettings(GetLinuxSettingsSnapshot().Settings)`, then asserts that `LinuxGameLibraryStore.Load().HdrProfiles`, `LumaModePreferenceStore`, `OptiScalerVariantPreferenceStore` and `NeuralAddonVersionPreferenceStore` are unchanged. `vm-probe2/Program.cs` is that test in harness form.

---

## High

### UI-02 — A partial refresh serves stale cached probe results, so the API, engine and architecture shown are wrong

- **Where:**
  - `MainWindowViewModel.cs:1117`: `_probeCache` is cleared only when `onlyDetectors is null`.
  - The partial refreshes happen at `:2529`/`:2544` (edit/reset game), `:2573` (add manual) and `:2591` (remove manual).
  - Cache hit path: `:1000-1006`.
- **What happens:**
  - A partial refresh rebuilds `_allGames` from fresh `ShallowClone`s of pristine detector output (`GameLibraryScanner.cs:66-71`).
  - The fields that the runtime probe had filled in on the old instances are therefore gone. These include `GraphicsApi*`, `EngineHint`, `ForcedArchitecture` and `ManifestGraphicsApiOverride`, set by `LinuxGameRuntimeProbe.cs:31-53` and `DetectionHelpers.cs:346-348`.
  - `_probeCache` still holds each other game's entry, keyed by `GameKey`. Reselecting such a game hits the cache: `ApplyProbeResult` restores the component rows but `AnalyzeSelectedGameAsync` never runs, so the new instance is never re-probed.
- **Reproduced in `vm-probe3`:**
  ```
  1st select A: api='DirectX 12 · high confidence'
  AddManualGame -> Added Manual M…
  reselect A after partial refresh: sameInstance=False api='Unknown' status='Ready — Game A (cached)'
  ```
- **Why it matters:**
  - The screen shows "Unknown" API, no engine hint and no forced architecture while claiming "Ready (cached)".
  - Getters that decide eligibility read the stripped instance. Examples: `NeuralRenderingRecommendation` `:513`, `GetUalArchitecture` `:2780`, `GraphicsApiOverrideHint` `:291-295`, and Luma/RenoDX policy via `Decorate` on re-evaluation (`:3032-3062`).
  - The cached component rows were decorated against the old, fully-probed instance, so rows and header disagree.
- **Minimal seam:** key the cache by `(GameKey, scanEpoch)` or store the probe-derived metadata inside `ProbeCacheEntry` and re-apply it to the new instance on a hit. The simplest option is to clear `_probeCache` on every refresh, partial or full.
- **Verification:** `vm-probe3` scenario as a unit test: select A, add a manual game, reselect A, then assert that `SelectedApi` equals its first-probe value (or that the probe ran again).

### UI-03 — Property getters do synchronous disk I/O on the UI thread (≈220 full `deployments.json` parses per game selection)

- **Where:**
  - Getters that reach `LinuxDeploymentRecordStore.Find/FindAll`, which re-reads and re-deserialises the whole JSON file on every call (`LinuxDeploymentRecordStore.cs:23-33, 59-75`):
    - `UalInstalled/UalInstalledProxy/UalInstalledVersion` `:414-420`
    - `ShortFuseInstalled/…Version` `:461-462`
    - `CanConfigureFeeder/BridgeVersionPin` `:488-489`
    - `Dlss5FeederFullManaged` `:494`
    - `NeuralRenderingRecommendation` `:510-513`: two `Find`s plus `PeArchitectureDetector.Detect`, which opens the exe
    - `CanUpdateAll` `:169` → `HasManagedTargets`, which calls `FindAll` once per game in the library (`LinuxUpdateAllService.cs:143-144`)
  - File-system getters:
    - `PatchTargetStatusText` `:192-194`
    - `UalProxyHint` `:448-453`
    - `Dlss*RestoreAvailable`/`*DeploymentState` `:535-558, 3315-3334`
    - `CanOpenDlssBatch` `:603-606` (`Directory.Exists` per game)
  - `SelectedHdrPlan` `:332-334` → `BuildPlan` → `ProbeHost` reads `/proc/driver/nvidia/version` and scans `$PATH` for gamescope (`LinuxHdrLaunchProfileService.cs:83-100, 239-257`). It is rebuilt by each of `HdrProfileHint`, `HdrLaunchOptions` and `CanCopyHdrLaunchOptions`.
- **Fan-out:**
  - `NotifySelected` (`:3217-3262`) raises about 46 properties, then calls `NotifyDlss` (35, plus `NotifyDlssDefaults`), `NotifyUal` (11) and `NotifyShortFuse` (9).
  - A single selection triggers this set several times: `OnSelectedGameChanged` → `Load*`/`Refresh*State` → `NotifySelected` → `IsSelectionProbeBusy` change → `ApplyProbeResult` → probe end.
- **Measured (`vm-probe`):** 60 synthetic games, 3,000 files each, and a 392 KB `deployments.json` holding 60 records. Every notified property was re-read via reflection, as the reflection bindings do.

  | action | read syscalls | bytes read | sync ms on caller | PropertyChanged | getter ms |
  |---|---|---|---|---|---|
  | select one game | 21,732 | 86.1 MB (≈220× the JSON) | 1,543 | 346 | 1,673 |
  | search keystroke ("All") | 100 | 0.39 MB | 9 | 135 | 15 |
  | switch filter to "Installed" | 5,942 | 23.6 MB | 273 | – | – |

  The fixture is synthetic, and cost scales linearly with the size of `deployments.json` and the library. The absolute numbers are an upper-bound emulation; the multiplicity (≈220 parses per click) is structural.
- **Why it matters:**
  - Selecting a game stalls the UI thread, and the "background" probe does not help because the stall is in the getters.
  - Every `CanExecute` re-evaluation of `UpdateAllCommand` parses the store once per game.
- **Minimal seam:**
  - Compute a per-selection immutable `SelectedGameFacts` snapshot off-thread in `AnalyzeSelectedGameAsync`. It would hold records, installed flags, architecture, restore-availability and the HDR host snapshot.
  - Getters then read only that snapshot.
  - Separately, add an in-memory cache with file-mtime invalidation to `LinuxDeploymentRecordStore`, and cache `ProbeHost()` for the process lifetime.
- **Verification:** assert that the `/proc/self/io` `rchar` delta for `vm.SelectedGame = g` stays below 2 × `deployments.json` size, or count `Find` calls through a decorator store.
- **Status:** fixed in `a9bf929`, `0ba966e` and `fc6c40d` (#71, epic #89 unit 1). The selected game's analysis (`SelectedGameSession`) carries a `SelectionFacts` snapshot worked out off the UI thread: UAL/ShortFuse/Feeder records, files beside the executable, architecture, `.original` backup counts, the Luma policy, the HDR host (`LinuxHdrLaunchProfileService.BuildPlan(game, profile, host)`) and the Wine prefix state; getters read only it. `CanUpdateAll`/`CanOpenDlssBatch` read library facts worked out off the UI thread after scans and operations, and the Installed/RenoDX/Luma filter facts are kept per game until the next scan or operation. Tests: `SelectedGameSessionTests.Getters_ReadTheAnalysisSnapshot_NotTheDisk`.

### UI-04 — Several bulk operations clear the cache without re-probing, which wedges component actions ("wait for analysis")

- **Where:**
  - `UpdateAllAsync` `:1166-1168` re-probes only if `ChangedCount > 0`.
  - `RunBackgroundUpdatePassAsync` `:1251-1255` never re-probes.
  - `ApplyDlssBatchAsync` `:1957-1959` and `RestoreDlssBatchAsync` `:1984-1986` also re-probe only if something changed.
- **What happens:**
  - `_probeCache` loses the selected game's entry while `IsSelectionProbeBusy == false` and the old `Components` rows stay on screen.
  - Then:
    - `ManageComponentAsync` refuses with "Wait for game analysis to finish" (`:1494-1498`).
    - `ApplyReShadeProfileAsync` refuses the same way (`:1725-1729`).
    - `ApplyGlobalReShadeSettingsToSelectedGame` wrongly reports "Install or detect ReShade" (`:808-811`).
    - `CurrentShortFuseComponent` becomes null, so ShortFuse install is disabled (`:463-466, 477-480`).
  - After a background pass that did change something, the on-screen rows keep showing pre-update state.
- **Reproduced in `vm-probe3`:** `after cache wipe: components shown=18 probeBusy=False manage-> 'Wait for game analysis to finish before changing components.'`
- **Why it matters:** the UI claims analysis is pending when none is running. The only recovery is selecting another game and coming back.
- **Minimal seam:** use one `InvalidateAndReprobeSelected()` helper for every cache-invalidating path. The cache is cleared inside it, and it always calls `ReprobeGameAsync(selected)` when a game is selected.
- **Verification:** unit test that calls `UpdateAllCommand` with no managed changes and then `ManageComponentCommand`; assert that no "wait for analysis" status appears.
- **Status:** fixed in `a9bf929` (#72). Update All, the background pass and DLSS batch apply/restore go through `InvalidateAllAndReprobeSelectedAsync` (also after a cancel or failure); component actions read the published analysis, which an invalidation no longer removes. Tests: `SelectedGameSessionTests.DlssBatch_WithNothingChanged_LeavesTheSelectedGameAnalysed`.

### UI-05 — Unhandled exceptions from UI handlers and persistence writes can terminate the process

- **Where:**
  - No global handler: grep finds no `UnhandledException` or `UnobservedTaskException` in `src/RHI.Desktop` (`Program.cs:8-14`, `App.axaml.cs`).
  - `async void` or lambda handlers without try/catch around awaited work:
    - `MainWindow.axaml.cs:1140-1145` (FG unlock Update), `:1254-1258` (DLSS Enabler Update), `:1402-1406` (MFG Update). These go to `vm.Update*Async`, and e.g. `Dlssg2030UnlockDeploymentService.cs:395` awaits a SHA-256 read outside its `try`.
    - `:1684-1701`: the OptiScaler combo `SelectionChanged` awaits `SetOptiScalerAdvancedSettingAsync` (`VM :1358-1373`, no catch). The combo also stays disabled if it throws.
  - Synchronous handlers that do disk writes or deletes with no catch:
    - `save.Click` `:617-625` → `LinuxSettingsStore.Save` (`LinuxAppSettings.cs:111-120`)
    - `purgeCache.Click` `:511` → `PurgeCache` (`:128-134`, recursive `Directory.Delete`)
    - `OpenFolder` `:501-505` and `openCustomShaders/Addons` `:802-814` (`Directory.CreateDirectory`)
    - Commands `ToggleFavourite`/`ToggleHidden` `:2453, 2474` and `ToggleViewMode` `:2427`
    - `OnLibraryFilterModeChanged` `:884`
    - `SaveHdrProfile` `:3117`
  - Contrast: `ApplyGame` (`:634-643`) and `SaveDetailSectionLayout` (`:1858-1869`) are wrapped correctly.
- **What happens:**
  - An `IOException` or `UnauthorizedAccessException` (read-only or full `$XDG_CONFIG_HOME`, a busy cache file) escapes onto the dispatcher.
  - With no handler registered, Avalonia's default behaviour is process termination. This was not exercised live because launching the GUI was not permitted.
- **Why it matters:** user-triggered crashes on routine actions, and a partially completed update/delete gives no feedback.
- **Minimal seam:**
  - Register `Dispatcher.UIThread.UnhandledException` (log + status + `Handled = true`).
  - Route every `async void` handler through one `RunUiSafeAsync(Func<Task>)` wrapper.
  - Wrap all store writes behind a `TrySave` that reports to `StatusText`.
- **Verification:** point `XDG_CONFIG_HOME` at a read-only directory and invoke `SaveLinuxSettings` and `ToggleFavouriteCommand` from a test. Assert that there is no throw and that the status is set.
- **Status:** fixed in `41e8005` and `da6e9b0` (#73). `UiExceptionGuard` registers AppDomain/TaskScheduler/Dispatcher handlers (log + localised status line, dispatcher exceptions marked handled); errors go to the size-rotated app log `$XDG_STATE_HOME/rhi-linux/logs/rhi.log` (`AppLog`, the one log since integration/w1; `errors.log` was merged into it). Every `async void` handler and async event lambda runs through `RunUiSafe`, VM fire-and-forget tasks through `UiExceptionGuard.Forget`, and store writes report failure (`TryPersist`) instead of throwing; a failed settings save or manual-game removal changes nothing. Tests: `UiExceptionHandlingTests` (read-only config/data dirs through the real VM).

---

## Medium

### UI-06 — `_probeCache` keeps every visited game's full file index for the whole session

- **Where:** `MainWindowViewModel.cs:44-47, 70`. It is filled at `:1032, 1059`. Eviction happens only through the whole-cache clears (`:1117, 1166, 1253, 1957, 1984`) and single removes (`:1544, 2528, 2543`).
- **What happens:** `ProbeCacheEntry.Files` is the complete `GameFileIndexer.Build` list, capped at 200,000 paths per game (`GameFileIndexer.cs:16`).
- **Measured (`vm-probe`):** after visiting 60 games of 3,000 files each (≈140-char paths), the cache held **180,060 paths and 57.2 MB retained**, about **317 bytes per path**.
  - Arithmetic extrapolation at ~250 bytes per path: one capped game ≈ 50 MB; browsing a 100-game library of 20k-file installs ≈ 500 MB.
- **Also:** `GetDlssBatchCandidatesAsync` (`:1918-1940`), `PrepareUpdateIndexesAsync` (`:1183-1206`) and `PrepareDlssBatchIndexesAsync` (`:1997-2020`) hold the full index of every eligible game at once. This is transient but has the same peak.
- **Why it matters:** unbounded growth in a tray-resident app (close-to-tray keeps the process alive), for data that consumers only need for the selected game.
- **Minimal seam:** keep only derived results (`Components`, `Dlss`, facts) for non-selected games, and keep `Files` for the selected game plus a small LRU (e.g. 3). Re-index on demand; the harness's status line showed 3,001 files indexed in 402 ms.
- **Verification:** the same `vm-probe` loop, asserting retained bytes stay under a fixed bound (e.g. under 10 MB for 60 games).
- **Status:** fixed in `a9bf929` (#74, #60). The cache is an LRU of 8 analyses (`SelectedGameSession.DefaultCapacity`); measured 6.2 MiB retained after 60 games of 3,000 paths (only the 8 kept stay reachable). Bulk-operation indexes stay transient. Tests: `SelectedGameSessionTests.Cache_RetainsOnlyItsCapacityOfFileIndexes`, `BrowsingManyGames_KeepsAtMostTheCacheCapacity`.

### UI-07 — Each search keystroke and filter change resets the selection, cancelling and restarting analysis

- **Where:** `ApplyFilter` `:2359-2376` calls `Games.Clear()`. The ListBox TwoWay `SelectedItem` (`MainWindow.axaml:70`) writes `null`, then `:2374` re-selects.
- **What happens:**
  - `OnSelectedGameChanged(null)` (`:970-988`) runs `CancelSelectionProbe` and every `Reset*`.
  - `OnSelectedGameChanged(game)` then reloads every per-game store from disk, fires `RefreshSelectedLatestVersionsAsync` (network), and re-probes if not yet cached.
  - `vm-probe2` confirmed the transitions `<null> -> Game A` per keystroke.
  - In Installed/RenoDX/Luma modes, `GetLibraryFilterFacts` (`:2378-2396`) also parses `deployments.json` and enumerates a directory per game on every keystroke (see UI-03).
- **Why it matters:**
  - Typing in the search box keeps killing an in-flight probe of a large game, so it may never finish.
  - It multiplies the I/O from UI-03.
  - The null window briefly exposes `Reset*` side effects.
- **Minimal seam:** diff `Games` in place (remove non-matching, insert matching) instead of `Clear()`. Alternatively, suppress selection write-back during `ApplyFilter` with a guard, or keep a filtered view (`DataGridCollectionView`-style) over `_allGames`.
- **Verification:** a test that records `SelectedGame` transitions while setting `SearchText` to a value that still matches; assert zero transitions.
- **Status:** fixed in `cd51fac` (#75, epic #89 unit 3). `LibraryViewModel` filters the list in place (removes and inserts cards, never clears), the search applies 200 ms after the last keystroke, and a selection that still matches is kept without passing through null. Tests: `LibraryListTests`.

### UI-08 — `IsBusy` has no owner, so concurrent operations clear each other's gate

- **Where:**
  - `IsBusy` is set without checking it first in `ApplyDlssBatchAsync` `:1948`, `RestoreDlssBatchAsync` `:1975`, `SwapDlssComponentAsync` `:2049` and `RestoreDlssComponentAsync` `:2097`.
  - Dialog-driven writers never check it: `ApplyGlobalReShadeSettingsToSelectedGame` `:801-814`, `SetRenoDxCompatibilitySetting` `:1320`, `SetOptiScalerAdvancedSetting*` `:1349-1373`, and `SetDlssg2030/…Preferences`.
  - The background pass sets it too (`:1242, 1259`).
  - `RefreshAsync` returns silently when busy (`:1108`), and callers still report success (`SaveSelectedGameEditAsync` `:2529-2530`, `AddManualGameAsync` `:2573-2576`).
- **What happens:**
  - The batch dialog (its own window) can start while a background pass holds `IsBusy`. Whichever finishes first sets `IsBusy = false`, dropping the full-window overlay (`MainWindow.axaml:548`) while the other is still writing DLLs.
  - A modal settings dialog can write `OptiScaler.ini`/`ReShade.ini` while the background pass updates the same component.
  - If `IsBusy` is set, an edit or manual add is persisted but the library is not rebuilt. `AddManualGameAsync` then sets `SelectedGame` to null (`:2574`), yet its message still says "Added".
  - The background pass itself raises the blocking overlay, so a "silent" auto-update freezes interaction for its whole duration.
- **Why it matters:** overlapping mutations of the same game directory, UI gating that lies, and misleading success messages.
- **Minimal seam:**
  - Add an `OperationCoordinator` with `using var op = coordinator.TryBegin(kind)` returning null when busy, a reference count or owner token, and `IsBusy => op != null`.
  - Route every mutating entry point, dialogs included, through it.
  - Give background passes a non-blocking indicator.
  - Make `RefreshAsync` return a result so callers can report "deferred".
- **Verification:** a test that holds a fake long-running background pass and calls `ApplyDlssBatchAsync`. Assert it is refused, or that `IsBusy` stays true until both finish.
- **Status:** fixed in `34b0f67` (#76, epic #89 unit 2). `OperationCoordinator` owns the gate: `TryBegin(kind)` returns a scope or null, a stale scope never clears another's gate, and every mutating entry point (dialog writers, DLSS batch, background pass, refresh) claims it or is refused with "Another RHI operation is already running.". The background pass keeps the overlay (deviation: a non-blocking indicator would let the user start a conflicting write mid-pass) but it can now be cancelled. Tests: `OperationCoordinatorTests`.

### UI-09 — Long operations cannot be cancelled while the busy overlay blocks the whole window

- **Where:**
  - `ManageComponentAsync` `:1520-1530`: no token.
  - `UpdateAllAsync` `:1161-1165`: `CancellationToken.None`, with no token passed to `RunAsync`.
  - All install/remove/check commands `:1556-1860`: no token.
  - `BatchDlss_Click` → `GetDlssBatchCandidatesAsync()` `MainWindow.axaml.cs:121`: default token, no progress, and the button can be clicked again.
  - The shared `HttpClient` timeout is 5 minutes (`App.axaml.cs:36`).
- **What happens:** a stalled GitHub download holds `IsBusy` and the opaque overlay for up to 5 minutes per request, with no Cancel control.
- **Why it matters:** the app looks hung, and users will kill it mid-transaction.
- **Minimal seam:** have the coordinator from UI-08 own a `CancellationTokenSource` per operation, expose a Cancel command on the overlay, and pass the token through `LinuxManagedComponentService` (most service methods already accept one).
- **Verification:** a fake `HttpMessageHandler` that never completes. Invoke `ManageComponentCommand`, cancel, and assert that `IsBusy` returns to false within one second.
- **Status:** fixed in `34b0f67` (#77). Each operation has its own token, passed to the services; the busy overlay has a Cancel button (hidden for Repair after move, which must not stop part-way), the DLSS batch dialog's Close becomes Cancel while it runs, and a cancelled result reads "Operation cancelled.". Tests: `OperationCoordinatorTests.StalledDownload_Cancel_ReleasesTheOverlayWithinASecond`, `StalledComponentCheck_Cancel_SaysCancelled_NotFailed`.

### UI-10 — In Portuguese, the Add-game dialog never closes after a successful add

- **Where:** `MainWindow.Ux.cs:214` tests `result.StartsWith("Added ", Ordinal)`. The success string is `LF("{0} foi adicionado à biblioteca manual.", "Added {0} …")` (`MainWindowViewModel.cs:2576`).
- **What happens:** in pt-BR, which is the default for any `pt*` system culture (`LinuxAppSettings.cs:62-63`), the game is added but the dialog stays open. The user may add it again and get "already saved". `:433` uses the same localised-string-matching pattern; it currently checks both languages, but it is brittle.
- **Why it matters:** broken primary flow for the default locale of the original author.
- **Minimal seam:** return a result record (`Success`, `Message`) from `AddManualGameAsync` and `SaveSelectedGameEditAsync`, and branch on `Success`.
- **Verification:** a unit test with `UiLocalizationService.Apply("pt-BR")` asserting that `AddManualGameAsync` reports success structurally.
- **Status:** Add dialog fixed in `5d431b8`; Edit dialog fixed in `f94effe` (#78): `SaveSelectedGameEditAsync`/`ResetSelectedGameEditAsync` return a `(flag, message)` result and the dialog closes on the flag; a refused reset now stays open with its reason. Tests: `GameEditDialogResultTests` (PT-BR and EN).

### UI-11 — Localisation is split across two mechanisms, and several user-facing strings are single-language

- **Two parallel mechanisms:**
  - A key table: 374 dictionary entries, i.e. about 187 keys × pt/en, parity-tested by `LocalizationAndFeederDefaultsTests.cs:16-17`.
  - About 530 inline `L()`/`LF()` pairs:
    - VM: 189 `L` + 104 `LF`
    - `MainWindow.axaml.cs`: 136 + 27
    - `MainWindow.Ux.cs`: 60 + 14
  - The inline pairs have no parity check, no extraction and no translator workflow.
  - Backend strings go through the heuristic `Present()` (`UiLocalizationService.cs:577-609`). When no mapping exists, it replaces the real error with the generic `Ui.UnlocalizedPlatformMessage` (`:604-606`), so the actual cause is hidden.
- **Hard-coded single-language user-facing strings:**
  - PT only:
    - File-picker titles at `MainWindow.axaml.cs:890` "Selecionar Microsoft D3DCompiler_47.dll", `:897` "Selecionar DLL personalizada do ReShade" and `:1000` "Selecionar pacote oficial Deep Fried Chicken".
    - Tray menu `MainWindow.Ux.cs:687` "Mostrar RHI" and `:689` "Sair".
    - Window titles `MainWindow.axaml.cs:1551` "RenoDX — Compatibilidade — …" and `:1725` "OptiScaler — Avançado — …".
    - The launch-provider option "Automático" (`MainWindow.Ux.cs:239`, `LinuxGameIdentityService.cs:13`).
  - EN only, inside PT sentences: `DlssComponentDisplayName` `:2350-2357` ("Super Resolution"…) is interpolated into PT status lines at `:2045, 2053, 2060, 2065, 2093, 2103`. Elsewhere the code uses `L("Super-resolução", …)` (`MainWindow.axaml.cs:206`).
  - Unpresented backend text: `:1270` `LF(…, plan.Summary)` passes the English `LinuxHdrLaunchProfileService` summary raw, whereas `:341-342` calls `Present()`.
  - Backend PT-only messages reach EN users as the generic fallback. Examples are `Dlssg2030UnlockDeploymentService.cs:382, 386, 390, 392, 396`. A heuristic grep finds about 151 PT-looking literals in `RHI.Platform.Linux`.
  - Formatting: `ChooseFormat`/`Format` use `CultureInfo.CurrentUICulture` (`UiLocalizationService.cs:566-573`), not the chosen UI language.
- **Why it matters:** mixed-language UI, lost diagnostic detail, and no mechanical guard against regressions.
- **Minimal seam:**
  - Move inline pairs into the key table incrementally, starting with `MainWindow.Ux.cs` (74 pairs, self-contained).
  - Add a Roslyn/grep test that fails on string literals with non-ASCII PT diacritics outside `L(`/the dictionary.
  - Have backends return message keys plus arguments instead of prose.
- **Verification:** a test enumerating a whitelist, plus a grep test for `"[^"]*[ãõçé][^"]*"` outside localisation calls. Assert the count does not grow.
- **Status:** fixed in `a6a36e0` and `1aaf21e` (#79, #143). One mechanism: all inline `L()`/`LF()` pairs moved to the `UiStrings` key table (`tools/l10n-migrate.py` re-runs the move); the single-language strings listed above are keyed; `Format` uses the chosen language's culture; Portuguese-only platform messages get English through `PlatformMessageTranslations` at the `PresentFor` boundary, and text that still falls back to the generic message is logged once. Deviation: backends still return prose (keys + arguments would touch every service and collide with other lanes); the guard is a table test (parity, placeholders, no Portuguese in English, every PT-only template) rather than a source grep.

### UI-12 — A pinned neural version is saved but the ComboBox shows blank

- **Where:**
  - `LoadNeuralVersionPins` `:2668-2676` sets `SelectedDlss5*Version = pin` before `EnsureOption` adds the pin to the options.
  - `RefreshLocalizedPresentation` `:758-763` uses the same order, followed by the `Clear()` from UI-01.
- **What happens:** Avalonia's `ComboBox` rejects a `SelectedItem` that is not in `ItemsSource`. `combo-probe` showed `set-before-add: vm=v9 combo=<null>`, and adding the item afterwards does not re-select it.
- **Why it matters:** the user cannot see their pin and will likely "re-pin" or assume Latest.
- **Minimal seam:** call `EnsureOption` before assigning the selection. This becomes moot with the value/display records from UI-01.
- **Verification:** a headless combo bound to `Dlss5FeederVersionOptions`; select a game with a stored pin and assert that `combo.SelectedItem == pin`.

### UI-13 — The background update loop dies permanently on its first exception

- **Where:** `BackgroundUpdateLoopAsync` `:1218-1235`; `RunBackgroundUpdatePassAsync` `:1237-1261` has no catch.
- **What happens:** any exception except cancellation (e.g. from `PrepareUpdateIndexesAsync` or `RunAsync`) exits the `while` loop. The only trace is a status line. Auto-update stays off until the user changes the setting (`:687-688`).
- **Why it matters:** an opt-in feature silently stops for the rest of the session.
- **Minimal seam:** wrap the pass body in try/catch inside the loop, log it, and continue at the next tick.
- **Verification:** a fake `ILinuxManagedUpdateBackend` that throws once. Advance the timer through an injected `TimeProvider` or interval and assert that a second pass runs.

### UI-14 — Compiled bindings are disabled: 212 reflection bindings with no build-time checking

- **Where:**
  - `RHI.Desktop.csproj:7` sets `AvaloniaUseCompiledBindingsByDefault=false`.
  - `MainWindow.axaml` has 212 `{Binding}` occurrences over 176 distinct paths.
  - The two `DataTemplate`s declare `x:DataType` (`:75, :442`), but with compiled bindings off those declarations do not make the bindings compiled.
- **What happens:** every binding resolves by reflection at runtime. A renamed or removed ViewModel property turns into a silent blank or disabled control, logged only to Trace.
  - Today all 176 paths resolve, checked by a script against the VM, `ComponentRowViewModel` and `DetectedGame` sources.
  - Nothing enforces this. The VM's 150-odd hand-maintained `OnPropertyChanged(nameof(...))` lists make drift likely.
  - It also means per-read reflection plus boxing on each notification. With the UI-03 fan-out (346 notifications per selection), that is not free.
  - It blocks trimming/AOT. No `PublishTrimmed` is set today.
- **Why it matters:** refactoring the god VM (see Structure) without compiled bindings is high-risk. Compiled bindings are the safety net for that split.
- **Minimal seam:** add `x:DataType="vm:MainWindowViewModel" x:CompileBindings="True"` to `MainWindow.axaml` first and fix any build errors, then flip the project default. There are no `$parent`/`ElementName` bindings to complicate the move.
- **Verification:** `dotnet build` succeeds with compiled bindings on; a deliberately misspelt path then fails the build.
- **Status:** fixed in `d995716` (#88). `AvaloniaUseCompiledBindingsByDefault=true`, `MainWindow.axaml` typed `x:DataType="vm:MainWindowViewModel"`; a misspelt path fails with AVLN2000 (checked by hand). Test: `CompiledBindingsTests`.

---

## Low

### UI-15 — The probe mutates the shared `DetectedGame` off-thread while getters read it

- **Where:** `LinuxGameRuntimeProbe.cs:31-53` (`ConfigureAwait(false)` + `Task.Run`) and `DetectionHelpers.cs:346-348` write several related fields (`GraphicsApi`, `GraphicsApis`, `GraphicsApiConfidence`) on a thread-pool thread. At the same time, `SelectedApi` `:226-259` and other getters read them on the UI thread.
- **Also:** a cancelled probe keeps running until its next token check. If the user returns to the game before it is cached, two probes mutate the same instance.
- **Why it matters:** the UI can observe inconsistent field combinations. There is no crash risk.
- **Seam:** the probe returns an immutable result that the VM applies on the UI thread; this is the same seam as UI-03's snapshot.
- **Verification:** the probe interface returns a record; the model has no setters written from a background thread.
- **Status:** fixed in `a9bf929` (#82). The probe runs on `DetectedGame.ShallowClone()`; the fields it writes are captured as an immutable `GameRuntimeFacts` and applied on the UI thread with the analysis. A contract test classifies every writable `DetectedGame` property. Tests: `SelectedGameSessionTests.Probe_WritesAWorkingCopy_…`, `RuntimeFacts_CoverEveryProbeWrittenField_AndRoundTrip`.

### UI-16 — Some mutations leave the cached file index stale

- **Where:** `InstallOrUpdateUalAsync`/`RemoveUalAsync` `:1592-1667`, `SwapDlssComponentAsync`/`RestoreDlssComponentAsync`/`SwapStreamlineAsync`/`RestoreStreamlineAsync` `:2022-2192`.
- **What happens:** they call only `RefreshUalState`/`NotifyDlss`. `ProbeCacheEntry.Files` and `Components` are not recomputed, so a later `InstallAsync(game, kind, probe.Files)` (`:1530`) receives an index without the new proxy DLL or `.original` backups.
- **Seam:** reuse UI-04's `InvalidateAndReprobeSelected()`.
- **Status:** fixed in `a9bf929` (#83). UAL install/update/remove, DLSS and Streamline swap/restore and the ShortFuse load-order toggle re-analyse the selected game (`ReprobeKeepingStatusAsync`, the operation's own line stays). Tests: `SelectedGameSessionTests.DlssRestore_RebuildsTheFileIndex_AndKeepsItsResultLine`.

### UI-17 — "Clear cache" runs synchronously on the UI thread

- **Where:** `PurgeLinuxCache` `:706-710` → `LinuxAppSettings.cs:128-134`.
- **What happens:** it enumerates and then recursively deletes `~/.cache/rhi-linux` (downloaded packages and shader packs) synchronously in a click handler.
- **Seam:** `await Task.Run(...)` with the coordinator gate from UI-08.

### UI-18 — `DetectedGame` is not observable

- **Where:** `DetectedGame.cs:7`, `LibraryBadge` `:24`.
- **What happens:** the library list's badge reflects favourite/hidden only because `ToggleFavourite`/`ToggleHidden` call `ApplyFilter`, which re-creates the rows (and triggers UI-07). Any future in-place update will not refresh the list.
- **Seam:** a thin `GameRowViewModel` wrapper implementing INPC.
- **Status:** fixed in `cd51fac` (#85). The list shows `GameRowViewModel` cards (INPC); favourite/hidden changes go through the card on the UI thread and update the badge in place. Tests: `LibraryListTests.ToggleFavourite_UpdatesTheSameCardInPlace`.

### UI-19 — "Latest version" caches never expire

- **Where:** `_ualLatestVersionCache`/`_shortFuseLatestVersionCache` `:75-78, 2799-2864`.
- **What happens:** they are filled once per session. A long-running tray process shows a stale "latest" until the user presses Check.
- **Seam:** add a TTL.

### UI-20 — Duplicated display↔value mapping between the VM and the Settings dialog

- **Where:** `MainWindow.axaml.cs:326-339` reimplements `ReShadeChannelDisplay/Value` from `MainWindowViewModel.cs:388-409`. `:317-318` maps language display names by exact English/Portuguese literals.
- **Why it matters:** divergence is only a matter of time; the root cause is the same display-string-as-value pattern as UI-01.

### Checked and not found (negative results)

- No `ObservableCollection` is mutated off the UI thread. Every VM mutation follows an `await` without `ConfigureAwait(false)`, so it resumes on the UI context. The watcher callback is marshalled (`MainWindow.Ux.cs:32-33`).
- No blocking `.Result`/`.Wait()`. The `.Result` uses at `:2757-2759` and `:2875-2881` all follow a completed `Task.WhenAll`.
- `LinuxAddonFileWatcher` unsubscribes and disposes (`LinuxAddonFileWatcher.cs:53-61`) and is disposed on `Closed` (`MainWindow.Ux.cs:60-64`).
- The `PeriodicTimer` is `using`-scoped (`:1224`). The tray icon is removed and disposed (`:707-713`).
- The selection-generation guard in `AnalyzeSelectedGameAsync` (`:1030, 1057, 1070, 1075`) correctly prevents stale apply and stale `IsSelectionProbeBusy` resets. Its only cost is that a completed but superseded probe is discarded rather than cached.

---

## Structure

### Concerns inside `MainWindowViewModel.cs` (3385 lines)

| # | Concern | Main line ranges |
|---|---|---|
| 1 | Localisation helpers and display↔value parsing | 35-42, 145-152, 388-409, 3003-3020, 3064-3081, 3160-3215, 3345-3355 |
| 2 | Composition with service-locator fallbacks (three hidden `new LinuxDeploymentRecordStore()`) | 608-644 |
| 3 | App settings, UX settings and language refresh | 646-799, 2422-2429, 2636-2648 |
| 4 | Startup and DLSS manifest load | 816-844 |
| 5 | Library: scan, filter, search, favourite/hide, manual add/remove, edit target | 82, 106-108, 154-171, 876-892, 1096-1150, 2359-2420, 2431-2593 |
| 6 | Selection plus probe orchestration and `_probeCache` | 44-47, 70-72, 970-1094, 1542-1554, 2650-2656, 3032-3062, 3139-3158 |
| 7 | Selected-game presentation (identity, paths, badges, API text) | 162-307, 348-387, 3217-3262 |
| 8 | Graphics-API override | 917-924, 2974-2983, 3132-3190 |
| 9 | Luma mode policy | 308-329, 942-948, 3022-3030, 3048-3062 |
| 10 | HDR launch profile and Launch | 331-346, 950-968, 1263-1293, 3064-3130 |
| 11 | Update All and background loop | 1152-1261 |
| 12 | Per-component advanced settings (RenoDX, OptiScaler, FG2030, DLSS Enabler, MFG): pure middle-man pass-throughs | 1296-1487 |
| 13 | Generic managed-component install/remove | 1489-1540 |
| 14 | UAL | 414-459, 1556-1667, 2778-2836, 2922-2972 |
| 15 | ShortFuse | 461-492, 1669-1718, 2838-2920 |
| 16 | ReShade profile, D3DCompiler, Shaders/Add-ons, Luma drop, custom add-on | 411-412, 1720-1860, 2215-2331, 2595-2634 |
| 17 | DLSS/Streamline swap/restore, defaults and batch | 494-606, 1862-2192, 2333-2357, 3264-3343 |
| 18 | Neural-rendering version pins and catalogue | 488-501, 894-909, 2658-2776 |
| 19 | Desktop openers | 2194-2213 |
| 20 | Hand-maintained notification fan-out | 851-874, 3217-3313 |

### Code-behind

- **`MainWindow.axaml.cs`:** about 1,450 of its 1,900 lines are imperatively built dialogs with no bindings or templates:
  - Batch DLSS 114-289
  - Settings 310-677
  - Shaders/Add-ons 689-886
  - Pack files 914-996
  - Five component dialogs 1040-1734
  - The rest is section layout (1736-1869) and pickers.
- **`MainWindow.Ux.cs`:** window lifecycle, tray, watcher, MOTD, drag-drop, and the Add and Edit dialogs.

### Proposed split (7 units, cheapest seams first)

1. **`SelectedGameSession` (service + VM).**
   - Owns selection generation, `_probeCache` (bounded, UI-06), re-probe and invalidation (UI-02/04/16).
   - Publishes one immutable `GameAnalysis` (components, DLSS detection, record facts, architecture, HDR host, restore availability) computed off-thread (UI-03/15).
   - Seam: everything under concern 6, plus the `IsSelectionProbeBusy` handling. All other panels consume `GameAnalysis`, which is what makes the rest cheap.
2. **`OperationCoordinator`.**
   - Owns `IsBusy` with ownership, per-operation CTS and a Cancel command, plus `StatusText` and error surfacing (UI-05/08/09/13).
   - Seam: replace the ~25 `IsBusy = true … finally false` blocks with `using var op = …`.
3. **`LibraryViewModel`.**
   - Concern 5: `Games`, `_allGames`, `_libraryState`, filter diffing (UI-07), manual add/remove/edit with result records (UI-10).
   - Emits `SelectedGame`. Its only dependencies are the coordinator and the scanner.
4. **`DlssPanelViewModel`.**
   - Concern 17; the `NotifyDlss`/`NotifyDlssDefaults` groups already delineate its surface.
   - It reads `GameAnalysis.Dlss` and `OrchestratedStackManaged`.
5. **`ExtrasViewModel`.**
   - Concerns 14, 15 and 18 (UAL, ShortFuse, neural pins); `NotifyUal`/`NotifyShortFuse` already delineate these.
   - Absorbs the latest-version caches (UI-19).
6. **`RuntimePanelViewModel`.**
   - Concerns 8, 9 and 10: per-game preferences with the `_loading*` pattern, replaced by `{Value, Display}` option records (UI-01/12/20).
7. **`ShellViewModel` + per-dialog VMs.**
   - Settings (concern 3), Update All/background (11), and startup (4).
   - Concerns 12 and 16 move into `ReShadeProfileViewModel` and one small VM per component dialog, bound from AXAML `DataTemplate`s instead of 1,450 lines of code-behind.

Suggested order: enable compiled bindings (UI-14) first as the safety net, then 1, then 2, then 3, then 4 and 5 (mechanical moves), then 6, then 7. Units 1 and 2 remove most of the Critical and High findings on their own.

**Status (lane w2-vmsplit, #89):** units 1 (`SelectedGameSession`, `a9bf929`), 2 (`OperationCoordinator`, `34b0f67`) and 3 (`LibraryViewModel` list with `GameRowViewModel` cards, `cd51fac`) are extracted, with VM tests for each. Units 4–7 (DLSS panel, Extras, runtime panel, shell and per-dialog VMs) are not started; manual add/edit/remove (UI-10 result records) still live in the shell view model.

---

## Test blind spots

- **Nothing constructs `MainWindowViewModel`.** `grep` finds no reference in `tests/RHI.Core.Tests/*.cs`. The test project references `RHI.Desktop` only for `UiLocalizationService`, `ComponentRowViewModel` (`LocalizationAndFeederDefaultsTests.cs`) and `DetailSectionLayoutStore` (`DetailSectionLayoutStoreTests.cs`).
- **Covering these is cheap.** The `vm-probe*` harnesses build the real VM headless in about 40 lines: offline `HttpClient`, XDG redirection, fake `IGameDetector`/`IGameRuntimeProbe`. Plain Avalonia `ComboBox`/`ListBox` instances work without an `AppBuilder`.

| Finding | Covered today? | What is missing |
|---|---|---|
| UI-01 settings save wipes per-game prefs | No | bound-combo + `SaveLinuxSettings` persistence assertion |
| UI-02 stale cache after partial refresh | No | select → add manual → reselect → assert probe or API |
| UI-03 getter I/O | No | I/O or store-call budget per selection |
| UI-04 cache wipe wedge | No; `UpdateAllBackgroundTests` cover only the service | VM-level Update All → ManageComponent |
| UI-05 crash paths | No | read-only XDG test for every store write reachable from the UI |
| UI-06 cache growth | No | retained-bytes bound |
| UI-07 filter churn | No; `LibraryFilteringTests` cover the pure `GameLibraryFilter` only | selection transition count on search |
| UI-08/09 busy ownership and cancellation | No; only the service's own reentrancy guard (`UpdateAllBackgroundTests.cs:160-177`) | VM-level concurrent-operation and cancel tests |
| UI-10 PT add dialog | No | pt-BR structural success test |
| UI-11 inline `L()` pairs and hard-coded strings | Partly: key-table parity and `Present()` (`LocalizationAndFeederDefaultsTests.cs:16-47`) | lint for single-language literals; inline-pair coverage |
| UI-12 pin display | No | headless combo selection assertion |
| UI-13 background loop resilience | No; the service is tested, the loop is not | throw-once backend, second tick runs |
| UI-14 binding paths | No | compiled bindings would make the build the test |
| Watcher, MOTD, settings store, DLSS defaults service, desktop opener | Yes (`LinuxNativeUxTests`, `SettingsPageTests`, `LinuxDlssDefaultsServiceTests`) | none |

---

## Verdict

1. The Linux service layer below the UI is reasonably tested (746 passing tests); the 3,385-line ViewModel and the 2,600 lines of code-behind above it have no tests at all.
2. One confirmed critical bug (UI-01): an ordinary Settings → Save silently deletes per-game HDR, Luma, OptiScaler and version-pin preferences, because localised display strings are used as ComboBox values.
3. The probe cache is both wrong (stale after partial refresh, and wedging component actions after bulk operations) and unbounded (~57 MB per 180k paths measured).
4. Property getters do synchronous disk and JSON I/O on the UI thread (~220 full `deployments.json` parses per game click). Persistence writes and `async void` handlers lack error handling and can crash the app.
5. The fix path is incremental. First enable compiled bindings, then extract `SelectedGameSession` (an immutable, off-thread analysis snapshot) and an `OperationCoordinator` (busy ownership plus cancellation). Those two seams remove most High findings before any wider VM split.
