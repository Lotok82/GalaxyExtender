# Discord Chat Bridge — Stage 2 (Discord → game) Investigation Plan

Status: **S1/S2 spike COMPLETE; R1 claim contract pinned and stub shipped; S4 answered (2026-08-05).** Send path confirmed end-to-end (injected line visible in guild chat, seen by a second player, relayed to Discord). `GET /api/v1/messages` now exists on the relay with the pinned consume contract (empty + `X-Relay-Stage2: disabled` until R3); Core3 constraints recorded under "S4 findings". Next: extension poll/inject path against the stub. **Decision made: Model B — Discord messages are injected into the real guild room** so every guild member sees them, extension or not. Messages carry a `[Discord]` text marker; marked lines are never forwarded back to Discord.
Last updated: 2026-08-05

Companion docs: [discord-bridge-plan.md](discord-bridge-plan.md) (Stage 1, as built), [discord-bridge-research.md](discord-bridge-research.md) (binary/source findings), [discord-relay-plan.md](discord-relay-plan.md) (relay).

## The design in one paragraph

Every extension client polls the relay. The relay hands each pending Discord message to **exactly one** poller (per-message claim with a redelivery timeout — no standing "leader"). That client sends `[Discord] <author>: <text>` into the real guild room through the game's own chat pipeline. Everyone in the guild — extension or not — sees it as a normal guild line. The line then arrives back at every relaying client's Stage 1 capture hook and is relayed to the relay as usual; the relay recognises the `[Discord]` marker, uses it as the **delivery ack** for the claimed message, and does **not** forward it to Discord. If no ack arrives within the timeout, the message is redelivered to the next poller.

### Why the `[Discord]` marker is checked at the relay, not the client

The obvious implementation of "don't feed marked lines back" is to skip them in the capture hook. Filtering at the relay instead buys three things:

