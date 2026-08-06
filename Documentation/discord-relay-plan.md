# Discord Relay — Implementation Plan (.NET 8)

Status: **All phases built and deployed; the full bridge is live in both directions (2026-08-06).** Stage 1 forwarding (game → Discord) confirmed 2026-08-05; the Stage 2 read path (R3–R7, Discord → game, 143 tests) deployed and confirmed end-to-end with multiple users 2026-08-06 — see [discord-stage2-plan.md](discord-stage2-plan.md). Remaining here: the Phase 6 durability checks below (state file survives recycle/redeploy, `stdoutLogEnabled` off), R8 poll-load sanity, and the optional R9/R10 bot work from the Stage 2 plan.
Last updated: 2026-08-05

Deployed at `https://mesanderson.co.uk/relay` (subfolder registered as an IIS application). Confirmed from `/api/v1/health` on 2026-08-05:

| Fact | Value |
|---|---|
| Runtime | .NET 8.0.29, Windows Server 2019 (10.0.17763) |
| Hosting | in-process, dedicated IIS app pool, **1 worker process** |
| TLS | `isHttps: true` reported correctly → `RequireHttps` enabled |
| `App_Data` | writable, file logging active |
| Config delivery | `appsettings.Production.json` (git-ignored, not part of the publish output, so it survives redeploys) — webhook + API key both loaded |

| Outbound to Discord | ✅ `reachable: true`, HTTP 200 in 194 ms from the app pool |
| Worker process | ✅ `process.id` identical across readings 4 minutes apart (50400), `startedUtc` unchanged, `uptimeSeconds` 0 → 240 |

**Consequences:** the app pool makes outbound HTTPS calls, so the relay approach is viable end to end. The single stable pid confirms the Plesk `maxProcesses = 1` setting, so `FileStateStore` needs no cross-process mutex.

**Not proven:** idle-stop behaviour. Four minutes is well inside a typical 20-minute idle timeout, so state must still be durable on disk — that constraint stands.
Code: [../Relay/](../Relay/) — see [../Relay/README.md](../Relay/README.md) for the operational reference.
Companion to [discord-bridge-plan.md](discord-bridge-plan.md) and [discord-bridge-research.md](discord-bridge-research.md).

## Purpose

The relay is the piece that makes the bridge correct rather than merely working. It exists to:

1. **De-duplicate** — several guild members may run the extension; every one of them sees the same guild line and would POST it. The relay collapses those to exactly one Discord post.
2. **Hold the credentials** — the Discord webhook URL (Stage 1) and later the bot token (Stage 2) live on the server, never on players' machines.
3. **Become the Stage 2 read path** — the extension polls the relay; the relay talks to Discord's REST API. No gateway connection, so IIS recycling is harmless.

Decision from the parent plan's "Open decision": **relay first**. The extension points at the relay from day one; webhook-direct mode is not shipped.

## Hosting decision (2026-08-05)

**Decided: the existing IIS shared hosting.** Alternatives were priced before committing:

| Option | Cost/mo | Long-lived process? |
|---|---|---|
| Existing IIS shared hosting ← **chosen** | £0 marginal | No |
| Azure App Service **Linux** F1 / B1 | £0 / ~£10 (+VAT) | No / Yes |
| Azure App Service **Windows** B1 | ~£52 | Yes |
| Azure Container Apps (min-replica 1) | ~£10–14 | Yes |
| VPS (Hetzner-class) or Railway/Render/Fly | ~£4–6 | Yes |

(Azure figures from the retail prices API, UK South, GBP, VAT-exclusive. Note Windows App Service is ~5× Linux for identical specs — if this is ever revisited, Linux is the only sensible Azure choice, and the relay has no Windows-specific dependencies.)

What the £0 buys and what it costs: a paid always-on host would let us delete the mutex-guarded state store and the durable outbox in favour of in-memory state plus a background retry loop, and would unlock two Stage 2 improvements — a persistent Discord **gateway** connection (instant inbound instead of REST cursor polling) and **long-polling** from the extension (~290k requests/month at 10 players instead of ~8.6M). Those are deliberately given up. The design below absorbs the cost instead; it is more complex than it would otherwise need to be, and that is the accepted trade.

