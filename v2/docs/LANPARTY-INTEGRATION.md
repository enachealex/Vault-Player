# Vault Player ↔ LAN Party integration

The mirror of LAN Party's `docs/VAULT-PLAYER-INTEGRATION.md`. That document says what LAN Party
offers. This one says what Vault Player will consume, what it will build, and what is left for
LAN Party — so both sides work from a written contract instead of a relay.

**Vault relay (prod):** `https://party.thejumpvault.com`
**Vault repo:** `github.com/enachealex/Vault-Player` — desktop app `v2/src/App` (WPF), relay
`v2/src/Rendezvous` (ASP.NET Core).

---

## 1. The requirements, verbatim

The numbered list referenced in conversation is **not** from a LAN Party planning doc — it is my
numbering of the product owner's original request. Here it is unedited:

> Create a plan for implementation for another application using our App and automatically using
> their signed in user as our user in this application. The Friends list should be integrating the
> app's friends list as well. There should be consideration of Parties, Channels, and Servers. If
> the application is ran in the app's Server Voice Channel, the users within the Server should be
> appearing in the list (online/offline). Online users can be invited, offline cannot be.

Numbered:

| # | Requirement | Owner |
| --- | --- | --- |
| 1 | A signed-in LAN Party user is automatically the Vault Player user — no second login | **Vault** (LAN Party side already shipped) |
| 2 | Vault Player's friends list integrates LAN Party's friends | **Vault** |
| 3 | The design accounts for Parties, Channels and Servers | **LAN Party** |
| 4 | Run inside a Server voice channel, the Server's users appear in a list with online/offline | **LAN Party** |
| 5 | Online users can be invited; offline users cannot | **LAN Party** |

"Parties" is a Vault Player concept (a relay room, `MOVIE-XXXX`). "Channels" and "Servers" are
LAN Party's. Requirement 3 is the mapping between them — see §4.

---

## 2. Confirmed against your code, 2026-08-09

Everything in your latest contract checks out. Verified by reading the repo at `c40ff25` and
probing production, not by trusting the note:

| Claim | Verified |
| --- | --- |
| `voice:join` gated on membership | ✅ `index.js:1107` — `mayUseVoice()` → `roleOf()`, emits `server:denied` |
| `sfu:caps` gated | ✅ `sfu.bindSocket(socket, voiceRoomOf, mayUseVoice)` at `index.js:852` |
| Assertion TTL now 5 minutes | ✅ `routes/vault.js:27` — `TTL_SECONDS = 300` |
| Rate limits live | ✅ `sso-token` 60/5min, `userinfo` 120/5min, `routes/vault.js:53,92` |
| `/userinfo` no longer returns nulls | ✅ new `loadUser()` at `routes/vault.js:34` selects `email, settings` |
| Production running it | ✅ `/mnt/retroboard-data/lan-party` at `6849d77` |

The 5-minute TTL resolves the only real objection we had to the original design: 60 seconds did not
reliably survive a cold desktop launch, and the same token is also the credential for `/userinfo`.
Five minutes is comfortable.

**Nothing further is needed from LAN Party for requirements 1 and 2.** The API is complete.

---

## 3. What Vault Player will build (requirements 1 and 2)

### 3.1 Where the assertion is verified

**The relay verifies it, not the desktop app.** A shipped WPF binary cannot hold `VAULT_SSO_SECRET`
— anything in it is readable by whoever runs it. So:

```
desktop app  --sso=<JWT>-->  relay POST /api/v1/auth/lanparty  --Bearer <same JWT>-->  LAN Party /userinfo
```

The desktop app treats the assertion as opaque and never inspects it. Both services run on the same
host, so the secret is read server-side straight into the relay's gitignored `deploy/vault.env`,
exactly as your note describes.

### 3.2 `POST /api/v1/auth/lanparty`

Request `{ "assertion": "<JWT>" }`. The relay then does, in order:

