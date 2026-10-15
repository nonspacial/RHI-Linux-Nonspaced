# Changelog

Changes in this fork ([nonspacial/RHI-Linux-Nonspaced](https://github.com/nonspacial/RHI-Linux-Nonspaced)) since the
wizardkof RHI Linux beta it started from, `v0.9.23.4-beta.1`. Newest first. Each entry is the date it reached `main`.
How each upstream RHI feature was adapted for Linux is described in [`UPSTREAM.md`](UPSTREAM.md); download sources are
listed in [`docs/download-trust.md`](docs/download-trust.md).

The app still reports version `0.9.23.4-beta.1`; the fork has not published a release yet.

## In progress

Not on `main` yet (upstream features still to port):

- ReShade for Vulkan games.
- Following the newest DLSS and Streamline from the manifest automatically.
- Update exclusions: keeping a component on a game, or everywhere, out of Update All.
- ReShade presets and user INI templates applied on install.
- Info dialogs for components and links on library cards.
- Renaming a game in Edit game.

## Wave 4 — 2026-10-15: store launchers, RTX Encore, No Addons add-ons, OptiScaler DLL name

> [!NOTE]
> A build older than this one does not know RTX Encore, the OptiScaler DLL-name choice or add-ons turned off by No
> Addons. The first time it saves a ReShade DLL name, it forgets RTX Encore's saved DLL name and Neural Rendering
> choice, and the saved OptiScaler DLL name, for every game where that component is not installed. It cannot update or
> remove RTX Encore: remove RTX Encore and switch No Addons games back to Stable or Nightly first if you want them
> handled on the older build.

Platforms & launchers

- Games of the **EA app, Ubisoft Connect, Battle.net, the Rockstar Games Launcher and itch.io** installed inside a Wine,
  Lutris or Heroic prefix get their own cards, found from the launcher's own install records (read only), wherever the
  game is installed. Play starts the launcher with the game's own start argument.
- Games of the **itch.io app for Linux** (native or Flatpak) are listed; Play opens the game in the itch app.
- In Heroic, a game is handed only to its own store's sideloaded launcher; otherwise the card has no launch command
  until you set one in Edit game.
- HDR guidance for games started through a store launcher says the profile cannot be applied through it, instead of
  asking for a wrapper setting the launcher does not have.
- Xbox / Game Pass cards carry the name of the product that was installed, so **Control Ultimate Edition** is no longer
  listed as "Control Standard Edition". A product whose name only adds "Standard Edition" keeps the game's plain
  name.
- The Settings tab is called **Xbox / Game Pass folders** and describes Xodus, a Linux Game Pass client still in
  development, with a link to it. The card menu item is **Show in the app that installed it**.
- On a system without Xodus or another Xbox installer app, nothing Xbox-related appears in the library, the scan
  warnings, the log or the card menu.

Features

- **RTX Encore (Beta)** as a managed component: install, update and removal under a DLL name you choose (`version.dll`
  by default), optionally with NVIDIA's Neural Rendering file (exact version 310.8.0, checked). The download is checked
  against GitHub's digest and the release's SHA256SUMS. Its row appears only for 64-bit DirectX 11/12 or Vulkan games
  that ship DLSS. Not yet confirmed on Linux: frame generation and Neural Rendering may work through Proton's DLSS;
  Smooth Motion cannot. RTX Encore proxies are no longer mistaken for ReShade.
- **ReShade No Addons** now turns the add-ons RHI installed **off** (renamed in place, nothing deleted) and back **on**
  when the game goes back to Stable or Nightly. Add-ons that are off are not updated or swapped meanwhile, and Update
  All counts them separately. Your own add-ons are never touched.
- **OptiScaler DLL name:** choose the name OptiScaler is installed under in its ⚙ settings. Automatic picks `dxgi.dll`,
  or the first free name while another RHI component uses it; names other RHI components hold are not offered, and the
  install says so when it had to skip your saved choice.
- **Nexus Mods page** in the library card menu for every game Nexus has a page for (no account needed), including
  games whose store name Nexus spells differently, such as Skyrim Special Edition.

Safety

- The DLSS panel, Quick Apply, batch and Restore never replace a file owned by a Neural Rendering component (RTX
  Encore, ShortFuse, DLSS5 Feeder, OptiScaler DLSS NR/F5) or the original it kept.
- itch.io's database is read together with its write-ahead log, so it reads cleanly while itch is running.
- A folder that holds many games, such as `~/Games`, is never taken as one game's folder, also when a record names
  the place its symbolic link points to.

Interface

- OptiScaler's channel list is labelled "Channel: Stable, Nightly, DLSS NR or F5 DLSSNR", with a tooltip for each
  channel and a line describing the one chosen.
- The Xbox / Game Pass folders tab keeps `~/Games` on one line at small window sizes.

## Wave 3 — 2026-10-14: upstream RHI 2.8.6–2.8.7, Xbox / Game Pass, accounts

> [!WARNING]
> Before going back to an older build, remove OptiScaler F5 DLSSNR and set pinned OptiScaler Nightly games back to
> "Latest (auto-update)". See [`UPSTREAM.md`](UPSTREAM.md#rolling-back-to-a-build-older-than-wave-3-f5-dlssnr-nightly-pins).

Upstream 2.8.6–2.8.7

- OptiScaler **F5 DLSSNR** channel (janblade's multi-pass Neural Rendering fork). Its Windows-made archive is unpacked
  into the right folders.
- OptiScaler **Nightly build** pin: keep a game on one nightly build; the update check reports it as pinned and current.
- OptiScaler is recognised by its version information, so RTX Encore and other tools that mention OptiScaler are no
  longer mistaken for it.
- Only one component at a time owns a game's `nvngx_dlssnr.dll`; installing a second one is refused with the owner's
  name.
- GitHub "latest release" lookups ask for the release GitHub marks as Latest.
- Manifest DLSS and Streamline folder overrides (for games that keep them deeper than RHI searches).
- RE Framework: a button to delete its `_storage_` cache folder.
- Control Ultimate Edition: the RenoDX dialog lists every required in-game setting and can also set up OptiScaler
  frame generation (pinned build, as upstream) and the "Using HDR?" choice. Settings you change after the install are
  kept on update and removal.
- Available HDR mods also lists the games in the RHI Unity database.

Features

- **Xbox / Game Pass** games are listed from Xodus-format installs and from a configurable list of Xbox game folders.
  RHI is ready for games installed by Xodus (https://github.com/xodus-gaming/xodus), a Linux Game Pass client that is
  still in development. Play hands the game to the app that installed it and never starts the executable directly.
  RHI reads bitness from the game's package (the executables are encrypted; set Force API if a component cannot tell
  the graphics API) and changes nothing while that app is still downloading or updating the game.
- **ReShade DLL name**: choose the name ReShade is installed under (Automatic by default).
- ReShade **No Addons** channel.
- OptiScaler **GPU type** INI templates (NVIDIA / AMD / Intel), kept in `~/.config/rhi-linux/inis/` and never
  overwritten, so your edits apply to every later install.
- **OptiPatcher** is installed and updated with OptiScaler.
- **NR Cost Scaler** on top of an installed Neural Rendering method; removal puts the original file back exactly.
- **Shader profiles**: save, load, rename, export and import shader-pack selections.
- Luma mods published in a release before they reach the wiki.
- **Settings → Accounts & updates**: Connect Nexus account (API key), Nexus "Mod Manager Download" links (always
  confirmed before download), a GitHub token field that checks the token before saving it, and an update notice for new
  releases of this fork (notify only; nothing is downloaded or installed).
- **Library card right-click menu**: Open folder, Favourite, Hide, View notes, Create shortcut (desktop or applications
  menu, only when chosen), Nexus Mods page where known.

Performance and safety

- The library scan looks only as deep as it needs to find each game's executable; the full file list is built for the
  selected game only.
- Settings files are re-read only when they have changed.
- Removals are journaled like installs: an interrupted removal is finished or undone at the next start. Install and
  removal journaling now also covers ShortFuse, ReShade add-ons, OptiScaler, ReShade, Ultimate ASI Loader, DLSS
  Enabler, Universal MFG, RTX 20 FG Unlock, DOF Fix and the Addon Manager.
- OptiScaler refuses to install when an OptiScaler RHI did not install is already beside the game.

Interface

- Selecting another game scrolls the detail pane back to the top.
- Refresh clicked during another operation is queued instead of being dropped.
- Clear cache runs in the background.
- The Shaders/Add-ons dialog's status line is readable at its smallest size.

## Wave 2 — 2026-10-08: downloads, Wine registry, saved state, launching, Cancel

- **Verified downloads**: every source has its own allowed hosts (checked again after redirects) and size limit;
  published checksums are verified, and the SHA-256 of what was installed is recorded either way. Archives are unpacked
  with size limits and integrity checks. External tools (7-Zip, helpers) run with a time limit.
- GitHub answers are cached and revalidated, saving the anonymous API allowance.
- **Wine registry**: no edits while Wine runs in the prefix; your own DLL overrides are kept; removal restores the
  earlier value exactly; override changes are part of the install's rollback.
- **Library moves**: favourites, hidden games, edits and HDR choices follow a game when its Steam library or games
  folder moves (including a symlinked games folder).
- **Cancel**: long operations can be cancelled until they first change a game file or the registry; after that they
  finish or roll back completely. Downloads are made before that point.
- Only one operation runs at a time, and searching keeps the selected game.
- **HDR launch guidance** for Steam, Heroic and Lutris (where to paste the settings in that launcher), in English and
  Brazilian Portuguese. A blocked HDR profile no longer stops a store launch.
- Launch commands are recognised through wrappers such as `gamemoderun`, `mangohud` and `gamescope … --`.
- **Edit game** can change the launch command.
- Update All reports a component as updated only when its files really changed, and as "could not be checked" when
  it could not tell.
- The background update pass survives a failure and retries later.
- A read-only `Engine.ini` is written and locked again, as upstream does.
- A Wine prefix whose runtime cannot be identified is never given the system Wine for Winetricks; Play is disabled
  with a hint to set a launch command.

## Wave 1 — 2026-10-07: foundations

- **Crash-safe installs**: files are written to a temporary copy, checked and moved into place; installs are journaled
  and an interrupted one is rolled back at the next start, keeping anything you changed meanwhile.
- **Saved settings**: an unreadable settings or library file is set aside and never saved over.
- **Repair after move**: checks a moved game's new folder and updates its records in one step.
- The GitHub token file must be private (mode 0600).
- Detection: each Steam library read once; Steam tools and DLC not listed; Lutris and Heroic parsing fixes; refusal of
  a Wine prefix root, the home folder or `/` as a game folder; folder links never followed.
- Library start-up no longer waits for the DLSS manifest download (15 s limit).
- Graphics API chosen from the executable's imports; DirectX first when several are named.
- Manual games whose folder is gone are shown as "folder missing" and stay removable.
- Every message in English and Brazilian Portuguese from one table; errors are logged to
  `~/.local/state/rhi-linux/logs/rhi.log` instead of closing the app.
- RenoDX downloads limited to trusted hosts; `ReShade.ini` written safely.
- Build: pinned .NET SDK, locked packages, `validate.sh` gate, reproducible AppImage script, `install-user.sh` keeps
  the previous install.

## 2026-10-07: upstream RHI 2.8.0–2.8.5, responsive window

- **Responsive window** from 640×480, landscape or portrait: toolbar overflow menu, slide-out library below 900 pixels,
  paths shortened in the middle. RHI never resizes your window; restoring from the tray keeps its state.
- RenoDX updates detect rebuilt snapshots; Update All installs only real updates.
- Update history shows RenoDX's own add-on version.
- Manifest install warnings before an install, with clickable links.
- ReShade falls back to a cached copy when reshade.me is down.
- A revoked GitHub token falls back to anonymous access.
- OptiScaler frame generation: `nvngx_dlssg.dll` in the Streamline stack; Force Reflex and game Reflex marker settings.
- Donate dialog; installed RenoDX/Luma add-on file names and mod authors on the game page; readable Available HDR mods.
- Running the test suite no longer writes into your real cache folder; cached downloads are reused only when they
  match what was recorded.

## 2026-10-06: upstream RHI 2.7.6–2.7.9, Lutris

- Neural Rendering add-on version swaps and NR DLL version choice.
- Available HDR mods list and collapsed-section summaries.
- PCGamingWiki single-file data with polite retry on rate limits.
- OptiScaler FG Nvngx "Combo" and RR Preset F.
- RenoDX Unity add-on from GitHub Releases, cached; Unity per-game settings from the RHI database.
- Control Ultimate Edition ray-reconstruction setup (undone on removal).
- ShortFuse ZZZ load order; component update history; shader and ReShade channel fixes; launch working folder.
- Lutris: games inside a Lutris launcher's prefix (Ubisoft Connect, Battle.net) get their own cards; games started
  through a wrapper script no longer take the script's folder or merge into one card; entries without a real game
  folder are not listed as games; Wine prefixes already owned by a launcher are not listed twice.

## 2026-09-30: library scan and data-loss fixes

- **Library scan memory and speed**: the indexer no longer follows Wine's drive links (`dosdevices`), and each game
  folder is capped at 200,000 files. Lutris executables written relative to the prefix or with `$GAMEDIR` are resolved.
  One Lutris entry used to pass 2.8 GB of memory and keep growing; a full 21-game scan now takes about 1.4 s and 77 MB.
- Add game rescans only the manually added games.
- Games under a path containing a word on the upstream blacklist (such as "Windows") no longer vanish.
- Lutris launches use the right game name.
- Saving Settings no longer deletes the selected game's HDR, Luma, OptiScaler and version-pin choices.
- An unreadable install-records file is set aside instead of being replaced with an empty one.
- A cancelled OptiScaler or RenoDX install is rolled back.
