# m4xmudovv

A clean Windows 10/11 x64 WPF live wallpaper application, built from scratch without Lively Wallpaper source code.

## Windows installer

The installer is built as a **self-contained Windows x64** application, so users do not need to install .NET or another runtime separately. The full GitHub release installer includes the bundled video wallpapers; the large media files are downloaded by GitHub Actions during the Windows build rather than committed to the source repository.

On Windows, run `build-installer.bat` after installing the .NET 8 SDK and Inno Setup 6. The installer uses app-branded dark wizard artwork and is written to `dist\m4xmudovv-Setup.exe`. If the source tree does not contain the optional `Wallpapers` folder, the local build produces a runtime-only installer; the GitHub Actions release is the full build with videos.

The application also repairs old saved bundled paths after reinstalling or moving the app. For example, an old `D:\Video\package\publish\Wallpapers\Videos\v321.mp4` entry is remapped to the current installation's `Wallpapers\Videos\v321.mp4` when that file exists.

GitHub Actions is also configured in `.github/workflows/windows-release.yml`. Push a tag such as `v2.0.0` to build the installer on a Windows runner and publish it automatically under GitHub Releases.

## Current milestone: reliable thumbnails, hover previews and readable controls

- **Thumbnails.** Imported images are decoded straight to 640 px and cached as JPEGs under `%LOCALAPPDATA%\\m4xmudovv\\thumbnails` (key = path + size + write time). Cards show a frozen 400 px decode of that cache file, never the original image.
- **Video thumbnails.** Generated through the Windows Shell thumbnail provider (what Explorer shows), with a `MediaPlayer` frame capture as fallback for formats the shell cannot thumbnail. Cache writes are atomic (temp file + move), so a failed write can no longer leave a corrupt "valid" thumbnail.
- **Throttled background work.** Image decode, shell extraction, `MediaPlayer` capture and cache decode each have their own concurrency limit; the UI thread is never blocked and the library is saved before thumbnails finish.
- **Hover preview.** Hovering a video card (after a short 220 ms delay) creates one muted `MediaElement` inside that card; leaving, scrolling, filtering, minimizing or deactivating the window disposes it and restores the thumbnail. Only one preview exists at a time and no card owns a `MediaElement` until hovered.
- **Layout redesign.** The window now follows the mockup: text logo and navigation sidebar, search bar plus Import on top, a 3-column card grid (play badge, type pill, title), and a details panel with an in-preview play/pause + timeline bar, Set as Wallpaper, Preview, Volume, Playback speed (0.5x-2.0x), Fit mode (Fill / Fit / Stretch / Center) and file details. Icons use Segoe Fluent Icons (Windows 11) with Segoe MDL2 Assets as the Windows 10 fallback. Favorites, Playlists, About, heart and "more" show a "planned" status message for now.
- **Set as Wallpaper (desktop host).** `DesktopWallpaperHost` implements `IWallpaperHost`.
  - *Images:* applied through the Windows `IDesktopWallpaper` COM API on every monitor with the selected Fit mode (Fill / Fit / Stretch / Center). GIF and other unusual formats are converted to a cached PNG first. If the COM API fails, the classic registry + `SystemParametersInfo` path is used.
  - *Videos:* one borderless native LibVLC renderer per monitor is re-parented into the shell's `WorkerW` layer (behind the desktop icons, above the static wallpaper). On Windows 11 raised-desktop systems it uses the layered `Progman`/`SHELLDLL_DefView` path used by current Lively Wallpaper versions. The Fit mode, Playback speed and desktop sound settings are applied, and the video loops forever. Windows session lock always mutes desktop wallpaper audio; unlocking restores the Settings volume.
  - *Video backend:* `LibVLCSharp` plus `VideoLAN.LibVLC.Windows` are included for Windows x64. LibVLC renders directly into the wallpaper HWND and ships its codec/plugin DLLs, so playback is not limited to the codecs registered for WPF `MediaElement`.
  - *Lock screen:* an optional Settings toggle applies the selected image to the Windows lock screen. Windows does not support video lock-screen backgrounds, so a video uses its generated static thumbnail.
  - The app manifest declares Per-Monitor-V2 DPI awareness so monitor rectangles are in physical pixels on mixed-DPI setups.
  - Success is reported only after Windows/WPF confirmed the result (for video: after the media actually opened). Errors are shown in the status line and in a dialog.
- **Controls.** `Button` now has a real `ControlTemplate` with `DarkButton`, `LightButton` and `NavButton` styles. Every state (normal, hover, pressed, disabled, selected) sets background and foreground together: light text on dark buttons, dark text on white buttons. Sliders, scrollbars and the search box caret are themed for the dark UI.

