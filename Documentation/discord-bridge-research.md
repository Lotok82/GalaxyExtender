# Discord Chat Bridge — Research Findings

Companion to [discord-bridge-plan.md](discord-bridge-plan.md). Everything below was gathered 2026-08-05 from the forked client source (`D:\Galaxies\SWGClient\client-tools`) and verified against our actual client binary where noted.

## Binary facts (SWGEmu.exe)

The binary we inject into is `D:\Galaxies\SWGEmu_Clone\SWGEmu.exe` (22,061,142 bytes / `0x150A056`). Identical copy in `SWGEmu_Clean`. (ARCHITECTURE.md calls it SwgClient_r.exe — same client, renamed.)

PE layout (image base `0x400000`):

| Section | VA | vsize | raw offset | raw size |
|---------|----|-------|------------|----------|
| .text | `0x00401000` | `0x11DAC9D` | `0x1000` | `0x11DB000` |
| .rdata | `0x015DC000` | `0x270B4D` | `0x11DC000` | `0x271000` |
| .data | `0x0184D000` | `0x16FFD0` | `0x144D000` | `0xBB000` |
| .rsrc | `0x019BD000` | `0x14E8` | `0x1508000` | `0x2000` |

VA ↔ file-offset conversion for .text is identity (`foff == VA - 0x400000`); helper scripts in [tools/](tools/).

### Confirmed addresses (new — not yet in ARCHITECTURE.md)

| Address | Function | Confidence |
|---------|----------|------------|
| `0x0102DA80` | `SwgCuiChatWindow::Tab::appendText(const ChannelId&, const Unicode::String&)` — `__thiscall`, this = Tab | **High** (triple-verified, see below) |
| `0x0102D2D0` | `SwgCuiChatWindow::Tab::hasChannel(const ChannelId&)` (from the `call` inside appendText) | Medium-high |
| `0x018D52F8` | string `"Attempt to SwgCuiChatWindow::Tab::appendText empty string"` (.data) | High |
| `0x018D5340` | string `"tabId"` (settings key — anchor for Tab::save/loadTabSettings if ever needed) | High |
| `0x018D0392` | string `"SwgCuiChatWindow_%d"` | High |
| `0x018D040A` | string `"SwgCuiChatWindow"` | High |
| `0x018CF4E2` | RTTI `".?AVSwgCuiChatWindow@@"` | High |
| `0x0187D41F` | string `"%s_chatlog.txt"` (ChatLogManager) | High |

Callers of `Tab::appendText` (3 total — consistent with source: `appendTextToChannel`, `appendTextToCurrentTab`, +1):

- call at `0x00F3A35D`, enclosing function ≈ `0x00F3A300`
- call at `0x00F3C0FF`, enclosing function ≈ `0x00F3C0A0`
- call at `0x00F3C292`, enclosing function ≈ `0x00F3C1F0`

All in the same `0xF3xxxx` region as the known `SwgCuiChatWindow` ctor (`0x00F364B0`, already in ARCHITECTURE.md) — right code neighbourhood.

### Verification of 0x0102DA80 (why confidence is high)

1. The warning string above has exactly **one** code reference in the whole binary: `0x0102DAC5`, inside this function.
2. Disassembly matches the source line-for-line:
   - `55 8B EC 6A FF 68 ...` — SEH prologue
   - `8B 45 08` / `83 38 00` — load arg1 (`ChannelId*`), compare `type` (offset +0) against `CT_none` (0)
   - `E8 1E F8 FF FF` — call `hasChannel` (→ `0x0102D2D0`), early-out on false (the `id.type != CT_none && !hasChannel(id)` check)
   - `8B 7D 0C` / `8B 07 3B 47 04` — load arg2 (`Unicode::String*`), `begin == end` empty check
   - `75 1F 68 F8 52 8D 01` — jump-if-not-empty over `push 0x18D52F8` (the warning)
3. Exactly 3 callers, matching source expectations.

## Client source findings (client-tools fork)

> Fork paths below are reference only — the fork has diverged from our binary; structure is trustworthy, addresses/offsets are not.

### The receive path (game chat → screen)

All chat that appears in any tab funnels through **one choke point**:

```
network msg → CuiChatRoomManager::receiveChatRoomMessage (once per message)
  → Transceivers::messageReceived.emitMessage
    → SwgCuiChatWindow::onChatRoomMessageReceived   (per window instance)
      → appendTextToChannel(ChannelId, formattedStr)
        → for each tab: if (id.type == CT_none || tab->hasChannel(id)) tab->appendText(id, str)   ← HOOK HERE
```

Key file:lines (fork):

- `SwgCuiChatWindow.cpp:1942` — `onChatRoomMessageReceived`: builds the display string, maps roomId → ChannelId (see guild routing below).
- `SwgCuiChatWindow.cpp:2205` — `appendTextToChannel`: loops `m_tabVector`, `hasChannel` filter; `ChannelId(CT_none)` (from `appendToAllTabs`) broadcasts to every tab.
- `SwgCuiChatWindow_Tab.cpp:193` — `Tab::appendText`: re-checks `hasChannel`, warns on empty string, **prepends timestamp inside** (so our pre-hook sees the string WITHOUT timestamp), writes to `ChatLogManager::appendLine`, appends to tab buffers, clamps to `ConfigClientGame::getChatTabMaxTextLines()`.
- `SwgCuiChatWindow.cpp:569` — handlers are connected **per window instance** via `m_callback->connect(*this, &SwgCuiChatWindow::onChatRoomMessageReceived, ...)`. Multiple windows exist (cloned/torn-off tabs → `SwgCuiChatWindow_%d`).
- `SwgCuiChatWindow.cpp:894` — `onSpatialChatReceived` → `appendTextToChannel(ChannelId(CT_spatial), str)` (already-formatted string arrives from spatial system).
- `CuiChatRoomManager.cpp:1884` — `receiveChatRoomMessage(const ChatRoomMessage&)`: the once-per-message network entry. Fields: `getFromRoom()` (uint32 roomId), `getFromName()` (ChatAvatarId), `getMessage()`, `getOutOfBand()`. Accepts msg if roomId ∈ {planet, group, guild, city, entered rooms, named rooms}. **No string anchor in this function** → hard to find in binary without full disasm; that's why we hook Tab::appendText instead and dedupe per-frame.

### Guild chat routing — why no channel ID capture is needed

`onChatRoomMessageReceived` (fork :1983-1994):

```cpp
if      (message.roomId == CuiChatRoomManager::getPlanetRoomId()) appendTextToChannel(ChannelId(CT_planet), str);
else if (message.roomId == CuiChatRoomManager::getGroupRoomId())  appendTextToChannel(ChannelId(CT_group),  str);
else if (message.roomId == CuiChatRoomManager::getGuildRoomId())  appendTextToChannel(ChannelId(CT_guild),  str);
...
else if (hasNamedRoom) appendTextToChannel(ChannelId(CT_named, roomNode->getFullPath()), str);   // named rooms DO need a name
```

Guild is resolved to the **fixed enum constant** `CT_guild` before our hook. The server-assigned guild room id exists (`s_guildRoomId` static, set via `setGuildRoomId` on login, read via `getGuildRoomId()` — fork `CuiChatRoomManager.cpp:2147`) but we never need it.

### ChannelType enum (fork `SwgCuiChatWindow.h:66`)

```
CT_none=0, CT_chatRoom=1, CT_spatial=2, CT_planet=3, CT_combat=4,
CT_systemMessage=5, CT_instantMessage=6, CT_group=7, CT_matchMaking=8,
CT_guild=9, CT_city=10, CT_quest=11, CT_gcw=12, CT_named=13
```

⚠️ Verify `CT_guild == 9` in our diverged binary at runtime (debug-log `ChannelId.type` during guild chat) before hardcoding.

### ChannelId / Tab layout (fork — offsets NOT confirmed in our binary)

- `ChannelId` = `{ ChannelType type; std::string lowerName; std::string name; Unicode::String displayName; bool isPublic; }` — `type` is at **offset +0** (this IS confirmed by the binary disasm: `cmp dword [eax], 0`), which is all Stage 1 needs.
- `Tab` (inherits UINotificationServer): `m_defaultChannel`, `ChannelSet* m_channels`, `ChannelSet* m_modifiedChannels`, 4× `Unicode::String`, 2× bool, `int m_tabId`, `int m_charactersCut`. `m_tabId` persists across sessions via `saveTabSettings`/`loadTabSettings`. Estimated offset ~`0xD8–0xF0` (old-MSVC std::string = 28 bytes) — only needed if we ever return to per-tab filtering.

