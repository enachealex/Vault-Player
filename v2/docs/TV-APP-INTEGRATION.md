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
    "addedAt":    1787021644056
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
and will play anywhere. The rest (HEVC/DTS/TrueHD in MKV) plays only if the
TV's decoder handles it natively — **the server does not transcode**. Check
`videoCodec`/`audioCodec` against the device's capabilities and be honest in
the UI about a film the device can't decode.

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
