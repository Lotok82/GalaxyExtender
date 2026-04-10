# Food & Drink System — Research Notes

## Overview

In Star Wars Galaxies, food and drink items fill a "stomach" meter. Each consumable has a fill value (e.g., 34 or 8). The stomach has a maximum capacity (default 100) and decays over time. The client stores these values in the `PlayerObject` class as `AutoDeltaVariable<int>` fields, synchronized from the server via PLAY9 baseline/delta network messages.

The SWG client binary (`SwgClient_r.exe`) was compiled without debug symbols. The food/drink UI bars exist in the character sheet but were never populated by the client code in the emulator builds. This document records everything learned about discovering and reading these values at runtime.

---

## PlayerObject Memory Layout

The `PlayerObject` inherits from `IntangibleObject → Object`. The `this` pointer is obtained via `Game::getPlayerObject()` at address `0x00425180`.

### Confirmed Field Offsets (from PlayerObject base)

| Field | ADV Base Offset | Current Value Offset | Status |
|-------|----------------|---------------------|--------|
| m_food | `0x0570` | `0x057C` | **Confirmed** |
| m_maxFood | Unknown | Unknown | TODO — likely `0x0584` |
| m_drink | `0x0598` | `0x05A4` | **Confirmed** |
| m_maxDrink | Unknown | Unknown | TODO — likely `0x05AC` |
| m_meds | Unknown | Unknown | TODO |
| m_maxMeds | Unknown | Unknown | TODO |

### How AutoDeltaVariable<int> Works

`AutoDeltaVariable<T>` is the SWG engine's network-synchronized variable type. Its internal layout:

```
+0x00: vtable pointer (4 bytes)
+0x04: AutoDeltaByteStream* owner (4 bytes)
+0x08: unknown (4 bytes)
+0x0C: T currentValue  ← this is the live game value
+0x10: T lastValue      ← previous value before last delta
```

So if `m_food` is at base offset `0x0570`, the actual current food integer is at `PlayerObject + 0x0570 + 0x0C = PlayerObject + 0x057C`.

The gap between food (`0x0570`) and drink (`0x0598`) is `0x28` (40 bytes). This is enough for two ADVs — `m_food` and `m_maxFood` — each being ~20 bytes (0x14), which fits perfectly: `0x0570 + 0x14 = 0x0584` (m_maxFood), `0x0584 + 0x14 = 0x0598` (m_drink).

### Expected Full Layout (Predicted)

Based on the SWG source code, these fields are declared consecutively:
```cpp
AutoDeltaVariable<int> m_food;      // 0x0570
AutoDeltaVariable<int> m_maxFood;   // 0x0584 (predicted)
AutoDeltaVariable<int> m_drink;     // 0x0598 (confirmed)
AutoDeltaVariable<int> m_maxDrink;  // 0x05AC (predicted)
AutoDeltaVariable<int> m_meds;      // 0x05C0 (predicted)
AutoDeltaVariable<int> m_maxMeds;   // 0x05D4 (predicted)
```

To confirm maxFood/maxDrink, use `/emu memscan dump 0x570 0x50` and look for the value 100 at the predicted offsets (at +0xC from each base: `0x0590` and `0x05B8`).

---

## How We Discovered These Offsets

### Method: Runtime Memory Scanning (memscan)

The DLL includes a built-in memory scanner (`FoodDrinkMonitor`) that reads raw bytes from the `PlayerObject` instance. The workflow:

1. **Snapshot**: `/emu memscan snapshot` — saves the first 8KB of PlayerObject memory
2. **Perform action**: Eat food or drink something in-game
3. **Diff**: `/emu memscan diff` — compares current memory to snapshot, reports all int32-aligned offsets that changed
4. **Verify**: `/emu memscan search <exact_value>` — searches for a specific integer value to confirm

### Actual Discovery Session

**Food discovery:**
- Took snapshot, ate food with fill value 34
- Diff showed 3 changed offsets:
  - `0x056C`: large number change (timestamp, ignore)
  - `0x057C`: `0 → 33` (off by 1 due to stomach decay between eat and diff)
  - `0x0580`: `0 → 33` (ADV last value mirror at +0x10)
- Ate second food with fill 8: `0x057C: 12 → 19` (+7, off by 1 from decay again)

