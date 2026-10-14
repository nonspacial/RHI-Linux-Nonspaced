# Changelog

Changes in this fork ([nonspacial/RHI-Linux-Nonspaced](https://github.com/nonspacial/RHI-Linux-Nonspaced)) since the
wizardkof RHI Linux beta it started from, `v0.9.23.4-beta.1`. Newest first. Each entry is the date it reached `main`.
How each upstream RHI feature was adapted for Linux is described in [`UPSTREAM.md`](UPSTREAM.md); download sources are
listed in [`docs/download-trust.md`](docs/download-trust.md).

The app still reports version `0.9.23.4-beta.1`; the fork has not published a release yet.

## In progress

Not on `main` yet:

- RTX Encore as a managed component.
- Games from other launchers inside Wine prefixes: EA app, Ubisoft Connect, Battle.net, Rockstar and itch.io.
- Turning installed add-ons off when a game switches to ReShade's No Addons channel.
- A DLL-name choice for OptiScaler in its settings dialog.

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

- **Xbox / Game Pass** games installed by PartingX or Xodus are listed. Play asks PartingX to start the game; the card
  menu has Show in PartingX. RHI reads bitness from the game's package (the executables are encrypted) and changes
  nothing while PartingX has a job queued for the game.
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
