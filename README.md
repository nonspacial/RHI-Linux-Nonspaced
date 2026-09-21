<div align="center">

# RHI Linux

### Native Linux port of RHI for Steam / Proton, Heroic, Lutris and Wine

[![Version](https://img.shields.io/badge/RHI%20Linux-v0.9.23.4--beta.1-0ea5e9?style=for-the-badge)](https://github.com/wizardkof/RHI-Linux)
[![Upstream](https://img.shields.io/badge/Upstream%20RHI-2.7.5-6366f1?style=for-the-badge)](https://github.com/RankFTW/RHI)
[![Linux](https://img.shields.io/badge/Platform-Linux-f59e0b?style=for-the-badge\&logo=linux\&logoColor=white)](https://github.com/wizardkof/RHI-Linux)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge\&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/wizardkof/RHI-Linux?style=for-the-badge)](LICENSE)

**RHI Linux brings the RHI workflow to Linux with a native .NET 8 + Avalonia application for Windows games running through Wine and Proton.**

<br>

[Status](#-status) ·
[Supported Launchers](#-supported-launchers) ·
[Quick Start](#-quick-start) ·
[Compatibility](#️-compatibility) ·
[Upstream](#-upstream) ·
[License](#-license)

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

**Current public beta:** `v0.9.23.4-beta.1`
**Upstream target:** `RHI 2.7.5`

RHI Linux is a native Linux port of the RHI workflow, adapted for Windows games running through Wine and Proton.

The project targets functional parity with upstream RHI where the same functionality can be implemented reliably on Linux.

This release is published as a **beta** while broader testing continues across different Linux distributions, Wine / Proton versions, GPUs, launchers and games.

> [!NOTE]
> RHI Linux is a native Linux application.
>
> It does not run the original Windows RHI application through Wine.

> [!IMPORTANT]
> DLSS5 Feeder / Neural Rendering workflows are experimental.
>
> The default DLSS5 Feeder consumer is **RenoDX DLSS5**.

---

# 🎮 Supported Launchers

## Steam / Proton

RHI Linux supports Steam games running through Proton, including:

* Steam library discovery
* additional Steam libraries
* AppID detection
* Proton prefix detection
* game executable discovery
* launching through Steam / Proton

## Heroic

RHI Linux supports games managed through Heroic, including:

* Epic / Legendary games
* sideloaded games
* Wine prefixes
* Proton prefixes
* per-game runner detection

## Lutris

RHI Linux supports Lutris configurations, including:

* game discovery
* executable detection
* Wine prefix detection
* active runner detection

## Standalone Wine

Manually added games and standalone Wine prefixes are also supported.

The game executable and Wine prefix can be configured independently when necessary.

---

# ⚡ Quick Start

## CachyOS / Arch Linux

Install the required packages:

```fish
sudo pacman -S --needed dotnet-sdk-8.0 7zip git
```

Clone RHI Linux:

```fish
git clone https://github.com/wizardkof/RHI-Linux.git
cd RHI-Linux
```

Run:

```fish
./run.fish
```

### Bash

```bash
./run.sh
```

The validation scripts included in the repository are intended for development and release verification and are **not required to run RHI Linux**.

---

# ⚠️ Compatibility

RHI Linux manages Windows game components running through Wine and Proton.

Compatibility may vary depending on:

* game
* game engine
* Wine / Proton version
* GPU
* graphics driver
* launcher
* existing mods

Some Windows-only RHI functionality may remain unavailable when Linux does not provide a real equivalent.

### Native Linux games

Windows DLL injection components are not intended for native Linux games.

### Windows-specific NVIDIA features

Windows NvAPI / DRS functionality is not emulated when the Linux NVIDIA stack does not provide a real equivalent.

RHI Linux targets functional parity where possible instead of presenting unsupported Windows functionality as available.

### Anti-cheat

> [!WARNING]
> ReShade, RenoDX, OptiScaler and similar components may not be appropriate for games using anti-cheat systems.

Use game modifications only when permitted by the game and service involved.

**RHI Linux does not bypass anti-cheat.**

---

# 🔄 Upstream

RHI Linux is a native Linux derivative of:

**[RankFTW/RHI](https://github.com/RankFTW/RHI)**

The current synchronization target is:

```text
RHI 2.7.5
```

RHI Linux aims to preserve the functionality and workflow of the original RHI project while adapting Windows-specific behavior for Linux, Wine and Proton.

Functional parity is the goal; the underlying platform implementation does not need to be identical.

When an upstream feature can function under Wine or Proton, RHI Linux aims to provide an equivalent Linux-compatible workflow.

When Linux does not provide a real equivalent, the corresponding functionality may remain unavailable.

See [`UPSTREAM.md`](UPSTREAM.md) for more information about the relationship with the original project.

---

# 🙏 Original Projects

RHI Linux exists thanks to the original **RHI** project and the wider PC graphics modding ecosystem.

Original and third-party projects used or supported by RHI Linux include:

* [RHI](https://github.com/RankFTW/RHI)
* [ReShade](https://reshade.me/)
* [RenoDX](https://github.com/clshortfuse/renodx)
* [OptiScaler](https://github.com/optiscaler/OptiScaler)
* [Luma Framework](https://github.com/Filoppi/Luma-Framework)
* [RE Framework](https://github.com/praydog/REFramework)
* [ReLimiter](https://github.com/RankFTW/ReLimiter)
* [Display Commander](https://github.com/pmnoxx/display-commander)
* Ultimate ASI Loader
* ShortFuse

RHI Linux does not claim ownership of these projects.

All third-party projects and components remain the property of their respective authors and contributors.

Where required, third-party components are obtained from their original or supported upstream sources.

---

# 📜 License

RHI Linux is a derivative of the GPLv3-licensed:

**[RankFTW/RHI](https://github.com/RankFTW/RHI)**

RHI Linux is distributed under **GPLv3**.

See:

* [`LICENSE`](LICENSE)
* [`UPSTREAM.md`](UPSTREAM.md)

Third-party projects and components retain their respective original licenses.

---

<div align="center">

### RHI Linux

**Native Linux tooling for the RHI ecosystem.**

`v0.9.23.4-beta.1`

[Back to top](#rhi-linux)

</div>
