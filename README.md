<div align="center">

# RHI Linux

### Native Linux port of RHI for Steam / Proton, Heroic, Lutris, Wine and Xbox / Game Pass

[![Release](https://img.shields.io/badge/RHI%20Linux%20Nonspaced-2.8.8-0ea5e9?style=for-the-badge)](https://github.com/nonspacial/RHI-Linux-Nonspaced/releases)
[![Upstream](https://img.shields.io/badge/Upstream%20RHI-2.8.8%20beta-6366f1?style=for-the-badge)](https://github.com/RankFTW/RHI)
[![Linux](https://img.shields.io/badge/Platform-Linux-f59e0b?style=for-the-badge\&logo=linux\&logoColor=white)](https://github.com/nonspacial/RHI-Linux-Nonspaced)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge\&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/nonspacial/RHI-Linux-Nonspaced?style=for-the-badge)](LICENSE)

**RHI Linux brings the RHI workflow to Linux with a native .NET 8 + Avalonia application for Windows games running through Wine and Proton.**

<br>

[Status](#-status) ·
[What it does](#-what-it-does) ·
[Supported Launchers](#-supported-launchers) ·
[Quick Start](#-quick-start) ·
[Changes](#-changes) ·
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

**Project:** [nonspacial/RHI-Linux-Nonspaced](https://github.com/nonspacial/RHI-Linux-Nonspaced)
**Release:** `v2.8.8` ([GitHub Releases](https://github.com/nonspacial/RHI-Linux-Nonspaced/releases)), level with upstream RHI 2.8.8 (its beta notes; upstream had not tagged 2.8.8 at release time)
**Upstream target:** `RHI 2.8.8` (beta)

RHI Linux is a native Linux port of the RHI workflow, adapted for Windows games running through Wine and Proton. It follows upstream RHI up to 2.8.8 and adds Linux-specific safety around installs, downloads and Wine prefixes. It started from [wizardkof/RHI-Linux](https://github.com/wizardkof/RHI-Linux) (GPL-3.0).

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

## ✨ What it does

RHI Linux Nonspaced:

* **scans large libraries quickly and in little memory.** It never follows a Wine prefix's drive links into every mounted drive; a full scan of a 21-game library takes about 1.4 s and 77 MB.
* **never leaves a game half-modded.** Installs, updates and removals are journaled and roll back as a whole if anything fails, including after a crash or power cut.
* **checks what it downloads.** Each download source has its own list of allowed hosts and a size limit, and published checksums are verified. A download that stops sending data is given up after a minute instead of holding up Update All.
* **leaves your Wine prefixes alone while they run**, and keeps DLL overrides you set yourself.
* **follows upstream RHI to 2.8.8**, including OptiScaler F5 DLSSNR, nightly pinning, OptiPatcher, the NR Cost Scaler, RTX Encore, Control Ultimate Edition frame generation, Universal MFG's MFG Unlocker releases and Nexus-only Luma mods.
* **lists Xbox / Game Pass games** from Xodus-format installs and from a configurable list of Xbox game folders.
* **lists games from other store launchers inside Wine prefixes** (Epic Games Launcher, EA app, Ubisoft Connect, Battle.net, Rockstar and itch.io), from the itch.io app for Linux, and non-Steam games added to Steam.
* **adds RTX Encore** as a managed component (not yet confirmed on Linux).
* **installs ReShade on Vulkan games** as a Vulkan layer set up for that one game (not yet confirmed in a real game).
* **adds DX9 HDR** for DirectX 9 games, with a choice of presets (not yet confirmed in a real game).
* **adds upstream's RenoDX `Engine.ini` settings for Unreal Engine games** (colour-grading updates, and HDR output for UE-Extended), each with a switch in RenoDX's ⚙.
* **picks the Neural Rendering DLL made for your graphics card**, hides the ones made for another card, and moves "Latest" installs onto the right one with Update All.
* **checks a game's Neural Rendering set-up** on a read-only page: graphics card, Neural Rendering DLL, recommended method, anti-cheat folders and known errors in the logs, each problem with where in RHI to fix it.
* **sets NVIDIA DLSS presets, render scale and frame generation per game** through Proton's NVIDIA layer, with default presets that **Apply defaults** and **Batch DLSS** copy into each game.
* **installs the RenoDX or Luma add-on inside a dropped or watched archive** (`.zip`, `.7z`, `.rar`) after checking the archive.
* **adds a global shader set and global add-ons**, with each game choosing the global set, its own list or none; every shader pack gets ReShade's own headers, and applying shaders changes only what changed.
* **shows HDR launch controls only where they can apply:** the HDR section hides for games RHI's data says have no HDR (unless RHI installed an HDR mod), and the Legacy HDR WSI tick shows only on NVIDIA drivers older than 595.
* **follows the newest DLSS and Streamline** for games that were on the previous newest, if you turn it on, and lets you leave any component out of Update All, for one game or every game.
* **updates ReShade Addon Manager add-ons** with Update All, and can download every shader pack at start-up.
* **ports upstream's INI templates, ReShade presets, peak brightness, ReLimiter, Luma and OptiScaler settings**, the ⓘ notes on component rows and the web links on library cards.
* **adds Nexus Mods and GitHub accounts, update notices, desktop shortcuts, a right-click menu on library cards, saved filters, masked log bundles and game reports.**
* **fits any window size** from 640×480 up, landscape or portrait, in English or Brazilian Portuguese.

The full list is under [Changes](#-changes) and in [`CHANGELOG.md`](CHANGELOG.md).

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

Tools, DLC and other entries Steam itself does not class as games are not listed. A non-Steam shortcut to the EA app, Ubisoft Connect, Battle.net, Rockstar or itch.io gets no card of its own; every game of those stores found in the shortcut's prefix is listed instead (see [Other store launchers](#other-store-launchers)). Play starts the launcher through Steam for that launcher's own games; a game from another store in the same prefix has no launch command until you set one in **Edit game**. Installers, native programs and scripts added as shortcuts are not listed.

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

Games installed inside a launcher that runs through Lutris (the Epic Games Launcher, Ubisoft Connect, Battle.net, the EA app, Rockstar, itch.io) get their own cards. They are modded in their real folder and started through their own launcher. A Lutris entry that Lutris' Epic, Ubisoft, EA or Battle.net service made for one game starts only that game; a game in a prefix that has only other games' entries is listed without a launch command until you set one in **Edit game**. Lutris entries with no real game folder (applications such as the launchers themselves) are not listed as games.

## Other store launchers

Games installed by the **Epic Games Launcher (the classic one and the new one), the EA app, Ubisoft Connect, Battle.net, the Rockstar Games Launcher or itch.io** inside a Wine, Lutris or Heroic prefix are found from that launcher's own install records, so each gets its own card even when it is installed outside the launcher's default folder. RHI only reads those records; it never changes them.

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

## From a release (AppImage)

Download `RHI-Linux-v2.8.8-x86_64.AppImage` and `RHI-Linux-v2.8.8-x86_64.AppImage.sha256` from [GitHub Releases](https://github.com/nonspacial/RHI-Linux-Nonspaced/releases) into the same folder, check the download, make it executable and run it:

```bash
sha256sum --check RHI-Linux-v2.8.8-x86_64.AppImage.sha256
chmod +x RHI-Linux-v2.8.8-x86_64.AppImage
./RHI-Linux-v2.8.8-x86_64.AppImage
```

`sha256sum --check` must print `OK`; if it does not, delete the file and download it again. The AppImage bundles the .NET runtime, so nothing from .NET needs installing (see [Runtime requirements](#runtime-requirements) for the archive tool some components need). RHI tells you when a newer release is published (Settings → Accounts & updates); it never downloads or installs one itself.

## Building from source on CachyOS / Arch Linux

Install the required packages:

```fish
sudo pacman -S --needed dotnet-sdk dotnet-runtime-8.0 7zip git
```

Building from source needs a .NET 10 SDK (any 10.0 feature band, pinned by `global.json`) and the .NET 8 runtime the app targets. NuGet restores are locked by the committed `packages.lock.json` files; after changing a package reference, run `dotnet restore --force-evaluate` and commit the updated lock files.

Clone the repository:

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

> [!WARNING]
> Before going back to 2.8.7 (27 October 2026, the first release), do these first; it does not know what 2.8.8 added:
>
> * **Remove whatever RHI installed on Epic Games Launcher games inside a Wine, Lutris, Heroic or Steam prefix** if you want it managed there: 2.8.7 does not list games it could only find through the Epic launcher's records, so it can neither update nor remove what is installed on them. Their install records are kept, and 2.8.8 manages them again.
> * **Expect Legacy HDR WSI back:** 2.8.8 hides the tick and leaves `ENABLE_HDR_WSI=1` out on an NVIDIA driver 595 or newer, but keeps the tick saved. 2.8.7 applies a saved tick on every driver again (a game can then stop while loading); untick **Legacy HDR WSI** in those games once you are back on 2.8.7.
>
> 2.8.7 also brings back what 2.8.8 fixed: its shader sync takes `ReShade.fxh`/`ReShadeUI.fxh` out of RHI's shader folder again (packs such as Prod80 stop compiling), its Update All puts back an add-on file you deleted outside RHI, it cannot update Universal MFG past 1.4.x, it shows the HDR section on every game (a saved HDR mode applies again at a direct start), and offline it turns Luma mode on for the games the old manifest listed.

> [!WARNING]
> Before going back further, to the 22 October 2026 build (the one that added default DLSS presets and add-ons inside archives), do these first; it does not know what the later 22 October 2026 build added (the Neural Rendering DLL per card, also in 2.8.7):
>
> * **In each game with ShortFuse or DLSS5 Feeder whose Neural Rendering DLL is on Latest, pick the version made for your card by hand** in the **NR DLL** dropdown (on an RTX 20, 30 or 40 card, the one marked `(20/30/40/50)`). The older build takes the newest version in the list as Latest, whatever the card, so its next install or version swap on Latest puts back a DLL made for another card (`310.8.3 (50xx)` on an RTX 40). It does not undo what this build's Update All moved, and it keeps a version picked by hand.
> * **Leave OptiScaler out of Update All** (card menu **Choose what Update All changes…**) in games with OptiScaler DLSS NR or F5 DLSSNR. The older build's Update All replaces their Neural Rendering DLL with the newest one in the list, also one made for another card.
>
> The older build lists every Neural Rendering version, also those made for another card, and installs F5 DLSSNR without the driver question. A Nightly build switched here stays pinned and out of Update All there, but the older build cannot switch it again (remove OptiScaler and install the build you want). A download that stops sending data can make it wait without end; press Cancel then.

> [!WARNING]
> Before going back further, to the 21 October 2026 build (the internal tidy-up after the one that added RenoDX's `Engine.ini` settings for Unreal games), also do these; it does not know what the 22 October 2026 build that added default DLSS presets added:
>
> * **Close RHI and check that `~/.local/share/rhi-linux/journal` is empty.** When this build cannot finish undoing a change, it holds that game until the undo succeeds. The older build does not: it lets that game change again, and a later start may then undo the old change over the new one. Start this build once more first (make the game's folder writable again if that was the problem) and let it finish the undo.
> * **Delete `~/.cache/rhi-linux/dropped-archives` if it is there.** It holds RHI's own copies of dropped and watched archives; this build clears what an interrupted install left there when it starts, the older build never does.
>
> The older build forgets the default DLSS presets the next time it saves the settings; presets already copied into a game's NVIDIA settings stay. It does not open archives: a dropped or watched archive only gets its old notice.

> [!WARNING]
> Before going back further, to the 21 October 2026 build that added DX9 HDR and the NVIDIA settings, also do these; it does not know what the RenoDX `Engine.ini` build added:
>
> * **Remove RenoDX from every Unreal Engine game where RHI installed it** (at least where its ⚙ shows **Fix colour grading updates (Unreal)** or **Turn on the game's HDR output (UE-Extended)**). This build's removal takes its lines out of the game's `Engine.ini` and ReShade.ini and gives `Engine.ini` back the lock it had. The older build leaves those lines in place and `Engine.ini` read-only, and its next RenoDX update forgets that RHI wrote them, so after that no build takes them out; remove them by hand then (`r.LUT.UpdateEveryFrame=1`, the UE-Extended HDR lines).
> * **Close RHI and check that `~/.local/share/rhi-linux/journal` is empty.** A change this build left unfinished (RHI was closed or crashed in the middle of one, or could not read its install records afterwards) may hold a step the older build cannot undo — putting back `Engine.ini`'s lock — and it would keep that entry. Start this build once more first and let it finish or undo the change.
>
> Presets, **Apply to all games** and Luma ⚙ changes made here stay as they are; the older build simply does not know that some of those ReShade.ini lines are RHI's.

> [!WARNING]
> Before going back further, to the 20 October 2026 build (the one that added Addon Manager updates and non-Steam shortcuts), also do these; it does not know what the 21 October 2026 DX9 HDR build added:
>
> * **Remove DX9 HDR from every game that has it.** The older build drops DX9 HDR's install record the first time it saves RHI's install records; after that no build can remove its `d3d9.dll`, `dxvk.conf` and Wine override, and a ReShade that DX9 HDR moved to its Vulkan layer stays there. Removing DX9 HDR also puts that ReShade back as `d3d9.dll`.
> * **Remove ReShade from XNA games** (Terraria and the like). The older build does not know the copies in the prefix's .NET assembly folder: it does not update them, and its removal leaves at least the ReShade.ini copy there.
> * **Remove OptiScaler from a game where you renamed its DLL while RHI's ReShade runs through it.** The older build moves ReShade to OptiScaler's current name when it removes OptiScaler, and ReShade does not load under names such as `wininet.dll`. This build puts ReShade back under its own name. Other renamed DLLs need nothing.
> * **A RenoDX add-on you installed from a dropped or downloaded file:** leave RenoDX out of Update All for that game (card menu **Update inclusion…** in the older build) or remove it, or the older build's Update All replaces it with the RenoDX list's build for the game.
>
> The older build forgets the NVIDIA settings the next time it saves the library and launches without them; a line you pasted into Steam's launch options stays there until you remove it. It ignores the global shader set and add-ons (`shader-addon-sets.json`): every game keeps the shader packs and add-ons it has. Back on this build, the next Apply in a game's Shaders/Add-ons dialog or the next save of the global set brings each game back to the choice saved here, replacing shader or add-on changes made on the older build.

> [!NOTE]
> Going back further, to the 15 October 2026 build (the one that added ReShade for Vulkan games), needs nothing more removed (for older builds, the notes above still apply). Older builds still read the ReShade Addon Manager's selection from `addon-manager.json`, but drop its entries from RHI's install records the first time they save them. Back on the current build, RHI takes the selection up again from that file, and Update All downloads those add-ons once more. An older build shows no card for non-Steam games added to Steam, so components installed on them cannot be updated or removed from it; their install records are kept, and the current build manages them again.

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

# 🧾 Changes

Everything below is on the `main` branch. Dated entries are in [`CHANGELOG.md`](CHANGELOG.md); how each upstream feature was adapted for Linux is in [`UPSTREAM.md`](UPSTREAM.md).

## Performance

* **Library scan.** The file indexer no longer follows folder links, so a Wine prefix's drive links (`z:` → `/`, others to your home folder and drives) can no longer pull the whole machine into a scan. Each game folder is capped at 200,000 files, and the scan looks only as deep as it needs to find each game's executable; the full file list is built only for the game you select.
* **Lutris.** Executables written relative to the prefix or with `$GAMEDIR` are resolved, so these games no longer fall back to the whole prefix as their folder. Result: a full scan in about 1.4 s and 77 MB, where one Lutris entry used to pass 2.8 GB and keep growing.
* **Add game** rescans only the manually added games, not every launcher.
* Each Steam library is read once, even when several paths lead to it.
* The library is listed while the DLSS manifest is still downloading; that download now gives up after 15 seconds instead of holding up start-up for minutes.
* Selecting a game no longer re-reads the install records hundreds of times. Settings files are re-read only when they have changed, and the analysis of recently viewed games is kept in a small, bounded cache.
* Searching waits until you stop typing and keeps the selected game if it still matches, instead of restarting its analysis on every key.
* Large files are scanned once, in a fixed-size window, instead of being read whole for each check.
* GitHub answers are cached, kept between launches and revalidated, and overlapping lookups share one request, which saves the anonymous API allowance; a saved answer is used only once GitHub confirms it, or while GitHub's rate limit lasts. A GitHub API request is given up after 30 seconds and counts as could not be checked.

## Safety

* **Crash-safe installs.** Every install, update and removal is journaled. Files are written to a temporary copy, checked, then moved into place. If anything fails, or RHI is interrupted, the change is rolled back as a whole the next time RHI starts. Files you changed yourself in the meantime are kept, never overwritten. A removal is marked on its record before it starts, so an interrupted removal is finished or undone, never left half-done. This covers DLSS5 Feeder and Luma with dgVoodoo2 and their INI edits, ReShade's shader-pack folder, the ReShade Addon Manager's selection together with its add-on files, RenoDX with Control Ultimate Edition's files and its `Engine.ini` lines, peak brightness, ReShade presets, **Apply to all games** and the Luma ⚙. When the install records are saved but the save then reports an error, the change is kept and its line says so (an install, a ⚙ switch, Shaders/Add-ons Apply, a preset with its shader packs, a batch button, Update All or Repair after move) — also for a preset installed into every game with its shader packs, for the game's add-ons applied after ReShade's install, and on a "shaders applied but the add-ons failed" line. If the records cannot be read at all afterwards, nothing is undone; that game's next change is refused with a plain reason until they can be read again, and the message says to restart RHI to finish it. If RHI cannot finish undoing a change (a folder or file it cannot write), that game is held: its next change tries the undo again first and is refused until the undo succeeds — the message says to restart RHI to finish it; other games carry on. If RHI's own copy of a file it needs for the undo is gone (deleted, or the game was reinstalled), the change is closed instead and RHI names the file, so you can remove that component and install it again. A game folder that is not there at all (its drive not mounted, or unplugged) is not taken for a gone copy: the game stays held until the folder is back. When only a folder inside a game folder that is there was deleted, RHI's copy went with it: the change is closed and the file named.
* **Cancel.** Cancel stops an operation only before it has changed anything in the game or prefix. After that point the operation finishes or rolls back completely, so a cancelled install never leaves half-installed files.
* **Verified downloads.** Each source (ReShade, OptiScaler, RenoDX, Luma and the rest) has its own policy: HTTPS only, a fixed list of allowed hosts (checked again after redirects) and a size limit. Checksums are verified whenever the publisher announces one; where none is published, RHI records the SHA-256 of what it installed. A download that stops sending data is given up after 60 seconds without a byte (a slow one that keeps arriving is never cut off), and a server that never answers after 5 minutes; Update All then counts that game as failed or not checked and carries on, and it never reads as Cancel. Cached downloads are reused only when they still match what was recorded. Archives are unpacked with size limits and integrity checks, and 7-Zip archives that contain links are refused. Per-source details: [`docs/download-trust.md`](docs/download-trust.md).
* **Wine registry.** RHI does not edit a prefix's registry while Wine is running in that prefix; it changes nothing and asks you to close the game first. DLL overrides you set yourself are kept, and removing a component puts back the value that was there before, unless you have changed it since.
* **Your files.** RHI never overwrites or deletes a file it did not install. If a ReShade or OptiScaler that RHI did not install is already there, RHI refuses to add a second one rather than have both load. Game settings files you have locked read-only (such as `Engine.ini`) are edited the way upstream does and locked again, and stay locked when a change is undone. A ReShade.ini linked to your shared file in `~/.config/ReShade` stays a link whenever a change to it is undone.
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
* **HDR launch section** only for games that can use HDR: it is hidden when RHI's data (its own list, PCGamingWiki, the Microsoft Store listing for Xbox games) says the game has no HDR and RHI installed no HDR mod on it; unknown or offline shows it, and the section label's tooltip says where the answer came from. A hidden section's saved HDR mode is kept but not used. The **Legacy HDR WSI** tick shows only with an NVIDIA driver older than 595; elsewhere a saved tick stays saved but `ENABLE_HDR_WSI=1` is left out of every launch line and direct start.
* **Change resolution while playing** (its own **Resolution** row, also where the HDR section is hidden): a game RHI starts directly switches the display to the size you chose and back when it ends, also when RHI was closed meanwhile; with Gamescope the size goes to Gamescope. Uses `kscreen-doctor` on KDE Plasma or `wlr-randr` on wlroots desktops. Not yet confirmed on a real desktop.

## Features

* **Shader packs:** ReShade's `ReShade.fxh` and `ReShadeUI.fxh` go with every pack (in RHI's own shader folder), applying shaders changes only the files that changed, and a pack you turn off leaves no empty folders; a file in RHI's shader folder that is not the one RHI placed stops the sync with a line saying what to do.
* **ReShade Addon Manager:** its add-ons are updated by Update All and the background update pass; one that cannot be compared with the add-on list is reported as "could not be checked". **Download every shader pack at start-up** (Settings, on by default) fills RHI's shader cache in the background. **Deploy ReShade.ini** is recorded, so removing ReShade takes back what it added.
* **Global shader set and add-ons:** Settings → ReShade → **Global shaders and add-ons…** sets the shader packs and Addon Manager add-ons every game gets by default. In a game's **Shaders/Add-ons** dialog each tab chooses **Use the global set**, **Choose for this game** or **None**. Changing shaders only changes the shader files and ReShade.ini's search paths, never ReShade itself, and is recorded in the update history (other ReShade.ini keys you changed stay as they are); **Save global set** has its own **Cancel** while it runs; a change to the global set leaves alone games you left out of Update All for ReShade (shaders) or the Addon Manager (add-ons).
* **ReShade:** choose the DLL name ReShade is installed under, and change it later in place (the Wine override and RHI's record follow), with **Reset DLL names** to go back to the automatic names; a **No Addons** channel (the ReShade build without add-on support). Switching a game to No Addons turns the add-ons RHI installed off (they stay in the game folder and are not updated meanwhile); switching back to Stable or Nightly turns them on again. Falls back to a cached copy when reshade.me is down. Installs and updates fill in only the keys a `reshade.ini` template adds that the game's ReShade.ini does not have yet; a ReShade.ini linked to your shared file in `~/.config/ReShade` stays a link, also when an install is rolled back.
* **ReShade for XNA games** (Terraria and the other games the RHI manifest lists): ReShade goes into the game folder and into the .NET assembly folder of the game's prefix, where XNA loads it. After **Repair after move** its ReShade.ini copy there points to the game's new folder (one you changed is kept, and the repair line says what to set).
* **ReShade for Vulkan games** (Proton or Wine 8 or newer): on a game whose graphics API is Vulkan the row reads **ReShade · Vulkan** and installs ReShade as a Vulkan layer for that game only (the Khronos loader beside the game, the layer and its registration in the prefix). DirectX games are unchanged; a plain reason is given where it cannot work. Changing the API to or from Vulkan while ReShade is installed is refused until it is removed. Not yet confirmed in a real game.
* **Legacy ReShade** versions from a list, the manifest's per-game pins (Max Payne 3 → 6.4.1) on fresh installs, and the manifest's ReShade note for the game.
* **INI templates and presets:** editable `reshade.ini`, `reshade.vulkan.ini`, `relimiter.ini` and `DisplayCommander.ini` templates merged on install or deployed from the row's ⚙ or to every game (asked first; a changed add-on INI is kept as a restorable backup); ReShade presets from the ⚙, by dropping a preset `.ini`, or into every game; the effect list style and **Apply to all games** for the screenshot folder and hotkeys. Presets and **Apply to all games** are recorded, so removing ReShade still deletes a ReShade.ini RHI created, and the chosen preset stays when ReShade writes a new ReShade.ini.
* **ReLimiter** global settings (display key, shared presets, DLSS info, FPS cap); **Luma ⚙** on Unreal Engine games (HDR on first boot, TAA); RenoDX **peak brightness** with Auto from your monitor or KDE's display settings, a per-game maximum and the UE-Extended choice; **OptiScaler presets** and a global **OptiScaler menu key**.
* **Follow newest DLSS / Streamline** (off by default): games whose files are on the previous newest version move to the new one; versions you picked, a game's own files and DLSS 1.x are left alone, and nothing ever moves to an older version.
* **Luma mods only on Nexus Mods** are checked on Nexus with your connected account: the Luma row's **Nexus** button reads **Update on Nexus** when Nexus has a newer file and **Open on Nexus** when RHI has not installed the mod or no account is connected; RHI never downloads them. Luma mode is on only where you chose **Enabled**.
* **Choose what Update All changes:** leave a component out of Update All for every game (Settings → Maintenance) or one game (card menu **Choose what Update All changes…**).
* **ⓘ notes** on component rows for the selected game (manifest notes, the RenoDX and OptiScaler wikis, change logs; the ⓘ is highlighted where the game has notes of its own), and **Available HDR mods** with Nexus/Discord-only RenoDX mods and UE-Extended games.
* **OptiScaler:** choose the **DLL name** it is installed under (Automatic picks `dxgi.dll`, or the first free name when another RHI component already uses it) and change it in place while it is installed, also while RHI's ReShade runs through it; the **F5 DLSSNR** channel, with the channel list labelled "Channel: Stable, Nightly, DLSS NR or F5 DLSSNR" and a tooltip for each, and a question before installing it when the NVIDIA driver is older than 615.71.09 (the Linux equivalent of upstream's Windows 616.56) or cannot be read, or when the Neural Rendering DLL it would load is a 310.8.0 build on a 615 or newer driver (**Install anyway** goes ahead; Update All only says so in its line); pin a game to a **Nightly build**, and change an installed Nightly's build in place from the ⚙ (**Apply**, with its own **Cancel** while the build downloads; OptiScaler.ini, the DLL name and the Wine override stay; the build Control Ultimate Edition's frame generation needs stays locked); **GPU type** INI templates (NVIDIA / AMD / Intel) you can edit once for every later install; **OptiPatcher** is installed and updated with OptiScaler; Force Reflex and game Reflex marker settings; the frame-generation `nvngx_dlssg.dll` joins the Streamline stack.
* **RTX Encore (Beta):** installed, updated and removed like other components, under a DLL name you choose (`version.dll` by default), optionally with NVIDIA's Neural Rendering file. Its row appears only for 64-bit DirectX 11/12 or Vulkan games that ship DLSS. Not yet confirmed on Linux: frame generation and Neural Rendering may work through Proton's DLSS; Smooth Motion cannot.
* **Neural Rendering DLL per graphics card:** **Latest** is the newest Neural Rendering DLL made for your card's generation (from the DLSS list's `(50xx)` / `(20/30/40/50)` labels; with several cards the newest counts), and versions made for another generation are left out of the **NR DLL** dropdown, the DLSS swap row, **Batch DLSS** and the Settings default. When RHI cannot tell the card, every version is listed and the dropdown's tooltip says why. Update All moves ShortFuse and DLSS5 Feeder Full installs on **Latest** onto the DLL made for the card, in place and recorded in the update history; a version you picked by hand stays (its row says when it is made for another card), and a DLL RHI cannot identify or that was changed outside RHI is left as it is and counted as not checked. A Settings default or batch choice made for another card skips only that game's Neural Rendering step.
* **Check Neural Rendering:** a read-only page per game, opened with **Check…** right after the **Neural rendering** line of the game's panel or with **Check Neural Rendering…**, the last item of the card's right-click menu. It lists the graphics card and its generation (the driver version in its tooltip); the Neural Rendering DLL in the game, what put it there and whether it is made for this card, with the version made for it and a warning for a 310.8.0 build on a 615 or newer driver; the recommended Neural Rendering method and the ones in use; anti-cheat folders; and known errors in the ReShade, OptiScaler and DLSS5 Feeder logs beside the game's executable (only the last 2 MB of each is read), including the Feeder's "driver did not confirm Neural Rendering", flat-depth and stopped-after-a-display-change lines. Each problem has a line saying what to do. Both openers show only for Windows games on an NVIDIA card with a Neural Rendering DLL or a Neural Rendering method RHI installed. The page changes no files.
* **Universal MFG** takes the publisher's MFG Unlocker releases (1.5.0 and newer) and updates a 1.4.x install in place. **MFG Ada Unlock, DLSSG 20/30 Unlock and the Ultimate ASI Loader** are updated by Update All; the ASI Loader only for the bitness it was installed for. An update never puts back a component file you deleted outside RHI, nor replaces one you changed: Update All names it as not checked with the reason, and the row offers **Remove**.
* **NR Cost Scaler:** runs Neural Rendering at reduced scale on top of the Neural Rendering method you installed; removing it puts the original file back exactly. Update All updates it and keeps a scale you set in `nvngx_dlssnr.ini`.
* **DX9 HDR** (a row on DirectX 9 games): installs EndlesslyFlowering's DXVK HDR build beside the game with a preset you choose from its ⚙ (Safest to Experimental); HDR launch must be on for it to show HDR. A ReShade already installed as `d3d9.dll` moves to its Vulkan layer and comes back when DX9 HDR is removed. Not offered on Xbox / Game Pass installs or XNA games; the row says when the game's launch command turns DX9 HDR off (`PROTON_USE_WINED3D`). Update All never puts back a DX9 HDR `d3d9.dll` you deleted. Not yet confirmed in a real game.
* **NVIDIA settings** (DLSS section → **NVIDIA settings…**, for DLSS games on NVIDIA GPUs with the NVIDIA driver): per game, the DLSS preset, render scale, Ray Reconstruction and Frame Generation presets where the game has those files (Frame Generation on RTX 40 or newer), multi frame generation on RTX 50 cards (GeForce RTX 5050–5090 and Blackwell workstation cards), and **Newest DLSS from NVIDIA**. They are passed to Proton's NVIDIA layer at launch (Proton 10 or newer); for Steam games the dialog gives the launch-options line to paste into Steam. **Restore all** sets them back to Default. **Default presets** (Settings → DLSS/Streamline, for DLSS, Ray Reconstruction and Frame Generation) are copied into a game's NVIDIA settings by **Apply defaults** and **Batch DLSS**, only for the rows that game's dialog shows and never over a DLSS 1.x file; Default leaves the game's own choice, and nothing is applied at launch from the defaults alone. The batch dialog starts its three preset dropdowns from them, and its **Restore selected** sets the presets back to Default.
* **Dropped and downloaded add-ons:** a RenoDX add-on dropped on the window, or saved to the watched folder, installs into the game you pick (the matching game is preselected; a file from the watched folder is removed from it once installed); Update All cannot check it. Another ReShade add-on you drop (one that no RHI row installs itself) goes into Custom Add-ons and is ticked in that game's Shaders/Add-ons list, which becomes the game's own list; only games with ReShade in their folder are offered, and not those where RHI's ReShade is the No Addons build. A dropped `.zip`, `.7z` or `.rar` (or one saved to the watched folder with "renodx" or "luma" in its name) is copied into a private folder of RHI's and checked first: an archive with unsafe names, links or members larger than allowed is refused before anything is taken out. The RenoDX or Luma add-on inside is offered in the same dialog, with an **Add-on** dropdown when there are several, and installed as the file dropped alone; a Luma add-on installs the whole package. A watched archive is removed after a successful install while it is still the file RHI read; a dropped one is never removed. `.7z` and `.rar` need 7-Zip or bsdtar.
* **Control Ultimate Edition:** the RenoDX install can also set up OptiScaler frame generation (pinned to the nightly build upstream recommends) and the "Using HDR?" choice, and the dialog lists every required in-game setting.
* **RenoDX on Unreal Engine games:** install, update, Update All and a dropped add-on put upstream's `r.LUT.UpdateEveryFrame=1` into the game's `Engine.ini` (not where the RHI manifest says not to or the game uses a custom Engine.ini file), with **Fix colour grading updates (Unreal)** in RenoDX's ⚙ to turn it off. `Engine.ini` is read-only while RHI's line is in it, as upstream does; turning it off or removing RenoDX takes only RHI's line out and gives back the lock the file had. A line you already had is left alone, and RHI writes the file only once the game has made its config folder. UE-Extended RenoDX also gets upstream's HDR lines in `Engine.ini` and its native-HDR keys in ReShade.ini, with **Turn on the game's HDR output (UE-Extended)** in the ⚙.
* **RE Framework:** a button to delete its `_storage_` cache folder.
* **Shader profiles:** save, load, rename, export and import named shader-pack selections.
* **Accounts:** **Settings → Accounts & updates** has **Connect Nexus account** (API key), handling of Nexus "Mod Manager Download" links (each one confirmed before anything is downloaded), and a GitHub token field that checks the token before saving it.
* **Update notices:** at start-up RHI checks this project's releases on GitHub and tells you when a newer one exists. It never downloads or installs anything itself.
* **Library cards:** right-click (or Shift+F10 / the menu key) for Open folder, Favourite, Hide, View notes, **Create shortcut** (desktop or applications menu, with the game's own icon; created only when you choose it), **Nexus Mods page** for every game Nexus has a page for (no account needed), **Choose what Update All changes…**, **Discussion / instructions** and **Web pages** (PCGamingWiki, Ultra+, ultrawide fix, HDR Gaming Database, each only where the game has one), **Copy game report** and, for Xbox games, **Show in the app that installed it**.
* **Edit game** can change the launch command, the name a game is shown under, the wiki mod name used to find its RenoDX mod, and its bitness.
* **Support:** Settings → Support copies all logs as one zip with your home folder, user name, Steam IDs, tokens and saved keys masked, opens the logs folder and exports the library's game data.
* **Ported from upstream RHI 2.7.6–2.8.8:** update history, the Available HDR mods list, manifest install warnings, the Donate dialog, mod authors and add-on file names on the game page, Unity per-game RenoDX settings, Control UE ray-reconstruction setup, Neural Rendering version swaps, ShortFuse ZZZ load order, PCGamingWiki single-file data, Luma mods published before they reach the wiki, manifest DLSS/Streamline folder overrides, deferred updates of games that were running, and the Epic Games Launcher inside prefixes.

## Interface & languages

* The main window works at every size from **640×480**, landscape or portrait. Toolbar buttons that do not fit move into a **…** menu, the library becomes a slide-out panel (**☰ Library**) below 900 pixels, and long paths are shortened in the middle with the full path in a tooltip. RHI never resizes your window.
* Every message is available in **English and Brazilian Portuguese**, including errors and launcher guidance.
* Errors in the interface are logged to `~/.local/state/rhi-linux/logs/rhi.log` instead of closing the app.
* The Update All summary counts what was really updated, what is current, what could not be checked and what you left out, names the first failure, and names each item it could not check for a reason on your machine. The DLSS result line names each failed game with its reason.
* **Saved filters:** save the current search as a named filter chip under the filter tabs.
* A **New mods** button appears in the toolbar while the RenoDX, Luma or Ultra+ lists have mods you have not seen.
* **Help** opens from Quick start, and **What's new** from Help or the version in the status bar.

## In progress

Not on `main` yet: nothing at the moment.

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

RHI Linux Nonspaced ports **[RankFTW/RHI](https://github.com/RankFTW/RHI)**, the original Windows application, to Linux. It started from **[wizardkof/RHI-Linux](https://github.com/wizardkof/RHI-Linux)** (GPL-3.0).

The current synchronization target is:

```text
RHI 2.8.8 (beta notes; untagged at release time)
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

Release `v2.8.8` · level with upstream RHI `2.8.8`

[Back to top](#rhi-linux)

</div>
