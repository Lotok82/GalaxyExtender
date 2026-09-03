# GalaxyExtender

Client-side enhancements for Star Wars Galaxies (SWGEmu). Adds graphics overrides,
food/drink tracking, vehicle hover height control, search in server list windows, a two-way
guild chat bridge to Discord (with world boss alerts), and quality-of-life commands.

## Setup

1. Use **GalaxyLoader** to load `SWGCommandExtension.dll` into the SWG client.
2. Once in-game, type `/console` in the chat window to open the console. All extension commands are entered here.
3. Type `/emu help` to see the full list of available commands.

Further reading. Per-command detail — ranges, defaults, examples, and the developer-only
commands — is in [Documentation/USER-GUIDE.md](Documentation/USER-GUIDE.md), and the internals in
[Documentation/ARCHITECTURE.md](Documentation/ARCHITECTURE.md). The Discord bridge has a
plain-language guide for each side: players in
[SWGCommandExtension/README.html](SWGCommandExtension/README.html), whoever runs the relay in
[Relay/README.html](Relay/README.html), with the full wire contract and every setting in
[Relay/README.md](Relay/README.md).

### Network Monitor (Food & Drink)

The network monitor panel (Ctrl+Shift+N) can display your current food and drink fill values alongside ping, FPS, and packet loss. This works automatically — **no commands are needed**.

To enable it, copy the file `SWGRootFiles\ui\ui_pda_net_status.inc` from this project to your game directory at `<SWG Install>\swgroot\ui\ui_pda_net_status.inc` and restart the client.

---

## Commands

All commands begin with `/emu` and are typed in the console or chat window.

### Graphics

| Command | Description |
|---------|-------------|
| `/emu viewdistance <value>` | Set view/rendering distance. Range: 512–4096. Alias: `/emu vd` |
| `/emu globaldetail <value>` | Set global terrain detail level. Range: 1–24. Move the terrain slider after using this. |
| `/emu highdetailterrain <value>` | Set high-detail terrain distance. Range: 1–50. Alias: `/emu hdterrain` |
| `/emu radialflora <value>` | Set flora rendering distance. Range: 1–256 |
| `/emu noncollidableflora <value>` | Set non-collidable flora distance. Range: 1–128. Alias: `/emu ncflora` |
| `/emu overrideall <preset>` | Apply all graphics settings at once. Alias: `/emu setall` |
| `/emu reloadTerrain` | Force a full terrain reload |

**Presets for `/emu setall`:** `default`, `low`, `medium`, `high`, `ultra`. Type `/emu setall help` in-game to see the values for each preset.

### Graphics (Query)

| Command | Description |
|---------|-------------|
| `/emu getviewdistance` | Show current view distance. Alias: `/emu getvd` |
| `/emu getradialflora` | Show current radial flora distance |
| `/emu getnoncollidableflora` | Show current non-collidable flora distance. Alias: `/emu getncflora` |

### Food & Drink

| Command | Description |
|---------|-------------|
| `/emu food` | Show current food fill value and percentage |
| `/emu drink` | Show current drink fill value and percentage |
| `/emu stomach` | Show both food and drink |

### Targeting

| Command | Description |
|---------|-------------|
| `/emu assist2` | Target your current target's target |

### List Window Search

Server list windows — the guild member list, the sponsored-members list, and any similar
dialog — arrive unsorted and can run to hundreds of rows. `/emu find` jumps to the row you
are after instead of scrolling for it.

| Command | Description |
|---------|-------------|
| `/emu find <text>` | Select and scroll to the next row containing `<text>` in the open list window. Case-insensitive, spaces allowed; repeat to cycle through matches. Alias: `/emu search` |
| `/emu find` | Diagnostics: whether a list window was detected, its title, row count and current selection |

The list is never reordered or filtered, and that is deliberate: the server maps the
highlighted row back through its own list when you press Ok, so client-side sorting would make
Ok act on the wrong entry (kick the wrong guild member). Selecting a row sends nothing to the
server — traffic happens on Ok or Cancel, exactly as when you click a row by hand.

### Vehicle Hover Height

Adjust the hover height of jetpacks, speeders, and other hover vehicles while mounted. Changes are visible to you only (client-side).

| Command | Description |
|---------|-------------|
| `/emu hover` | Show current hover height |
| `/emu hover <value>` | Set hover height in game units (e.g. `5.0` for high, `0.2` for low) |
| `/emu hover reset` | Restore the original hover height |

### Discord Guild Chat Bridge

Bridges your guild chat and a Discord channel **in both directions**. Guild lines are sent to the
GalaxyExtender relay, which de-duplicates them across everyone running the bridge and forwards a
single copy to Discord; messages typed in the Discord channel come back the other way and appear in
the guild room as `[Discord] <author>: <text>`. It is safe (and useful, for resilience) for several
guild members to enable it — the relay hands each incoming Discord message to exactly one client,
so nothing is posted twice.

Guild chat has to be visible in at least one chat tab for the bridge to see it. The default
UI qualifies. Nothing has to be typed to make the return direction work: the extension reads the
client's own guild room id at login, so posting into the guild room starts working as soon as you
are in the world.

**Setup.** Create `DiscordBridge.ini` next to `SWGCommandExtension.dll` and ask the relay
operator for the key. The file is git-ignored — never commit it or paste the key anywhere.

