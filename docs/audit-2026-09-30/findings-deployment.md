# RHI-Linux deployment layer — read-only audit

- Commit: `3aa0f21` (detached worktree `/mnt/vdo_storage/worktrees/rhi-audit`).
- Scope: `src/RHI.Platform.Linux/Deployment/**` (20,741 lines, 47 files), `src/RHI.Core/Services/{TransactionalFileDeploymentService,DlssManagerService,ComponentProbe}.cs`, `src/RHI.Platform.Linux/Network/GitHubApiTokenHandler.cs`.
- Baseline: `dotnet test tests/RHI.Core.Tests` → 746 passed, 0 failed. No source file was edited.
- Paths below are relative to `src/RHI.Platform.Linux/Deployment/` unless they start with `src/`.

---

## Critical

### DEP-01 — A single unreadable `deployments.json` silently wipes every ownership record
- **Where:** `LinuxDeploymentRecordStore.cs:59-75` (`LoadUnsafe`), `:35-44` (`Upsert`), `:46-54` (`Remove`), `:77-84` (`SaveUnsafe`).
- **What happens:** `LoadUnsafe` catches every exception and returns an empty list (`catch { return []; }`, line 74). The next `Upsert` or `Remove` then calls `SaveUnsafe` with that list, so the file is rewritten to hold only the single record being saved. Any of these triggers it: truncated or corrupt JSON, a transient read error, or a future schema that fails to deserialise. `SaveUnsafe` writes the temporary file without an fsync before the rename (lines 82-83), so a power loss can leave an empty or truncated file on some filesystems. The only lock is the in-process `_gate` (line 11), so a second app instance loses updates.
- **Why it matters:** These records are the only authority for `.original` restore, Wine override restore and hash-guarded removal. Once they are lost, every RHI-installed DLL is classed as external: `LinuxManagedComponentService.cs:196-203` and `ReShadeDeploymentService.cs:128-129` lock removal. The `.original` backups and the `user.reg` overrides are left orphaned for every game at once.
- **Remediation seam:** Change `LoadUnsafe` to fail closed. Distinguish "file missing" (empty) from "unreadable" (throw, or quarantine to `deployments.json.corrupt-<ts>` and refuse writes). Write with `FileStream.Flush(true)` before `File.Move`, keep a `.bak` of the last good file, and take an advisory file lock (`FileShare.None` on a `.lock` sibling).
- **Verification:** Unit test: write `"{ not json"` to the path, call `Upsert(recordB)`, then assert the original bytes are preserved (or that the call throws) and that `Find(recordA)` is not silently null.

---

## High

### DEP-02 — Transactional rollbacks delete the last snapshot even when the restore copy failed
- **Where:** `src/RHI.Core/Services/TransactionalFileDeploymentService.cs`: `RestoreBatchAsync` rollback `:253-273` plus `CleanupRestoreSnapshots` `:276-284`. The same pattern appears in `ReplaceAsync` `:62-77`, `RestoreAsync` `:118-131` and `ReplaceBatchAsync` `:176-202`.
- **What happens:** Every rollback copy runs inside `try { File.Copy(...) } catch { }` and the snapshots are then deleted unconditionally. Concrete loss path in `RestoreBatchAsync`:
  1. Target A restores successfully, which consumes `A.original` (`RestoreAsync:113`).
  2. Target B fails.
  3. Rollback copies A's target snapshot back over A (the modded file), then copies A's backup snapshot to `A.original`.
  4. If that second copy throws (ENOSPC is the likely cause of the original failure too), it is swallowed at line 267.
  5. Line 270 deletes the backup snapshot.
  6. End state: A is modded, `A.original` is gone, and the game's true original no longer exists anywhere.
- **Rollback copies are unsafe too:** They use plain `File.Copy(overwrite: true)`. That is neither atomic nor hash-verified, unlike the forward path.
- **Why it matters:** This is exactly the failure mode the class claims to prevent, as stated in its header doc (lines 5-10).
- **Remediation seam:** Track rollback success per target. Delete a snapshot only after its restore is verified (reuse `CopyVerifiedAsync` into a temp file, then `File.Move`). Otherwise keep it and report the path in the error. Return a `RollbackIncomplete` status instead of a plain failure.
- **Verification:** Inject a failing file-system seam (or `chmod 0500` the directory after the first restore) in a two-target `RestoreBatchAsync` test. Assert that either `A.original` or a surviving snapshot still holds the original bytes.

### DEP-03 — No crash journal: a crash mid-operation leaves unowned payloads, orphaned `.original` files and temp copies that are never swept
- **Where:**
  - Records are written last: `ReShadeDeploymentService.cs:254` (file replaced) → `:265` (Wine override) → `:375` (`Upsert`); `OptiScalerDeploymentService.cs:277` → `:295`; `StandaloneDlssEnablerDeploymentService.cs:159-164`.
  - Snapshot/temp names that are never swept: `TransactionalFileDeploymentService.cs:29-30,156,231,236` and `ReShadeDeploymentService.cs:216`. A grep for any sweep of `*.rhi-tmp-*`, `*.rhi-rollback-*`, `*.rhi-replace-current-*` or `*.rhi-update-snapshot-*` finds none.
