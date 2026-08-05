# Stage 2 poll/inject harness

Compiles the **real** `DiscordBridge.cpp` into a standalone x86 exe with the game seams
stubbed (`CuiChatParser::hasCachedRoomId`/`injectRoom` record calls instead of touching the
client; `soe::unicode`'s ctor is link-only and never executed). Mirrors the Stage 1 harness
approach described in `Documentation/discord-bridge-plan.md`.

Build: `build.bat` (adjust the vcvars32 path to your VS edition). Artifacts (`*.exe`, `*.obj`,
the generated `DiscordBridge.ini`) are git-ignored.

Two modes:

- `stage2_harness.exe` — unit tests for `parseMessagesResponse` (escapes, surrogate pairs,
  malformed/truncated bodies) and `rewriteMarkedLine` (sender-prefix stripping), then a live
  poll/inject loop against a scripted local HTTP stub: claim gates (no room id / stale frame
  tick), two-message claim with paced injection, idle, 429 + Retry-After, 500 backoff,
  malformed 200, relay-reported drops, `X-Relay-Stage2: disabled`, 404 fault latch + recovery,
  `/emu discord off` discarding claimed messages, 401 latch + recovery. 56 checks.

- `stage2_harness.exe live <path-to-real-DiscordBridge.ini>` — copies the real ini beside the
  exe (deleted afterwards), polls the LIVE relay's `/messages` stub once and prints the bridge
  status. Expected while relay R3 is unbuilt: `stage 2 disabled on the relay`.
