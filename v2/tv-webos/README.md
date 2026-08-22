# Vault Movies on an LG television

Puts a Vault Movies tile on the home screen of an LG smart TV (webOS 4.0 and
later, so roughly 2018 onward), so it launches like any other app instead of
being a URL somebody has to type into the browser.

It is worth doing for more than the icon. **In LG's web browser the arrow keys
drive the browser's own cursor and never reach the page**, which makes the
sign-in keypad unusable with the remote. In an installed app they go straight to
the page, and the D-pad works properly.

## What this package actually contains

Almost nothing: an icon, a name, and six lines that hand over to the server.
The app itself is served from the Vault server and always has been.

That is deliberate. The page then shares an origin with the API, so every call
to `/api/v1` and `/media` stays same-origin — a bundled copy would sit on a
`file://` origin, turn every request into a cross-origin one, and force CORS
headers onto an API that deliberately sends none. It also means the television
never needs reinstalling: improvements ship from the server.

On launch it tries the LAN address first and falls back to the public one, so
at home it takes one hop rather than a round trip through the internet, and it
keeps working when the line is down.

## Installing it

Everything below runs **on your PC**, not on the television. webOS has no way to
install an app from a file downloaded in its own browser, and no "add to home
screen" — sideloading over the network is the only route, and it is LG's
supported one.

### 1. Put the television in Developer Mode

1. On the TV, open the **LG Content Store** and install **Developer Mode**.
   (Search for it; it is a free LG app.)
2. Open it and sign in with a free LG developer account from
   <https://webostv.developer.lge.com>.
3. Turn **Dev Mode Status** on. The TV restarts.
4. Reopen the app and note the **passphrase** and the TV's IP address.

### 2. Install the tooling on your PC

Needs Node.js.

```bash
npm install -g @webos-tools/cli
```

### 3. Point it at your television

```bash
ares-setup-device
```

Add a device: name `tv`, the IP from step 1, port `9922`, user `prisoner`.
Then fetch its key — you will be asked for the passphrase:

```bash
ares-novacom --device tv --getkey
```

### 4. Build and install

From this directory:

```bash
ares-package .
ares-install --device tv com.jumpvault.vaultmovies_1.0.0_all.ipk
```

The tile appears on the TV's home screen. `ares-launch --device tv com.jumpvault.vaultmovies`
starts it from your PC if you want to check without picking up the remote.

## The one ongoing cost

**LG's Developer Mode expires after 1000 hours — about 41 days — and when it
does, every app installed this way is deleted from the television.** The Dev
Mode app on the TV has an *Extend* button that resets the clock.

To stop thinking about it, extend it from your PC on a schedule:

```bash
ares-launch --device tv com.palmdts.devmode -p '{"extend":"true"}'
```

Run that monthly — a scheduled task is enough — and the tile stays where it is.

## Updating

Not needed for changes to the app itself; those come from the server. Only
rebuild and reinstall if something in this folder changes — the icon, the name,
or the addresses in `index.html`.
