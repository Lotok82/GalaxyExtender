# GalaxyExtender Discord Relay

De-duplicating relay between the SWG command extension and Discord. Design, rationale and phase
plan live in [../Documentation/discord-relay-plan.md](../Documentation/discord-relay-plan.md);
this file is the operational reference and the wire contract the C++ side codes against.

- **Target:** `net8.0`, ASP.NET Core minimal API
- **Host:** IIS shared hosting (Plesk), in-process (`AspNetCoreModuleV2`), dedicated app pool, 1 worker process
- **Live at:** `https://mesanderson.co.uk/relay` — subfolder registered as an IIS application
- **Status:** **All phases complete — forwarding AND the Stage 2 read path (R3–R7) are implemented.** `POST /api/v1/chat` authenticates, validates, **de-duplicates across clients** (occurrence-aware, durable state in `App_Data/relay-state.json`) and **forwards to the Discord webhook** with the `allowed_mentions` lockdown; failures land in a durable outbox drained by later requests or `POST /api/v1/heartbeat`. The response header `X-Relay-Forwarding` reads `enabled`; an unconfigured webhook answers `503`. `GET /api/v1/messages` serves the pinned Stage 2 claim contract for real when `Discord:BotToken` + `Discord:ChannelId` + `Discord:Stage2Enabled` are configured (on-demand channel fetch, echo filter, sanitizer, claim/redelivery/ack store) and answers empty + `X-Relay-Stage2: disabled` otherwise. Marked lines (`[Discord] …` after the sender prefix) arriving on `/chat` are the Stage 2 delivery ack — matched exact-first-then-mask-tolerant, counted as `accepted`, and **never forwarded to Discord**. `POST /api/v1/presence` records which extension clients are alive, and with `Discord:CommandsEnabled` the bot answers a `status` mention in the channel with how many clients are online (R11 — see [Bot commands and presence](#bot-commands-and-presence-r11)). A [background ticker](#background-ticker-r12) (R12) runs the outbox drain, the cleanup sweep and the command scan on a timer, so all three still happen with nobody in game — the case where the bot most needs to answer. 237 tests. Remaining: Phase 6 (post-deploy hardening checks).

Verified on the host 2026-08-05: .NET 8.0.29 / Windows Server 2019, outbound to discord.com reachable (200 in 194 ms), `App_Data` writable, `process.id` stable across 4 minutes, `isHttps` reported correctly so `RequireHttps` is enabled.

## Layout

```
Relay/
  global.json                        SDK pin (>= 8.0.0, no prereleases)
  GalaxyExtender.Relay.sln
  src/GalaxyExtender.Relay/
    Program.cs                       pipeline, logging, limits
    Endpoints/ChatEndpoints.cs       /chat forwarding flow + /heartbeat
    Endpoints/MessagesEndpoints.cs   /messages Stage 2 claim poll
    Endpoints/PresenceEndpoints.cs   /presence check-in
    Endpoints/HealthEndpoints.cs     /health and /health/outbound
    Options/                         RelayOptions, DiscordOptions
    Services/HostProbe.cs            host-capability probe
    Services/FileStateStore.cs       durable state (dedupe/batches/outbox), atomic writes
    Services/DedupeService.cs        occurrence-aware dedupe + batchId idempotency
    Services/TextSanitizer.cs        normalise for hashing; escape/neutralise for Discord
    Services/DiscordPublisher.cs     webhook POSTs, allowed_mentions lockdown, 429-aware
    Services/Outbox.cs               parked payloads, opportunistic drain, backoff
    Services/DiscordReader.cs        Stage 2 channel read, echo + command filter
    Services/DiscordMessageParser.cs one Discord message shape, shared by reader and scanner
    Services/Stage2Queue.cs          claim/redelivery/ack work queue
    Services/ChannelCleaner.cs       request-piggybacked history sweep
    Services/PresenceTracker.cs      who is running the extension (throttled writes)
    Services/BotCommandScanner.cs    reads mentions, posts the bot's replies
    Services/BotCommands.cs          what counts as a command
    Services/StatusReport.cs         the wording the bot posts
    Services/BackgroundTicker.cs     the same work on a timer, for when nobody is in game
    App_Data/                        runtime state + logs (git-ignored contents)
    web.config                       IIS in-process hosting
  tests/GalaxyExtender.Relay.Tests/
```

## Build, test, run

```bash
cd Relay
dotnet build
dotnet test
dotnet run --project src/GalaxyExtender.Relay --urls http://localhost:5199
```

Then `GET http://localhost:5199/api/v1/health`.

Note: only the .NET 8 *runtime* is required to run. The 9.x SDK builds `net8.0` fine, so an 8.x SDK
install is not needed — `global.json` allows any SDK >= 8.0.0.

## Configuration

Bound from the `Relay` and `Discord` sections. **Never put real values in `appsettings.json`.**

| Key | Default | Meaning |
|---|---|---|
| `Relay:RequireHttps` | `false` | Reject non-HTTPS. Off until `/health` confirms the host reports `isHttps` correctly — see below. |
| `Relay:DedupeWindowSeconds` | `15` | How long a message hash is remembered. |
| `Relay:BatchIdWindowSeconds` | `300` | Retry-idempotency window. |
| `Relay:MaxLinesPerBatch` | `50` | Rejected above this. |
| `Relay:MaxLineLength` | `512` | Per-line clamp. |
| `Relay:ApiKeys` | `{}` | `label -> secret`. See [API keys](#api-keys). |
| `Relay:StateFilePath` | `App_Data/relay-state.json` | Durable state document (dedupe window, batch ids, outbox). Overridable mainly for tests. |
| `Relay:OutboxMaxEntries` | `200` | Undelivered payloads kept at most; oldest dropped beyond it. |
| `Relay:OutboxMaxAttempts` | `10` | Delivery attempts before an outbox entry is dropped (error-logged). |
| `Discord:WebhookUrl` | — | Live credential. Must be an absolute `https://` URL or the relay reports unconfigured and answers `503`. |
| `Discord:EmbedColor` | `3066993` | `0x2ECC71` green. |
| `Discord:ShowContributingClient` | `false` | Debug embed field naming the client that won the dedupe race. |
| `Discord:BotToken` | — | Live credential for the Stage 2 read path. Raw token, no `Bot ` prefix. |
| `Discord:ChannelId` | — | Bridge channel snowflake, as a string of digits. |
| `Discord:Stage2Enabled` | `false` | Operator kill switch: Stage 2 reads happen only when this is true AND token + channel are set. |
| `Relay:Stage2RedeliveryTimeoutSeconds` | `60` | Unacked claim redelivery timeout (contract value; override is for tests). |
| `Relay:Stage2MaxDeliveries` | `3` | 1 initial delivery + 2 redeliveries, then dropped and counted. |
| `Relay:Stage2TtlSeconds` | `300` | Pending messages older than this are dropped, not injected stale. |
| `Relay:Stage2MaxPending` | `50` | Pending queue cap; oldest dropped (counted) beyond it. |
| `Relay:Stage2MaxPerPoll` | `5` | Messages claimed per poll. |
| `Relay:Stage2FetchCacheSeconds` | `2.5` | Discord fetch freshness window — polls inside it skip the Discord call. |
| `Discord:CleanupEnabled` | `false` | Operator switch for the channel-history cleanup below. Off by default — deleting history must be an explicit decision, never a side effect of a deploy. |
| `Relay:CleanupMaxAgeHours` | `5` | Bridge-channel messages older than this are deleted; pinned messages always survive. |
| `Relay:CleanupIntervalMinutes` | `15` | Minimum time between cleanup sweeps. |
| `Relay:CleanupMaxSingleDeletesPerSweep` | `5` | Per-message DELETE cap for the over-14-day tail that bulk-delete rejects (first run on an old channel only). |
| `Discord:CommandsEnabled` | `false` | Operator switch for the bot commands below (`@bot status`). Off by default like the switches above — the relay starts posting messages of its own authorship when it goes on. Needs **Send Messages** + **Read Message History**. |
| `Discord:BotUserId` | — | Override for the bot's own user id. Normally left empty: it is discovered once from `GET /users/@me` and cached in durable state. A *wrong* value here makes the bot deaf to every mention. |
| `Relay:CommandScanIntervalSeconds` | `15` | Minimum time between channel scans for commands. A floor, not a schedule — see below. |
| `Relay:CommandMaxAgeSeconds` | `300` | Mentions older than this get no reply. |
| `Relay:CommandMaxRepliesPerScan` | `3` | Replies per scan; the excess is dropped, not deferred. |
| `Relay:DeliveryNoticeIntervalMinutes` | `15` | Minimum gap between unprompted "nobody is online to receive this" notices, so a conversation held while the guild is offline is not annotated line by line. |
| `Relay:PresenceOnlineWindowSeconds` | `180` | How recently a client must have checked in to count as online (the extension pings every 60 s). |
| `Relay:PresenceWriteIntervalSeconds` | `30` | Minimum time between durable writes of one client's presence stamp. |
| `Relay:PresenceRetentionDays` | `7` | How long a silent client still counts as "known" (the connected-count denominator). |
| `Relay:PresenceMaxClients` | `200` | Hard cap on the presence roster. |
| `Relay:BackgroundTickSeconds` | `60` | Interval for the background ticker below. `0` disables it and everything reverts to running on request traffic only. Clamped to 1 s–1 h. |
| `Relay:SelfPingUrl` | — | Optional absolute `http(s)` URL the ticker GETs once per tick, to stop IIS idle-stopping the pool. Point it at this relay's own `/api/v1/health`. Off by default — see below for how to tell whether you need it, and check `backgroundTicker.selfPingError` on `/health` once set. |

### Channel-history cleanup (R10)

With `Discord:CleanupEnabled` true (and the bot token + channel configured), the relay deletes
bridge-channel messages older than `CleanupMaxAgeHours`, keeping pinned ones. The channel is a
live ticker, not an archive. Like everything else on this host it is request-driven: the sweep
piggybacks on chat POSTs, heartbeats and Stage 2 polls, at most once per `CleanupIntervalMinutes`,
claimed atomically through the durable `lastCleanupUtc` stamp (visible on `/health`). One sweep is
one page read (≤100 messages) plus one bulk-delete; a bigger backlog self-heals across sweeps.
Requires the bot to have **Manage Messages** and **Read Message History** in the channel. When
nobody is online the request traffic stops, and the [background ticker](#background-ticker-r12)
carries the sweep instead.

**Check `Discord:ChannelId` before enabling both.** Deletion is irreversible, and with the ticker
running the sweep no longer happens only while somebody is playing — a wrong channel is now emptied
round the clock rather than during play sessions, with nobody there to notice.

### Bot commands and presence (R11)

With `Discord:CommandsEnabled` true, mentioning the bot in the bridge channel answers questions
about the bridge:

```
@YourBot status
→ **Guild chat bridge: online** — 2 of 5 clients connected (checked in within the last 3 min).

@YourBot status                              (with nobody playing)
→ **Guild chat bridge: offline** — nobody has checked in within the last 3 min.
  5 clients seen recently; last seen 2 h 11 min ago.
```

**Nothing here depends on what the bot is called.** A mention is matched by the bot's own user id —
Discord puts `<@id>` on the wire, whatever name was typed — and that id is discovered from
`GET /users/@me`, i.e. from the token. The replies name no bot and no product either: they describe
the subject ("Guild chat bridge"), because the operator picks the application's name and can change
it whenever, and Discord already renders the current name beside the reply. So renaming the bot,
or running this under an entirely different name, needs no configuration and no code change.

**The answer is a count, never a list of names.** The client labels it could have used are optional
ini fields that the handed-out file ships blank on purpose (nobody should have to edit anything), so
a name list would be empty or misleading — and a count means nothing a client says about itself can
reach a message the relay itself authored. Any client connected reads as online; none reads as
offline, with how long since the last one was seen, which is the useful part of a negative answer.

`status` (also `online` / `who`) reports it; `help` and a bare mention get the one-line help;
**anything else the bot is merely named in gets no reply at all** — it shares a channel with people,
and a bot that answers everything becomes noise. Replies quote the command and carry
`allowed_mentions: {"parse": []}`. A command is answered in Discord and **not** injected into the
guild room.

### Unprompted delivery notices

The bot also speaks up **without being asked** when somebody posts ordinary chat that is not going to
reach the guild room as posted — the case where saying nothing means the sender assumes it arrived.
The two answers are different in kind, so they are worded as such:

```
Bob: anyone up for a Krayt run?
→ **Guild chat bridge: offline** — nobody has checked in within the last 3 min (last seen 2 h 11 min ago).
  This message is waiting, not lost: the first client to come online posts it into the guild room.
  If nobody comes online within about 5 h, the channel tidy-up removes it undelivered.

  (read path switched off instead)
→ **Guild chat bridge: offline** — Discord → game delivery is switched off on the relay.
  This message will not appear in the guild room, now or later.
```

The "waiting, not lost" promise is the truth about this design rather than optimism — see the TTL
note under [`GET /messages`](#get-messagesclientid--discord--game-work-queue-stage-2): nothing is
fetched while nobody polls, so nothing expires while the guild is empty. The tidy-up deadline is the
one thing that can still lose the message, and it is stated only when `CleanupEnabled` is on. The
last-line caveat the notice does *not* spell out: a backlog beyond the 50-message fetch and queue
caps can still lose the oldest.

Silence is the default everywhere else. The bot stays quiet when a client is online (the
overwhelmingly common case), for messages that sanitize to nothing, for chat older than
`CommandMaxAgeSeconds`, and for anything within `DeliveryNoticeIntervalMinutes` of the last notice —
one notice tells everyone in the channel what they need to know. A `status` command and a notice are
separate events; neither silences the other.

**Operational catch:** whatever drives the scan while nobody is in game has to do so more often than
`CommandMaxAgeSeconds` (5 min), or every message is already too stale to answer by the time the relay
looks. That is what the [background ticker](#background-ticker-r12) is for, at 60 s. Before it
existed the only candidate was an external pinger on `/heartbeat`, and without one the bot was
simply deaf whenever the guild was empty.

There is no gateway connection, so "the bot listens" means the relay reads the channel on the back
of authenticated request traffic — chat POSTs, presence pings, Stage 2 polls and `/heartbeat` — or
on a background tick, at most once per `CommandScanIntervalSeconds`, claimed atomically through the
durable `lastCommandScanUtc` stamp. **With nobody in game, the tick interval is the bot's response
time**, which is exactly the case that matters: "is it online?" gets asked when it looks
like it isn't. The scan keeps its own cursor, independent of Stage 2's, so it works with the read
path switched off; the first scan after enabling stamps that cursor and answers nothing, so turning
the feature on never replies to mentions already in the channel. Replies are at-most-once — the
cursor advances before anything is posted, because a missed answer is invisible and a duplicated one
is spam.

**"Connected" means an extension client was in touch inside `PresenceOnlineWindowSeconds`.** The
relay never talks to the game server, so this is a statement about extension clients, not about the
galaxy's population. "Known" is every client seen within `PresenceRetentionDays` — the closest
available answer to "how many people have this installed", which is why the offline line words it as
"seen recently" rather than as an install count. Both figures also appear on `/health`.

Three signals feed it, and **the first two need nothing installed on the player's side** — which
matters, because there is no update mechanism for the DLL and players replace it by hand:

| Signal | Arrives when | Covers |
|---|---|---|
| `/chat` batch | somebody talks in guild chat | any relaying client |
| `/messages` poll | every 5 s (60 s while Stage 2 reads are off) | a client in the ground scene with a cached guild room id |
| `POST /presence` | every 60 s while in the world | everything, including a silent lurker — **new DLLs only** |

The poll gate is the interesting one: a client only polls once the player has typed something in the
guild tab this session (that is what caches the room id). So on the installed base, "connected"
really means **"a client that can currently move traffic"** — and that is the honest reading for this
bot's purpose, because a client that is not polling cannot receive an injection either. What the
presence ping adds is the player who is in the world but has not typed in the guild tab: counted as
connected once their client sends it. Until the DLL is widely updated, expect the count to be a
floor, not a census.

What separates one client from another is `client.id`. On an updated DLL that is unique by
construction — a hash of the machine's Windows `MachineGuid` (hostname as fallback), with any
`client_id` from the ini as a readable *prefix* rather than a replacement, so a pre-filled ini handed
to the whole guild still counts everyone separately. On older DLLs it is the ini's `client_id`, or a
hash of the computer name when that is blank, so the one thing that can collapse the count there is a
literal value typed into the ini and copied to several people.

Two consequences worth knowing. Two game clients on **one PC** share the fingerprint and count once
(they are one person). And a client that updates to this build **changes id once** — which matters
more than it sounds, because rollouts are manual and staggered, so the relay meets both forms for as
long as anyone is still on an old DLL. Where the old id is a configured label the new id is that
label plus the fingerprint, so the relay retires the old entry the moment the new one appears. Where
it was blank the two ids are unrelated hashes and nothing can link them, so the stale entry only ages
out — hence a retention window of days rather than the month it would otherwise want.

### Background ticker (R12)

Everything above is request-driven, and that leaves one hole: **with nobody in game there are no
requests**, so nothing drained the outbox, swept the channel, or read the bridge channel for
commands. The bot was deaf and mute for exactly as long as the guild was empty — which is when
"is the bridge up?" gets asked, and when a Discord message posted into the channel most needs to be
told it is not being delivered. The design named an external `/heartbeat` pinger as the answer;
none was ever set up, so in practice the gap was total.

`Relay:BackgroundTickSeconds` (default 60) runs the same three pieces of work a request carries,
on a timer. It adds no behaviour of its own and no Discord traffic an equivalent request would not
have caused: each piece keeps its durable interval stamp, so a tick arriving inside a piece's window
is a few in-memory reads. `0` switches it off and restores the old behaviour exactly.

That free ride applies to the cleanup sweep, whose window (`CleanupIntervalMinutes`, 15 min) is far
longer than a tick. It does **not** apply to the command scan: at the shipped defaults the tick
(60 s) is slower than `CommandScanIntervalSeconds` (15 s), so the scan is due every time. Steady
state with the guild empty is therefore **one Discord channel read and one `relay-state.json` write
per tick** — 60 an hour, round the clock — because claiming a scan stamps it durably. That is the
floor this feature costs; a longer `BackgroundTickSeconds` lowers it, at the price of the bot's
response time.

The request-piggybacked calls stay where they are rather than deferring to the timer, because the
timer is the part that can be taken away:

**IIS idle-stops a worker process that has gone without a *request*, and background CPU activity
does not count.** On a pool with the default 20-minute `idleTimeout` the ticker is killed by the
very quiet period it exists for. Nothing in-process can prevent that — only an inbound request
resets the timer — which is what `Relay:SelfPingUrl` is: point it at this relay's own
`/api/v1/health` (unauthenticated, does no outbound work) and each tick makes one request against
itself. It is off by default because it costs an outbound call per tick and buys nothing on a host
that does not idle-stop.

`/health` reports enough to decide:

```jsonc
"backgroundTicker": {
  "enabled": true,        // the LOOP is running, not merely that the config says it should
  "intervalSeconds": 60,
  "ticks": 1043,          // climbing while presence.online is 0 = it works on this host
  "lastTickUtc": "...",   // a gap here vs process.startedUtc = the pool stopped, not a wedge
  "lastError": null,      // last tick's failure, if any — the host refusing something shows here
  "selfPing": false,      // whether a SelfPingUrl is configured
  "selfPingError": null   // ...and whether it WORKS. Non-null = the keep-alive is not keeping alive
}
```

Read `ticks` against `process.uptimeSeconds`: **ticks resetting to a low number after a quiet
night means the pool idle-stopped and killed the ticker** — turn on `SelfPingUrl`, or set the app
pool's idle timeout to 0 if the control panel allows it. Ticks climbing steadily through the small
hours means shared hosting is tolerating it and nothing more is needed.

**Measured on this host 2026-08-10, and the answer is that it survives: `ticks` 802 against
`uptimeSeconds` 48216 — 803 expected at the 60 s interval, so no gaps across 13.4 hours including
overnight — with `lastError: null` and `selfPing: false`.** The pool is not idle-stopping the
worker, and `SelfPingUrl` is **not needed here**. Keep the check in mind if the hosting plan or app
pool configuration ever changes, but do not configure the keep-alive on spec.

`lastError` and `selfPingError` mean opposite things and are kept apart on purpose: the first says
the relay's own work is failing, the second says the keep-alive is failing and the ticker may be
about to be idle-stopped out of existence. After setting `SelfPingUrl`, check `selfPingError` is
`null` — a typo'd URL is otherwise indistinguishable from a working one until the night it fails to
save you.

`enabled` reports the loop, not the setting. Changing `BackgroundTickSeconds` to `0` at runtime
stops the loop permanently, and it does not come back when the value does — restoring it needs an
app restart, which on IIS is what editing appsettings or an environment variable causes anyway.

Load, since a shared host that decides you are abusive is the risk that matters: one timer, and at
the default 60 s tick one Discord GET plus one small state-file write per minute while the channel
scan is enabled — less than a single player's client generates by polling.

Locally:

```bash
dotnet user-secrets --project src/GalaxyExtender.Relay set "Discord:WebhookUrl" "https://discord.com/api/webhooks/..."
dotnet user-secrets --project src/GalaxyExtender.Relay set "Relay:ApiKeys:guild" "<generated-key>"
```

On the host, use environment variables (double underscore for nesting) so no credential lands in the
deployed tree: `Discord__WebhookUrl`, `Relay__ApiKeys__guild`. If the control panel makes environment
variables awkward, the fallback is a git-ignored `appsettings.Production.json` uploaded alongside the
app — outside source control either way.

## API keys

Keys are **generated by you and handed out**. Nobody sends you a key, and no per-user setup is
required. The normal configuration is a single shared entry:

```
Relay__ApiKeys__guild = 7f3a9c21-4e08-4b6d-9a17-2c8e5d0b1f44
```

Generate one with PowerShell:

```powershell
[guid]::NewGuid().ToString()
```

Give that value to anyone who should be allowed to relay guild chat; it goes in their
`DiscordBridge.ini` and travels as the `X-Relay-Key` header. Authentication is simply "does the
presented key match any configured secret" — the dictionary label (`guild` above) names the key in
logs and is never sent by the client.

**Why a set rather than one secret: rotation.** This key sits in plaintext in an ini file on other
people's machines, so assume it leaks eventually. To roll it, add a second entry, distribute it while
both are accepted, then delete the old one — no flag day where everyone breaks at once. The same
mechanism supports per-person keys later (add `kaelen`, `tarn`, drop the shared one) if an individual
ever needs revoking, but there is no reason to start that way.

`client.id` / `client.character` in the request body are **self-reported labels** for logging and the
optional debug embed field. They are not authentication and must not be trusted.

## Deploying

```bash
dotnet publish src/GalaxyExtender.Relay -c Release -o publish
```

Upload the contents of `publish/` to the site root. Framework-dependent, ~18 files, needs the 8.0
runtime present on the host. If the host turns out to lack the ASP.NET Core 8 hosting bundle, the
fallback is a self-contained publish:

```bash
dotnet publish src/GalaxyExtender.Relay -c Release -r win-x86 --self-contained -o publish-sc
```

then point `web.config`'s `processPath` at `.\GalaxyExtender.Relay.exe` with empty `arguments`.

## The host probe

`GET /api/v1/health` is unauthenticated and doubles as the answer to the hosting questions the plan
flagged as unverified. Call it **twice, a few minutes apart**, and read:

| Field | What it tells you |
|---|---|
| `process.id` | Changes between calls → app-pool **recycle** or a **web garden** (`maxProcesses > 1`). A web garden is what forces the cross-process mutex on the state store; if the pid is stable, that can be simplified. |
| `process.uptimeSeconds` | Resets to ~0 → the pool is idle-stopping. Tells you how aggressive it is, and decides whether the [background ticker](#background-ticker-r12) needs `SelfPingUrl` to survive a quiet night. |
| `backgroundTicker.ticks` | Climbing while `presence.online` is 0 → the timer works on this host. Back at a low number after a quiet spell → the pool stopped and killed it. |
| `process.framework` | Confirms which runtime the host actually loaded. |
| `storage.appDataWritable` | `false` → de-duplication and the outbox cannot persist. Hard blocker; `appDataError` says why. |
| `request.isHttps` / `forwardedProto` | Must be correct before setting `RequireHttps=true`, or the relay 403s every client. |
| `config.discordConfigured` | Whether the webhook URL actually reached the app. |

`GET /api/v1/health/outbound` separately confirms the app pool may reach `discord.com` over TLS
(it probes Discord's unauthenticated gateway endpoint, so no credential is involved). Kept off the
main health check so uptime pings do not hit Discord, and it **requires `X-Relay-Key`**: every hit
makes the shared host's IP call discord.com, and anonymous hammering could get that IP rate-limited
or banned by Discord — punishing every tenant on the host.

## Wire contract (`/api/v1`)

Auth on everything under `/api/` except the base `/health` document: header `X-Relay-Key: <key>`,
fixed-time compared against every configured secret (see [API keys](#api-keys)). Health
sub-endpoints such as `/health/outbound` DO require the key — they make outbound calls on the
caller's behalf, which makes them operator tools rather than uptime-ping targets.

Applied as path-prefix middleware rather than a per-endpoint filter, deliberately: it **fails
closed**, so an endpoint added under `/api/` later is protected whether or not anyone remembers to
attach a filter, and it runs before model binding so unauthenticated callers never get their JSON
deserialised. Keys are compared as SHA-256 digests — `FixedTimeEquals` returns early on a length
mismatch, which would leak the secret's length — and the loop over configured keys never
short-circuits, so response time does not reveal which key matched.

### `POST /chat` — game → Discord

```jsonc
{
  "batchId": "8f2c...",            // GUID, stable across client retries → idempotency
  "client":  { "id": "kaelen", "character": "Kaelen", "galaxy": "Basilisk" },
  "lines": [
    { "text": "Kaelen: anyone up for a Krayt run?", "occurrence": 1, "clientSeq": 412 }
  ]
}
```

```jsonc
{ "accepted": 1, "deduped": 0, "queued": 0, "retryAfterMs": null }
```

Response semantics now that forwarding is live: `accepted` = unique lines posted to Discord in
this request · `deduped` = lines collapsed as duplicates (an earlier client won) · `queued` =
unique lines parked in the durable outbox because Discord refused (they are delivered by a later
request or heartbeat) · `retryAfterMs` = set when Discord is rate-limiting and the client should
slow its cadence. A retried `batchId` (see below) replays the original response verbatim.

`occurrence` is how many times the client has seen this exact line in the last 60 s, including this
one. It is what lets a genuine repeat through while still collapsing the same line arriving from
several guild members: every client watches the same stream, so they all label the first "lol" as
`1` and the second as `2`. Without it a 15 s dedupe window silently eats real repeats. The dedupe
key is `sha256(normalised text)[..16] + ":" + occurrence`; the window is `DedupeWindowSeconds`,
and a client retrying a timed-out POST with the **same `batchId`** is answered from a 5-minute
idempotency window without posting anything twice.

What reaches Discord: one **plain message** per batch — no embed, no quote box (split above 2000
characters, Discord's `content` ceiling). Guild lines already arrive carrying the game's own
`[GuildChat] ` prefix, so nothing is added. Chat used to post as a green embed; it moved to plain text
so that a *boxed* message means something — the world boss alert feed keeps the embed and colours its
bar. Two consequences worth knowing: `allowed_mentions: {"parse": []}` is now the actual ping
guarantee rather than a second layer (an embed cannot ping whatever it holds; `content` can), and
`[`/`]` are deliberately left unescaped on this path because plain messages render brackets literally
— escaping them only published visible backslashes. The embed path still escapes them, and must.
Everything else is unchanged: SWG escapes
stripped, Discord markdown escaped, and `@everyone`/`@here` neutralised with a zero-width joiner.

Status codes: `400` contract violation (RFC 7807 body naming the offending field, e.g. `lines[1].text`) · `401` missing/bad key · `413` oversize · `429` rate-limited (`Retry-After`) · `503` webhook not configured.

Validation rules enforced today — a violation rejects the **whole batch** rather than silently dropping a line, because the extension is specified to enforce the same limits and anything out of bounds is a client bug worth surfacing:

| Field | Rule |
|---|---|
| `batchId` | required, must parse as a GUID |
| `client.id` | required, ≤ 64 chars, no control characters |
| `client.character`, `client.galaxy` | optional, ≤ 64 chars, no control characters |
| `lines` | 1 to `MaxLinesPerBatch` (50), no `null` elements |
| `lines[].text` | required, non-blank, ≤ `MaxLineLength` (512), no control characters |
| `lines[].occurrence` | required, ≥ 1 |
| `lines[].clientSeq` | must not be negative |

Control characters (C0 and DEL) are rejected everywhere because the extension maps them to spaces
before sending — anything carrying one is a buggy or hostile client, and rejecting them keeps
forged newlines out of the relay's logs and out of the Phase 3 Discord messages.

**Body limit: 32 KB on the wire.** This is part of the contract, separate from the per-line
character limits — a maximum-legal batch of multi-byte or heavily escaped text can exceed it, so
clients must budget bytes when assembling a batch (the extension does; see its
`MAX_LINES_JSON_BYTES`). The app answers oversize bodies with `413`; web.config carries a higher
64 KB backstop only because IIS request filtering rejects with an opaque `404.13`.

Rate limit: `RateLimitPermitsPerMinute` (120) per key per minute, fixed window, no queue — rejections get `429` with `Retry-After`. Partitioned on a hash of the key — never the key itself — and only once the key has **validated**; missing or unrecognised keys share a per-IP partition, so rotating random keys cannot mint fresh permit buckets. `/health` carries no policy, so diagnostics stay reachable while a client is throttled.

### `GET /messages?client=<id>` — Discord → game work queue (Stage 2)

**This is a work-queue CONSUME, not a broadcast read.** A successful `200` marks every returned
message as **claimed by this key+client**: no other poller receives it unless the claim expires.
The claimant is expected to inject each message into the guild room as
`[Discord] <author>: <text>`; the injected line re-entering the relay through the Stage 1 capture
is the **delivery ack** (matched by the marker — see the Stage 2 plan's "Marker and echo rules").
Ack matching is **exact first, then mask-tolerant** (same length, characters equal wherever the
received character is not `*`) because the echoed line passes through each receiving client's
profanity filter and can arrive masked. Marked lines count toward the batch's `accepted` and are
never forwarded to Discord, matched or not.
If no ack arrives within the **redelivery timeout of 60 s**, the message is handed to the next
poller; after **2 redeliveries** it is dropped and counted in `dropped`. Delivery is therefore
at-least-once — rare duplicates are accepted by design, silent loss is not.

There is deliberately **no `after=<cursor>` parameter**: under claim semantics the relay owns the
queue position, and a client cursor would fight redelivery (a redelivered message is by definition
"before" a cursor the client has already advanced past). Unknown query parameters are ignored.

```
GET /api/v1/messages?client=kaelen
X-Relay-Key: <key>
```

`client` is required: ≤ 64 chars, no control characters — same rules as `/chat`'s `client.id`. It
attributes claims for redelivery accounting and logging; it is **not** authentication and is not
trusted beyond that.

```jsonc
{
  "messages": [
    { "id": "1402691702617341952",          // Discord snowflake, unique, ascending
      "author": "Bob",                       // display name, sanitized, clamped ≤ 32 chars
      "text": "anyone on tonight?",          // sanitized, clamped (see below)
      "timestampUtc": "2026-08-05T21:14:09+00:00" }
  ],
  "dropped": 0
}
```

- Messages arrive **oldest first** (ascending id), at most **5 per poll** — sized so a claimant
  injecting at the game-safe ~1 line/s finishes well inside the 60 s claim window.
- `dropped` counts messages discarded since the last poll that reported them (TTL expiry or the
  redelivery cap). Report-once: each loss is reported to exactly one poller, so the extension can
  surface "N Discord messages were missed" without double counting.
- **The TTL runs from the moment the relay FETCHED a message, not from Discord's timestamp**, and
  fetching only happens when a client polls. So an idle channel accumulates nothing that can
  expire: a message posted while nobody is in game waits in Discord and is delivered to the first
  client that comes online, however long that takes. TTL expiry therefore means "a client polled,
  queueing this, and then stopped injecting for 5 minutes" — a claimant that quit or zoned out,
  not an empty guild. (What *can* still lose a waiting message: the R10 channel tidy-up deleting
  it from Discord first, or a backlog beyond the 50-message fetch/queue caps. The bot says so —
  see [Bot commands and presence](#bot-commands-and-presence-r11).)
- `text` is pre-sanitized by the relay (R5: mentions/emoji resolved, newlines collapsed, SWG
  escapes stripped — the Core3 server does not strip `\#` colour codes itself) and pre-clamped:
  `author` ≤ 32 chars, `text` ≤ 200 chars, so the full injected line
  `[Discord] <author>: <text>` is ≤ 244 chars. Core3 enforces **no** room-message length limit
  (S4 finding), so this bound is ours: it mirrors the game's own 255-char spatial-chat
  convention and keeps the re-captured line inside `/chat`'s 512-char `MaxLineLength`. The
  client injects verbatim and never needs to truncate.
- Header `X-Relay-Stage2: disabled` until the Discord read path (R3) is configured and enabled,
  then `enabled`. An unconfigured Stage 2 still answers `200` with an empty list — deliberately
  **not** `503`, so the extension's poll loop treats "bridge off" as the ordinary idle case
  rather than an error worth backing off from.

Status codes: `400` missing/invalid `client` (RFC 7807 body) · `401` missing/bad key · `429`
rate-limited (`Retry-After`) — same per-key/per-IP partitions and the same
`RateLimitPermitsPerMinute` budget as `/chat`, so a client's polls and posts draw from one
bucket.

### `POST /presence` — "I am here"

Authenticated. How the relay knows anyone is running the extension, and therefore what
`@bot status` reports.

```jsonc
{ "client": { "id": "kaelen", "character": "Kaelen", "galaxy": "Basilisk" } }
```

```jsonc
{ "online": 2, "known": 5, "onlineWindowSeconds": 180 }
```

`client` follows exactly the same rules as `/chat`'s (`id` required, ≤ 64 chars, no control
characters; `400` names the offending field) and is equally self-reported — it is not
authentication. **Only `id` is used**, as the thing that distinguishes one install from another;
`character` and `galaxy` are accepted and ignored (the status answer is a count), so an older client
that still sends them is fine and the relay stores no player labels.

**This endpoint is an accuracy upgrade, not a prerequisite.** Presence is primarily derived from
traffic the relay already receives — `/chat` and `/messages` refresh the same stamp — because DLL
updates are manual and reach the guild slowly. What those two signals cannot see is a player who is
in the world but has not typed in the guild tab this session: no room id is cached, so their client
never polls, and a quiet guild looks identical to an empty one. That is the gap this closes, and it
is a gap in *counting* only — such a client cannot receive an injection either, so the bridge's own
behaviour does not depend on it.

The extension pings it every **60 s** while the bridge is active *and the frame tick is fresh* — the
tick only runs in the ground scene, so a client parked on the login screen is not reported as logged
on. The response counts are returned so the in-game `/emu discord status` can show the same figures
the Discord bot reports without a second endpoint. Like every authenticated request, a ping also
drains the outbox and carries the cleanup sweep and the command scan.

Status codes: `400` invalid `client` (RFC 7807 body) · `401` missing/bad key · `429` rate-limited —
same per-key bucket as `/chat` and `/messages`, which one ping a minute per client barely touches.

### `POST /heartbeat`

Authenticated. Drains the outbox, sweeps the channel and carries the bot-command scan — the same
work as one [background tick](#background-ticker-r12) — and returns `{ "outbox": <depth> }`.

Since the ticker exists this is no longer the only way to get that work done with nobody in game,
but it stays useful for two things the ticker cannot do: an **external** pinger (with the key) is
an inbound request, so it also prevents the app pool idle-stopping, and it is the manual way to
force a drain or a scan right now.