- **What happens:** If the process dies between the file mutation and `Upsert`, the game directory keeps the new proxy DLL, its `.original` sidecar and the `native,builtin` override, with no record. On the next run ReShade is detected as external (`ReShadeDeploymentService.cs:128-129`), so install, update and remove are all refused. The `.rhi-*` snapshots are full copies of game DLLs, and they accumulate in the game directory.
- **Why it matters:** A kill or crash during a long step (shader-pack sync at `ReShadeDeploymentService.cs:322`, archive extraction) leaves a state the user cannot recover without doing it by hand.
- **Remediation seam:** Write-ahead intent record: `Upsert` a `Pending` record listing the planned targets and overrides before mutating, then flip it to `Committed`. On start-up, reconcile pending records (roll back using the snapshots and `.original` files) and sweep `.rhi-*` temporaries under directories that have records.
- **Verification:** Fault-injection test: wrap `IFileDeploymentService` so it throws `Environment.FailFast`-equivalent (simulated by abandoning the task) after `ReplaceAsync`, construct a fresh service, and assert that the reconciler restores the original bytes and removes the override.

### DEP-04 — Rollback paths reuse the caller's (cancelled) token, so a cancelled install is not rolled back
- **Where:**
  - `OptiScalerDeploymentService.cs:303-312`: `RollbackDeploymentAsync(..., cancellationToken)` and `RollbackReShadeMigrationAsync(migration, cancellationToken)`.
  - `OptiScalerDeploymentService.cs:494`: `_files.RestoreAsync(target, cancellationToken)`, which reaches `src/RHI.Core/Services/TransactionalFileDeploymentService.cs:93`, `ThrowIfCancellationRequested` inside `try`, which returns a failure result that is ignored.
  - `RenoDxDeploymentService.cs:238,242,248` (install rollback) and `:497,499` (remove rollback) call `File.WriteAllBytesAsync(..., cancellationToken)` inside `catch { }`.
- **What happens:** When the operation fails because the token was cancelled (e.g. during `Sha256Async(target, cancellationToken)` at `OptiScalerDeploymentService.cs:290`), each rollback restore throws or returns immediately. For pre-existing targets the modded file and its `.original` stay in place with no record. RenoDX leaves the modified INI or addon in place.
- **Contrast:** `LinuxManagedComponentService.cs:856,861,867,878,883,889` correctly use `CancellationToken.None` for rollback.
- **Latent case:** The same pattern exists in `ReShadeDeploymentService.cs:393`. It is currently unreachable because a fresh install always has `existedBefore == false`.
- **Remediation seam:** Every compensation path should take `CancellationToken.None`. Add an analyser or code-review rule: no `cancellationToken` identifier inside `catch` blocks in `Deployment/`.
- **Verification:** Test: OptiScaler install over a game directory with a pre-existing file from the package set; cancel the CTS from a fake `IFileDeploymentService` after the batch succeeds. Assert that the original bytes are restored, no `.original` remains, and there is no record.

### DEP-05 — `user.reg` is edited while Wine may own it, with no lock, and one path restores it with a non-atomic truncate-and-write
- **Where:**
  - `WineDllOverrideService.cs:22-38` (read-modify-write), `:202-207` (`AtomicWrite`, no fsync).
  - Callers with no running-game check: `ReShadeDeploymentService.cs:265,307`, `OptiScalerDeploymentService.cs:247`, `StandaloneDlssEnablerDeploymentService.cs:159`, `D3DCompilerToolInstaller.cs:138`, and others. `IsRunning` exists only in `LinuxUpdateAllService.cs:172` and `LinuxDlssDefaultsService.cs:129,160`.
  - `ShortFuseDeploymentService.cs:369-372` snapshots the whole `user.reg` into memory; `:513-517` restores it with `File.WriteAllBytes(registryPath, ...)`, errors swallowed.
- **What happens:**
  1. wineserver keeps the registry in memory and rewrites `user.reg` on flush or exit. An edit made while the prefix is live is either overwritten (the override silently disappears and the record claims ownership of a value that is gone) or clobbers Wine's own changes.
  2. There is no in-process or inter-process lock between services that edit the same prefix.
  3. ShortFuse's rollback truncates `user.reg` before writing. A crash or ENOSPC there leaves the prefix's HKCU hive empty or partial. The wholesale restore also discards any other registry changes made since the snapshot.
- **Why it matters:** `user.reg` holds every per-user setting of the prefix (game saves are sometimes in HKCU as well). Shared Heroic and Lutris prefixes widen the blast radius.
- **Remediation seam:**
  - Add a `PrefixRegistryGuard`: refuse (or offer to run `wineserver -k`) when a process in the prefix is alive (scan `/proc/*/environ` for `WINEPREFIX`, or reuse `LinuxGameRunningProbe`).
  - Add a per-prefix `SemaphoreSlim` plus a `flock` on `user.reg.rhi-lock`.
  - Replace ShortFuse's byte snapshot with per-key `WineOverrideRecord` restore through `WineDllOverrideService`.
  - Fsync the temp file before rename.
- **Verification:** Integration test with a fake `IGameRunningProbe` returning true: install must refuse. Unit test: make ShortFuse remove throw after the registry snapshot and assert that `user.reg` is replaced atomically (inode changes; never zero length).

