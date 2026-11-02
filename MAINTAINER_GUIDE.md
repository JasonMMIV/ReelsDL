# ReelsDL - Maintainer & Architecture Guide

> A technical guide for developers and maintainers of **ReelsDL** (Universal Short Video Downloader).

---

## 1. Core Architecture & Philosophy

### 1.1 Why Headless CDP Instead of Private API Reverse-Engineering?

Social platforms (Meta, X, TikTok, etc.) frequently change query hashes, CSRF rules, authentication handshakes, and bot-detection heuristics. Pure HTTP scrapers break constantly.

**ReelsDL operates on one principle:**

> *"If a modern browser can render and play the video, the decrypted media stream is already on the machine."*

1. Launch a hidden **Chrome** or **Edge** instance with a dedicated profile.
2. Connect to it over the **Chrome DevTools Protocol (CDP)** WebSocket.
3. Let the platform's own JavaScript resolve signatures, tokens, and CDN routing.
4. Walk the SSR hydration JSON / Relay state to find progressive MP4, WebM, or image URLs.

### 1.2 Two Processes, One Job

```
+---------------------------------------------------------------+
|                       ReelsDL.exe                             |
|  C# Fluent WinForms UI (MainForm.cs, Fluent.cs, Launcher.cs)   |
|  - URL input, account menu, activity card, cancel, logs        |
|  - Single instance: Local\ReelsDL.SingleInstance               |
+------------------------------+--------------------------------+
                               | Process.Start (CreateNoWindow)
                               | -Cli / -Login / -CookieState
                               v
+---------------------------------------------------------------+
|                       ReelsDL.ps1                             |
|  Console-only extraction engine. No GUI, no WinForms.          |
+---------------+-----------------------------+-----------------+
                |                             |
      CDP WebSocket (loopback)      winsqlite3 (read-only)
                v                             v
+-------------------------------+  +--------------------------+
|    Chrome / Edge Chromium     |  |  Dedicated Profile       |
|  - Stealth: navigator.webdriver |  |  %LOCALAPPDATA%\ReelsDL  |
|  - UA derived from browser    |  |  ...\Network\Cookies     |
+---------------+---------------+  +--------------------------+
                |
                v
+---------------------------------------------------------------+
|            Universal Multi-Platform Extractor                 |
|  Meta: video_versions, carousel_media                         |
|  X: video_info.variants + every photo                         |
|  TikTok: bitrateInfo, video fields, imagePost, SIGI_STATE     |
|  Facebook: Relay video URLs, OpenGraph                        |
|  Fallbacks: OpenGraph, DOM <video>, loaded MP4 resources      |
+---------------------------------------------------------------+
```

The UI never launches or controls Chrome. It only starts the engine and reads its
structured events. The engine never creates a window.

### 1.3 Engine Modes

| Mode | Purpose |
|---|---|
| `-Cli -Url <url> [-Out <dir>] [-Log <file>]` | Download a post |
| `-Login [-Platform <name>] [-Url <url>] [-Log <file>]` | Open an interactive login window and wait for it to close |
| `-CookieState` | Print `Platform=0/1` lines for the UI account menu |
| `-SelfTest` | Offline checks (no Chrome, no network) |
| `-Help` | Print usage and exit 0 |

Running `ReelsDL.ps1` with no mode prints usage and exits 1.

---

## 2. Project Directory Structure

```
C:\Users\<user>\Desktop\ReelsDL\
├── .gitignore
├── README.md
├── MAINTAINER_GUIDE.md
├── build.ps1                     # Compiles, self-tests, and packages
├── src\
│   ├── app.ico                   # Multi-resolution application icon
│   ├── app.manifest              # asInvoker + system DPI + Win10/11 + comctl32 v6
│   ├── Launcher.cs               # WinExe entry point, single instance, CLI console attach
│   ├── MainForm.cs               # Fluent desktop UI
│   ├── ReelsDL.ps1               # Console-only CDP extraction engine
│   ├── shared\
│   │   ├── AssemblyInfo.cs       # Assembly version/product metadata
│   │   └── Fluent.cs             # Fluent controls, theme, KnownFolders, helpers
│   └── installer\
│       ├── Setup.cs              # Fluent per-user installer
│       └── Uninstall.cs          # Fluent uninstaller
├── tests\
│   └── Test-CookieProbe.ps1      # Read-only cookie DB diagnostic
├── build\                        # Staging directory (generated)
└── dist\
    └── ReelsDL-Setup.exe         # Standalone installer (generated)
```

---

