# TV app integration

Everything a TV (or any device) client needs from the Vault server, in the
order a session actually happens. Base URL: `https://party.thejumpvault.com`.
All bodies are JSON unless said otherwise; errors come back as
`{ "error": "human-readable message" }` — show them verbatim, they are written
for people.

## 1. Sign in (device-link)

Typing a password with a remote is misery, so the TV never sees one:

1. Show a QR of `https://party.thejumpvault.com/link` (plus the URL as text for
   anyone who'd rather type it).
2. The person scans it, signs in on their phone, and the page shows a
   **6-character code** (valid 10 minutes, single use).
3. They type that code into the TV. The TV claims it:

```
POST /api/v1/link/claim
{ "code": "662VUQ" }

200 →
{
  "accessToken":  "…",        // JWT, ~1 hour
  "refreshToken": "…",        // ~60 days, single-use (rotates)
  "expiresAt":    1787200000000,   // unix ms, access token expiry
  "displayName":  "alex",
  "username":     "alex",
  "status":       "approved", // "pending" = signed in but library gated
  "isOwner":      true
}
400 → bad/expired code (tell them to scan again)
429 → too many wrong guesses server-wide; wait a minute
```

Keep `refreshToken` in device storage; `accessToken` in memory.

## 2. Staying signed in

```
POST /api/v1/refresh
{ "refreshToken": "…" }
```

Same response shape. **The refresh token is single-use** — every refresh hands
back a new one; store it immediately. A 401 means the session is dead (revoked,
expired, or the account was blocked): return to the QR screen.

## 3. The library

```
GET /api/v1/catalog
Authorization: Bearer <accessToken>

200 →
{
  "items": [ {
    "itemKey":    "1b106a4ee0e31556",
    "name":       "Finding Nemo",
    "sizeBytes":  4948889600,
    "durationMs": 6000000,
    "contentType":"video/x-matroska",
    "filmKey":    "Finding Nemo.mkv|4948889600",
    "webPlayable": false,          // can a browser <video> play it raw?
    "videoCodec": "hevc",
    "audioCodec": "aac",
    "streamUrl":  "/media/1b106a…?t=…",   // RELATIVE — prefix the base URL
    "artUrl":     "/api/v1/catalog/1b106a…/art",  // relative too
    "addedAt":    1787021644056,
    "hlsAvailable":  true,         // can the server repackage it? (see §4b)
    "hlsVideoCodec": "hevc",       // what the repackaged stream will contain
    "hlsUrl":     "/api/v1/catalog/1b106a…/hls"   // relative too
  } ],
  "scanning":  false,   // true = server still indexing; show "still loading"
  "lastScanAt": 1787021644410,
  "mediaRootPresent": true
}
403 → account still pending the owner's approval; say so
```

- `streamUrl` carries a **signed token bound to this account and film, valid
  12 hours**. Don't cache catalog responses across app launches — refetch to
  get fresh URLs.
- Posters (`artUrl`) need the `Authorization: Bearer` header. Stream URLs need
  nothing extra — the token in the URL is the auth, which is what lets a bare
  video player play them.

## 4. Playback

`GET <base><streamUrl>` — plain HTTP(S) video with full range-request support
(seek = byte range, the server honours `Range:` and answers 206). Hand the
absolute URL to the platform's player.

Codec reality: `webPlayable: true` means h264 + aac in an MP4-family container
and will play anywhere. Use this path when you can — it is a plain file with no
preparation and no second copy on disk.

## 4b. Playback of everything else (HLS)

Most of a real library is HEVC or DTS inside an MKV, which no browser opens.
For those, the server repackages on demand: the video is **copied**, never
re-encoded, and only the audio is converted when it has to be. The picture is
the original, and a two-hour film is ready to start within seconds.

```
GET <base><hlsUrl>          Authorization: Bearer <access>
200 {"state":"running","percent":12,"ready":true,
     "error":null,"playlistUrl":"/hls/1b106a…/index.m3u8?t=…"}
409 → this film's video cannot be repackaged at all; send them to the app
```

`state` is `idle` → `starting` → `running` → `ready`; `failed` if it broke.
**Poll every ~2s until `ready` is true**, then hand `playlistUrl` (prefixed with
the base URL) to the platform's player. `ready` turns true well before
`percent` reaches 100 — the front of the film is enough to start, and the rest
is written faster than anyone watches it. Only the first viewer ever waits; the
result is cached and shared by every device afterwards.

The playlist and its segments carry their own signed tokens, so a bare player
needs no headers. Segments are fMP4; HEVC is tagged `hvc1` so Apple platforms
accept it.

Decide playability like this, in order:

1. `webPlayable` → play `streamUrl` directly.
2. `hlsAvailable` **and** the device decodes `hlsVideoCodec` **and** it plays
   HLS → prepare and play the playlist.
3. Otherwise say plainly that this film needs the desktop app.

Ask the device with `video.canPlayType(…)`, **not**
`MediaSource.isTypeSupported(…)` — the latter answers a Media Source Extensions
question and returns false for types the element plays perfectly well, which
would strand films for no reason.

One trap worth knowing: while a film is still being prepared its playlist is an
EVENT playlist, and players report `duration` as `Infinity`. Anything gated on
a finite duration — seeking, a scrub bar, saving a resume point — will silently
switch itself off. Use `durationMs` from the catalogue instead. Once the remux
finishes the playlist is served as VOD and `duration` becomes real.

## 5. Resume across devices

```
GET /api/v1/library                  (Bearer)  → this account's watch state
PUT /api/v1/library { "items": [ … ] }  (Bearer)  → push local changes, get merged truth
```

Item shape: `{ filmKey, name, resumeMs, watchCount, lastWatchedAt,
chaptersJson, updatedAt }` — keyed by `filmKey` (matches the catalog), merged
last-writer-wins on `updatedAt` (unix ms). Push on pause/stop; the response is
the authoritative merged set, apply it.

## 6. Watch parties (optional for v1 of the TV app)

Room codes are **6 plain characters** (legacy `MOVIE-XXXX` still accepted).

- `GET /room/{code}` — no auth: what the room is watching, whether a browser
  can play it, and the host's latest clock beacon (`positionMs`, `playing`,
  `atUnixMs`, plus `serverNowMs` to cancel clock skew).
- `GET /stream/{code}` — the party's film, range-aware, no token (the code is
  the secret).
- Full membership (roster, chat, kicks) is the WebSocket at
  `/ws?role=guest&room={code}&name={name}[&auth={accessToken}]` — `auth` is
  required for private rooms and pins your roster name to the account.

## Notes

- Everything is HTTPS on 443 via the public hostname — works from any network,
  no VPN. On the home LAN the same hostname resolves and hairpins through the
  router.
- Rate limits and CAPTCHA exist only on **registration**; the TV app never
  registers accounts — people bring an account they made in the desktop app
  (or via an invite QR).