### DEP-06 — RenoDX add-on download: no scheme or host allow-list and no integrity check
- **Where:**
  - Wiki scraping accepts any `http…` link: `RenoDxCatalogService.cs:307-311` (`MakeAbsolute`: `href.StartsWith("http")` passes `http://` and any host), `:303-305` (only the `.addon64` / `.addon32` suffix is checked).
  - The manifest `SnapshotOverrides` URL is taken verbatim: `UpstreamRhiManifestService.cs:466` → `RenoDxCatalogService.cs:143-154`.
  - The download does only an MZ check: `RenoDxDeploymentService.cs:575-592`.
- **What happens:** Whatever URL the upstream JSON, the wiki or the manifest supplies (including plain HTTP or an arbitrary host) is fetched and written into the game directory as a ReShade add-on, i.e. a DLL loaded into the game process. No digest is available or checked.
- **Contrast:** `DgVoodooDeploymentService.cs:121-127`, `LumaFrameworkDeploymentService.cs:904-909` and `src/RHI.Core/Services/DlssManagerService.cs:467-474` all enforce HTTPS plus a host allow-list.
- **Why it matters:** A compromised wiki edit, or an on-path attacker once any `http://` link appears, gives code execution in the user's session through the game.
- **Remediation seam:** One shared `TrustedDownloadPolicy.Validate(Uri, allowedHosts)`, called by `RenoDxDeploymentService.DownloadAsync`, `ReShadeAddonManagerService.DownloadAsync` (`:649`) and `DofFixDeploymentService` (see DEP-07). Minimum rules: HTTPS only; hosts `github.com`, `objects.githubusercontent.com`, `raw.githubusercontent.com`.
- **Verification:** Unit test: `ParseWiki` with an `http://evil/x.addon64` link, and a manifest with an `http://` `SnapshotOverrides` value. Assert rejection before any HTTP call (use a fake handler that fails the test if it is invoked).

---

## Medium

### DEP-07 — GitHub asset digests are optional: verification is fail-open almost everywhere
- **Where:**
  - The resolver returns `Sha256 = null` when `digest` is absent: `GitHubReleaseAssetResolver.cs:190-193`.
  - Consumers skip verification on null: `GitHubReShadeAddonDeploymentService.cs:298`, `REFrameworkDeploymentService.cs:305`, `DofFixDeploymentService.cs:321`, `LumaFrameworkDeploymentService.cs:931`, `UltimateAsiLoaderDeploymentService.cs:522-523`, `ShortFuseDeploymentService.cs:723`, `DgVoodooDeploymentService.cs:97`, `Dlss5FeederFullDeploymentService.cs:958`.
  - `OptiScalerPackageProvider.cs:198` returns `true` ("usable") when there is no digest.
  - A manifest override drops the hash on purpose: `DofFixDeploymentService.cs:272-280` (`usingOverride ? null : best.Sha256`), with no host validation of the override URL.
  - Only `Rtx40MfgStandaloneDeploymentService.cs:500-501` fails closed.
- **What happens:** An asset without a published digest is deployed on the strength of TLS plus an MZ or PE check alone. The policy differs from service to service.
- **Why it matters:** It is an inconsistent trust model. A tampered release asset (or a mirror, for DOF Fix) is not detected.
- **Remediation seam:** Put a single `IntegrityPolicy { Required, PreferDigest, None }` on each component definition, enforced in one download helper. Record the verified digest in the deployment metadata. Warn in the UI when the policy is `None`.
- **Verification:** Table-driven test over every `*DeploymentService` using a fake release JSON without `digest`. Assert the configured policy is honoured and that the DOF Fix override URL is rejected for non-GitHub hosts.

### DEP-08 — ReShade binaries: no hash, a third-party proxy for nightly builds, and a size-only identity
- **Where:**
  - Setup exe from `reshade.me`, MZ check only: `ReShadeDeploymentService.cs:851-869`.
  - Nightly via `nightly.link`: `ReShadeDeploymentService.cs:14-15,776-811`.
  - Nightly cache identity is `Content-Length`: `:784-787,804`, and `CheckUpdateAsync:532-535`.
  - The staged DLL is reused if `IsValidReShadeDll` passes: `:849`.
- **What happens:** Stable and legacy DLLs have no integrity anchor. A nightly artifact of equal size is treated as current, and a cached DLL is never re-verified against its source.
- **Why it matters:** ReShade is injected into every game that uses it, so it is the highest-value payload.
- **Remediation seam:** Store the SHA-256 of each downloaded setup or zip in a sidecar `.sha256`. For nightly, use the GitHub Actions artifact digest (or the ETag) instead of the length. Re-hash the staged DLL against the sidecar before reuse.
- **Verification:** Test: two nightly zips of equal length with different content must trigger a re-stage.

### DEP-09 — ReShade Addon Manager: fragile rollback, a separate ledger and unvalidated URLs
- **Where:** `ReShadeAddonManagerService.cs:277-289` (old files deleted before the new ones are written), `:318-329` (the rollback `File.Copy` loop has no per-item catch), `:346` (`finally` deletes the rollback directory unconditionally), `:336` (state saved after the files are mutated), `:758-764` (`SaveState`, no fsync), `:649-663` (`DownloadAsync`, no scheme, host or hash check).
- **What happens:**
  1. If one rollback copy throws, the remaining old add-ons are not restored and their only copies are deleted with `rollbackDir`.
  2. A crash between the file writes and `SaveState` leaves new add-ons that the next apply treats as external (`:266-267`), which blocks further changes.