## 3. Core Modules & Implementation Details

### 3.1 Desktop UI (C#)

`MainForm.cs` uses the shared Fluent toolkit in `shared\Fluent.cs`:

* Owner-drawn `FluentButton`, `FluentTextBox`, `FluentCheckBox`, `Card`,
  `ProgressLine`, and `InfoBar`.
* Light/dark palette from `HKCU\...\Themes\Personalize\AppsUseLightTheme`.
  `WM_SETTINGCHANGE` triggers a live theme refresh.
* DWM rounded corners, caption colors, and scrollbar theming.
* `Local\ReelsDL.SingleInstance` prevents a second window. The second launch
  shows a message instead of activating the existing window (known limitation).
* The activity card exposes **Cancel** while a job is running. Cancel kills the
  engine process tree; Chrome is a child of the engine, so it is cleaned up too.
* Every CLI argument is quoted with `ProcessUtil.QuoteArg` (Windows
  `CommandLineToArgvW` rules).

### 3.2 Extraction Engine (PowerShell)

`ReelsDL.ps1` is a **console-only** script running under Windows PowerShell 5.1.
It has no WinForms code and no `Add-Type -AssemblyName System.Windows.Forms`.

* `$ErrorActionPreference = 'Stop'`, `$ProgressPreference = 'SilentlyContinue'`.
* `Write-Log` writes one sanitized line to the console and, when `-Log` is
  supplied, to a UTF-8 (no BOM) log file.
* `Write-Evt` writes structured `##EVT|...` lines to that log file only.
* `Save-RemoteFile`, `Get-MediaExtension`, `Get-UniqueFilePath`,
  `Get-SafeBaseName`, and `Get-DownloadsFolder` are pure helpers.
* `-SelfTest` exercises the helpers, URL/host matching, filename rules, and the
  cookie probe compiler. `build.ps1` runs it before compiling.

### 3.3 Stealth & Anti-Bot Detection

* **Engine flag:** `--disable-blink-features=AutomationControlled`
* **Pre-document script:**
  ```powershell
  Invoke-Cdp -Ws $ws -Method 'Page.addScriptToEvaluateOnNewDocument' -Params @{
      source = 'Object.defineProperty(navigator, "webdriver", { get: () => undefined }); window.chrome = { runtime: {} };'
  }
  ```
* **User-Agent:** derived from the installed Chrome/Edge `ProductVersion` by
  `Get-BrowserUserAgent`, never a hard-coded version.

### 3.4 High-DPI Layout (C#)

* `Theme.Scale` is read once from `Graphics.FromHwnd(IntPtr.Zero).DpiX / 96`.
* Every coordinate and size goes through `Theme.S()`.
* `Theme.ClampScale(860, 640)` shrinks the design scale when the primary work
  area is smaller than the 860x640 design (e.g. 1366x768 at 125%).
* The manifest declares `dpiAware=true` and `dpiAwareness=system`, matching the
  runtime `SetProcessDPIAware()` call. The app is system-DPI aware, not
  per-monitor v2: dragging it to a monitor with a different scale factor keeps
  the original layout.

### 3.5 Isolated Profile & Cookie Persistence

* Profile: `%LOCALAPPDATA%\ReelsDL\profile`
* Chromium stores the session database in `<profile>\Network\Cookies`; values
  are encrypted with Windows DPAPI, but `host_key`, `name`, and expiry metadata
  are plain text.
