# Watch Party rendezvous server

The little coordination + relay server behind Watch Party. It does three jobs:

1. **Control relay** — connects one host to its guests over WebSockets and passes
   along play/pause, sync beacons, chat, roster, and pause requests.
2. **Media relay** — guests pull the movie from *this server*
   (`GET /stream/{room}`); the server asks the host for each byte range over the
   control channel and the host pushes it back (`POST /upload/{reqId}`). So a
   guest needs only the **server address + a room code** — the host never has to
   be directly reachable, which is what makes parties work across the internet
   through home routers.
3. **Library** — films kept on *this machine's* disk, listed to signed-in
   accounts (`GET /api/v1/catalog`) and streamed on demand
   (`GET /media/{key}`). The other two jobs move somebody else's bytes; here the
   server is the origin, so nobody has to be sat at their PC with the file open
   for someone else to watch it.

## The library

Point `VAULT_MEDIA` at a folder of films (default `/media`) and the server
indexes it on startup, walking subfolders. It never writes to that folder — the
mount is read-only in `docker-compose.yml` — and the index is disposable: delete
the rows and a rescan rebuilds them.

With `ffprobe` and `ffmpeg` on the path (the Docker image installs both) each
film also gets a runtime, its codecs, and a poster frame. Without them files
still list and stream, they just show a name and a size.

Playback URLs carry a **signed token** rather than the account's access token,
because a media player follows a URL and cannot be made to send an
`Authorization` header. Each token is bound to one film and one account and
lasts 12 hours, so a link that escapes grants that one film until the end of the
day and nothing else.

Adding or removing files takes effect on the next `POST /api/v1/catalog/rescan`
(or a restart).

## Running it

**Local (default).** You don't run anything by hand — when someone hosts a party,
the app starts this server automatically on `127.0.0.1:5555`. Friends on the same
network join with the host's LAN address and the room code.

**Deployed (internet parties).** Put it on any small always-on box so friends
anywhere join with just a code:

```bash
# from v2/
docker build -f src/Rendezvous/Dockerfile -t videoplayer-rendezvous .
docker run -d -p 5555:5555 --restart unless-stopped videoplayer-rendezvous
```

or without Docker:

```bash
PORT=5555 dotnet run -c Release --project src/Rendezvous
```

Then in the app set the rendezvous server (Settings → `RendezvousServer`) to the
box's hostname/IP on every machine. Hosts stream *out* to it; guests pull *from*
it — both only make outbound connections, so no port-forwarding on anyone's home
router.

## Sizing

RAM/CPU are tiny (it just shuttles bytes). The real cost is **bandwidth**: every
guest streaming pulls the movie's bitrate through the server, so a 3-friend movie
night at ~6 Mbps moves ~18 Mbps of egress. Pick a host with enough transfer
allowance, or keep parties on the LAN where the relay is effectively free.

## Endpoints

| Path | Who | Purpose |
|---|---|---|
| `GET /health` | — | liveness check (`ok`) |
| `WS /ws?role=host&name=` | host | control channel; returns a room code |
| `WS /ws?role=guest&room=&name=` | guest | control channel for a room |
| `GET \| HEAD /stream/{room}` | guest player | pulls the movie (Range-aware) |
| `POST /upload/{reqId}` | host | pushes a requested byte range |
| `GET /api/v1/catalog` | signed-in app | the server's own film library |
| `GET /api/v1/catalog/{key}/art` | signed-in app | poster frame |
| `POST /api/v1/catalog/rescan` | signed-in app | re-index the media disk |
| `GET \| HEAD /media/{key}?t=` | media player | streams a library film (Range-aware) |
