# TLO — Tarkov Light Overlay

[Website](https://William-Baaklini.github.io/Tarkov-Light-Overlay/) ·
[Download for Windows](https://github.com/William-Baaklini/Tarkov-Light-Overlay/releases/latest/download/TLO-win-x64.zip) ·
[Releases](https://github.com/William-Baaklini/Tarkov-Light-Overlay/releases)

A deliberately small always-on-top overlay for Escape from Tarkov: hit a shortcut
mid-game, get Tarkov.dev / the Wiki / eft-ammo / your map images with keyboard
focus, hit it again to drop straight back into the game.

It also reads the item under your cursor straight off the screen and tells you
what it sells for, without ever touching the game process.

**Idle cost while you play: ~20 MB and zero browser processes.**

---

## Quick start

1. Download **TLO-win-x64.zip** from the link above or from Releases.
2. Extract the **entire ZIP** into a folder of your choice.
3. Run **TLO.exe** inside that folder. Keep `tiles` and `data` alongside it.
   No installer or separate .NET installation is needed. The runtime libraries
   are bundled inside the executable, so there are no loose DLLs to manage.
4. Use **Ctrl + Shift + T** to show or hide the overlay.

TLO opens on first launch; later launches start hidden in the system tray.
The executable, window, and tray use the bundled gold TLO emblem. This community
build is unsigned; the icon is branding, not a code-signing certificate.
The emblem also appears in the title bar, to the left of Tarkov.dev.

| Action | Default |
| --- | --- |
| Show / hide the overlay | `Ctrl + Shift + T` |
| Second monitor fullscreen / restore | `Ctrl + Shift + M`, **Monitor** / **Restore** in the title bar, or the tray menu |
| Hide it | `Esc`, the `✕` button, or the shortcut again |
| Price the item under the cursor | `Alt + F` (again to open it on tarkov.dev) |
| Settings | `⚙` in the title bar, or right-click the tray icon |
| Reset window size | Right-click tray icon → Reset window size |
| Quit properly | Right-click tray icon → Exit |

Set your own shortcuts in Settings. Any mix of `Ctrl` / `Alt` / `Shift` / `Win`
plus a key works, and a bare function key (`F1`–`F24`) is allowed too. You can
also bind a shortcut that jumps straight to one tab — pressing it again while
that tab is showing hides the overlay.

## Requirements

- Windows 10/11
- Windows x64; the release ZIP includes the .NET runtime
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) for the web tabs
- **Tarkov must run in Borderless or Windowed mode**, not exclusive Fullscreen

### Why Borderless

The overlay is an ordinary always-on-top window. It does **not** hook, inject
into, or read the game's memory. Windows cannot draw this ordinary desktop
window over an exclusive-fullscreen game. Use Borderless or Windowed mode.
This community tool does not claim anti-cheat approval.

Taking keyboard focus works because Windows grants foreground rights to the
process that owns a shortcut when that shortcut fires — no injection needed.

## Second monitor mode

Press **Ctrl + Shift + M** to show TLO on another monitor and fill that display,
including the taskbar area. The resize frame disappears; the tabs and title-bar
controls stay available. Press the same shortcut, or click **Restore**, to return
to the previous position and size. A previously maximized window is restored to
that state too.

- Change the combination in **Settings → Shortcuts → Second monitor / restore**,
  just like the overlay, tab, and scanner shortcuts.
- Choose the destination under **Settings → Fullscreen monitor**. The default,
  **Automatic — non-primary monitor**, chooses a non-main display even if the
  normal window is already on that display. With multiple non-main displays,
  automatic mode sorts by Windows display device name.
- Select a specific display to always use it. The list includes resolutions and
  marks the main display. Your choice is saved across restarts; you can select
  the main display too, including when it is the only connected monitor.
- If a selected display is disconnected, it remains listed as disconnected.
  The shortcut shows a notice and leaves the window unchanged until you reconnect
  it or choose another display. Automatic mode similarly leaves the window alone
  when there is no non-main display.
- A changed preference applies the next time you enter fullscreen. If already
  fullscreen, the next shortcut press still restores your previous window first.
- Hiding and reopening keeps the current fullscreen mode for this session.
  Your normal window bounds remain saved for the next launch.
- If a monitor is disconnected or the desktop becomes smaller, the restored
  window is fitted onto an available display so its controls remain reachable.

## The tabs

| Tab | What it is |
| --- | --- |
| **Tarkov.dev** | tarkov.dev item / quest / barter database |
| **Wiki** | Official Escape from Tarkov Fandom wiki |
| **Ammo** | eft-ammo.com ammo tables and charts |
| **Maps** | Your own map images, zoomable |

The three web tabs put the caret in the site's own search box as soon as the page
settles, so you can start typing a quest or item name immediately. On eft-ammo,
where the search sits below the fold, the page scrolls to it first.

Each web tab also has back / forward / reload / home and an address bar
(`Ctrl + L` to jump to it). Typing something without dots searches the wiki.

## Maps

Drop map images into `maps\` and run:

```
tile-maps.bat
```

Then restart the overlay — new maps appear as buttons in the Maps tab.

| Control | Action |
| --- | --- |
| Mouse wheel | Zoom at the cursor |
| Drag (left or middle) | Pan |
| Double-click | Toggle fit ⇄ 100% |
| Arrow keys | Pan |
| `0` or the Fit button | Fit to window |

The overlay remembers which map you had open and where you were zoomed to on
each map, per map, across restarts.

### Notes on maps

The bar under the map buttons lets you draw on a map and pin text to it: a
route you want to take, a spot to avoid, where a key goes. Everything is saved
per map and comes back exactly where you left it, at any zoom.

| Tool | Key | What it does |
| --- | --- | --- |
| **Pan** | `V` | Normal map behaviour. Drag to pan, wheel to zoom |
| **Draw** | `D` | Drag to draw. A tap makes a dot. `Ctrl`+drag or middle-drag still pans |
| **Text** | `T` | Click to place a note, type, `Enter` to keep it (`Shift+Enter` for a new line, `Esc` to cancel). Click a note to edit it, drag it to move it |
| **Erase** | `E` | Hover shows what will go, click or drag across it to delete |
| Colours / Thin / Medium / Thick | | Apply to the next thing you draw or write |
| **Undo** / **Redo** | `Ctrl+Z` / `Ctrl+Y` | Works on drawing, erasing, moving, editing, and Clear map |
| **Hide notes** | `H` | Get the clean map back without losing anything |
| **Clear map** | | Deletes everything on this map, after asking |

Some details that make it feel right:

- Positions are stored in **map pixels**, not screen pixels, so a line drawn
  zoomed in on a doorway sits on that doorway when you zoom out, and vice versa.
- Line width stays constant on screen: a route drawn at fit-to-window is still
  a clean line when you zoom into it, not a fat smear.
- Every stroke and pin has a dark rim so it reads on snow, sand and rust alike.
- The overlay drops back to **Pan** each time you hide it, so the first drag
  after the hotkey moves the map rather than scribbling on it.
- Notes autosave moments after each change and again when the overlay hides,
  to `%APPDATA%\TarkovOverlay\notes\<map>.json` — one small file per map, so
  they survive a config reset and can be backed up or shared.

### Why tiling matters here

Your `Interchange.png` is 12241 × 8380. Loaded as a plain image that is about
**410 MB of RAM for one map**, which alone would blow the whole memory budget.

`tools\tile_maps.py` slices each map into 512 px tiles across several zoom
levels. The viewer only ever decodes the ~20 tiles currently on screen and
evicts them as you pan, so memory stays flat no matter how large the source
image is. The nine current maps become 2466 tiles / ~445 MB on disk.

Filenames do not have to be tidy — `groundzero.png` becomes **Ground Zero** in
the tab strip, and the strip wraps to a second row rather than clipping when the
window is narrow.

## The item scanner

Hover an item in your stash and press `Alt + F`. A small card appears next to it
with the flea price, price per slot, and the best trader offer. Press `Alt + F`
again and that item opens on tarkov.dev in the overlay.

Turn on **Price on hover** in Settings and the card appears on its own, no
keypress.

### How it reads the screen

It never touches the game. This is a desktop screen grab plus arithmetic — no
process is opened, nothing is injected, no game memory is read. Two facts about
Tarkov's inventory carry the whole thing:

1. **The grid pitch is 63 px**, and every reference icon is `63n + 1` px on a
   side. At 1080p / 100% scale the game blits those icons 1:1, so a crop off
   your screen is very nearly the reference image.
2. **Tarkov merges an item's cells** into one borderless rectangle, so a missing
   grid line means the item continues that way.

Neither of those needs a single one of Tarkov's colours, which is deliberate —
they change with rarity, patch and UI theme. Where the icon is transparent the
slot background shows through, so the observed pixel is

```
observed = premultiplied_icon + (1 - alpha) * background
```

The index stores premultiplied luminance *and* alpha, which lets the matcher
solve for `background` per candidate in closed form. That makes matching
invariant to any rarity tint without ever enumerating the palette.

### What it costs

| | |
| --- | --- |
| Index on disk | **2.0 MB** for 3876 icons |
| Icon files at runtime | **none** — the card's thumbnail is the crop off your screen |
| One scan | ~7 ms median, on a worker thread |
| Hover mode idle | a `GetCursorPos` every 66 ms; a capture only once the cursor settles on a *different* cell |

For comparison, RatScanner ships 62 MB of OpenCV, 46 MB of Tesseract language
data and 56 MB of icons to do the same job. None of that is needed: because the
pixels match the reference almost exactly, this is a lookup rather than a fuzzy
template match, and the item IDs are already tarkov.dev's own IDs so no OCR or
name matching is involved.

### How well it works

Measured over 889 synthetic stash pages (`tests\ScanTest`), which reproduce the
63 px grid at a random phase, merged item rectangles, rarity-tinted backgrounds
and the durability bar and stack count the game paints over the artwork:

| | |
| --- | --- |
| Correct item picture | **97.9%** |
| Wrong slot shape | 1.0% |
| Exact item ID | 83.1% |

The gap between the first and last rows is not scanner error. **About 13% of
Tarkov items share their grid image with another item** — one picture is used by
as many as 37 different IDs — so no algorithm can tell them apart from pixels.
When that happens the card names the best match and says "or N other items with
this icon" rather than pretending to be sure.

### Limits worth knowing

- **1080p at 100% scale.** The 63 px pitch is what makes this exact. At 1440p or
  4K the inventory scales and the crop would need resampling first; that is not
  implemented.
- **Borderless or windowed**, same as the rest of the overlay.
- If the capture spills off the inventory onto very different content, the grid
  phase can be misread. The scanner takes two independent readings of it and
  keeps whichever actually explains an icon, which covers the cases seen in
  testing, but it is the most fragile step.

### Rebuilding the index

```
build-index.bat
```

Reads a folder of `<tarkov.dev item id>.png` grid images and writes
`data\icons.idx`. Run it after a patch adds items. Point it somewhere else with
`--source`. The icons are only needed to build the index, never to run.

## Memory

Measured on this machine, working set including every WebView2 child process:

| State | Total |
| --- | --- |
| **Hidden (playing)** | **~21 MB**, browser processes fully gone |
| Open, page loaded and settled | ~70–250 MB |
| Transient peak while a heavy page loads | up to ~570 MB |

The design goal was the first row, because that is the state you are in for
almost the entire time the game is running.

What keeps it there:

- **Browsers are destroyed on hide**, not merely hidden — that is the single
  biggest lever, and it is what the "Unload web pages when hidden" setting does.
  Turning it off keeps pages warm and instant to reopen, at ~60–120 MB idle.
- Only one web tab is alive at a time; switching tabs disposes the other.
- The map viewer drops every decoded tile when you hide the overlay.
- Ad and tracker blocking, which is most of the weight on the wiki.
- **Low GPU mode** (on by default) renders pages in software, which drops the
  GPU helper process and keeps the overlay from competing with the game for the
  GPU. Turn it off in Settings if page scrolling feels rough.
- Workstation non-concurrent GC, a compacting collect and a working-set trim
  every time the overlay is hidden.

## Ad and tracker blocking

Two levels, both in Settings:

- **Block ads and trackers** (on by default) — blocks ad exchanges, analytics,
  beacons and session recorders, and hides the empty slots they leave behind.
- **...strictly** (off by default) — additionally blocks anti-adblock
  circumvention CDNs. Saves more, **but Fandom may refuse to render the page.**

Two details worth knowing, both found by logging real page loads:

1. Blocked requests are answered with an empty **HTTP 200**, never a 403 or a
   connection error. Anti-adblock scripts detect blocking by watching for
   requests that *fail*; a clean empty response looks like an ad that had
   nothing to show, so the page keeps working.
2. The anti-adblock CDN on the wiki **rotates its hostname constantly** — inside
   one test session it moved `html-load.com` → `veto.hencewafer.com` →
   `fb.stg.content-loader.com`. Chasing domain names is unwinnable, so strict
   mode matches the one thing that survived every rotation: the `stg` label in
   the hostname.

To block something yourself without rebuilding, create
`%APPDATA%\TarkovOverlay\blocklist.txt` and put one hostname per line
(`#` starts a comment). Subdomains are matched automatically.

To see exactly what is being requested and blocked, set
`TARKOV_OVERLAY_LOG_REQUESTS=1` before launching; it writes
`%LOCALAPPDATA%\TarkovOverlay\requests.log`.

## Settings

| Setting | Notes |
| --- | --- |
| Shortcuts | Overlay toggle, second monitor / restore, and one per tab. Click a field, press the combo, `Backspace` clears |
| Fullscreen monitor | Automatic non-primary display, or a specific saved display; resolutions and main-display status are shown |
| Opacity | 30–100%, previews live as you drag |
| Unload web pages when hidden | Lowest RAM. Off = instant reopen, ~60–120 MB idle |
| Block ads and trackers / strictly | See above |
| Low GPU mode | Needs a restart to apply |
| Map tile cache | 12–256 tiles, ~0.8 MB each. Default 48 |
| Item scanner | On/off, its shortcut, and whether it prices on hover |
| Price on hover | No keypress. Optionally only while Tarkov is the active window |

Config lives at `%APPDATA%\TarkovOverlay\config.json`.

## Starting with Windows

Press `Win + R`, run `shell:startup`, and drop a shortcut to `TLO.exe` in there.

## Build from source

Install the .NET 8 SDK or a newer compatible SDK, then run `build.bat` followed
by `run.bat`. Development builds need the .NET 8 Desktop Runtime to run.
The executable is at `src/TarkovOverlay/bin/Release/net8.0-windows/win-x64/TLO.exe`.
Exit an older running copy from its tray menu before starting the new one.

Generated tiles are excluded from Git. To generate them from the included maps,
install Python, run `python -m pip install Pillow`, then `python tools/tile_maps.py`.
The bundled `data/icons.idx` is ready to use; rebuilding it additionally requires
NumPy and a folder of source item icons passed using `--source`.

Run placement regression checks with:

```powershell
dotnet run --project tests/WindowPlacementTest -c Release
```

Create the portable download with `publish.bat` (or
`powershell -File tools/publish.ps1 -Version 1.0.2`). This publishes a Windows x64
[self-contained build](https://learn.microsoft.com/dotnet/core/deploying/), copies
all map tiles and scanner data, and creates `release/TLO-win-x64.zip` plus its
SHA-256 checksum. The ready-to-run folder is at `release/TLO-win-x64/`.
Existing downloads and published folders are never overwritten; move them first
to rebuild, or pass `-OutputDirectory release/v1.0.2` to build beside a running
copy. To verify that output, run `python tools/verify_release.py release/v1.0.2`.
Native runtime components are extracted by .NET into its temporary
cache on first launch; the download folder stays tidy.

For a noninteractive installation check that leaves your settings untouched, run
`TLO.exe --verify-install C:\path\to\report.json`. It verifies WinForms, maps,
the scanner index, icon loading, and the installed WebView2 Runtime. The report's
parent folder must already exist.

## GitHub releases and website

Source code, source map images, and scanner data live in the repository.
Compiled files and generated tiles stay out of Git. The **Build Windows release**
workflow tests placement, regenerates tiles, and packages the executable. A `v*`
tag creates a draft [GitHub Release](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)
with the ZIP and checksum; review and publish that draft. GitHub also supplies
source archives automatically. The workflow can be run manually for a build artifact.

The landing page lives in `docs/`. GitHub Pages serves `main` → `/docs`.
Its download button always targets the latest published release's `TLO-win-x64.zip`.
See [third-party notices](THIRD-PARTY-NOTICES.md) for bundled components and artwork.

## Layout

```
maps\                   your source map images
tiles\                  generated tile pyramid (built by tile-maps.bat)
tools\tile_maps.py      the tiler
src\TarkovOverlay\
  OverlayForm.cs        window, tabs, tray, shortcut handling, show/hide
  WebTab.cs             lazily created WebView2 + blocking + search focus
  RequestFilter.cs      what gets blocked and why
  MapView.cs            tiled pan/zoom viewer + drawing / text note tools
  MapNotes.cs           note model and per-map JSON persistence
  TileCache.cs          LRU tile cache with a background loader
  HotkeyManager.cs      RegisterHotKey wrapper
  HotkeyBox.cs          shortcut capture control
  SettingsForm.cs       settings dialog
  Config.cs             settings model + persistence
  ScreenScan.cs         screen grab, grid detection, slot matching
  IconIndex.cs          the icon index and the matcher
  ItemScanner.cs        hotkey and hover triggers, worker thread
  ScanPopup.cs          the price card
  TarkovDevApi.cs       tarkov.dev prices + offline cache
toolsuild_icon_index.py   builds data\icons.idx from grid images
tests\ScanTest\        synthetic stash pages that measure the scanner
data\icons.idx         the 2 MB index the scanner matches against
```

## Troubleshooting

**The shortcut does nothing.** Another app already owns that combination —
Windows refuses duplicates. The tray icon shows a warning when a shortcut fails
to register; pick a different one in Settings.

**The overlay does not appear over the game.** Tarkov is in exclusive
Fullscreen. Switch it to Borderless.

**The window is bigger than the screen and cannot be grabbed.** Right-click the
tray icon → **Reset window size**. This used to happen after dragging the
overlay between monitors running different display scaling: Windows rescales the
window as it crosses, so a window sized on a 125% display arrives too large for
a 100% one, and because the overlay is borderless its resize edges end up off
screen — where they were then saved and restored every launch. The window is now
fitted to its monitor on restore, when shown, and whenever Windows moves it
across a scaling boundary, so it should not recur.

**It appears but the game keeps the keyboard.** If Tarkov is running as
administrator and the overlay is not, Windows blocks the focus change. Run the
overlay as administrator too, or the game without it.

**A page will not load.** Turn off strict ad blocking in Settings — that is
almost always the cause.

**"No maps found".** Run `tile-maps.bat`, then restart the overlay.

**The price card says "Prices not loaded yet".** tarkov.dev has not answered.
Prices are cached at `%LOCALAPPDATA%\TarkovOverlay\prices.json` and refreshed
every 20 minutes, so this clears itself once the API is reachable; the scanner
still identifies items meanwhile.

**The scanner reads the wrong item.** Check Tarkov is at 1920x1080 with UI scale
100% — the whole method depends on that 63 px cell pitch. If the item genuinely
shares its artwork with others, the card says so.
