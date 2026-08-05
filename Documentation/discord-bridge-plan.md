# Discord Chat Bridge — Plan

Status: **investigation complete for Stage 1 — ready to implement**
Last updated: 2026-08-05

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
   - Hosting checklist to verify: ASP.NET Core vs .NET Framework support; outbound HTTPS to discord.com allowed; valid TLS cert on the hostname (extension WinHTTP verifies); writable persistence (App_Data JSON file is enough) for dedupe window + message cursor across app-pool recycles.
3. **Core3 server-side bridge** — architecturally strongest for pure logging (server sees all guild chat authoritatively, zero clients involved, zero duplicates) but a different codebase/PR. Noted, not pursued for now.

**Decided (2026-08-05): relay first.** Option 2 is built before the extension ships, and the extension points at the relay from day one — webhook-direct mode is not shipped. This kills the duplicate problem immediately and makes Stage 2 a config change. Extension-side code is nearly identical either way — `DiscordBridge` batches lines and POSTs JSON to a configured HTTPS endpoint; only URL + payload shape differ. Relay lives in this repo under `Relay/` with its own solution, targets .NET 8 LTS, hosted on IIS shared hosting. See [discord-relay-plan.md](discord-relay-plan.md) for the full design — note it adds two small requirements to `DiscordBridge` (per-line `occurrence` counter, per-POST `batchId`).

## Stage 1 implementation plan (branch `feature/discord-chat-bridge`)

1. **`DiscordBridge.h/.cpp`** (new):
   - Config from `DiscordBridge.ini` beside the DLL (git-ignored): endpoint URL (webhook or relay), `enabled` (default **0** — designated-relay model), optional footer name override.
   - Thread-safe outbound queue (CRITICAL_SECTION), worker thread using **WinHTTP** (TLS to discord.com works fine from the 32-bit injected process).
   - Batch queued lines every ~1.5 s into one webhook POST: embed, colour green `0x2ECC71` (3066993), footer `via <CharacterName>`. Respect webhook rate limit (~5 req / 2 s), back off on HTTP 429.
   - Helpers: strip SWG escapes (`\#RRGGBB`, `\#.`, `\>NNN`), UTF-16 → UTF-8.
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
6. **Docs**: README, `ARCHITECTURE.md` address table; add `DiscordBridge.ini` to `.gitignore`.

### Known caveats (accepted)

- The hook only sees text that reaches a chat tab → the relaying player must have the guild channel in at least one tab (default UI includes it).
- Webhook-direct mode cannot prevent duplicates if two people enable the bridge — designated-relay convention + `via <name>` footer until the relay exists.
- The webhook URL was shared in plaintext during planning — **regenerate it in Discord** once the ini-based config is in place; never commit it.

## Stage 2 sketch (separate PR, after relay exists)

- Extension polls relay for new Discord messages (few-second cadence, from the existing per-frame hook via a timer).
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
