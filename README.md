# GalaxyExtender

Client-side enhancements for Star Wars Galaxies (SWGEmu). Adds graphics overrides, food/drink tracking, and quality-of-life commands.

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

### Other

| Command | Description |
|---------|-------------|
| `/emu getcurrenthealth` | Show your current Health attribute value |
| `/emu help` | Show command list in-game |
