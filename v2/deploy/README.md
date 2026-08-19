# Deploying the Watch Party rendezvous

The relay lets people outside your network find each other and exchange video.
It stores nothing — no accounts, no media, no history. Host and guests both
connect *outbound*, so no one else needs to touch their router.

## What it costs you in bandwidth

Read this before choosing where to put it, because it decides how many people
can watch.

The host sends a **separate copy of the film to every guest**. The relay does
not fan one stream out to many — it forwards each guest's request individually.
So:

```
upload needed = film bitrate x number of guests
```

A 3.6 Mbps film with 4 guests needs ~14 Mbps of sustained upload.

This is why running the relay **on the same network you host from** is the good
case: your PC reaches the server over gigabit LAN, and only the server's link to
the internet has to carry the guests. Your home upload becomes the ceiling.

If you host from somewhere else, the traffic crosses the internet twice — once
up to the relay, once back down to each guest.

## Setup on an Ubuntu box

Ports and addresses assume the defaults; the app talks to port **5555** and
builds `ws://<address>:5555` directly, so that port number must not change.

### 1. Get the code onto the server

```bash
git clone https://github.com/enachealex/Vault-Player.git
cd Vault-Player/v2
```

### 2. Check Docker is there

```bash
docker --version && systemctl is-active docker
```

If it isn't:

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER   # log out and back in
```

### 3. Start it

```bash
docker compose -f deploy/docker-compose.yml up -d --build
curl http://localhost:5555/health     # expect: ok
```

### 4. Let the internet reach it

Forward TCP **5555** on your router to the server's LAN address. Then point a
DNS record at your home IP address:

```
party.thejumpvault.com   A   <your public IP>
```

**Set this record to DNS-only, not proxied.** If your DNS is on Cloudflare, that
means the grey cloud, not the orange one. Two reasons: Cloudflare's proxy does
not forward arbitrary TCP on port 5555, and pushing film-sized traffic through
their free plan is against its terms.

The trade-off of DNS-only is that the record exposes your home IP address to
anyone who looks it up. If that bothers you, put the relay on a cheap VPS
instead — but re-read the bandwidth section first, because hosting from home to
a remote relay doubles the traffic.

If your home IP changes, use a dynamic DNS updater so the record follows it.

### 5. Point the app at it

In the app, set the party server to `party.thejumpvault.com`. Guests then only
need the room code.

## The server's own library

Separate from Watch Party: films kept on the server itself, listed in the app
for anyone signed in and streamed on demand. Nobody has to be at their PC with
the file open.

### Where the films live

Mount the media disk on the host, then tell compose where it is:

```
# deploy/.env
MEDIA_DIR=/mnt/media
```

Compose bind-mounts that at `/media` inside the container, **read-only**. The
server indexes and reads; it never writes there, so nothing it does can damage
the collection.

Layout inside is up to you — subfolders are walked, and the film's name comes
from its filename:

```
/mnt/media/
  Arrival (2016).mkv
  Heat (1995)/Heat.mkv
```

A dedicated disk is worth doing. On a box that boots off a small eMMC or SSD,
`df -h` will show the root filesystem is nowhere near big enough for films, and
Docker's own storage may already have been moved off it (see
`move-docker-to-hdd.sh`). Mount the media disk somewhere stable and put it in
`/etc/fstab` so it survives a reboot — a missing mount means an empty catalogue
and, worse, a rescan that decides every film has been deleted.

### Indexing

The catalogue is built at startup and rebuilt on demand:

```bash
curl -X POST https://party.thejumpvault.com/api/v1/catalog/rescan \
  -H "Authorization: Bearer <access token>"
```

The first sweep of a large library takes a while — every file is probed for its
runtime and codecs and has a poster frame extracted — and runs in the
background, so parties and health checks keep working throughout. Later sweeps
only look at what changed.

### What it costs

Storage is the obvious one: films run 5–30 GB each, so a 2 TB disk holds
somewhere between 70 and 400 of them.

**Upload bandwidth is the real ceiling**, and it is the same arithmetic as the
relay section above, minus the doubling:

```
upload needed = film bitrate x number of people watching
```

The difference is that a server-hosted film is read off local disk instead of
being pushed up from someone's PC first, so the traffic crosses your connection
**once**. On a home line, expect a handful of simultaneous viewers at film
bitrates. Music, when it lands, is roughly twenty times cheaper per listener.

### Who can see it

Only accounts the owner has approved. Registration (username + password + a
captcha the server draws itself) creates an account, but new accounts start
**pending** and see an empty library until the owner approves them from the
app's Manage Users screen. The owner is simply the first account registered on
a fresh server — **register yours immediately after deploying**.

There is no per-film permission model: approval opens the whole library.
Playback URLs are signed and expire after 12 hours, and each one only opens the
single film it was issued for.

### Getting films up there

Besides copying files onto the disk, the owner can upload straight from the
app: right-click a film in the library → "Upload to Vault". Uploads are
chunked, resume after interruptions, and are checksum-verified before they're
indexed — but they run at your home connection's upload speed, so a 20 GB remux
is hours, not minutes.

## Checking it works

```bash
curl http://party.thejumpvault.com:5555/health    # from outside your network
docker compose -f deploy/docker-compose.yml logs -f
```

If health works locally but not from outside, the port forward is the problem,
not the container.

## A note on encryption

Traffic is currently plain `http://` and `ws://`. On a local network that is
fine. Over the internet it means room codes, chat messages and the video itself
travel unencrypted, and anyone between you and your friends could read them.

Adding TLS needs a change in the app as well as the server — the client builds
`ws://` and `http://` URLs directly — so it is a code change, not just a proxy
in front. Worth doing before this is used for anything beyond friends.