- **Why it matters:** The user's selected add-ons are lost or stuck, and the Addon Manager has no tests at all (see the test blind spots).
- **Remediation seam:** Route add-on deployment through `IFileDeploymentService.ReplaceBatchAsync` plus a single ownership ledger (DEP-S5). Keep rollback snapshots until the restore is verified. Apply `TrustedDownloadPolicy`.
- **Verification:** Test `ApplySelectionAsync` with a fake payload source that fails on the second add-on, and make the rollback copy of the first fail. Assert the backup survives on disk.

### DEP-10 — RenoDX can overwrite and later delete a foreign file without a backup
- **Where:** `RenoDxDeploymentService.cs:166-173` (foreign-file guard matches only `renodx*.addon*`), `:162,215` (`CopyAtomicAsync` overwrites `target`), `:280` (`ExistedBeforeInstall = false` hard-coded), `:489-490` (remove deletes the target).
- **What happens:** When the resolved file name does not start with `renodx` (possible for manifest `SnapshotOverrides` URLs such as third-party UE-Extended builds), a pre-existing file of that name is overwritten without a `.original`. On remove it is deleted. The pre-copy snapshot (`:190-193`) is used only for rollback of a failed install.
- **Why it matters:** A user-installed add-on is destroyed silently.
- **Remediation seam:** Before copying, refuse if `File.Exists(target) && existingRecord is null`, or route the copy through `_files.ReplaceAsync` and record `ExistedBeforeInstall` truthfully.
- **Verification:** Test: a manifest override URL `.../foo.addon64` with an existing `foo.addon64` in the game directory must leave its bytes intact after install and after remove.

### DEP-11 — Wine override restore ignores user changes; the registry backup is taken once and never used
- **Where:** `WineDllOverrideService.cs:46-64` (`Restore` writes `PreviousValue` or removes the key without checking that the current value is still `native,builtin`), `:196-200` (`BackupRegistryOnce` writes `user.reg.rhi-before-dll-overrides` only if absent; nothing reads it, confirmed by grep).
- **What happens:** If the user or another tool changes the override after RHI set it, uninstall reverts that change. The one-off backup goes stale after its first use and offers no recovery path.
- **Remediation seam:** In `Restore`, act only when `TryReadOverride(...) == "native,builtin"` (the value RHI wrote); otherwise leave it and report. Either drop the stale backup or replace it with timestamped rotating backups plus a restore command.
- **Verification:** Unit test: ensure, then the user sets `"dxgi"="builtin"`, then `Restore`. Assert the user value is preserved.

### DEP-12 — Writes are not durable, and several "atomic" copies verify length only
- **Where:**
  - No `Flush(true)` or `FileOptions.WriteThrough` anywhere under `src/` (grep).
  - Length-only verification: `RenoDxDeploymentService.cs:557`, `OptiScalerDeploymentService.cs:576`, and the same helper copied into `DofFixDeploymentService.cs:338`, `GitHubReShadeAddonDeploymentService.cs:455`, `REFrameworkDeploymentService.cs:341,362`, `RenoDxUpgradeDeploymentService.cs:259` and `LumaFrameworkDeploymentService.cs:1047`.
  - Non-atomic whole-file rewrites in rollback paths: `ShortFuseDeploymentService.cs:458,461,471,481,487,499,504`; `RenoDxDeploymentService.cs:238,242,248,497,499`.
- **What happens:** After a power loss the renamed file can be empty or partial. The truncate-and-write rollbacks leave half-written game DLLs or INIs if interrupted.
- **Remediation seam:** One `AtomicFile.WriteAsync` / `CopyVerifiedAsync` helper (hash-verify, fsync the file, rename, fsync the directory). Delete the private copies.
- **Verification:** Grep gate in CI: forbid `File.WriteAllBytes*` and `File.Copy(..., overwrite: true)` targeting game or prefix paths outside the helper.

### DEP-13 — The record store re-reads and re-parses the whole JSON on every `Find`; `Decorate` does this about 39 times per refresh
- **Where:** `LinuxDeploymentRecordStore.cs:23-33` (each call goes to `LoadUnsafe` → `File.ReadAllText` + `Deserialize`). `LinuxManagedComponentService.Decorate` `:106-768` has 20 direct `_records.Find` calls plus one per component inside the loop (`:154`), about 19 components.
- **What happens:** Selecting a game triggers roughly 40 disk reads and full deserialisations. The cost grows linearly with the number of games and records.
- **Remediation seam:** Give `LinuxDeploymentRecordStore` an in-memory snapshot invalidated by mtime (or by its own writes). Better, have `Decorate` take a single `FindAll(game)` dictionary.
- **Verification:** Micro-benchmark or counter test: wrap the store to count `LoadUnsafe` calls in `Decorate` and assert at most one.

### DEP-14 — Hash and copy amplification in `TransactionalFileDeploymentService`
- **Where:** `src/RHI.Core/Services/TransactionalFileDeploymentService.cs:40,44,47,49,56,157,295-307`.
- **What happens:** For one target that already exists and has no backup, `ReplaceAsync` does:
  - `FilesEqual`: 2 full reads;
  - three `CopyVerifiedAsync` calls (snapshot, backup, temp): 3 reads + 1 write each;
  - post-verify: 2 reads.
  
  That totals about 13 full reads and 3 writes. `ReplaceBatchAsync` adds another verified snapshot per target (+3 reads, +1 write), which duplicates `currentSnapshot`. The source hash is recomputed each time. Callers then hash the target again for the record (e.g. `ReShadeDeploymentService.cs:333`, `OptiScalerDeploymentService.cs:290`).
