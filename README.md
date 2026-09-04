# RHI Linux — Native Linux Port

Native Linux port of [RankFTW/RHI](https://github.com/RankFTW/RHI), preserving the upstream workflow and component model while replacing WinUI / Windows App SDK and Windows-only platform integration with Linux-aware services for **Steam/Proton, Heroic, Lutris and standalone Wine/Bottles**.

> **Current status: v0.6.7 — RHI Windows 2.5.9 upstream-sync foundation**
>
> The v0.6.6 CachyOS/Proton field regression is the current accepted ReShade/D3DCompiler baseline. Streamline and DLSS **Super Resolution**, **Ray Reconstruction** and **Frame Generation** have also completed real Apply → launch → Restore validation with byte-safe restoration.
>
> v0.6.7 adds the lifecycle infrastructure required by newer RHI 2.5.9 components: manifest-aware add-on metadata, dynamic GitHub release asset resolution, stable deployment filenames and persistent shader-pack exclusions.
>
> **DLSS5 Feeder, DX11 Bridge and MFG Ada Unlock are not exposed in the Linux UI yet. Neural Rendering remains available at the DLSS backend level, but its real-world acceptance gate is still deferred.**

---

## Why a native Linux port?

Upstream RHI is a Windows application built around WinUI / Windows App SDK, Windows Registry integration and Windows-specific NVIDIA APIs.

RHI Linux keeps the portable .NET/core concepts while moving the desktop application to **Avalonia** and implementing Linux-specific platform services for:

* Steam and Proton;
* Heroic and Legendary;
* Heroic sideload/custom games;
* Lutris;
* standalone Wine;
* Bottles-style Wine prefixes;
* Linux filesystem and desktop integration.

Windows-only behavior is not silently emulated. Features without a real Linux equivalent remain explicitly unavailable.

---

## Validated real-world baseline

The following functionality has been exercised on **CachyOS + Proton** and forms the current regression baseline:

* Native .NET 8 / Avalonia Linux application.
* Steam, Heroic, Lutris and Wine/Bottles discovery.
* Native Linux vs Wine/Proton runtime classification.
* Background/cancellable game probing.
* Conservative graphics API detection.
* DLSS transactional replacement and restoration.
* ReShade full-add-on installation/removal.
* Exact per-game Wine Registry override cleanup.
* RenoDX installation/removal with ReShade dependency ordering.
* OptiScaler installation/removal on supported 64-bit DX11/DX12 Wine/Proton games.
* ReShade + OptiScaler coexistence.
* ReShade + RenoDX + OptiScaler coexistence.
* Both validated safe teardown orders while preserving the surviving component chain.
* ReShade shader packs with native Microsoft `D3DCompiler_47.dll`.
* ReLimiter and Display Commander deployment/removal.
* Heroic Legendary/Epic discovery.
* Heroic sideload/custom-game discovery.
* Streamline Apply / launch / Restore.
* DLSS Super Resolution Apply / launch / Restore.
* DLSS Ray Reconstruction Apply / launch / Restore.
* DLSS Frame Generation Apply / launch / Restore.
* Byte-for-byte verified `.original` restoration.
* Linux NVIDIA cache permission repair.
* ReShade third-party shader core-header dependency handling.

---

## Component status

| Component                   | Status                 | Notes                                                     |
| --------------------------- | ---------------------- | --------------------------------------------------------- |
| **ReShade**                 | ✅ Available            | Stable, Nightly, Legacy and Custom profiles               |
| **ReShade shader packs**    | ✅ Available            | Initial isolated Linux-safe subset                        |
| **RenoDX**                  | ✅ Available            | ReShade dependency ordering enforced                      |
| **OptiScaler**              | ✅ Available            | Validated on supported x64 DX11/DX12 Wine/Proton titles   |
| **DLSS Super Resolution**   | ✅ Validated            | Transactional Apply / Restore                             |
| **DLSS Ray Reconstruction** | ✅ Validated            | Transactional Apply / Restore                             |
| **DLSS Frame Generation**   | ✅ Validated            | Transactional Apply / Restore                             |
| **DLSS Neural Rendering**   | ⚠️ Backend present     | Real-world acceptance remains deferred                    |
| **Streamline**              | ✅ Validated            | Multi-DLL / multi-folder transactional Apply / Restore    |
| **ReLimiter**               | ✅ Available            | ReShade add-on; mutually exclusive with Display Commander |
| **Display Commander**       | ✅ Available            | ReShade add-on; mutually exclusive with ReLimiter         |
| **Luma**                    | 🔒 Not yet ported      | Future auxiliary-component gate                           |
| **REFramework**             | 🔒 Not yet ported      | Future auxiliary-component gate                           |
| **DOF Fix**                 | 🔒 Not yet ported      | Future auxiliary-component gate                           |
| **DLSS5 Feeder**            | 🔒 Infrastructure only | Not exposed in v0.6.7                                     |
| **DX11 Bridge**             | 🔒 Infrastructure only | Not exposed in v0.6.7                                     |
| **MFG Ada Unlock**          | 🔒 Infrastructure only | Not exposed in v0.6.7                                     |
| **NVIDIA DRS controls**     | 🚫 Unsupported         | No fake Windows NvAPI/DRS emulation                       |

---

## ReShade profiles

RHI Linux supports the larger upstream-style ReShade channel workflow.

### Stable

Current full-add-on setup obtained from ReShade's official distribution source.

### Nightly

Current 32/64-bit artifact from the upstream ReShade development build workflow.

### Legacy

Explicit pinned numeric versions such as:

```text
6.4.1
```

Legacy profiles remain pinned instead of being automatically upgraded.

### Custom

A user-selected local ReShade DLL.

Before injection, RHI validates that the DLL:

* is a valid PE file;
* has a reasonable size;
* matches the detected game architecture;
* contains recognizable ReShade identification.

The installed ReShade channel/source is recorded per game.

---

## Conservative `ReShade.ini` management

RHI Linux does not replace an existing `ReShade.ini` wholesale.

When **Manage / merge ReShade.ini** is enabled, RHI records only the values and search-path tokens it owns.

On teardown:

* RHI-owned tokens can be removed;
* unrelated user configuration is preserved;
* user-edited files are not silently deleted;
* a file created entirely by RHI may be removed only when its managed state still matches what RHI created.

---

## Shader packs

The first Linux-native shader-pack subset currently includes:

* crosire `reshade-shaders`;
* crosire legacy shaders;
* clshortfuse ReShade shaders;
* MaxG2D Simple HDR Shaders.

Managed content is isolated below:

```text
reshade-shaders/RHI/<Pack>/Shaders
reshade-shaders/RHI/<Pack>/Textures
```

Per-game hashes are recorded so externally modified files are not silently overwritten or deleted.

### ReShade core-header dependency

Real-world testing found that clshortfuse and MaxG2D effects require the standard:

```text
ReShade.fxh
ReShadeUI.fxh
```

even when the full crosire shader pack is not selected.

Since v0.6.6, RHI automatically stages only those required headers when one of the dependent packs is selected:

```text
reshade-shaders/RHI/_Core/Shaders/ReShade.fxh
reshade-shaders/RHI/_Core/Shaders/ReShadeUI.fxh
```

The `_Core` search path is appended after pack-local paths.

These files remain hash-managed and are removed only when they still match the RHI-owned copy.

---

## Native `D3DCompiler_47.dll` handling

Real-world Wine/Proton validation showed that ReShade itself could load correctly while some effects failed to compile using Wine's built-in `d3dcompiler_47`.

Using a native Microsoft `D3DCompiler_47.dll` with:

```text
d3dcompiler_47=native,builtin
```

resolved those shader compilation failures.

RHI Linux therefore provides a conservative compatibility workflow when managed shader packs are selected.

It can:

* detect a native Microsoft compiler beside the game executable;
* detect a suitable compiler already present in the Wine/Proton prefix;
* accept a user-selected Microsoft DLL through an Avalonia **Browse…** picker;
* validate PE architecture and Microsoft/HLSL compiler identity;
* deploy a selected DLL application-local transactionally;
* manage `d3dcompiler_47=native,builtin` per executable;
* track the D3DCompiler override independently from the ReShade proxy override;
* restore only state actually created by RHI;
* preserve external/native compiler files;
* refuse to replace an RHI-managed compiler whose hash changed externally.

If no valid native compiler exists, shader-pack deployment is blocked before ReShade modification.

For Steam titles RHI can provide an AppID-specific hint such as:

```fish
protontricks <AppId> d3dcompiler_47
```

**RHI Linux does not bundle or redistribute Microsoft's `D3DCompiler_47.dll`.**

---

## ReLimiter and Display Commander

RHI Linux supports two upstream auxiliary ReShade add-ons:

### ReLimiter

Uses the appropriate:

```text
relimiter.addon64
relimiter.addon32
```

payload for the detected executable architecture.

### Display Commander

Uses the appropriate:

```text
zzz_display_commander.addon64
zzz_display_commander.addon32
```

payload.

Both implementations:

* require ReShade full-add-on support;
* respect `[ADDON] AddonPath` from `ReShade.ini`;
* select x86/x64 assets automatically;
* validate downloaded PE payloads;
* verify a published SHA-256 digest when available;
* refuse to overwrite unknown external add-on files;
* hash-guard managed updates/removal;
* block ReShade removal while an RHI-managed dependent add-on remains installed.

ReLimiter and Display Commander are intentionally **mutually exclusive**, matching upstream RHI behavior.

Since v0.5.1, configuration/support files such as:

```text
relimiter.ini
DisplayCommander.ini
*.log
*.json
```

do **not** count as installed add-ons.

Only actual `.addon64` / `.addon32` binaries determine installed state, allowing user configuration files to remain untouched after binary teardown.

---

## DLSS and Streamline

RHI Linux consumes the upstream RHI DLSS manifest for game-local DLSS and Streamline file management.

The UI exposes the complete component names:

* **Super Resolution**
* **Ray Reconstruction**
* **Frame Generation**
* **Neural Rendering**
* **Streamline**

### Existing-file-only rule

RHI never invents a DLSS or Streamline target layout.

Apply only touches DLL paths that already exist and are recognized.

For Streamline, only known existing `sl.*.dll` modules inside detected game-local Streamline folders are eligible.

A downloaded package containing additional modules does not cause RHI to create them in the game.

---

## Transactional `.original` safety

File replacement is designed around preservation of the real game file.

On a genuine replacement:

1. the original file is preserved as `.original`;
2. replacement staging is performed safely;
3. copies are SHA-256 verified;
4. multi-file operations act as a transaction;
5. a failed operation rolls back to the pre-operation state.

Since v0.6.3, source and target are SHA-256 compared before modification.

If they are already identical:

* Apply becomes a no-op;
* no `.original` backup is created;
* the UI reports that no change was required.

Restore does **not** consume `.original` before verification.

The target must match the backup SHA-256 after restoration before the backup may be removed. If restoration fails, RHI keeps the `.original` and restores the previously active target from a transaction snapshot.

---

## Linux DLSS / Streamline cache repair

Real CachyOS testing discovered NVIDIA payloads in RHI's own cache with Unix mode:

```text
0010
------x---
```

The files existed but could not be read by the normal user.

### v0.6.4

RHI now:

* normalizes relevant cached NVIDIA DLLs to `0644`;
* verifies an actual readable file handle before cache reuse;
* normalizes newly extracted DLLs immediately;
* retries by downloading the payload again if an unreadable entry cannot be repaired.

Only RHI's cache is modified. Game DLL permissions are not changed.

### v0.6.5

A proactive startup migration now scans only the RHI-owned NVIDIA cache namespaces:

```text
DLSS
DLSS-D
DLSS-G
DLSS-NR
Streamline
```

Recognized NVIDIA DLL payloads are normalized to:

```text
0644
rw-r--r--
```

ReShade, RenoDX, OptiScaler, auxiliary add-on caches and unrelated files are not touched by this sweep.

---

## Heroic / Legendary / sideload discovery

### Legendary and Epic

RHI no longer assumes Heroic's Epic metadata is stored only inside the Heroic configuration directory.

Supported layouts include native Legendary metadata such as:

```text
~/.config/legendary/installed.json
```

as well as Heroic-managed and Flatpak Legendary layouts.

The detector also:

* parses nested Heroic `install` objects;
* uses the registered executable when present;
* ignores records explicitly marked `is_installed=false`;
* preserves the actual Heroic app identity;
* reads exact per-game `GamesConfig` `winePrefix` configuration before using fuzzy fallback matching.

### Heroic sideload

RHI also understands Heroic custom/sideload entries such as:

```text
heroic://launch?appName=<id>&runner=sideload
```

It reads the current sideload library:

```text
~/.config/heroic/sideload_apps/library.json
```

while retaining compatibility with the older nested layout.

Supported sideload metadata includes:

* `app_name`;
* `title`;
* `folder_name` / `folderName`;
* nested `install.executable`;
* absolute executable paths;
* exact `GamesConfig/<appName>.json` Wine-prefix association.

RHI deliberately does **not** enumerate every directory inside Heroic's prefix directory as a game, because that would create false positives such as shared or utility prefixes.

Discovery remains metadata-backed.

---

## Open Folder / Open Prefix

Paths containing spaces are handled as one argument through `.NET ProcessStartInfo.ArgumentList`.

RHI also provides Linux desktop opener fallbacks including:

```text
xdg-open
gio open
kde-open6
kioclient6 exec
kde-open5
```

The same handling is used for both **Open Folder** and **Open Prefix**.

---

## v0.6.7 — RHI Windows 2.5.9 lifecycle foundation

The current upstream compatibility target is:

```text
Project: RankFTW/RHI
Tag: RHI-2.5.9
Release: v2.5.9
Baseline commit: c9eba5d7d728228402655580103a43ec1550ad60
```

RHI Windows 2.5.7–2.5.9 introduced lifecycle changes that matter to the Linux port:

1. add-on release asset filenames cannot always be assumed to remain fixed;
2. newer add-on metadata is driven by `manifest.json` → `addonPacks`;
3. shader-pack exclusions must persist across refresh/restart for newer DLSS5 Feeder orchestration.

v0.6.7 ports the shared Linux-safe infrastructure first.

### Dynamic GitHub release resolver

`GitHubReleaseAssetResolver` can consume:

```text
/releases/latest
```

or release-list responses such as:

```text
/releases?per_page=100
```

It supports:

* exact filename matching;
* regular-expression filename matching;
* release-list fallback;
* draft skipping;
* release version extraction;
* GitHub SHA-256 digest propagation when published.

The resolver is payload-agnostic so later gates can use either direct `.addon64/.addon32` files or dynamically named ZIP archives.

### Stable deployment filenames

`GitHubReShadeAddonDeploymentService` can now distinguish between:

* the changing filename of a downloaded GitHub release asset;
* the stable filename that should exist inside the game.

### Upstream add-on manifest reader

`UpstreamRhiManifestService` currently parses the RHI 2.5.9 `addonPacks` fields required by future Linux gates:

```text
packageName
description
downloadUrl
downloadUrl32
downloadUrl64
repositoryUrl
effectInstallPath
deployFileName
releaseApiUrl
disabled
```

The compatibility source is pinned to the `RHI-2.5.9` tag for reproducible behavior.

### Persistent shader-pack exclusions

`ReShadeShaderPackService` now persists per-pack shader exclusions in:

```text
.pack-settings.json
```

Exclusions:

* survive refresh/restart;
* use case-insensitive shader filename matching;
* apply to later shader synchronization;
* do not filter textures;
* survive per-game ReShade teardown.

This infrastructure is required before enabling the newer DLSS5 Feeder lifecycle safely.

### Not enabled by v0.6.7

v0.6.7 does **not** yet:

* automatically install a Neural Rendering workflow;
* expose DLSS5 Feeder;
* expose DX11 Bridge;
* expose MFG Ada Unlock;
* execute upstream PowerShell installers through Wine;
* copy Windows NvAPI/NVIDIA-profile behavior;
* claim a Neural Rendering real-world PASS.

Those remain separate feature gates.

---

## Safety invariants

RHI Linux follows several non-negotiable deployment rules:

1. Unknown/external proxy DLLs are never silently overwritten or adopted.
2. Managed binary removal/update remains hash guarded.
3. Custom ReShade DLLs remain architecture/signature validated.
4. RHI-managed shaders stay isolated below `reshade-shaders/RHI`.
5. User-modified shader and INI content is preserved.
6. A user-selected native D3DCompiler must match the target architecture and Microsoft/HLSL identity.
7. An external D3DCompiler may be used without becoming RHI-owned.
8. An RHI-deployed D3DCompiler is hash-tracked and restored/removed transactionally.
9. Its Wine override is managed independently from the ReShade proxy override.
10. ReShade + RenoDX + OptiScaler coexistence remains a regression invariant.
11. Native Linux games do not receive Windows DLL injection.
12. A missing DLSS/Streamline target is never invented.
13. Failed multi-file deployment restores the previous state.
14. RHI does not present a Windows DLL proxy as a native Vulkan layer.
15. Windows-only NvAPI/DRS functionality is not represented as equivalent Linux functionality.

---

## Anti-cheat / online warning

ReShade full-add-on mode, RenoDX, OptiScaler and similar DLL-injection workflows modify or inject code into the Windows game process.

Use these workflows with **single-player/offline titles** unless the specific game's anti-cheat policy explicitly allows them.

**RHI Linux does not bypass anti-cheat.**

RHI Linux is a third-party port/manager and is not an official ReShade, RenoDX, OptiScaler, NVIDIA, Valve, Heroic or Lutris application.

---

## CachyOS / Arch quick start

Install the required build tools:

```fish
sudo pacman -S --needed dotnet-sdk-8.0 7zip git
```

Clone the repository:

```fish
git clone https://github.com/wizardkof/RHI-Linux.git
cd RHI-Linux
```

Restore and test:

```fish
dotnet restore RHI-Linux.sln
dotnet test RHI-Linux.sln
```

Run:

```fish
./run.fish
```

Bash users can use:

```bash
./run.sh
```

---

## Build

Fish:

```fish
./build.fish
```

Bash:

```bash
./build.sh
```

Build output is written to:

```text
artifacts/linux-x64/
```

---

## Validation status

The v0.6.7 source contains **155 statically counted xUnit cases**:

```text
125 [Fact]
30  [InlineData] theory rows
155 total
```

The v0.6.6 real-world CachyOS regression is accepted.

The current v0.6.7 upstream-sync infrastructure still requires its authoritative:

```text
dotnet test RHI-Linux.sln
```

plus a normal UI smoke launch on CachyOS before the v0.6.7 lifecycle foundation is promoted to the stable accepted baseline.

---

## Still locked / not parity-complete

The Linux port intentionally does not claim full RHI parity yet.

Still pending:

* DLSS5 Feeder deployment and shader orchestration.
* DX11 Bridge.
* MFG Ada Unlock.
* Neural Rendering real-world acceptance.
* Broader RenoDX parity and per-game manifest behavior.
* ReLimiter / Display Commander advanced INI and preset parity.
* Luma.
* REFramework.
* DOF Fix.
* Additional auxiliary upstream components.
* Full upstream shader-pack catalog/import UI.
* Deeper Streamline/custom configuration parity.
* Native Vulkan ReShade-layer deployment.
* Automatic Vulkan OptiScaler topology.
* Full remote-manifest / Update All parity.
* Background updater.
* Complete settings parity.
* Tray / compact-mode parity.
* Finished release packaging.
* Linux HDR/compositor integration.
* NVIDIA functionality only where the Linux driver stack provides a real verifiable equivalent.

---

## Architecture

```text
RHI-Linux
├── src/RHI.Core
│   ├── Models
│   └── Services
├── src/RHI.Platform.Linux
│   ├── Detection
│   ├── Deployment
│   ├── Launch
│   └── Paths
├── src/RHI.Desktop
│   ├── ViewModels
│   └── Views
├── tests/RHI.Core.Tests
└── packaging
```

The architectural boundary is deliberate:

* cross-platform component/manifests logic belongs in `RHI.Core`;
* Linux discovery, Wine/Proton, deployment and filesystem integration belong in `RHI.Platform.Linux`;
* the desktop frontend belongs in `RHI.Desktop`.

Windows Registry APIs, WinUI and NvAPI must not leak into the cross-platform core.

---

## Upstream relationship

Current compatibility baseline:

```text
RankFTW/RHI
RHI-2.5.9
v2.5.9
```

RHI Linux intentionally mirrors Linux-relevant upstream behavior where it can be implemented safely, including:

* ReShade proxy conventions;
* full-add-on ReShade staging;
* architecture-aware payload selection;
* protection of occupied/external proxies;
* ReShade/OptiScaler coexistence;
* RenoDX catalog concepts;
* normalized title matching;
* ReShade add-on path handling;
* game-local DLSS/Streamline manifest usage.

Linux-specific divergence is intentional where required:

* Avalonia replaces WinUI / Windows App SDK.
* Linux launcher metadata replaces Windows storefront Registry discovery.
* Wine/Proton DLL overrides are managed per application.
* native Linux targets do not receive Windows DLL injection.
* unsupported NvAPI/DRS functionality is not faked.

RHI Linux is a derivative Linux port, not a claim that the original WinUI executable itself is cross-platform.

---

## License

RHI Linux is a derivative of the GPLv3-licensed [RankFTW/RHI](https://github.com/RankFTW/RHI) project and is distributed under **GPLv3**.

See:

```text
LICENSE
UPSTREAM.md
```

Third-party runtime binaries are not bundled in the source package.

ReShade, RenoDX, OptiScaler, shader content and supported auxiliary components are obtained from their respective runtime/upstream sources as required.

Microsoft `D3DCompiler_47.dll` is **never bundled or redistributed by RHI Linux**.
