<div align="center">

# RHI Linux

### Native Linux port of RHI for Steam / Proton, Heroic, Lutris and Wine

[![Version](https://img.shields.io/badge/RHI%20Linux-v0.6.7-0ea5e9?style=for-the-badge)](https://github.com/wizardkof/RHI-Linux)
[![Upstream](https://img.shields.io/badge/Upstream%20RHI-2.5.9-6366f1?style=for-the-badge)](https://github.com/RankFTW/RHI)
[![Platform](https://img.shields.io/badge/Platform-Linux-f59e0b?style=for-the-badge\&logo=linux\&logoColor=white)](https://github.com/wizardkof/RHI-Linux)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge\&logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-8b5cf6?style=for-the-badge)](https://avaloniaui.net/)
[![License](https://img.shields.io/github/license/wizardkof/RHI-Linux?style=for-the-badge)](LICENSE)

**A native Linux reimplementation of [RankFTW/RHI](https://github.com/RankFTW/RHI), preserving the upstream workflow and component model while replacing WinUI, Windows App SDK and Windows-only platform integration with Linux-aware services.**

<br>

[Features](#-current-feature-set) ·
[Status](#-project-status) ·
[Quick Start](#-quick-start) ·
[ReShade](#-reshade) ·
[DLSS / Streamline](#-dlss--streamline) ·
[Safety](#-safety-model) ·
[Architecture](#-architecture) ·
[Roadmap](#-still-locked--roadmap)

</div>

---

## 🚀 Project status

|                                   |                                          |
| --------------------------------- | ---------------------------------------- |
| **RHI Linux**                     | `v0.6.7`                                 |
| **Upstream compatibility target** | `RHI 2.5.9`                              |
| **Current upstream tag**          | `RHI-2.5.9`                              |
| **Primary validated environment** | CachyOS + Proton                         |
| **Runtime**                       | .NET 8                                   |
| **Desktop UI**                    | Avalonia                                 |
| **Current regression baseline**   | v0.6.6 accepted                          |
| **v0.6.7 gate**                   | 🟡 Compile / xUnit + UI smoke validation |
| **Static test cases**             | `155`                                    |

> [!IMPORTANT]
> **v0.6.7 is an upstream lifecycle foundation release.**
>
> It adds the infrastructure required by newer RHI 2.5.9 components, but **DLSS5 Feeder, DX11 Bridge and MFG Ada Unlock are not exposed in the Linux UI yet.**

### Real-world validation completed

The following paths have completed real CachyOS / Proton testing:

* ✅ ReShade installation / removal
* ✅ RenoDX dependency chain
* ✅ OptiScaler installation / teardown
* ✅ ReShade + OptiScaler coexistence
* ✅ ReShade + RenoDX + OptiScaler coexistence
* ✅ Native Microsoft `D3DCompiler_47.dll` shader workflow
* ✅ ReLimiter
* ✅ Display Commander
* ✅ Heroic / Legendary discovery
* ✅ Heroic sideload discovery
* ✅ DLSS Super Resolution
* ✅ DLSS Ray Reconstruction
* ✅ DLSS Frame Generation
* ✅ Streamline
* ✅ Transactional `.original` restoration
* ✅ SHA-256 byte verification
* ✅ NVIDIA cache permission repair
* ✅ ReShade third-party core-header dependency handling

**Neural Rendering remains deferred for a suitable real-world acceptance gate.**

---

# ✨ Current feature set

## Component matrix

| Component                       | State | Current Linux support                           |
| :------------------------------ | :---: | :---------------------------------------------- |
| **ReShade**                     |   ✅   | Stable / Nightly / Legacy / Custom              |
| **ReShade shader packs**        |   ✅   | Initial isolated Linux-safe pack subset         |
| **RenoDX**                      |   ✅   | Install / remove + ReShade dependency ordering  |
| **OptiScaler**                  |   ✅   | Supported x64 DX11 / DX12 Wine / Proton games   |
| **DLSS Super Resolution**       |   ✅   | Apply / Restore validated                       |
| **DLSS Ray Reconstruction**     |   ✅   | Apply / Restore validated                       |
| **DLSS Frame Generation**       |   ✅   | Apply / Restore validated                       |
| **DLSS Neural Rendering**       |   🟡  | Backend present; field gate deferred            |
| **Streamline**                  |   ✅   | Multi-DLL / multi-folder transactional workflow |
| **ReLimiter**                   |   ✅   | Native RHI-managed ReShade add-on               |
| **Display Commander**           |   ✅   | Native RHI-managed ReShade add-on               |
| **Luma**                        |   🔒  | Future component gate                           |
| **REFramework**                 |   🔒  | Future component gate                           |
| **DOF Fix**                     |   🔒  | Future component gate                           |
| **DLSS5 Feeder**                |   🧱  | v0.6.7 infrastructure only                      |
| **DX11 Bridge**                 |   🧱  | v0.6.7 infrastructure only                      |
| **MFG Ada Unlock**              |   🧱  | v0.6.7 infrastructure only                      |
| **Native Vulkan ReShade layer** |   🔒  | No fake Windows-DLL implementation              |
| **NVIDIA DRS controls**         |   🚫  | No equivalent Linux backend available           |

Legend:

* ✅ **Available / validated**
* 🟡 **Implemented but gate incomplete**
* 🧱 **Foundation implemented; feature not exposed**
* 🔒 **Not ported yet**
* 🚫 **Intentionally unsupported**

---

# 🐧 Why a native Linux port?

The upstream RHI desktop application targets Windows technologies including:

* WinUI
* Windows App SDK
* Windows Registry
* Windows-specific launcher discovery
* NvAPI / NVIDIA DRS

Simply installing .NET inside Wine does not turn that application into a native Linux program.

RHI Linux instead keeps the portable RHI concepts while moving platform-specific functionality behind Linux implementations.

### Linux integrations currently include

* **Steam**
* **Proton**
* **Heroic**
* **Legendary**
* **Heroic sideload**
* **Lutris**
* **standalone Wine**
* **Bottles-style prefixes**
* Linux filesystem integration
* Linux desktop file-manager integration

> [!NOTE]
> Windows-only features are not silently emulated.
>
> If Linux does not provide a real equivalent, RHI Linux keeps that functionality unavailable instead of presenting a fake or no-op implementation.

---

# ⚡ Quick start

## CachyOS / Arch Linux

Install the required development packages:

```fish
sudo pacman -S --needed dotnet-sdk-8.0 7zip git
```

Clone the project:

```fish
git clone https://github.com/wizardkof/RHI-Linux.git
cd RHI-Linux
```

Restore dependencies:

```fish
dotnet restore RHI-Linux.sln
```

Run the test suite:

```fish
dotnet test RHI-Linux.sln
```

Launch RHI Linux:

```fish
./run.fish
```

### Bash

```bash
./run.sh
```

---

## Build

### Fish

```fish
./build.fish
```

### Bash

```bash
./build.sh
```

Published files are written to:

```text
artifacts/linux-x64/
```

---

# 🎨 ReShade

RHI Linux implements an upstream-style ReShade profile workflow rather than supporting only one fixed build.

## Available channels

| Channel     | Behavior                           |
| :---------- | :--------------------------------- |
| **Stable**  | Current official full-add-on build |
| **Nightly** | Development artifact               |
| **Legacy**  | Explicit pinned version            |
| **Custom**  | User-selected local ReShade DLL    |

### Custom DLL protection

Before injection, a Custom DLL must:

* be a valid PE binary;
* have a sane size;
* match the target game's architecture;
* contain recognizable ReShade identification.

RHI records the source/channel identity per game.

---

<details>
<summary><strong>📄 Conservative ReShade.ini ownership</strong></summary>

<br>

RHI Linux does **not** blindly replace an existing `ReShade.ini`.

When **Manage / merge ReShade.ini** is enabled, RHI tracks only the values and search-path entries it owns.

During teardown:

* RHI-owned entries are removed;
* unrelated user configuration survives;
* edited files are preserved;
* a completely RHI-created INI is removed only when it still matches the state RHI created.

This makes cleanup ownership-based rather than filename-based.

</details>

---

## Shader packs

The first Linux-native pack subset includes:

* crosire `reshade-shaders`
* crosire legacy shaders
* clshortfuse ReShade shaders
* MaxG2D Simple HDR Shaders

Managed files remain isolated:

```text
reshade-shaders/
└── RHI/
    ├── <Pack>/
    │   ├── Shaders/
    │   └── Textures/
    └── _Core/
        └── Shaders/
```

RHI tracks managed files through hashes.

An externally modified shader is **never silently overwritten or removed**.

---

<details>
<summary><strong>🧩 ReShade.fxh / ReShadeUI.fxh dependency fix</strong></summary>

<br>

Real-world testing showed that clshortfuse and MaxG2D effects can depend on:

```text
ReShade.fxh
ReShadeUI.fxh
```

without shipping those headers themselves.

Since v0.6.6, selecting one of the dependent packs automatically stages only the required core headers:

```text
reshade-shaders/RHI/_Core/Shaders/ReShade.fxh
reshade-shaders/RHI/_Core/Shaders/ReShadeUI.fxh
```

The fallback search path is appended after the individual pack paths.

The files are hash tracked and removed only while they are still RHI-owned and unmodified.

</details>

---

# 🧱 Native D3DCompiler_47 support

A real Wine / Proton regression showed that ReShade could successfully load while individual shaders still failed to compile against Wine's built-in compiler.

Using a native Microsoft:

```text
D3DCompiler_47.dll
```

with:

```text
d3dcompiler_47=native,builtin
```

resolved those failures.

RHI Linux therefore manages this compatibility dependency explicitly when shader packs require it.

### Supported workflow

RHI can:

* find a native compiler beside the game executable;
* detect one already installed inside the Wine / Proton prefix;
* accept a user-selected DLL;
* provide a native Avalonia **Browse…** picker;
* validate PE architecture;
* validate Microsoft / HLSL identity;
* deploy the selected DLL application-local;
* configure `d3dcompiler_47=native,builtin` per game;
* track this Wine override independently from the ReShade proxy;
* preserve pre-existing external compiler files;
* hash-guard an RHI-managed compiler;
* perform exact teardown of RHI-owned state.

If no valid compiler exists, RHI stops **before modifying ReShade**.

For Steam games it can provide a hint such as:

```fish
protontricks <AppId> d3dcompiler_47
```

> [!CAUTION]
> **Microsoft `D3DCompiler_47.dll` is not bundled or redistributed by RHI Linux.**

---

# 🧩 ReLimiter & Display Commander

Two upstream auxiliary ReShade add-ons are currently supported.

## ReLimiter

Architecture-aware deployment of:

```text
relimiter.addon64
relimiter.addon32
```

## Display Commander

Architecture-aware deployment of:

```text
zzz_display_commander.addon64
zzz_display_commander.addon32
```

Both implementations:

* require ReShade full-add-on support;
* respect `[ADDON] AddonPath`;
* select x86 / x64 automatically;
* validate PE payloads;
* verify GitHub-published SHA-256 when available;
* refuse to overwrite external files;
* hash-guard managed teardown;
* block ReShade removal while a managed dependency remains.

> [!IMPORTANT]
> **ReLimiter and Display Commander are mutually exclusive**, matching upstream RHI behavior.

### Probe safety

Files such as:

```text
relimiter.ini
DisplayCommander.ini
*.log
*.json
```

do **not** count as an installed add-on.

Only real:

```text
.addon64
.addon32
```

binaries determine installed state.

User configuration can therefore remain after removing the managed binary.

---

# 🟢 DLSS / Streamline

RHI Linux consumes the upstream RHI DLSS manifest for game-local management of:

* **Super Resolution**
* **Ray Reconstruction**
* **Frame Generation**
* **Neural Rendering**
* **Streamline**

## Existing-file-only deployment

A central safety rule is:

> **RHI never invents a DLSS or Streamline installation layout.**

A DLL is eligible only when:

1. the target already exists in the game;
2. its filename is recognized;
3. the target location was detected by RHI.

For Streamline, downloading a package containing more `sl.*.dll` files does **not** cause missing modules to be created automatically.

---

## Transactional replacement

A genuine replacement follows this model:

```text
Game DLL
   │
   ├── preserve original
   │      └── file.dll.original
   │
   ├── stage replacement
   │
   ├── verify SHA-256
   │
   └── activate replacement
```

Multi-file operations use the same transaction model.

If an operation fails, RHI attempts to return the entire batch to its previous state.

---

## Byte-safe restore

Since v0.6.3:

* source and target are compared by SHA-256 before mutation;
* identical payloads are treated as a **no-op**;
* no `.original` is created for a no-op;
* backup copies are SHA-256 verified;
* transaction snapshots are verified;
* staging files are verified;
* restored files are verified before backup deletion;
* `.original` is never consumed early;
* restore failure keeps the backup available;
* the previously active target is restored from a transaction snapshot when necessary.

### No-op behavior

If:

```text
downloaded DLL SHA-256 == current game DLL SHA-256
```

RHI reports that the game already has the selected payload.

Nothing is rewritten.

---

# 🛠 Linux NVIDIA cache repair

Real CachyOS testing exposed cached NVIDIA payload DLLs using the Unix mode:

```text
0010
------x---
```

The files existed but the normal user could not read them.

## v0.6.4

RHI added:

* permission normalization to `0644`;
* actual readability checks;
* post-extraction permission normalization;
* damaged cache retry/redownload behavior.

Only RHI's own cache is modified.

**Game DLL permissions are never changed by this repair.**

## v0.6.5

RHI now performs a proactive migration sweep over only:

```text
DLSS
DLSS-D
DLSS-G
DLSS-NR
Streamline
```

Recognized NVIDIA payload DLLs become:

```text
0644
rw-r--r--
```

Unrelated ReShade, RenoDX, OptiScaler and add-on caches are left untouched.

---

# 🎮 Game discovery

## Steam / Proton

RHI Linux supports:

* native Steam libraries;
* additional `libraryfolders.vdf` libraries;
* AppID association;
* `compatdata/<appid>/pfx` prefix mapping;
* executable probing.

---

## Heroic / Legendary

The detector understands Linux Legendary metadata outside Heroic itself, including:

```text
~/.config/legendary/installed.json
```

and Heroic-managed / Flatpak Legendary layouts.

It also supports:

* nested `install` metadata;
* registered executables;
* `is_installed=false` filtering;
* correct Heroic runner identity;
* exact `GamesConfig` Wine-prefix association.

---

## Heroic sideload

Custom Heroic entries such as:

```text
heroic://launch?appName=<id>&runner=sideload
```

are supported.

Current metadata path:

```text
~/.config/heroic/sideload_apps/library.json
```

RHI understands:

```text
app_name
title
folder_name
folderName
install.executable
GamesConfig/<appName>.json
```

> [!NOTE]
> RHI deliberately does **not** treat every directory below Heroic's prefix directory as a game.
>
> Prefix-only discovery would create false entries such as shared prefixes and utility environments.

---

# 📂 Desktop integration

**Open Folder** and **Open Prefix** safely handle paths containing spaces through:

```text
ProcessStartInfo.ArgumentList
```

Linux opener fallback order includes:

```text
xdg-open
gio open
kde-open6
kioclient6 exec
kde-open5
```

---

# 🔄 v0.6.7 — RHI 2.5.9 lifecycle foundation

Current upstream baseline:

```text
Project:         RankFTW/RHI
Compatibility:   RHI-2.5.9
Upstream release: v2.5.9
Baseline commit: c9eba5d7d728228402655580103a43ec1550ad60
```

RHI 2.5.7–2.5.9 introduced lifecycle behavior that also matters to Linux:

1. GitHub add-on asset names are not always stable.
2. Add-on metadata is increasingly driven by `manifest.json → addonPacks`.
3. Shader exclusions must survive restart / refresh.
4. Newer DLSS5 Feeder behavior depends on persistent exclusions.

v0.6.7 ports the reusable Linux-safe primitives **before exposing the corresponding components**.

---

<details>
<summary><strong>🌐 Dynamic GitHub release resolver</strong></summary>

<br>

`GitHubReleaseAssetResolver` understands both:

```text
/releases/latest
```

and:

```text
/releases?per_page=100
```

Supported behavior includes:

* exact filename matching;
* regex asset matching;
* release-list fallback;
* draft-release skipping;
* version extraction;
* published GitHub SHA-256 propagation.

The resolver is intentionally payload agnostic.

Future consumers can therefore use either:

```text
addon.addon64
addon.addon32
```

or dynamic ZIP packages without hardcoding a permanent GitHub asset filename.

</details>

---

<details>
<summary><strong>📦 Manifest-driven add-on metadata</strong></summary>

<br>

`UpstreamRhiManifestService` currently reads the upstream 2.5.9 `addonPacks` fields required by future Linux component gates:

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

The compatibility source is pinned to:

```text
RHI-2.5.9
```

to keep this development gate reproducible.

</details>

---

<details>
<summary><strong>💾 Persistent shader-pack exclusions</strong></summary>

<br>

`ReShadeShaderPackService` now stores per-pack exclusions in:

```text
.pack-settings.json
```

These exclusions:

* survive service restart;
* survive refresh;
* use case-insensitive shader filenames;
* apply to future pack synchronization;
* do not filter textures;
* are not destroyed by removing ReShade from one game.

This lifecycle support is required before DLSS5 Feeder can safely be exposed.

</details>

---

## v0.6.7 does NOT yet enable

* ❌ automatic Neural Rendering workflow
* ❌ DLSS5 Feeder UI
* ❌ DX11 Bridge UI
* ❌ MFG Ada Unlock UI
* ❌ upstream PowerShell execution through Wine
* ❌ Windows NvAPI profile behavior
* ❌ fabricated Neural Rendering validation

These remain independent gates.

---

# 🛡 Safety model

RHI Linux uses ownership and transaction rules rather than blindly modifying game files.

### Core invariants

1. **Unknown proxy DLLs are never silently overwritten.**
2. **External files are never automatically adopted as RHI-owned.**
3. Managed updates and removal remain hash guarded.
4. Custom ReShade remains architecture validated.
5. RHI shader files stay inside `reshade-shaders/RHI`.
6. User-modified shaders and configuration are preserved.
7. External D3DCompiler binaries are usable without becoming RHI-owned.
8. RHI-created D3DCompiler state is tracked independently.
9. ReShade / RenoDX / OptiScaler coexistence remains a regression invariant.
10. Native Linux games never receive Windows DLL injection.
11. Missing DLSS / Streamline layouts are never invented.
12. Multi-file failure triggers rollback behavior.
13. Windows DLL proxy injection is never advertised as a native Vulkan layer.
14. Unsupported NvAPI / DRS functionality is never faked.

---

# ⚠️ Anti-cheat / online games

> [!WARNING]
> **ReShade full-add-on mode, RenoDX, OptiScaler and similar workflows inject or load code inside the Windows game process.**

Use them primarily with:

* single-player games;
* offline games;
* titles whose anti-cheat policy explicitly allows the modification.

**RHI Linux does not bypass anti-cheat.**

RHI Linux is a third-party port / manager and is **not** an official application from:

* ReShade
* RenoDX
* OptiScaler
* NVIDIA
* Valve
* Heroic
* Lutris

---

# 🧪 Validation

Current v0.6.7 source:

```text
125 [Fact]
 30 [InlineData] theory rows
---
155 statically counted cases
```

The v0.6.6 real-world regression baseline is accepted.

The current v0.6.7 infrastructure must still complete:

```fish
dotnet test RHI-Linux.sln
```

followed by a normal desktop UI smoke launch on the CachyOS validation machine.

Only after that gate is accepted should the v0.6.7 lifecycle foundation become the new stable regression baseline.


# 🏗 Architecture

```text
RHI-Linux
│
├── src/
│   │
│   ├── RHI.Core/
│   │   ├── Models/
│   │   └── Services/
│   │
│   ├── RHI.Platform.Linux/
│   │   ├── Detection/
│   │   ├── Deployment/
│   │   ├── Launch/
│   │   └── Paths/
│   │
│   └── RHI.Desktop/
│       ├── ViewModels/
│       └── Views/
│
├── tests/
│   └── RHI.Core.Tests/
│
└── packaging/
```

### Architectural boundary

```text
┌───────────────────────────┐
│       RHI.Desktop         │
│          Avalonia         │
└─────────────┬─────────────┘
              │
              ▼
┌───────────────────────────┐
│         RHI.Core          │
│ Components / manifests /  │
│ ownership / transactions  │
└─────────────┬─────────────┘
              │
              ▼
┌───────────────────────────┐
│    RHI.Platform.Linux     │
│ Steam / Heroic / Wine /   │
│ Proton / filesystem / OS  │
└───────────────────────────┘
```

Cross-platform behavior belongs in:

```text
RHI.Core
```

Linux-specific behavior belongs in:

```text
RHI.Platform.Linux
```

Desktop presentation belongs in:

```text
RHI.Desktop
```

WinUI, Windows Registry APIs and NvAPI must not leak into the portable core.

---

# 🔗 Upstream relationship

RHI Linux is currently aligned against:

```text
RankFTW/RHI
└── RHI-2.5.9
    └── v2.5.9
```

Linux-relevant upstream behavior is mirrored where it can be implemented safely.

Examples include:

* ReShade proxy conventions;
* architecture-aware payload selection;
* ReShade full-add-on staging;
* occupied-proxy protection;
* ReShade + OptiScaler chaining;
* RenoDX catalog concepts;
* normalized title matching;
* ReShade `AddonPath`;
* DLSS / Streamline manifest usage;
* newer manifest-driven add-on lifecycle concepts.

Linux divergence is deliberate where required.

| Windows RHI                           | RHI Linux                                                 |
| :------------------------------------ | :-------------------------------------------------------- |
| WinUI / Windows App SDK               | Avalonia                                                  |
| Windows storefront Registry discovery | Steam / Heroic / Lutris / Wine metadata                   |
| Windows DLL environment               | Per-application Wine / Proton overrides                   |
| NvAPI / DRS                           | Only real Linux equivalents                               |
| Windows injection into Windows titles | Windows DLL injection restricted to Wine / Proton targets |

RHI Linux is a derivative port.

It does **not** claim that the original WinUI executable itself is cross-platform.

---

# 🔒 Still locked / roadmap

The project intentionally does **not** claim full upstream parity yet.

### Next component gates

* [ ] DLSS5 Feeder
* [ ] DX11 Bridge
* [ ] MFG Ada Unlock
* [ ] Neural Rendering field validation
* [ ] Luma
* [ ] REFramework
* [ ] DOF Fix

### Deeper parity

* [ ] broader RenoDX behavior
* [ ] per-game manifest parity
* [ ] ReLimiter advanced INI / presets
* [ ] Display Commander advanced INI / presets
* [ ] deeper Streamline configuration
* [ ] full upstream shader-pack catalog
* [ ] shader import UI

### Platform work

* [ ] native Vulkan ReShade layer backend
* [ ] Vulkan-aware OptiScaler topology
* [ ] Update All
* [ ] background updater
* [ ] settings parity
* [ ] tray integration
* [ ] compact mode
* [ ] final packaging
* [ ] Linux HDR / compositor integration
* [ ] NVIDIA controls where a real Linux API exists

---

# 📜 License

RHI Linux is a derivative of the GPLv3-licensed:

**[RankFTW/RHI](https://github.com/RankFTW/RHI)**

and is distributed under **GPLv3**.

See:

* [`LICENSE`](LICENSE)
* [`UPSTREAM.md`](UPSTREAM.md)

Third-party runtime binaries are **not bundled in the source repository**.

ReShade, RenoDX, OptiScaler, shader content and supported auxiliary components are obtained from their respective upstream/runtime sources when required.

> [!CAUTION]
> Microsoft `D3DCompiler_47.dll` is **never bundled or redistributed by RHI Linux**.

---

<div align="center">

### RHI Linux

**Native Linux tooling for the RHI ecosystem — without pretending Windows-only functionality exists where Linux has no equivalent.**

[Back to top](#rhi-linux)

</div>
