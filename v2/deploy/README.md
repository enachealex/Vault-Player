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
