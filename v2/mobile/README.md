# Vault Movies for Android

The Vault server's film library as a phone app: sign in, browse rows of films,
and watch, with your place shared with the TV, the desktop app and `/watch`.
It is a client of the same API as everything else; the contract is
[`../docs/TV-APP-INTEGRATION.md`](../docs/TV-APP-INTEGRATION.md).

iPhones use the web app instead: `https://party.thejumpvault.com/watch`, then
Share → Add to Home Screen. A native iPhone build needs a paid Apple developer
account to stay installed.

## How it plays films

Android's own player (ExoPlayer, via `expo-video`) opens MKV and MP4 directly
and decodes H.264, HEVC, VP9 and AV1, so most films start straight away with no
server work. Films whose audio phones generally cannot decode (AC-3, E-AC-3,
DTS, TrueHD) go through the server's HLS copy, where the audio is converted;
a file that fails on a particular phone falls back to that copy by itself.
See `routeFor` in `src/library.ts`.

The player fills the screen itself rather than using the native fullscreen
mode, because on Android that mode pauses JavaScript and with it the resume
saves. Going to the home screen mid-film switches to picture-in-picture.

## Layout

| File | What it is |
|---|---|
| `App.tsx` | Sign-in gate, the screen stack, Android back handling |
| `src/session.ts` | Server address, tokens (refresh token in SecureStore), `api()` |
| `src/library.ts` | Catalogue, resume sync, playback route, formatting |
| `src/ui.tsx` | Buttons, film cards, stills, progress bars |
| `src/screens/` | Sign-in, home, film details, player |

`android/` is generated from `app.json` by `npx expo prebuild` and is not
committed. Icons come from `../scripts/make-vault-icons.js`.

## Building an APK

Needs JDK 17 and the Android SDK (platform 36).

```bash
npm install
npx expo prebuild --platform android
cd android
JAVA_HOME="C:/Program Files/Java/jdk-17" ./gradlew assembleRelease
```

**Build from a short path.** This folder's full path is long enough that the
C++ part of the build passes Windows' 260-character limit ("Filename longer
than 260 characters"). Map a drive letter to `v2` (not to `mobile` itself:
Expo's autolinking cannot find `package.json` at a drive root) and build from
there:

```powershell
subst W: "C:\Users\romok\Documents\Alex's Files\My Apps\Video Player\v2"
cd W:\mobile\android
$env:JAVA_HOME = "C:\Program Files\Java\jdk-17"; .\gradlew assembleRelease
subst W: /D
```

The APK is `android/app/build/outputs/apk/release/app-release.apk`. Copy it
to the phone and open it (allow "install unknown apps" for whatever opened
it), or with the phone plugged in: `adb install -r app-release.apk`.

**Signing:** the release build is signed with the debug keystore that
`prebuild` generates. That is fine for installing on your own phones. An
update installs over the old version only if it was signed with the same key,
so keep `android/app/debug.keystore` if you regenerate `android/`, or set up a
proper upload key before publishing to the Play Store.

**Plain HTTP** is allowed (`usesCleartextTraffic`) so the app can reach a
server on the home network by its LAN address. The default server is HTTPS.

## Developing

`npx expo run:android` builds a debug app, installs it on a running emulator
or plugged-in phone, and starts Metro for live reload. An emulator reaches a
server on this PC at `http://10.0.2.2:<port>`; enter that under "Use a
different server" on the sign-in screen.