1. **A true end-to-end ack.** The marked line only re-enters `Tab::appendText` if the game server actually broadcast it to the room — so its arrival at the relay proves delivery, which is exactly what the claim/redelivery mechanism needs. A client-side "I called the send function" ack proves much less (the server's flood throttle could have eaten it).
2. One place for the marker rules, changeable without redeploying DLLs to every player.
3. The relay can log Discord→game traffic in the same place as game→Discord.

Cost: marked lines consume a little upstream bandwidth and dedupe work. Acceptable.

### Why per-message claim instead of leader election

With 5 clients running the extension, an uncoordinated design injects every Discord message 5 times **into the game itself** — the marker does nothing to stop that (it only stops the copies reaching Discord; the relay's Stage 1 dedupe already did). Exactly-one-injection needs relay coordination. A per-message claim (first poller gets the message, marked in the durable store, redelivered only if unacked after T seconds) is simpler and more robust than electing a standing injector: no lease renewal, no failover logic, self-healing when the claimant zones/logs off mid-delivery, and it degrades to "whoever is online injects".

### What Model B removes from the plan

The previous draft's hardest items — calling `Tab::appendText` ourselves, obtaining window/tab `this` pointers, constructing an old-ABI `ChannelId` — are all **gone**. Nothing is injected into the local UI; the message goes upstream and comes back through the client's normal receive path, rendering in every tab that shows guild chat, with timestamps, logging, everything. And `soe::unicode` (soewrappers.h) already wraps the client's own string allocators, so building arguments for client calls is a solved problem, proven daily by `EmuCommandParser`.

## The send path — main investigation

The typed-chat entry point is **already hooked**: `CuiChatParser::parse(const soe::unicode& command, soe::unicode& result, uint32_t chatRoomID, bool useChatRoom)` at `0x9FF6F0` ([CuiChatParser.h](../SWGCommandExtension/CuiChatParser.h)). Every line the player types arrives here **with the destination chat room id** — this is how room chat (guild included) is routed. Working hypothesis: calling `originalParse::run(text, dummyResult, guildRoomId, true)` with plain (non-`/`) text sends it into that room.

| # | Question | How to answer |
|---|----------|---------------|
| S1 | **Does plain text through `originalParse::run(text, result, roomId, true)` actually send to the room?** The hook comment says 0x9FF6F0 is the *last* handler (languages/moods) — plain-text room sends may happen in this function, or in a caller before it. | Add a debug log of `(chatRoomID, useChatRoom, text)` in the existing parse hook, type a line in the guild tab in-game, and see what arrives. Then add a hidden `/emu discord inject <text>` that replays via `originalParse::run` with the captured room id and check the message appears in guild chat (and reaches other players). One session answers both. |
| S2 | **How do we get the guild room id without the player typing first?** Fork: `s_guildRoomId` static, set on login via `CuiChatRoomManager::setGuildRoomId`, read via `getGuildRoomId()` (fork `CuiChatRoomManager.cpp:2147`). | Preferred: hunt the static's data address (reuse `tools/` string-anchor scripts; `"GuildRoomId"`-ish strings or the setter's caller). Fallback that needs zero hunting: cache `chatRoomID` from the parse hook whenever the player types in the guild tab — but that means the injector can't inject until they've typed once per session, so treat it as a stopgap. S1's log output tells us what value to expect. |

### S1/S2 spike findings (session run 2026-08-05)

From the live session (`/emu discord rooms` after typing in the guild tab, then inject):

- **Plain guild-tab lines DO reach the 0x9FF6F0 hook**, with `chatRoomID` set and `useChatRoom=1` — observed value `4266328066` (`0xFE4B0002`). The room send path runs through this handler; **S3 (hunting a separate send function) looks unnecessary.**
- **Slash commands typed in the guild tab arrive with the same room id and `useChatRoom=1`** (`/console` logged identically to plain lines). So the S2 stopgap cache populates from *any* line typed in the guild tab, commands included — e.g. running `/emu discord rooms` from the guild tab is itself enough to cache the id. The `s_guildRoomId` static hunt is only needed if requiring one typed line per session is deemed too much friction for injector clients.
- **`/emu discord inject Test Text` → `originalParse::run(room=4266328066, useChatRoom=1) returned true`, empty echo** — the handler accepted the injected line exactly like a typed one. Visual confirmation (line visible in guild chat / to a second account / relayed to Discord) recorded below.
- Treat the room id as **per-session dynamic** until proven otherwise — one sample only; never hardcode it.
- **End-to-end confirmed (2026-08-05):** the injected line appeared in the sender's guild tab, was seen by a second player, and relayed to Discord through the Stage 1 capture. **S1 answered: `originalParse::run(text, echo, roomId, true)` sends plain text into the room through the game's own pipeline. The send path needs no further hunting.**

### S1/S2 spike instrumentation (built 2026-08-05)

The instrumentation for both questions is in [CuiChatParser.h](../SWGCommandExtension/CuiChatParser.h)/[.cpp](../SWGCommandExtension/CuiChatParser.cpp), surfaced through `EmuCommandParser`:

- **`/emu discord rooms`** — shows the last 16 lines submitted through the `CuiChatParser::parse` hook as `#seq room=<id> useChatRoom=<0|1> <text sample>`, plus the currently cached room id. Every typed line is recorded (commands included), so the log also reveals whether plain guild-tab lines reach this handler at all — if they don't, that alone answers S1 (the send happens earlier in the call chain → S3).
- **S2 stopgap cache** — the most recent line with `useChatRoom == true` and a non-zero `chatRoomID` sets the cached room id (shown by `rooms`). Commands typed in a room tab may also populate it, depending on what the client passes — the session will tell.
- **`/emu discord inject <text>`** (hidden, not in help) — replays `<text>` via `originalParse::run(text, echo, cachedRoomId, true)` and prints the return value and any echo text. No-op with a red message if nothing is cached yet.

Session script: type a plain line in the guild tab → `/emu discord rooms` (note the room id / useChatRoom values and confirm the cache) → `/emu discord inject hello from the spike` → check the line appears in guild chat, ideally on a second account, and relays to Discord via the Stage 1 hook. Findings recorded above.
| S3 | **If S1 fails** (plain text isn't sent by this handler): find the real send function — `CuiChatRoomManager::sendPrelocalizedChat(roomId, text)` or equivalent — via a fresh address hunt. | Fork source first for signature + string anchors, then `tools/` scripts. Only if S1 fails. |
| S4 | **Server-side constraints (Core3).** Message length limit, flood throttle (how many lines/sec before the server drops or kicks), whether `\#RRGGBB` colour escapes survive the round-trip (purple text — cosmetic, plain marker works either way), and what happens if the sender isn't in a guild. | ✅ **Answered 2026-08-05 from the local Core3 source — see "S4 findings" below.** Short version: no length limit, no flood throttle on the room path, non-guild senders dropped silently with no error packet, colour escapes survive. |
| S5 | **Threading.** `originalParse::run` must be called from the main thread. | Existing pattern: poll worker fills a locked queue, `GroundScene::parseMessages` frame tick drains it. Same as Stage 1's architecture, opposite direction. |
| S6 | **Injection pacing.** Discord bursts (someone pastes 10 lines) must not trip the server flood throttle or get the injecting player kicked. | Inject max ~1 line per second (tune from S4); claim only what can be injected before the redelivery timeout. |

### S4 findings (Core3 source read 2026-08-05, `MMOCoreORB/src/server/chat/`)

The room-message path is client packet `ChatSendToRoom` → `ChatSendToRoomCallback` → `ChatManagerImplementation::handleChatRoomMessage` (`ChatManagerImplementation.cpp:702-758`) — this is the path our injected `originalParse::run` line takes, and it bypasses the command queue entirely.

- **No message length limit.** No length check anywhere on the room path — no constant, no config, no lua. (`IM_MAXSIZE = 255` in `ChatManager.idl` applies to `/tell` and spatial `/say` only; `PM_MAXSIZE = 4000` is mail.) Room messages are bounded only by transport packet size. **The R5 clamp is therefore ours to choose; pinned in the R1 contract:** author ≤ 32, text ≤ 200, so the full injected line `[Discord] <author>: <text>` is ≤ 244 chars — mirrors the game's own 255 spatial-chat convention and keeps the re-captured line (`InjectorName: ` + body) comfortably inside Stage 1's 512-char `/chat` limit.
- **No flood throttle on the room path.** The only client-message throttle in Core3 is the command-queue spam guard (5 commands per 1000 ms, `CommandQueueEnqueue.cpp:34-63`, silent drop) — it applies to the `/guild` *command* route but **not** to chat-tab room sends, which never enter the command queue. No auto-mute, no kick; mute is admin-only (`FreezePlayerCommand`). S6's ~1 line/s pacing is retained anyway for readability and client-side safety, but the server will not punish bursts.
- **Non-guild sender: dropped silently.** `handleChatRoomMessage` returns with **no ack packet and no error** if the sender isn't in the room's `playerList` (`:731-732`, name-keyed membership, populated on guild join and removed on leave/kick). Same silent drop for a null zone (`:735`) and — with only a local system message — for a muted sender (`:712`). The **only** failure ack ever emitted is error 9 (moderated room, non-moderator, `:740-742`), and guild rooms are never moderated. **Consequence: a client-side "the send call returned true" ack is genuinely meaningless — the relay-side marker ack is the only real delivery proof, exactly as designed.** (The success ack `ChatOnSendRoomMessage(counter, 0)` is broadcast to the whole room, not just the sender — a curiosity, not something we rely on.)
- **Server does not filter message text.** `formatMessage` (`:2053-2066`) strips only the literal sequences `\>` and `\@`; `\#RRGGBB` colour escapes survive the round-trip intact, and there is no profanity filter on message bodies server-side (client-side filtering still applies at each receiver — the R7 caveat stands). So R5's stripping of SWG escapes from Discord text is mandatory: the server will not do it for us.

## Marker and echo rules

- Injected body format: **`[Discord] <author>: <text>`** — plain ASCII marker, survives profanity filtering and any server-side escape stripping. At other players' capture hooks the full line reads `[GuildChat] <InjectorName>: [Discord] <author>: <text>`.
- **Relay rule:** a relayed line whose body (after the game's `Sender: ` prefix) starts with `[Discord] ` is (a) matched against pending claimed deliveries → ack, (b) **never forwarded to Discord**, matched or not.
- **Loop 2 (game → Discord → game) still needs the Discord-side filter:** the relay's `/messages` read must exclude its own webhook/bot posts (`webhook_id` / `author.bot`), or every game line comes back for injection. The marker does not cover this loop — game→Discord lines carry no marker.
- **Spoofing, accepted:** any guild member can type `[Discord] ...` manually. Effect: their line never reaches Discord (self-muting) and looks like a bridged message in-game (impersonation of Discord users). Tolerable for a guild tool; the relay drops rather than forwards unmatched marked lines, which is the safe default. Revisit only if abused.
- **Injector-name display (decided 2026-08-05):** the injecting character's game-level sender name (`Kaelen: [Discord] Bob: hi`) must not be shown to extension users — it reads as confusing noise. The colour trick (prefix the body with a background-coloured escape) is ruled out: `\#RRGGBB` escapes only affect text *after* their position, and the sender prefix is composed by each receiving client *before* the body, so nothing in the body reaches it (and chat backgrounds are semi-transparent/user-themed anyway — no single colour to match). Instead: **extension clients rewrite the line locally in the existing `Tab::appendText` hook** — when the body after the sender prefix starts with `[Discord] `, strip the sender-name portion (tolerate the client's own colour escapes around it) and pass the cleaned line to the original. Ordering rule: Stage 1 capture relays the *original* line (R7 ack matching depends on it); only the displayed copy is rewritten. **Non-extension clients necessarily still see the raw line** — the server stamps the true sender and their client renders what it received; only a Core3 change could alter that. Accepted — and the incentive points the right way: installing the extension is what removes the artifact. (A designated-injector-account softener was considered and rejected 2026-08-05 — not worth the extra account; per-message claim among whoever is online stays the one and only delivery path.)
- **Duplicate-injection edge:** claim redelivery is at-least-once — if client A injects but its ack is lost/late (its relaying broke mid-flight), client B gets a redelivery and the room sees the message twice. Mitigations: redelivery timeout ≫ Stage 1 batch latency (≥30 s vs ~1.5 s), cap redeliveries at 2, and before redelivering check the relay's recent-line history for a matching marked line (ack may have arrived without being matched). Design for "rare and bounded", not "impossible".

## Relay side

| # | Question / task | Notes |
|---|----------------|-------|
| R1 | **Contract & stub first** (relay plan Phase 7, revised for claim semantics). ✅ **Done 2026-08-05.** Contract pinned in [../Relay/README.md](../Relay/README.md): consume semantics (200 = claimed by key+client), 60 s redelivery timeout, max 2 redeliveries then dropped, ≤5 messages per poll oldest-first, `dropped` count with report-once semantics, required `client=<id>` query param (same rules as `/chat`'s `client.id`), **`after=<cursor>` dropped** (relay owns queue position; unknown params ignored so this is never a breaking change), clamps `author` ≤ 32 / `text` ≤ 200 (S4: full injected line ≤ 244). Stub shipped: authenticated + rate-limited exactly like `/chat` (shared per-key bucket), validates `client`, returns empty + `X-Relay-Stage2: disabled` (deliberately 200, never 503, when unconfigured). 77 relay tests green. | The whole extension side can now be built and harness-tested against it. |
| R2 | **Bot setup + REST content check.** ✅ **Done 2026-08-05.** Bot created, invited, and verified with a live `GET /channels/{id}/messages` (via `curl.exe` — Windows PowerShell 5.1's `Invoke-RestMethod` gets rejected by Discord's edge with `40333 internal network error`; use curl or pwsh 7). Findings: user messages carry `content` with the intent on; the relay's webhook posts have `webhook_id` + `author.bot: true` + empty `content` (text is in `embeds[0].description`) — so the R4 filter keys are confirmed; users carry `global_name` (display name, may be absent) and `username` — **prefer `global_name`, fall back to `username`** for the injected author prefix, and clamp it (real example seen: a 30+-char display name). Token not yet on the host — goes into `appsettings.Production.json` when R3 is built. | ✅ Read path proven before any code was written. |
| R3 | **On-demand Discord fetch, shared cache.** Fetch from Discord only when a client polls AND the cached snapshot is older than ~2–3 s; new messages append to the durable pending queue (reuse the outbox/file-store patterns). Discord-facing request rate stays independent of player count; request-driven, IIS-recycle-safe. | Store Discord's last-seen snowflake in `RelayState` (the existing `Stage2Cursor`). |
| R4 | **Echo filter (loop 2):** exclude webhook/bot-authored messages from the pending queue. Mandatory. | Confirm field shapes during R2's live call. |
| R5 | **Sanitizer for Discord → game text:** resolve `<@id>` mentions and `<:name:id>` emoji to readable text, drop/mark attachments/embeds/stickers, collapse newlines, clamp to the game-safe length from S4 (split long messages into ≤2 lines, drop the rest with a `…`), map characters the game font can't render, and **strip SWG escape sequences (`\#`, `\>`)** so Discord users can't inject colour/format codes into the game. Mirror `TextSanitizer`/`ChatBatchValidator` test style. | Discord text is untrusted input into every guild member's client — this is the security-sensitive piece. |
| R6 | **Queue policy:** TTL so stale messages aren't injected when no client was online (drop if older than ~5 min, count them in a `dropped` field), cap pending queue size, deliver in order, ≤N per poll (match S6 pacing). | |
| R7 | **Marker ack matching:** relay composed the injected text itself, so match acks by comparing the normalised marked line against outstanding claims. Reuse the Stage 1 normalise-for-hash pipeline so injector-side profanity filtering can't break matching… **check:** the injected text passes through the *receiving* clients' profanity filters (known Stage 1 caveat) — if the Discord text contains a filtered word, the ack line arrives as `****` from some clients. Match on the claim's normalised text AND its filtered variants, or match loosely (prefix + author + length). Needs a design decision. | |
| R8 | **Poll-load sanity:** all clients poll (every client is a potential injector). N players at 5 s = 12 N/min against the 120/min/key limit — fine to ~10 players, but confirm the Plesk host's request quotas (~17k req/day/player). Long-polling remains off the table on shared IIS. | |
| R9 | **Optional once the bot exists:** post game→Discord via the bot, retire the webhook (single identity simplifies R4 to `author.id == bot`). | Opportunistic. |
| R10 | **Channel history cleanup — delete bridge-channel messages older than 5 hours.** Needs the bot to have **Manage Messages** (grant at invite time). Mechanism: compute the snowflake for `now − 5 h` (snowflakes encode timestamps: `(unixMs − 1420070400000) << 22`), `GET /messages?before=<cutoffSnowflake>` (paginate), skip `pinned` messages, delete the rest via `POST /channels/{id}/messages/bulk-delete` in ≤100-ID chunks (valid for anything under 14 days old; a first run on an old channel may need per-message `DELETE` for the >14-day tail). **Trigger:** the relay is request-driven (no background timers — deliberate shared-IIS decision), so the sweep piggybacks on chat POSTs / polls, throttled via a `lastCleanupUtc` timestamp in the durable state file (~every 15 min). When no client is online the sweep pauses until the next request — mitigate with a free uptime monitor pinging `/heartbeat` (which already drains the outbox), or accept the lag. No conflict with Stage 2 reads: the injection TTL (R6) is minutes, so anything 5 h old was long since delivered or dropped. | Decide: pinned messages are preserved (recommended); whether cleanup covers only the bridge channel (yes — hardcode the one channel ID). |

## Extension side (beyond the send path)

- Poll worker on the existing WinHTTP thread (or a second thread mirroring the Stage 1 worker): `GET /messages`, same `X-Relay-Key`, backoff on 429/5xx exactly as Stage 1.
- Receive-side display rewrite (see "Injector-name display" above): in the `Tab::appendText` hook, strip the injector-name prefix from marked lines before calling the original — capture/relay the untouched line first.
- Locked incoming queue → frame-tick drain → `originalParse::run` (S5), paced (S6). Decide what happens to claimed messages while zoning/not in ground scene: the frame tick doesn't fire, injection stalls, claims expire, another client picks them up — which is the system working as intended; just don't poll (or don't claim) while the tick is stale.
- `/emu discord status` additions: stage 2 on/off, pending queue depth, last poll result, last injection. `/emu discord off` drops the incoming queue too (mirror of the Stage 1 rule); unacked claims simply expire on the relay and are redelivered elsewhere.
- Test harness first, like Stage 1: compile the poll/inject path standalone, feed it synthetic relay responses (empty, N messages, 429, 5xx, malformed), point it at the R1 stub, only then go in-game.

## Bot setup checklist (R2, manual steps)

The "bot" is a credential, not a process — the relay stays the only running software and talks to Discord with plain REST calls. To create it:

1. <https://discord.com/developers/applications> → **New Application** (name it e.g. `GalaxyExtender Bridge`).
2. **Bot** tab → under *Privileged Gateway Intents* enable **Message Content Intent** (self-serve below 100 servers). Gateway intents normally apply to gateway connections, but this toggle is also what entitles the app to message `content`; verify via the test below.
3. **Bot** tab → *Reset Token* → copy the token. Treat it like the webhook URL: git-ignored `appsettings.Production.json` only, never in chat/commits.
4. Invite it: **OAuth2 → URL Generator** → scope `bot` → bot permissions **View Channels**, **Read Message History**, **Manage Messages** (for the R10 5-hour history cleanup), and **Send Messages** (for R9's webhook retirement). Open the generated URL, pick the server.
5. In Discord, make sure the bot's role can see the bridge channel (channel permission overwrites can hide it).

**The go/no-go test** — one request, no code (PowerShell; channel ID via right-click channel → Copy Channel ID with developer mode on):

```powershell
Invoke-RestMethod -Uri "https://discord.com/api/v10/channels/<CHANNEL_ID>/messages?limit=5" `
  -Headers @{ Authorization = "Bot <TOKEN>" }
```

Pass = each returned message has a non-empty `content` field, and webhook-posted messages carry `webhook_id`/`author.bot` (confirms the R4 echo filter has something to key on). If `content` comes back empty for normal user messages, the intent toggle didn't take — fix that before any relay code is written. Also note the `X-RateLimit-*` response headers while you're there; they size R3's cache window.

## Accepted gaps

- Discord message edits/deletes are ignored — what was injected stays.
- The injected line shows the injecting player as the game-level sender (`InjectorName: [Discord] bob: hi`) **to non-extension viewers only** — extension clients strip it locally (see "Injector-name display" in the marker rules). Which member's name appears in the raw line varies with who claimed; accepted, a pinned one-line explanation in guild chat covers it.
- Guild members can suppress their own lines from Discord (or spoof bridged-looking lines) by typing the marker; see marker rules.
- If no extension client is online, Discord messages older than the TTL are dropped, not queued forever.

## Proposed build order

1. **S1/S2 spike** ✅ **Done 2026-08-05** — send mechanism confirmed end-to-end (see "S1/S2 spike findings"); guild room id comes from the parse-hook cache, filled by any line typed in the guild tab.
2. **R1** ✅ **Done 2026-08-05** — claim contract pinned in Relay/README.md, stub live on the relay (77 tests). S4 limits read from the local Core3 source the same session (see "S4 findings").
3. Extension poll/inject path against the stub, harness-tested.
4. **R2** — bot + REST content verification.
5. **R3–R7** — real relay read path + sanitizer + claim/ack store, behind the pinned contract; flip `X-Relay-Stage2` to enabled.
6. **R8/R9** — load sanity, webhook retirement.
7. End-to-end: type in Discord → line appears in guild chat for a non-extension player too → confirm it does **not** echo back to Discord → kill the injecting client mid-stream and watch redelivery pick another injector.