* **`CookieProbe`** (C# embedded at the top of `ReelsDL.ps1`) P/Invokes the
  in-box `winsqlite3.dll` and runs a read-only query:
  ```sql
  SELECT host_key, name, expires_utc, has_expires FROM cookies
  ```
  It falls back to narrower column lists for older schemas.
* `Get-CookieState` pairs **host_key and name on the same record** and checks
  `expires_utc` / `has_expires`. This prevents a TikTok `sessionid` plus an
  Instagram `csrftoken` from being reported as an Instagram login.
* If Chrome holds the database open, the engine copies `Cookies`, `Cookies-wal`,
  and `Cookies-shm` to `%TEMP%` with `FileShare.ReadWrite` and reads the copy.
* If `winsqlite3.dll` is unavailable or the database cannot be parsed, the
  engine falls back to the older token + domain string scan. That fallback is
  file-level, not record-level, and is only a last resort.
* The C# UI does **not** duplicate cookie logic. `GetLoginStates()` starts the
  engine with `-CookieState` (cached for 30 seconds) and parses the result.

---

## 4. Multi-Platform Media Extractor (`ExtractJs`)

| Platform | Primary Target | Fallback Strategy | Key Fields |
|---|---|---|---|
| **Instagram** | Embedded Relay JSON (`video_versions`, depth <= 32) | OpenGraph / DOM `<video>` | `video_versions`, `carousel_media`, largest `image_versions2` candidate |
| **Threads** | SSR JSON / shortcode DOM | OpenGraph / DOM `<video>` | `video_versions`, `carousel_media`; nodes scoped to the URL's post id |
| **X (Twitter)** | `video_info.variants` / `extended_entities.media` | `twitter:player:stream` | Highest-bitrate MP4; every photo in multi-photo posts |
| **TikTok** | `bitrateInfo` (highest bitrate) → `downloadAddr` / `playAddr` | DOM `<video src>` / loaded MP4 resources | `downloadAddr`, `playAddr`, `PlayAddr.UrlList`, `imagePost.images` |
| **Facebook** | Relay JSON / Video Model | DOM / OpenGraph | `playable_url_quality_hd`, `browser_native_hd_url`, `hd_src` |

The scanner ignores hydration nodes whose `code`, `shortcode`, `rest_id`, or
`aweme_id` does not match the post in the address bar. This prevents
recommended/related posts embedded in the same page from being downloaded.

**Facebook is exempt from the id filter.** Facebook video object ids are not
guaranteed to equal the reel/post id; applying the filter there could hide the
actual video. This is intentional and documented in the extractor comment.

### 4.1 Download & Lifecycle Safeguards

* **Atomic-ish writes:** downloads stream to a `.part` file and are renamed only
  after the byte count matches `Content-Length` (when the server sends one).
* **Size cap:** 8 GB per file, checked from `Content-Length` and while streaming.
* **Retry/backoff:** three attempts with 1s/2s backoff. HTTP 429 honors
  `Retry-After` (capped at 30s).
* **Extension mapping:** `Get-MediaExtension` maps known `Content-Type` values
  to `.mp4`, `.webm`, `.mov`, `.mkv`, `.avi`, `.mpg`, `.ogv`, `.3gp`, `.jpg`,
  `.png`, `.webp`, `.gif`, `.bmp`, `.avif`, `.heic`, `.tiff`. Unknown types keep
  the media type's default extension.
* **Filenames:** `Get-SafeBaseName` strips `\/:*?"<>|`, trailing dots/spaces,
  reserved device names (`CON`, `NUL`, `COM1`...), and caps the base at 120
  characters. `Get-UniqueFilePath` appends ` (1)`, ` (2)`, ... and never
  overwrites an existing file.
* **Extraction retries:** six attempts over ~9 seconds with a scroll nudge,
  because hydration JSON and lazy players may arrive late.
* **Profile cleanup:** `Stop-ProfileBrowsers` kills every Chrome/Edge process
  using the dedicated profile. Only an **interactive** login window (no
  `--headless`) blocks a new job; leftover headless processes from a crash are
  cleaned up instead of deadlocking the app.
* **Login detection:** after the login window closes, the engine retries the
  cookie probe for up to 15 seconds because Chrome flushes the database
  asynchronously.
* **UI diagnostics:** `MainForm` redirects the engine's stderr into the details
  log and reads the log with `FileShare.ReadWrite`, buffering partial lines.
* **Downloads folder:** respects the `Downloads` known folder
  (`{374DE290-123F-4565-9164-39C4925E467B}`), so OneDrive/group-policy
  redirection works. The legacy profile import is atomic (copy to
  `profile.migrating`, then rename).

---

## 5. Building & Packaging

ReelsDL requires no external SDK. It compiles with the in-box .NET Framework
`csc.exe` (C# 5) on Windows 10/11.

```powershell
# Build the app, uninstaller, and dist\ReelsDL-Setup.exe
powershell -ExecutionPolicy Bypass -File .\build.ps1

# Build and launch the development version
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run

# Clean build and dist
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Clean
```

`build.ps1`:

1. `Assert-Ps1Engine` ensures `src\ReelsDL.ps1` has a UTF-8 BOM and parses with
   zero errors.
2. Runs `ReelsDL.ps1 -SelfTest`; the build aborts on any failure.
3. Compiles `ReelsDL.exe`, `Uninstall.exe`, and `ReelsDL-Setup.exe` with
   `/optimize+`, `/codepage:65001`, `app.manifest`, and `shared\AssemblyInfo.cs`.
4. Embeds the app, engine, icon, and uninstaller into
   `dist\ReelsDL-Setup.exe`.

> **Important:** `src\ReelsDL.ps1` must stay UTF-8 **with BOM**. Windows
> PowerShell 5.1 otherwise reads it with the active ANSI code page (for example
> CP950) and reports parse errors. `Assert-Ps1Engine` fixes a missing BOM and
> fails the build on syntax errors. All C# sources are compiled with
> `/codepage:65001`.

Useful checks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\ReelsDL.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\ReelsDL.ps1 -CookieState
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-CookieProbe.ps1
```

`tests\Test-CookieProbe.ps1` reads the local profile database and prints cookie
**hosts and names only** (never values). It is a diagnostic, not a network test.

---

## 6. IPC: Log Event Protocol

The engine appends one line per event to the file passed via `-Log`:

```
##EVT|<type>|<field>|<field>...
```

| Type | Fields | Meaning |
|---|---|---|
| `stage` | `phase`, `message` | High-level status text |
| `meta` | `platform`, `author`, `code`, `count` | Post identity and item count |
| `item` | `index`, `count`, `name`, `type` | Starting one media item |
| `prog` | `index`, `count`, `readBytes`, `totalBytes` | Download progress (`total` may be `-1`) |
| `saved` | `index`, `count`, `name`, `sizeMb`, `path`, `type` | File finished |
| `done` | `count`, `outDir` | All files finished |

Fields are stringified and `|`, CR, and LF are replaced with spaces. Human log
lines use `[HH:mm:ss] message`. The UI treats every line starting with `##EVT|`
as an event and displays all other lines in the details pane.

The UI polls the file every 250 ms, reads with
`FileShare.ReadWrite | FileShare.Delete`, and only processes complete lines,
so a partially written event cannot be dropped or rendered as garbage.

---

## 7. Exit Codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error (invalid URL, extraction failure, all downloads failed) |
| `2` | Login window closed without a saved session / login cancelled |
| `3` | The post requires login (`-Cli` only) |

`Launcher.exe` forwards the engine's exit code. The UI uses `3` to show the
**Login Required** info bar and auto-retry after a successful login.

---

## 8. Installer & Uninstaller

* Per-user install (no elevation): default
  `%LOCALAPPDATA%\Programs\ReelsDL`.
* Embedded payload: `ReelsDL.exe`, `ReelsDL.ps1`, `app.ico`, `Uninstall.exe`.
* `InstallCore` extracts to a temporary staging folder, copies each payload to
  a `<name>.new` side file, then swaps it in with `File.Replace` (atomic on the
  same volume). A missing payload or full disk during extraction/copy cannot
  corrupt the live installation. The engine, icon, and uninstaller are replaced
  before `ReelsDL.exe`, so an interrupted swap keeps an old launcher compatible
  with the new engine rather than the reverse.
* Only processes whose executable lives under the target folder are terminated
  (`ProcessUtil.KillInstalledApp`). A portable copy elsewhere keeps running.
* Registry entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ReelsDL`
  with `DisplayVersion`, `InstallLocation`, `UninstallString`, and
  `QuietUninstallString`.
* `/silent`, `/quiet`, `/s`, and `/dir=<path>` are supported. In silent mode,
  warnings are appended to `%TEMP%\ReelsDL-Setup.log` and the exit code is `2`.
* The uninstaller refuses drive roots and folders that do not contain both
  `ReelsDL.exe` and `Uninstall.exe`, then schedules a detached cleanup script.
  The script starts with `cd /d "%TEMP%"`; without it, `rd` cannot remove the
  folder when the uninstaller was launched from a shortcut whose working
  directory is the install folder.
* Uninstall runs on a background thread so the window stays responsive, and the
  destructive work is shown with a wait cursor and a disabled close box.

---

## 9. Known Limitations

* Binaries are unsigned. SmartScreen/AV may warn, especially because the app
  launches PowerShell with `-ExecutionPolicy Bypass`.
* System-DPI aware only; mixed-DPI multi-monitor moves do not rescale controls.
* The extractor depends on each platform's current hydration schema. There are
  no live network tests in this repository; `-SelfTest` covers local logic only.
* `winsqlite3.dll` ships with Windows 10/11. On systems where it is missing,
  the engine falls back to a file-level cookie heuristic, which can still
  produce a false "logged in" label in rare cross-platform cookie mixes.
  `-SelfTest` only verifies that the probe *compiles*; it does not require the
  DLL, so a missing DLL degrades at runtime instead of failing the build.
* The cookie probe reads names, hosts, and expiry only; values remain encrypted
  and are never decrypted or logged.
