# Discord Chat Bridge — Plan

Status: **Stage 1 extension side is built and verified in-game. Relay Phases 2–5 (de-duplication + Discord forwarding) are implemented and tested — guild chat reaches Discord as soon as the relay is redeployed.** See [discord-relay-plan.md](discord-relay-plan.md) "Next actions".
Last updated: 2026-08-05

## Start here (handoff)

**What exists:** the relay (built, deployed, verified against the live host — see [discord-relay-plan.md](discord-relay-plan.md) and [../Relay/README.md](../Relay/README.md)) **and the Stage 1 extension side** — `DiscordBridge.h/.cpp`, the `SwgCuiChatWindowTab` hook, `/emu discord`, and the `dllmain`/`GroundScene` wiring. Both build clean (Debug and Release, Win32/x86).

**What is verified, and how.** The bridge's own translation unit was compiled into a test harness that feeds synthetic `appendText` calls through the real code path (only the Detours hook itself is stubbed out), pointed first at a locally-run copy of the relay, then at a raw TCP stub that returns chosen status codes, then at the live relay:

| Behaviour | Result |
|---|---|
| Batch shape, `POST <prefix>/api/v1/chat`, `X-Relay-Key` header | Accepted by the live relay's validator: `200 accepted=6 deduped=0` |
| `occurrence` for a line repeated in three separate frames | `1`, `2`, `3` |
| Same line twice in one frame (multi-tab case) | Collapsed to one relayed line |
| `clientSeq` | Monotonic, no gaps |
| 512-char line, 50-line batch, 32 KB body | Enforced client-side; a 900-char line arrived clamped to 512, a 120-line burst split into 50s |
| Blank-after-cleaning line | Dropped before it could fail the batch |
| `401` | One request, then latched off, queue cleared. Confirmed against the **live relay over TLS** with a deliberately wrong key |
| `429` with `Retry-After: 7` | One request, then paused 7 s |
| `500` then `200` | Retried with the **same `batchId`** |
| `400` | Batch dropped, body logged, no retry, bridge stays on and sends the next batch |
| `shutdown()` with the worker running | Returned cleanly |

**In-game verification: ✅ done (2026-08-05).** The remaining assumptions were confirmed with a real client session against the live relay:

- The Detours hook at `0x0102DA80` fires — chat lines are captured (137 console lines, plus types 1/5/9 observed).
- Guild chat **is** `ChannelId.type == 9`; `/emu discord types` flagged it `<- relayed` with a genuine guild line as the sample. No `channel_type` override needed.
- Sample format matched expectations after cleaning: `[GuildChat] lotok: <text>` — prefix present, escapes stripped.
- End-to-end send worked: `last result: 200 accepted=1 deduped=0`, 4 lines accepted in 4 batches, 0 dropped, worker running, frame counter ticking.

