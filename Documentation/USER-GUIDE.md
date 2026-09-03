# GalaxyExtender — User Guide

All commands are typed in the SWG chat window and begin with `/emu`.

---

## Graphics & Terrain Commands

### `/emu viewdistance <value>` (alias: `/emu vd`)

Sets the view/rendering distance. Controls how far away objects and terrain are drawn.

- **Range**: 512–4096
- **Default**: 1024
- **Example**: `/emu vd 2048`

### `/emu globaldetail <value>`

Sets the global terrain detail level. Higher values = more terrain detail at distance.

- **Range**: 1–24
- **Default**: 6
- **Note**: You must move the slider in Terrain options AFTER using this command for it to take full effect.
- **Example**: `/emu globaldetail 12`

### `/emu highdetailterrain <value>` (alias: `/emu hdterrain`)

Sets the high-detail terrain rendering distance.

- **Range**: 1–50
- **Default**: 10
- **Note**: You must move the slider in Terrain options AFTER using this command.
- **Example**: `/emu hdterrain 30`

### `/emu radialflora <value>`

Sets the radial flora (grass, flowers, etc.) rendering distance.

- **Range**: 1–256
- **Default**: 64
- **Example**: `/emu radialflora 128`

### `/emu noncollidableflora <value>` (alias: `/emu ncflora`)

Sets the non-collidable flora (decorative plants you can walk through) rendering distance.

- **Range**: 1–128
- **Default**: 32
- **Example**: `/emu ncflora 64`

### `/emu overrideall <preset>` (alias: `/emu setall`)

Applies all graphics settings at once using a named preset. Saves you from running five separate commands.

| Preset | View Distance | Global Detail | HD Terrain | Radial Flora | NC Flora |
|--------|--------------|---------------|------------|-------------|----------|
| **default** | 1024 | 6 | 10 | 64 | 32 |
| **low** | 1536 | 8 | 12 | 80 | 50 |
| **medium** | 2048 | 9 | 15 | 100 | 55 |
| **high** | 4096 | 12 | 30 | 128 | 64 |
| **ultra** | 4096 | 16 | 50 | 256 | 128 |

- **Example**: `/emu setall high`
- **Tip**: Use `/emu setall help` to see this table in-game.

### `/emu reloadTerrain`

Forces a full terrain reload. Useful after changing terrain detail settings if things look wrong.

---

## Graphics Query Commands

These commands print the current value of a graphics setting to the chat window.

| Command | Alias | Shows |
|---------|-------|-------|
| `/emu getviewdistance` | `/emu getvd` | View/rendering distance |
| `/emu getradialflora` | | Radial flora distance |
| `/emu getnoncollidableflora` | `/emu getncflora` | Non-collidable flora distance |

---

## Food & Drink Commands

These commands show your character's current stomach fill levels. Food and drink decay over time after eating/drinking.

### `/emu food`

Shows your current food fill value and percentage (e.g., `Food: 81 / 100 (81%)`).

### `/emu drink`

Shows your current drink fill value and percentage (e.g., `Drink: 45 / 100 (45%)`).

### `/emu stomach`

Shows both food and drink on one line (e.g., `Food: 81 / 100 (81%)  |  Drink: 45 / 100 (45%)`).

### Network Monitor Display

If you have the modified `ui_pda_net_status.inc` installed, food and drink values are displayed live on the network monitor panel (Ctrl+Shift+N) alongside ping, FPS, and packet loss. The values update automatically every frame — no commands needed.

**Setup**: Copy `SWGRootFiles\ui\ui_pda_net_status.inc` to your game directory at `<SWG Install>\swgroot\ui\ui_pda_net_status.inc`.

---

## List Window Search

### `/emu find <text>` (alias: `/emu search`)

Searches the currently open server list window — the guild member list, the sponsored-members list, or any similar list dialog — and selects and scrolls to the next entry containing `<text>` (case-insensitive, spaces allowed). Repeat the command to cycle through multiple matches.

- **Example**: open the guild terminal's member list, then `/emu find lierza` — the row is highlighted and scrolled into view; press Ok to act on it as usual.
- The list is never reordered or filtered: the highlighted row is the real row the server acts on when you press Ok, so kick/title/permission operations always target exactly the highlighted member.
- Selecting via `/emu find` sends nothing to the server — traffic only happens when you press Ok or Cancel, same as clicking a row by hand.

### `/emu find`

With no text, prints diagnostics: whether a list window was detected, its title, the row count, and the current selection.

---

## Targeting

### `/emu assist2`

Targets your current target's target. Look at a player or NPC, and this command will switch your target to whatever they are looking at. Useful for quickly targeting what a tank or healer is focused on.

