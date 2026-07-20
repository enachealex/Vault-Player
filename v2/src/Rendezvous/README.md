# Watch Party rendezvous server

The little coordination + relay server behind Watch Party. It does two jobs:

1. **Control relay** — connects one host to its guests over WebSockets and passes
   along play/pause, sync beacons, chat, roster, and pause requests.
2. **Media relay** — guests pull the movie from *this server*
   (`GET /stream/{room}`); the server asks the host for each byte range over the
   control channel and the host pushes it back (`POST /upload/{reqId}`). So a
   guest needs only the **server address + a room code** — the host never has to
   be directly reachable, which is what makes parties work across the internet
   through home routers.

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
