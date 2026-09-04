# RHI Linux — native port bootstrap

This repository is the first native-Linux port milestone of [RankFTW/RHI](https://github.com/RankFTW/RHI).
It keeps the upstream product structure and visual identity, but replaces WinUI/Windows App SDK and
Windows-only discovery with a Linux platform layer for Steam/Proton, Heroic, Lutris and standalone Wine.

> **Status:** v0.1.0 native foundation + first functional DLSS backend. This is intentionally **not** presented
> as full RHI parity yet. Generic component install/update buttons remain locked until each Linux deployment
> path has parity tests, but game-local DLSS SR/RR/FG/NR version swapping and `.original` restore are wired.
> Detection, game/prefix inspection, local component probing, folder opening and launching are also active.

## Why a native port

Upstream targets `net8.0-windows10.0.19041.0`, uses WinUI 3 / Windows App SDK, Windows Registry and NvAPI.
Those dependencies are why installing .NET 8 in Wine is not enough. The Linux port keeps .NET 8 and the
cross-platform/core logic but moves the desktop UI to Avalonia and the platform services behind interfaces.

## What works in this milestone

- Native .NET 8 Linux process; no Wine needed for the manager itself.
- RHI-like dark/teal desktop UI using Avalonia 12.
- Steam native install detection, including additional Steam libraries.
- Steam AppID extraction and automatic `steamapps/compatdata/<appid>/pfx` mapping.
- Heroic cache discovery with schema-tolerant JSON walking and prefix matching.
- Lutris YAML discovery for executable/prefix/runner.
- Standalone Wine/Bottles prefix discovery.
- Game executable heuristic and basic graphics API probing.
- Local component presence probe for ReShade, RenoDX, OptiScaler, Luma, REFramework, DLSS, Streamline,
  ReLimiter, Display Commander and DOF Fix.
- Live upstream `dlss_manifest.json` loading with on-disk cache fallback.
- DLSS SR / RR / FG / NR version selection, ZIP download, DLL extraction and game-local replacement.
- True original preservation as `.original`, explicit Restore, same-directory temporary writes and batch rollback.
- Refuses DLSS swaps when no existing target DLL is detected; it does not guess/create a target layout.
- Streamline multi-DLL transactional swap backend exists in core (UI exposure is a later gate).
- Open game folder / prefix and launch through Steam, Heroic, Lutris or Wine when a launch command is known.
- Upstream `DetectedGame` fields are retained; Linux metadata is additive.

## Deliberately locked for v0.1.0

The following are visible structurally but are not allowed to pretend they work before parity validation:

- One-click download/install/update/remove of ReShade/RenoDX/Luma/OptiScaler/etc.
- Shader/addon manager.
- Streamline version-selection UI and restore workflow (the core swap backend is present).
- Remote manifest update pipeline.
- Linux HDR compositor integration.
- NVIDIA driver-profile features that depend on Windows NvAPI/DRS.
- Update All, settings parity, tray and background updater.

## CachyOS / Arch quick start

Install the Arch/CachyOS .NET 8 SDK first:

```fish
sudo pacman -S dotnet-sdk-8.0
```

Then (fish):

```fish
git clone <your-RHI-Linux-repository>
cd RHI-Linux
dotnet restore RHI-Linux.sln
dotnet test RHI-Linux.sln
./run.fish
```

To publish:

```fish
./build.fish
```

Equivalent `run.sh` / `build.sh` helpers are included for bash.

The output is written to `artifacts/linux-x64/`.

## Architecture

```text
RHI-Linux
├── src/RHI.Core
│   ├── Models
│   └── Services
├── src/RHI.Platform.Linux
│   ├── Detection
│   ├── Launch
│   └── Paths
├── src/RHI.Desktop
│   ├── ViewModels
│   └── Views
├── tests/RHI.Core.Tests
└── packaging
```

The key rule is **platform isolation**. Core services must not call the Registry, WinUI, NvAPI or Linux shell
commands directly. Windows and Linux implementations can then evolve independently without corrupting RHI's
component/manifests logic.

## Next parity gates

1. **G1 — discovery parity:** Steam/Heroic/Lutris/Wine fixtures, duplicate elimination, exact prefix mapping.
2. **G2 — manifest parity:** consume upstream local/remote manifests without changing their schema.
3. **G3 — safe file deployment:** transactional copy, `.original` backup, rollback, foreign-DLL protection.
4. **G4 — DLSS/Streamline:** SR/RR/FG/NR swap is implemented; finish Streamline UI, path fixtures and release-build validation.
5. **G5 — ReShade/RenoDX:** Linux/Wine-aware DLL naming and deployment with anti-cheat warnings preserved.
6. **G6 — OptiScaler/Luma/REFramework:** per-engine deployment and INI/Engine.ini edits.
7. **G7 — UI parity:** settings, compact/detail views, filters, Add Game, shader/addon manager.
8. **G8 — platform extras:** Wayland/KDE HDR integration, tray, autostart, updater and packaging.
9. **G9 — NVIDIA split:** implement Linux equivalents only where the NVIDIA Linux stack exposes a real,
   verifiable control. Keep unsupported DRS-only features explicitly unavailable.

## License / upstream

This is a derivative port of GPLv3-licensed RHI and is distributed under GPLv3. See `LICENSE`.
No third-party mod binaries are bundled by this bootstrap; the final installer layer should continue the
upstream model of downloading components from their official sources at runtime.