**Drink discovery:**
- Fresh snapshot, drank something with fill value 10
- Diff showed:
  - `0x056C`: timestamp change (ignore)
  - `0x05A4`: `0 → 10` (exact match!)
  - `0x05A8`: `0 → 10` (ADV last value mirror)

### Key Observations

- The `0x056C` offset always changes — it's a timestamp or counter, not food/drink related
- The diff may show a value 1 less than expected due to stomach decay ticking between the eat action and the diff command
- Each ADV produces TWO hits in the diff: the current value (`+0x0C`) and the last value (`+0x10`)
- Direct search (`/emu memscan search <value>`) is more reliable than diff for confirming exact offsets

---

## Ghidra Reverse Engineering Findings

### SwgCuiCharacterSheet

- **Constructor**: `0x00F9F290`
  - Pushes the string `"SwgCuiCharacterSheet"`
  - Stores UI widget pointers at these offsets from the sheet object:
    - `+0xAC`: `imageFoodBar` (string at `0x018D229C`)
    - `+0xB0`: `imageFoodBarBack` (string at `0x018D2288`)
    - `+0xB4`: `imageDrinkBar` (string at `0x018D2278`)
    - `+0xB8`: `imageDrinkBarBack` (string at `0x018D2264`)
  - All four string XREFs point to this single constructor function

- **Message handler**: `0x00FA0100`
  - Handles: `CharacterSheetResponseMessage`, `GuildResponseMessage`, `PlayerMoneyResponse`, `BadgesResponseMessage`, `FactionResponseMessage`
  - Does NOT handle food/drink updates — those come through PLAY9 baselines/deltas

### SwgCuiNetStatus

- **Constructor**: `0x00D58490`
  - Pushes `"SwgCuiNetStatus"` string (at `0x018C36C0`)
  - Widget member offsets on the SwgCuiNetStatus object:
    - `+0x84`: m_textPing (UIText*)
    - `+0x88`: m_textPacketLoss (UIText*)
    - `+0x8c`: m_textBandwidth (UIText*)
    - `+0x90`: m_textFps (UIText*)
    - `+0x94`, `+0x98`, `+0x9c`: cached int values (initialized to -1)
    - `+0xa0`: timer accumulator (float)
    - `+0xa4`: dirty flag (byte)
  - Uses `FUN_00A68210` (`getCodeDataObject`) to look up widgets by CodeData name
  - Calls `FUN_01112660` (`SetPreLocalized(true)`) on all 4 text widgets

- **Vtable**: `0x015F1990`
  - Index 4: `FUN_00D586C0` = `update(float)` — the main periodic update method

- **Update method**: `0x00D586C0`
  - Throttled by timer at `+0xa0`, resets every ~0.3s (`FCOMP` against `DAT_015dd878`)
  - Reads ping via network API, formats as `"%3d (host id %d)"`, calls `SetLocalText` on `[this+0x84]`
  - Reads packet loss, formats as `"%3d%%"`, calls `SetLocalText` on `[this+0x88]`
  - Reads FPS via `Clock::framesPerSecond()`, formats as `"%d"`, calls `SetLocalText` on `[this+0x90]`
  - Only updates when value differs from cached value at `+0x94`/`+0x98`/`+0x9c`

### UIText Methods

| Address | Method | Signature |
|---------|--------|-----------|
| `0x0110F580` | `SetLocalText` | `void __thiscall(const UIString&)` — sets display text directly |
| `0x0110FB40` | `AppendLocalText` | `void __thiscall(const UIString&)` — appends to existing text |
| `0x01112660` | `SetPreLocalized` | `void __thiscall(bool)` — disables localization pipeline |
| `0x0110F8D0` | Unknown | Complex function, NOT SetLocalText (first param compared to int 1) |

### PlayerObject-related functions

- `0x0065FEA0`: `PlayerObject::speaksLanguage(int)` — confirmed, used as anchor point
- `0x0065FE90`: Reads `[ECX + 0x528]` — unknown getter near speaksLanguage, NOT food/drink
- `0x00425180`: `Game::getPlayerObject()` — returns the PlayerObject pointer
- `0x00425200`: `Game::getPlayerCreature()` — returns the CreatureObject pointer

### Why Ghidra Didn't Find Food/Drink Getters