```ini
[DiscordBridge]
enabled=1
endpoint=https://<relay-host>/relay
key=<X-Relay-Key from the relay operator>

; all optional — labels for the relay's logs only
client_id=kaelen
character=Kaelen
galaxy=Basilisk

; optional escape hatch: which chat channel counts as guild (default 9)
channel_type=9

; optional opt-outs, all on by default
stage2=1                    ; 0 = don't post Discord messages into guild chat on this client
alerts=1                    ; 0 = don't relay tagged server broadcasts (world boss alerts)
allow_http=0                ; 1 = permit an http:// endpoint (the key travels in cleartext)
```

Without the file the bridge stays inactive and says why in `/emu discord status`.

| Command | Description |
|---------|-------------|
| `/emu discord on` | Start the bridge. Also re-reads `DiscordBridge.ini`, so a corrected endpoint or key takes effect without restarting the client |
| `/emu discord off` | Stop the bridge in both directions and discard anything queued |
| `/emu discord status` | State, relay host, queue depths, last HTTP results, alert state, and how many players have the extension running. Never prints the key |
| `/emu discord test` | Queue a synthetic line to check the round trip |
| `/emu discord poll` | Ask the relay for waiting Discord messages now instead of at the next interval |
| `/emu discord types` | Chat channel types seen so far — use this to confirm which one carries guild chat |
| `/emu discord rooms` | The guild room id the client reported, plus the room log for the return direction |

If the relay rejects your key the bridge stops rather than retrying; `status` says so. Fix
`key` in the ini and run `/emu discord on`.

**Emoji survive the trip both ways.** Discord emoji arrive in game as their shortcode (`:joy:`,
`:thumbsup:`), flags as `[flag]` and anything unnamed as `[emoji]`, instead of the `?` the client
would otherwise show; typing `:joy:` in guild chat posts the real emoji in Discord. Speakers are
named by their **server nickname** — the name the guild recognises — rather than their Discord
account name.

### World Boss Alerts

Tagged server broadcasts are relayed to Discord as a coloured embed, so the guild hears about a
world boss whether or not anyone is at their keyboard. The extension scans a small allow-list of
channels (system messages and quest by default) for lines that **start** with a configured tag —
`[PvE World Boss]` or `[PvP World Boss]`. Matching at the start is what keeps players from faking
one: a server broadcast has no sender prefix, a typed line always does. Everything else on those
channels is personal to you (mission, loot and error messages) and never leaves your machine.

Alerts are on by default and need no configuration. To turn them off, or to point them at
different channels or tags on a server that words its broadcasts differently:

```ini
alerts=0
alert_channel_types=5,11
alert_tags=[PvE World Boss],[PvP World Boss]
```

Both lists replace the default rather than adding to it, and cap at 16 entries. A malformed
`alert_*` value switches alerts off and says so in `/emu discord status` — it never invalidates the
rest of the config, so guild chat keeps flowing while the typo is fixed. Pinging a role on each
alert is a relay-side setting; see [Relay/README.md](Relay/README.md).

### Talking to the Bridge from Discord

**Asking from Discord.** Mention the bridge bot in the Discord channel — whatever it is called on
your server — and it answers with a count:

```
@YourBot status
→ **Guild chat bridge: online** — 2 of 5 clients connected (checked in within the last 3 min).
  Last World Boss Alert: 3 hours and 07 minutes ago.

@YourBot status          (nobody playing)
→ **Guild chat bridge: offline** — nobody has checked in within the last 3 min.
  5 clients seen recently; last seen 2 h 11 min ago.
```

Any client connected means online; none means offline, and then it says how long ago the last one
was seen. "Connected" means **a client currently able to carry guild chat** — one that has been in
touch with the relay in the last few minutes, which is what the bot can actually observe. No names
are involved anywhere, and nothing needs filling into `DiscordBridge.ini` for the count to work.
Mentioning it with `help` lists what it understands, and it stays quiet when mentioned in ordinary
conversation. Renaming the bot in Discord changes nothing: it recognises mentions of *itself*, not of
a particular name, and its replies never name it either.

**Anything else you address it with gets a magic eight ball answer** — one of a hundred stock
phrases, fixed per message, so asking again shakes the ball again:

```
@YourBot will the boss drop anything good tonight?
→ Never tell me the odds. (They're not great.)
```

While somebody is in game the whole exchange — question and answer — also appears in the guild room,
so players see the conversation rather than half of it. With nobody online the reply still posts in
Discord but is not queued for later: a fortune arriving in guild chat hours after it was asked is
noise. Pressing reply on one of the bot's posts is not addressing it, and stays ordinary bridged
chat.

**It also speaks up on its own** when you post something that isn't going to reach the guild room, so
you never have to guess whether it landed:

```
Bob: anyone up for a Krayt run?
→ **Guild chat bridge: offline** — nobody has checked in within the last 3 min (last seen 2 h 11 min ago).
  This message is waiting, not lost: the first client to come online posts it into the guild room.
```

A message posted to an empty guild really does arrive later — whoever logs in next posts it into
guild chat. If the bridge is switched off at the relay instead, the bot says so plainly ("will not
appear in the guild room, now or later") rather than implying it is queued. You get one notice per
quiet spell, not one per line, and none at all while somebody is online.

### Other

| Command | Description |
|---------|-------------|
| `/emu getcurrenthealth` | Show your current Health attribute value |
| `/emu help` | Show command list in-game |