### Message text format at hook time

Built in `onChatRoomMessageReceived` (fork :1947-1981):

```
[roomPrefix]SenderShortName\>032: <colored message>\>000
```

- Sender name and body are ALREADY merged into one formatted string; raw fields only exist upstream in `CuiChatRoomMessage` (`sender.chatId`, `message`, `oob`).
- Colour is applied via `ClientTextManager::colorAndFilterText(text, TT_guild, profanityFiltered)` for guild.
- OOB prose packages already expanded (`ProsePackageManagerClient::appendAllProsePackages`).
- No timestamp yet (added inside `Tab::appendText` only if `CuiChatManager::getChatBoxTimestamp()`).
- Player's OWN sent guild messages come back through this same path → the bridge captures the full conversation.

### Colour escape syntax (fork `ClientTextManager.cpp`)

- Set colour: `\#RRGGBB` (lowercase hex, produced by `sprintf("\\#%06x", rgb)` — :150)
- Reset: `\#.` (:507)
- Indent/format codes seen in chat strings: `\>NNN` (3 digits, e.g. `\>032` after sender name, `\>000` at end)
- Strip pattern for Discord: `\#` + (6 hex chars | `.`), and `\>` + 3 digits.

### ChatLogManager (rejected alternative)

Client can log chat to `profiles/<loginId>/<clusterName>/<networkId>_chatlog.txt` (fork `ChatLogManager.cpp:51`). Lines prefixed `[TabName]`. Rejected for the bridge: caches 50 lines / autoflush at 1000 (not real-time), mixes all tabs, needs the setting enabled. Could serve as an external-tail fallback someday.

## Extension facts

- No networking or config-file code exists in the extension today (grep confirmed: no wininet/winhttp/socket/thread, no GetPrivateProfile/fopen/ifstream). WinHTTP + `GetPrivateProfileString` are both new-but-standard additions.
- Known chat-window address already in the project: `SwgCuiChatWindow` ctor `0x00F364B0` (`SwgCuiChatWindow.h` stub wrapper, `CUICHATWINDOWCTOR`).
- Per-frame main-thread tick available via the existing `GroundScene::parseMessages` hook (`0x0051A900`) — used for the frame counter (local dedupe) and any main-thread marshaling.
- Old-MSVC ABI warning (from vehicle-hover work): never pass modern `std::string` into client functions — direct memory reads at known offsets instead.

## Discord API facts

- **Webhooks are write-only** — cannot read channel messages, no idempotency/dedupe key. Rate limit ≈ 5 requests / 2 s per webhook; expect HTTP 429 with `retry_after`.
- Green embed for game→Discord: `{"embeds":[{"description":"...","color":3066993}]}` (`0x2ECC71`). Embed description limit 4096 chars — batch multiple chat lines per POST.
- Reading messages requires a **bot token** (`GET /channels/{id}/messages?after=<id>`) — REST-only bots work fine, no gateway connection required (important for IIS hosting; see plan).
- Purple for Stage 2 in-game text: pick e.g. `\#9B59B6`-ish via SWG escape (exact value TBD at implementation).
- The webhook URL provided during planning is a live credential — goes in git-ignored `DiscordBridge.ini` only, and should be **regenerated** in Discord channel settings since it appeared in chat/logs.

## Multi-user duplicate analysis (summary)

- Same client, multiple tabs/windows showing guild channel → duplicate `Tab::appendText` calls **within one dispatch/frame** → dedupe with (string, frame counter) pair.
- Multiple guild members running the bridge → NOT solvable webhook-only (stateless, write-only). Stage 1: designated relay (default-off ini + `via <name>` embed footer). Proper fix: hosted relay dedupes centrally (see plan).

## Tooling

Reusable binary-analysis scripts (string-anchor → xref → prologue walk) preserved in [tools/](tools/):

- `find_appendtext.py` — PE parse, string search, xref scan, prologue detection
- `verify_appendtext.py` — function byte dump, caller enumeration, string-of-interest sweep

Both hardcode `D:\Galaxies\SWGEmu_Clone\SWGEmu.exe` and the section table above; adapt for future anchor hunts (e.g. Stage 2's send path).