## Constraints that shape the design

Hosting is **IIS shared hosting**, which is the single biggest design driver:

| Constraint | Consequence |
|---|---|
| App pool idle-stops and recycles on its own schedule | **No background services, no timers, no in-memory-only state.** Everything happens inside a request; all state that matters is durable. |
| ~~Possible web garden~~ — **resolved 2026-08-05**: dedicated IIS application pool enabled with **1 worker process** | Cross-process safety is no longer required, so the state store drops the named mutex and becomes a plain read-modify-write behind a normal in-process lock. Confirm empirically after deploy: `/api/v1/health` → `process.id` stable across calls. Keep the `IStateStore` seam so this can be reinstated if the pool config ever changes. |
| Runtime version is whatever the host installed | Target **net8.0** — ✅ confirmed supported by the host (2026-08-05). Self-contained publish remains the fallback if the hosting bundle turns out to be absent in practice. |
| Shared box, unknown neighbours | Small footprint, no SQL Server dependency, strict request limits. |

Everything below follows from those four rows.

## Wire contract

Base path `/api/v1`. HTTPS only. Auth on every endpoint: header `X-Relay-Key: <secret>`, compared in fixed time against the set of configured secrets.

**Key model.** Keys are generated by the operator and handed out — normally **one shared key** for the whole guild (`Relay__ApiKeys__guild`), so adding a guild member needs no config change. The config is a `label -> secret` map rather than a single string purely to allow **rotation without a flag day** (add the replacement, distribute, delete the old) and optional per-person revocation later. The label is a log-facing name; clients never send it. `client.id` / `client.character` in the body are self-reported labels, **not** authentication.

### `POST /api/v1/chat` — Stage 1, game → Discord

```jsonc
{
  "batchId": "8f2c...",            // GUID, stable across client retries → idempotency
  "client":  { "id": "kaelen", "character": "Kaelen", "galaxy": "Basilisk" },
  "lines": [
    { "text": "Kaelen: anyone up for a Krayt run?", "occurrence": 1, "clientSeq": 412 },
    { "text": "Tarn: give me 10 min",               "occurrence": 1, "clientSeq": 413 }
  ]
}
```

```jsonc
// 200 OK
{ "accepted": 1, "deduped": 1, "queued": 0, "retryAfterMs": null }
```

- `text` — already escape-stripped and UTF-8 by the extension; the relay strips again defensively.
- `occurrence` — **how many times the client has seen this exact line in the last 60 s, including this one.** This is what lets a genuine repeat through while still collapsing cross-client duplicates: every client independently counts the same stream, so all of them label the first "lol" as `1` and the second as `2`. Without it, a 15 s dedupe window would silently swallow real repeats.
- `clientSeq` — monotonic per client, ordering + debugging only.
- Status codes: `401` bad key, `413` oversize, `429` rate-limited (with `Retry-After`), `503` webhook not configured.

### `GET /api/v1/messages?client=<id>` — Stage 2 work queue (stub shipped)

**Contract revised under Stage 2's claim semantics — a work-queue consume, not a broadcast read** (the `after` cursor was dropped: the relay owns queue position, a client cursor would fight redelivery). A 200 claims the returned messages (≤5, oldest first) for this key+client until a 60 s redelivery timeout, max 2 redeliveries, then dropped and counted in the response's `dropped` field. The pinned contract lives in [../Relay/README.md](../Relay/README.md); the R1 stub ships it returning `{ "messages": [], "dropped": 0 }` plus a `X-Relay-Stage2: disabled` header, so the extension's polling code can be written and exercised against the real endpoint before the bot token exists.

### `GET /api/v1/health`

Unauthenticated, returns version, uptime, last successful Discord post, outbox depth, dedupe entry count. This is also the endpoint an external pinger hits if cold starts prove annoying.

### `POST /api/v1/heartbeat`

Authenticated no-op that flushes the outbox and keeps the app pool warm. Cheap insurance given idle-stop.

## Internal design

