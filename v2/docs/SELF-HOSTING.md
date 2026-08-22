# Where a Vault library can live

Three arrangements, one setting. `Settings.LibraryMode` records which, and all
three come down to what `Settings.AuthServer` points at — every service in the
app derives its base URL from `AccountService.BaseUrl`, which reads that value
per request.

| `LibraryMode` | `AuthServer` | What runs |
|---|---|---|
| `this-pc` | `http://127.0.0.1:5555` | `LibraryServer` starts the shipped `server\VideoPlayer.Rendezvous.exe` |
| `own-server` | whatever was typed | nothing locally; the app is a client |
| `none` | `null` | nothing; local files only, and the public relay for parties |

The user-facing version of this document is served by the server itself at
`GET /setup` ([`SetupPage.cs`](../src/Rendezvous/SetupPage.cs)) and linked from
the app's setup screen. Change both together.

## The app hosting it

[`LibraryServer`](../src/App/Services/LibraryServer.cs) starts the same server
binary that runs in production, with this environment:

| Variable | Value |
|---|---|
| `PORT` | `5555` — `LibraryServer.Port` is `PartySession.RendezvousPort`, and a guest given a bare hostname is sent to that port |
| `VAULT_BIND` | `0.0.0.0` or `127.0.0.1`, from `HostShareOnNetwork` |
| `VAULT_MEDIA` | the chosen films folder |
| `VAULT_UPLOADS` | the same folder, so uploads land where the library reads |
| `VAULT_DB` | `%LOCALAPPDATA%\VideoPlayerV2\server\vault.db` |
| `VAULT_HLS_CACHE` | `…\server\hls` — bulky, pure cache, kept off the films disk |
| `VAULT_JWT_SECRET` | 48 random bytes generated once and kept in settings |
| `VAULT_FFMPEG` / `VAULT_FFPROBE` | the copies beside the app, if present |

`VAULT_JWT_SECRET` is not optional. Without it the server falls back to a key
committed to this repository, and anyone who has read it can mint a valid login.

### One spawn, not two

Hosting a party and hosting a library are the same executable on the same port.
`PartySession.EnsureLocalRendezvousAsync` therefore delegates to
`LibraryServer.StartForPartyAsync` rather than starting the binary itself. It
used to start it directly with no environment at all, and whichever path won the
race decided what the "library" was — a party could quietly replace it with a
server pointed at no films and signing with the development key, while the
library screen, seeing a healthy port, reported success.

## ffmpeg and ffprobe are both required

The deployed server gets these from its container image. On a PC they are on
nobody's PATH, so [`MediaTools`](../src/Rendezvous/Vault/MediaTools.cs) resolves
them beside the executable first, then from `VAULT_FFMPEG` / `VAULT_FFPROBE`,
then PATH.

**ffprobe is not the softer half.** It is what fills in each film's
`VideoCodec`, and `HlsRemuxer.CanRemux` refuses a film whose codec is unknown —
so without it *every* film answers 409 "can't be repackaged for a browser" on a
phone or a television, however ordinary it is. The library looks perfect on the
PC and nothing works on the TV.

Both binaries live in `v2/tools/` and are copied into `server/` by
`scripts/pack.ps1`, which throws if either is absent.

## What we hold

Nothing of the user's. Under all three arrangements the films sit on hardware
they own and stream from it. There is no code path that uploads a film to
`party.thejumpvault.com`; that server relays Watch Party messages and serves the
television app. A user of somebody's shared library has an account on *their*
server, not on ours.

Keep it that way. If a feature would need films or a catalogue on our side, it
needs a different design.
