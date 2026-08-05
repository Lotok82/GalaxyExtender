# Discord Chat Bridge — Plan

Status: **relay is built and live; extension side not started — this is the next piece of work.**
Last updated: 2026-08-05

## Start here (handoff)

**What exists:** the relay. Built, tested, deployed and verified against the live host — see [discord-relay-plan.md](discord-relay-plan.md) and [../Relay/README.md](../Relay/README.md). Commits `da8bf3c` (scaffold) and `eeb1ac8` (fixes + host verification) on branch `GuildChat`.

**What does not exist:** any extension-side code. No `DiscordBridge.*`, no chat hook, no `/emu discord` command. Nothing in the C++ project has been touched for this feature.

**Relay state:** Phases 0–1 of 7 complete. **`POST /api/v1/chat` exists and works** — it authenticates, validates the full contract and returns accept counts. It does not de-duplicate (Phase 2) or forward to Discord (Phase 3) yet, and says so via the response header `X-Relay-Forwarding: disabled`. That is ideal for extension development: you get real 401/400/429/200 responses from the live relay, with nothing able to reach the Discord channel while you iterate.

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

## Stage 1 implementation plan (branch `feature/discord-chat-bridge`)

1. **`DiscordBridge.h/.cpp`** (new):
   - Config from `DiscordBridge.ini` beside the DLL (git-ignored — already added to `.gitignore`):
     - `endpoint` — relay base URL, e.g. `https://example.invalid/relay` (note the path prefix; the relay is an IIS application in a subfolder).
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
3. **`EmuCommandParser.cpp`**: `/emu discord on|off|status|test` + help text (no `capture` — not needed for guild).
4. **`dllmain.cpp`**: `ATTACH_HOOK`/`DETACH_HOOK` for the tab hook; bridge init/shutdown.
5. **Dev-time verifications** (first run):
   - Confirm `CT_guild == 9` in our diverged binary (debug-log `ChannelId.type` while guild chat flows).
   - Confirm guild line formatting matches expectations (`prefix + Name\>032: text + \>000`, colour escapes embedded, no timestamp at hook time).
6. **Docs**: README user-facing command docs. ✅ `ARCHITECTURE.md` address table and `DiscordBridge.ini` in `.gitignore` are both done.

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
