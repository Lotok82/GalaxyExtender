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

## Targeting

### `/emu assist2`

Targets your current target's target. Look at a player or NPC, and this command will switch your target to whatever they are looking at. Useful for quickly targeting what a tank or healer is focused on.

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

## Food/Drink Monitor (Advanced)

Runtime configuration for the food/drink monitor. These are mainly useful if the compiled-in offsets ever need to change without rebuilding the DLL.

### `/emu monitor`

Shows the current monitor status (enabled/disabled, offsets configured, cached values) and lists available subcommands.

### `/emu monitor on`

Enables per-frame polling of food/drink values from `PlayerObject` memory. Requires offsets to be configured first.

### `/emu monitor off`

Disables per-frame polling.

### `/emu monitor status`

Shows detailed diagnostic info: whether the monitor is enabled, whether offsets are configured, the cached food/drink values, direct-read values from `PlayerObject`, and whether the UI widgets on the network monitor panel have been found.

### `/emu monitor setoffsets <food> <maxFood> <drink> <maxDrink>`

Sets the `AutoDeltaVariable` base offsets for food/drink fields in `PlayerObject`. Values are hex offsets.

- **Example**: `/emu monitor setoffsets 0x570 0x584 0x598 0x5AC`

This is only needed if you're testing new offsets at runtime. The confirmed offsets (food=0x570, drink=0x598) are compiled into the DLL.

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
| `/emu getvd` | Show current view distance |
| `/emu help` | Show command list in-game |
