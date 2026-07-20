# Video Player

A cross-platform video player built with **Expo / React Native**. One codebase runs as:

- a **desktop app** (Windows / macOS / Linux) via an Electron shell,
- a **web app** in any Chromium browser,
- a **mobile app** on iOS / Android.

## Features

- **Open a folder, see only its videos** — on desktop/web the native folder picker lists just the playable video files inside (nothing is uploaded; everything stays on your device). On mobile you add videos through the system picker.
- **Plays MKV / AVI and surround audio instantly (desktop)** — the desktop app bundles ffmpeg. Files Chromium can't play natively (MKVs with AC-3/DTS audio, AVI/WMV containers) play as a **live stream**: ffmpeg starts at the requested position, copies the video untouched and re-encodes only the audio to AAC on the fly, so playback begins within seconds — no upfront conversion of the whole file. Seeking restarts the stream at the target time.
- **Thumbnail previews** — each video shows a poster frame grabbed from the file; posters are cached (IndexedDB) so they appear instantly next time, including for recents.
- **Searchable & sortable library** — filter the folder by name and sort by folder order or name (A–Z / Z–A).
- **Subtitles** — sidecar `.srt` / `.vtt` files are auto-detected and matched by name (`movie.srt`, `movie.en.srt`, …). Toggle/switch tracks with the **CC** button; cues render as an overlay and are parsed in-app so they work on web and native.
- **Recent / Continue watching** — every video remembers where you left off and resumes from there.
- **High-quality playback** — video plays at native resolution using the platform's hardware-accelerated player (`expo-video` → HTML5 `<video>` on desktop/web). Audio plays uncompressed at full volume; videos autoplay with sound on desktop.
- **Four end-of-video modes**, switchable any time:
  - **Autoplay** — play the next video automatically
  - **Loop list** — repeat the whole folder
  - **Loop one** — repeat the current video
  - **Play once** — stop after the current video
- **Intuitive controls** — scrubbable timeline, ±10s skip, previous/next, volume, playback speed (0.5×–2×), subtitles, fullscreen, plus keyboard shortcuts on desktop.

### Keyboard shortcuts (desktop / web)

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| `Space` / `K` | Play / pause | `M` | Mute |
| `←` / `→` | Seek ∓5s | `↑` / `↓` | Volume ±10% |
| `J` / `L` | Seek ∓10s | `F` | Fullscreen |
| `N` / `P` | Next / previous | `Esc` | Back to library |

## Getting started

```bash
npm install
```

### Run in the browser (fastest dev loop)

```bash
npm run web
```

### Run as the desktop app

Development (hot reload — start the web server first, then Electron points at it):

```bash
npm run web          # terminal 1: Expo web dev server on :8081
npm run electron:dev # terminal 2: Electron window loading the dev server
```

Production preview (builds the web bundle, then runs Electron against it):

```bash
npm run electron:preview
```

### Run on a phone

```bash
npm run android   # or: npm run ios
```

## Building installers

```bash
npm run dist:win     # Windows  -> release/*.exe (NSIS installer)
npm run dist:mac     # macOS    -> release/*.dmg
npm run dist:linux   # Linux    -> release/*.AppImage
```

Output lands in `release/`.

## How it fits together

```
App.tsx                     Root: navigation + recents/settings state
src/screens/
  LibraryScreen.tsx         Recents, folder contents, search + sort, thumbnails
  PlayerScreen.tsx          expo-video player + custom controls + mode + subtitles
src/services/
  videoFolder.web.ts        Folder picking + subtitle detection; routes through the
                            desktop bridge in Electron, FS Access API in browsers
  videoFolder.native.ts     Video picking via expo-document-picker (mobile)
  thumbnails.web.ts         Canvas frame-grab posters, cached in IndexedDB
  thumbnails.native.ts      Posters via expo-video-thumbnails
src/subtitles.ts            SRT / VTT parser (in-app cue rendering)
src/storage.ts              Recents + settings (AsyncStorage / localStorage)
scripts/make-icons.js       Generates the app icon set (run: npm run icons)
electron/main.js            Desktop shell; local server (static build + Range-
                            capable media streaming), native folder dialog IPC
electron/media.js           ffprobe/ffmpeg pipeline: direct-play detection and
                            audio conversion for MKV/AC-3/DTS, cached per file
electron/preload.js         contextBridge exposing the desktop bridge
```

> **Why localhost instead of `file://` on desktop?** Chromium disables the
> File System Access API on `file://` origins, so the Electron shell serves the
> exported build from a local HTTP server. Folder-picking then works identically
> to the browser.
