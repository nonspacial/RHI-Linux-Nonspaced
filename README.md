<div align="center">

# RHI Linux

### Native Linux port of RHI for Steam / Proton, Heroic, Lutris, Wine and Xbox / Game Pass

[![Fork](https://img.shields.io/badge/RHI%20Linux%20Nonspaced-main%20%C2%B7%20based%20on%200.9.23.4--beta.1-0ea5e9?style=for-the-badge)](https://github.com/nonspacial/RHI-Linux-Nonspaced)
[![Upstream](https://img.shields.io/badge/Upstream%20RHI-2.8.7-6366f1?style=for-the-badge)](https://github.com/RankFTW/RHI)
[![Linux](https://img.shields.io/badge/Platform-Linux-f59e0b?style=for-the-badge\&logo=linux\&logoColor=white)](https://github.com/nonspacial/RHI-Linux-Nonspaced)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge\&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/nonspacial/RHI-Linux-Nonspaced?style=for-the-badge)](LICENSE)

**RHI Linux brings the RHI workflow to Linux with a native .NET 8 + Avalonia application for Windows games running through Wine and Proton.**

<br>

[Status](#-status) ·
[What this fork adds](#-what-this-fork-adds) ·
[Supported Launchers](#-supported-launchers) ·
[Quick Start](#-quick-start) ·
[Changes](#-changes-in-this-fork) ·
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

**This repository:** [nonspacial/RHI-Linux-Nonspaced](https://github.com/nonspacial/RHI-Linux-Nonspaced), a fork of [wizardkof/RHI-Linux](https://github.com/wizardkof/RHI-Linux)
**Based on:** RHI Linux `v0.9.23.4-beta.1` (the app still reports this version; the fork has published no release of its own yet)
**Upstream target:** `RHI 2.8.7`

RHI Linux is a native Linux port of the RHI workflow, adapted for Windows games running through Wine and Proton. This fork continues that port: it fixes problems found in a full code review, follows upstream RHI up to 2.8.7, and adds Linux-specific safety around installs, downloads and Wine prefixes.

The project targets functional parity with upstream RHI where the same functionality can be implemented reliably on Linux.

It is still a **beta** while broader testing continues across different Linux distributions, Wine / Proton versions, GPUs, launchers and games.

> [!NOTE]
> RHI Linux is a native Linux application.
>
> It does not run the original Windows RHI application through Wine.

> [!IMPORTANT]
> DLSS5 Feeder / Neural Rendering workflows are experimental.
>
> The default DLSS5 Feeder consumer is **RenoDX DLSS5**.

---

## ✨ What this fork adds

Compared with the wizardkof beta it started from (`v0.9.23.4-beta.1`), this fork:

* **scans large libraries quickly and in little memory.** A single Lutris entry could make the old scan read every mounted drive through the Wine prefix's drive links, passing 2.8 GB of memory and still growing. A full scan of a 21-game library now takes about 1.4 s and 77 MB.
* **never leaves a game half-modded.** Installs, updates and removals are journaled and roll back as a whole if anything fails, including after a crash or power cut.
* **checks what it downloads.** Each download source has its own list of allowed hosts and a size limit, and published checksums are verified.
* **leaves your Wine prefixes alone while they run**, and keeps DLL overrides you set yourself.
* **follows upstream RHI to 2.8.7**, including OptiScaler F5 DLSSNR, nightly pinning, OptiPatcher, the NR Cost Scaler, RTX Encore and Control Ultimate Edition frame generation.
* **lists Xbox / Game Pass games** from Xodus-format installs and from a configurable list of Xbox game folders.
* **lists games from other store launchers inside Wine prefixes** (EA app, Ubisoft Connect, Battle.net, Rockstar and itch.io), from the itch.io app for Linux, and non-Steam games added to Steam.
* **adds RTX Encore** as a managed component (not yet confirmed on Linux).
* **installs ReShade on Vulkan games** as a Vulkan layer set up for that one game (not yet confirmed in a real game).
* **follows the newest DLSS and Streamline** for games that were on the previous newest, if you turn it on, and lets you leave any component out of Update All, for one game or every game.
* **updates ReShade Addon Manager add-ons** with Update All, and can download every shader pack at start-up.
* **ports upstream's INI templates, ReShade presets, peak brightness, ReLimiter, Luma and OptiScaler settings**, the ⓘ notes on component rows and the web links on library cards.
* **adds Nexus Mods and GitHub accounts, update notices, desktop shortcuts, a right-click menu on library cards, saved filters, masked log bundles and game reports.**
* **fits any window size** from 640×480 up, landscape or portrait, in English or Brazilian Portuguese.

The full list is under [Changes in this fork](#-changes-in-this-fork) and in [`CHANGELOG.md`](CHANGELOG.md).

---

# 🎮 Supported Launchers

## Steam / Proton

RHI Linux supports Steam games running through Proton, including:

* Steam library discovery
* additional Steam libraries (each library is read once, even when it is reachable by several paths)
* AppID detection
* Proton prefix detection
* game executable discovery
* launching through Steam / Proton
* non-Steam games added to Steam (**Add a Non-Steam Game**) that point at a Windows game's `.exe`, with the Proton prefix and compatibility tool Steam uses for them; Play starts them through Steam

Tools, DLC and other entries Steam itself does not class as games are not listed. A non-Steam shortcut to the EA app, Ubisoft Connect, Battle.net, Rockstar or itch.io gets no card of its own; the games that launcher installed in the shortcut's prefix are listed instead (see [Other store launchers](#other-store-launchers)). Installers, native programs and scripts added as shortcuts are not listed.

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
* executable detection, including installer paths written relative to the prefix or with `$GAMEDIR`
* Wine prefix detection
* active runner detection

Games installed inside a launcher that runs through Lutris (Ubisoft Connect, Battle.net, the EA app, Rockstar, itch.io) get their own cards. They are modded in their real folder and started through their own launcher. Lutris entries with no real game folder (applications such as the launchers themselves) are not listed as games.

## Other store launchers

Games installed by the **EA app, Ubisoft Connect, Battle.net, the Rockstar Games Launcher or itch.io** inside a Wine, Lutris or Heroic prefix are found from that launcher's own install records, so each gets its own card even when it is installed outside the launcher's default folder. RHI only reads those records; it never changes them.

* **Play** starts the launcher with that game's own start argument, so the game starts the way that launcher normally starts it. HDR launch profiles cannot be applied through a launcher; RHI says so instead of pointing you at a wrapper setting the launcher does not have.
* In Heroic, a game is handed only to its own store's launcher when that launcher is installed as a sideloaded app; otherwise the card has no launch command until you set one in **Edit game**.
* Games installed by the **itch.io app for Linux** (native or Flatpak) are listed too; Play opens the game's page in the itch app, where you start it.
* If a launcher's records cannot be read, the scan says so and the games RHI can find by their folders are still listed.

## Standalone Wine

Manually added games and standalone Wine prefixes are also supported.

The game executable and Wine prefix can be configured independently when necessary. **Edit game** can also change the launch command; clearing the box goes back to the detected one.

## Xbox / Game Pass

Linux has no official Xbox app. RHI finds Xbox / Game Pass games from Xodus-format installs and from the folders listed under **Settings → Xbox / Game Pass folders**, and lists them straight away; unfinished downloads stay hidden until they complete. Each card carries the name of the product that was installed, so an edition shows as itself (**Control Ultimate Edition**, not the shared "Control Standard Edition" package). RHI is ready to list games installed by [Xodus](https://github.com/xodus-gaming/xodus), a Linux Game Pass client that is still in development.

* **Play** hands the game to the app that installed it, so that app's normal launch path (licensing, saves and cloud sync) applies. RHI never starts the game's executable itself. If RHI cannot find that app, Play is disabled and says why. The card's right-click menu has **Show in the app that installed it**.
* Game Pass executables are encrypted on disk. RHI reads the game's bitness from its package instead, but it cannot always see which graphics API the game uses; if a component refuses to install for that reason, set **Force API** for the game.
* RHI changes nothing in a game while the app that installed it is still downloading or updating it, and never touches the encrypted game files, the package files or the files that app manages in the game's prefix.
* On a system without Xodus or another Xbox installer app, nothing Xbox-related appears (no cards, warnings or menu items) apart from the **Xbox / Game Pass folders** tab in Settings, which describes Xodus and links to it.

---

# ⚡ Quick Start

## CachyOS / Arch Linux

Install the required packages:

```fish
sudo pacman -S --needed dotnet-sdk dotnet-runtime-8.0 7zip git
```

Building from source needs a .NET 10 SDK (any 10.0 feature band, pinned by `global.json`) and the .NET 8 runtime the app targets. NuGet restores are locked by the committed `packages.lock.json` files; after changing a package reference, run `dotnet restore --force-evaluate` and commit the updated lock files.

Clone this fork:

```fish
git clone https://github.com/nonspacial/RHI-Linux-Nonspaced.git
cd RHI-Linux-Nonspaced
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

## Installing for the current user

```bash
./build.sh && ./install-user.sh
```

This installs to `~/.local/opt/rhi-linux` (the previous install is kept as `~/.local/opt/rhi-linux.prev`), links `~/.local/bin/rhi-linux`, adds a menu entry and icon, and refreshes the desktop and icon caches when `update-desktop-database` / `gtk-update-icon-cache` are installed. It warns when `~/.local/bin` is not on `PATH`; the menu entry uses the full path and works either way.

## Going back to an older build

> [!WARNING]
> Before going back to a build from before 14 October 2026, **remove OptiScaler F5 DLSSNR from every game that has it and set every pinned OptiScaler Nightly game back to "Latest (auto-update)"**. An older build reads F5 as OptiScaler Stable and ignores nightly pins, so its next update would replace them. Reinstall them once you are back on the current build. Details: [`UPSTREAM.md`](UPSTREAM.md#rolling-back-to-a-build-older-than-wave-3-f5-dlssnr-nightly-pins).

> [!NOTE]
> Before going back to an older build, remove ReShade from every game where its row reads "ReShade · Vulkan": an older build cannot remove the Vulkan layer, so its registration, the Vulkan loader beside the game and its folder in the prefix would stay, together with the game's `vulkan-1` Wine override. Worse, the older build drops the layer's registry details from RHI's record the first time it saves, so even after coming back this build can no longer remove the registration. Removing it first leaves only RHI's `system.reg.rhi-lock` and up to three `system.reg.rhi-backup-…` files in the prefix. An older build also forgets names, wiki names and bitness set in Edit game the next time it saves the library.

> [!NOTE]
> A build from before 15 October 2026 does not know RTX Encore, the OptiScaler DLL-name choice or add-ons turned off by No Addons. The first time it saves a ReShade DLL name, it forgets RTX Encore's saved DLL name and Neural Rendering choice, and the saved OptiScaler DLL name, for every game where that component is not installed; choose them again once you are back on the current build. **Remove RTX Encore from every game before going back.** An older build cannot update or remove it, and the first time it saves any install record it drops RTX Encore's record for good: after that even this build can no longer update or remove it, and its `nvngx_dlssnr.dll.original` and Wine override are left behind. Also switch No Addons games back to ReShade Stable or Nightly first, so their add-ons are turned back on.

> [!NOTE]
> Older builds still read the ReShade Addon Manager's selection from `addon-manager.json`, but drop its entries from RHI's install records the first time they save them. Back on the current build, RHI takes the selection up again from that file, and Update All downloads those add-ons once more.

## Runtime requirements

* The AppImage bundles the .NET runtime; nothing else from .NET is needed on the host.
* ReShade is extracted from its official setup by RHI Linux itself; no archive tool is needed.
* OptiScaler releases, `.7z` shader packs and Deep Fried Chicken packages are 7-Zip archives and need a host `7z`/`7zz`/`7za` (package `7zip` on Arch/CachyOS, `7zip` or `p7zip-full` elsewhere); `bsdtar` also works except for encrypted packages. RHI Linux names the missing tool when one is needed.

## Building the AppImage

```bash
./build.sh --appimage                          # tests, then the AppImage
packaging/appimage/build-appimage.sh [--out DIR]   # the AppImage only
```

The script publishes a self-contained `linux-x64` build (it runs on hosts without .NET) into an AppDir with `AppRun`, the desktop entry and the icon, then packs it with `appimagetool`. The result is `artifacts/appimage/RHI-Linux-v<version>-x86_64.AppImage` plus a `.sha256` file that holds only the file name. The version comes from `<Version>` in `Directory.Build.props`.

`appimagetool` and the AppImage runtime are downloaded over HTTPS from the AppImage project's GitHub releases at pinned versions and used only when their SHA-256 matches; they are cached in `artifacts/appimage-tools/`. Building needs `curl` and `sha256sum` but no FUSE.

## Validating a change

```bash
./validate.sh            # locked restore, Release build (warnings are errors), full test suite
./validate.sh --clean    # same, after deleting bin/, obj/ and artifacts/ first
```

`validate.sh` keeps existing build outputs unless `--clean` is given. Test results are written to `artifacts/test-results/`.

---

# 🧾 Changes in this fork

Everything below is on the fork's `main` branch. Dated entries are in [`CHANGELOG.md`](CHANGELOG.md); how each upstream feature was adapted for Linux is in [`UPSTREAM.md`](UPSTREAM.md).

## Performance

* **Library scan.** The file indexer no longer follows folder links, so a Wine prefix's drive links (`z:` → `/`, others to your home folder and drives) can no longer pull the whole machine into a scan. Each game folder is capped at 200,000 files, and the scan looks only as deep as it needs to find each game's executable; the full file list is built only for the game you select.
* **Lutris.** Executables written relative to the prefix or with `$GAMEDIR` are resolved, so these games no longer fall back to the whole prefix as their folder. Result: a full scan in about 1.4 s and 77 MB, where one Lutris entry used to pass 2.8 GB and keep growing.
* **Add game** rescans only the manually added games, not every launcher.
* Each Steam library is read once, even when several paths lead to it.
* The library is listed while the DLSS manifest is still downloading; that download now gives up after 15 seconds instead of holding up start-up for minutes.
* Selecting a game no longer re-reads the install records hundreds of times. Settings files are re-read only when they have changed, and the analysis of recently viewed games is kept in a small, bounded cache.
* Searching waits until you stop typing and keeps the selected game if it still matches, instead of restarting its analysis on every key.
* Large files are scanned once, in a fixed-size window, instead of being read whole for each check.
* GitHub answers are cached and revalidated, which saves the anonymous API allowance; when GitHub refuses for rate limits, the last answer is used until the limit resets.

## Safety

* **Crash-safe installs.** Every install, update and removal is journaled. Files are written to a temporary copy, checked, then moved into place. If anything fails, or RHI is interrupted, the change is rolled back as a whole the next time RHI starts. Files you changed yourself in the meantime are kept, never overwritten. A removal is marked on its record before it starts, so an interrupted removal is finished or undone, never left half-done. This covers DLSS5 Feeder and Luma with dgVoodoo2 and their INI edits, ReShade's shader-pack folder, and the ReShade Addon Manager's selection together with its add-on files.
* **Cancel.** Cancel stops an operation only before it has changed anything in the game or prefix. After that point the operation finishes or rolls back completely, so a cancelled install never leaves half-installed files.
* **Verified downloads.** Each source (ReShade, OptiScaler, RenoDX, Luma and the rest) has its own policy: HTTPS only, a fixed list of allowed hosts (checked again after redirects) and a size limit. Checksums are verified whenever the publisher announces one; where none is published, RHI records the SHA-256 of what it installed. Cached downloads are reused only when they still match what was recorded. Archives are unpacked with size limits and integrity checks, and 7-Zip archives that contain links are refused. Per-source details: [`docs/download-trust.md`](docs/download-trust.md).
* **Wine registry.** RHI does not edit a prefix's registry while Wine is running in that prefix; it changes nothing and asks you to close the game first. DLL overrides you set yourself are kept, and removing a component puts back the value that was there before, unless you have changed it since.
* **Your files.** RHI never overwrites or deletes a file it did not install. If a ReShade or OptiScaler that RHI did not install is already there, RHI refuses to add a second one rather than have both load. Game settings files you have locked read-only (such as `Engine.ini`) are edited the way upstream does and locked again.
* **Saved settings.** If a settings or library file cannot be read, RHI sets the original aside, says so, and refuses to save over it instead of starting from empty.
* **Games that move.** Favourites, hidden games, edits and HDR choices follow a game when you move its Steam library or games folder. When a game with installed components has moved, **Repair after move** checks the new folder and updates every record in one step; nothing changes if anything does not match.
* The GitHub token and Nexus API key are stored readable by you alone.

## Platforms & launchers

* **Xbox / Game Pass** games, as described under [Xbox / Game Pass](#xbox--game-pass). Cards are named after the product that was installed (**Control Ultimate Edition**).
* **Other store launchers:** games of the EA app, Ubisoft Connect, Battle.net, Rockstar and itch.io inside Wine, Lutris and Heroic prefixes, and of the itch.io app for Linux, as described under [Other store launchers](#other-store-launchers).
* **Non-Steam games added to Steam** get their own cards ("Steam/Non-Steam"), as described under [Steam / Proton](#steam--proton).
* **HDR launch guidance.** For games started by Steam, Heroic or Lutris, RHI does not wrap the store's launch. It tells you exactly where to paste the HDR settings in that launcher (Steam launch options, Heroic's Advanced settings, Lutris's command prefix). Games started directly with a Proton build's `wine` are told that Proton's HDR variables do not apply to them.
* Wrapper commands (`gamemoderun`, `mangohud`, `gamescope … --` and similar) are seen through, so a launch is always recognised as the store's.
* A game's error output goes to a capped log file under `~/.local/state/rhi-linux/logs/launch/`; if the game exits at once, the last line is shown.
* Commands you type for a manual game run in a login shell, so wrappers in `~/.local/bin` are found.
* A Wine prefix whose runtime cannot be identified is never given the system Wine for helpers such as Winetricks.
* Manual games whose folder has gone are shown as "folder missing" and stay removable, instead of disappearing.
* Detectors report entries they had to skip, rather than failing silently.
* The graphics API is decided from what the executable actually imports. When an executable names several APIs, DirectX is chosen first, and the game's own settings files decide where they can; set the game's API override to Vulkan to get ReShade as a Vulkan layer instead.
* **Change resolution while playing** (HDR row): a game RHI starts directly switches the display to the size you chose and back when it ends, also when RHI was closed meanwhile; with Gamescope the size goes to Gamescope. Uses `kscreen-doctor` on KDE Plasma or `wlr-randr` on wlroots desktops. Not yet confirmed on a real desktop.

## Features

* **ReShade Addon Manager:** its add-ons are updated by Update All and the background update pass; one that cannot be compared with the add-on list is reported as "could not be checked". **Download every shader pack at start-up** (Settings, on by default) fills RHI's shader cache in the background. **Deploy ReShade.ini** is recorded, so removing ReShade takes back what it added.
* **ReShade:** choose the DLL name ReShade is installed under; a **No Addons** channel (the ReShade build without add-on support). Switching a game to No Addons turns the add-ons RHI installed off (they stay in the game folder and are not updated meanwhile); switching back to Stable or Nightly turns them on again. Falls back to a cached copy when reshade.me is down.
* **ReShade for Vulkan games** (Proton or Wine 8 or newer): on a game whose graphics API is Vulkan the row reads **ReShade · Vulkan** and installs ReShade as a Vulkan layer for that game only (the Khronos loader beside the game, the layer and its registration in the prefix). DirectX games are unchanged; a plain reason is given where it cannot work. Changing the API to or from Vulkan while ReShade is installed is refused until it is removed. Not yet confirmed in a real game.
* **Legacy ReShade** versions from a list, the manifest's per-game pins (Max Payne 3 → 6.4.1) on fresh installs, and the manifest's ReShade note for the game.
* **INI templates and presets:** editable `reshade.ini`, `reshade.vulkan.ini`, `relimiter.ini` and `DisplayCommander.ini` templates merged on install or deployed from the row's ⚙ or to every game (asked first; a changed add-on INI is kept as a restorable backup); ReShade presets from the ⚙, by dropping a preset `.ini`, or into every game; the effect list style and **Apply to all games** for the screenshot folder and hotkeys.
* **ReLimiter** global settings (display key, shared presets, DLSS info, FPS cap); **Luma ⚙** on Unreal Engine games (HDR on first boot, TAA); RenoDX **peak brightness** with Auto from your monitor or KDE's display settings, a per-game maximum and the UE-Extended choice; **OptiScaler presets** and a global **OptiScaler menu key**.
* **Follow newest DLSS / Streamline** (off by default): games whose files are on the previous newest version move to the new one; versions you picked, a game's own files and DLSS 1.x are left alone, and nothing ever moves to an older version.
* **Update inclusion:** leave a component out of Update All for every game (Settings → Maintenance) or one game (card menu **Update inclusion…**).
* **ⓘ notes** on component rows for the selected game (manifest notes, the RenoDX and OptiScaler wikis, change logs), and **Available HDR mods** with Nexus/Discord-only RenoDX mods and UE-Extended games.
* **OptiScaler:** choose the **DLL name** it is installed under (Automatic picks `dxgi.dll`, or the first free name when another RHI component already uses it); the **F5 DLSSNR** channel, with the channel list labelled "Channel: Stable, Nightly, DLSS NR or F5 DLSSNR" and a tooltip for each; pin a game to a **Nightly build**; **GPU type** INI templates (NVIDIA / AMD / Intel) you can edit once for every later install; **OptiPatcher** is installed and updated with OptiScaler; Force Reflex and game Reflex marker settings; the frame-generation `nvngx_dlssg.dll` joins the Streamline stack.
* **RTX Encore (Beta):** installed, updated and removed like other components, under a DLL name you choose (`version.dll` by default), optionally with NVIDIA's Neural Rendering file. Its row appears only for 64-bit DirectX 11/12 or Vulkan games that ship DLSS. Not yet confirmed on Linux: frame generation and Neural Rendering may work through Proton's DLSS; Smooth Motion cannot.
* **NR Cost Scaler:** runs Neural Rendering at reduced scale on top of the Neural Rendering method you installed; removing it puts the original file back exactly.
* **Control Ultimate Edition:** the RenoDX install can also set up OptiScaler frame generation (pinned to the nightly build upstream recommends) and the "Using HDR?" choice, and the dialog lists every required in-game setting.
* **RE Framework:** a button to delete its `_storage_` cache folder.
* **Shader profiles:** save, load, rename, export and import named shader-pack selections.
* **Accounts:** **Settings → Accounts & updates** has **Connect Nexus account** (API key), handling of Nexus "Mod Manager Download" links (each one confirmed before anything is downloaded), and a GitHub token field that checks the token before saving it.
* **Update notices:** at start-up RHI checks this fork's releases and tells you when a newer one exists. It never downloads or installs anything itself.
* **Library cards:** right-click (or Shift+F10 / the menu key) for Open folder, Favourite, Hide, View notes, **Create shortcut** (desktop or applications menu; created only when you choose it), **Nexus Mods page** for every game Nexus has a page for (no account needed), **Update inclusion…**, **Discussion / instructions** and **Web pages** (PCGamingWiki, Ultra+, ultrawide fix, HDR Gaming Database, each only where the game has one), **Copy game report** and, for Xbox games, **Show in the app that installed it**.
* **Edit game** can change the launch command, the name a game is shown under, the wiki mod name used to find its RenoDX mod, and its bitness.
* **Support:** Settings → Support copies all logs as one zip with your home folder, user name, Steam IDs, tokens and saved keys masked, opens the logs folder and exports the library's game data.
* **Ported from upstream RHI 2.7.6–2.8.7:** update history, the Available HDR mods list, manifest install warnings, the Donate dialog, mod authors and add-on file names on the game page, Unity per-game RenoDX settings, Control UE ray-reconstruction setup, Neural Rendering version swaps, ShortFuse ZZZ load order, PCGamingWiki single-file data, Luma mods published before they reach the wiki, and manifest DLSS/Streamline folder overrides.

## Interface & languages

* The main window works at every size from **640×480**, landscape or portrait. Toolbar buttons that do not fit move into a **…** menu, the library becomes a slide-out panel (**☰ Library**) below 900 pixels, and long paths are shortened in the middle with the full path in a tooltip. RHI never resizes your window.
* Every message is available in **English and Brazilian Portuguese**, including errors and launcher guidance.
* Errors in the interface are logged to `~/.local/state/rhi-linux/logs/rhi.log` instead of closing the app.
* The Update All summary counts what was really updated, what is current and what could not be checked, and names the first failure.
* **Saved filters:** save the current search as a named filter chip under the filter tabs.
* A **New mods** button appears in the toolbar while the RenoDX, Luma or Ultra+ lists have mods you have not seen.
* **Help** opens from Quick start, and **What's new** from Help or the version in the status bar.

## In progress

Not on `main` yet; being worked on now (upstream features still to port):

* A **global shader set** and global add-ons, with each game choosing Global, its own selection or Off.
* Installing a dropped or downloaded **RenoDX add-on** straight into the matching game.
* **Renaming an installed** ReShade or OptiScaler DLL in place.

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

This fork builds on:

* **[wizardkof/RHI-Linux](https://github.com/wizardkof/RHI-Linux)**, the native Linux port, which itself derives from
* **[RankFTW/RHI](https://github.com/RankFTW/RHI)**, the original Windows application.

The current synchronization target is:

```text
RHI 2.8.7
```

RHI Linux aims to preserve the functionality and workflow of the original RHI project while adapting Windows-specific behavior for Linux, Wine and Proton.

Functional parity is the goal; the underlying platform implementation does not need to be identical.

When an upstream feature can function under Wine or Proton, RHI Linux aims to provide an equivalent Linux-compatible workflow.

When Linux does not provide a real equivalent, the corresponding functionality may remain unavailable.

See [`UPSTREAM.md`](UPSTREAM.md) for more information about the relationship with the original project, and for how each upstream feature was adapted.

---

# 🙏 Original Projects

RHI Linux exists thanks to the original **RHI** project, the **[RHI Linux](https://github.com/wizardkof/RHI-Linux)** port by wizardkof, and the wider PC graphics modding ecosystem.

Original and third-party projects used or supported by RHI Linux include:

* [RHI](https://github.com/RankFTW/RHI)
* [ReShade](https://reshade.me/)
* [RenoDX](https://github.com/clshortfuse/renodx)
* [OptiScaler](https://github.com/optiscaler/OptiScaler)
* [OptiPatcher](https://github.com/optiscaler/OptiPatcher)
* [Luma Framework](https://github.com/Filoppi/Luma-Framework)
* [RE Framework](https://github.com/praydog/REFramework)
* [ReLimiter](https://github.com/RankFTW/ReLimiter)
* [Display Commander](https://github.com/pmnoxx/display-commander)
* [DLSSNR Cost Scaler](https://github.com/xenmods/DLSSNR-Cost-Scaler)
* [OptiScaler F5 DLSSNR](https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass)
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

Fork of `v0.9.23.4-beta.1` · upstream RHI `2.8.7`

[Back to top](#rhi-linux)

</div>