Reminder for anyone repeating the test: **no message appears in Discord at this stage** — the live relay is Phase 1 and does not forward (`X-Relay-Forwarding: disabled`). A `200 accepted=N` in `/emu discord status` (or the batch in the relay's `App_Data/logs`) is the success signal until Phase 3 ships.

**Relay state:** Phases 0–1 of 7 complete. **`POST /api/v1/chat` exists and works** — it authenticates, validates the full contract and returns accept counts. It does not de-duplicate (Phase 2) or forward to Discord (Phase 3) yet, and says so via the response header `X-Relay-Forwarding: disabled`. `deduped` is therefore always 0 for now; that is not a bug.

**Two corrections to be aware of if reading this plan cold** — the detail sections below have been updated, but the change is easy to miss:

1. **The extension no longer talks to Discord.** It POSTs JSON to the relay. It does **not** build embeds, does not know the webhook URL, does not handle Discord rate limits, and does not apply colours. All of that moved server-side.
2. **The relay contract adds two fields** the original design had no concept of: a per-line `occurrence` counter and a per-POST `batchId`. Both are required. See "Relay contract obligations" below.

## Goal

Bridge in-game **guild chat** with a Discord channel.

- **Stage 1** (this branch/PR): game → Discord. Guild chat lines appear in Discord as **green** embeds.
- **Stage 2** (separate branch/PR): Discord → game. Discord messages appear in the guild chat tab in **purple** (`\#RRGGBB` inline colour escape). Requires read access to Discord (bot token) — deferred.

Scope decisions made along the way:

- Guild channel only (originally "any chosen tab"). This removed the need to capture a tab/channel ID entirely — guild chat is routed client-side with the fixed enum `CT_guild`, so the hook just filters on that constant. The `/emu discord capture` command and the `m_tabId` offset hunt were dropped from the plan.
- Webhook is used for Stage 1 posting. A webhook is **write-only** — it can never read Discord messages, which is why Stage 2 needs a bot token.

## Architecture decision — where does the bridge run?

Three options were assessed:

1. **Extension → webhook direct** (zero infrastructure). Works today; duplicate prevention across multiple extension users is impossible (webhooks are stateless/write-only, no idempotency key) — mitigated socially by a "designated relay" convention (bridge ships disabled by default; one guild member enables it; embed footer `via <CharacterName>` makes rogue second relays visible).
2. **Extension → hosted relay → Discord** (RECOMMENDED end state). User has .NET/IIS web hosting. Build a small **request-driven ASP.NET Web API** (NOT a persistent-gateway bot — IIS app pool idle/recycle kills gateway connections, especially on shared hosting):
   - `POST /api/chat` — extensions send batched chat lines + shared secret; relay dedupes (message hash + few-second window) and forwards ONE copy to Discord. For Stage 1 the relay can post via the existing webhook (no bot account needed yet). Solves multi-user duplicates properly and moves the webhook secret off players' machines.
   - Stage 2: `GET /api/messages?after=<cursor>` — extension polls the relay; relay fetches new channel messages on-demand via Discord REST with the bot token (server-side only), caches last-seen message ID. Entirely request-driven → IIS recycling is harmless. At that point the webhook is redundant (bot token can post too) and can be retired.
   - Hosting checklist: ✅ all verified against the live host 2026-08-05 (.NET 8 supported, outbound HTTPS to discord.com reachable, valid TLS cert, `App_Data` writable, single worker process). Details in [discord-relay-plan.md](discord-relay-plan.md).
3. **Core3 server-side bridge** — architecturally strongest for pure logging (server sees all guild chat authoritatively, zero clients involved, zero duplicates) but a different codebase/PR. Noted, not pursued for now.

**Decided (2026-08-05): relay first.** Option 2 is built before the extension ships, and the extension points at the relay from day one — webhook-direct mode is not shipped. This kills the duplicate problem immediately and makes Stage 2 a config change. Extension-side code is nearly identical either way — `DiscordBridge` batches lines and POSTs JSON to a configured HTTPS endpoint; only URL + payload shape differ. Relay lives in this repo under `Relay/` with its own solution, targets .NET 8 LTS, hosted on IIS shared hosting. See [discord-relay-plan.md](discord-relay-plan.md) for the full design — note it adds two small requirements to `DiscordBridge` (per-line `occurrence` counter, per-POST `batchId`).

## Stage 1 implementation — as built (branch `GuildChat`)

The plan below is what was implemented. Deviations and decisions taken during implementation:

- **`/emu discord types`** was added beyond the planned four subcommands. The plan called for debug-logging `ChannelId.type` to verify `CT_guild`; a command that reports every observed type with a sample line makes that a one-step in-game check instead of a DebugView session, and `channel_type` in the ini lets a wrong guess be corrected without a rebuild.
- **Character name is not read from the client.** There is no name accessor in the project and finding one is a fresh address hunt. `client.id` defaults to the sanitised machine name and `character`/`galaxy` come from the ini — all three are self-reported labels the relay uses only for logging, so nothing is lost.
- **Local dedupe also compares a tick count** (same text AND same frame AND within 200 ms), not just `(text, frame)`. `GroundScene::parseMessages` only ticks in the ground scene, so a frozen frame counter would otherwise suppress genuine repeats indefinitely. Duplicate `appendText` calls for one message happen microseconds apart, so the extra condition never merges distinct messages.
- **Initialisation is deferred out of `DllMain`.** `DiscordBridge::initialize` only creates the critical section and stores the module handle; the ini read and `CreateThread` happen on the first frame, because neither is safe under the loader lock.
- **Shutdown waits on a "worker finished" event, not the thread handle.** Waiting for a thread to *exit* inside `DllMain` deadlocks on the loader lock via `DLL_THREAD_DETACH`. On process exit (`lpReserved != nullptr`) the worker has already been terminated — possibly mid-hold on the bridge lock — so shutdown touches nothing at all: no lock, no waits, no frees. On `FreeLibrary` the hook is detached *before* the bridge tears down, so no new chat line can enter it. If the bounded 2 s wait ever expires, resources are deliberately leaked rather than freed under a still-running worker.
- **Text cleaning maps control characters to spaces** rather than deleting them. Either is fine for Discord; what matters is that it is deterministic, since two clients must produce byte-identical text or the relay's dedupe hash misses.
- Retries are capped at 6 attempts, the outbound queue at 500 lines (oldest dropped), so a relay outage cannot grow memory without bound or wedge the queue forever. Only transport errors and 5xx count toward the attempt cap — a 429 is the relay pacing us, not a failure. Server-supplied backoff (`Retry-After`, `retryAfterMs`) is capped at 15 minutes so a misbehaving response cannot silently pause the bridge for hours.
- **`/emu discord off` discards the in-flight batch too**, not just the queue — the worker drops any batch it had already taken out of the queue, so nothing survives an off/on cycle.

### Known caveat found during implementation

**Profanity filtering breaks cross-client dedupe.** Guild lines are built by `colorAndFilterText(text, TT_guild, profanityFiltered)`, so a relaying client with the filter on sends `****` where one with it off sends the word. The relay sees two different strings and posts both. Nothing can be done client-side; it only affects lines containing filtered words, and only when relaying members' filter settings differ.

1. **`DiscordBridge.h/.cpp`** (new):
   - Config from `DiscordBridge.ini` beside the DLL (git-ignored — already added to `.gitignore`):
     - `endpoint` — relay base URL, e.g. `https://mesanderson.co.uk/relay` (note the path prefix; the relay is an IIS application in a subfolder).
     - `key` — the shared `X-Relay-Key` value, handed out by the relay operator.
     - `enabled` — can default to **1**. The designated-relay convention is obsolete: the relay de-duplicates centrally, so several members running the bridge is now a resilience feature rather than a duplicate-message problem.
     - `client_id` / optional character-name override — cosmetic only, used for relay logs.
   - Thread-safe outbound queue (CRITICAL_SECTION), worker thread using **WinHTTP** (TLS works fine from the 32-bit injected process; the relay endpoint has a valid cert, verified).
   - Batch queued lines every ~1.5 s into one POST to `<endpoint>/api/v1/chat` with header `X-Relay-Key`. **Payload is the relay contract, not a Discord webhook body** — see "Relay contract obligations".
   - Helpers: strip SWG escapes (`\#RRGGBB`, `\#.`, `\>NNN`), UTF-16 → UTF-8. The relay strips again defensively, but sending clean text keeps the dedupe hash stable across clients, which matters — two clients must produce byte-identical text for the same line or de-duplication silently fails.
   - Handle HTTP 401 (bad key — log once, stop retrying), 429 (honour `Retry-After`), 5xx (retry with the same `batchId`).
   - Clean shutdown on DLL detach (signal + join worker).
2. **`SwgCuiChatWindowTab.h`** (new wrapper): `DEFINE_HOOK(0x0102DA80, appendTextHook, ...)` on `SwgCuiChatWindow::Tab::appendText(const ChannelId&, const Unicode::String&)` (`__thiscall`, this = Tab).
   - Filter: `ChannelId.type` (int at arg1 + 0x0) `== CT_guild` (9 — verify at runtime, see below).
   - **Local dedupe**: same guild message is appended once per tab/window that shows the guild channel, all within one dispatch. Keep `(last relayed string, frame counter)` — frame counter incremented by the existing `GroundScene::parseMessages` hook — and skip identical string in the same frame. Genuine repeats arrive in later frames and still relay.
   - Clean + enqueue, then always run the original.
3. **`EmuCommandParser.cpp`**: `/emu discord on|off|status|test` + help text (no `capture` — not needed for guild). Shipped with `types` as well.
4. **`dllmain.cpp`**: `ATTACH_HOOK`/`DETACH_HOOK` for the tab hook; bridge init/shutdown. The frame tick is added in `GroundScene::parseMessages`.
5. **Dev-time verifications** (first run) — ✅ **done 2026-08-05**, see "Start here":
   - Confirmed `CT_guild == 9` in our diverged binary via `/emu discord types` in a live session.
   - Confirmed guild line formatting (`[GuildChat] name: text` after cleaning) from the sample `types` printed.
6. **Docs**: ✅ README user-facing command docs and ini template, `ARCHITECTURE.md` address table, `DiscordBridge.ini` in `.gitignore`.

## Relay contract obligations

Full contract in [../Relay/README.md](../Relay/README.md). What the extension must produce:

```jsonc
POST <endpoint>/api/v1/chat        Header: X-Relay-Key: <key>
{
  "batchId": "8f2c...",            // GUID, REUSED on retry of the same batch
  "client":  { "id": "kaelen", "character": "Kaelen", "galaxy": "Basilisk" },
  "lines": [
    { "text": "Kaelen: anyone up for a Krayt run?", "occurrence": 1, "clientSeq": 412 }
  ]
}
```

- **`occurrence`** — how many times this client has seen this *exact* line in the last 60 s, including this one. Requires a rolling 60 s history of relayed lines in `DiscordBridge`. This is what lets a genuine repeat through while still collapsing the same line arriving from several guild members: every client watches the same stream, so all of them label the first "lol" as `1` and the second as `2`. Without it, the relay's dedupe window silently eats real repeats.
- **`batchId`** — a GUID per POST, **reused unchanged if the POST is retried** after a timeout or 5xx. This is what makes retries idempotent; a fresh GUID on retry double-posts.
- **`clientSeq`** — monotonic per client, for ordering and debugging only.
- Limits enforced by the relay: 50 lines per batch, 512 chars per line, 32 KB body.

### Known caveats (accepted)

- The hook only sees text that reaches a chat tab → the relaying player must have the guild channel in at least one tab (default UI includes it).
- ~~Webhook-direct duplicate problem~~ — resolved by the relay; central de-duplication means multiple relaying members are harmless.
- Local per-frame dedupe is still needed (item 2 above): one client with the guild channel in several tabs/windows produces several `appendText` calls for one message, within a single dispatch. That is a *client-side* duplicate the relay cannot distinguish from a genuine repeat, so it must be collapsed before `occurrence` is computed.
- Guild lines pass through the client's profanity filter before our hook, so relaying members with different filter settings produce different text for the same message and the relay's dedupe misses it. See "Known caveat found during implementation" above.
- ~~The webhook URL was shared in plaintext during planning~~ — regenerated 2026-08-05. It now lives only in the relay's git-ignored `appsettings.Production.json` on the host; the extension never sees it.

## Stage 2 sketch (separate PR)

The relay now exists, and `GET /api/v1/messages?after=<cursor>` ships as a stub returning an empty list with header `X-Relay-Stage2: disabled` — so the extension's polling path can be written and exercised against the real endpoint before any bot token exists.

- Extension polls relay for new Discord messages (few-second cadence, from the existing per-frame hook via a timer).
- **Polling cost is worth thinking about before committing to it.** Every player displaying Discord messages locally must poll: ~10 players at 3 s is ~8.6M requests/month. Two cheaper shapes exist — inject into the *real* guild room so only one poller is needed and everyone sees it via normal chat, or long-poll (~30 s held requests, ~290k/month). Long-polling needs a relay process that stays alive, which the current shared-IIS hosting does not guarantee; see the hosting trade-off recorded in [discord-relay-plan.md](discord-relay-plan.md).
- Inject into the guild chat tab prefixed with purple `\#RRGGBB` escape. Decide: local-display only vs. sending into the actual guild room via the server (affects all players; colour codes must survive Core3 round-trip — investigate `ChatManagerImplementation.cpp` filtering, message length limits, flood throttling).
- Bot token lives ONLY on the relay server. Bot can post directly (retire webhook) and read via REST — no gateway connection needed.
- Proper multi-relay dedupe moves into the relay.

## Original investigation plan + status

| Step | Topic | Status |
|------|-------|--------|
| 1 | Client receive path & tab/channel model (client-tools fork) | ✅ Done — see [discord-bridge-research.md](discord-bridge-research.md) |
| 2 | Locate hook point in our binary | ✅ Done — `Tab::appendText` @ `0x0102DA80`, triple-verified |
| 3 | Client send path (for Stage 2 upstream) | ⏸ Deferred to Stage 2 |
| 4 | Core3 server behaviour (colour passthrough, limits) | ⏸ Deferred to Stage 2 (only matters for injecting into the room) |
| 5 | Colour rendering (escape syntax) | ✅ Done — `\#RRGGBB` / `\#.` reset / `\>NNN` indent |
| 6 | HTTP + threading from the DLL | ✅ Design settled (WinHTTP worker + queue) |
| 7 | Config & capture UX | ✅ Simplified — ini only, no capture needed for guild |