1. Verify HS256 against `VAULT_SSO_SECRET`, **algorithm pinned**, never read from the header.
2. Require `iss === "lanparty"` and `aud === "vault-player"`.
3. Reject expired, allowing ≤30s skew.
4. Reject a replayed `jti` (persisted, 10-minute TTL — longer than the assertion, so a replay can
   never outlive its own dedupe entry).
5. Resolve the account by `(provider, sub)`; create on first sight.
6. `GET /integrations/vault/userinfo` with the same assertion, once. Never on a timer.
7. Issue normal Vault access + refresh tokens.

Response is the existing `AuthResp` shape, so the desktop client's session handling is unchanged.

### 3.3 Identity storage

A new `LinkedIdentity` row: `(Provider, Subject, UserId, ProviderName, CreatedAt, LastSeenAt)`,
unique on `(Provider, Subject)` and on `(Provider, UserId)`. `Subject` is your `sub` verbatim.

Because `EnsureCreated()` is a no-op against the existing `vault.db`, this goes in the hand-written
idempotent DDL block in `Program.cs` alongside `Friendships` and `PartyInvites`, and gets a delete in
`POST /api/v1/delete`.

### 3.4 Friends — read-through, never mirrored

Your `friends[]` is stored as a **snapshot on the session**, refreshed once per SSO login, and shown
in `FriendsWindow` as a separate read-only group headed *"From LAN Party"*. It is deliberately not
written into Vault's own `Friendship` table:

- LAN Party has no unfriend route, so mirrored rows could never be retired.
- Vault's own friend DTO exposes email; a LAN Party friendship must never disclose one.
- Vault-native friends keep Add / Accept / Remove exactly as today, untouched.

Avatars are resolved against `https://lanparty.thejumpvault.com` before display, since you return a
relative path.

### 3.5 Deferring the update prompt

Vault Player checks for updates on launch. If a `vaultmovies://` link carries `sso=`, that check is
deferred until after the assertion is redeemed — otherwise an update prompt could burn the window.
This is our fix, not yours; it is why we did not ask you to extend the TTL further.

---

## 4. Parties ↔ Servers ↔ Channels (requirement 3)

The binding already exists in your code and we propose reusing it verbatim rather than inventing
anything:

| Concept | Role |
| --- | --- |
| **Server** (`server_members`) | The invitable population and the permission boundary |
| **Voice channel** | The party's identity and lifetime — one channel, one party |
| **Party** (`MOVIE-XXXX`) | A transient routing handle in the relay. Least authoritative thing here: in-memory in another process, gone when the host disconnects |

So: **a Party is the `movie` activity state of exactly one voice channel.** `activities['voice:'+serverId+':'+channelId]`
is already one-per-room, already replayed to late joiners, and already dies when the room empties.

Two consequences worth stating:

- **Outside a voice channel there is no server scope**, and that is not an error state — the panel
  should fall back to inviting friends rather than showing an empty roster.
- **The `demo` commons has no `server_members` rows**, so it has no roster. Falling back to friends
  there is the right behaviour, and worth making an explicit product decision rather than an
  omission.

---

## 5. Roster and invites (requirements 4 and 5) — LAN Party's side

We are **not** asking for a REST API for this, and we would decline one if offered. Your reasoning is
right: exposing a members endpoint to Vault would add a long-lived credential and an enumeration
surface in order to render a list the LAN Party client already renders, for a user its own socket has
already authenticated. Requirements 4 and 5 belong in the Movie Night panel.

What that panel needs, concretely:

### 5.1 The list

Rows sourced from `serverRoster(serverId)`, which already returns `{ username, role, online, profile }`.

Suggested row shape for the client:

```json
{ "username": "bea", "displayName": "bea", "avatarUrl": "/uploads/…", "avatarColor": "#a55eea",
  "role": "member", "reachable": true, "inVoice": false, "invitable": true }
```

- `invitable = reachable && !inVoice && username !== me && canAccessChannel(...)`
- `inVoice` from the `voice:<serverId>:<channelId>` room membership — those people can already see
  the activity, so they are shown but not invitable.
- Private channels: list **server** membership (which the members panel already shows), but gate
  `invitable` on channel access. Inviting someone who cannot enter the channel is a silent no-op.