The explicit Windows target framework fix from Visual Studio is preserved:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
```

The import service also preserves the manually added `System`, `System.IO`, and `System.Linq` namespaces.

## Architecture

- **UI:** WPF on .NET 8 (`net8.0-windows10.0.19041.0`).
- **Models:** Observable wallpaper model shared by the library and playback surface.
- **Services:** Import classification, JSON library persistence, thumbnail generation/cache, and an `IWallpaperHost` boundary.
- **Assets:** The supplied m4xmudovv logo is copied into `src/m4xmudovv/Assets` and used as the visual identity.

## Build and test on Windows

Open `m4xmudovv.sln` in Visual Studio 2022, select `Debug | x64`, and run **Rebuild Solution**. Then verify:

1. A fresh profile opens with the empty-library state.
2. Importing images shows their actual thumbnails; importing videos shows a representative frame.
3. Hovering a video card plays a muted preview inside that card only; moving away restores the thumbnail.
4. Scrolling the library or typing in search stops any running hover preview.
5. Import, Set as Wallpaper, Preview, navigation, title-bar and transport buttons are readable in normal, hover, pressed and disabled states; the Wallpapers entry shows as selected.
6. Restarting restores the library and reuses cached thumbnails.

Video thumbnails depend on the codecs/handlers available to Windows (shell) and WPF (Media Foundation); actual desktop playback uses the bundled LibVLC backend.

### Desktop wallpaper checks (must be done on a real Windows desktop)

1. Select an image, choose each Fit mode, click **Set as Wallpaper**: the desktop changes and the status line confirms it.
2. With two monitors: the image appears on both with the chosen fit.
3. Select a video and click **Set as Wallpaper**: it plays behind the desktop icons on every monitor; icons stay clickable.
4. Change Fit mode / Playback speed while the video wallpaper runs: it updates.
5. Apply an image afterwards: the video stops and the image shows. Closing the app also stops the video wallpaper.
6. Select a file that was moved/deleted, or a video with an unsupported codec: a clear error appears.

### Known limitations

- Video wallpaper lives inside the running app: closing m4xmudovv removes it (the previous static wallpaper returns). It is not restored automatically on the next launch yet.
- There is no dedicated "stop video wallpaper" control (the UI is unchanged): apply an image or close the app.
- Desktop video wallpapers are muted, and the same video is played independently on each monitor (not frame-synchronised).
- Playback is not paused automatically for full-screen apps, battery saver or locked sessions.
- Video desktop playback uses the bundled LibVLC codec/plugin set. The thumbnail preview still depends on Windows Shell/Media Foundation handlers.
- Windows 11 24H2 and later changed the shell layout; both layouts are handled but the newer one needs verification on real machines.

LibVLCSharp and LibVLC are distributed under LGPL-2.1-or-later. The source project references the official NuGet packages; a Windows x64 publish output includes the corresponding native `libvlc` and plugin DLLs.

### Diagnostic log (Set as Wallpaper)

Every apply attempt writes a step-by-step log to `%LOCALAPPDATA%\m4xmudovv\logs\m4xmudovv.log` (rotated at about 1 MB to `m4xmudovv.log.old`). Failure dialogs show this path. The log contains: Windows build, elevation, COM apartment and DPI awareness, the monitor list and rectangles, the selected file (exists / size / attributes / readable), wallpaper policies, each COM / Win32 call with its HRESULT or Win32 error, what Windows reports back after the change, the shell window hierarchy (`Progman`, `WorkerW`, icon-view window), every attach attempt for video windows and the final window state.

What "success" means now:

- **Image:** reported only after Windows itself (`IDesktopWallpaper.GetWallpaper` / `GetPosition`) confirms the new wallpaper and fit mode on the monitors. If it does not, the `SystemParametersInfo` fallback is tried, then verified the same way.
- **Video:** reported only after the video opened, the window is attached to the desktop layer, visible, has the monitor's size, and the playback clock advanced. Whether the video is actually *visible behind the icons* can only be confirmed by looking at the desktop.

If Set as Wallpaper fails, send the log file; the `Shell:` and `attached via` lines identify the failing step.

## Settings

Open **Settings** in the sidebar (the **About** entry jumps to the About section). Changes apply immediately and are saved to
`%LOCALAPPDATA%\\m4xmudovv\\settings.json` (debounced, atomic writes).

| Section | Setting | What it does |
|---|---|---|
| General | Start with Windows | Per-user `HKCU\\...\\Run` entry (no admin rights). |
| General | Start minimized to tray | Launches hidden; the tray icon opens the window. |
| General | Minimize to tray when closing | The close button hides the window; tray menu **Exit** quits. A running video wallpaper keeps playing. |
| General | Remember window size and position | Restores bounds (ignored if the saved position is off-screen). |
| General | Restore previous state | Reopens with the last selected wallpaper and Fit mode. |
| Performance | Pause previews when minimized / inactive | Pauses the details preview and resumes it afterwards. |
| Performance | Video hover previews + delay | Enables hover previews; delay 0 ms (instant) to 2 s. |
| Performance | Reduce background activity when idle | After 60 s in the background / minimized / in the tray: releases the preview, collects garbage and trims the working set. |
| Appearance | Theme | System default / Dark / Light, switched live; System follows the Windows app mode. |
| Playback | Default volume, speed, mute, loop | Defaults for the details preview (loop also applies to hover previews). Changing volume/speed in the details panel is remembered too. |
| Library | Remember last category | Restores the All / Images / Videos filter. |
| Library | Thumbnail quality, Clear cache | Low / Balanced / High (own cache folder per level, rebuilt after a change); cache size is shown. |
| About | Version, architecture, updates, licenses | **Check for updates** is real: it queries `UpdateFeedUrl` from settings.json (JSON `{"version":"x.y.z","url":"https://..."}`); with no feed it says so instead of pretending. |

Only one copy of the app runs: starting a second copy shows the first one (useful when it is in the tray).

### Settings: not yet verified on a real Windows desktop

Tray behaviour, start-with-Windows, window-bounds restore, idle memory trimming, per-theme appearance and update checking
could not be run in the environment this was written in. Test each row above on Windows before relying on it.

## Navigation, Favorites and Playlists

Sidebar pages: **Wallpapers**, **Favorites**, **Playlists** (top) and **Settings**, **About** (bottom). Each has its own page and active state;
About is a separate page, not a part of Settings.

- **Card menu** (the three-dot button on every wallpaper card, and the ⋯ button in the details panel):
  *Add to / Remove from Favorites*, *Add to Playlist*, *Delete*.
  - *Add to Playlist* opens a dialog with one switch per playlist (on = the wallpaper is in it, off = removed) and a **New playlist** button. A wallpaper can never be added twice to the same playlist.
  - *Delete* asks for confirmation and removes the wallpaper from the library, Favorites and every playlist. **The original file on disk is never deleted.**
- **Favorites** page: the same cards (video hover previews included); the card menu removes an entry immediately. The heart button in the details panel toggles the selected wallpaper.
- **Playlists** page: create, open, rename and delete playlists (card menu or the buttons inside an opened playlist). Remove a wallpaper from a playlist with *Add to Playlist* and switch that playlist off.
- **Storage:** everything lives in the existing `library.json` (`{ "wallpapers": [...], "playlists": [...] }`, favorites are a flag on each wallpaper). The old plain-array format is still read. Saves are atomic (temp file + move).

### Start with Windows

The switch writes `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\m4xmudovv` (no administrator rights) and also honours Windows' own
enabled/disabled flag in `...\\Explorer\\StartupApproved\\Run` (what Task Manager > Startup apps changes). After every change the state is read back
from Windows: the switch only stays on if Windows really shows the entry as enabled. It follows Windows when the Settings page opens or the window is
activated, and at launch (an entry you disabled in Task Manager is never silently re-enabled; a stale path after moving the app is repaired).
Failures (access denied, blocked by the `DisableCurrentUserRun` policy, ...) show a message and the switch returns to its real state. Operations are logged
to the app log.

Manual test: switch on, then check Task Manager > Startup apps (m4xmudovv, Enabled) or `reg query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run`;
sign out/in (the app must start); switch off (entry gone); disable it in Task Manager while the app is open and re-open Settings (switch is off); restart the app
(state unchanged).

### Fit modes for Set as Wallpaper

| Mode | Image (Windows wallpaper position) | Video wallpaper |
|---|---|---|
| Fill | Fill | `UniformToFill` (cover, crop the overflow) |
| Fit | Fit | `Uniform` (letterbox) |
| Stretch | Stretch | `Fill` (ignore aspect ratio) |
| Center | Center (original size, centred) | `None`, DPI-compensated so the clip is shown at its real pixel size, centred |

Success is reported only after Windows confirms the result: images are read back from `IDesktopWallpaper` (path and position), videos must open,
stay attached to the desktop layer behind the icons and show an advancing playback clock. Otherwise an error with the reasons is shown and the details
are written to the diagnostic log.
