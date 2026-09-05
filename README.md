<div align="center">

# RHI Linux

### Native Linux port of RHI for Steam / Proton, Heroic, Lutris and Wine

[![Version](https://img.shields.io/badge/RHI%20Linux-v0.9.2-0ea5e9?style=for-the-badge)](https://github.com/wizardkof/RHI-Linux)
[![Upstream](https://img.shields.io/badge/Upstream%20RHI-2.6.0-6366f1?style=for-the-badge)](https://github.com/RankFTW/RHI)
[![Linux](https://img.shields.io/badge/Platform-Linux-f59e0b?style=for-the-badge\&logo=linux\&logoColor=white)](https://github.com/wizardkof/RHI-Linux)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge\&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/wizardkof/RHI-Linux?style=for-the-badge)](LICENSE)

**RHI Linux brings the RHI component-management workflow to Linux with native .NET 8 + Avalonia integration for Wine and Proton games.**

<br>

[Features](#-features) ·
[Supported Launchers](#-supported-launchers) ·
[Quick Start](#-quick-start) ·
[Compatibility](#-compatibility--limitations) ·
[Roadmap](#-roadmap)

<br>

<p align="center">
  <a href="https://github.com/user-attachments/assets/2a8a8134-7e07-469c-9129-a8e6cac46199">
    <img
      src="https://github.com/user-attachments/assets/2a8a8134-7e07-469c-9129-a8e6cac46199"
      alt="RHI Linux main interface"
      width="48%"
    />
  </a>
  &nbsp;
  <a href="https://github.com/user-attachments/assets/f46adb7c-faec-4dbf-bf0f-3a97fc2a1560">
    <img
      src="https://github.com/user-attachments/assets/f46adb7c-faec-4dbf-bf0f-3a97fc2a1560"
      alt="RHI Linux game configuration"
      width="48%"
    />
  </a>
</p>

</div>

---

## 🚀 Status

**Current development:** `v0.9.2`
**Upstream target:** `RHI 2.6.0`

RHI Linux is actively developed and already provides functional Linux-native management for several RHI components.

The current development focus is **ShortFuse / Frame Generation orchestration** on top of the Ultimate ASI Loader backend introduced in v0.9.1.

> [!NOTE]
> RHI Linux is a native Linux port of the RHI workflow.
> It does not run the original WinUI application through Wine.

---

# ✨ Features

| Component                        | Status |
| :------------------------------- | :----: |
| **ReShade**                      |    ✅   |
| **ReShade shader packs**         |    ✅   |
| **RenoDX HDR**                   |    ✅   |
| **OptiScaler**                   |    ✅   |
| **DLSS Super Resolution**        |    ✅   |
| **DLSS Ray Reconstruction**      |    ✅   |
| **DLSS Frame Generation**        |    ✅   |
| **Streamline**                   |    ✅   |
| **MFG Ada Unlock**               |    ✅   |
| **DLSS5 Feeder**                 |    ✅   |
| **DLSS5 Feeder Full**            |    ✅   |
| **DLSS5 DX11 Bridge**            |    ✅   |
| **ReLimiter**                    |    ✅   |
| **Display Commander**            |    ✅   |
| **Ultimate ASI Loader**          |    ✅   |
| **ShortFuse auto-configuration** |   🚧   |
| **Luma**                         |   🔒   |
| **REFramework**                  |   🔒   |
| **DOF Fix**                      |   🔒   |

### Status

* ✅ Available
* 🚧 In development
* 🔒 Not ported yet

---

## 🎨 ReShade

RHI Linux supports multiple ReShade sources:

* **Stable**
* **Nightly**
* **Legacy**
* **Custom DLL**

It also supports managed shader packs and preserves user configuration during install and removal.

Existing files are protected whenever possible instead of being blindly overwritten.

---

## 🟢 DLSS & Streamline

RHI Linux can manage game-local versions of:

* **DLSS Super Resolution**
* **Ray Reconstruction**
* **Frame Generation**
* **Neural Rendering runtimes**
* **Streamline**

Changes use a transactional backup system with `.original` restoration.

> [!IMPORTANT]
> Standard DLSS swapping only works when the game already contains a recognized target DLL.
>
> RHI does not guess or create arbitrary DLSS locations.

---

## 🧠 DLSS5 Feeder Full

For compatible games without an existing game-local DLSS runtime, RHI Linux provides a separate **Feeder Full** workflow.

It can manage:

* DLSS5 Feeder
* private DLSS SR / NR runtimes
* Lumenite Kernel
* DLSS5 Feed shaders
* ReShade dependencies
* native `D3DCompiler_47.dll` integration
* per-game Wine overrides

The current Linux-safe consumer path uses **Deep Fried Chicken**.

> [!NOTE]
> Deep Fried Chicken is not bundled with RHI Linux.
>
> The package must be supplied by the user.

User-modified `deep-fried-chicken.cfg` files are preserved during removal and can be reused during reinstall.

---

## 🔌 Ultimate ASI Loader

RHI Linux includes native management for **Ultimate ASI Loader** under Wine and Proton.

Supported behavior includes:

* x86 / x64 automatic selection
* per-game proxy selection
* Steam / Proton
* Heroic
* Lutris
* standalone Wine
* per-application `native,builtin` overrides
* existing proxy preservation
* `Hooked.dll` chaining
* safe Update / Remove
* restoration of previous Wine override state

---

## 🧱 Native D3DCompiler support

Some ReShade shaders require Microsoft's native:

```text
D3DCompiler_47.dll
```

RHI Linux can detect a valid compiler from:

* the game directory
* the Wine / Proton prefix
* a user-selected DLL
* Protontricks
* Winetricks

Supported launchers use their actual active Wine / Proton runner instead of blindly falling back to system Wine.

> [!CAUTION]
> Microsoft `D3DCompiler_47.dll` is **not bundled or redistributed by RHI Linux**.

---

# 🎮 Supported launchers

## Steam / Proton

* Steam library discovery
* additional Steam libraries
* AppID detection
* automatic Proton prefix mapping
* Protontricks integration

## Heroic

* Legendary / Epic games
* custom / sideload games
* Wine and Proton prefixes
* per-game runner detection
* launcher-aware Winetricks

## Lutris

* game discovery
* executable detection
* Wine prefix detection
* active Wine runner integration

## Standalone Wine

Existing standalone Wine prefixes are also supported.

---

# 🖥 Interface

RHI Linux uses **Avalonia** for a native Linux desktop interface.

Game detail sections can be:

* collapsed
* expanded
* reordered

The layout persists between sessions.

DLSS and Streamline components also show their current state directly:

```text
— Not present
• Game DLL
✓ RHI applied
```

---

# ⚡ Quick start

## CachyOS / Arch Linux

Install the required packages:

```fish
sudo pacman -S --needed dotnet-sdk-8.0 7zip git
```

Clone:

```fish
git clone https://github.com/wizardkof/RHI-Linux.git
cd RHI-Linux
```

Restore:

```fish
dotnet restore RHI-Linux.sln
```

Run:

```fish
./run.fish
```

### Bash

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

Output:

```text
artifacts/linux-x64/
```

---

# 🛡 File safety

RHI Linux is designed to avoid destructive game modifications.

Managed workflows use:

* `.original` backups
* SHA-256 verification
* transactional replacement
* rollback after failure
* ownership tracking
* protection of external files
* preservation of user-modified configuration

RHI will refuse some operations rather than overwrite a file it cannot safely identify as its own.

---

# ⚠️ Compatibility & limitations

RHI Linux manages Windows game components running through Wine / Proton.

Compatibility can vary depending on:

* game engine
* Wine / Proton version
* GPU generation
* driver version
* existing mods
* anti-cheat

Some features are therefore available only when the selected game meets their requirements.

### Native Linux games

Windows DLL injection components are not installed into native Linux games.

### NVIDIA Windows features

Windows NvAPI / DRS functionality is not emulated when Linux has no real equivalent.

### Neural Rendering

Deployment infrastructure exists, but actual feature availability still depends on the game, GPU and NVIDIA runtime.

---

# ⚠️ Anti-cheat

> [!WARNING]
> ReShade full-add-on mode, RenoDX, OptiScaler, Ultimate ASI Loader and similar components load or inject code into the Windows game process.

Use these features primarily with:

* single-player games
* offline games
* titles whose anti-cheat policy explicitly permits modifications

**RHI Linux does not bypass anti-cheat.**

---

# 🔄 Upstream

RHI Linux currently follows:

```text
RankFTW/RHI
└── RHI-2.6.0
    └── v2.6.0
```

The Linux port follows upstream behavior where it can be implemented safely.

Platform-specific differences are intentional:

| RHI Windows                  | RHI Linux                             |
| :--------------------------- | :------------------------------------ |
| WinUI / Windows App SDK      | Avalonia                              |
| Windows launcher discovery   | Steam / Heroic / Lutris               |
| Windows Registry integration | Wine / Proton prefix integration      |
| Windows DLL environment      | Per-game Wine overrides               |
| NvAPI / DRS                  | Linux equivalents only when available |

---

# 🚧 Roadmap

### Current development

* [ ] ShortFuse Frame Generation orchestration
* [ ] automatic `Reshade64.asi` workflow
* [ ] automatic Ultimate ASI Loader setup
* [ ] `HookStreamline`
* [ ] `HookDirectX`
* [ ] complete transactional rollback

### Future

* [ ] Luma
* [ ] REFramework
* [ ] DOF Fix
* [ ] broader RenoDX HDR parity
* [ ] deeper Streamline configuration
* [ ] additional shader management
* [ ] native Vulkan workflows
* [ ] Update All
* [ ] background updater
* [ ] tray integration
* [ ] final Linux packaging
* [ ] HDR / compositor integration

---

# 📜 License

RHI Linux is a derivative of the GPLv3-licensed:

**[RankFTW/RHI](https://github.com/RankFTW/RHI)**

and is distributed under **GPLv3**.

See:

* [`LICENSE`](LICENSE)
* [`UPSTREAM.md`](UPSTREAM.md)

Third-party runtime components are downloaded from their respective upstream sources when required and are not bundled with the RHI Linux source repository.

---

<div align="center">

### RHI Linux

**Native Linux tooling for the RHI ecosystem.**

[Back to top](#rhi-linux)

</div>