---

## Vehicle Hover Height

Adjusts the hover height of jetpacks, speeders, swoops, and other hover vehicles. You must be mounted on a vehicle to use these commands.

**Note**: Height changes are client-side only — other players will see your vehicle at its normal height.

### `/emu hover`

Shows the current hover height of your vehicle.

- **Example output**: `Current hover height: 0.50`

### `/emu hover <value>`

Sets the hover height to the specified value in game units (float).

- **Default**: ~0.5 for most vehicles
- **Example**: `/emu hover 5.0` — hover 5 units above ground
- **Example**: `/emu hover 0.2` — fly very low

### `/emu hover reset`

Restores the hover height to whatever it was before you changed it. Only works if you've previously set a custom height during this session.

### `/emu hover debug`

Developer tool — prints the mount object hierarchy, memory addresses, and dynamics pointers for debugging. Useful if the command isn't finding the vehicle dynamics on a particular mount type.

---

## Discord Guild Chat Bridge

Bridges guild chat and a Discord channel in both directions. Guild lines go to the GalaxyExtender
relay, which de-duplicates them across everyone running the bridge and forwards a single copy to
Discord; messages typed in Discord come back and appear in the guild room as
`[Discord] <author>: <text>`, in colour so they stand out from in-game speech.

Several guild members can (and should) run it at once — the relay hands each incoming Discord
message to exactly one client, so nothing is said twice, and the bridge keeps working when any one
player logs off.

**What the extension sends:** guild chat lines, and server broadcasts that match a world boss alert
tag. Nothing else. Other channels — tells, group, spatial, mission and loot messages — are never
read for relaying, and the extension never talks to Discord itself: it only ever talks to the relay,
and never sees the Discord webhook or bot token.

### Setup

Guild chat has to be visible in at least one chat tab for the bridge to see it; the default UI
qualifies. Nothing needs typing to make the Discord → game direction work — the extension reads the
client's own guild room id when the server auto-joins you at login.

Create `DiscordBridge.ini` beside `SWGCommandExtension.dll` and ask the relay operator for the key.
The file is git-ignored and holds a shared secret — never commit it or paste the key into chat.

```ini
[DiscordBridge]
enabled=1
endpoint=https://<relay-host>/relay
key=<X-Relay-Key from the relay operator>
```

Everything below is optional:

| Key | Default | What it does |
|-----|---------|--------------|
| `client_id` | anonymous per-machine hash | Label for the relay's logs. Never your hostname unless you set it to one |
| `character` | — | Label for the relay's logs |
| `galaxy` | — | Label for the relay's logs |
| `channel_type` | `9` | Which chat channel counts as guild. Escape hatch for a server that numbers it differently — check with `/emu discord types` |
| `stage2` | `1` | `0` opts this client out of posting Discord messages into guild chat. Sending guild chat to Discord is unaffected |
| `allow_http` | `0` | An `http://` endpoint is refused unless this is `1`, because the key would travel in cleartext |
| `alerts` | `1` | `0` stops this client relaying world boss alerts |
| `alert_channel_types` | `5,11` | Channels scanned for alert tags (system message, quest). Replaces the default, max 16 |
| `alert_tags` | `[PvE World Boss],[PvP World Boss]` | Comma-separated. A line is an alert when it *starts* with one of these, matched case-insensitively. Replaces the default, max 16 |

Without the file the bridge stays inactive and `/emu discord status` says why.

### `/emu discord on`

Starts the bridge and re-reads `DiscordBridge.ini`, so a corrected endpoint or key takes effect
without restarting the client. Also clears the "relay rejected the key" latch.

### `/emu discord off`

Stops the bridge in both directions and discards anything queued. Discord messages this client had
claimed but not yet posted are redelivered to another client rather than lost.

### `/emu discord status`

The one command worth knowing. Reports, in order: state (`on`, `off`, `not configured`, or
`stopped` when the relay rejected the key), relay host and scheme, client id, guild channel type,
world boss alert state and how many alerts this client has captured, the outgoing queue and last
HTTP result, the Discord → game side (relay switch, where the guild room id came from, queued and
injected counts, last poll result, last injected line), and how many players have the extension
online — the same figure the Discord bot reports.

It never prints the key.

### `/emu discord test`

Queues a synthetic line so you can watch a full round trip. Run `status` a few seconds later to see
what the relay said.

### `/emu discord poll`

Asks the relay for waiting Discord messages immediately rather than at the next poll (normally every
5 seconds), and clears the poll fault latch. Useful after fixing a config problem.

### `/emu discord types`

