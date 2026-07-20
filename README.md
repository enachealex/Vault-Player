# Vault Player

A Windows video player for watching your own films — alone, cast to a TV, or
together with friends over the internet.

Built with .NET 10 / WPF and libVLC, so it plays what you actually have: MKV,
HEVC, DTS and AC-3 all decode natively with hardware acceleration and no
transcoding step.

## Features

- **Your folder, your films.** Point it at a folder and it lists the videos,
  with poster frames pulled from each file and cached.
- **Continue watching.** Every film remembers where you stopped, and the
  library shows how much is left.
- **Search, sort and filter.** Sort by name or length, or filter down to what
  you've actually watched — most watched, or most recent.
- **Chapters you define.** Mark points in a film and jump straight to them.
- **Cast to a TV.** Discovers DLNA renderers on your network (smart TVs,
  consoles, streaming sticks) and plays to them.
- **Watch Party.** Watch the same film, in sync, with friends.
- **Subtitles and audio tracks**, including sidecar `.srt` files.

## Watch Party

One person hosts. Their copy of the film is streamed to everyone else, and the
host controls playback — guests see "Watching *host*'s screen" and follow along,
with the group staying synced to the second. Guests can ask the host to pause,
and there's a chat channel alongside the video.

Joining takes a room code and a nickname. There are no accounts.

**Streaming services.** Titles on Prime Video, Netflix and similar are
DRM-protected and cannot be played or relayed by this app. You can still add
them to your library as shortcuts, and Watch Party can run a synchronised
countdown so everyone presses play together in the service's own app.

### Running it over the internet

On a local network nothing extra is needed — the app runs its own rendezvous.

To watch with people elsewhere, deploy `src/Rendezvous` (Dockerfile included)
somewhere with a public address, and set that address in the app. The server
only relays: it holds no accounts and stores no media. Connections from both
host and guests are outbound, so it works behind NAT without port forwarding.

Bandwidth note: the host uploads a full copy of the film to **each** guest
simultaneously. The lobby tells you what that adds up to before you start.

## Install

Download the latest `VideoPlayer-win-Setup.exe` from
[Releases](https://github.com/enachealex/Vault-Player/releases).

The app checks for updates when it starts and offers to install them.

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet build v2/VideoPlayer.slnx -c Release
```

To produce an installer:

```powershell
v2\scripts\pack.ps1 -Version 2.0.1
```

> The publish step deliberately stages through a temporary directory. The .NET
> SDK builds publish destinations with a single-quoted MSBuild transform, so any
> apostrophe in the checkout path collapses every destination into one and the
> build fails with `MSB3094`. Staging elsewhere avoids it.

## Layout

```
v2/src/App          the WPF application
v2/src/Protocol     shared Watch Party message types
v2/src/Rendezvous   the relay server (ASP.NET Core, Dockerfile included)
v2/scripts          packaging
```

An earlier version built with Expo / React Native and Electron lives in the
repository root (`src/`, `electron/`). It is superseded by `v2/`.