The food/drink getter functions (`getFood()`, `getMaxFood()`, etc.) are trivial one-liners in the SWG source:
```cpp
int PlayerObject::getFood() const { return m_food.get(); }
```
These are likely **inlined by the compiler** — they're too simple for the optimizer to keep as standalone functions. This means there's no discrete function address to find in the binary. The runtime memscan approach was necessary.

---

## UI Integration — Net Status Panel

Rather than modifying the character sheet bars, food/drink values are displayed on the **network monitor** panel alongside ping, packet loss, bandwidth, and FPS.

### Modified .inc Layout

File: `SWGRootFiles\ui\ui_pda_net_status.inc` (deploy to `swgroot\ui\` in game directory)

Changes from original:
- Page height increased from 64 to 94 (`MaximumSize`, `MinimumSize`, `Size`, `ScrollExtent`)
- Composite height increased from 60 to 90
- Added `food` page (Location `0,60`) with label "food:" and text "0/100"
- Added `drink` page (Location `0,75`) with label "drink:" and text "0/100"
- Same styling as existing rows (bold_12 label, bold_13 value, `#96F4FC` color)

### The Workspace Mediator Clone Problem

**This was the hardest part of the UI integration.** The network monitor panel uses a **workspace mediator** with `duplicateOnly=true`, which means the SWG UI system **clones** the template page when creating the visible panel. This has a critical consequence: **writing to the template widgets has no visible effect** — you must find and write to the **cloned** widgets instead.

#### How We Discovered This

1. **First attempt — path from root UI tree**: We tried `UIManager::gUIManager()->GetRootPage()->GetObjectFromPath("netStatus.comp.food.text")`. The path resolved to **nothing** (nullptr). The `/emu findui` debug command confirmed: all paths prefixed with `netStatus.*` returned NOT FOUND.

2. **Second attempt — pda prefix**: Studying the forked client source (`SwgCuiMediatorFactorySetup.cpp`), we found the net status panel is registered as:
   ```cpp
   CuiMediatorFactory::addConstructor("WS_NetStatus", 
       new CuiMediatorFactory::Constructor<SwgCuiNetStatus>(
           "pda.netStatus", true));  // true = duplicateOnly
   ```
   The template page lives at path `pda.netStatus` in the UI tree. A second `/emu findui` scan using the `pda.` prefix found all 12 widgets:
   - `pda.netStatus.comp.food.text` → **FOUND**
   - `pda.netStatus.comp.drink.text` → **FOUND**
   - All original widgets (ping, packetLoss, bandwidth, fps) → **FOUND**

3. **Template widgets are read-only in practice**: We called `SetLocalText` and `AppendLocalText` on the template widgets found via `pda.netStatus.comp.food.text`. The calls succeeded with no crash, but **nothing appeared on screen**. The visible network monitor was rendering from a completely different set of cloned widget instances.

4. **The solution — CuiMediatorFactory::get()**: The active mediator object holds a pointer to its **cloned page** (the one actually rendered on screen). The approach:
   ```cpp
   // Step 1: Get the active mediator by its workspace name
   CuiMediator* mediator = CuiMediatorFactory::get("WS_NetStatus", false);
   
   // Step 2: Access the cloned UIPage at mediator offset +0x14
   // (CuiMediator stores its UIPage* at this offset)
   UIPage* page = mediator->getMemoryReference<UIPage*>(0x14);
   
   // Step 3: Look up widgets relative to the CLONED page
   UIBaseObject* foodObj = page->GetObjectFromPath("comp.food.text");
   UIBaseObject* drinkObj = page->GetObjectFromPath("comp.drink.text");
   ```
   The `false` parameter in `get()` means "don't create if it doesn't exist" — we only want to find an already-active panel.

#### Why `duplicateOnly` Matters

When a mediator is registered with `duplicateOnly=true`, the `Constructor<T>::createInto()` method is used instead of `Constructor<T>::get()`. This calls `UIPage::DuplicateInto()` which deep-clones the template page into a workspace page. The clone has its own independent widget instances — they share the same relative paths (e.g., `comp.food.text`) but are completely separate objects in memory.

The **template** (`pda.netStatus`) remains in the UI tree as a hidden blueprint. The **clone** is what gets attached to the active workspace and rendered on screen. You can verify this by comparing pointer addresses — template widgets and clone widgets have different addresses even though they share the same path names.

#### Key Insight for Future Mediator Work

Any mediator registered with `duplicateOnly=true` will have this same behavior. The pattern is:
1. **Don't** look up widgets from `UIManager::gUIManager()->GetRootPage()` — you'll find the template (or nothing, depending on path)
2. **Do** use `CuiMediatorFactory::get("WS_<MediatorName>", false)` to get the active mediator
3. Access its page at `+0x14` and use `GetObjectFromPath()` relative to that page

Known workspace mediators in the SWG client include: `WS_NetStatus`, `WS_ChatWindow`, `WS_Toolbar`, `WS_MiniMap`, etc.

### DLL Implementation Details

`FoodDrinkMonitor::updateNetStatusUI()` (called per-frame from `GroundScene::parseMessages`):

1. **Lazy widget lookup with retry**:
   - On first call, attempts to find the mediator via `CuiMediatorFactory::get("WS_NetStatus", false)`
   - Throttled to one attempt per 60 frames (~1 second) to avoid performance overhead
   - Gives up after 600 frames (~10 seconds) if the panel is never opened
   - On success, caches `UIText*` pointers for food and drink widgets
   - Calls `SetPreLocalized(true)` on each widget once (disables the localization pipeline so raw text is displayed as-is, without going through `@string_id` lookup)

2. **Per-frame text update** (after widgets are found):
   - Reads food/drink from `PlayerObject::getFood()` / `PlayerObject::getDrink()`
   - Only calls `SetLocalText` when value changes (compares to last displayed value)
   - Formats as `"<value>/<max>"` (e.g., "81/100")
   - Uses `sprintf_s` into a stack buffer, converts to `soe::unicode` for the call

3. **Widget paths from the cloned mediator page**:
   - `comp.food.text` (relative to the mediator's page at +0x14)
   - `comp.drink.text`
   - These correspond to the `Name` attributes in the `.inc` file hierarchy: `comp` → `food` → `text`

### SetPreLocalized — Why It's Required

Without calling `SetPreLocalized(true)`, the UIText widget treats the string passed to `SetLocalText` as a **localization key** (e.g., `@ui:some_string_id`) and attempts to look it up in the string table. Since our text (`"81/100"`) isn't a valid localization key, it would display as an empty string or garbage. `SetPreLocalized(true)` tells the widget to display the raw text directly.

The original SwgCuiNetStatus constructor calls `SetPreLocalized(true)` on all its text widgets (textPing, textPacketLoss, textBandwidth, textFps) for the same reason — those also display dynamic formatted values, not localized strings. Our food/drink widgets need the same treatment.

Address: `UIText::SetPreLocalized(bool)` at `0x01112660`.

---

## Network Protocol

Food/drink values are part of the **PLAY9** baseline and delta messages:

- **Baseline**: Full snapshot sent when a player enters the game or zones
- **Delta**: Incremental updates sent whenever a value changes (eating, drinking, decay tick)

The client-side `AutoDeltaVariable<int>` is updated by the baseline/delta unpacking code. The values we read from memory are already decoded and current.

---

## Code Files

| File | Role |
|------|------|
| `PlayerObject.h` | `getFood()`, `getDrink()` etc. using confirmed field offsets (0x0570, 0x0598) |
| `FoodDrinkMonitor.h/.cpp` | Memory scanner tools, net status UI updater |
| `EmuCommandParser.cpp` | Chat commands (`/emu food`, `/emu drink`, `/emu memscan`) |
| `GroundScene.cpp` | Calls `FoodDrinkMonitor::updateNetStatusUI()` each frame |
| `dllmain.cpp` | Calls `FoodDrinkMonitor::initialize()` / `shutdown()` |
| `UIText.h` | `SetLocalText()` at `0x0110F580`, `AppendLocalText()` at `0x0110FB40` |
| `SWGRootFiles\ui\ui_pda_net_status.inc` | Modified UI layout with food/drink rows |

---

## Outstanding Work

- [ ] Confirm maxFood offset (predicted `0x0584`, value at `0x0590`) via `/emu memscan dump 0x570 0x50`
- [ ] Confirm maxDrink offset (predicted `0x05AC`, value at `0x05B8`)
- [ ] Find meds/maxMeds offsets
- [x] Test net status UI in-game — **working**, food/drink values display and update live
- [ ] Character sheet visual bars (hook SwgCuiCharacterSheet, widget offsets: foodBar=+0xAC, drinkBar=+0xB4)
- [ ] PLAY9 delta hook for push-based updates instead of polling