Lists every chat channel type seen since the DLL loaded, with a line count and a sample, marking
the one being relayed and the ones scanned for alert tags. Use it to confirm the right
`channel_type` on a server that numbers channels differently, or to find which channel a boss
broadcast actually arrives on. Chat has to arrive while the DLL is loaded for a type to appear.

### `/emu discord rooms`

Shows the guild room id the client reported (and whether it came from the login auto-join or from a
line you typed in the guild tab), plus the room log. Diagnostics for the Discord → game direction:
no room id means nothing can be posted into guild chat.

### World Boss Alerts

Tagged server broadcasts are relayed to Discord as a coloured embed, so the guild hears about a
world boss whether or not anyone is watching chat. The extension scans only the channels in
`alert_channel_types` and relays only lines that **start** with one of `alert_tags`.

Matching at the start of the line is what stops a player faking an alert: a server broadcast arrives
with no sender prefix, a player's line always has one. Everything else on those channels is personal
to you — mission, loot and error messages — and never leaves your machine.

A backstop caps how many alerts one client can relay per minute; `status` reports any suppressed and
tells you to check `alert_channel_types`, since hitting it means the setting is pointed at a chatty
channel. A malformed `alert_*` value switches alerts off and says so in `status` — it never
invalidates the rest of the config, so guild chat keeps flowing while you fix the typo.

### From the Discord Side

Mention the bot in the bridge channel and it answers with a count of connected clients, and how long
ago the last world boss alert passed through. `help` lists what it understands. Address it with
anything else and you get a magic eight ball answer — fixed per message, so asking again shakes the
ball again; while somebody is in game, both halves of that exchange also appear in the guild room.

If you post while nobody is online, the bot tells you so, once per quiet spell, and says whether the
message is waiting (the next player to log in posts it into the guild room) or genuinely will not
arrive. Emoji work in both directions — Discord emoji reach the game as `:joy:`-style shortcodes
instead of `?`, and shortcodes typed in game post as real emoji — and Discord speakers are named by
their server nickname, the name the guild recognises.

---

## Memory Scanner (Advanced)

These are developer tools for discovering unknown memory offsets in the client. You probably don't need these unless you're contributing to the project.

### `/emu memscan snapshot` (alias: `snap`)

Saves a snapshot of the first 8KB of `PlayerObject` memory. This is the "before" picture for a diff.

### `/emu memscan diff`

Compares the current `PlayerObject` memory against the last snapshot and reports every 4-byte-aligned offset that changed. Useful for finding which memory offset changes when you perform an in-game action (e.g., eating food).

Output highlights likely `AutoDeltaVariable` fields when both the current and last value changed together.

### `/emu memscan search <value>`

Searches the `PlayerObject` memory for all 4-byte-aligned offsets containing the specified integer value. Accepts decimal or hex (`0x` prefix).

- **Example**: `/emu memscan search 34` — finds all offsets holding the value 34
- **Example**: `/emu memscan search 0x64` — finds all offsets holding 100

### `/emu memscan delta <offset>` (alias: `adv`)

Reads the `AutoDeltaVariable<int>` at the specified byte offset and displays both the current value and the last (previous) value.

- **Example**: `/emu memscan delta 0x570` — reads the ADV at PlayerObject + 0x570

### `/emu memscan dump <offset> [byte_count]`

Dumps raw memory as a hex dump starting at the specified offset. Default is 64 bytes, maximum is 512.

- **Example**: `/emu memscan dump 0x570 0x50` — dumps 80 bytes starting at offset 0x570

---

## Debug Commands

### `/emu findui`

Probes many UI widget paths from the root page and reports which ones resolve. Used for discovering where widgets live in the UI tree. Mainly useful for debugging UI integration issues.

### `/emu testhooks`

Internal test command that exercises various wrapper functions (attribute reads, string handling, vector operations, network IDs, etc.) and dumps the results to chat. Used to verify the DLL's hooks and wrappers are working correctly.

### `/emu getcurrenthealth`

Displays your character's current Health attribute value.

### `/emu octrl <message_id> [value]`

Sends a raw message to your character's controller. **Use with caution** — sending arbitrary controller messages can have unexpected effects.

---

## Quick Reference

| Command | What it does |
|---------|-------------|
| `/emu vd 2048` | Set view distance to 2048 |
| `/emu setall high` | Apply "high" graphics preset |
| `/emu food` | Show food fill |
| `/emu drink` | Show drink fill |
| `/emu stomach` | Show food + drink |
| `/emu assist2` | Target your target's target |
| `/emu find lierza` | Jump to a row in the open list window |
| `/emu discord status` | Discord bridge state and diagnostics |
| `/emu getvd` | Show current view distance |
| `/emu help` | Show command list in-game |