- **Why it matters:** DLSS and Streamline DLLs run to tens of MB and batches span several folders, so a swap does hundreds of MB of avoidable I/O.
- **Remediation seam:** Hash the source once and pass it down. Copy while hashing (a hashing stream) instead of hash-copy-hash. In batch mode drop the per-item `currentSnapshot`, since the batch snapshot already exists. Return the target hash in `FileDeploymentResult`.
- **Verification:** Test that counts `FileStream` opens through an abstraction; a benchmark on a 100 MB file before and after.

### DEP-15 — Whole-file reads and naive scans in binary sniffing, repeated per probe
- **Where:**
  - `src/RHI.Core/Services/BinarySignatureInspector.cs:36-51`: reads up to 32 MiB and then materialises both a Latin-1 and a UTF-16 string, about 128 MB of transient allocation per call.
  - `src/RHI.Core/Services/ComponentProbe.cs:45-49,176-179`: called for every proxy-named DLL in the binary directory.
  - `ReShadeDeploymentService.cs:624-735`: `LooksLikeNativeD3DCompiler` does `File.ReadAllBytes` plus 4 needles × 2 encodings with an O(n·m) byte loop, and is called per candidate at `:576,584,594,602,639,649` and from `LinuxManagedComponentService.cs:1206` and `Dlss5FeederFullDeploymentService.cs:780,791`.
  - `LinuxManagedComponentService.cs:825,923,937`: `InspectDependency` re-runs the full `ComponentProbe.Probe` twice per dependency.
- **Remediation seam:** Use a streaming span search (`MemoryExtensions.IndexOf` over pooled windows with overlap) that returns on the first hit. Cache results by `(path, length, mtime)`. Pass the already-computed probe result into `InspectDependency`.
- **Verification:** A BenchmarkDotNet run on a 30 MB DLL; an allocation assertion via `GC.GetAllocatedBytesForCurrentThread` in a unit test.

### DEP-16 — No conditional requests, no release-lookup cache, and repeated downloads (GitHub rate limit)
- **Where:**
  - No `ETag`, `If-None-Match` or `If-Modified-Since` anywhere in `src/` (grep).
  - `GitHubReleaseAssetResolver.cs:35-78`: no cache.
  - Always re-download: `RenoDxDeploymentService.cs:575-592`, `GitHubReShadeAddonDeploymentService.cs:278-296`, `DofFixDeploymentService.cs:300-313`, `REFrameworkDeploymentService.cs:293-300`, `ReShadeAddonManagerService.cs:568-575` (GUID-named zip on every apply).
  - `src/RHI.Core/Services/DlssManagerService.cs:40-56` always fetches the manifest.
  - The background Update All loop (`src/RHI.Desktop/ViewModels/MainWindowViewModel.cs:1218-1260`) runs checks for every game and component.
- **What happens:** Unauthenticated users hit the 60 requests/hour GitHub API limit on libraries with more than a handful of managed games. Every install downloads again even when the cached payload's digest matches.
- **Remediation seam:** Put a small `HttpCache` (ETag plus body on disk, TTL) under the resolver. Before any download, reuse a cached payload whose sidecar digest matches the resolved digest.
- **Verification:** Fake handler test: two consecutive `CheckUpdateAsync` calls issue a conditional request; a 304 returns the cached result.

---

## Low

