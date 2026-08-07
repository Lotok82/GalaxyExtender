# GalaxyExtender

Client-side enhancements for Star Wars Galaxies (SWGEmu). Adds graphics overrides, food/drink tracking, vehicle hover height control, and quality-of-life commands.

## Setup

1. Use **GalaxyLoader** to load `SWGCommandExtension.dll` into the SWG client.
2. Once in-game, type `/console` in the chat window to open the console. All extension commands are entered here.
3. Type `/emu help` to see the full list of available commands.

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

### Vehicle Hover Height

Adjust the hover height of jetpacks, speeders, and other hover vehicles while mounted. Changes are visible to you only (client-side).

| Command | Description |
|---------|-------------|
| `/emu hover` | Show current hover height |
| `/emu hover <value>` | Set hover height in game units (e.g. `5.0` for high, `0.2` for low) |
| `/emu hover reset` | Restore the original hover height |

### Discord Guild Chat Bridge

Relays your guild chat to a Discord channel. Lines are sent to the GalaxyExtender relay,
which de-duplicates them across everyone running the bridge and forwards a single copy to
Discord — so it is safe (and useful, for resilience) for several guild members to enable it.

Guild chat has to be visible in at least one chat tab for the bridge to see it. The default
UI qualifies.

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
```

Without the file the bridge stays inactive and says why in `/emu discord status`.

| Command | Description |
|---------|-------------|
| `/emu discord on` | Start relaying. Also re-reads `DiscordBridge.ini`, so a corrected endpoint or key takes effect without restarting the client |
| `/emu discord off` | Stop relaying and discard anything queued |
| `/emu discord status` | State, relay host, queue depth, the last HTTP result, and how many players have the extension running. Never prints the key |
| `/emu discord test` | Queue a synthetic line to check the round trip |
| `/emu discord types` | Chat channel types seen so far — use this to confirm which one carries guild chat |

If the relay rejects your key the bridge stops rather than retrying; `status` says so. Fix
`key` in the ini and run `/emu discord on`.

**Asking from Discord.** Mention the bridge bot in the Discord channel — whatever it is called on
your server — and it answers with a count:

```
@YourBot status
→ **Guild chat bridge: online** — 2 of 5 clients connected (checked in within the last 3 min).

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