```
Request ──► auth ──► rate limit ──► ChatEndpoint
                                       │
                     ┌─────────────────┼──────────────────┐
                     ▼                 ▼                  ▼
              StateStore          TextSanitizer      DiscordPublisher
          (file + named mutex)   strip escapes,      HttpClientFactory,
          dedupe / batchIds /    neutralise pings,   429 aware
          cursor / outbox        escape markdown,           │
                                 split at 4096             ▼
                                                       Outbox (durable)
                                                    drained opportunistically
                                                    at the start of every request
```

### StateStore — the crux

Single JSON document under `App_Data/relay-state.json`. Read-modify-write per mutating request, atomic replace (write temp → `File.Move` with overwrite), under an in-process lock. Traffic is a handful of chat lines per second at worst, so the simplicity is worth more than the IO.

The pool is configured with a single worker process (confirmed 2026-08-05), so a `Global\` named mutex is **not** needed — but the file must still be durable, because the pool idle-stops and recycles. If the pool ever gains workers, reinstate the mutex inside `FileStateStore`; nothing outside `IStateStore` should care.

Contents:

- `dedupe: [{ key, firstSeenUtc, firstSeenBy }]` — key = `sha256(normalisedText)[0..16] + ":" + occurrence`. Entries older than `DedupeWindowSeconds` (default **15**) are pruned on every touch. First arrival wins and is forwarded; later arrivals return `deduped++`.
- `batchIds: [{ id, seenUtc, response }]` — 5 min window. A client retry after a timeout replays the stored response instead of double-posting.
- `outbox: [{ id, payload, attempts, notBeforeUtc }]` — Discord posts that have not landed yet.
- `stage2Cursor` — last-seen Discord message id, for later.

Escape hatch if volume ever outgrows this: swap the implementation for SQLite (`Microsoft.Data.Sqlite`, WAL) behind the same `IStateStore` interface. Design for it, don't build it.

### TextSanitizer

Order matters, and every step is a test case:

1. Strip SWG escapes — `\#` + (6 hex | `.`), `\>` + 3 digits (patterns confirmed in the research doc).
2. Neutralise mentions — belt and braces: send `"allowed_mentions": { "parse": [] }` on every webhook POST **and** rewrite `@everyone` / `@here` to use a zero-width joiner. Guild chat is player-authored text; nobody gets to mass-ping Discord through it.
3. Escape Discord markdown (`` ` ``, `*`, `_`, `~`, `|`, `>` at line start) so `_underscored_` names render literally.
4. Clamp per line (default 512 chars) and split the joined description at 4096, emitting additional embeds/posts as needed.

### DiscordPublisher

`IHttpClientFactory`-registered client, webhook URL from configuration. Green embed `color: 3066993` (`0x2ECC71`) per the research doc. No `via <name>` footer in relay mode — the relay is the author of record now; the contributing client id goes into logs and, behind a config flag, an embed field for debugging.

429 handling, given there is no background worker: honour `retry_after` with **one** bounded in-request retry (cap ~2 s), and on continued failure park the payload in the durable outbox. Every subsequent request (chat POST, heartbeat, Stage 2 poll) drains the outbox first, oldest first, respecting `notBeforeUtc`. Discord's webhook limit is ~5 requests / 2 s, and the extension already batches on a 1.5 s cadence, so this should be a rare path — but on shared hosting the outbox is the only honest way to not lose lines.

### Security posture

- HTTPS only + HSTS; reject plaintext.
- Fixed-time key comparison (`CryptographicOperations.FixedTimeEquals`) against every configured secret — iterate all entries rather than short-circuiting on first match, so response time does not leak which key matched. Keys never logged; log the matched label instead.
- Built-in ASP.NET Core rate limiter, partitioned per key (e.g. 60 req/min, burst 10). Resets on recycle — acceptable, it is abuse mitigation not billing.
- `MaxRequestBodySize` 32 KB, max 50 lines/batch, both enforced before parsing.
- No trust in client-supplied `character`/`galaxy` beyond display and logging; treat as hostile text.
- Secrets: `appsettings.Production.json` outside git, or environment variables via the host's control panel. `dotnet user-secrets` for local dev.

## Project layout

```
Relay/
  GalaxyExtender.Relay.sln
  README.md                          ← wire contract, the C++ side codes against this
  src/GalaxyExtender.Relay/
    Program.cs                       ← minimal API, DI, middleware
    Endpoints/{Chat,Messages,Health}Endpoints.cs
    Contracts/{ChatBatchRequest,ChatLine,ChatBatchResponse,MessagesResponse}.cs
    Services/{IStateStore,FileStateStore,DedupeService,TextSanitizer,DiscordPublisher,Outbox,ApiKeyValidator}.cs
    Options/{RelayOptions,DiscordOptions}.cs
    App_Data/.gitkeep                ← state + logs land here (git-ignored contents)
    web.config                       ← AspNetCoreModuleV2, hostingModel="inprocess"
    appsettings.json
  tests/GalaxyExtender.Relay.Tests/
```

Separate `.sln` from `SWGCommandExtension.sln` — the C++ solution stays untouched, but both sides of the protocol are versioned together in one repo, so a contract change is one commit.

Minimal dependency set: framework only, plus `Serilog.AspNetCore` + `Serilog.Sinks.File` for file logging (console logging is invisible under IIS). For bring-up, enable the ASP.NET Core Module's `stdoutLogEnabled` temporarily, then turn it off.

## Phases

| # | Phase | Deliverable | Est. |
|---|---|---|---|
| 0 | Scaffold + **deploy spike** | ✅ **Done.** Solution, project, `web.config`, `/health` + `/health/outbound`, Serilog to `App_Data/logs`, 7 tests. Live on the host over HTTPS. Two defects found and fixed by doing this first: logging failure could kill startup (Serilog opens the file at `CreateLogger()`, outside the guard), and `StartedUtc` initialised lazily on first request so uptime was meaningless. | 1–2 h |
| 1 | Contract + auth | ✅ **Done.** DTOs, `ApiKeyValidator` (match-any, SHA-256 digests, non-short-circuiting), path-prefix auth middleware that fails closed, per-key fixed-window rate limiter, full contract validation, `POST /api/v1/chat` returning counts with `X-Relay-Forwarding: disabled`. 32 tests. | 1–2 h |
| 2 | State + dedupe | ✅ **Done.** `FileStateStore` (in-process lock per the single-worker finding, atomic replace, pruning, corrupt-file recovery); `DedupeService` with `occurrence` keys and `batchId` idempotency. State survives a simulated recycle in tests. | 2–3 h |
| 3 | Sanitize + publish | ✅ **Done.** `TextSanitizer` (normalise-for-hash vs display-for-Discord kept strictly separate), `DiscordPublisher`, embed split at 4096, `allowed_mentions: {parse: []}` + zero-width-joiner rewrite. Unconfigured webhook → `503` before any state mutation. | 2 h |
| 4 | Outbox + 429 | ✅ **Done.** Durable outbox in the state file, opportunistic drain (chat POST + heartbeat, ≤5 entries/request, stop on first failure), one bounded ≤2 s in-request retry on 429, exponential backoff capped 300 s, drop after `OutboxMaxAttempts` with an error log. `POST /api/v1/heartbeat` returns outbox depth. | 1–2 h |
| 5 | Tests | ✅ **Done.** 65 total. `FakeDiscordHandler` records payloads and scripts 429s; covers the named cases below plus sanitizer and state-store units. | 2–3 h |
| 6 | Harden deploy | Prove survival across an app-pool recycle and across a redeploy (state file must not be wiped); confirm outbound HTTPS to discord.com; turn off `stdoutLogEnabled`. | 1–2 h |
| 7 | Stage 2 stubs | ✅ **Done (2026-08-05).** Claim contract pinned in Relay/README.md (consume semantics, no cursor, `dropped` count, 60 s redelivery, ≤5/poll); `GET /messages` stub authenticated + rate-limited like /chat, answers empty + `X-Relay-Stage2: disabled`. 77 tests. | 1 h |

Roughly **1.5–2 days**. Phases 0–3 are the minimum that puts guild chat in Discord with correct de-dup; 4–6 are what make it trustworthy unattended.

**Phase 0 is a gate, not a formality.** The one assumption that can invalidate the whole plan is whether the host will run an ASP.NET Core 8 app at all, so a hello-world deploy happens before any relay logic is written. If it fails, the fallbacks (self-contained `win-x86` publish, then a .NET Framework 4.8 port) get chosen while nothing has been built on top of the wrong assumption.

### Next actions (as of 2026-08-06)

1. ✅ ~~Redeploy `Relay/publish/`~~ — done; forwarding live 2026-08-05, Stage 2 read path live 2026-08-06.
2. **Phase 6 checks** (still open): `/health` → `relay.lastForwardUtc` set and `relay.outboxDepth` 0; confirm `relay-state.json` survives an app-pool recycle; redeploy once more and confirm the state file is not wiped; turn off `stdoutLogEnabled`.
3. ✅ ~~Phase 7~~ — done 2026-08-05 (stub), superseded by the real R3–R7 read path 2026-08-06.
4. **R8** — poll-load sanity against the Plesk request quotas (every client polls at 5 s ≈ 17k req/day/player); **R9** — optional bot posting / webhook retirement (see the Stage 2 plan).
5. ✅ ~~R10~~ — channel-history cleanup built 2026-08-06 (`ChannelCleaner`); goes live when `Discord:CleanupEnabled: true` is added to the host's `appsettings.Production.json` and `Relay/publish/` is redeployed. See README.md "Channel-history cleanup".

### Test cases worth naming up front

- Two clients POST the same line → one Discord post, second response reports `deduped: 1`.
- Same line twice from the same player → `occurrence` 1 then 2 → **two** posts (the case a naive time-window dedupe breaks).
- State survives a simulated recycle: dispose the store, reload from file, dedupe still holds.
- Retried `batchId` after a timeout → no second post, identical response replayed.
- Discord returns 429 → payload parked in outbox → next request drains it, nothing lost.
- `@everyone`, backticks, and a 5 000-char line all survive sanitisation harmlessly.
- Bad/absent key → 401, no state mutation, no log of the key.

## Hosting checklist (verify on the actual host before phase 6)

Most of this is now **self-answering**: `GET /api/v1/health` and `/api/v1/health/outbound` were built in Phase 0 specifically to report it off the deployed instance. Deploy, then call `/health` twice a few minutes apart. Note `/health/outbound` requires the `X-Relay-Key` header (it makes the host call discord.com, so it is not left open to anonymous hammering); the base `/health` stays unauthenticated for uptime pings.

| # | Question | How to check |
|---|---|---|
| 1 | .NET 8 runtime + ASP.NET Core Module V2 | ✅ host confirms .NET 8; `/health` → `process.framework` proves which runtime actually loaded |
| 2 | Outbound HTTPS to discord.com | `/health/outbound` → `discord.reachable` |
| 3 | Valid TLS cert on the hostname | Browser/curl the site — the extension's WinHTTP call verifies it, and a self-signed cert fails in a way that is awkward to diagnose from inside the DLL |
| 4 | `App_Data` writable, and not wiped on deploy | `/health` → `storage.appDataWritable`; for the wipe question, deploy twice and see if the log files survive |
| 5 | App pool `maxProcesses` and idle timeout | `/health` → `process.id` changing across calls = recycle or web garden; `process.uptimeSeconds` resetting = idle-stopping. **If the pid proves stable, the cross-process mutex on the state store can be dropped** — worth checking before building Phase 2 |
| 6 | Env vars / config outside the deployed tree | Control panel; `/health` → `config.discordConfigured` confirms whatever you set actually reached the app |

**Fallbacks if 1 fails:** self-contained publish (`-r win-x86 --self-contained`, `web.config processPath` pointed at the produced exe) removes the runtime dependency entirely; if the host forbids that too, the last resort is a .NET Framework 4.8 Web API port — same design, different plumbing.

## Impact on the extension-side Stage 1 plan

The relay's contract adds two requirements to `DiscordBridge` in [discord-bridge-plan.md](discord-bridge-plan.md):

- Track a 60 s rolling history of relayed lines to compute `occurrence` per line.
- Generate a `batchId` GUID per POST and reuse it on retry after a network failure.

Otherwise unchanged: URL + `X-Relay-Key` come from `DiscordBridge.ini`, and the payload is the JSON above instead of a Discord webhook body. `enabled` can now default to **1** — central de-dup makes the designated-relay convention unnecessary, which was the whole point.
