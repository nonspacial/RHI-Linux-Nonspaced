# Changelog

Changes in RHI Linux Nonspaced ([nonspacial/RHI-Linux-Nonspaced](https://github.com/nonspacial/RHI-Linux-Nonspaced)).
Newest first. Each entry is the date it reached `main`. The project started from
[wizardkof/RHI-Linux](https://github.com/wizardkof/RHI-Linux) (GPL-3.0).
How each upstream RHI feature was adapted for Linux is described in [`UPSTREAM.md`](UPSTREAM.md); download sources are
listed in [`docs/download-trust.md`](docs/download-trust.md).

Releases are published on [GitHub Releases](https://github.com/nonspacial/RHI-Linux-Nonspaced/releases). The first,
`2.8.7`, is level with upstream RHI 2.8.7 and takes its number; later releases of this project alone add a fourth
number (`2.8.7.1`). The port of an upstream release takes that release's number (`2.8.8`) and is published only once
upstream's release leaves beta; until then its builds carry a beta label (such as `2.8.8-beta.1`) and are not published.

## In progress

Not on `main` yet.

### Wave 14 — renodx-reshade

- The generic Unity RenoDX add-on comes from its new location (Voosh's GitHub Pages, 64-bit), and the upstream
  manifest can move it again without an app update. Games with the add-on from the old link show an update. When a
  link cannot be reached, RHI tries the add-on's older links in turn (#268).
- Shader packs you have downloaded are checked for updates at every start, whether or not a game uses them and with
  "Download all shaders" on or off; packs you never used are still downloaded only when needed (#267). Packs that
  come as a branch archive (most of them) now update when their author changes them; before, the first download was
  kept for good. Packs the online manifest adds (SD Shaders, UltrawideSideGlass) update like the others, and
  UltrawideSideGlass, a single `.fx` file, now installs at all (#265).
- RenoDX add-ons hosted on a modder's own site (such as UE-Extended) install from the same file on the modder's
  GitHub when the site cannot be reached. A second download of an add-on (`renodx-x (1).addon64`) no longer blocks an
  install or update: it is renamed so ReShade does not load it twice (the file is kept), and a dropped one installs
  under the add-on's own name. Games that RHI's data lists as Unreal Engine 4 or 5 get the generic Unreal mod even when
  the folder scan did not recognise the engine (#287).
- Vulkan ReShade: checked against upstream's three Vulkan fixes. Each Vulkan game here has its own ReShade layer, so
  updates show and Update All applies them, and Stable, Nightly and Legacy each switch that game's layer; new tests
  keep it that way (#288).
- Source engine games (Half-Life 2, Portal, Counter-Strike: Source and the others in RenoDX's Source list) now show
  RenoDX's Source engine mod on their RenoDX row. RHI does not install it yet: the mod needs DXVK set up in front of
  ReShade, and how to do that under Proton is waiting for a test on a real game. Install says so and points to the
  RenoDX wiki's manual steps (#286, partial).

Wave 14 — library-updates

- **The RHI-Linux update check no longer says "You have the newest version" after GitHub's hourly request limit was
  reached** (upstream 2.9.4). It says the limit is reached and that a GitHub token raises it, or that it could not
  check.
- **Hidden games are left alone by everything that runs over the whole library** (upstream 2.8.9): Update All, automatic
  component updates, Follow newest DLSS / Streamline and Settings' "Apply to all games" buttons. To update a hidden
  game, show it again or use its own buttons.
- **Automatic updates run every 4 hours, every hour with a GitHub token, and count across restarts** (upstream
  2.8.9/2.9.2): a start runs them at once only when the last run is 4 hours (1 hour) ago or more, and saving Settings no
  longer starts an extra run.
- **The Ultra+ and ultrawide fix lists are checked every 4 hours** instead of once a day, also while RHI stays open.
- **Update History lists DLSS and Streamline swaps and restores** made from the DLSS section, the DLSS batch and the
  DLSS defaults (upstream 2.9.3), with the version before and after.
- **Settings no longer saves an add-on watch folder that does not exist**: it says so and saves the rest (upstream
  2.8.9).
- **An invalid ReLimiter custom FPS is no longer saved as "no cap" without a word**: the saved cap stays and RHI says
  the value must be a whole number from 20 to 1000 (upstream 2.8.9).
- **Games that ship FFmpeg, Qt, SDL or a web view no longer read as DirectX 12** when nothing else says which API they
  use (OpenMW, for example; upstream 2.9.2).
- **Block Steam updates** (upstream 2.9.4): a new action beside Favourite and Hide for Steam games makes the game's
  Steam app manifest read-only, so Steam can't update the game and your mods keep working until they're updated. Allow
  Steam updates turns it off. Refused while Steam is in the middle of updating the game.
- **Remove from RHI** (upstream 2.9.0): takes a game out of every list, Hidden included, and scans don't bring it back.
  **Settings → Removed games** lists them with Restore. Components RHI installed in the game stay installed.

Not on `main` yet:

- Wave 14 — extras-addons
  - ReShade's ⚙ has **Load ReShade as ASI** (upstream 2.8.9): ReShade becomes `ReShade64.asi` / `ReShade32.asi` for an
    ASI loader, installs and updates keep that name, Off goes back to its DLL name. Not for Vulkan games (#283).
  - ASI Loader and the extras (upstream 2.9.0): with ASI Loader installed as `version.dll`, 20/30 FG Unlock, Universal
    MFG, RTX Encore and DLSS Enabler offer `versionHooked.dll`, which ASI Loader loads. Names another component uses
    are greyed with its name. Installing ASI Loader over an extra moves the extra to the Hooked name and RHI follows it;
    removing ASI Loader moves it back, Wine overrides included (#284).
  - Display Commander's ⚙ has its main settings (upstream 2.8.9): FPS Limit (VRR presets or your own value), FPS Limiter
    Mode, Inject Reflex, Window Mode, the four Reflex options and Win+Up grace time, saved to the game's
    `DisplayCommander.ini`, and Open DisplayCommander.ini (#282).
  - HSF frame generation (upstream 2.9.3, HSF 0.9.8) in Extras: one click installs HSF's add-on, Streamline and frame
    generation files, HSF.ini, HSF.fx and iMMERSE Launchpad; DirectX 12 games work from the first launch; updates keep
    your HSF settings; the ⚙ sets the DirectX 12 mode and Launchpad; RTX 20/30/40 cards are offered Universal MFG
    Unlocker. HSF needs RHI's ReShade installed first (#277).

### Wave 14 — optiscaler

- OptiPatcher shows its real build again and its cache is named by build: since November 2026 its release notes put a
  `^G` before the commit, which RHI did not read (#264, upstream 2.9.3).
- OptiPatcher now loads with OptiScaler Nightly, DLSS NR, F5 and the forks: it goes to `OptiScaler/plugins`, where
  those builds look, instead of the game's `plugins` folder, which only Stable reads. Games that already have it show
  an update; Update moves it (#273, upstream 2.8.9).
- OptiScaler versions (#270, upstream 2.9.0–2.9.2):
  - New **Default version** setting next to OptiScaler's GPU Type: the version a game installs when you haven't picked
    one in its ⚙. Games that already have OptiScaler keep theirs.
  - New **DXGI Spoofing** setting in OptiScaler's ⚙ on every version (Auto, True, False). Presets save it.
  - The ⚙ shows the installed and the newest version, with **Check now**, and **Update** when there is one.
  - New **Release** setting in the ⚙: keep a game on an older release of Stable, DLSS NR or F5 DLSSNR. Pinned games are
    not updated.
  - New **Remove old Nightly builds** setting (off by default): deletes old downloaded Nightly builds, keeping the
    newest and any a game uses.
- OptiScaler's ⚙ opens with a Presets panel on every version, Stable included (#269, upstream 2.8.9):
  - Recommended presets in one click: Real DLSS FG, DLSS Enabler FG and UMFG XeFG (which installs Universal MFG
    Unlocker if it's missing). The one matching the game is ticked. New ones can arrive without an app update.
  - Your four preset slots are one dropdown with Apply, Save current (Overwrite) and a … button to rename or clear.
  - New **Replace with a fresh copy…** at the bottom: asks first, then gives the game the same OptiScaler.ini as a
    fresh install (your GPU, DLSS Inputs and its version). The old file is kept in RHI's data folder.
- More OptiScaler versions: the recommended forks Venom, NR PreSR MFG, Feeder NR and OpenNR sit beside Stable,
  Nightly, DLSS NR and F5 DLSSNR. RHI downloads them from their GitHub releases and keeps them up to date like the
  others; the Neural Rendering forks get NVIDIA's Neural Rendering file (#271, upstream 2.9.2/2.9.3). Custom OptiScaler
  builds are not in this build yet.
- OptiScaler ⚙: FG Nvngx Override lists custom frame generation providers found in OptiScaler's custom folder by name
  (#272, upstream 2.9.3).

Not on `main` yet:

- Wave 14 — nr
  - DLSS5 Feeder 2.0 (#275): new installs use the motion vectors built into Feeder 2.0, so LumeniteFX is no longer
    downloaded for it; games that already use LumeniteFX keep it. Feeder is added to the game's own ReShade preset
    instead of replacing it, so your other effects and their settings stay, and removing Feeder takes out only what it
    added (a preset that was there before is never deleted). The Feeder row shows "Built-in motion vectors" or
    "LumeniteFX motion vectors" and mentions the Feeder menu (Alt+C).
  - GITC Uplift (#274): a new Neural Rendering method by ghostinthecamera for almost any game on an RTX card (DirectX
    9–12, Vulkan, OpenGL; 32 and 64-bit). Install it from its row in Managed components; in the game open ReShade
    (Home), go to the GITC Uplift tab and tick Enable. Uplift Version (under NR versions) follows the latest release
    or stays on one; the add-on and its helper always update together. It brings iMMERSE Launchpad for motion vectors
    (on by default in games without DLSS, with a check box per game) and keeps it at the top of the game's ReShade
    effects. Settings › DLSS/Streamline has a GITC Uplift card with the NR on/off key and "Write key to all Uplift
    games". OpenGL games are set up so Uplift works with anti-aliasing on. It is the recommended method for 64-bit
    DX11, DX12 and Vulkan games without DLSS.

Wave 14 — extras-fg

- **Closed-source notice** (upstream 2.9.3): installing RTX Encore or 20/30 FG Unlock first says the mod is
  closed source (its author publishes only the DLL, not the code), with Install, Cancel and **Don't show this
  again**. The tick is remembered per mod, also when you press Cancel. The ⓘ on the RTX Encore, 20/30 FG Unlock,
  Universal MFG Unlocker and MFG Unlock rows opens the mod's GitHub page.
- **Universal MFG Unlocker** (formerly Universal MFG / RTX 40 MFG, upstream 2.8.9): the row has the mod's name and links
  to its new GitHub page. The GPU generation choice is gone: RTX 30 is supported (RTX 20 experimental) and XeSS frame
  generation (XeMFG) runs on any GPU, in DirectX 12 and (experimentally) Vulkan games.
  - Installing it also puts Intel's `libxess_fg.dll` and `libxell.dll` in an `RTXMFG-XeSS` folder beside it, which
    the mod's "XeMFG standalone" mode needs. Files you put there yourself are left alone. **Existing installs get them
    with Update or Update All.**
  - With RHI's OptiScaler Nightly, F5 DLSSNR or DLSS NR in the same game it installs as an OptiScaler plugin
    (`OptiScaler/plugins/MFGUnlocker.asi`), as its author recommends; installing such an OptiScaler moves an existing
    install in, and removing OptiScaler moves it back beside the game first. Choose DLSSG or XeFG with FG Output in
    OptiScaler's ⚙.
  - Installing it where ReShade is warns that XeMFG standalone does not start while ReShade is loaded, with **Don't
    show this again**. The ⚙ explains XeMFG, says whether its files are there and warns about ReShade as `dxgi.dll`.
- **MFG Unlock** (formerly MFG Ada Unlock, upstream 2.8.9/2.9.2): the row and its messages use the mod's new name;
  your installs carry over. Its new ⚙ has the **Frame generation method** (NVIDIA DLSS-G, Intel XeMFG (CUDA), XeMFG via
  DLSS-G (XMX)) and **DLSS-SR input (experimental)** for XeMFG (CUDA), saved in the game's `MFGAdaUnlock.ini`, and an
  Open MFGAdaUnlock.ini button.
  - Installing it also installs Intel's XeSS files its XeMFG modes need in the game folder. A game's own older copies
    (Of Ash and Steel) are kept as `<file>.mfgada-original` and put back when you remove the mod. **Existing installs
    get them with Update or Update All**, and the ⚙ says whether the right files are there.
  - The ⚙ warns when the game can't use it: DirectX 11 and older don't work, Vulkan is experimental.
- **20/30 FG Unlock ⚙** (upstream 2.8.9): the mod's main settings as dropdowns — Frame Generation, Engine (DLSS or
  XeSS), Max Frames (2X to 6X), Preset (A, B, C: FG Lite), Speed Tier and Super Resolution (preset M or L) — saved in
  the game's `dlssg_sm86.ini`, so you can't enter a value that would switch frame generation off. XeSS goes up to 4X.
  Your settings are kept when the mod updates. Open dlssg_sm86.ini and Open log buttons. The GPU generation setting is
  gone: the mod detects RTX 20 and 30 cards by itself.
- **RTX Encore** is no longer labelled Beta (upstream 2.8.9–2.9.3).
  - Its ⚙ has RTX Encore's settings — frame generation, the XeFG engine, generated frame quality, HUD and UI,
    upscaling, Neural Rendering and compatibility — written straight into the game's `rtx-encore.jsonc`, keeping the
    file's comments, and an Open rtx-encore.jsonc button.
  - Installing it also installs Intel's XeSS files the way RTX Encore Hub does: the game's own copies are replaced where
    they are and put back when you remove RTX Encore; the rest, and Intel's licence, go in RTX Encore's own folder.
  - The Neural Rendering file RHI installs for it is now 310.9.16.
  - **Existing installs get the XeSS files and the 310.9.16 file with Update or Update All.**
  - The Bink names (`binkw64.dll`, `bink2w64.dll`) are offered: the game's own Bink file is kept beside it as
    `binkw64Hooked.dll` / `bink2w64Hooked.dll` and put back on removal.
  - `rtx-encore.asi` installs the separate ASI build from RTX Encore's release. An `.asi` install from an older build is
    corrected by Update.
  - The DLL name list puts the name the RHI manifest recommends for a game first and uses it by default (Euro Truck
    Simulator 2: `winmm.dll`); the ⚙ names it.

Wave 14 — library-folders

- **Custom game folders** (upstream 2.9.0): Settings → Custom game folders lists folders where you keep DRM-free or
  other non-store games. RHI checks them at start, on Refresh and with Scan now, and never adds a game on its own: the
  status line says "N new games found in your custom folders · Review". In the review you fix the name, choose the exe
  (RHI picks the likely one and says "Check the exe" when two look alike) and, as in Add game, the Wine/Proton prefix
  and launch command. Ignore selected, or removing a game you added this way, stops it being offered; Forget ignored
  games undoes that.
- **Remove from RHI asks first and can uninstall the game's components** at the same time (upstream 2.9.0). A component
  that cannot be removed is named, and comes back with the game on Restore.
- **Changing or resetting a game's install folder moves its mods, settings and presets** (upstream 2.9.2): Edit game and
  Restore automatic detection ask, and when RHI corrects a folder itself (the online manifest) the mods move on their
  own. Nothing moves while the game is running, a file you changed is never overwritten, and a failed move is undone.

Wave 14 — extras-fg-2

- **20/30 FG Unlock on RTX 30** (upstream 2.8.9+): RTX 30 owners are no longer sent to Universal MFG Unlocker; 20/30
  FG Unlock installs on RTX 20 and 30 alike, and the mod finds the card itself. Both rows are offered on every card, as
  upstream's are. Their notes now give upstream's description instead of a "legacy RTX 20" path or a card generation
  you never picked, and every message calls the mod 20/30 FG Unlock (#281).
- **RTX Encore ⚙ warns while the game is running** (upstream 2.9.0): changes apply the next time it starts, and RTX
  Encore's in-game menu may overwrite them while it's open (#280).

- Wave 14 — nr-2
  - DLSS5 Tool (#317): RenoDX's DLSS5 Tool is now a Neural Rendering method of its own, as in upstream RHI, for 64-bit
    games with native DLSS. Install it from its row in Managed components; it brings NVIDIA's Neural Rendering file when
    the game has none. DLSS5 Tool Version (under NR versions) follows the latest release or stays on one, and switching
    version swaps the add-on in place. The DX11 Bridge row now installs "DLSS5 Tool + DX11 Bridge" (the bridge alone did
    nothing); a bridge installed earlier says it needs DLSS5 Tool. Check Neural Rendering lists it.
  - Foveated DLSS5 Tool (#276): a Foveated check box under NR versions installs FoveatedDLSS5 (by maximedgoulet) instead
    of the stock add-on: full-resolution Neural Rendering in the middle of the screen, less around it, for a large
    performance gain. Every download is checked against the hash published with it. Latest keeps it current, stock
    updates never replace it, and switching while installed swaps the add-on in place.
  - GITC Uplift's NR on/off key (Settings › DLSS/Streamline › GITC Uplift) is set by clicking the box and pressing the
    key, as in upstream RHI; Backspace or ↺ sets None. Keys saved before stay as they were.
  - HSF and GITC Uplift now share one iMMERSE Launchpad: a game with both never gets two, removing one leaves what the
    other still uses, and Launchpad stays at the top of the game's ReShade effects for HSF too. Existing HSF installs
    move their Launchpad into the shared place the next time RHI checks them (not while the game is running).

### Wave 14 — optiscaler-2

- **Custom OptiScaler builds** (#271, upstream 2.9.2): Settings › DLSS/Streamline › Custom OptiScaler builds. Add any
  OptiScaler build from a download link, a zip/7z/rar, a folder or an OptiScaler.dll, or drop one on the card. It
  appears as a version in every game's OptiScaler ⚙ and in Default version. RHI installs it like the other versions but
  never updates it. Replace swaps its files and offers to update the games that use it (their OptiScaler.ini is kept);
  Rename and Remove are next to it, with a **DLSS NR DLL** tick for Neural Rendering forks. Only add builds from people
  you trust: the card says so.
- The recommended OptiScaler forks now come from the online manifest, so a new fork appears without an app update. The
  four known forks stay as a fallback, and a fork the manifest drops keeps working in the games that have it. Forks are
  listed by name, as upstream lists them. Saved choices, Default version, release pins and installed games all keep the
  version they had.
- **OptiScaler presets remember their version** (upstream): a preset saved on F5 (or any version) switches the game to
  that version when you apply it. RHI asks first, because that reinstalls OptiScaler; your OptiScaler.ini settings are
  kept and a copy goes to RHI's data folder. Presets saved before this build keep the game's version.
- **All Settings in OptiScaler's ⚙** (#272, upstream 2.9.3): every setting of the game's OptiScaler.ini, with the
  options the installed build lists in its own INI (forks and custom builds show theirs). Pick a section or search;
  nothing is written until you press Apply, and the old file is backed up first. Applied changes are written again
  after a reinstall or a version switch; Forget saved edits stops that.
- **Save ini… / Load saved ini…** in OptiScaler's ⚙ (#272): keep any game's OptiScaler.ini under a name ("F5 super
  ini"), add one from a file, and use it for any game. The game remembers it and gets it back after a reinstall or a
  version switch.
- **ANR FG preset** in OptiScaler's ⚙ (#272, upstream 2.9.3): Apex Neural Rendering frame generation in place of the
  game's DLSS frame generation. It first says ANR FG is closed source (#285), checks that the game's OptiScaler build
  can load it (Nightly from late October 2026, or a build based on it), then downloads the newest ANR FG release,
  checked against its published hashes, into OptiScaler's custom folder. Your `nr_fg.ini` is kept.

### Wave 14 — ui-logic

- **Game search like Steam's** (upstream 2.9.3): punctuation and spaces are ignored (fear finds F.E.A.R.), words can
  come in any order (batman lego), initials work (gta, rdr2, ff7, hl2, mw3), Roman numerals match numbers (gta 3 finds
  Grand Theft Auto III) and small typos are fine (resident eviaa). When a name or a whole word matches, near misses
  are hidden (sifu shows only Sifu), and the best match comes first (resident evil 4 puts Resident Evil 4 on top).
  Stores, runners, engines, graphics APIs, authors and folders match whole words only: st no longer lists every Steam
  game; type steam, unreal, dx12 or vulkan to list those games.
- **Game collections** (upstream 2.9.2): make your own lists of games, like Racing or Souls-likes. Right-click a game
  and choose **Add to collection** (tick a collection, or New collection…); a game can be in as many as you like.
  Your collections show as buttons under the filter tabs: click one to show only its games, click it again to show
  everything. Right-click one to rename, move or delete it; deleting a collection never removes any games. Picking a
  filter tab leaves the collection, and RHI remembers the one you had open.
- **Colour themes** (upstream 2.9.3): Settings › General › **Colour theme** — Default, OLED Black, Midnight Blue,
  Ember, Forest, Kiro, Slate, High Contrast or Colour-blind Friendly. RHI changes colour straight away, no restart.
  - **Custom** lets you choose your own colours: background, panels, text, accent, buttons and the status colours
    (installed, update available, warning, error). It starts from the theme you had; Start from a theme… and Reset
    to default are there too. RHI warns you if text would be hard to read.
  - **Share theme** copies your colours as one line of text (`RHI-THEME:…`) and **Import theme** takes a code someone
    shared. Codes from RHI on Windows work here and the other way round.
- **Per-game FPS limit** (upstream 2.8.9): the DLSS section of each Wine/Proton game has an **FPS Limit**, so you can
  cap one game without capping them all. Global follows the new FPS Limit in Settings › Driver Settings, Off turns
  the limit off for that game, and Custom… takes 20 to 1000; upstream's VRR caps (59 FPS for 60 Hz up to 416 FPS for
  480 Hz) are in the list. On Linux RHI hands the cap to DXVK (`DXVK_FRAME_RATE`, Direct3D 8–11) and VKD3D-Proton
  (`VKD3D_FRAME_RATE`, Direct3D 12) when it starts the game; for Steam, Heroic and Lutris it shows the launch options to
  paste. As upstream, Global does not cap games with ReLimiter or Display Commander, so the two limiters don't fight.

### Wave 14 — ui-settings

- **Settings looks like upstream's** (upstream 2.8.9/2.9.3): eight sections in upstream's order with an index on the
  left, Find a setting (it scrolls to the setting and outlines it), a Tabs button for one section at a time, and
  Settings reopens where you left it. Settings save as soon as you change them; there is no Save button. Clear cache and
  Reset settings are red and ask first, with Cancel as the default (#296).
- **Hide the HDR or Resolution row on game pages** (upstream 2.9.3): Settings › Display & HDR. The game's own choice
  still applies when you launch it (#312).
- **More tooltips** (upstream 2.8.9): the library filters, section headings and summaries, the DLSS defaults
  abbreviations, and greyed settings say why they're greyed (#302).
- Every component ⚙ now looks the same, as in RHI 2.9.0: cards, the setting's name on the left with its option beside
  it,
  a short explanation under each setting, and a scroll bar when there is a lot to show. RenoDX's and OptiScaler's
  compatibility settings (and Display Commander's and RTX Encore's) stay two per row; hover a setting for its
  explanation (#297).
- Messages such as "Deployed ReShade.ini", a saved setting or an error now appear inside the ⚙ window, not only on the
  game
  page behind it (#297).
- Settings in a ⚙ save as soon as you change them; one that can't be saved goes back to its saved value and the ⚙ says
  why.
  DX9 HDR's preset no longer needs Apply. The ⚙ window has a Close button (#297).
- New buttons to open ReShade.ini, ReShade.log, relimiter.ini, the ReLimiter log, DisplayCommander.ini, OptiScaler.ini,
  HSF.ini and HSF's helper log from their ⚙ (#297).
- Components with nothing to set say so: DOF Fix has a ⚙ that says it has no settings, and Luma's ⚙ on a game that isn't
  Unreal Engine says there is nothing to set there instead of having no ⚙ (#297).
- RTX Encore's ⚙ explains every setting in its tooltip, and a greyed setting in a ⚙ says why it is greyed (for example,
  Multiplier is only used when Mode is Fixed) (#302).
- The status bar, bottom left, shows what RHI is doing in the background (scanning your games, checking for updates,
  updating DLSS, checking custom game folders) and, when idle, when RHI last checked for updates and when the next
  check is due ("Last checked: 18m ago · Next check in 2h 41m"). A status line shows there for 10 seconds; the last
  one stays in the bar's tooltip (#298).
- Help's Quick Start is rewritten for the current app: welcome and the GitHub token, picking a game, ReShade, RenoDX
  and Luma, shaders, DOF Fix and frame limiters, DLSS / Streamline, Neural Rendering, OptiScaler, frame generation
  unlocks, HDR and resolution on launch, adding games, updating and troubleshooting (#309).
- Links in the message of the day can be clicked (web links only) (#309).
- What's New has a version list with a search box, an arrow that hides them to widen the notes, and + / − for the text
  size. RHI remembers both (#309).
- Support the Mod Authors shows Ko-fi and Patreon buttons for authors who have both (#309).
- Clicking the store badge on a game's page opens the game's Steam Community page, when RHI knows its Steam app id
  (#309).
- A greyed game name in the library means ReShade isn't installed for that game; its tooltip says so (#309).
- The mod author credit says which mod it is for: "RenoDX: Marat", "Luma: Pumbo" (#309).
- A game you add yourself opens straight away; a search or filter that would hide it is cleared (#309).
- Hovering the HDR mode and "Change resolution while playing" says whether they are on for the game (#309).
- The Favourite button is quieter: "★ Favourited" in amber once a game is a favourite (#309).
- A ⚙ beside the game's name opens Game settings, with Block Steam updates as an On/Off switch (upstream 2.9.4) and
  Remove from RHI; both left the row of buttons beside Favourite and Hide.
- Installing DLSS5 Tool (or DLSS5 Tool + DX11 Bridge) also brings the game's DLSS Super Resolution, Ray Reconstruction
  and Frame Generation files to the newest, through the DLSS / Streamline section: the originals are kept there and
  Restore puts them back, and Updates lists the swaps. A game whose update inclusion leaves DLSS out keeps its files
  (#317).

### Wave 14 — display-modes

- **Display modes** (upstream 2.9.3): save sets of the settings RHI applies to a game — e.g. Monitor and TV, up to six
  modes — and switch a game between them from its page. Turn them on in Settings › Display & HDR › Display modes, where
  you add, rename, reorder and delete modes and set each mode's values with Edit…. A mode can set RenoDX's peak, game
  and UI brightness, the ReLimiter and Display Commander caps, the game's FPS limit, DLSS frame generation, the DLSS
  Super Resolution / Ray Reconstruction presets and render scales, the ReShade preset, the ReShade overlay and
  screenshot keys, controller navigation in ReShade, the ReLimiter OSD key, the HDR mode the game launches with and the
  resolution switch. Anything a mode does not set keeps the game's normal value (#311).
- The mode switch sits beside HDR and resolution in both layouts (in Classic+ under the resolution row in Game
  Overrides, in Tabs in the game header beside HDR on launch and the resolution switch), with a ⚙ for the game's
  display mode settings; the game menu has a Display mode submenu, and the game's own ⚙ a Display modes row. Click
  the game's active mode again (or pick Off) to turn its mode off: it goes back to your global settings (and what it
  had before modes for settings without a global value). Switch all games switches every game, or turns every game's
  mode off; hidden and running games are left out. In a game's ⚙ you can make a Custom mode just for that game (#311).
- While a game is in a mode, ReShade, RenoDX, ReLimiter and Display Commander installs and updates, and the Settings
  "write to all games" buttons, keep that game's mode values (#311).
- Not on Linux: RTX HDR peak brightness and the choice of display for HDR (no Wine/Proton equivalent). Launch
  arguments per mode wait for an owner decision (#311).

### Wave 14 — layouts-a

- **Three layouts, switched from the top bar: Classic+ | Tabs | Spotlight** (upstream 2.9.0). Classic+ is the page you
  know, now with upstream's seven sections in its order: Components, Game Overrides, Neural Rendering, DLSS /
  Streamline, Driver Settings, Management and Extras. Tabs shows a large game header, then one tab at a time: Overview
  (each component as a card, and an At a glance tile for every other tab), Game Overrides, Extras, DLSS / Streamline,
  Driver Settings, Neural Rendering and Management. RHI remembers the layout and the tab. Spotlight comes in a later
  update; its switch is there, greyed (#303, #304).
- **New top bar, the same in every layout** (upstream 2.9.0): Available Mods (with a green New tag when new mods have
  arrived) and Shaders & Addons (Global Shaders, Global Addons) on the left; when RHI last checked, Refresh, Update All
  with how many games have updates, the layout switch, a Help menu (Quick Start, Guide, Discord, the RenoDX and Luma
  wikis, the GitHub pages, Patch notes, Message of the day, Update log, About, Donate) and Settings, lit while Settings
  is open. Quick start, Links, MOTD, Updates and Donate moved into Help; Batch DLSS is in Settings › DLSS / Streamline;
  Add game is the green + beside the search box; the Compact view button is gone (#303).
- **The game list matches upstream's**: dark navy, All | Installed | Favourites | Hidden under the search box, your
  collections under that, and a Filters button beside the counts for the Unreal, Unity, Other, RenoDX and Luma filters
  (#303).
- **Management** (upstream 2.9.0): Change install folder, Reset install folder, Remove from RHI (moved out of the
  Game settings ⚙; Block Steam updates stays there), Reset Overrides and Copy Report, in one row in Classic+ and as
  tiles in Tabs. Reset Overrides puts the forced API, Luma mode, DLL names, Update All inclusion and NVIDIA settings
  back to the defaults (#304).
- **In Tabs, a component's ⚙ opens on the page, right under it**; click the same ⚙ again, or ✕, to close it (#304).
- **Interface size** from 50% to 200% in 5% steps (Settings › General › Windows, or hold Ctrl and turn the mouse wheel;
  the status bar says the new size), and **Top bar size** and **Game list size** of their own: pick them in Settings or
  drag the top bar's bottom edge and the game list's right edge; double-click an edge to reset it (#306).
- Tooltips, menus and pop-ups use upstream's dark navy look (#303).
- Custom colour themes have a real colour picker beside each colour's hex box, and the favourite star keeps its amber
  in every theme that keeps upstream's warning colour (#301).
- ASI Loader has a ⚙, between Install and Remove, that says it has nothing to set, as upstream (#297).

### Wave 14 — layouts-c

- **Launch arguments for each game** (upstream 2.9.0): type them in Game Overrides › Launch arguments (saved when you
  leave the box or press Enter), or reach the box from Tabs' Launch ▾ › Launch arguments…. RHI adds them when it starts
  a standalone Wine game itself, and starts a Steam game with `steam -applaunch <id>` and your arguments. Under the box
  RHI says how they reach the game, or, for Heroic, Lutris, a store client, a non-Steam shortcut or a launch command you
  typed in Edit game, where to add them instead: RHI never changes a command you typed. **Reset launch options** clears
  the arguments and the launch command typed in Edit game; Management's Reset Overrides clears the arguments too
  (#304).
- Tabs' Launch ▾ menu has upstream's entries: Executable (opens Edit game), Launch arguments…, then Copy launch profile
  and Create shortcut (#304).
- **Display modes can swap the launch arguments** (upstream 2.9.3): set them in a mode's Edit… or a game's Custom mode
  (empty means no arguments in that mode); Off puts the game's own back (#311).
- **Update All shows how many games have updates, or "All up to date"** (upstream 2.9.0): RHI now checks every game
  for updates at start and then every 4 hours (every hour with a GitHub token), whether or not automatic updates are on.
  The check only looks: it changes no file, leaves hidden games out and stops when GitHub's hourly request limit is
  reached, saying how many games it did not check. Games with an update are marked in the game list (#303).
- **In Tabs, a component's ⚙ now opens right under the card you clicked**, and the cards after it move down; before,
  it opened under all the cards. In Extras it opens right under the row (#304).

### Wave 14 — layouts-b

- **Spotlight is here** (upstream 2.9.0–2.9.4): your games as a row of covers above the page (or below it: the strip's ⚙
  › Covers below the game page; No background for the page colour), the game's Steam artwork as a large banner behind
  its header, "What's installed" and "Tune this game" tiles, and a side panel that slides in with the section you
  open; a component's ⚙ opens in it with a back button. Drag the banner's bottom edge or the covers' edge to resize
  them, drag the covers to scroll; double-click an edge to reset. Covers with an update have a purple outline (#305).
- **Artwork**: right-click a game › Artwork › Set custom cover / banner (your file is copied, never changed; Use Steam
  cover / banner undoes it), or Browse covers / banners from SteamGridDB with your own free API key (Settings ›
  Accounts & Data). Steam art is found for games from other stores too, including Microsoft Store names with
  "- Windows", "(PC)" or an edition in them, and Steam's own copy on your PC is used first (#305).
- **Screenshots** beside the game's ⚙ in Spotlight: its ReShade screenshots, newest first; open, set as the banner,
  copy, show in the folder, or delete to the Trash (#305).
- **Steam achievements** (opt-in, Settings › Achievements): progress, the latest and recent unlocks under Launch, and
  the full list with dates, rarity, icons, search and hidden ones masked (Show hidden), refreshed as you unlock them.
  Read from Steam's files on your PC (native or Flatpak Steam): no sign-in or API key (#307).
- **Controller support in Spotlight** (Xbox, PlayStation and Steam controllers): upstream's buttons, a focus ring and
  hints in the footer, only while RHI is the active window (Settings › General › Controller support). The right stick
  click steps through your collections. RHI asks to come back to the front when a game it launched closes. If Steam
  is running, Steam Input may hold the controller: see Settings' tooltip (#308).

## 2.8.8 — 2026-11-16: level with upstream RHI 2.8.8

- The second upstream level: the same downloads as 2.8.7.1 (AppImage, `.deb`, `.rpm`, `.tar.gz` and one
  `SHA256SUMS`). It holds everything in the wave entries below, up to and including wave 13, and is level with the
  upstream RHI 2.8.8 release (tag `RHI-2.8.8`). How each upstream feature was adapted for Linux:
  [`UPSTREAM.md`](UPSTREAM.md).
- The app now reports version `2.8.8`, so 2.8.7.1, 2.8.7 and the unpublished 2.8.8 beta builds announce this release
  as newer.
- Before going back to 2.8.7.1 or 2.8.7, read the notes at the top of the wave-13 and wave-12 entries below.

## Wave 13 — 2026-11-16: the upstream RHI 2.8.8 release — Available mods, add-ons in reshade.ini's AddonPath

> [!WARNING]
> Before going back to 2.8.7.1 or 2.8.7, do these first as well as those in the wave-12 entry; they do not know what
> this build added:
>
> - **Remove RTX 20 FG Unlock (DLSSG 20/30)** from every game where this build installed it or repaired it with
>   Update. The older builds do not know the mod's release package: their Update and Update All put the old single DLL
>   in place of the package's proxy, so frame generation stops starting again, and their Remove leaves the
>   `dlssg_sm86` folder behind. Install it again once you are back on this build.
> - **Remove the Universal UE DOF Fix from games whose `reshade.ini` sets an `AddonPath`.** This build moved it into
>   that folder; the older builds' next DOF Fix update puts a new copy beside the executable, where ReShade ignores it,
>   and forgets the copy in the AddonPath folder, which ReShade keeps loading and RHI no longer owns. Install it again
>   once you are back on this build.
>
> The NR Cost Scaler's companion add-on needs nothing: the older builds find it through its install record.

Mods list

- **"Available HDR mods" is now "Available mods"** (upstream 2.8.8) and adds an **Ultra+** column: games on the Ultra+
  list get a tick there, and games with only an Ultra+ mod get a row of their own.
- **Every tick opens that mod's page**; the separate Download column is gone. A game covered only by RenoDX's generic
  Unreal, Unity or Source add-on opens the RenoDX wiki's mod list.
- **The top line counts** the RenoDX, Luma and Ultra+ mods, and says which list could not be loaded (the rest still
  shows).
- **Type RenoDX, Luma or Ultra+ in the search box** to show only games with that mod; add a game name after it to narrow
  it down, e.g. "luma dead".
- **Source engine games** that RenoDX's Source add-on supports (Half-Life 2, Portal, Left 4 Dead 2 and so on) are
  listed. Installing the Source add-on from RHI comes with a later upstream release.
- **HDR Gaming Database links are back** in the game card menu and the ⓘ dialog: the database's README changed how it
  lists games in November 2026 and RHI found none in it. If the list changes shape again, RHI keeps the last list it
  could read instead of dropping the links.

Components

- **ReShade add-ons go where ReShade loads them** when the game's `reshade.ini` sets an `AddonPath`: the Universal UE
  DOF Fix and NR Cost Scaler's companion add-on went beside the executable, where ReShade then ignored them. The next
  install or update moves RHI's copy into the AddonPath folder (also for MFG Ada Unlock, ReLimiter, Display Commander
  and the Addon Manager's add-ons, which already installed there). A copy you changed is left alone and the update says
  so; a same-named file in the game folder that is not RHI's is left there and named. The component rows also find
  add-ons in an AddonPath folder outside the game folder. The move counts as an update, so Update and Update All make
  it even when there is no new release. A Windows-style AddonPath (`C:\ReShade\Addons`, `Z:\home\…`) is found in the
  game's Wine prefix; one RHI cannot find there is refused with a message instead of creating a stray folder.
- **OptiScaler F5 DLSSNR installs and updates again.** Since v0.1.37 its release names the package differently
  (`v0.1.37-Optiscaler-F5.zip`), and RHI found no package. Both names work now, the optional companion downloads are
  never taken, and a release with two packages is refused with both names shown instead of RHI guessing.
- **RTX 20 FG Unlock (DLSSG 20/30) installs the mod's 0.4 release package**: the proxy DLL, `dlssg_sm86.ini` and the
  `dlssg_sm86` folder. RHI used to place only the DLL, so frame generation never started. The package detects the card
  itself, so RHI no longer writes a `Router=` setting. The proxy can be version.dll, winmm.dll, dbghelp.dll,
  dinput8.dll, dxgi.dll or d3d12.dll; winhttp.dll is no longer offered. **Existing installs are repaired by Update or
  Update All**: an old single-DLL install gets the full package, and an install on winhttp.dll moves to version.dll
  (refused if another file already has that name). Your own changes to `dlssg_sm86.ini` are kept.

Settings

- **The GitHub token is recommended**, as upstream now says: without one GitHub allows 60 requests an hour and installs
  and update checks fail once they run out; with one, 5,000. The note says how to make one (a classic token with no
  scopes).

## Wave 12 — 2026-10-28: Universal MFG again, shader headers, HDR controls where they apply, Epic in prefixes

> [!WARNING]
> Before going back to 2.8.7.1 or 2.8.7 (the released builds, 28 and 27 October 2026; the same app), do these first;
> it does not know what this build added:
>
> - **Remove whatever RHI installed on Epic Games Launcher games inside a Wine, Lutris, Heroic or Steam prefix** if you
>   want it managed there: 2.8.7 does not list games it could only find through the Epic launcher's records, so it can
>   neither update nor remove what is installed on them. Their install records are kept, and this build manages them
>   again.
> - **Expect Legacy HDR WSI back:** this build hides the tick and leaves `ENABLE_HDR_WSI=1` out on an NVIDIA driver 595
>   or newer, but keeps the tick saved. 2.8.7 applies a saved tick on every driver again (a game can then stop while
>   loading, as Borderlands 3 did); untick **Legacy HDR WSI** in those games once you are back on 2.8.7.
>
> 2.8.7 also brings back what this build fixed: its shader sync takes `ReShade.fxh`/`ReShadeUI.fxh` out of RHI's shader
> folder again (packs such as Prod80 stop compiling), its Update All puts back an add-on file you deleted outside RHI
> (ReLimiter, Display Commander, the DLSS5 DX11 Bridge), it cannot update Universal MFG past 1.4.x, it shows the HDR
> section on every game (a saved HDR mode on a game this build hides it for applies again at a direct start), and
> offline it turns Luma mode on for the games the old manifest listed.

Components

- **Universal MFG installs and updates again** (upstream 2.8.8): the publisher renamed the project and, from version
  1.5.0, ships `MFGUnlocker.dll` in `MFGUnlocker-*.zip`, which RHI refused, so no newer version could be installed. RHI
  now takes the new releases, still checks each download against the publisher's checksum, and updates a 1.4.x install
  in place under the DLL name you chose.
- **MFG Ada Unlock, DLSSG 20/30 Unlock and the Ultimate ASI Loader are updated by Update All** (and by the background
  update pass when automatic updates are on), with the per-game **Choose what Update All changes…** exclusions, like
  every other component. Unlike upstream, nothing is updated at start-up while automatic updates are off. The ASI Loader
  is only ever updated for the bitness it was installed for; when the game's executable changes bitness, remove it and
  install it again (the Extras section's ASI Loader check says so too). A record that lost its DLL name reads one line
  for these three, Universal MFG and DLSS Enabler Standalone alike: remove the component, then install it again.
  **Remove** works on such a record: it deletes only the files that still hold what RHI installed, puts the Wine setting
  back and names any file it left.
- **A file you deleted outside RHI is never put back by an update** — Universal MFG, MFG Ada Unlock, DLSSG 20/30, the
  ASI Loader, ReLimiter, Display Commander and the DLSS5 DX11 Bridge. Update All says it could not be checked, with the
  reason; the row still offers **Remove**, which clears RHI's record (then **Install** puts it back). A file changed
  outside RHI is never replaced either.
- **Luma mods that are only on Nexus Mods are checked on Nexus** (upstream 2.8.8) with your connected Nexus account: the
  Luma row gets a **Nexus** button, before the ⚙, reading **Update on Nexus** when Nexus has a newer file and **Open on
  Nexus** when RHI has not installed the mod or no account is connected (its tooltip names **Connect Nexus account**).
  RHI never downloads these mods; Update All counts them as could not be checked. Before, such a mod could never be
  checked, and a GitHub mod whose wiki entry listed the Nexus link first could not be installed. Only a key Nexus
  refuses reads **Open on Nexus** with **Disconnect, then Connect** in its tooltip, and RHI asks no more with it until
  you connect again; a mod Nexus hides or removed just reads as could not be checked.
- **Luma mode follows only your choice** (upstream 2.8.8 dropped the manifest's list of Luma default games): Luma mode
  is on where you chose **Enabled**, off for **Disabled** and **Automatic**, online or offline. Saved choices are kept.
- Luma removal deletes a recorded file only inside the game folder or ReShade's add-on folder.

Shaders

- **`ReShade.fxh` and `ReShadeUI.fxh` go into the game with every shader pack** (upstream 2.8.8; Borderlands 3 with
  Prod80): packs that use them without shipping a copy no longer fail with "could not open included file". They sit in
  RHI's own shader folder and go with the last RHI pack.
- **A shader pack you turn off leaves no empty folders behind**, including the empty folders older builds left. Folders
  holding anything of yours stay.
- **Applying shaders changes only what changed** (upstream 2.8.8): unchanged files are left alone, so applying the same
  shaders again (Shaders/Add-ons, Update All, a ReShade update) writes nothing in the game. A file in RHI's shader
  folder that is not the one RHI placed stops the sync with a line saying what to do (move or delete it, or untick its
  pack; for `ReShade.fxh`/`ReShadeUI.fxh`, which go with every pack, move or delete it). When `reshade-shaders/RHI` is a
  link to a folder of yours, RHI removes no empty folder there.

HDR

- **Legacy HDR WSI only where it is needed** (Borderlands 3 would not load with `ENABLE_HDR_WSI=1`): the tick shows only
  with an NVIDIA driver older than 595. On a newer driver it is hidden and `ENABLE_HDR_WSI=1` is left out of the launch
  line RHI gives you for Steam, the app that installed an Xbox game, Heroic and Lutris, and of a game RHI starts itself.
  A tick saved earlier stays saved and applies again on an older driver. Remove `ENABLE_HDR_WSI=1` from a launch line
  you pasted into Steam yourself.
- **HDR launch controls only for games that can use HDR:** the HDR section is hidden when RHI's data says the game has
  no HDR and RHI installed no HDR mod on it (RenoDX, Luma, DX9 HDR). Unknown, offline or not checked yet shows it. RHI
  reads its own list, PCGamingWiki and, for Xbox games, the Microsoft Store listing; the section label's tooltip says
  which. A hidden section's saved HDR mode is kept but not used.
- **Change resolution while playing** has its own **Resolution** row and stays wherever it applied before, also on a
  game whose HDR section is hidden.

Updates

- **A game that was running during an automatic update is updated soon after it closes** (upstream 2.8.8): RHI looks
  once a minute while automatic updates are on, and the status line says when such an update ran.
- **Update All names what it could not check for a reason on your machine:** a Custom-channel ReShade whose source DLL
  is gone (upstream 2.8.8), a component file removed or changed outside RHI, a game held after an undo RHI could not
  finish, a missing or unreadable game executable, a RenoDX installed from a local file, and the like, each with its
  game and the reason, instead of only counting them (the Custom ReShade used to count as failed).
- **GitHub answers are kept between launches** (upstream 2.8.8) and overlapping lookups share one request, which saves
  the anonymous API allowance; a saved answer is used only once GitHub confirms it, or while GitHub's rate limit lasts.
  A GitHub API request that takes longer than 30 seconds is given up and counts as could not be checked.

Library

- **Epic Games Launcher games inside a Wine, Lutris, Heroic or Steam prefix get their own cards** (upstream 2.8.8: the
  classic launcher and the new one), modded in their real folder; **Play** starts them through the launcher in that
  prefix.
- **Lutris store entries:** a Lutris entry that Lutris' Epic, Ubisoft, EA or Battle.net service made for one game now
  starts only that game; before, the first such entry started every game of that store in the prefix. A game in a prefix
  that has only other games' entries is still listed, without a launch command (set one in **Edit game**), and the scan
  line says how many such games still have none (no longer counted as "skipped"). Such an entry's card now sits in
  the game's own folder, so favourite, hidden and **Edit game** changes saved on its old card must be set again.

Check Neural Rendering

- **Three DLSS5 Feeder failures in plain words** (Borderlands 3's log): the driver not confirming Neural Rendering
  support (DLSS Super Resolution still works), a flat depth buffer (set in ReShade's in-game overlay, not in RHI), and
  the Feeder stopping after the game's display mode changed (restart the game with its HDR and resolution set first; if
  the Feeder stops again, turn HDR off in the game's options).

DLSS

- **The DLSS result line names each failed game with its reason** (upstream 2.8.8; Batch DLSS apply and restore, Apply
  defaults, Restore all), in the interface language, counts included; the same game owned on two stores is named twice.
  On a narrow DLSS batch dialog the line sits on its own row above the buttons.

## 2.8.7.1 — 2026-10-28: .deb, .rpm and .tar.gz downloads

- The same app as 2.8.7; only the version number and the downloads changed. Besides the AppImage, each release now
  has a `.deb` (Debian, Ubuntu and derivatives), an `.rpm` (Fedora, openSUSE) and a `.tar.gz` (any distribution),
  all made from the same build, and one `SHA256SUMS` file to check any of them with
  `sha256sum -c SHA256SUMS --ignore-missing`. The README's [Installing a release](README.md#installing-a-release)
  has the steps for each system and how to remove it.
- The `.deb` and `.rpm` install RHI in `/opt/rhi-linux` with the `rhi-linux` command and a menu entry; the package
  manager installs the system libraries RHI needs. There is no package repository: updates stay manual downloads.
- The update check is unchanged: it tells you when a newer release is published and never installs one itself. 2.8.7
  announces this release as newer.
- Going back to 2.8.7 needs nothing removed first.
- `packaging/build-packages.sh` builds the new downloads with a pinned, checksum-checked nFPM.

## 2.8.7 — 2026-10-27: first release

- The first published release: an AppImage with its `.sha256` file on GitHub Releases. It holds everything in the
  wave entries below, up to and including wave 11, and is level with upstream RHI 2.8.7 (how each upstream feature was
  adapted for Linux: [`UPSTREAM.md`](UPSTREAM.md)). It is still a beta.
- The app now reports version `2.8.7`. Earlier builds reported `0.9.23.4-beta.1`, so they announce this release as
  newer.
- The update check (Settings → Accounts & updates) reads only this project's releases on GitHub; no other repository
  is asked, also when nothing is published. Releases marked as pre-releases on GitHub are not offered to this build.
- Going back to the wave-11 build needs nothing removed first: only the version, the update check and the documents
  changed.

## Wave 11 — 2026-10-22: NR DLL per card, Check Neural Rendering, F5 driver warning, Nightly switch, stalled downloads

> [!WARNING]
> Before going back to the wave-10 build, do these first; it does not know what this build added:
>
> - In each game with ShortFuse or DLSS5 Feeder whose Neural Rendering DLL is on **Latest**, pick the version made for
>   your card by hand in the **NR DLL** dropdown (on an RTX 20, 30 or 40 card, the one marked `(20/30/40/50)`). The
>   wave-10 build takes the newest version in the list as **Latest**, whatever the card, so its next install or version
>   swap on **Latest** puts back a DLL made for another card (`310.8.3 (50xx)` on an RTX 40). It does not undo what this
>   build's Update All moved, and it keeps a version picked by hand.
> - Leave OptiScaler out of Update All (card menu **Choose what Update All changes…**) in games with OptiScaler DLSS NR
>   or F5 DLSSNR. The wave-10 build's Update All replaces their Neural Rendering DLL with the newest one in the list,
>   also one made for another card.
>
> The wave-10 build lists every Neural Rendering version, also those made for another card, and its **Latest** is the
> newest in the list. It installs F5 DLSSNR without the driver warning. A Nightly build switched here stays as it is:
> the wave-10 build keeps it pinned and leaves it out of Update All, but cannot switch it again (remove OptiScaler and
> install the build you want). A download that stops sending data can make it wait without end; press Cancel then.

Components

- **Neural Rendering DLL per graphics card** (wizardkof's policy): **Latest** is now the newest Neural Rendering DLL
  made for your card's generation, read from the DLSS list's label (`(50xx)`, `(20/30/40/50)`). Versions made for
  another generation are left out of every Neural Rendering list: the **NR DLL** dropdown, the DLSS section's swap row,
  **Batch DLSS** and the Settings default. With several NVIDIA cards the newest generation counts. When RHI cannot tell
  the card (no NVIDIA driver loaded, a card from before RTX), every version is listed, the dropdown's tooltip says why,
  and nothing is moved because of the card. A Settings default or a **Batch DLSS** choice made for another card skips
  only that game's Neural Rendering step, with a line saying so; the other DLLs are still applied.
- **Update All moves Latest Neural Rendering installs** onto the DLL made for the card: ShortFuse and DLSS5 Feeder Full
  installs on **Latest** whose DLL is made for another card (Borderlands 3 with `310.8.3 (50xx)` on an RTX 40) are
  swapped in place, and the update history shows the move. A version you picked by hand stays, with a row note when it
  is made for another card. A DLL RHI cannot identify, or one changed or removed outside RHI, is counted as "could not
  be checked" and left as it is; while the NR Cost Scaler is installed the move waits. OptiScaler DLSS NR and F5 DLSSNR
  take the DLL made for the card at their next install or update.
- **F5 DLSSNR driver warning** (upstream's driver check, as a question): before installing OptiScaler F5 DLSSNR, RHI
  asks when the NVIDIA driver is older than 615.71.09 (the first Linux driver of the 615 branch, the Linux equivalent of
  upstream's Windows 616.56) or cannot be read, and when the Neural Rendering DLL F5 would load is a 310.8.0 build on a
  615 or newer driver (it crashes there; 310.8.2 or newer is needed). **Install anyway** goes ahead; RHI never refuses.
  Update All never asks: its line carries the warning. ShortFuse, DLSS5 Feeder and OptiScaler rows say when their Neural
  Rendering DLL is a 310.8.0 build on such a driver. The F5 package is also checked for its forwarder DLL and its helper
  files before anything is installed.
- **Change an installed OptiScaler Nightly's build** (upstream's live dropdown, wizardkof's "Apply selected build now"):
  in OptiScaler's ⚙ the **Nightly build** dropdown stays available while Nightly is installed; pick a build (or
  **Latest**) and press **Apply**. The build is changed in place: OptiScaler.ini, the DLL name and the Wine override
  stay, and the update history records it. While the build downloads the row has its own **Cancel**. A switch that fails
  or is cancelled changes nothing, and the dropdown goes back to the installed build. The build Control Ultimate
  Edition's frame generation needs stays locked, with a note.
- **Check Neural Rendering** (as in wizardkof's newer beta): a read-only page per game, opened with **Check…** right
  after the **Neural rendering** line of the game's panel, or with **Check Neural Rendering…**, the last item of the
  card's right-click menu. It lists the graphics card and its generation (the driver version in its tooltip); the Neural
  Rendering DLL in the game, what put it there and whether it is made for this card, with the version made for it and a
  warning for a 310.8.0 build on a 615 or newer driver; the recommended Neural Rendering method and the ones in use;
  anti-cheat folders; and known errors in the ReShade, OptiScaler and DLSS5 Feeder logs beside the game's executable
  (only the last 2 MB of each is read). Each problem has a line saying where in RHI to fix it. Both openers show only
  for Windows games on an NVIDIA card with a Neural Rendering DLL or a Neural Rendering method RHI installed. The page
  changes no files.

Downloads

- A download that stops sending data is given up after 60 seconds without a byte, with a line that names the source,
  instead of waiting without end; a slow download that keeps arriving is never cut off. A server that never answers is
  given up after 5 minutes with RHI's own line. In Update All and the background update pass a stalled download counts
  for that game only (failed, or could not be checked) and the pass carries on; it never reads as Cancel. Cancel still
  stops a stalled download straight away.

Safety

- A game held after an undo RHI could not finish now also gets the restart message from OptiScaler's Update All, the
  Nightly build switch, OptiScaler's removal (also with RHI's ReShade chained behind it), Update All's Neural Rendering
  move and a Neural Rendering version swap, instead of a "modified outside RHI" refusal that took RHI's own half-undone
  file for a change of yours.

Interface

- The DLSS / Streamline section's version dropdowns grow to show the whole version picked: the Neural rendering one cut
  `310.8.2 (20/30/40/50)` short at every window size.

## Wave 10 — 2026-10-22: default DLSS presets, add-ons inside archives, games held after a failed undo

> [!WARNING]
> Before going back to the wave-9 build, do these first; it does not know what this build added:
>
> - Close RHI and check that `~/.local/share/rhi-linux/journal` is empty. When this build cannot finish undoing a
>   change, it holds that game until the undo succeeds. The wave-9 build does not: it lets that game change again, and a
>   later start may then undo the old change over the new one. Start this build once more first (make the game's folder
>   writable again if that was the problem) and let it finish the undo.
> - Delete `~/.cache/rhi-linux/dropped-archives` if it is there. It holds RHI's own copies of dropped and watched
>   archives; this build clears what an interrupted install left there when it starts, the wave-9 build never does.
>
> The wave-9 build forgets the default DLSS presets the next time it saves the settings; presets already copied into a
> game's NVIDIA settings stay. It does not open archives: a dropped or watched archive only gets its old notice.

Components

- **Default DLSS presets** (upstream's Settings): Settings → DLSS/Streamline has **Default presets** for DLSS, Ray
  Reconstruction and Frame Generation. **Apply defaults** in the DLSS section and **Batch DLSS** copy them into a game's
  NVIDIA settings, only for the rows that game's **NVIDIA settings…** shows and never over a DLSS 1.x file; Default
  leaves the game's own choice, and nothing is applied at launch from the defaults alone. The status line says which
  presets were set and which the game does not offer.
- **Batch DLSS** has the same three preset dropdowns under the versions, filled from Settings (**Do not change** keeps
  each game's preset). It goes game by game, so Cancel leaves the games it has not reached untouched; after Cancel or
  an error its line names the games whose presets were already set or reset. **Restore selected** sets the presets
  back to Default and turns **Newest DLSS from NVIDIA** off; render scale and frame generation stay.
- **Apply defaults** and **Batch DLSS** no longer put a newer DLSS over a DLSS 1.x file (upstream's rule; the check
  never fired on Linux before).
- **Add-ons inside archives** (upstream's archive drop): a `.zip`, `.7z` or `.rar` dropped on the window, or saved to
  the watched folder with "renodx" or "luma" in its name, is copied into a private folder of RHI's and checked: an
  archive with unsafe names, links or members larger than allowed is refused before anything is taken out. The RenoDX or
  Luma add-on inside is offered in the same dialog as a dropped add-on, with an **Add-on** dropdown when there are
  several, and installed as the file dropped alone; a Luma add-on installs the whole package. A watched archive is
  removed after a successful install while it is still the file RHI read; a dropped one is never removed. `.7z` and
  `.rar` need 7-Zip or bsdtar.

Safety

- If RHI cannot finish undoing a change (a folder or file it cannot write), that game is held: its next change tries the
  undo again first and is refused until the undo succeeds, with a message that says to restart RHI to finish it. Other
  games carry on. If RHI's own copy of a file it needs for the undo is gone (deleted, or the game was reinstalled), the
  change is closed instead and RHI names the file, so you can remove that component and install it again. A game
  folder that is not there at all (its drive not mounted, or unplugged) is not taken for a gone copy: the game stays
  held until the folder is back. When only a folder inside a game folder that is there was deleted, RHI's copy went
  with it: the change is closed and the file named.
- When the install records cannot be read after an error, the refusal of that game's next change now says to restart RHI
  to finish it.
- The note that a change was kept although its save reported an error now also appears for a preset installed into every
  game with its shader packs, for the add-ons applied after ReShade's install, and on a "shaders applied but the add-ons
  failed" line.

## Wave 9 — 2026-10-21: internal tidy-up

- An internal tidy-up of the component code; nothing changes in how RHI looks or behaves.

## Wave 8 — 2026-10-21: RenoDX Engine.ini settings for Unreal games, safer RenoDX and ReShade.ini changes

> [!WARNING]
> Before going back to the wave-7 build, do these first; it does not know what this build added:
>
> - Remove RenoDX from every Unreal Engine game where RHI installed it (at least where its ⚙ shows **Fix colour grading
>   updates (Unreal)** or **Turn on the game's HDR output (UE-Extended)**). This build's removal takes its lines out of
>   the game's `Engine.ini` and ReShade.ini and gives `Engine.ini` back the lock it had. The wave-7 build leaves those
>   lines in place and `Engine.ini` read-only, and its next RenoDX update forgets that RHI wrote them, so after that no
>   build takes them out; remove them by hand then (`r.LUT.UpdateEveryFrame=1`, the UE-Extended HDR lines).
> - Close RHI and check that `~/.local/share/rhi-linux/journal` is empty. A change this build left unfinished (RHI was
>   closed or crashed in the middle of one, or could not read its install records afterwards) may hold a step the wave-7
>   build cannot undo — putting back a read-only `Engine.ini` — and it would keep that entry. Start this build once more
>   first and let it finish or undo the change.
>
> Presets, **Apply to all games** and Luma ⚙ changes made here stay as they are; the wave-7 build simply does not know
> that some of those ReShade.ini lines are RHI's.

Components

- **RenoDX on Unreal Engine games** (upstream's Engine.ini setting): install, update, Update All and a dropped add-on
  put `r.LUT.UpdateEveryFrame=1` into the game's `Engine.ini`, unless the RHI manifest says not to, the game uses a
  custom Engine.ini file, or you turned it off. RenoDX's ⚙ gets a **Fix colour grading updates (Unreal)** switch. As
  upstream does, `Engine.ini` is made read-only while RHI's line is in it; turning the switch off or removing RenoDX
  takes only RHI's line out and gives the file back the lock it had. A line you already had is left alone and not
  locked, the file keeps its line endings and encoding, and RHI writes it only where the game has made its config folder
  (after its first launch). When `Engine.ini` cannot be written (a link outside the prefix, an unreadable file, a
  read-only config folder) RenoDX still installs and its line says why. If `Engine.ini` is no longer text RHI can edit,
  removing RenoDX still finishes: RHI's line stays in the file, the line names it and the file so you can delete it, and
  RHI's lock comes off.
- **UE-Extended RenoDX** (upstream's native HDR set-up): the HDR lines in `Engine.ini` and the native-HDR keys in
  ReShade.ini's `[renodx]` section are written as upstream's UE-Extended list says, and the ⚙ gets **Turn on the game's
  HDR output (UE-Extended)**. Switching UE-Extended off takes the HDR lines out again and removing RenoDX takes out
  every line RHI wrote; values you set yourself stay.
- **XNA games:** after **Repair after move**, the ReShade.ini copy in the prefix's .NET assembly folder points to the
  game's new folder. If you changed that copy, it is kept and the repair line names the file and the folder to set.

Safety

- RenoDX's install, update, dropped add-on and removal are each one change that is undone as a whole if anything fails,
  also after a crash: the add-on, its ReShade.ini keys, peak brightness, the Engine.ini lines and lock, and Control
  Ultimate Edition's files. A Control UE file that cannot be put back during removal now undoes the whole removal;
  trying again finishes it.
- ReShade presets, **Apply to all games** and the per-game Apply of the ReShade settings are recorded on ReShade's
  install record, so removing ReShade still deletes a ReShade.ini that RHI created, and RHI's preset choice stays when
  ReShade writes a new ReShade.ini.
- A ReShade.ini linked to your shared file in `~/.config/ReShade` stays a link when a preset, Luma ⚙, peak brightness,
  RenoDX or DLSS5 Feeder change is undone, and the shared file gets its earlier content back. A read-only `Engine.ini`
  stays read-only when a Luma ⚙ change is undone.
- When RHI's install records are saved but the save then reports an error, the change is kept and its line says so (also
  for the Luma ⚙, peak brightness, the RenoDX ⚙ `Engine.ini` switches, the per-game Apply, most Shaders/Add-ons Apply
  lines, a preset with its shader packs in one game, the batch buttons, Save global set, Update All and the ReShade
  settings file Repair after move rewrites; the few remaining lines since wave 10), instead of being undone while the
  records say it is installed. If the records cannot be read at all afterwards, nothing is undone: RHI finishes or
  undoes the change before the game's next change, or at the next start. The game's next change goes ahead once the
  records can be read again, and is refused with a plain reason until then.

Interface

- **What's new** shows quoted paragraphs, nested quotes and code blocks as written.

## Wave 7 — 2026-10-21: DX9 HDR, NVIDIA settings, global shader set, dropped add-ons, DLL renames

> [!WARNING]
> Before going back to the wave-6 build, do these first; it does not know what this build added:
>
> - Remove DX9 HDR from every game that has it. The wave-6 build drops DX9 HDR's install record the first time it saves
>   RHI's install records; after that no build can remove its `d3d9.dll`, `dxvk.conf` and Wine override, and a ReShade
>   that DX9 HDR moved to its Vulkan layer stays there. Removing DX9 HDR also puts that ReShade back as `d3d9.dll`.
> - Remove ReShade from XNA games (Terraria and the like). The wave-6 build does not know the copies in the prefix's
>   .NET assembly folder: it does not update them, and its removal leaves at least the ReShade.ini copy there.
> - Remove OptiScaler from a game where you renamed its DLL while RHI's ReShade runs through it. The wave-6 build moves
>   ReShade to OptiScaler's current name when it removes OptiScaler, and ReShade does not load under names such as
>   `wininet.dll`. This build puts ReShade back under its own name. Other renamed DLLs need nothing.
> - A RenoDX add-on installed from a dropped or downloaded file: leave RenoDX out of Update All for that game (card menu
>   **Update inclusion…** there) or remove it, or the wave-6 build's Update All replaces it with the RenoDX list's build
>   for the game.
>
> The wave-6 build forgets the NVIDIA settings the next time it saves the library and launches without them; a line you
> pasted into Steam's launch options stays there until you remove it. It ignores the global shader set and add-ons
> (`shader-addon-sets.json`): every game keeps the shader packs and add-ons it has. Back on this build, the next Apply
> in a game's Shaders/Add-ons dialog or the next save of the global set brings each game back to the choice saved here,
> replacing shader or add-on changes made on the wave-6 build.

Components

- **DX9 HDR**, a new row on DirectX 9 games (upstream's DXVK HDR for DX9): installs EndlesslyFlowering's DXVK HDR build
  beside the game with a preset chosen from its ⚙ (Safest to Experimental), and the game's per-exe Wine override. The
  download is verified and capped in size before it is unpacked. A ReShade already installed as `d3d9.dll` moves to its
  Vulkan layer, and comes back when DX9 HDR is removed. Not offered on Xbox / Game Pass installs or on XNA games; the
  row says when HDR launch is off or the game's launch command sets `PROTON_USE_WINED3D`, and still offers Remove when
  you deleted its `d3d9.dll` by hand; Update All then reports it as not updated (remove it and install it again) instead
  of putting the file back. Luma and the DLSS5 Feeder on a DX9 HDR game, and ReShade added for another component there,
  are refused with a plain reason before anything is downloaded. Not yet confirmed in a real game.
- **NVIDIA settings** (DLSS section → **NVIDIA settings…**, for DLSS games on NVIDIA GPUs with the NVIDIA driver): one
  dialog per game with the DLSS preset, render scale, the Ray Reconstruction and Frame Generation presets where the game
  has those files (Frame Generation on RTX 40 or newer), multi frame generation mode, frame count and target frame rate
  on RTX 50 cards (GeForce RTX 5050–5090 and Blackwell workstation cards), and **Newest DLSS from NVIDIA**. They are
  passed to Proton's NVIDIA layer at launch (Proton 10 or newer); for Steam games the dialog gives the launch-options
  line to paste into Steam. **Restore all** sets them back to Default.
- **Global shader set and add-ons**: Settings → ReShade → **Global shaders and add-ons…** sets the shader packs
  and Addon Manager add-ons every game gets by default. Each tab of a game's **Shaders/Add-ons** dialog chooses **Use
  the global set**, **Choose for this game** or **None**, and says when the game's files are not up to date with its
  choice. A game RHI already manages keeps the packs and add-ons it has, as its own list. While **Save global set**
  brings the games up to date, the dialog has its own **Cancel**, which stops between games.
- Changing a game's shaders now changes only the shader files and ReShade.ini's search paths, never ReShade itself (its
  version, channel and DLL stay), and is recorded in the update history; other ReShade.ini keys you changed, such as a
  hotkey, stay as they are. A change to the global set leaves alone games you left out of Update All for ReShade
  (shaders) or the Addon Manager (add-ons).
- **ReShade DLL names can be changed in place** once ReShade or OptiScaler is installed: the file is renamed, and the
  Wine override and RHI's record follow in one step that rolls back as a whole. **Reset DLL names** beside the dropdown
  puts every name back to automatic. Where another component decides the name (OptiScaler's chain, ShortFuse, the
  DirectX 9 translation, DX9 HDR) the dropdown is locked and says why. OptiScaler can be renamed while RHI's ReShade
  runs through it, and removing OptiScaler then puts ReShade back under its own name.
- **ReShade for XNA games** (Terraria and the other games the RHI manifest lists): ReShade goes into the game folder
  and, as copies, into the .NET assembly folder of the game's prefix, where XNA loads it. Updates replace both copies;
  removal takes away only the copies RHI placed. Repair after move also re-points those copies when the prefix moved;
  switching the game to another prefix, or reaching the same prefix through a symbolic link, does not count as a move.
  When the game moved to a fresh prefix, Repair after move says to copy the game folder's `d3d9.dll` into that prefix's
  .NET assembly folder first.
- ReShade's install and update now fill in only the keys a `reshade.ini` template adds that the game's ReShade.ini does
  not have yet; **Deploy** still writes every key.
- A ReShade.ini linked to your shared file in `~/.config/ReShade` stays a link when a ReShade install, update or removal
  fails or is cut short, and removing ReShade takes RHI's lines back out of the shared file. A ReShade.ini linked
  somewhere RHI does not write no longer stops ReShade's install (with **Manage ReShade.ini** off) or removal; RHI
  leaves that file alone.
- **Dropped and downloaded add-ons**: a RenoDX add-on dropped on the window, or saved to the watched folder, installs
  into the game you pick (the matching game is preselected; a file from the watched folder is removed from it once
  installed). Update All cannot check it, and says so. Another ReShade add-on you drop (one that no RHI row installs
  itself) goes into Custom Add-ons and is ticked in that game's Shaders/Add-ons list, which becomes the game's own list;
  only games with ReShade with add-on support in their folder are offered (not the No Addons build), and with none the
  status line says so and nothing is copied. Bitness is checked first, with a plain reason.
- **NR Cost Scaler** is updated by Update All; a scale you set in `nvngx_dlssnr.ini` is kept.

Library and interface

- The card menu's **Update inclusion…** is now **Choose what Update All changes…**, and the Update All summary says how
  many components you left out.
- A component row's ⓘ is highlighted where the selected game has notes of its own.
- **What's new** shows the changelog's text again, not only its headings.
- **Create shortcut** gives the shortcut the game's own icon (from its `.exe`, else its Steam art).

## Wave 6 — 2026-10-20: Addon Manager updates, non-Steam shortcuts, crash-safe Feeder and Luma

> [!NOTE]
> **Repair after move** now treats ReShade Addon Manager add-ons like every other component. If an add-on RHI placed
> is missing from the game's new folder (deleted by hand, say), the repair changes nothing and says which file is
> missing, and the moved game's installs, updates and removals stay blocked until the repair succeeds. Before, the
> repair skipped missing add-ons. Put the file back at the new location (or move the game back and remove that add-on
> there first), then run the repair again.

> [!NOTE]
> Going back to the wave-5 build needs nothing removed first (for older builds, the wave-5 and earlier notes below still
> apply). The Addon Manager's selection is still written to `addon-manager.json`, which older builds read, so they keep
> seeing the add-ons. An older build drops the Addon Manager's entries from RHI's install records the first time it
> saves them. Back on this build, RHI takes the selection up again from that file, including changes the older build
> made; where the entries were dropped, Update All downloads those add-ons once more, because the versions recorded for
> them are gone. An older build shows no card for non-Steam games added to Steam, so components installed on them
> cannot be updated or removed from it; their install records are kept, and this build manages them again.

Library

- **Non-Steam games added to Steam** (Steam's "Add a Non-Steam Game") get their own cards when the shortcut points at
  a Windows game's `.exe`, with the Proton prefix and compatibility tool Steam uses for it; Play starts the shortcut
  through Steam. A shortcut to the EA app, Ubisoft Connect, Battle.net, Rockstar or itch.io gives no card of its own:
  every game of those stores found in the shortcut's prefix gets a card instead, modded in its real folder. For that
  launcher's own games, Play starts the launcher through Steam for you to start the game from; a game from another
  store in the same prefix has no launch command until you set one in **Edit game**. Installers, uninstallers, native
  programs and scripts are not listed, and a shortcuts file that cannot be read is a scan warning; the rest of the
  Steam library is still listed. These cards show as "Steam/Non-Steam".
- The Luma mod for a game is found by the wiki name set in **Edit game** (or the manifest's) as well as by its
  detected name, and changing the wiki name looks it up again.

Components

- **ReShade Addon Manager add-ons are updated** by Update All and by the background update pass. Each add-on records
  the release or file it was downloaded as; when that cannot be compared with the add-on list, the add-on is reported
  as "could not be checked" rather than current. An add-on turned off for No Addons is held, not updated. Update
  inclusion lists it as "ReShade Addon Manager".
- **Download every shader pack at start-up** (Settings, on by default; upstream's "cache all shaders"): every shader
  pack not yet in RHI's cache is downloaded in the background once the library is listed, ready for later ReShade
  installs. A pack already cached is not replaced, and closing RHI stops the download.
- **Deploy ReShade.ini** is now recorded on ReShade's install record, so removing ReShade takes back the search paths
  and settings it added, and still deletes a ReShade.ini RHI created. A change you made to the file meanwhile is kept.
- The OptiScaler ⚙ dialog shows the "not RHI-managed" warning once instead of twice.

Safety

- The Addon Manager's selection is part of RHI's install records: a failed or interrupted change puts the add-on files
  and the selection back together.
- **DLSS5 Feeder** and **Luma** installs and removals are one journaled operation each, including their INI edits and,
  for DirectX 9 games, dgVoodoo2 and the move of ReShade between `d3d9.dll` and `dxgi.dll`: a failure or crash puts
  every file back, and ReShade's record always names the file ReShade is really in. ReShade's shader-pack folder is
  journaled with ReShade's install and update.
- A display switch that failed and could not go back says the display may not be at its previous mode and that RHI
  puts it back at its next start.

Interface

- Brazilian Portuguese: the OptiScaler ⚙ dialog's channel note no longer uses the English word "Install".

## Wave 5 — 2026-10-15: Vulkan ReShade, Follow newest DLSS, update inclusion, INI templates and presets, info dialogs

> [!NOTE]
> Before going back to an older build, remove ReShade from every game where its row reads "ReShade · Vulkan": an older
> build cannot remove the Vulkan layer, so its registration, the Vulkan loader beside the game and its folder in the
> prefix would stay, with the game's `vulkan-1` Wine override. The older build also drops the layer's registry details
> from RHI's record when it saves, after which even this build cannot remove the registration. Removing it first leaves
> only RHI's `system.reg.rhi-lock` and up to three `system.reg.rhi-backup-…` files in the prefix. An older build also
> forgets names, wiki names and bitness set in Edit game the next time it saves the library.

ReShade

- **ReShade for Vulkan games** (Windows games under Proton or Wine 8 or newer). On a game whose graphics API is Vulkan
  the ReShade row reads **ReShade · Vulkan**, and its Install sets ReShade up as a Vulkan layer for that game only: the
  Khronos Vulkan loader beside the game, the layer in the prefix, its registration in the prefix's `system.reg`, and a
  `ReShade.ini` from your `reshade.vulkan.ini` template. DirectX games and multi-renderer games are unchanged, and a
  component that needs ReShade never installs the layer by itself. A plain reason is given where it cannot work (an old
  Proton, a prefix never started, a game with its own `vulkan-1.dll`, a 32-bit game in a 32-bit-only prefix). Not yet
  confirmed in a real game.
- Changing a game's graphics API to or from Vulkan while ReShade is installed is refused until ReShade is removed.
- **Legacy ReShade** versions come from a list (the RHI manifest's, or 6.4.1), and games the manifest pins to an older
  ReShade (Max Payne 3 → 6.4.1) get that version on a fresh install. An installed ReShade is never changed by a pin.
  The manifest's ReShade note for a game shows in its ReShade profile and in ReShade's ⓘ.
- **INI templates:** RHI's INIs folder holds editable `reshade.ini`, `reshade.vulkan.ini`, `reshade.rdr2.ini`,
  `relimiter.ini` and `DisplayCommander.ini`. A ReShade install merges your template into the game's ReShade.ini (every
  other line stays). Settings → ReShade adds the effect list style (Tabs/Tree), **Apply to all games** for the
  screenshot folder, hotkeys and effect list, and **Deploy** of each template to every game where RHI manages the
  component; each asks first, with the number of games. The ⚙ on the ReShade, ReLimiter and Display Commander rows
  deploys the template to one game. A ReLimiter or Display Commander INI you changed is replaced only after you confirm,
  and is kept beside it as a backup you can restore.
- **ReShade presets:** the ReShade row's ⚙ installs a preset into the game; a preset `.ini` dropped on RHI is installed
  into a game you pick, optionally with the shader packs it needs; Settings → ReShade installs one preset into every
  game where RHI manages ReShade. A preset already in your presets folder is never overwritten.
- **ReLimiter** global settings in Settings → Shaders/Add-ons: on-screen display key, shared presets, DLSS info and FPS
  cap, with Apply to all games.

DLSS and updates

- **Follow newest DLSS / Streamline** (Settings → DLSS/Streamline, off by default): when the DLSS manifest publishes a
  newer DLSS (SR/RR/FG) or Streamline, games whose files are on the previous newest are moved to it. Versions you picked
  yourself, a game's own older files and DLSS 1.x stay as they are; a running game waits for the next pass; it never
  moves a game to an older version.
- **Update inclusion:** Settings → Maintenance → **Update All includes** leaves a component out of Update All for every
  game; a card's right-click **Update inclusion…** does it for one game (and leaves that game out of Follow newest). A
  component's own Update button still works.

RenoDX, Luma and OptiScaler

- **Peak brightness** for RenoDX (Settings → ReShade): a value written into RenoDX's presets on install, **Auto** reads
  it from your monitor (or KDE's display settings), and Apply to all games. The RenoDX ⚙ sets a game's own maximum nits
  and the UE-Extended choice.
- **Luma ⚙** on Unreal Engine games: HDR on first boot and TAA.
- **OptiScaler presets:** four saved sets of ⚙ settings to apply to any game, and a global **OptiScaler menu key**
  (Settings → DLSS/Streamline) with Apply to all games.

Library and links

- **ⓘ on component rows** (RenoDX, Luma, OptiScaler, ReLimiter, Display Commander, RE Framework, ReShade): the notes
  for the selected game from the RHI manifest, the RenoDX and OptiScaler wikis and change logs, with links.
- The card's right-click menu adds **Discussion / instructions** and a **Web pages** submenu (PCGamingWiki, Ultra+,
  ultrawide fix, HDR Gaming Database), each only where the game has one. Each kind of link opens only its own sites.
- **Available HDR mods** also lists RenoDX mods published only on Nexus Mods or Discord, and Unreal Engine games of the
  UE-Extended database.
- A **New mods** button appears in the toolbar when the RenoDX, Luma or Ultra+ lists have mods you have not seen.
- **Edit game** can change the name a game is shown under, the wiki mod name used to find its RenoDX mod, and its
  bitness (refused while RHI components are installed and the change would matter).
- **Saved filters:** save the current search as a named filter chip under the filter tabs.
- **Change resolution while playing** (HDR row): a game RHI starts directly switches the display to the size you chose
  and back when it ends, also when RHI was closed meanwhile; with Gamescope the size goes to Gamescope instead. Uses
  `kscreen-doctor` on KDE Plasma or `wlr-randr` on wlroots desktops. Not yet confirmed on a real desktop.

Support

- Settings → **Support**: **Copy logs** (all of RHI's logs zipped, with your home folder, user name, Steam IDs, tokens
  and saved keys masked), **Open logs folder** and **Export game data**. A card's right-click **Copy game report** saves
  a masked report for that game.
- **Help** (from Quick start) and **What's new** (from Help or the version in the status bar).

## Wave 4 — 2026-10-15: store launchers, RTX Encore, No Addons add-ons, OptiScaler DLL name

> [!NOTE]
> A build older than this one does not know RTX Encore, the OptiScaler DLL-name choice or add-ons turned off by No
> Addons. The first time it saves a ReShade DLL name, it forgets RTX Encore's saved DLL name and Neural Rendering
> choice, and the saved OptiScaler DLL name, for every game where that component is not installed. Remove RTX Encore
> from every game first: an older build cannot update or remove it and drops its install record the first time it saves
> any record, after which even this build cannot remove it. Switch No Addons games back to Stable or Nightly first too.

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
> "Latest (auto-update)".
> See [`UPSTREAM.md`](UPSTREAM.md#rolling-back-to-a-build-older-than-wave-3-f5-dlssnr-nightly-pins).

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