### DEP-17 — GitHub token stored in plain text with no permission check
- **Where:** `src/RHI.Desktop/App.axaml.cs:35` (`~/.config/rhi-linux/github_api.txt`); `src/RHI.Platform.Linux/Network/GitHubApiTokenHandler.cs:39-82` (never checks `File.GetUnixFileMode`).
- **What is correct:** The token is attached only to `https://api.github.com` (`:27-29`) and never overrides caller auth (`:29`). No logging exists in the layer, so no log leak was found.
- **Risk:** A world-readable file (e.g. created with the default umask 022) exposes the token to other local users.
- **Remediation seam:** Refuse or warn when mode `& 0o077 != 0`; optionally read from libsecret.
- **Verification:** Unit test: a token file with mode 0644 is ignored or produces a warning.
- **Status:** fixed in `df41791` (#36). The handler checks the mode on every use: group/other bits set → `chmod 600` before the token is sent; when that fails (not the owner) the token is ignored. One PT-BR/EN status notice per file version; contents never changed, token never logged.

### DEP-18 — External extractors: no timeout and no post-extraction containment or symlink check
- **Where:**
  - `ReShadeArchiveExtractor.cs:64-71` and `OptiScalerArchiveExtractor.cs:43-50`: no timeout, unlike `Dlss5FeederFullDeploymentService.cs:1336-1354` and `ReShadeShaderPackService.cs:650-667`.
  - `OptiScalerDeploymentService.cs:518-530`: copies every enumerated file, including symlinks, out of a 7z/bsdtar extraction.
- **ZIP handling is sound:** Every `ZipFile` path either extracts a fixed leaf name (`ReShadeArchiveExtractor.cs:24`, `DgVoodooDeploymentService.cs:143`, `src/RHI.Core/Services/DlssManagerService.cs:325,362`, `GitHubReShadeAddonDeploymentService.cs:340`, `ReShadeAddonManagerService.cs:588`, `REFrameworkDeploymentService.cs:318-320`, `UltimateAsiLoaderDeploymentService.cs:538-540`, `ShortFuseDeploymentService.cs:733-735`) or checks containment (`LumaFrameworkDeploymentService.cs:806-810`, `ReShadeShaderPackService.cs:614-623`, `Dlss5FeederFullDeploymentService.cs:1046-1049`). No zip-slip was found.
- **Remaining gap:** Containment of the 7z/bsdtar outputs relies entirely on the external tool.
- **Remediation seam:** Share one `ExternalArchiveRunner` with a timeout (DEP-S4). After extraction, reject any entry that is a symlink or resolves outside `outputDirectory`.
- **Verification:** Test with a crafted tar containing a symlink to `/etc/hostname`: the plan must reject it.

### DEP-19 — `GameKey` identity and absolute paths make records brittle across library moves
- **Where:** `LinuxDeploymentRecordStore.cs:56-57` (Steam app id, else the full install path); `ReShadeDeploymentService.cs:120-123` (update writes to the recorded `payload.Path`, not the current `BinaryDirectory`).
- **What happens:**
  - A Steam game moved to another library keeps its key but points at the old paths. Update writes there if that directory still exists, or fails otherwise.
  - A non-Steam game that is moved loses its records entirely.
- **Remediation seam:** Store paths relative to `BinaryDirectory` and re-base them on the current game on load. Detect `record.BinaryDirectory != game.BinaryDirectory` and require an explicit repair.
- **Verification:** Test: a record created under `/a/game` and a game now at `/b/game`; update must refuse with a repair message rather than write to `/a`.
- **Status:** fixed in `c72e9d4` (#38). Records and per-game stores key on `steam:<appid>`, then `heroic:<app>`, else the install path (`GameRecordIdentity`); older Heroic folder keys are read and migrated after each scan. A record outside the game's current folders blocks install/update/remove/update checks with a repair message (`DeploymentRelocation`, via `PatchTargetGuard`). "Repair after move" (header button, shown only when it applies) verifies every owned hashed file at the mapped path and then rebases the record in one atomic save; it refuses while the old folder still exists. Folder-keyed games are re-adopted by name plus verified files. Deviation: paths stay absolute (rebased by the explicit repair) rather than stored relative to `BinaryDirectory`, which would have touched every deployment service.

### DEP-20 — `DlssManagerService` cache hygiene
- **Where:** `src/RHI.Core/Services/DlssManagerService.cs`:
  - `:54`: the manifest cache is written in place (not atomic);
  - `:318,353`: fixed name `download.zip.part` (concurrent same-version downloads collide);
  - `:319-332,354-368`: the `.part` file is not deleted on failure;
  - `src/RHI.Core/Models/DlssManifest.cs:14-18`: the manifest carries no hash, so cached DLLs are reused if longer than 64 KB and readable (`:413-425`).
- **Remediation seam:** Unique temp names with `try/finally` delete; atomic manifest write; optional `sha256` field in the manifest, verified when present.
- **Verification:** Test: a download that throws midway leaves no `download.zip.part`.

### DEP-21 — `RestoreAsync` deletes an unowned `.rhi_custom` sidecar
- **Where:** `src/RHI.Core/Services/TransactionalFileDeploymentService.cs:115`. No other reference to `.rhi_custom` exists in `src/` (grep).
- **Risk:** A dead side effect on a file this layer never creates. Harmless today, but surprising.
- **Remediation seam:** Remove it, or document its owner.
- **Verification:** Grep plus a unit test asserting that restore touches only the target and its `.original`.

### DEP-22 — The Dlssg2030 doc claims a pinned commit, but the code resolves the latest commit at runtime
- **Where:** `Dlssg2030UnlockDeploymentService.cs:16-17` (doc: "pinned to an immutable Git commit") versus `:24,453-466` (`commits?path=version.dll&per_page=1`, i.e. the latest commit) and `:480-481` (raw URL at that SHA). There is no hash check beyond PE and INI validation.
- **Risk:** The payload changes whenever upstream pushes. Reproducibility and the trust anchor are weaker than documented.
- **Remediation seam:** Pin a constant SHA plus a SHA-256, or record and verify the downloaded blob's SHA-256 against the API's `sha` for the file.
- **Verification:** Unit test: the resolver ignores the API and uses the pinned constant.

### Positive notes (verified, no finding)
- **No shell injection:** Every `Process.Start` uses `UseShellExecute = false` plus `ArgumentList`: `D3DCompilerToolInstaller.cs:162-224`, `ReShadeArchiveExtractor.cs:37-62`, `OptiScalerArchiveExtractor.cs:20-41`, `Dlss5FeederFullDeploymentService.cs:1300-1321`, `ReShadeShaderPackService.cs:640-650`. The DFC password goes through stdin, not argv (`Dlss5FeederFullDeploymentService.cs:1327-1334`).
- **No hard-coded `http://` URL** exists in the scoped code.
- **Consistent hash-guarded removal:** "Modified outside RHI" guards exist in 11 files (27 sites).

---

## Structure

### DEP-S1 — About 15 near-identical `*DeploymentService` classes share one unextracted shape
- **Shared shape:**
  1. **Guard:** Native Linux, `BinaryDirectory`, `ExecutablePath`, PE architecture, prefix initialised. The "Native Linux" string check appears in 17 files.
  2. **Resolve:** GitHub or manifest release to (version, url, digest).
  3. **Fetch:** temp file, then optional digest, PE/size check and move into the cache. 14 private download routines.
  4. **Protect:** foreign-file check plus hash-guard of the existing managed files. 27 duplicated loops.
  5. **Deploy:** atomic copy (14 private `CopyAtomic`/`ReplaceAtomic`/`AtomicWrite` helpers, see DEP-12) plus an optional `EnsureNativeBuiltin`.
  6. **Record:** build a `LinuxDeploymentRecord` and `Upsert`.
  7. **Remove:** record, hash-guard, restore or delete, restore the override, remove the record.
  8. **Update:** check update or update.
- **Duplicated helpers:**
  - 20 `Sha256*` helper definitions in 19 files;
  - 9 `TryDelete` copies;
  - `HasReShade` duplicated verbatim (`GitHubReShadeAddonDeploymentService.cs:363-371` = `RenoDxDeploymentService.cs:594-602`);
  - four hand-rolled process runners (DEP-S4).
- **Measured size:** Classifying methods by name across the 17 `*DeploymentService*.cs` files (about 12.4k lines):

  | Family | Lines |
  |---|---|
  | Install | ~2,660 |
  | Remove | ~1,010 |
  | Download / resolve | ~1,010 |
  | Check / update | ~700 |
  | Eligibility | ~340 |
  | Small helpers | ~330 |
  | **Total** | **≈6.1k** |

- **Estimate:** A composition-based kit would remove roughly **2.0–2.8k lines (15–20 % of the layer)**. The kit would be:
  - `ComponentGuard`;
  - `ReleasePayloadFetcher` with an integrity policy (DEP-07);
  - `OwnedFileSet` (plan → deploy through `IFileDeploymentService` → record → hash-guarded remove);
  - `WineOverrideLease`;
  - `AtomicFile`.
  
  Most of the saving comes from download, remove, helpers and guards. Install bodies stay component-specific. This is an estimate from method-level counts, not a prototype.
- **Remediation seam:** Start with the lowest-risk extraction, i.e. `Sha256`/`TryDelete`/`AtomicFile`/`HasReShade` into one internal static class. Then build `ReleasePayloadFetcher` and migrate the four GitHub-add-on-shaped services first (GitHubReShadeAddon, DofFix, RenoDxUpgrade, REFramework).
- **Verification:** The existing 746 tests must stay green after each migration step; also diff the per-service line counts.

### DEP-S2 — `LinuxManagedComponentService` is a god class (1,790 lines)
- **Where:** 22 constructor dependencies (`:32-54`). `Decorate` alone spans `:106-768` (about 660 lines, one `foreach` holding per-kind `if` cascades, with Portuguese and English user-facing strings mixed into backend logic). Dependency orchestration sits at `:782-1070`, and about 60 thin façade methods at `:1089-1775`.
- **Why it matters:** Every new component forces edits in `Decorate`, `DependencyConflictGuard`, `InstallCoreAsync`, `RemoveAsync`, `DisplayDependencyName` and the update switch (shotgun surgery). It is also untestable in isolation: no test calls `Decorate` (grep).
- **Remediation seam:**
  - `IComponentPolicy` per `ManagedComponentKind` (Decorate, conflict and dependency rules), registered in a dictionary;
  - `ManagedDependencyOrchestrator` (`:782-1026`) as its own class;
  - UI strings moved to the localisation layer;
  - the façade methods move to the Desktop view-model adapters.
- **Verification:** Golden tests on `Decorate` output for a matrix of (records × probe) fixtures, captured before the split.

### DEP-S3 — `Dlss5FeederFullDeploymentService` is a god class (1,618 lines)
- **Where:** `InstallCoreAsync` `:155-610` (about 455 lines in one method), plus in the same file:
  - DFC package verification: `:1155-1280`, `:1376-1400`;
  - external 7z runner: `:1286-1374`;
  - INI token editing: `:1409-1437`;
  - shader-dependency extraction: `:1030-1055`;
  - host64 orchestration: `:278-279,430-434`.
- **Remediation seam:** Split into `FeederLayoutPlanner`, `DeepFriedChickenPackageVerifier`, `FeederShaderStackInstaller` and `ReShadeIniTokenEditor`. Reuse `OwnedFileSet` (DEP-S1) for the copy, record and rollback loop.
- **Verification:** The existing `Dlss5Feeder*Tests` and `FeederConflictRegressionTests` stay green; `InstallCoreAsync` ends up under 100 lines.

### DEP-S4 — Four copies of the external-process runner
- **Where:** `ReShadeArchiveExtractor.cs:37-71`, `OptiScalerArchiveExtractor.cs:20-50`, `Dlss5FeederFullDeploymentService.cs:1286-1374`, `ReShadeShaderPackService.cs:640-675` (plus `D3DCompilerToolInstaller.cs:113-159`). Two have timeouts and kill the process tree; two do not.
- **Remediation seam:** One `ExternalArchiveRunner.ExtractAsync(archive, dest, members?, password?, timeout)` with a containment check (DEP-18).

### DEP-S5 — Three independent ownership ledgers with different durability
- **Where:**
  - `deployments.json` (`LinuxDeploymentRecordStore.cs:20`);
  - `addon-manager.json` (`ReShadeAddonManagerService.cs:102,748-764`);
  - per-game `reshade-shaders/RHI/.rhi-manifest.json` (`ReShadeShaderPackService.cs:364-373`);
  - the `.original` sidecar convention.
- **Why it matters:** Cross-ledger operations (e.g. ReShade removal cleaning shader packs, `ReShadeDeploymentService.cs:470-471`) cannot be atomic, and each ledger has its own failure semantics (DEP-01, DEP-09).
- **Remediation seam:** Fold add-on-manager state into `LinuxDeploymentRecord` (component `ReShadeAddons`). Keep the shader manifest but reference it from the ReShade record.

---

## Test blind spots (tests/RHI.Core.Tests, grep-verified)

| Risky path | Evidence of gap |
|---|---|
| Record store corruption → wipe (DEP-01) | `LinuxDeploymentRecordStoreTests.cs` has one test (`:13`, metadata normalisation). Nothing for corrupt JSON, a missing directory, or concurrent instances. |
| TFDS rollback failure (DEP-02) | `TransactionalFileDeploymentTests.cs` has 6 tests (`:12,33,55,77,100,118`). The batch rollback test (`:33`) fails before any write to the second target. None forces a rollback copy to fail, and none covers `RestoreBatch` failing after a successful earlier target. |
| Crash between mutation and `Upsert` (DEP-03) | No fault-injection test anywhere. No orphan `.rhi-*` sweep exists to test. |
| Cancellation rollback (DEP-04) | No `Cancel` test in `OptiScalerDeploymentServiceTests.cs`, `RenoDxDeploymentServiceTests.cs` or `TransactionalFileDeploymentTests.cs`. |
| `user.reg` concurrency and ShortFuse registry restore (DEP-05) | `WineDllOverrideServiceTests.cs` has 5 tests (`:11,29,49,68,92`). None covers a running prefix, CRLF input, `BackupRegistryOnce`, or a user-modified value on restore (DEP-11). |
| URL trust (DEP-06/07) | `IsTrustedArchiveUrl`, `IsTrustedFiloppiUrl` and `DlssManagerService.ValidateDownloadUrl` have no tests (grep returns nothing). No test feeds `http://` or a foreign host into the RenoDX catalog, wiki or manifest. |
| Missing-digest policy (DEP-07) | No test asserts behaviour when the release JSON lacks `digest` (OptiScaler `IsUsableArchiveAsync` returning `true`, the DofFix override dropping the hash). |
| ReShade Addon Manager apply and rollback (DEP-09) | No test calls `ApplySelectionAsync` (grep). `ShaderAddonManagerTests.cs` covers only parsing and catalogue. |
| RenoDX foreign-file overwrite (DEP-10) | No test with an existing non-`renodx*` target name. |
| Real 7z/bsdtar extraction and containment (DEP-18) | Only fakes: `ReShadeChannelTests.cs:562`, `ReShadeDeploymentServiceTests.cs:245`, `OptiScalerPackageProviderTests.cs:158`. No symlink or traversal test for external extraction. |
| `LinuxManagedComponentService.Decorate` and dependency rollback | No test calls `Decorate(` (grep). `NeuralRenderingSwitchTests.cs` covers static helpers only. `RollBackAutoDependenciesAsync` has no direct test. |
| Feeder shader-dependency archive traversal (`Dlss5FeederFullDeploymentService.cs:1046-1049`) | No traversal or escape test in `Dlss5Feeder*Tests.cs` (grep). Zip-slip *is* covered for shader packs (`ReShadeShaderPackServiceTests.cs:275`) and Luma (`LumaFrameworkDeploymentServiceTests.cs:296`). |
| GitHub token handler | 3 tests (`GitHubApiTokenHandlerTests.cs:10,36,59`). None covers `http://api.github.com`, file mode, or reload on change. |
| Performance regressions (DEP-13–16) | No allocation, I/O-count or HTTP-count assertions exist. |

---

## Overall verdict

1. **Ownership-first design:** The layer is careful about foreign files: hash guards, refusal to adopt external DLLs, and `.original` sidecars are applied consistently, and no ZIP zip-slip or shell injection was found.
2. **Two weak points:** The durability of the record store (DEP-01: one corrupt read wipes every ownership record) and rollback and crash semantics (DEP-02/03/04). Both undermine that safety model precisely when something goes wrong.
3. **Registry handling:** `user.reg` is edited without knowing whether Wine owns it, and without a lock. ShortFuse's truncate-and-write restore is the sharpest data-loss edge outside the game directory.
4. **Supply-chain trust is inconsistent:** Some paths enforce HTTPS, host and SHA-256 (DLSS, dgVoodoo, Luma, RTX40MFG). RenoDX, the Addon Manager, ReShade, DOF Fix overrides and digest-less GitHub assets deploy DLLs on TLS plus an MZ check alone.
5. **Fix order:** First fail-closed plus journalled records, then `CancellationToken.None` rollbacks, then a single `TrustedDownload` + `AtomicFile` + `OwnedFileSet` kit. That kit also removes an estimated 2–3k duplicated lines and the god-class pressure.