### 5.2 What "online" should mean

**Our recommendation: reachable anywhere on LAN Party, not "currently viewing this server."**

`serverRoster` currently computes `online` from the `clients` map filtered by `c.serverId`, which
means "connected *and* looking at this server". Someone reading a DM in another server would show as
offline and be un-invitable, despite an emit reaching them fine.

The predicate we would use instead:

```js
const isReachable = (username) =>
  (io.sockets.adapter.rooms.get(`user:${username}`)?.size || 0) > 0;
```

Every authenticated socket already joins `user:<username>`, socket.io refcounts membership, so this
is multi-tab correct and is *exactly* co-extensive with "an `io.to()` emit will land". Invitability
and deliverability become the same fact, which is what makes requirement 5 honest.

We would treat the `presence_status` column as a decorative label only (`available` / `idle` / `dnd`),
not as the gate.

### 5.3 Invites

Delivery via `io.to('user:' + username)` — you already have the channel. Two rules:

- **Re-check reachability at emit time**, not at render time, and return a per-target result
  (`{ username, delivered, reason }`) so the row can say "cam just went offline" instead of lying.
- **No store-and-forward.** An invite delivered later points at a dead room, since the relay drops a
  room when the host disconnects.

### 5.4 Auto-bind — stop pasting codes

Today `MovieNight` takes a pasted code. Once the desktop app knows its LAN Party identity it can
report the room it just created, so the activity state fills itself in. That is requirement 3's real
payoff: nobody types a room code in either direction.

We are happy to own whichever half of that you prefer — a small authenticated endpoint on your side,
or a relay-side push. Worth deciding together rather than either of us guessing.

---

## 6. Two rules we will hold

**Never match on email.** LAN Party has no email verification — no `email_verified` column, no verify
endpoint — so anyone can register with someone else's address. If an incoming `sub` is new but its
`email` matches an existing Vault account, Vault Player will **not** link them. It returns a
`confirm-merge` state and asks the user to sign into that Vault account. Auto-merging would be
takeover of someone's film library.

**`sub` is the only join key, and it is opaque to us.** A Vault-local account can never claim an
arbitrary `sub`, so pre-registering a LAN Party username cannot inherit that identity. We store it
verbatim and never case-fold it — `users.username` has no `COLLATE NOCASE`, so `Alice` and `alice`
are different accounts and we must not merge them.

---

## 7. One honest limit, for the product copy

Gating invites on presence controls **who learns a room code**, not who can watch. Codes are ~923k
combinations, `/stream/{code}` is anonymous by design (it is what makes `/join/{code}` links work
from any chat), and an invited user can forward a code to anyone. Neither UI should say "only
invited people can watch."

That is an accepted, pre-existing trade-off rather than something this integration introduces — but
it should be inherited knowingly.

---

## 8. Division of labour

| Phase | Work | Repo |
| --- | --- | --- |
| 1 | Roster panel, online/offline, invites, auto-bind (req 3/4/5) | LAN Party |
| 2 | `auth/lanparty`, `LinkedIdentity`, `jti` dedupe, friends group, deferred update check (req 1/2) | Vault |
| 3 *(optional)* | Roster mirrored into the WPF lobby; durable account linking | Both |

Phases 1 and 2 are independent — neither blocks the other, and each is useful shipped alone. We would
not start phase 3 until phase 1 has been lived with; the LAN Party panel may well turn out to be
where the list belongs permanently.

---

## 9. Open questions for LAN Party

1. **Online semantics** — do you agree with §5.2 (reachable anywhere) over the current
   "viewing this server" behaviour?
2. **Auto-bind ownership** — your endpoint, or a relay push? (§5.4)
3. **1:1 friend calls** (`call:invite`) are not voice rooms, so there is no server scope there. Is
   Movie Night expected to work in a DM call? If so it is the friends-fallback path, not the roster.
4. **`name` equals `sub` today.** We will display `name` and treat it as mutable, so a real display
   name later needs no change on our side. Flagging only so it is not a surprise.
